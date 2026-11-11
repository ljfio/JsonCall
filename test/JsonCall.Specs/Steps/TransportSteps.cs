using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Server;
using JsonCall.Specs.Support;
using JsonCall.Transport;
using Reqnroll;

namespace JsonCall.Specs.Steps;

[Binding]
public sealed class TransportSteps
{
    private readonly World _world;

    public TransportSteps(World world) => _world = world;

    private JsonRpcClient Client => _world.Connection!.Client;

    [Given(@"^an echo server exposing the method ""echo"" that returns its first parameter$")]
    public void GivenEchoServer()
    {
        // The shared test service exposes "echo"; the server is created per transport.
    }

    [Given(@"^a client connected over (\S+)$")]
    public async Task GivenClientConnected(string transport) =>
        _world.Connection = await Connections.ConnectAsync(transport, _world.State);

    [Given(@"^a client connected over (\S+) using the (\S+) serializer$")]
    public async Task GivenClientConnectedWith(string transport, string serializer)
    {
        _world.Values["serializer"] = serializer;
        _world.Connection = await Connections.ConnectAsync(transport, _world.State, serializer);
    }

    [When(@"^the client calls ""echo"" with (\[.*\])$")]
    public async Task WhenCallsEcho(string @params)
    {
        _world.Values["sent"] = JsonDocument.Parse(@params).RootElement.EnumerateArray().First().Clone();
        _world.Result = await Client.InvokeAsync("echo", JsonRpcParams.FromJson(@params));
    }

    [Then(@"^the result is ""(.*)""$")]
    public void ThenResultIs(string expected) =>
        Assert.Equal(expected, ((JsonElement)_world.Result!).GetString());

    [Then("^the result equals the sent value$")]
    public void ThenResultEqualsSent() =>
        Assert.Equal(((JsonElement)_world.Values["sent"]!).GetRawText().Replace(" ", ""),
            ((JsonElement)_world.Result!).GetRawText());

    [When(@"^the client calls ""echo"" with a 20 MB string$")]
    public async Task WhenCallsEchoLarge()
    {
        var text = new string('x', 20 * 1024 * 1024);
        _world.Values["sentString"] = text;
        _world.Result = await Client.InvokeAsync("echo", JsonRpcParams.ByPosition(text), TimeSpan.FromSeconds(60));
    }

    [Then("^the result equals the sent string$")]
    public void ThenResultEqualsString() =>
        Assert.Equal((string)_world.Values["sentString"]!, ((JsonElement)_world.Result!).GetString());

    // ---------------------------------------------------------------- tcp framing

