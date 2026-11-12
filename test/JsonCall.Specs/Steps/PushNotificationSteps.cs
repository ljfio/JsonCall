using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Specs.Support;
using Reqnroll;

namespace JsonCall.Specs.Steps;

/// <summary>
/// Steps for feature 10: typed On&lt;Name&gt; handlers for server-pushed notifications, generated from the checked-in
/// Language Server Protocol and Agent Client Protocol contracts (Support/Data/lsp, Support/Data/acp).
/// </summary>
[Binding]
public sealed class PushNotificationSteps
{
    private readonly World _world;
    private BuildResult? _build;
    private string _document = "";
    private readonly Dictionary<string, string> _siblings = new();
    private readonly Dictionary<string, string> _metadata = new();
    private JsonRpcPeer? _clientPeer;
    private JsonRpcPeer? _serverPeer;
    private object? _client;
    private Type[] _handlerTypes = Array.Empty<Type>();
    private readonly List<object?[]> _first = new();
    private readonly List<object?[]> _second = new();
    private readonly TaskCompletionSource<object?[]> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PushNotificationSteps(World world) => _world = world;

    // ---------------------------------------------------------------- fixtures

    private void Load(string document, string client, string ns, params string[] siblings)
    {
        _document = document;
        _metadata["ClientName"] = client;
        _metadata["Namespace"] = ns;

        foreach (var sibling in siblings)
            _siblings[sibling] = ContractData.Read(sibling);
    }

    private BuildResult Build() =>
        _build ??= GeneratorHarness.Build(ContractData.Read(_document), _document, _metadata, siblings: _siblings);

    private Type Interface() => Build().Type("I" + _metadata["ClientName"]);

    private Type ClientType() => Build().Type(_metadata["ClientName"]);

    private void EnsureClient()
    {
        if (_client is not null)
            return;

        var (clientSide, serverSide) = InMemoryTransport.CreatePair();
        _clientPeer = new JsonRpcPeer(clientSide);
        _serverPeer = new JsonRpcPeer(serverSide);
        _clientPeer.Start();
        _serverPeer.Start();
        _world.OnCleanup(_clientPeer.DisposeAsync);
        _world.OnCleanup(_serverPeer.DisposeAsync);

        _client = Activator.CreateInstance(ClientType(), _clientPeer, null, JsonRpcParamStyle.ByName)!;
    }

    /// <summary>Wraps a sink in a delegate of the handler's parameter type, whatever its arity.</summary>
    private static Delegate Capture(Type delegateType, Action<object?[]> sink)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        var boxed = Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))));
        return Expression.Lambda(delegateType, Expression.Invoke(Expression.Constant(sink), boxed), parameters).Compile();
    }

    private void Register(string method, List<object?[]> target, TaskCompletionSource<object?[]>? completion = null)
    {
        EnsureClient();
        var registrar = ClientType().GetMethod(method) ?? throw new InvalidOperationException($"The generated client has no {method}.");
        var handlerType = registrar.GetParameters().Single().ParameterType;
        _handlerTypes = handlerType.GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray();

        var handler = Capture(handlerType, args =>
        {
            lock (target)
                target.Add(args);

            completion?.TrySetResult(args);
        });

        registrar.Invoke(_client, new object[] { handler });
    }

    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();

                foreach (var property in obj.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal))
                    result[property.Key] = Normalize(property.Value);

                return result;

            case JsonArray array:
                return new JsonArray(array.Select(Normalize).ToArray());

            default:
                return node?.DeepClone();
        }
    }

    // ---------------------------------------------------------------- steps

    [Given("^the Language Server Protocol contract$")]
    public void GivenLsp() => Load("lsp/lsp.openrpc.json", "LanguageServerClient", "Lsp",
        "lsp/basic.schema.json", "lsp/diagnostic.schema.json", "lsp/window.schema.json");

    [Given("^the Agent Client Protocol contract$")]
    public void GivenAcp() => Load("acp/acp.openrpc.json", "AgentClient", "Acp", "acp/schema.json");

    [When(@"^the generated client registers (On\w+)$")]
    public void WhenRegisters(string method) => Register(method, _first, _received);

    [When(@"^the generated client registers (On\w+) twice$")]
    public void WhenRegistersTwice(string method)
    {
        Register(method, _first);
        Register(method, _second, _received);
    }

    [When(@"^the server pushes ""(.*)"" with params (.*)$")]
    public async Task WhenPushes(string method, string paramsJson)
    {
        EnsureClient();
        await _serverPeer!.NotifyAsync(method, JsonRpcParams.FromJson(paramsJson));
    }

    [Then(@"^the handler receives (.*)$")]
    public async Task ThenReceives(string expectedJson)
    {
        var args = await _received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Serialize each argument as its declared type, so a union base writes its discriminator.
        var actual = new JsonArray(args.Select((arg, i) =>
            JsonSerializer.SerializeToNode(arg, _handlerTypes[i], JsonRpcSerializer.DefaultOptions)).ToArray());

        Assert.Equal(Normalize(JsonNode.Parse(expectedJson))!.ToJsonString(), Normalize(actual)!.ToJsonString());
    }

    [Then(@"^argument (\d+) is a ""(.*)""$")]
    public async Task ThenArgumentType(int position, string type)
    {
        var args = await _received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(type, args[position - 1]?.GetType().Name);
    }

    [Then("^only the second handler runs$")]
    public async Task ThenOnlySecond()
    {
        await _received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Empty(_first);
        Assert.Single(_second);
    }

    [Then(@"^the interface has ""(.*)"" and no ""(.*)""$")]
    public void ThenInterfaceHas(string present, string absent)
    {
        var members = Interface().GetMethods().Select(m => m.Name).ToArray();

        Assert.Contains(present, members);
        Assert.DoesNotContain(absent, members);
        Assert.Null(ClientType().GetMethod(absent));
    }

    [Then(@"^the interface has no member starting with ""(.*)""$")]
    public void ThenNoMemberStarting(string prefix) =>
        Assert.DoesNotContain(Interface().GetMethods(), m => m.Name.StartsWith(prefix, StringComparison.Ordinal));

    [Then("^the generated code compiles without warnings$")]
    public void ThenCompiles() => Assert.Empty(Build().Problems);

    [When(@"^the generated client calls SessionCancelAsync with ""(.*)""$")]
    public async Task WhenCancels(string sessionId)
    {
        EnsureClient();
        var method = Interface().GetMethod("SessionCancelAsync")!;

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.Values["notified"] = received;

        // The server side is a plain peer: record the notification it receives instead of dispatching it.
        _serverPeer!.OnNotification("session/cancel", e =>
            received.TrySetResult(e is { } p ? p.GetRawText() : "null"));

        await (Task)method.Invoke(_client, new object?[] { sessionId, CancellationToken.None })!;
    }

    [Then(@"^the server receives the notification ""(.*)"" for ""(.*)""$")]
    public async Task ThenServerReceives(string method, string sessionId)
    {
        var received = (TaskCompletionSource<string>)_world.Values["notified"]!;
        var raw = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A peer only runs notification handlers for messages without an id, so reaching it proves there was none.
        Assert.Equal("session/cancel", method);
        Assert.Equal(sessionId, JsonNode.Parse(raw)!["sessionId"]!.GetValue<string>());
    }
}
