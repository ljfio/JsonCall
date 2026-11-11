using System.Diagnostics;
using System.Text.Json;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Server;
using JsonCall.Specs.Support;
using Reqnroll;

namespace JsonCall.Specs.Steps;

[Binding]
public sealed class ClientSteps
{
    private readonly World _world;

    public ClientSteps(World world) => _world = world;

    private JsonRpcClient Client => _world.Connection!.Client;

    private SwappableHandler ClientHandler => (SwappableHandler)_world.Values["clientHandler"]!;

    private T Get<T>(string key) => (T)_world.Values[key]!;

    [Given(@"^a connected client and a server with the methods ""delay_echo"", ""fail"", ""notify_me"" and ""add""$")]
    public async Task GivenConnected()
    {
        var handler = new SwappableHandler();
        _world.Values["clientHandler"] = handler;
        _world.Connection = await Connections.ConnectAsync("in-memory", _world.State, clientHandler: handler);
    }

    private static JsonRpcParams Args(params object?[] values) => JsonRpcParams.ByPosition(values);

    // ---------------------------------------------------------------- correlation

    [Given(@"^""delay_echo"" with \[""first""\] takes 200 ms and ""delay_echo"" with \[""second""\] takes 10 ms$")]
    public void GivenDelays()
    {
        _world.State.Delays["first"] = TimeSpan.FromMilliseconds(200);
        _world.State.Delays["second"] = TimeSpan.FromMilliseconds(10);
    }

    [When("^the client issues both calls concurrently$")]
    public async Task WhenBothConcurrently()
    {
        var first = Client.InvokeAsync<string>("delay_echo", Args("first")).AsTask();
        var second = Client.InvokeAsync<string>("delay_echo", Args("second")).AsTask();
        await Task.WhenAll(first, second);
        _world.Values["first"] = await first;
        _world.Values["second"] = await second;
    }

    [Then(@"^the first call returns ""first""$")]
    public void ThenFirst() => Assert.Equal("first", _world.Values["first"]);

    [Then(@"^the second call returns ""second""$")]
    public void ThenSecond() => Assert.Equal("second", _world.Values["second"]);

    [When(@"^the client issues 1000 concurrent ""add"" calls with distinct arguments$")]
    public async Task WhenThousand()
    {
        var calls = Enumerable.Range(0, 1000)
            .Select(i => (A: i, B: i * 3, Task: Client.InvokeAsync<int>("add", Args(i, i * 3)).AsTask()))
            .ToList();

        await Task.WhenAll(calls.Select(c => c.Task));
        _world.Values["calls"] = calls;
    }

    [Then("^every call returns the sum of its own arguments$")]
    public void ThenEverySum() =>
        Assert.All(Get<List<(int A, int B, Task<int> Task)>>("calls"), c => Assert.Equal(c.A + c.B, c.Task.Result));

    [Then("^no call receives another call's response$")]
    public void ThenNoMixup() =>
        Assert.Equal(1000, Get<List<(int A, int B, Task<int> Task)>>("calls").Select(c => c.Task.Result).Distinct().Count());