    [Given("^the network delivers each message one byte at a time$")]
    public async Task GivenDribble()
    {
        await _world.Connection!.DisposeAsync();

        // reconnect through the proxy to a fresh server
        var listener = new TcpMessageListener(IPAddress.Loopback, 0);
        _world.OnCleanup(() =>
        {
            listener.Dispose();
            return ValueTask.CompletedTask;
        });

        var server = Connections.CreateServer(_world.State);
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                    server.Attach(await listener.AcceptAsync());
            }
            catch
            {
            }
        });

        var dribble = new DribbleProxy(listener.Port);
        _world.OnCleanup(dribble.DisposeAsync);

        var transport = await TcpMessageTransport.ConnectAsync("127.0.0.1", dribble.Port);
        var recording = new RecordingTransport(transport);
        _world.Connection = new Connection
        {
            Client = new JsonRpcClient(recording),
            Recording = recording,
            TransportName = "tcp"
        };
    }

    [When("^the server writes 3 framed responses in a single socket write$")]
    public async Task WhenServerWritesThree()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _world.OnCleanup(() =>
        {
            listener.Stop();
            return ValueTask.CompletedTask;
        });

        var acceptTask = listener.AcceptTcpClientAsync();
        var transport = await TcpMessageTransport.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        _world.OnCleanup(transport.DisposeAsync);
        var serverSocket = await acceptTask;
        _world.OnCleanup(() =>
        {
            serverSocket.Dispose();
            return ValueTask.CompletedTask;
        });

        var buffer = new MemoryStream();

        foreach (var n in new[] { 1, 2, 3 })
        {
            var body = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"result\":{n},\"id\":{n}}}");
            buffer.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            buffer.Write(body);
        }

        await serverSocket.GetStream().WriteAsync(buffer.ToArray());

        var received = new List<string>();

        for (var i = 0; i < 3; i++)
            received.Add(Encoding.UTF8.GetString((await transport.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))!));

        _world.Values["received"] = received;
    }

    [Then("^the client receives 3 distinct messages in order$")]
    public void ThenThreeMessages()
    {
        var received = (List<string>)_world.Values["received"]!;
        Assert.Equal(3, received.Distinct().Count());

        for (var i = 0; i < 3; i++)
            Assert.Contains($"\"id\":{i + 1}", received[i]);
    }

    // ---------------------------------------------------------------- framing errors

    [Given(@"^a stdio peer sending a frame with ""Content-Length: abc""$")]
    public void GivenBadFrame()
    {
        var bytes = Encoding.ASCII.GetBytes(
            "Content-Length: abc\r\n\r\n{}Content-Length: 2\r\n\r\n{}");

        _world.Values["transport"] = new StreamMessageTransport(new MemoryStream(bytes), Stream.Null);
    }

    [Then("^the connection reports a framing error$")]
    public async Task ThenFramingError()
    {
        var transport = (StreamMessageTransport)_world.Values["transport"]!;
        _world.Error = await Record.ExceptionAsync(async () => await transport.ReceiveAsync());
        Assert.IsType<TransportFramingException>(_world.Error);
    }

    [Then("^no further messages are processed$")]
    public async Task ThenNoFurther()
    {
        var transport = (StreamMessageTransport)_world.Values["transport"]!;
        var second = await Record.ExceptionAsync(async () =>
            Assert.Null(await transport.ReceiveAsync()));

        Assert.IsType<TransportFramingException>(second);
    }

    // ---------------------------------------------------------------- stdio child process

    [Given("^a client connected over stdio to a child process that logs to standard error$")]
    public void GivenChildProcess()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Support", "echo_server.py");
        var diagnostics = new List<string>();

        var transport = StdioMessageTransport.Launch(new ProcessStartInfo("python3", $"\"{script}\""));
        transport.DiagnosticsReceived += line =>
        {
            lock (diagnostics)
                diagnostics.Add(line);
        };

        _world.Values["diagnostics"] = diagnostics;

        var recording = new RecordingTransport(transport);
        _world.Connection = new Connection
        {
            Client = new JsonRpcClient(recording),
            Recording = recording,
            TransportName = "stdio-process"
        };
    }

    [Then("^the log output is available on the transport's diagnostics stream$")]
    public async Task ThenDiagnostics()
    {
        var diagnostics = (List<string>)_world.Values["diagnostics"]!;
        await Wait.UntilAsync(() =>
        {
            lock (diagnostics)
                return diagnostics.Any(l => l.Contains("child handled echo"));
        });
    }

    // ---------------------------------------------------------------- frame types

    [Then("^the transport sends (text|binary) frames$")]
    public async Task ThenFrameType(string kind)
    {
        var socket = Assert.IsType<WebSocketMessageTransport>(_world.Connection!.InnerTransport);
        Assert.Equal(kind == "text" ? System.Net.WebSockets.WebSocketMessageType.Text : System.Net.WebSockets.WebSocketMessageType.Binary,
            socket.FrameType);

        // and the exchange really works in that format
        var result = await Client.InvokeAsync("echo", JsonRpcParams.ByPosition("frame"));
        Assert.Equal("frame", result.GetString());
    }

    // ---------------------------------------------------------------- custom transport

    [Given("^a user-defined transport implementing IMessageTransport$")]
    public void GivenCustomTransport()
    {
        var (a, b) = ChannelTransport.Pair();
        var server = Connections.CreateServer(_world.State);
        _world.OnCleanup(server.Attach(b).DisposeAsync);
        _world.Values["custom"] = a;
    }

    [When(@"^the client is constructed with it and calls ""echo"" with \[""custom""\]$")]
    public async Task WhenCustomClient()
    {
        var client = new JsonRpcClient((IMessageTransport)_world.Values["custom"]!);
        _world.OnCleanup(client.DisposeAsync);
        _world.Result = await client.InvokeAsync("echo", JsonRpcParams.ByPosition("custom"));
    }

    // ---------------------------------------------------------------- connection loss

    [Given(@"^a call to ""slow"" is in flight$")]
    public async Task GivenSlowInFlight()
    {
        _world.Values["inflight"] = Client.InvokeAsync("slow", null, Timeout.InfiniteTimeSpan).AsTask();
        await Task.Delay(300);
    }

    [When("^the peer closes the connection$")]
    public async Task WhenPeerCloses() => await _world.Connection!.KillServer!();

    [Then("^the in-flight call fails with a ConnectionLostException$")]
    public async Task ThenInFlightFails()
    {
        var task = (Task)_world.Values["inflight"]!;
        var error = await Record.ExceptionAsync(async () => await task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<ConnectionLostException>(error);
    }

    [Then("^no call hangs$")]
    public async Task ThenNoHang()
    {
        var error = await Record.ExceptionAsync(async () =>
            await Client.InvokeAsync("echo", JsonRpcParams.ByPosition("x"), TimeSpan.FromSeconds(5)));

        Assert.IsType<ConnectionLostException>(error);
    }
}
