using System.Net;
using System.Net.Sockets;
using JsonCall.Core;
using JsonCall.Core.Model;

namespace JsonCall.Transport;

public static class TcpMessageTransport
{
    /// <summary>Connects to a TCP server. A refused or failed connection throws <see cref="ConnectionLostException"/>.</summary>
    public static async Task<IMessageTransport> ConnectAsync(string host, int port,
        MessageFraming framing = MessageFraming.ContentLength, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new ConnectionLostException($"Could not connect to {host}:{port}: {ex.Message}", ex);
        }

        return FromClient(client, framing);
    }

    public static Task<IMessageTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
        ConnectAsync(host, port, MessageFraming.ContentLength, cancellationToken);

    public static IMessageTransport FromClient(TcpClient client, MessageFraming framing = MessageFraming.ContentLength)
    {
        client.NoDelay = true;
        return new TcpTransport(client, framing);
    }

    private sealed class TcpTransport : IMessageTransport
    {
        private readonly TcpClient _client;
        private readonly StreamMessageTransport _inner;

        public TcpTransport(TcpClient client, MessageFraming framing)
        {
            _client = client;
            var stream = client.GetStream();
            _inner = new StreamMessageTransport(stream, stream, framing: framing);
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
    private readonly MessageFraming _framing;

    public TcpMessageListener(IPAddress address, int port, MessageFraming framing = MessageFraming.ContentLength)
    {
        _framing = framing;
        _listener = new TcpListener(address, port);
        _listener.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<IMessageTransport> AcceptAsync(CancellationToken cancellationToken = default) =>
        TcpMessageTransport.FromClient(await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false), _framing);

    public void Dispose() => _listener.Stop();
}
