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

    private static MessageFraming Framing(string name) => name switch
    {
        "newline" => MessageFraming.Newline,
        "content-length" => MessageFraming.ContentLength,
        _ => throw new ArgumentException($"Unknown framing '{name}'")
    };

    private MessageFraming CurrentFraming => _world.Values.TryGetValue("framing", out var value) ? (MessageFraming)value! : MessageFraming.ContentLength;

    [Given(@"^a client connected over (\S+) using (content-length|newline) framing$")]
    public async Task GivenClientConnectedFramed(string transport, string framing)
    {
        _world.Values["framing"] = Framing(framing);
        _world.Connection = await Connections.ConnectAsync(transport, _world.State, framing: Framing(framing));
    }

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
        var listener = new TcpMessageListener(IPAddress.Loopback, 0, CurrentFraming);
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

        var transport = await TcpMessageTransport.ConnectAsync("127.0.0.1", dribble.Port, CurrentFraming);
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
        var transport = await TcpMessageTransport.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, CurrentFraming);
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
            if (CurrentFraming == MessageFraming.Newline)
            {
                buffer.Write(body);
                buffer.WriteByte((byte)'\n');
            }
            else
            {
                buffer.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
                buffer.Write(body);
            }
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

        Assert.NotNull(second);
        Assert.IsType(_world.Error!.GetType(), second);
    }

    // ---------------------------------------------------------------- stdio child process

    [Given("^a client connected over stdio to a child process that logs to standard error$")]
    public void GivenChildProcess() => ConnectChildProcess(MessageFraming.ContentLength);

    [Given("^a client connected over stdio using (content-length|newline) framing to a child process that logs to standard error$")]
    public void GivenChildProcessFramed(string framing) => ConnectChildProcess(Framing(framing));

    private void ConnectChildProcess(MessageFraming framing)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Support", "echo_server.py");
        var diagnostics = new List<string>();
        var arguments = framing == MessageFraming.Newline ? $"\"{script}\" --newline" : $"\"{script}\"";

        var transport = StdioMessageTransport.Launch(new ProcessStartInfo("python3", arguments), framing);
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

    // ---------------------------------------------------------------- newline framing

    private static StreamMessageTransport Newline(byte[] wire, int maxMessageSize = 64 * 1024 * 1024) =>
        new(new MemoryStream(wire), Stream.Null, maxMessageSize, framing: MessageFraming.Newline);

    private StreamMessageTransport Transport => (StreamMessageTransport)_world.Values["transport"]!;

    [Given(@"^a newline framed peer sending (LF line endings|CRLF line endings|blank lines between messages)$")]
    public void GivenNewlinePeer(string wire)
    {
        var text = wire switch
        {
            "LF line endings" => "{\"a\":1}\n{\"b\":2}\n",
            "CRLF line endings" => "{\"a\":1}\r\n{\"b\":2}\r\n",
            _ => "\n{\"a\":1}\n\r\n\n{\"b\":2}\n\n"
        };

        _world.Values["transport"] = Newline(Encoding.UTF8.GetBytes(text));
    }

    [Then(@"^the transport receives the messages (\[.*\])$")]
    public async Task ThenReceivesMessages(string expected)
    {
        var messages = JsonSerializer.Deserialize<string[]>(expected)!;

        foreach (var message in messages)
            Assert.Equal(message, Encoding.UTF8.GetString((await Transport.ReceiveAsync())!));
    }

    [Given(@"^a newline framed peer sending a ""Content-Length: 2"" header followed by ""\{\}""$")]
    public void GivenContentLengthToNewline() =>
        _world.Values["transport"] = Newline(Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}"));

    [Given(@"^a newline framed transport with a maximum message size of (\d+) bytes$")]
    public void GivenNewlineMax(int max) => _world.Values["max"] = max;

    [Given(@"^a peer sending a line of (\d+) bytes$")]
    public void GivenLineOf(int length) =>
        _world.Values["transport"] = Newline(Encoding.ASCII.GetBytes(new string('a', length) + "\n"), (int)_world.Values["max"]!);

    [Given(@"^a peer sending a line of (\d+) bytes terminated by CRLF$")]
    public void GivenLineOfCrlf(int length) =>
        _world.Values["transport"] = Newline(Encoding.ASCII.GetBytes(new string('a', length) + "\r\n"), (int)_world.Values["max"]!);

    [Then("^the transport reports that the message is too large$")]
    public async Task ThenTooLarge()
    {
        _world.Error = await Record.ExceptionAsync(async () => await Transport.ReceiveAsync());
        Assert.IsType<MessageTooLargeException>(_world.Error);
    }

    [Then(@"^the transport receives a message of (\d+) bytes$")]
    public async Task ThenMessageOf(int length) =>
        Assert.Equal(length, (await Transport.ReceiveAsync())!.Length);

    [Given("^a newline framed peer sending a partial message and then closing$")]
    public void GivenPartial() => _world.Values["transport"] = Newline(Encoding.ASCII.GetBytes("{\"a\":"));

    [Then("^the transport reports a lost connection$")]
    public async Task ThenLost() =>
        Assert.IsType<ConnectionLostException>(await Record.ExceptionAsync(async () => await Transport.ReceiveAsync()));

    [Given(@"^a newline framed peer sending 2 messages and then closing$")]
    public void GivenTwoAndClose() => _world.Values["transport"] = Newline(Encoding.ASCII.GetBytes("{\"a\":1}\n{\"b\":2}\n"));

    [Then("^the transport receives 2 messages and then the end of the stream$")]
    public async Task ThenTwoThenEnd()
    {
        Assert.NotNull(await Transport.ReceiveAsync());
        Assert.NotNull(await Transport.ReceiveAsync());
        Assert.Null(await Transport.ReceiveAsync());
    }

    [Given("^a newline framed transport$")]
    public void GivenNewlineTransport() =>
        _world.Values["transport"] = new StreamMessageTransport(Stream.Null, new MemoryStream(), framing: MessageFraming.Newline);

    [When("^a message containing a raw newline is sent$")]
    public async Task WhenRawNewlineSent() =>
        _world.Error = await Record.ExceptionAsync(async () => await Transport.SendAsync(Encoding.ASCII.GetBytes("{\n}")));

    [Then("^the send fails with a framing error$")]
    public void ThenSendFails() => Assert.IsType<TransportFramingException>(_world.Error);

    // ---------------------------------------------------------------- interleaving

    [When(@"^the client issues 100 concurrent ""echo"" calls while the server sends 100 notifications$")]
    public async Task WhenInterleaved()
    {
        var received = new List<int>();
        Client.OnNotification("tick", element =>
        {
            lock (received)
                received.Add(element!.Value.EnumerateArray().First().GetInt32());
        });

        _world.Values["ticks"] = received;

        var calls = Enumerable.Range(0, 100)
            .Select(i => (Sent: i, Task: Client.InvokeAsync("echo", JsonRpcParams.ByPosition(i)).AsTask()))
            .ToList();

        var server = _world.Connection!.ServerPeer!;

        for (var i = 0; i < 100; i++)
            await server.NotifyAsync("tick", JsonRpcParams.ByPosition(i));

        await Task.WhenAll(calls.Select(c => c.Task)).WaitAsync(TimeSpan.FromSeconds(30));
        _world.Values["calls"] = calls;
    }

    [Then("^every call returns its own argument$")]
    public void ThenOwnArgument() =>
        Assert.All((List<(int Sent, Task<JsonElement> Task)>)_world.Values["calls"]!, c => Assert.Equal(c.Sent, c.Task.Result.GetInt32()));

    [Then("^the client receives the 100 notifications in order$")]
    public async Task ThenTicksInOrder()
    {
        var received = (List<int>)_world.Values["ticks"]!;
        await Wait.UntilAsync(() => { lock (received) return received.Count >= 100; });

        lock (received)
            Assert.Equal(Enumerable.Range(0, 100), received);
    }

    // ---------------------------------------------------------------- connecting and listening

    private static string NewSocketPath() => Path.Combine(Path.GetTempPath(), "jc-" + Guid.NewGuid().ToString("N")[..10] + ".sock");

    [Given("^no process is listening on a Unix socket path$")]
    public void GivenNoUnixListener() => _world.Values["socketPath"] = NewSocketPath();

    [Given("^a Unix socket file left behind by a listener that did not clean up$")]
    public void GivenStaleSocket()
    {
        var path = NewSocketPath();

        // .NET removes the file when it closes a socket it bound, so a process that exits without unlinking is imitated
        // by a short-lived python3 (already needed for the stdio interop test) that binds and exits.
        using (var process = Process.Start(new ProcessStartInfo("python3", ["-c", "import socket,sys; s=socket.socket(socket.AF_UNIX); s.bind(sys.argv[1])", path])
               {
                   UseShellExecute = false
               })!)
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        Assert.True(File.Exists(path));
        _world.OnCleanup(() =>
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        });

        _world.Values["socketPath"] = path;
    }

    [When("^a client connects to it$")]
    public async Task WhenConnectsUnix() =>
        _world.Error = await Record.ExceptionAsync(async () =>
            await UnixSocketMessageTransport.ConnectAsync((string)_world.Values["socketPath"]!));

    [Given("^no process is listening on a TCP port$")]
    public void GivenNoTcpListener()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _world.Values["tcpPort"] = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    [When("^a client connects to it over tcp$")]
    public async Task WhenConnectsTcp() =>
        _world.Error = await Record.ExceptionAsync(async () =>
            await TcpMessageTransport.ConnectAsync("127.0.0.1", (int)_world.Values["tcpPort"]!));

    [Then("^the connect fails with a ConnectionLostException$")]
    public void ThenConnectFails() => Assert.IsType<ConnectionLostException>(_world.Error);

    [Given("^a Unix socket listener$")]
    public void GivenUnixListener()
    {
        var listener = new UnixSocketMessageListener(NewSocketPath());
        _world.Values["listener"] = listener;
        _world.OnCleanup(() =>
        {
            listener.Dispose();
            return ValueTask.CompletedTask;
        });
    }

    private UnixSocketMessageListener Listener => (UnixSocketMessageListener)_world.Values["listener"]!;

    [Then("^the socket file exists$")]
    public void ThenSocketFileExists() => Assert.True(File.Exists(Listener.Path));

    [When("^the listener is disposed$")]
    public void WhenListenerDisposed() => Listener.Dispose();

    [Then("^the socket file no longer exists$")]
    public void ThenSocketFileGone() => Assert.False(File.Exists(Listener.Path));

    [When("^a new listener is created at the same path$")]
    public void WhenNewListenerAtPath()
    {
        var listener = new UnixSocketMessageListener((string)_world.Values["socketPath"]!);
        _world.Values["listener"] = listener;
        _world.OnCleanup(() =>
        {
            listener.Dispose();
            return ValueTask.CompletedTask;
        });
    }

    [Then("^it accepts a client connection$")]
    public async Task ThenAccepts()
    {
        var accept = Listener.AcceptAsync();
        await using var client = await UnixSocketMessageTransport.ConnectAsync(Listener.Path);
        await using var server = await accept.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Then("^a second listener at the same path is refused$")]
    public void ThenSecondRefused() =>
        Assert.Throws<IOException>(() => new UnixSocketMessageListener(Listener.Path));
}
