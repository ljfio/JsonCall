using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JsonCall.AspNetCore;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Server;
using JsonCall.Specs.Support;
using JsonCall.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace JsonCall.Specs.Steps;

[Binding]
public sealed class StreamingSteps
{
    private readonly World _world;

    public StreamingSteps(World world) => _world = world;

    private T Get<T>(string key) => (T)_world.Values[key]!;

    private async Task<JsonRpcClient> ClientAsync()
    {
        _world.Connection ??= await Connections.ConnectAsync("in-memory", _world.State);
        return _world.Connection.Client;
    }

    private async Task<TestHost> HostAsync()
    {
        if (_world.Connection?.Host is { } host)
            return host;

        var created = await TestHost.StartRpcAsync(_world.State);
        _world.OnCleanup(created.DisposeAsync);
        return created;
    }

    private static JsonRpcParams Args(params object?[] values) => JsonRpcParams.ByPosition(values);

    private async Task<(List<Tick> Items, Exception? Error)> EnumerateAsync(string method, JsonRpcParams @params,
        CancellationToken ct = default)
    {
        var client = await ClientAsync();
        var items = new List<Tick>();

        try
        {
            await foreach (var tick in client.StreamAsync<Tick>(method, @params, ct))
                items.Add(tick);

            return (items, null);
        }
        catch (Exception ex)
        {
            return (items, ex);
        }
    }

    [Given("^a server contract$")]
    public void GivenContract(string contract) => Assert.Contains("IEvents", contract);

    // ---------------------------------------------------------------- delivery

    [When(@"^the client calls TicksAsync\(5, 100\) and enumerates the result$")]
    public async Task WhenEnumeratesFive()
    {
        var client = await ClientAsync();
        var items = new List<Tick>();
        var yieldedAtFirst = -1;

        await foreach (var tick in client.StreamAsync<Tick>("ticks", Args(5, 100)))
        {
            if (items.Count == 0)
                yieldedAtFirst = Volatile.Read(ref _world.State.Yielded);

            items.Add(tick);
        }

        _world.Values["items"] = items;
        _world.Values["yieldedAtFirst"] = yieldedAtFirst;
        _world.Values["completed"] = true;
    }

    [Then("^the first item is received before the producer has yielded the third$")]
    public void ThenFirstEarly() => Assert.InRange(Get<int>("yieldedAtFirst"), 1, 2);

    [Then("^all 5 items arrive in order$")]
    public void ThenFiveInOrder() => Assert.Equal(Enumerable.Range(0, 5), Get<List<Tick>>("items").Select(t => t.Index));

    [Then("^the enumeration completes$")]
    public void ThenCompletes() => Assert.True(Get<bool>("completed"));

    [When(@"^the client enumerates TicksAsync\((\d+), (\d+)\)$")]
    public async Task WhenEnumerates(int count, int delay)
    {
        var (items, error) = await EnumerateAsync("ticks", Args(count, delay));
        _world.Values["items"] = items;
        _world.Error = error;
    }

    [Then("^the enumeration completes with no items and no error$")]
    public void ThenEmpty()
    {
        Assert.Null(_world.Error);
        Assert.Empty(Get<List<Tick>>("items"));
    }

    // ---------------------------------------------------------------- HTTP / SSE

