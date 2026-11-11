using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.OpenRpc;
using JsonCall.Specs.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace JsonCall.Specs.Steps;

/// <summary>Steps for features 07 (document generation) and the OpenRPC scenario in 05.</summary>
[Binding]
public sealed class OpenRpcSteps
{
    private static readonly string[] NonDraft07Keywords =
    {
        "$defs", "prefixItems", "unevaluatedProperties", "unevaluatedItems", "dependentRequired", "dependentSchemas",
        "minContains", "maxContains", "$anchor", "$dynamicRef", "$recursiveRef", "contentSchema"
    };

    private readonly World _world;
    private JsonObject _document = new();
    private Type[] _contracts = { typeof(IMathApi) };
    private string? _lastMethod;
    private readonly List<Exception> _errors = new();
    private readonly List<Exception> _diagnostics = new();
    private JsonSerializerOptions? _serializerOptions;
    private JsonElement _discovered;
    private string? _publicUrl;

    public OpenRpcSteps(World world) => _world = world;

    // ---- generation ---------------------------------------------------

    private void Generate(params Type[] contracts)
    {
        _contracts = contracts;
        _errors.Clear();

        try
        {
            _document = new OpenRpcDocumentGenerator(new OpenRpcOptions { PublicBaseUrl = _publicUrl }, _serializerOptions)
                .Generate(contracts);
        }
        catch (InvalidOperationException ex)
        {
            _errors.Add(ex);
        }
    }

    private JsonObject Method(string name)
    {
        _lastMethod = name;

        return _document["methods"]!.AsArray().Select(m => m!.AsObject())
            .Single(m => (string)m["name"]! == name);
    }

    private JsonObject Param(string method, string name) =>
        Method(method)["params"]!.AsArray().Select(p => p!.AsObject()).Single(p => (string)p["name"]! == name);

