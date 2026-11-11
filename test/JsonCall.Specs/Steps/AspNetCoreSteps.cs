using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JsonCall.AspNetCore;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Serializer.Cbor;
using JsonCall.Serializer.MessagePack;
using JsonCall.Server;
using JsonCall.Specs.Support;
using JsonCall.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace JsonCall.Specs.Steps;

/// <summary>Describes the ASP.NET Core application a scenario runs against; started lazily by the first When step.</summary>
public sealed class HostSpec
{
    public List<Action<JsonRpcServerOptions>> Contracts { get; } = new();

    public List<Action<IServiceCollection>> Services { get; } = new();

    public List<Action<WebApplication>> BeforeMap { get; } = new();

    public Func<WebApplication, IEndpointConventionBuilder>? Map { get; set; }

    /// <summary>Registers the shared test contracts. Scenarios that need an exact set turn this off.</summary>
    public bool DefaultContracts { get; set; } = true;

    public bool TestService { get; set; } = true;

    public TestHost? Host { get; private set; }

    public async Task<TestHost> StartAsync(TestState state)
    {
        if (Host is not null)
            return Host;

        Host = await TestHost.StartAsync(
            services =>
            {
                services.AddSingleton(state);
                services.AddScoped<ICallContext, CallContext>();
                services.AddAuthentication("Test")
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
                services.AddAuthorization();

                foreach (var configure in Services)
                    configure(services);

                services.AddJsonRpc(
                    options =>
                    {
                        if (DefaultContracts)
                        {
                            options.AddContract<IMath, MathService>();
                            options.AddContract<IGreeter>(new TestService(state));
                            options.AddContract<IEvents>(new TestService(state));
                            options.AddContract<IAdmin, AdminService>();

                            if (TestService)
                                options.AddContract<ITestService>(new TestService(state));
                        }

                        foreach (var contract in Contracts)
                            contract(options);

                        state.ConfigureServer?.Invoke(options);
                    },
                    host => host.AddSerializer("cbor", new CborMessageSerializer())
                        .AddSerializer("messagepack", new MessagePackMessageSerializer()));
            },
            app =>
            {
                foreach (var action in BeforeMap)
                    action(app);

                app.UseAuthentication();
                app.UseAuthorization();

                _ = (Map ?? (a => a.MapJsonRpc("/rpc")))(app);
            });

        return Host;
    }
}

[Binding]
public sealed class AspNetCoreSteps
{
    private readonly World _world;
    private readonly HostSpec _spec = new();

    public AspNetCoreSteps(World world)
    {
        _world = world;
        _world.OnCleanup(async () =>
        {
            if (_spec.Host is not null)
                await _spec.Host.DisposeAsync();
        });
    }

    private TestHost Host => _spec.Host ?? throw new InvalidOperationException("The host has not been started.");

    private async Task<HttpClient> ClientAsync()
    {
        var host = await _spec.StartAsync(_world.State);
        return new HttpClient { BaseAddress = host.BaseAddress };
    }

    private T Get<T>(string key) => (T)_world.Values[key]!;

    private async Task<HttpResponseMessage> PostAsync(string body, string contentType = "application/json",
        Action<HttpRequestMessage>? configure = null, CancellationToken ct = default)
    {
        using var http = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/rpc")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };

        configure?.Invoke(request);

        var response = await http.SendAsync(request, ct);
        _world.Values["status"] = (int)response.StatusCode;
        _world.Values["body"] = await response.Content.ReadAsStringAsync(ct);
        _world.Values["contentType"] = response.Content.Headers.ContentType?.MediaType;
        return response;
    }

    private static string AddCall(int id = 1, string @params = "[1,2]") =>
        $"{{\"jsonrpc\":\"2.0\",\"method\":\"add\",\"params\":{@params},\"id\":{id}}}";

    // ---------------------------------------------------------------- background and mapping

    [Given(@"^an ASP\.NET Core test host with services\.AddJsonRpc\(o => o\.AddContract<IMath, MathService>\(\)\)$")]
    public void GivenHost()
    {
        // MathService (IMath) is registered by the HostSpec for every scenario.
    }

    [Given(@"^the app maps app\.MapJsonRpc\(""/rpc""\)$")]
    public void GivenMapDefault() => _spec.Map = app => app.MapJsonRpc("/rpc");

