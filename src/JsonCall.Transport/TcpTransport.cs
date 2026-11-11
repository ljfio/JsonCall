using System.Net;
using System.Net.Sockets;
using JsonCall.Core;

namespace JsonCall.Transport;

public static class TcpMessageTransport
{
    public static async Task<IMessageTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return FromClient(client);
    }

    public static IMessageTransport FromClient(TcpClient client)
    {
        client.NoDelay = true;
        return new TcpTransport(client);
    }

    private sealed class TcpTransport : IMessageTransport
    {
        private readonly TcpClient _client;
        private readonly StreamMessageTransport _inner;

        public TcpTransport(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            _inner = new StreamMessageTransport(stream, stream);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            _inner.SendAsync(message, cancellationToken);

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            _inner.ReceiveAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _client.Dispose();
        }
    }
}

/// <summary>Accepts TCP connections as message transports.</summary>
public sealed class TcpMessageListener : IDisposable
{
    private readonly TcpListener _listener;

    public TcpMessageListener(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
        _listener.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<IMessageTransport> AcceptAsync(CancellationToken cancellationToken = default) =>
        TcpMessageTransport.FromClient(await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false));

    public void Dispose() => _listener.Stop();
}
