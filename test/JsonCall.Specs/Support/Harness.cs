using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JsonCall.AspNetCore;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Serializer.Cbor;
using JsonCall.Serializer.MessagePack;
using JsonCall.Server;
using JsonCall.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JsonCall.Specs.Support;

public static class Serializers
{
    public static IMessageSerializer ByName(string name) => name.ToLowerInvariant() switch
    {
        "json" => JsonMessageSerializer.Instance,
        "cbor" => new CborMessageSerializer(),
        "messagepack" => new MessagePackMessageSerializer(),
        _ => throw new ArgumentException($"Unknown serializer '{name}'")
    };

    public static readonly string[] Names = { "json", "cbor", "messagepack" };
}

/// <summary>Records everything sent through the wrapped transport.</summary>
public sealed class RecordingTransport : IMessageTransport
{
    private readonly IMessageTransport _inner;
    private readonly IMessageSerializer _serializer;

    public RecordingTransport(IMessageTransport inner, IMessageSerializer? serializer = null)
    {
        _inner = inner;
        _serializer = serializer ?? JsonMessageSerializer.Instance;
    }

    public List<byte[]> Sent { get; } = new();

    public List<byte[]> Received { get; } = new();

    public IEnumerable<string> SentJson
    {
        get
        {
            lock (Sent)
                return Sent.Select(b => Encoding.UTF8.GetString(_serializer.ToJson(b))).ToList();
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        lock (Sent)
            Sent.Add(message.ToArray());

        return _inner.SendAsync(message, cancellationToken);
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var message = await _inner.ReceiveAsync(cancellationToken);

        if (message is not null)
            lock (Received)
                Received.Add(message);

        return message;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>A hand driven peer that exchanges raw JSON with a server, for protocol conformance tests.</summary>
public sealed class RawPeer : IAsyncDisposable
{
    private readonly IMessageTransport _transport;
    private readonly IMessageSerializer _serializer;
    private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
    private readonly Task _pump;

    public RawPeer(IMessageTransport transport, IMessageSerializer? serializer = null)
    {
        _transport = transport;
        _serializer = serializer ?? JsonMessageSerializer.Instance;
        _pump = Task.Run(PumpAsync);
    }

    public bool Closed { get; private set; }

    public Exception? Failure { get; private set; }

    private async Task PumpAsync()
    {
        try
        {
            while (await _transport.ReceiveAsync() is { } message)
                await _messages.Writer.WriteAsync(Encoding.UTF8.GetString(_serializer.ToJson(message)));
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
        finally
        {
            Closed = true;
            _messages.Writer.TryComplete();
        }
    }

    public ValueTask SendJsonAsync(string json) => _transport.SendAsync(_serializer.FromJson(Encoding.UTF8.GetBytes(json)));

    public ValueTask SendWireAsync(byte[] wire) => _transport.SendAsync(wire);

    /// <summary>The next received message as JSON text, or null if none arrives within the timeout.</summary>
    public async Task<string?> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        try
        {
            return await _messages.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async Task<bool> ReceivesNothingAsync(TimeSpan? window = null) =>
        await NextAsync(window ?? TimeSpan.FromMilliseconds(250)) is null;

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>A Kestrel host on a loopback port that serves JSON-RPC.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private TestHost(WebApplication app) => App = app;

    public WebApplication App { get; }

    public Uri BaseAddress => new(App.Urls.First());

    public static async Task<TestHost> StartAsync(
        Action<IServiceCollection> configureServices,
        Action<WebApplication> configureApp,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        configureBuilder?.Invoke(builder);
        configureServices(builder.Services);

        var app = builder.Build();
        configureApp(app);

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new TestHost(app);
    }

    /// <summary>A host with a header based test authentication scheme (X-Test-User, X-Test-Roles).</summary>
    public static Task<TestHost> StartAuthAsync(TestState state, Action<WebApplication>? beforeMap = null,
        Action<Microsoft.AspNetCore.Builder.IEndpointConventionBuilder>? configureEndpoint = null) =>
        StartAsync(
            services =>
            {
                services.AddSingleton(state);
                services.AddJsonRpc(o =>
                {
                    AddContracts(o, state);
                    o.AddContract<IAdmin, AdminService>();
                });
                services.AddAuthentication("Test").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
                services.AddAuthorization();
            },
            app =>
            {
                beforeMap?.Invoke(app);
                app.UseAuthentication();
                app.UseAuthorization();
                var endpoint = app.MapJsonRpc("/rpc");
                configureEndpoint?.Invoke(endpoint);
            });

    public static Task<TestHost> StartRpcAsync(TestState state, string path = "/rpc") =>
        StartAsync(
            services => services.AddJsonRpc(o => AddContracts(o, state),
                h => h.AddSerializer("cbor", new CborMessageSerializer()).AddSerializer("messagepack", new MessagePackMessageSerializer())),
            app => app.MapJsonRpc(path));

    public static void AddContracts(JsonRpcServerOptions options, TestState state)
    {
        var service = new TestService(state);
        options.AddContract<ITestService>(service);
        options.AddContract<IMath>(service);
        options.AddContract<IGreeter>(service);
        options.AddContract<IEvents>(service);
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}

/// <summary>A connected client plus whatever is needed to reach and kill the server side.</summary>
public sealed class Connection : IAsyncDisposable
{
    private readonly List<Func<ValueTask>> _cleanup = new();

    public required JsonRpcClient Client { get; init; }

    public required RecordingTransport Recording { get; init; }

    public required string TransportName { get; init; }

    public JsonRpcPeer? ServerPeer { get; set; }

    public Func<ValueTask>? KillServer { get; set; }

    public TestHost? Host { get; set; }

    public int ServerPort { get; set; }

    public IMessageTransport? InnerTransport { get; set; }

    public void OnDispose(Func<ValueTask> cleanup) => _cleanup.Add(cleanup);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
        }
        catch
        {
        }

        foreach (var cleanup in Enumerable.Reverse(_cleanup))
        {
            try
            {
                await cleanup();
            }
            catch
            {
            }
        }
    }
}

public static class Connections
{
    public static JsonRpcServer CreateServer(TestState state, Action<JsonRpcServerOptions>? configure = null)
    {
        var options = new JsonRpcServerOptions();
        TestHost.AddContracts(options, state);
        configure?.Invoke(options);
        return new JsonRpcServer(options);
    }

    public static async Task<Connection> ConnectAsync(string transport, TestState state, string serializerName = "json",
        IJsonRpcHandler? clientHandler = null, TimeSpan? timeout = null, int clientStreamBuffer = 64,
        int transportCapacity = 0)
    {
        var serializer = Serializers.ByName(serializerName);

        JsonRpcPeerOptions ClientOptions() => new()
        {
            Serializer = serializer,
            Handler = clientHandler,
            DefaultTimeout = timeout ?? TimeSpan.FromSeconds(30),
            StreamBufferSize = clientStreamBuffer
        };

        switch (transport)
        {
            case "in-memory":
            {
                var (clientSide, serverSide) = InMemoryTransport.CreatePair(transportCapacity);
                var server = CreateServer(state);
                var serverPeer = server.Attach(serverSide, serializer);
                var recording = new RecordingTransport(clientSide, serializer);
                var client = new JsonRpcClient(recording, ClientOptions());

                return new Connection
                {
                    Client = client,
                    Recording = recording,
                    TransportName = transport,
                    ServerPeer = serverPeer,
                    KillServer = serverPeer.DisposeAsync
                };
            }

            case "tcp":
            {
                var server = CreateServer(state);
                var listener = new TcpMessageListener(IPAddress.Loopback, 0);
                var accepted = new TaskCompletionSource<JsonRpcPeer>();
                var peers = new List<JsonRpcPeer>();
                var cts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var peer = server.Attach(await listener.AcceptAsync(cts.Token), serializer);
                            lock (peers)
                                peers.Add(peer);
                            accepted.TrySetResult(peer);
                        }
                    }
                    catch
                    {
                    }
                });

                var recording = new RecordingTransport(await TcpMessageTransport.ConnectAsync("127.0.0.1", listener.Port), serializer);
                var client = new JsonRpcClient(recording, ClientOptions());
                var serverPeer = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var connection = new Connection
                {
                    Client = client,
                    Recording = recording,
                    TransportName = transport,
                    ServerPeer = serverPeer,
                    ServerPort = listener.Port,
                    KillServer = serverPeer.DisposeAsync
                };

                connection.OnDispose(async () =>
                {
                    cts.Cancel();
                    listener.Dispose();

                    JsonRpcPeer[] snapshot;
                    lock (peers)
                        snapshot = peers.ToArray();

                    foreach (var peer in snapshot)
                        await peer.DisposeAsync();
                });

                return connection;
            }

            case "stdio":
            {
                var toServer = new Pipe();
                var toClient = new Pipe();
                var server = CreateServer(state);

                var serverTransport = new StreamMessageTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());
                var clientTransport = new StreamMessageTransport(toClient.Reader.AsStream(), toServer.Writer.AsStream());

                var serverPeer = server.Attach(serverTransport, serializer);
                var recording = new RecordingTransport(clientTransport, serializer);
                var client = new JsonRpcClient(recording, ClientOptions());

                return new Connection
                {
                    Client = client,
                    Recording = recording,
                    TransportName = transport,
                    ServerPeer = serverPeer,
                    KillServer = async () =>
                    {
                        await serverPeer.DisposeAsync();
                        await toClient.Writer.CompleteAsync();
                    }
                };
            }

            case "websocket":
            {
                var host = await TestHost.StartRpcAsync(state);
                var uri = new UriBuilder(host.BaseAddress) { Scheme = "ws", Path = "/rpc" }.Uri;

                var socketTransport = await WebSocketMessageTransport.ConnectAsync(uri, serializer, ws =>
                {
                    if (serializerName != "json")
                        ws.Options.AddSubProtocol("jsoncall." + serializerName);
                });

                var recording = new RecordingTransport(socketTransport, serializer);
                var client = new JsonRpcClient(recording, ClientOptions());

                var connection = new Connection
                {
                    Client = client,
                    Recording = recording,
                    TransportName = transport,
                    Host = host,
                    InnerTransport = socketTransport,
                    KillServer = async () => await host.App.StopAsync()
                };

                connection.OnDispose(host.DisposeAsync);
                return connection;
            }

            case "http" or "http-sse":
            {
                var host = await TestHost.StartRpcAsync(state);
                var http = new HttpClient { BaseAddress = host.BaseAddress };
                var httpTransport = new HttpMessageTransport(http, new Uri(host.BaseAddress, "/rpc"), serializer, ownsClient: true);
                var recording = new RecordingTransport(httpTransport, serializer);
                var client = new JsonRpcClient(recording, ClientOptions());

                var connection = new Connection
                {
                    Client = client,
                    Recording = recording,
                    TransportName = transport,
                    Host = host,
                    InnerTransport = httpTransport,
                    KillServer = async () => await host.App.StopAsync()
                };

                connection.OnDispose(host.DisposeAsync);
                return connection;
            }

            default:
                throw new ArgumentException($"Unknown transport '{transport}'");
        }
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(message ?? "The condition was not met in time.");