    private static string Compact(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(Compact(JsonNode.Parse(expected)), Compact(actual));

    /// <summary>
    /// Compares ignoring "format" annotations the expectation does not mention
    /// (the feature file writes an integer as {"type":"integer"} in places where the generator adds "int32").
    /// </summary>
    private static void AssertJsonIgnoringFormat(string expected, JsonNode? actual) =>
        Assert.Equal(Compact(JsonNode.Parse(expected)), Compact(StripFormat(JsonNode.Parse(Compact(actual)), JsonNode.Parse(expected))));

    private static JsonNode? StripFormat(JsonNode? actual, JsonNode? expected)
    {
        if (actual is JsonObject a && expected is JsonObject e)
        {
            foreach (var key in a.Select(p => p.Key).ToList())
            {
                if (key == "format" && !e.ContainsKey("format"))
                    a.Remove(key);
                else if (e.ContainsKey(key))
                    StripFormat(a[key], e[key]);
            }
        }

        return actual;
    }

    // "Given the contract" is bound in ClientSteps; the OpenRPC reference contract is IMathApi in Support/OpenRpcContracts.cs.

    [Given("^the document is generated$")]
    [When("^the document is generated$")]
    public void WhenTheDocumentIsGenerated() => Generate(_contracts);

    [When("^the OpenRPC document is generated$")]
    public void WhenTheOpenRpcDocumentIsGenerated() => Generate(typeof(IEvents));

    // ---- root ---------------------------------------------------------

    [Then(@"^""openrpc"" is ""1\.4\.1"" or the library's declared supported version$")]
    public void ThenOpenRpcVersion()
    {
        var version = (string)_document["openrpc"]!;
        Assert.True(version is "1.4.1" || version == OpenRpcDocumentGenerator.SupportedVersion);
    }

    [Then(@"^""info\.title"" is ""(.*)"" and ""info\.version"" is ""(.*)""$")]
    public void ThenInfo(string title, string version)
    {
        Assert.Equal(title, (string)_document["info"]!["title"]!);
        Assert.Equal(version, (string)_document["info"]!["version"]!);
    }

    [Then(@"^""methods"" contains an entry for each contract method$")]
    public void ThenMethodsContainEach()
    {
        var expected = typeof(IMathApi).GetMethods().Length;
        Assert.Equal(expected, _document["methods"]!.AsArray().Count);
    }

    [Then(@"^the method ""(.*)"" has summary ""(.*)""$")]
    public void ThenSummary(string method, string summary) =>
        Assert.Equal(summary, (string)Method(method)["summary"]!);

    [Then(@"^its param ""(.*)"" has description ""(.*)""$")]
    public void ThenParamDescription(string param, string description) =>
        Assert.Equal(description, (string)Param(_lastMethod!, param)["description"]!);

    // ---- names --------------------------------------------------------

    [Given(@"^the naming policy is camelCase without the ""Async"" suffix$")]
    public void GivenNamingPolicy() => Generate(typeof(IMathApi));

    [Then("^the methods are named (.*)$")]
    public void ThenMethodsNamed(string list)
    {
        var expected = System.Text.RegularExpressions.Regex.Matches(list, "\"([^\"]*)\"").Select(m => m.Groups[1].Value);
        var actual = _document["methods"]!.AsArray().Select(m => (string)m!["name"]!);
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Given(@"^a method annotated \[JsonRpcMethod\(""(.*)""\)\]$")]
    public void GivenAnnotated(string name) => Generate(typeof(IRenamedApi));

    [Then(@"^the document method name is ""(.*)""$")]
    public void ThenMethodName(string name) =>
        Assert.Contains(_document["methods"]!.AsArray(), m => (string)m!["name"]! == name);

    // ---- params -------------------------------------------------------

    [Then(@"^""(.*)"" lists ""(.*)"" as required and ""(.*)"" as not required$")]
    public void ThenRequiredAndOptional(string method, string required, string optional)
    {
        Assert.True((bool)Param(method, required)["required"]!);
        Assert.False((bool)Param(method, optional)["required"]!);
    }

    [Then("^all required params come before optional params$")]
    public void ThenRequiredFirst()
    {
        foreach (var method in _document["methods"]!.AsArray())
        {
            var flags = method!["params"]!.AsArray().Select(p => (bool)p!["required"]!).ToList();
            Assert.Equal(flags.OrderByDescending(f => f), flags);
        }
    }

    [Then(@"^""(.*)"" has default ""(.*)"" in its schema$")]
    public void ThenDefault(string param, string value) =>
        Assert.Equal(value, (string)Param(_lastMethod!, param)["schema"]!["default"]!);

    [Then(@"^""(.*)"" param ""(.*)"" is not required$")]
    public void ThenParamNotRequired(string method, string param) =>
        Assert.False((bool)Param(method, param)["required"]!);

    [Given("^the contract is configured with params (.*)$")]
    public void GivenParamsSetting(string setting) =>
        Generate(setting switch
        {
            "by-name" => typeof(IByNameApi),
            "by-position" => typeof(IByPositionApi),
            "either" => typeof(IEitherApi),
            _ => throw new ArgumentException(setting)
        });

    [Then(@"^every method has paramStructure ""(.*)""$")]
    public void ThenParamStructure(string structure)
    {
        foreach (var method in _document["methods"]!.AsArray())
            Assert.Equal(structure, (string)method!["paramStructure"]!);
    }

    [Then(@"^""(.*)"" has no ""result"" member$")]
    public void ThenNoResult(string method) => Assert.False(Method(method).ContainsKey("result"));

    // ---- schemas ------------------------------------------------------

    [Then(@"^the schema of ""(.*)"" param ""(.*)"" is (\{.*\})$")]
    public void ThenParamSchema(string method, string param, string schema) =>
        AssertJson(schema, Param(method, param)["schema"]);

    [Then(@"^the schema of ""(.*)"" result is (\{.*\})$")]
    public void ThenResultSchema(string method, string schema) =>
        AssertJson(schema, Method(method)["result"]!["schema"]);

    [Then(@"^""components\.schemas\.(.*)"" exists$")]
    public void ThenComponentExists(string name) =>
        Assert.NotNull(_document["components"]!["schemas"]![name]);

    [Then(@"^""(.*)"" result schema is (\{.*\})$")]
    public void ThenResultSchemaIs(string method, string schema) =>
        AssertJson(schema, Method(method)["result"]!["schema"]);

    [Then(@"^""(.*)"" result schema is an array of (\{.*\})$")]
    public void ThenResultArrayOf(string method, string items)
    {
        var schema = Method(method)["result"]!["schema"]!;
        Assert.Equal("array", (string)schema["type"]!);
        AssertJson(items, schema["items"]);
    }

    [Given("^a type Node with a property Children of type List<Node>$")]
    public void GivenNode() => Generate(typeof(INodeApi));

    [Then(@"^the Node schema references itself with \$ref and generation terminates$")]
    public void ThenNodeRecursive()
    {
        var node = _document["components"]!["schemas"]!["Node"]!;
        AssertJson("""{"type":"array","items":{"$ref":"#/components/schemas/Node"}}""", node["properties"]!["children"]);
    }

    [Then("^no schema uses a keyword that is not valid in Draft 07$")]
    public void ThenDraft07Only()
    {
        var offenders = new List<string>();
        Walk(_document, "", offenders);
        Assert.Empty(offenders);

        // Every hoisted schema must also be a valid Draft 07 schema.
        foreach (var (name, schema) in _document["components"]!["schemas"]!.AsObject())
        {
            var result = MetaSchemas.Draft7.Evaluate(schema, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, $"components.schemas.{name} is not a valid Draft 07 schema");
        }
    }

    private static void Walk(JsonNode? node, string path, List<string> offenders)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (NonDraft07Keywords.Contains(key))
                        offenders.Add(path + "/" + key);

                    Walk(value, path + "/" + key, offenders);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    Walk(array[i], path + "/" + i, offenders);

                break;
        }
    }

    [Then(@"^the document declares ""\$schema"" for Draft 07 where schemas are embedded$")]
    public void ThenDeclaresSchema()
    {
        foreach (var (name, schema) in _document["components"]!["schemas"]!.AsObject())
            Assert.Equal(SchemaGenerator.Draft07, (string)schema!["$schema"]!);
    }

    [Given("^a parameter of type (.*)$")]
    public void GivenParameterOfType(string type)
    {
        Generate(typeof(ITypeZoo));
        _world.Values["zoo"] = type;
    }

    [Then("^its schema is (.*)$")]
    public void ThenItsSchemaIs(string schema) =>
        AssertJsonIgnoringFormat(schema, Param((string)_world.Values["zoo"]!, "value")["schema"]);

    [Given("^the JSON naming policy is camelCase$")]
    public void GivenCamelCase()
    {
        _serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        Generate(typeof(IMathApi));
    }

    [Then("^property names in schemas are camelCase$")]
    public void ThenCamelCase()
    {
        var properties = _document["components"]!["schemas"]!["Person"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "name", "age" }, properties.Select(p => p.Key));

        // A different policy changes the schema, proving the option is honoured.
        var pascal = new OpenRpcDocumentGenerator(serializerOptions: new JsonSerializerOptions { PropertyNamingPolicy = null })
            .Generate(typeof(IMathApi));

        Assert.Equal(new[] { "Name", "Age" },
            pascal["components"]!["schemas"]!["Person"]!["properties"]!.AsObject().Select(p => p.Key));
    }

    // ---- deprecation, errors, tags -----------------------------------

    [Then(@"^""(.*)"" has ""deprecated"": true$")]
    public void ThenDeprecated(string method) => Assert.True((bool)Method(method)["deprecated"]!);

    [Then(@"^its description includes the obsolete message ""(.*)""$")]
    public void ThenObsoleteMessage(string message) =>
        Assert.Contains(message, (string)Method(_lastMethod!)["description"]!);

    [Given(@"^""getPerson"" is annotated \[JsonRpcError\(404, ""Person not found""\)\]$")]
    public void GivenErrorAnnotated() => Generate(typeof(IErrorsApi));

    [Then(@"^""(.*)"" has an error with code (\d+) and message ""(.*)""$")]
    public void ThenError(string method, int code, string message)
    {
        var error = Method(method)["errors"]!.AsArray().Single()!;
        Assert.Equal(code, (int)error["code"]!);
        Assert.Equal(message, (string)error["message"]!);
    }

    [Given(@"^""getPerson"" declares errors with codes 404 and 404, or an error with code -32010$")]
    public void GivenBadErrors()
    {
        var generator = new OpenRpcDocumentGenerator();

        foreach (var contract in new[] { typeof(IDuplicateErrorsApi), typeof(IReservedErrorApi) })
        {
            try
            {
                generator.Generate(contract);
            }
            catch (InvalidOperationException ex)
            {
                _diagnostics.Add(ex);
            }
        }
    }

    [Then("^generation fails with a diagnostic naming the method and code$")]
    public void ThenGenerationFails()
    {
        Assert.Equal(2, _diagnostics.Count);
        Assert.Contains("getPerson", _diagnostics[0].Message);
        Assert.Contains("404", _diagnostics[0].Message);
        Assert.Contains("getPerson", _diagnostics[1].Message);
        Assert.Contains("-32010", _diagnostics[1].Message);
    }

    [Given(@"^the contract is annotated \[JsonRpcTag\(""math""\)\]$")]
    public void GivenTagged() => Generate(typeof(ITaggedApi));

    [Then(@"^every method in it has the tag ""math""$")]
    public void ThenTagged()
    {
        Assert.NotEmpty(_document["methods"]!.AsArray());

        foreach (var method in _document["methods"]!.AsArray())
            Assert.Equal("math", (string)method!["tags"]![0]!["name"]!);
    }

    // ---- streaming ---------------------------------------------------

    [Given("^a contract method returning IAsyncEnumerable<Tick>$")]
    public void GivenStreamingMethod() => Generate(typeof(IEvents));

    [Then(@"^the method has ""x-jsoncall-stream"": true$")]
    public void ThenMethodStream() => Assert.True((bool)Method("ticks")["x-jsoncall-stream"]!);

    [Then(@"^the TicksAsync method has ""x-jsoncall-stream"": true$")]
    public void ThenTicksStream() => ThenMethodStream();

    [Then("^its result schema is the Tick schema$")]
    [Then("^its result schema describes a single Tick item$")]
    public void ThenTickSchema()
    {
        AssertJson("""{"$ref":"#/components/schemas/Tick"}""", Method("ticks")["result"]!["schema"]);
        Assert.NotNull(_document["components"]!["schemas"]!["Tick"]);
    }

    // ---- hosting and discovery ---------------------------------------

    private HostSpec? _host;

    [Given(@"^the app maps app\.MapJsonRpc\(""/rpc""\) and is configured with its public base URL$")]
    public async Task GivenPublicUrl()
    {
        _publicUrl = "https://api.example.test/rpc";
        _world.State.ConfigureServer = o => o.AddOpenRpc(r => r.PublicBaseUrl = _publicUrl);
        await StartHostAsync();
    }

    [Then(@"^""servers"" contains an entry for that URL$")]
    public async Task ThenServers()
    {
        var document = await DiscoverOverHttpAsync();
        Assert.Contains(document.GetProperty("servers").EnumerateArray(),
            s => s.GetProperty("url").GetString() == _publicUrl);
    }

    [Given("^an ASP\\.NET Core host with the OpenRPC module enabled$")]
    public async Task GivenOpenRpcHost()
    {
        _world.State.ConfigureServer = o => o.AddOpenRpc();
        await StartHostAsync();
    }

    private async Task StartHostAsync()
    {
        _host = new HostSpec { DefaultContracts = true };
        await _host.StartAsync(_world.State);
    }

    private async Task<JsonElement> DiscoverOverHttpAsync()
    {
        using var http = new HttpClient { BaseAddress = _host!.Host!.BaseAddress };
        using var response = await http.PostAsync("/rpc",
            new StringContent("""{"jsonrpc":"2.0","method":"rpc.discover","id":1}""", Encoding.UTF8, "application/json"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("result").Clone();
    }

    [When(@"^a client calls ""rpc\.discover""$")]
    public async Task WhenClientCallsDiscover()
    {
        _discovered = await DiscoverOverHttpAsync();
        _world.Values["discoveredAgain"] = await DiscoverOverHttpAsync();
    }

    [Then("^the result is the generated document$")]
    public void ThenResultIsDocument()
    {
        var host = _host!.Host!;
        var contracts = host.App.Services.GetRequiredService<JsonCall.Server.JsonRpcServerOptions>().Contracts
            .Select(c => c.Contract);

        var expected = new OpenRpcDocumentGenerator().Generate(contracts);

        Assert.Equal(OpenRpcDocumentGenerator.ToJson(expected), JsonSerializer.Serialize(_discovered, new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

        Assert.NotNull(host);
    }

    [Then("^repeated calls return an identical cached document$")]
    public void ThenCached() =>
        Assert.Equal(_discovered.GetRawText(), ((JsonElement)_world.Values["discoveredAgain"]!).GetRawText());

    [Given("^the client is connected over (http|websocket|tcp)$")]
    public async Task GivenConnectedOver(string transport)
    {
        _world.State.ConfigureServer = o => o.AddOpenRpc();
        _world.Connection = await Connections.ConnectAsync(transport, _world.State);
    }

    [When(@"^it calls ""rpc\.discover""$")]
    public async Task WhenItCallsDiscover() =>
        _discovered = await _world.Connection!.Client.InvokeAsync("rpc.discover");

    [Then("^it receives the document$")]
    public void ThenReceivesDocument()
    {
        Assert.Equal(OpenRpcDocumentGenerator.SupportedVersion, _discovered.GetProperty("openrpc").GetString());
        Assert.Contains(_discovered.GetProperty("methods").EnumerateArray(),
            m => m.GetProperty("name").GetString() == "add");
    }

    // ---- validation and golden file -----------------------------------

    [When(@"^the document is validated against the OpenRPC 1\.4 meta-schema$")]
    public void WhenValidated()
    {
        // The newest published meta-schema is open-rpc/meta-schema 1.14.9 (OpenRPC spec 1.3.x); see docs/PRD.md.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Support", "Data", "openrpc-meta-schema-1.4.json"));

        // The released schema points at meta.json-schema.tools for JSON Schema; use the Draft 07 meta-schema instead.
        text = text
            .Replace("\"$schema\": \"https://meta.json-schema.tools/\"", "\"$schema\": \"http://json-schema.org/draft-07/schema#\"")
            .Replace("\"$ref\": \"https://meta.json-schema.tools\"", "\"$ref\": \"http://json-schema.org/draft-07/schema#\"")
            .Replace("\"$ref\": \"https://meta.json-schema.tools/#/definitions/JSONSchemaObject/properties/$ref\"", "\"type\": \"string\"");

        var schema = JsonSchema.FromText(text);

        var documents = new[] { typeof(IMathApi), typeof(IEvents), typeof(ITypeZoo), typeof(ITaggedApi), typeof(IErrorsApi), typeof(INodeApi) };
        var failures = new List<string>();

        foreach (var contract in documents)
        {
            var document = new OpenRpcDocumentGenerator(new OpenRpcOptions { PublicBaseUrl = "https://x.test/rpc" }).Generate(contract);
            var result = schema.Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });

            if (!result.IsValid)
            {
                failures.AddRange(result.Details.Where(d => d.Errors is not null)
                    .SelectMany(d => d.Errors!.Select(e => $"{contract.Name} {d.InstanceLocation}: {e.Value}")));
            }
        }

        _world.Values["validationErrors"] = failures;
    }

    [Then("^there are no validation errors$")]
    public void ThenNoValidationErrors() =>
        Assert.Empty((List<string>)_world.Values["validationErrors"]!);

    [When("^the document for the reference contract is generated$")]
    public void WhenReferenceGenerated() => Generate(typeof(IMathApi));

    [Then(@"^it matches the checked-in golden ""math\.openrpc\.json"" byte for byte$")]
    public void ThenGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Support", "Data", "math.openrpc.json");
        var actual = Encoding.UTF8.GetBytes(OpenRpcDocumentGenerator.ToJson(_document));

        // Set JSONCALL_UPDATE_GOLDEN=1 to rewrite the checked-in file after an intentional change, then review the diff.
        if (Environment.GetEnvironmentVariable("JSONCALL_UPDATE_GOLDEN") == "1")
            File.WriteAllBytes(Path.Combine(SourceDataDirectory(), "math.openrpc.json"), actual);

        Assert.Equal(File.ReadAllBytes(path), actual);
    }

    private static string SourceDataDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JsonCall.slnx")))
            directory = directory.Parent;

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "test", "JsonCall.Specs", "Support", "Data");
    }

    // ---- protected discovery ------------------------------------------

    [Given("^discovery is configured to require authorization$")]
    public async Task GivenProtectedDiscovery()
    {
        _world.State.ConfigureServer = o => o.AddOpenRpc(r => r.Metadata.Add(new AuthorizeAttribute()));
        var host = await TestHost.StartAuthAsync(_world.State);
        _world.OnCleanup(host.DisposeAsync);
        _world.Values["authHost"] = host;
    }

    [When(@"^an anonymous client calls ""rpc\.discover""$")]
    public async Task WhenAnonymousDiscover()
    {
        var host = (TestHost)_world.Values["authHost"]!;
        using var http = new HttpClient { BaseAddress = host.BaseAddress };
        using var response = await http.PostAsync("/rpc",
            new StringContent("""{"jsonrpc":"2.0","method":"rpc.discover","id":1}""", Encoding.UTF8, "application/json"));

        _world.Values["authResponse"] = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Then("^it receives an authorization error$")]
    public void ThenAuthorizationError()
    {
        var response = (JsonElement)_world.Values["authResponse"]!;
        Assert.Equal(JsonRpcErrorCodes.Unauthorized, response.GetProperty("error").GetProperty("code").GetInt32());
    }
}