    [Given(@"^the app maps app\.MapJsonRpc\(""/rpc"", o => o\.Transports = JsonRpcTransports\.Http\)$")]
    public void GivenMapHttpOnly() =>
        _spec.Map = app => app.MapJsonRpc("/rpc", o => o.Transports = JsonRpcTransports.Http);

    [Given(@"^the app maps app\.MapJsonRpc\(""/rpc""\)\.RequireAuthorization\(\)$")]
    public void GivenMapAuthorized() => _spec.Map = app => app.MapJsonRpc("/rpc").RequireAuthorization();

    [Given(@"^the app maps app\.MapJsonRpc\(""/rpc""\)\.RequireCors\(""web""\)\.RequireRateLimiting\(""rpc""\)$")]
    public void GivenMapCorsAndRateLimit()
    {
        _spec.Services.Add(services =>
        {
            services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins("http://allowed.test").AllowAnyHeader().AllowAnyMethod()));
            services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = 429;
                o.AddFixedWindowLimiter("rpc", w =>
                {
                    w.PermitLimit = 2;
                    w.Window = TimeSpan.FromMinutes(1);
                    w.QueueLimit = 0;
                });
            });
        });

        _spec.BeforeMap.Add(app =>
        {
            app.UseCors();
            app.UseRateLimiter();
        });

        _spec.Map = app => app.MapJsonRpc("/rpc").RequireCors("web").RequireRateLimiting("rpc");
    }

    [Given(@"^the app maps app\.MapJsonRpc\(""/rpc"", r => r\.Method\(""echo"", \(string s\) => s\)\)$")]
    public void GivenMapMinimal()
    {
        _spec.TestService = false;
        _spec.Map = app => app.MapJsonRpc("/rpc", r => r.Method("echo", (string s) => s));
    }

    // ---------------------------------------------------------------- HTTP

    [When(@"^a client POSTs '(.*)' to ""/rpc"" as application/json$")]
    public async Task WhenPosts(string body) => await PostAsync(body);

    [Then(@"^the HTTP status is (\d+)$")]
    public void ThenStatus(int status) => Assert.Equal(status, Get<int>("status"));

    [Then("^the body is '(.*)'$")]
    public void ThenBody(string json) => JsonAssert.Equal(json, Get<string>("body"));

    [When(@"^a client POSTs a notification to ""/rpc""$")]
    public async Task WhenNotification() =>
        await PostAsync("{\"jsonrpc\":\"2.0\",\"method\":\"log\",\"params\":[\"x\"]}");

    [When("^a client POSTs a batch of 2 calls$")]
    public async Task WhenBatch() => await PostAsync($"[{AddCall(1)},{AddCall(2, "[3,4]")}]");

    [Then("^the body is a batch of 2 responses$")]
    public void ThenBatch()
    {
        var body = JsonAssert.Parse(Get<string>("body"));
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(2, body.GetArrayLength());
    }

    [When("^a client POSTs malformed JSON$")]
    public async Task WhenMalformed() => await PostAsync("{\"jsonrpc\":");

    [Then(@"^the body is a JSON-RPC error with code -32700$")]
    public void ThenParseError() =>
        Assert.Equal(-32700, JsonAssert.Parse(Get<string>("body")).GetProperty("error").GetProperty("code").GetInt32());

    [When(@"^a client POSTs with content type ""text/plain""$")]
    public async Task WhenWrongContentType() => await PostAsync(AddCall(), "text/plain");

    [Given("^the CBOR serializer is registered$")]
    public void GivenCbor()
    {
        // CBOR and MessagePack are registered by the HostSpec.
    }

    [When(@"^a client POSTs a CBOR-encoded call with content type ""application/cbor""$")]
    public async Task WhenCborPost()
    {
        var cbor = new CborMessageSerializer();
        var body = cbor.FromJson(Encoding.UTF8.GetBytes(AddCall()));

        using var http = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/rpc") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/cbor");

        using var response = await http.SendAsync(request);
        _world.Values["status"] = (int)response.StatusCode;
        _world.Values["contentType"] = response.Content.Headers.ContentType?.MediaType;
        _world.Values["bytes"] = await response.Content.ReadAsByteArrayAsync();
    }

    [Then("^the response is CBOR encoded with the same content type$")]
    public void ThenCborResponse()
    {
        Assert.Equal("application/cbor", Get<string>("contentType"));
        var json = Encoding.UTF8.GetString(new CborMessageSerializer().ToJson(Get<byte[]>("bytes")));
        Assert.Equal(3, JsonAssert.Parse(json).GetProperty("result").GetInt32());
    }

    // ---------------------------------------------------------------- WebSocket

    private async Task<RawPeer> OpenSocketAsync()
    {
        var host = await _spec.StartAsync(_world.State);
        var uri = new UriBuilder(host.BaseAddress) { Scheme = "ws", Path = "/rpc" }.Uri;
        var raw = new RawPeer(await WebSocketMessageTransport.ConnectAsync(uri));
        _world.Raw = raw;
        return raw;
    }

    [When(@"^a client opens a WebSocket to ""/rpc"" and sends a call$")]
    public async Task WhenOpensSocket()
    {
        var raw = await OpenSocketAsync();
        await raw.SendJsonAsync(AddCall(1));
        _world.Values["first"] = await raw.NextAsync();
    }

    [Then("^it receives the response over the socket$")]
    public void ThenSocketResponse() =>
        Assert.Equal(3, JsonAssert.Parse(Get<string>("first")).GetProperty("result").GetInt32());

    [Then("^the connection stays open for further calls$")]
    public async Task ThenSocketOpen()
    {
        await _world.Raw!.SendJsonAsync(AddCall(2, "[5,6]"));
        Assert.Equal(11, JsonAssert.Parse(await _world.Raw.NextAsync()).GetProperty("result").GetInt32());
    }

    [When(@"^a client attempts a WebSocket upgrade on ""/rpc""$")]
    public async Task WhenUpgradeAttempt()
    {
        var host = await _spec.StartAsync(_world.State);
        var uri = new UriBuilder(host.BaseAddress) { Scheme = "ws", Path = "/rpc" }.Uri;
        using var socket = new ClientWebSocket();
        _world.Error = await Record.ExceptionAsync(async () => await socket.ConnectAsync(uri, CancellationToken.None));
    }

    [Then("^the upgrade is refused with HTTP 400 or 404$")]
    public void ThenRefused()
    {
        var error = Assert.IsType<WebSocketException>(_world.Error);
        Assert.True(error.Message.Contains("400") || error.Message.Contains("404"), error.Message);
    }

    // ---------------------------------------------------------------- authorization, CORS, rate limits

    [When("^an unauthenticated client POSTs a call$")]
    public async Task WhenUnauthenticated() => await PostAsync(AddCall());

    [Given(@"^the contract method ""admin_reset"" is annotated \[Authorize\(Roles = ""admin""\)\]$")]
    public void GivenAdminReset()
    {
        // IAdmin.ResetAsync carries the attribute.
    }

    [When(@"^a non-admin user calls ""admin_reset""$")]
    public async Task WhenNonAdmin() =>
        await PostAsync("{\"jsonrpc\":\"2.0\",\"method\":\"admin_reset\",\"id\":1}", configure: r =>
        {
            r.Headers.Add("X-Test-User", "bob");
            r.Headers.Add("X-Test-Roles", "user");
        });

    [Then(@"^the response is a JSON-RPC error with the documented ""forbidden"" code$")]
    public void ThenForbidden() =>
        Assert.Equal(JsonRpcErrorCodes.Forbidden, JsonAssert.Parse(Get<string>("body")).GetProperty("error").GetProperty("code").GetInt32());

    [Then("^the handler is not invoked$")]
    public void ThenNotInvoked() => Assert.Equal(0, Volatile.Read(ref _world.State.Started));

    [When("^a client from an allowed origin exceeds the rate limit$")]
    public async Task WhenRateLimited()
    {
        for (var i = 0; i < 4; i++)
        {
            await PostAsync(AddCall(i + 1), configure: r => r.Headers.Add("Origin", "http://allowed.test"));

            if (Get<int>("status") == 429)
                return;
        }
    }

    [Then("^it receives HTTP 429$")]
    public void ThenTooMany() => Assert.Equal(429, Get<int>("status"));

    // ---------------------------------------------------------------- DI

    [Given("^MathService depends on a scoped service ICallContext$")]
    public void GivenScoped()
    {
        // MathService takes an ICallContext (scoped).
    }

    [When("^two calls arrive concurrently$")]
    public async Task WhenConcurrent() => await Task.WhenAll(PostAsync(AddCall(1)), PostAsync(AddCall(2)));

    [Then("^each call receives its own ICallContext instance$")]
    public void ThenOwnContext()
    {
        lock (_world.State.ScopeIds)
        {
            Assert.Equal(2, _world.State.ScopeIds.Count);
            Assert.Equal(2, _world.State.ScopeIds.Distinct().Count());
        }
    }

    [Given("^a WebSocket connection sending 3 calls$")]
    public async Task GivenSocketThreeCalls()
    {
        var raw = await OpenSocketAsync();

        for (var i = 1; i <= 3; i++)
        {
            await raw.SendJsonAsync(AddCall(i));
            Assert.NotNull(await raw.NextAsync());
        }
    }

    [Then("^each handler invocation has a distinct scope$")]
    public void ThenDistinctScopes()
    {
        lock (_world.State.ScopeIds)
        {
            Assert.Equal(3, _world.State.ScopeIds.Count);
            Assert.Equal(3, _world.State.ScopeIds.Distinct().Count());
        }
    }

    [Then("^scoped services are disposed after each call$")]
    public async Task ThenScopesDisposed() =>
        await Wait.UntilAsync(() => Volatile.Read(ref _world.State.ScopesDisposed) == 3);

    [Given("^a handler that injects IHttpContextAccessor$")]
    public void GivenAccessor()
    {
        // AdminService.WhoAmIAsync injects IHttpContextAccessor.
    }

    [When("^an authenticated user calls it$")]
    public async Task WhenAuthenticated()
    {
        await PostAsync("{\"jsonrpc\":\"2.0\",\"method\":\"whoami\",\"id\":1}", configure: r => r.Headers.Add("X-Test-User", "ada"));
        _world.Result = JsonAssert.Parse(Get<string>("body")).GetProperty("result");
    }

    [Then("^the handler sees that user's ClaimsPrincipal$")]
    public void ThenSeesUser() => Assert.Equal("ada", ((JsonElement)_world.Result!).GetString());

    // ---------------------------------------------------------------- start-up validation

    [Given(@"^two contracts both declare the JSON-RPC method ""add""$")]
    public void GivenDuplicate()
    {
        _spec.DefaultContracts = false;
        _spec.Services.Add(s => s.AddScoped<DuplicateMath>());
        _spec.Contracts.Add(o =>
        {
            o.AddContract<IMathA, DuplicateMath>();
            o.AddContract<IMathB, DuplicateMath>();
        });
    }

    [When("^the application starts$")]
    public async Task WhenStarts() =>
        _world.Error = await Record.ExceptionAsync(async () => await _spec.StartAsync(_world.State));

    [Then("^startup fails with an error naming both contracts and the method$")]
    public void ThenDuplicateError()
    {
        Assert.NotNull(_world.Error);
        Assert.Contains("IMathA", _world.Error!.Message);
        Assert.Contains("IMathB", _world.Error.Message);
        Assert.Contains("'add'", _world.Error.Message);
    }

    [Given("^a registered contract with no implementation$")]
    public void GivenNoImplementation()
    {
        _spec.DefaultContracts = false;
        _spec.Contracts.Add(o => o.AddContract<IMathA>());
    }

    [Then("^startup fails with an error naming the contract$")]
    public void ThenMissingImplementation()
    {
        Assert.NotNull(_world.Error);
        Assert.Contains("IMathA", _world.Error!.Message);
    }

    // ---------------------------------------------------------------- binding

    [Given(@"^the contract method AddAsync\(int a, int b\)$")]
    public void GivenAddContract()
    {
    }

    [When(@"^called with params \{""a"":1,""b"":2\}, \[1,2\] and \[1,2,""extra""\]$")]
    public async Task WhenCalledThreeWays()
    {
        var results = new List<JsonElement>();

        foreach (var @params in new[] { "{\"a\":1,\"b\":2}", "[1,2]", "[1,2,\"extra\"]" })
        {
            await PostAsync(AddCall(1, @params));
            results.Add(JsonAssert.Parse(Get<string>("body")));
        }

        _world.Values["results"] = results;
    }

    [Then("^the first two succeed with 3 and the third fails with -32602$")]
    public void ThenThreeResults()
    {
        var results = Get<List<JsonElement>>("results");
        Assert.Equal(3, results[0].GetProperty("result").GetInt32());
        Assert.Equal(3, results[1].GetProperty("result").GetInt32());
        Assert.Equal(-32602, results[2].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Given(@"^the contract method GreetAsync\(string name, string greeting = ""Hello""\)$")]
    public void GivenGreet()
    {
    }

    [When(@"^called with \[""Ada""\]$")]
    public async Task WhenGreet()
    {
        await PostAsync("{\"jsonrpc\":\"2.0\",\"method\":\"greet\",\"params\":[\"Ada\"],\"id\":1}");
        _world.Result = JsonAssert.Parse(Get<string>("body")).GetProperty("result");
    }

    [Given("^a handler awaiting its CancellationToken$")]
    public void GivenAwaitingToken()
    {
    }

    [When("^the HTTP client aborts the request$")]
    public async Task WhenAborts()
    {
        using var cts = new CancellationTokenSource();
        var pending = PostAsync("{\"jsonrpc\":\"2.0\",\"method\":\"slow\",\"id\":1}", ct: cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        await Record.ExceptionAsync(async () => await pending);
    }

    [Then("^the handler's token is cancelled$")]
    public async Task ThenTokenCancelled() =>
        await _world.State.SlowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // ---------------------------------------------------------------- shutdown

    [Given("^a call to a handler that takes 500 ms is in flight$")]
    public async Task GivenCallInFlight()
    {
        _world.State.DefaultDelay = TimeSpan.FromMilliseconds(500);
        var host = await _spec.StartAsync(_world.State);
        var http = new HttpClient { BaseAddress = host.BaseAddress };
        _world.Values["call"] = http.PostAsync("/rpc", new StringContent(
            "{\"jsonrpc\":\"2.0\",\"method\":\"delay_echo\",\"params\":[\"x\"],\"id\":1}", Encoding.UTF8, "application/json"));
        await Task.Delay(150);
    }

    [Given("^a client enumerating an endless stream$")]
    public async Task GivenEndlessStream()
    {
        var host = await _spec.StartAsync(_world.State);
        var http = new HttpClient { BaseAddress = host.BaseAddress };
        var transport = new HttpMessageTransport(http, new Uri(host.BaseAddress, "/rpc"), ownsClient: true);
        var client = new JsonRpcClient(transport);
        _world.OnCleanup(client.DisposeAsync);

        var first = new TaskCompletionSource();

        _world.Values["stream"] = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in client.StreamAsync<Tick>("subscribe", JsonRpcParams.ByPosition("orders")))
                    first.TrySetResult();

                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [When("^the host begins shutdown$")]
    public void WhenShutdown() => _world.Values["stopping"] = Host.App.StopAsync();

    [Then("^the call completes successfully before the host stops$")]
    public async Task ThenCallCompletes()
    {
        using var response = await Get<Task<HttpResponseMessage>>("call");
        var body = JsonAssert.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("x", body.GetProperty("result").GetString());
        await Get<Task>("stopping");
    }

    [Then("^new connections are refused$")]
    public async Task ThenRefusedConnections()
    {
        await Get<Task>("stopping");
        using var http = new HttpClient { BaseAddress = Host.BaseAddress, Timeout = TimeSpan.FromSeconds(3) };
        var error = await Record.ExceptionAsync(async () =>
            await http.PostAsync("/rpc", new StringContent(AddCall(), Encoding.UTF8, "application/json")));
        Assert.NotNull(error);
    }

    [Then("^the stream is completed or terminated with a documented shutdown error$")]
    public async Task ThenStreamEnded()
    {
        var error = await Get<Task<Exception?>>("stream").WaitAsync(TimeSpan.FromSeconds(10));
        await Get<Task>("stopping");

        if (error is not null)
            Assert.Equal(JsonRpcErrorCodes.ShuttingDown, Assert.IsType<JsonRpcException>(error).Code);
    }

    // ---------------------------------------------------------------- minimal API and metadata

    [When(@"^a client calls ""echo"" with \[""hi""\]$")]
    public async Task WhenEchoHi()
    {
        await PostAsync("{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[\"hi\"],\"id\":1}");
        _world.Result = JsonAssert.Parse(Get<string>("body")).GetProperty("result");
    }

    [When("^the application's EndpointDataSource is enumerated$")]
    public async Task WhenEnumerated()
    {
        var host = await _spec.StartAsync(_world.State);
        _world.Values["endpoints"] = host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.ToList();
    }

    [Then(@"^an endpoint for ""/rpc"" exists with JSON-RPC metadata listing its method names$")]
    public void ThenMetadata()
    {
        var endpoint = Get<List<Endpoint>>("endpoints")
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/rpc");

        var metadata = endpoint.Metadata.GetMetadata<JsonRpcEndpointMetadata>();
        Assert.NotNull(metadata);
        Assert.Contains("add", metadata!.Methods);
        Assert.Contains("greet", metadata.Methods);
    }
}