    private async Task PostRawAsync(string accept, string body)
    {
        var host = await HostAsync();
        using var http = new HttpClient { BaseAddress = host.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/rpc")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.ParseAdd(accept);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        _world.Values["status"] = (int)response.StatusCode;
        _world.Values["contentType"] = response.Content.Headers.ContentType?.MediaType;
        _world.Values["body"] = await response.Content.ReadAsStringAsync();
    }

    [When(@"^a raw HTTP client POSTs a TicksAsync\(3, 0\) request with ""Accept: (.*)""$")]
    public Task WhenRawPost(string accept) =>
        PostRawAsync(accept, "{\"jsonrpc\":\"2.0\",\"method\":\"ticks\",\"params\":[3,0],\"id\":1}");

    [Then(@"^the response content type is ""text/event-stream""$")]
    public void ThenSse() => Assert.Equal("text/event-stream", Get<string>("contentType"));

    private static List<(string? Event, string Data)> ParseEvents(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block =>
            {
                string? name = null;
                var data = new StringBuilder();

                foreach (var line in block.Split('\n'))
                {
                    if (line.StartsWith("event:"))
                        name = line["event:".Length..].Trim();
                    else if (line.StartsWith("data:"))
                        data.Append(line["data:".Length..].Trim());
                }

                return (name, data.ToString());
            }).ToList();

    [Then(@"^the body contains 3 ""data:"" events with the item JSON$")]
    public void ThenDataEvents()
    {
        var items = ParseEvents(Get<string>("body")).Where(e => e.Event is null).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { 0, 1, 2 }, items.Select(e => JsonDocument.Parse(e.Data).RootElement.GetProperty("index").GetInt32()));
    }

    [Then(@"^the last event is ""event: complete""$")]
    public void ThenLastComplete() => Assert.Equal("complete", ParseEvents(Get<string>("body")).Last().Event);