            await Task.Delay(10);
        }
    }
}

/// <summary>Lets a test supply the client's request handler after the client has been created.</summary>
public sealed class SwappableHandler : IJsonRpcHandler
{
    public IJsonRpcHandler? Inner { get; set; }

    public ValueTask<JsonRpcReply?> HandleAsync(JsonRpcRequest request, JsonRpcCallContext context) =>
        Inner is null
            ? ValueTask.FromResult<JsonRpcReply?>(request.IsNotification
                ? null
                : JsonRpcReply.FromError(JsonRpcErrorCodes.MethodNotFound, "Method not found"))
            : Inner.HandleAsync(request, context);
}


/// <summary>Forwards TCP traffic one byte at a time in both directions.</summary>
public sealed class DribbleProxy : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public DribbleProxy(int targetPort)
    {
        _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var outbound = new System.Net.Sockets.TcpClient { NoDelay = true };
                    await outbound.ConnectAsync(IPAddress.Loopback, targetPort, _cts.Token);
                    inbound.NoDelay = true;
                    _ = PumpAsync(inbound, outbound);
                    _ = PumpAsync(outbound, inbound);
                }
            }
            catch
            {
            }
        });
    }

    public int Port { get; }

    private async Task PumpAsync(System.Net.Sockets.TcpClient from, System.Net.Sockets.TcpClient to)
    {
        try
        {
            var source = from.GetStream();
            var target = to.GetStream();
            var buffer = new byte[4096];
            int read;

            while ((read = await source.ReadAsync(buffer, _cts.Token)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    await target.WriteAsync(buffer.AsMemory(i, 1), _cts.Token);
                    await target.FlushAsync(_cts.Token);
                }
            }
        }
        catch
        {
        }
        finally
        {
            from.Dispose();
            to.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A user written transport (not one of the library's) used to prove the seam is open.</summary>
public sealed class ChannelTransport : IMessageTransport
{
    private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
    private readonly System.Threading.Channels.ChannelWriter<byte[]> _writer;

    public ChannelTransport(System.Threading.Channels.ChannelReader<byte[]> reader, System.Threading.Channels.ChannelWriter<byte[]> writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public static (ChannelTransport A, ChannelTransport B) Pair()
    {
        var ab = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var ba = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        return (new ChannelTransport(ba.Reader, ab.Writer), new ChannelTransport(ab.Reader, ba.Writer));
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        _writer.WriteAsync(message.ToArray(), cancellationToken);

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _reader.ReadAsync(cancellationToken);
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}


public sealed class TestAuthHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
{
    public TestAuthHandler(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());

        var claims = new List<System.Security.Claims.Claim> { new(System.Security.Claims.ClaimTypes.Name, user.ToString()) };

        if (Request.Headers.TryGetValue("X-Test-Roles", out var roles))
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r)));

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Test");
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(identity), "Test");

        return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
    }
}

public sealed class AdminService : IAdmin
{
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _accessor;
    private readonly TestState _state;

    public AdminService(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor, TestState state)
    {
        _accessor = accessor;
        _state = state;
    }

    public Task<string> ResetAsync()
    {
        Interlocked.Increment(ref _state.Started);
        return Task.FromResult("reset");
    }

    public Task<string> WhoAmIAsync() =>
        Task.FromResult(_accessor.HttpContext?.User.Identity?.Name ?? "anonymous");
}
