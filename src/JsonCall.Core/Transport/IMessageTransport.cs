namespace JsonCall.Core;

/// <summary>
/// Moves whole messages between peers. Implementations own framing; the protocol layer only sees messages.
/// </summary>
public interface IMessageTransport : IAsyncDisposable
{
    /// <summary>Sends one message. Calls are serialized by the caller.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    /// <summary>Receives the next message, or null when the peer closed the connection cleanly.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);
}
