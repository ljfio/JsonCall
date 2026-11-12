using System.Net.Sockets;
using JsonCall.Core;
using JsonCall.Core.Model;

namespace JsonCall.Transport;

/// <summary>Messages over a Unix domain socket (a path on disk), with either framing.</summary>
public static class UnixSocketMessageTransport
{
    /// <summary>
    /// Connects to a listening socket. When nothing is listening, or the path is a stale socket file left by a process
    /// that exited, the failure is a <see cref="ConnectionLostException"/>, as for TCP.
    /// </summary>
    public static async Task<IMessageTransport> ConnectAsync(string path, MessageFraming framing = MessageFraming.ContentLength,
        CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new ConnectionLostException($"Could not connect to the Unix socket '{path}': {ex.Message}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return FromSocket(socket, framing);
    }

    /// <summary>Wraps a connected Unix domain socket. The transport owns the socket.</summary>
    public static IMessageTransport FromSocket(Socket socket, MessageFraming framing = MessageFraming.ContentLength) =>
        new UnixSocketTransport(socket, framing);

    private sealed class UnixSocketTransport : IMessageTransport
    {
        private readonly Socket _socket;
        private readonly StreamMessageTransport _inner;

        public UnixSocketTransport(Socket socket, MessageFraming framing)
        {
            _socket = socket;
            var stream = new NetworkStream(socket, ownsSocket: false);
            _inner = new StreamMessageTransport(stream, stream, framing: framing);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            _inner.SendAsync(message, cancellationToken);

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            _inner.ReceiveAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _socket.Dispose();
        }
    }
}

/// <summary>
/// Accepts connections on a Unix domain socket as message transports. The socket file is created on construction and
/// deleted on dispose. If a file already exists at the path (left by a crashed process) it is replaced, but only when
/// nothing is listening on it; a live socket makes the constructor throw <see cref="IOException"/>.
/// </summary>
public sealed class UnixSocketMessageListener : IDisposable
{
    private readonly Socket _listener;
    private readonly MessageFraming _framing;
    private int _disposed;

    public UnixSocketMessageListener(string path, MessageFraming framing = MessageFraming.ContentLength, int backlog = 128)
    {
        Path = System.IO.Path.GetFullPath(path);
        _framing = framing;

        RemoveStaleSocket(Path);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            _listener.Bind(new UnixDomainSocketEndPoint(Path));
            _listener.Listen(backlog);
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
    }

    /// <summary>The full path of the socket file.</summary>
    public string Path { get; }

    public async Task<IMessageTransport> AcceptAsync(CancellationToken cancellationToken = default) =>
        UnixSocketMessageTransport.FromSocket(await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false), _framing);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _listener.Dispose();

        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static void RemoveStaleSocket(string path)
    {
        if (!File.Exists(path))
            return;

        // A file at the path is only stale if connecting fails. Never delete a socket another process is serving.
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException)
        {
            File.Delete(path);
            return;
        }

        throw new IOException($"The Unix socket '{path}' is already in use by another listener.");
    }
}
