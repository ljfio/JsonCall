using System.Globalization;
using System.Text;
using JsonCall.Core;
using JsonCall.Core.Model;

namespace JsonCall.Transport;

/// <summary>
/// Messages over a pair of byte streams using Content-Length header framing (as used by LSP).
/// Used for TCP (a NetworkStream) and stdio (process or console streams).
/// </summary>
public sealed class StreamMessageTransport : IMessageTransport
{
    private const int MaxHeaderBytes = 8 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maxMessageSize;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _position;
    private int _length;
    private Exception? _faulted;

    public StreamMessageTransport(Stream input, Stream output, int maxMessageSize = 64 * 1024 * 1024, bool leaveOpen = false)
    {
        _input = input;
        _output = output;
        _maxMessageSize = maxMessageSize;
        _leaveOpen = leaveOpen;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
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
            return await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
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