    [When("^the client issues 100 concurrent calls$")]
    public async Task WhenHundred() =>
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Client.InvokeAsync<int>("add", Args(i, 1)).AsTask()));

    [Then("^all request ids on the wire are distinct$")]
    public void ThenIdsDistinct()
    {
        var ids = _world.Connection!.Recording.SentJson
            .Select(JsonAssert.Parse)
            .Where(m => m.TryGetProperty("id", out _))
            .Select(m => m.GetProperty("id").GetRawText())
            .ToList();

        Assert.Equal(100, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ---------------------------------------------------------------- timeouts and cancellation

    [Given(@"^the default call timeout is (\d+) (ms|seconds)$")]
    public void GivenDefaultTimeout(int amount, string unit) =>
        Client.DefaultTimeout = unit == "ms" ? TimeSpan.FromMilliseconds(amount) : TimeSpan.FromSeconds(amount);

    [When(@"^the client calls ""delay_echo"" which takes 1 second$")]
    public async Task WhenCallTakesOneSecond()
    {
        _world.State.DefaultDelay = TimeSpan.FromSeconds(1);
        _world.Error = await Record.ExceptionAsync(async () => await Client.InvokeAsync("delay_echo", Args("x")));
    }

    [Then("^the call fails with a TimeoutException$")]
    public void ThenTimeout() => Assert.IsType<TimeoutException>(_world.Error);

    [Then("^the connection remains usable for further calls$")]
    public async Task ThenUsable()
    {
        _world.State.DefaultDelay = TimeSpan.Zero;
        Client.DefaultTimeout = TimeSpan.FromSeconds(5);
        Assert.Equal(7, await Client.InvokeAsync<int>("add", Args(3, 4)));
    }

    [Given(@"^the client calls ""delay_echo"" which takes 300 ms and times out$")]
    public async Task GivenTimedOutCall()
    {
        _world.State.DefaultDelay = TimeSpan.FromMilliseconds(300);
        _world.State.ReleaseGate = new TaskCompletionSource();

        var error = await Record.ExceptionAsync(async () => await Client.InvokeAsync("delay_echo", Args("slow-one")));
        Assert.IsType<TimeoutException>(error);
    }

    [When("^the late response arrives$")]
    public async Task WhenLateResponse() => await Task.Delay(500);

    [Then("^it is ignored without error$")]
    public void ThenIgnored() => Assert.False(Client.Completion.IsCompleted, "The connection must stay up");

    [Then("^a subsequent call returns its own result$")]
    public async Task ThenSubsequent()
    {
        _world.State.DefaultDelay = TimeSpan.Zero;
        Client.DefaultTimeout = TimeSpan.FromSeconds(5);
        Assert.Equal("next", await Client.InvokeAsync<string>("delay_echo", Args("next")));
    }

    [Given(@"^a call to ""delay_echo"" which takes 5 seconds is in flight$")]
    public async Task GivenFiveSecondCall()
    {
        _world.State.DefaultDelay = TimeSpan.FromSeconds(5);
        var cts = new CancellationTokenSource();
        _world.Values["cts"] = cts;
        _world.Values["inflight"] = Client.InvokeAsync("delay_echo", Args("x"), Timeout.InfiniteTimeSpan, cts.Token).AsTask();
        await Task.Delay(200);
    }

    [When("^the caller cancels its token$")]
    public void WhenCancel()
    {
        _world.Values["cancelStart"] = Stopwatch.GetTimestamp();
        Get<CancellationTokenSource>("cts").Cancel();
    }

    [Then(@"^the call fails with an OperationCanceledException within 100 ms$")]
    public async Task ThenCancelled()
    {
        var error = await Record.ExceptionAsync(async () => await Get<Task>("inflight"));
        var elapsed = Stopwatch.GetElapsedTime(Get<long>("cancelStart"));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(100), $"Cancellation took {elapsed.TotalMilliseconds} ms");
    }

    [Then("^the server is notified of the cancellation$")]
    public async Task ThenServerNotified() =>
        await _world.State.DelayCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

    [When(@"^the client calls ""delay_echo"" with a 50 ms timeout and the call takes 1 second$")]
    public async Task WhenPerCallTimeout()
    {
        _world.State.DefaultDelay = TimeSpan.FromSeconds(1);

        _world.Error = await Record.ExceptionAsync(async () =>
            await Client.InvokeAsync("delay_echo", Args("x"), TimeSpan.FromMilliseconds(50)));
    }

    // ---------------------------------------------------------------- notifications

    [Given(@"^the client has a handler for the notification ""progress""$")]
    public void GivenProgressHandler()
    {
        var received = new List<JsonElement?>();
        _world.Values["notifications"] = received;

        Client.OnNotification("progress", p =>
        {
            lock (received)
                received.Add(p);
        });
    }

    [When(@"^the server sends the notification ""(.*)"" with (.*)$")]
    public async Task WhenServerNotifies(string method, string @params) =>
        await _world.Connection!.ServerPeer!.NotifyAsync(method, JsonRpcParams.FromJson(@params.StartsWith('[') ? @params : $"[{@params}]"));

    [When(@"^the server sends the notification ""unhandled""$")]
    public async Task WhenServerNotifiesUnhandled() =>
        await _world.Connection!.ServerPeer!.NotifyAsync("unhandled");

    [Then(@"^the handler is invoked once with \{""percent"":50\}$")]
    public async Task ThenHandlerInvoked()
    {
        var received = Get<List<JsonElement?>>("notifications");
        await Wait.UntilAsync(() => received.Count >= 1);

        // the notification carries one positional value: the object
        var element = Assert.Single(received)!.Value;
        Assert.Equal(50, element.EnumerateArray().First().GetProperty("percent").GetInt32());
    }

    [Given(@"^a call to ""delay_echo"" is in flight$")]
    public async Task GivenDelayInFlight()
    {
        GivenProgressHandler();
        _world.State.DefaultDelay = TimeSpan.FromSeconds(30);
        _world.State.ReleaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.Values["inflight"] = Client.InvokeAsync<string>("delay_echo", Args("own")).AsTask();
        await Task.Delay(200);
    }

    [When("^the server sends 10 notifications and then the response$")]
    public async Task WhenTenNotifications()
    {
        for (var i = 0; i < 10; i++)
            await _world.Connection!.ServerPeer!.NotifyAsync("progress", JsonRpcParams.ByPosition(i));

        _world.State.ReleaseGate.TrySetResult();
    }

    [Then("^the call completes with its own result$")]
    public async Task ThenOwnResult() =>
        Assert.Equal("own", await Get<Task<string?>>("inflight").WaitAsync(TimeSpan.FromSeconds(5)));

    [Then("^the handler received all 10 notifications$")]
    public async Task ThenTenNotifications()
    {
        var received = Get<List<JsonElement?>>("notifications");
        await Wait.UntilAsync(() => received.Count == 10);
    }

    [Then("^the client logs it at debug level and continues$")]
    public async Task ThenContinues()
    {
        await Task.Delay(100);
        Assert.Equal(3, await Client.InvokeAsync<int>("add", Args(1, 2)));
    }

    [Given(@"^the client exposes a method ""ping"" returning ""pong""$")]
    public void GivenClientExposes()
    {
        var options = new JsonRpcServerOptions();
        options.AddMethod("ping", () => "pong");
        ClientHandler.Inner = new JsonRpcServer(options);
    }

    [When(@"^the server calls ""ping"" on the client$")]
    public async Task WhenServerCalls() =>
        _world.Result = await _world.Connection!.ServerPeer!.InvokeAsync<string>("ping");

    [Then(@"^the server receives ""pong""$")]
    public void ThenPong() => Assert.Equal("pong", _world.Result);

    // ---------------------------------------------------------------- batches

    [When(@"^the client sends a batch of ""add""\[1,2\], ""add""\[3,4\] and ""fail""$")]
    public async Task WhenBatch() =>
        _world.Values["batch"] = await Client.BatchAsync(new[]
        {
            new BatchItem("add", Args(1, 2)),
            new BatchItem("add", Args(3, 4)),
            new BatchItem("fail")
        });

    [Then("^results 3 and 7 are returned in request order$")]
    public void ThenBatchResults()
    {
        var results = Get<IReadOnlyList<BatchResult>>("batch");
        Assert.Equal(3, results[0].GetResult<int>());
        Assert.Equal(7, results[1].GetResult<int>());
    }

    [Then(@"^the ""fail"" entry carries a JsonRpcException with the server's error$")]
    public void ThenBatchFail()
    {
        var failure = Get<IReadOnlyList<BatchResult>>("batch")[2];
        Assert.Equal(1001, failure.Error!.Code);
        Assert.Equal("Quota exceeded", failure.Error.Message);
        Assert.Throws<JsonRpcException>(() => failure.GetResult<int>());
    }

    [When("^the client sends a batch of one call and two notifications$")]
    public async Task WhenMixedBatch() =>
        _world.Values["batch"] = await Client.BatchAsync(new[]
        {
            new BatchItem("notify_me", Args("a"), isNotification: true),
            new BatchItem("add", Args(1, 2)),
            new BatchItem("notify_me", Args("b"), isNotification: true)
        });

    [Then("^one result is returned$")]
    public void ThenOneResult()
    {
        var results = Get<IReadOnlyList<BatchResult>>("batch");
        Assert.Single(results);
        Assert.Equal(3, results[0].GetResult<int>());
    }

    // ---------------------------------------------------------------- errors and disposal

    [When(@"^the client calls ""fail""$")]
    public async Task WhenFail() =>
        _world.Error = await Record.ExceptionAsync(async () => await Client.InvokeAsync("fail"));

    [Then("^a JsonRpcException is thrown with the server's code, message and data$")]
    public void ThenFailDetails()
    {
        var error = Assert.IsType<JsonRpcException>(_world.Error);
        Assert.Equal(1001, error.Code);
        Assert.Equal("Quota exceeded", error.Message);
        Assert.Equal(5, ((JsonElement)error.ErrorData!).GetProperty("limit").GetInt32());
    }

    [Given("^the client has been disposed$")]
    public async Task GivenDisposed() => await Client.DisposeAsync();

    [When(@"^the client calls ""add"" with \[1,2\]$")]
    public async Task WhenCallsAdd() =>
        _world.Error = await Record.ExceptionAsync(async () => await Client.InvokeAsync<int>("add", Args(1, 2)));

    [Then("^an ObjectDisposedException is thrown$")]
    public void ThenDisposed() => Assert.IsType<ObjectDisposedException>(_world.Error);

    // ---------------------------------------------------------------- proxy

    [Given("^the contract$")]
    public void GivenContract(string contract) => Assert.Contains("IMath", contract);

    private IMath Proxy
    {
        get
        {
            if (!_world.Values.TryGetValue("proxy", out var proxy))
                _world.Values["proxy"] = proxy = Client.CreateProxy<IMath>();

            return (IMath)proxy!;
        }
    }

    [When("^a proxy is created over the connected client$")]
    public void WhenProxyCreated() => _ = Proxy;

    [When(@"^the proxy calls AddAsync\(2, 3\)$")]
    public async Task WhenProxyAdds() => _world.Result = await Proxy.AddAsync(2, 3);

    [When(@"^the proxy calls AddAsync\(2, 3\) with a cancellation token$")]
    public async Task WhenProxyAddsWithToken() => _world.Result = await Proxy.AddAsync(2, 3, new CancellationTokenSource().Token);

    [When(@"^the proxy calls AddAsync\(1, 1\)$")]
    public async Task WhenProxyAddsOneOne() =>
        _world.Error = await Record.ExceptionAsync(async () => _world.Result = await Proxy.AddAsync(1, 1));

    private JsonElement LastWire(string method) =>
        _world.Connection!.Recording.SentJson.Select(JsonAssert.Parse)
            .Last(m => m.GetProperty("method").GetString() == method);

    [Then(@"^the wire request is a call to ""add"" with params by position or name per the contract setting$")]
    public void ThenWireAdd()
    {
        var wire = LastWire("add");
        Assert.True(wire.TryGetProperty("id", out _));
        Assert.Equal("[2,3]", wire.GetProperty("params").GetRawText());
    }

    [Then(@"^the result is (\d+)$")]
    public void ThenResultNumber(int expected) => Assert.Equal(expected, _world.Result);

    [Given(@"^the server returns error code 1001 for ""add""$")]
    public void GivenAddError() => _world.State.AddErrorCode = 1001;

    [Then("^a JsonRpcException with code 1001 is thrown$")]
    public void ThenProxyError() => Assert.Equal(1001, Assert.IsType<JsonRpcException>(_world.Error).Code);

    [When(@"^the proxy calls LogAsync\(""hi""\)$")]
    public async Task WhenProxyLogs() => await Proxy.LogAsync("hi");

    [Then("^the wire message has no id$")]
    public void ThenNoId() => Assert.False(LastWire("log").TryGetProperty("id", out _));

    [Then("^the task completes without waiting for a response$")]
    public async Task ThenNoWait()
    {
        // a notification produces no response; the server still receives it
        await Wait.UntilAsync(() => _world.State.Logs.Contains("hi"));
        Assert.DoesNotContain(_world.Connection!.Recording.Received.Select(System.Text.Encoding.UTF8.GetString), m => m.Contains("\"error\""));
    }

    [Then("^the wire params contain only the arguments 2 and 3$")]
    public void ThenOnlyArguments() => Assert.Equal("[2,3]", LastWire("add").GetProperty("params").GetRawText());
}
