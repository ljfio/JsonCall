using System.Globalization;
using System.Text;
using JsonCall.Core;
using JsonCall.Core.Model;

namespace JsonCall.Transport;

/// <summary>
/// Messages over a pair of byte streams, framed either with Content-Length headers (as used by LSP, the default) or
/// one message per line (<see cref="MessageFraming.Newline"/>).
/// Used for TCP (a NetworkStream), Unix domain sockets and stdio (process or console streams).
/// </summary>
public sealed class StreamMessageTransport : IMessageTransport
{
    private const int MaxHeaderBytes = 8 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maxMessageSize;
    private readonly bool _leaveOpen;
    private readonly MessageFraming _framing;
    private byte[] _line = Array.Empty<byte>();
    private int _lineLength;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _position;
    private int _length;
    private Exception? _faulted;

    public StreamMessageTransport(Stream input, Stream output, int maxMessageSize = 64 * 1024 * 1024, bool leaveOpen = false,
        MessageFraming framing = MessageFraming.ContentLength)
    {
        _framing = framing;
        _input = input;
        _output = output;
        _maxMessageSize = maxMessageSize;
        _leaveOpen = leaveOpen;
    }

    /// <summary>The framing this transport reads and writes.</summary>
    public MessageFraming Framing => _framing;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (_framing == MessageFraming.Newline)
        {
            await SendLineAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {message.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");

        try
        {
            await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ConnectionLostException(inner: ex);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_faulted is not null)
            throw _faulted;

        try
        {
            return _framing == MessageFraming.Newline
                ? await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                : await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TransportFramingException or MessageTooLargeException)
        {
            _faulted = ex;
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (ex is ConnectionLostException)
                throw;

            throw new ConnectionLostException(inner: ex);
        }
    }

    // ---- newline framing ----------------------------------------------

    private async ValueTask SendLineAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (message.Span.IndexOf((byte)'\n') >= 0)
        {
            throw new TransportFramingException(
                "The message contains a raw newline, so it cannot be sent with newline framing. Use compact JSON, or Content-Length framing for binary serializers.");
        }

        // Small messages go out in one write (message and terminator together) so each message is one packet.
        var rented = message.Length < 64 * 1024 ? System.Buffers.ArrayPool<byte>.Shared.Rent(message.Length + 1) : null;

        try
        {
            if (rented is not null)
            {
                message.CopyTo(rented);
                rented[message.Length] = (byte)'\n';
                await _output.WriteAsync(rented.AsMemory(0, message.Length + 1), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _output.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                await _output.WriteAsync(NewlineByte, cancellationToken).ConfigureAwait(false);
            }

            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ConnectionLostException(inner: ex);
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static readonly byte[] NewlineByte = { (byte)'\n' };

    private async ValueTask<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_position >= _length)
            {
                _length = await _input.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _position = 0;

                if (_length == 0)
                {
                    if (TrimmedLength() == 0)
                    {
                        _lineLength = 0;
                        return null;
                    }

                    throw new ConnectionLostException("The connection closed in the middle of a message.");
                }
            }

            var available = new ReadOnlySpan<byte>(_buffer, _position, _length - _position);
            var newline = available.IndexOf((byte)'\n');
            var take = newline < 0 ? available.Length : newline;

            // The limit applies to the message; one trailing '\r' is allowed on top of it.
            if ((long)_lineLength + take > (long)_maxMessageSize + 1)
                throw new MessageTooLargeException(_maxMessageSize);

            byte[] result;

            if (newline >= 0 && _lineLength == 0)
            {
                // The whole line is in the buffer: copy it once, straight out.
                var length = take > 0 && _buffer[_position + take - 1] == '\r' ? take - 1 : take;
                result = new byte[length];
                Buffer.BlockCopy(_buffer, _position, result, 0, length);
                _position += take + 1;
            }
            else
            {
                if (_line.Length < _lineLength + take)
                    Array.Resize(ref _line, Math.Max(_lineLength + take, Math.Min(_line.Length * 2, _maxMessageSize + 1)));

                Buffer.BlockCopy(_buffer, _position, _line, _lineLength, take);
                _lineLength += take;
                _position += take;

                if (newline < 0)
                    continue;

                _position++; // the newline
                var length = TrimmedLength();

                if (length > _maxMessageSize)
                    throw new MessageTooLargeException(_maxMessageSize);

                result = new byte[length];
                Buffer.BlockCopy(_line, 0, result, 0, length);
                _lineLength = 0;

                if (_line.Length > 1024 * 1024)
                    _line = Array.Empty<byte>(); // do not hold on to a buffer sized by one huge message
            }

            if (result.Length == 0)
                continue; // blank lines are ignored

            if (result.Length >= 15 && StartsWithContentLength(result))
            {
                throw new TransportFramingException(
                    "The peer sent a Content-Length header, but this transport uses newline framing.");
            }

            if (result.Length > _maxMessageSize)
                throw new MessageTooLargeException(_maxMessageSize);

            return result;
        }
    }

    private int TrimmedLength() => _lineLength > 0 && _line[_lineLength - 1] == '\r' ? _lineLength - 1 : _lineLength;

    private static bool StartsWithContentLength(byte[] line)
    {
        const string header = "Content-Length:";

        for (var i = 0; i < header.Length; i++)
        {
            if (char.ToLowerInvariant((char)line[i]) != char.ToLowerInvariant(header[i]))
                return false;
        }

        return true;
    }

    // ---- Content-Length framing ---------------------------------------

    private async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        var state = 0; // number of consecutive bytes matched in \r\n\r\n

        while (state < 4)
        {
            var b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (b < 0)
            {
                if (header.Count == 0)
                    return null;

                throw new ConnectionLostException("The connection closed in the middle of a message header.");
            }

            header.Add((byte)b);

            if (header.Count > MaxHeaderBytes)
                throw new TransportFramingException("The message header is too large.");

            state = (state, b) switch
            {
                (0, '\r') or (2, '\r') => state + 1,
                (1, '\n') or (3, '\n') => state + 1,
                (_, '\r') => 1,
                _ => 0
            };
        }

        long? contentLength = null;

        foreach (var line in Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');

            if (colon < 0)
                throw new TransportFramingException($"Invalid header line '{line}'.");

            if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    throw new TransportFramingException($"Invalid Content-Length value in '{line}'.");

                contentLength = parsed;
            }
        }

        if (contentLength is null)
            throw new TransportFramingException("The message header has no Content-Length.");

        if (contentLength > _maxMessageSize)
            throw new MessageTooLargeException(_maxMessageSize);

        var body = new byte[contentLength.Value];
        var read = 0;

        while (read < body.Length)
        {
            if (_position < _length)
            {
                var count = Math.Min(_length - _position, body.Length - read);
                Buffer.BlockCopy(_buffer, _position, body, read, count);
                _position += count;
                read += count;
                continue;
            }

            var n = await _input.ReadAsync(body.AsMemory(read), cancellationToken).ConfigureAwait(false);

            if (n == 0)
                throw new ConnectionLostException("The connection closed in the middle of a message.");

            read += n;
        }

        return body;
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_position >= _length)
        {
            _length = await _input.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _position = 0;

            if (_length == 0)
                return -1;
        }

        return _buffer[_position++];
    }

    public async ValueTask DisposeAsync()
    {
        if (_leaveOpen)
            return;

        try
        {
            await _output.DisposeAsync().ConfigureAwait(false);
            await _input.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }
}
