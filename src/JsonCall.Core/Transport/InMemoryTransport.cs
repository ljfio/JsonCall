using System.Threading.Channels;
using JsonCall.Core.Model;

namespace JsonCall.Core;

/// <summary>A connected pair of in-process transports.</summary>
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;

    private InMemoryTransport(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (InMemoryTransport Client, InMemoryTransport Server) CreatePair(int capacity = 0)
    {
        Channel<byte[]> Create() => capacity > 0
            ? Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true })
            : Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var a = Create();
        var b = Create();

        return (new InMemoryTransport(a, b), new InMemoryTransport(b, a));
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        try
        {
            await _outgoing.Writer.WriteAsync(message.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new ConnectionLostException(inner: ex);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