    [Then("^the response is a JSON-RPC error explaining the method streams$")]
    public void ThenStreamsError()
    {
        var error = JsonDocument.Parse(Get<string>("body")).RootElement.GetProperty("error");
        Assert.Contains("stream", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        _world.Values["errorCode"] = error.GetProperty("code").GetInt32();
    }

    [Then(@"^the HTTP status is 406 or the error code is the documented ""stream required"" code$")]
    public void ThenStreamRequired() =>
        Assert.True(Get<int>("status") == 406 || Get<int>("errorCode") == JsonRpcErrorCodes.StreamRequired);

    // ---------------------------------------------------------------- stream notifications

    [When(@"^a raw peer sends a TicksAsync\(2, 0\) call with id 1$")]
    public async Task WhenRawPeerSends()
    {
        var host = _world.Connection!.Host!;
        var uri = new UriBuilder(host.BaseAddress) { Scheme = "ws", Path = "/rpc" }.Uri;
        _world.Raw = new RawPeer(await WebSocketMessageTransport.ConnectAsync(uri));
        await _world.Raw.SendJsonAsync("{\"jsonrpc\":\"2.0\",\"method\":\"ticks\",\"params\":[2,0],\"id\":1}");
    }

    [Then(@"^it first receives the response \{""id"":1,""result"":\{""stream"":""<streamId>""\}\}$")]
    public async Task ThenFirstResponse()
    {
        var response = JsonAssert.Parse(await _world.Raw!.NextAsync());
        Assert.Equal(1, response.GetProperty("id").GetInt32());
        _world.Values["streamId"] = response.GetProperty("result").GetProperty("stream").GetString();
        Assert.False(string.IsNullOrEmpty(Get<string>("streamId")));
    }

    [Then(@"^then two ""\$/stream\.item"" notifications with seq 0 and 1 and the item values$")]
    public async Task ThenItems()
    {
        for (var seq = 0; seq < 2; seq++)
        {
            var message = JsonAssert.Parse(await _world.Raw!.NextAsync());
            Assert.Equal("$/stream.item", message.GetProperty("method").GetString());
            Assert.False(message.TryGetProperty("id", out _));

            var p = message.GetProperty("params");
            Assert.Equal(Get<string>("streamId"), p.GetProperty("stream").GetString());
            Assert.Equal(seq, p.GetProperty("seq").GetInt32());
            Assert.Equal(seq, p.GetProperty("value").GetProperty("index").GetInt32());
        }
    }

    [Then(@"^then a ""\$/stream\.end"" notification for that stream$")]
    public async Task ThenEnd()
    {
        var message = JsonAssert.Parse(await _world.Raw!.NextAsync());
        Assert.Equal("$/stream.end", message.GetProperty("method").GetString());
        Assert.Equal(Get<string>("streamId"), message.GetProperty("params").GetProperty("stream").GetString());
    }

    [When("^two TicksAsync streams are enumerated concurrently$")]
    public async Task WhenTwoStreams()
    {
        var first = EnumerateAsync("ticks", Args(6, 5));
        var second = EnumerateAsync("ticks", Args(4, 7));
        await Task.WhenAll(first, second);
        _world.Values["first"] = await first;
        _world.Values["second"] = await second;
    }

    [Then("^each enumeration yields only its own items in order$")]
    public void ThenOwnItems()
    {
        var first = Get<(List<Tick> Items, Exception? Error)>("first");
        var second = Get<(List<Tick> Items, Exception? Error)>("second");

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.Equal(Enumerable.Range(0, 6), first.Items.Select(t => t.Index));
        Assert.Equal(Enumerable.Range(0, 4), second.Items.Select(t => t.Index));
    }

    // ---------------------------------------------------------------- cancellation and disconnect

    [Given("^a producer that yields forever and records when its enumerator is disposed$")]
    public void GivenEndless()
    {
        // provided by the "endless" method of the test service
    }

    [When("^the client enumerates 3 items and then cancels$")]
    public async Task WhenEnumerateThenCancel()
    {
        var client = await ClientAsync();
        using var cts = new CancellationTokenSource();
        var count = 0;

        try
        {
            await foreach (var _ in client.StreamAsync<Tick>("endless", null, cts.Token))
            {
                if (++count == 3)
                {
                    _world.Values["cancelStart"] = Stopwatch.GetTimestamp();
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.Delay(100);
        _world.Values["receivedAtCancel"] = _world.Connection!.Recording.Received.Count;
    }

    [Then(@"^the producer's cancellation token is signalled within 500 ms$")]
    public async Task ThenTokenSignalled()
    {
        await _world.State.EndlessCancelled.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(Stopwatch.GetElapsedTime(Get<long>("cancelStart")) < TimeSpan.FromMilliseconds(500));
    }

    [Then(@"^the producer's enumerator is disposed exactly once$")]
    public async Task ThenDisposedOnce()
    {
        await Wait.UntilAsync(() => Volatile.Read(ref _world.State.EndlessDisposed) >= 1);
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref _world.State.EndlessDisposed));
    }

    [Then("^no further items are sent$")]
    public async Task ThenNoFurtherItems()
    {
        var before = _world.Connection!.Recording.Received.Count;
        await Task.Delay(300);
        Assert.Equal(before, _world.Connection.Recording.Received.Count);
    }

    [Given(@"^SubscribeAsync\(""orders""\) registers a subscription$")]
    public async Task GivenSubscription()
    {
        var client = await ClientAsync();
        var cts = new CancellationTokenSource();
        var first = new TaskCompletionSource();

        _world.Values["subscription"] = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in client.StreamAsync<Tick>("subscribe", Args("orders"), cts.Token))
                    first.TrySetResult();
            }
            catch
            {
            }
        });

        _world.OnCleanup(() =>
        {
            cts.Cancel();
            return ValueTask.CompletedTask;
        });

        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("orders", _world.State.Subscriptions);
    }

    [When("^the client connection drops abruptly$")]
    public async Task WhenDrops()
    {
        switch (_world.Connection!.InnerTransport)
        {
            case WebSocketMessageTransport socket:
                socket.Abort();
                break;
            default:
                await _world.Connection.InnerTransport!.DisposeAsync();
                break;
        }
    }

    [Then("^the subscription is unsubscribed$")]
    public async Task ThenUnsubscribed() =>
        await Wait.UntilAsync(() => _world.State.Subscriptions.Count == 0, TimeSpan.FromSeconds(5), "The subscription was not released");

    [Then("^the producer's enumerator is disposed$")]
    public async Task ThenEnumeratorDisposed() =>
        await Wait.UntilAsync(() => Volatile.Read(ref _world.State.EventsDisposed) >= 1);

    [Given("^the response stream fails on the second write$")]
    public async Task GivenFailingWrite()
    {
        // The earlier step subscribed over the default connection; release it so only the HTTP flow is observed.
        if (_world.Connection is { } existing)
        {
            await existing.DisposeAsync();
            _world.Connection = null;
            await Wait.UntilAsync(() => _world.State.Subscriptions.Count == 0);
        }

        var baseline = Volatile.Read(ref _world.State.SubscribeCount);

        var host = await TestHost.StartAsync(
            services => services.AddJsonRpc(o => TestHost.AddContracts(o, _world.State)),
            app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Response.Body = new FailingStream(context.Response.Body, failOnWrite: 2);
                    await next();
                });
                app.MapJsonRpc("/rpc");
            });

        _world.OnCleanup(host.DisposeAsync);

        using var http = new HttpClient { BaseAddress = host.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/rpc")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"subscribe\",\"params\":[\"orders\"],\"id\":1}",
                Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.ParseAdd("text/event-stream");

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await response.Content.ReadAsStringAsync();
        }
        catch
        {
            // the server aborts the response
        }

        await Wait.UntilAsync(() => Volatile.Read(ref _world.State.SubscribeCount) > baseline,
            message: "The HTTP subscription never started");
    }

    // ---------------------------------------------------------------- back-pressure

    [Given(@"^a producer yielding 10,000 items$")]
    public void GivenTenThousand() => _world.Values["count"] = 10_000;

    [Given(@"^a consumer that processes one item per 10 ms$")]
    public void GivenSlowConsumer() => _world.Values["consumerDelay"] = TimeSpan.FromMilliseconds(10);

    [When("^the client enumerates the stream$")]
    public async Task WhenEnumeratesSlowly()
    {
        // A bounded in-memory transport (4 messages) and a client stream buffer of 8 make the limit observable.
        _world.Connection = await Connections.ConnectAsync("in-memory", _world.State,
            clientStreamBuffer: 8, transportCapacity: 4);

        using var cts = new CancellationTokenSource();
        var consumed = 0;
        var maxAhead = 0;

        try
        {
            await foreach (var _ in _world.Connection.Client.StreamAsync<Tick>(
                               "ticks", Args(Get<int>("count"), 0), cts.Token))
            {
                consumed++;
                maxAhead = Math.Max(maxAhead, Volatile.Read(ref _world.State.Yielded) - consumed);

                if (consumed == 40)
                {
                    // the consumer goes idle but keeps the enumeration open: the producer must stay paused
                    await Task.Delay(100);
                    var before = Volatile.Read(ref _world.State.Yielded);
                    await Task.Delay(300);
                    _world.Values["pausedProgress"] = Volatile.Read(ref _world.State.Yielded) - before;
                    break;
                }

                await Task.Delay(Get<TimeSpan>("consumerDelay"));
            }
        }
        catch (OperationCanceledException)
        {
        }

        _world.Values["maxAhead"] = maxAhead;
    }

    [Then("^server-side buffered items never exceed the configured stream buffer size$")]
    public void ThenBounded() =>
        // client buffer (8) + transport capacity (4) + items held by the read loop and the sender
        Assert.InRange(Get<int>("maxAhead"), 0, 8 + 4 + 4);

    [Then("^the producer is paused while the buffer is full$")]
    public void ThenPaused() => Assert.InRange(Get<int>("pausedProgress"), 0, 2);

    // ---------------------------------------------------------------- errors

    [When(@"^the client enumerates FailAfterAsync\((\d+)\)$")]
    public async Task WhenFailAfter(int items)
    {
        var (received, error) = await EnumerateAsync("failAfter", Args(items));
        _world.Values["items"] = received;
        _world.Error = error;
    }

    [Then(@"^it receives (\d+) items$")]
    public void ThenReceives(int count) => Assert.Equal(count, Get<List<Tick>>("items").Count);

    [Then("^then a JsonRpcException is thrown from the enumeration$")]
    public void ThenException()
    {
        var error = Assert.IsType<JsonRpcException>(_world.Error);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Then("^the items already received are unaffected$")]
    public void ThenUnaffected() => Assert.Equal(new[] { 0, 1 }, Get<List<Tick>>("items").Select(t => t.Index));

    [Then("^a JsonRpcException is thrown on the first MoveNextAsync$")]
    public void ThenFirstMoveNext()
    {
        Assert.IsType<JsonRpcException>(_world.Error);
        Assert.Empty(Get<List<Tick>>("items"));
    }

    // ---------------------------------------------------------------- abandon and notification

    [When("^the client enumerates 2 items and disposes the enumerator$")]
    public async Task WhenAbandon()
    {
        var client = await ClientAsync();
        await using var enumerator = client.StreamAsync<Tick>("endless").GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
    }

    [Then("^the server stops the producer$")]
    public async Task ThenProducerStops() =>
        await Wait.UntilAsync(() => Volatile.Read(ref _world.State.EndlessDisposed) >= 1);

    [When("^a raw peer sends TicksAsync as a notification without an id$")]
    public async Task WhenRawNotification()
    {
        var (clientSide, serverSide) = InMemoryTransport.CreatePair();
        _world.OnCleanup(Connections.CreateServer(_world.State).Attach(serverSide).DisposeAsync);
        _world.Raw = new RawPeer(clientSide);
        await _world.Raw.SendJsonAsync("{\"jsonrpc\":\"2.0\",\"method\":\"ticks\",\"params\":[3,0]}");
    }

    [Then("^no stream is started$")]
    public async Task ThenNoStream()
    {
        Assert.True(await _world.Raw!.ReceivesNothingAsync(), "The server must not respond to a notification");
        Assert.Equal(0, Volatile.Read(ref _world.State.Yielded));
    }

    // ---------------------------------------------------------------- authorization

    [Given(@"^the TicksAsync method requires the ""reader"" role$")]
    public async Task GivenRoleRequired()
    {
        var host = await TestHost.StartAuthAsync(_world.State);
        _world.OnCleanup(host.DisposeAsync);
        _world.Values["authHost"] = host;
    }

    [When("^an unauthorized client calls it$")]
    public async Task WhenUnauthorizedCalls()
    {
        var host = Get<TestHost>("authHost");
        var http = new HttpClient { BaseAddress = host.BaseAddress };
        var transport = new HttpMessageTransport(http, new Uri(host.BaseAddress, "/rpc"), ownsClient: true);
        var client = new JsonRpcClient(transport);
        _world.OnCleanup(client.DisposeAsync);

        try
        {
            await foreach (var _ in client.StreamAsync<Tick>("secureTicks", Args(3)))
            {
            }
        }
        catch (Exception ex)
        {
            _world.Error = ex;
        }
    }

    [Then("^no producer is started$")]
    public void ThenNoProducer() => Assert.Equal(0, Volatile.Read(ref _world.State.Started));

    [Then("^the client receives an authorization error$")]
    public void ThenAuthError()
    {
        var error = Assert.IsType<JsonRpcException>(_world.Error);
        Assert.True(error.Code is JsonRpcErrorCodes.Unauthorized or JsonRpcErrorCodes.Forbidden, $"{error.Code}: {error.Message}");
        Assert.Contains(error.Code, new[] { JsonRpcErrorCodes.Unauthorized, JsonRpcErrorCodes.Forbidden });
    }
}

/// <summary>Wraps a response stream and throws on the nth write.</summary>
internal sealed class FailingStream : Stream
{
    private readonly Stream _inner;
    private readonly int _failOnWrite;
    private int _writes;

    public FailingStream(Stream inner, int failOnWrite)
    {
        _inner = inner;
        _failOnWrite = failOnWrite;
    }

    private void Count()
    {
        if (Interlocked.Increment(ref _writes) >= _failOnWrite)
            throw new IOException("The connection was broken.");
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Count();
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Count();
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Count();
        _inner.Write(buffer, offset, count);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
