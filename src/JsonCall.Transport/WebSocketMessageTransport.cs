using System.Buffers;
using System.Net.WebSockets;
using JsonCall.Core;
using JsonCall.Core.Model;

namespace JsonCall.Transport;

/// <summary>One JSON-RPC message per WebSocket message. Text frames for text formats, binary otherwise.</summary>
public sealed class WebSocketMessageTransport : IMessageTransport
{
    private readonly WebSocket _socket;
    private readonly WebSocketMessageType _sendType;
    private readonly int _maxMessageSize;

    public WebSocketMessageTransport(WebSocket socket, IMessageSerializer? serializer = null,
        int maxMessageSize = 64 * 1024 * 1024)
    {
        _socket = socket;
        _sendType = (serializer ?? JsonMessageSerializer.Instance).IsBinary
            ? WebSocketMessageType.Binary
            : WebSocketMessageType.Text;
        _maxMessageSize = maxMessageSize;
    }

    public WebSocketMessageType FrameType => _sendType;

    public static async Task<WebSocketMessageTransport> ConnectAsync(Uri uri, IMessageSerializer? serializer = null,
        Action<ClientWebSocket>? configure = null, CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        configure?.Invoke(socket);
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        return new WebSocketMessageTransport(socket, serializer);
    }

    /// <summary>Drops the connection without a close handshake.</summary>
    public void Abort() => _socket.Abort();

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        try
        {
            await _socket.SendAsync(message, _sendType, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ConnectionLostException(inner: ex);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>();

        try
        {
            while (true)
            {
                var memory = buffer.GetMemory(8192);
                var result = await _socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_socket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (WebSocketException)
                        {
                        }
                    }

                    return null;
                }

                buffer.Advance(result.Count);

                if (buffer.WrittenCount > _maxMessageSize)
                {
                    _socket.Abort();
                    throw new MessageTooLargeException(_maxMessageSize);
                }

                if (result.EndOfMessage)
                    return buffer.WrittenSpan.ToArray();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ConnectionLostException(inner: ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // closing is best effort
        }

        _socket.Dispose();
    }
}
