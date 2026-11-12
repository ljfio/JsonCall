using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Generator;
using JsonCall.OpenRpc;
using JsonCall.Specs.Support;
using Microsoft.CodeAnalysis;
using Reqnroll;

namespace JsonCall.Specs.Steps;

/// <summary>Steps for feature 08: typed client generation from OpenRPC documents.</summary>
[Binding]
public sealed class GeneratorSteps
{
    private readonly World _world;
    private string _json = ApiDocument();
    private string _path = "api.openrpc.json";
    private readonly Dictionary<string, string> _metadata = new();
    private BuildResult? _build;
    private readonly Dictionary<string, string> _siblings = new();

    public GeneratorSteps(World world) => _world = world;

    // ---- fixtures -----------------------------------------------------

    private const string PersonSchema = """
        {"type":"object","required":["id","name"],"properties":{"id":{"type":"string"},"name":{"type":"string"},"nickname":{"type":"string"}}}
        """;

    private static string ApiDocument(string? extraMethods = null, string? extraSchemas = null) => $$"""
        {
          "openrpc": "1.2.6",
          "info": { "title": "Test API", "version": "1.0.0" },
          "methods": [
            { "name": "add", "params": [
                { "name": "a", "required": true, "schema": { "type": "integer", "format": "int32" } },
                { "name": "b", "required": true, "schema": { "type": "integer", "format": "int32" } } ],
              "result": { "name": "result", "schema": { "type": "integer", "format": "int32" } } },
            { "name": "getPerson", "params": [
                { "name": "id", "required": true, "schema": { "type": "integer", "format": "int32" } } ],
              "result": { "name": "result", "schema": { "$ref": "#/components/schemas/Person" } } }
            {{(extraMethods is null ? "" : "," + extraMethods)}}
          ],
          "components": { "schemas": { "Person": {{PersonSchema}} {{(extraSchemas is null ? "" : "," + extraSchemas)}} } }
        }
        """;

    /// <summary>A document with one method, "probe", that has the given params and result schema.</summary>
    private static string Probe(string? resultSchema, string schemas = "", string paramsJson = "[]", string extraMethodFields = "") => $$"""
        {
          "openrpc": "1.2.6",
          "info": { "title": "Probe", "version": "1.0.0" },
          "methods": [
            { "name": "probe", "params": {{paramsJson}} {{(resultSchema is null ? "" : $", \"result\": {{ \"name\": \"result\", \"schema\": {resultSchema} }}")}} {{extraMethodFields}} }
          ],
          "components": { "schemas": { {{schemas}} } }
        }
        """;

    private BuildResult Build() =>
        _build ??= GeneratorHarness.Build(_json, _path, _metadata, siblings: _siblings);

    private Type Contract(string? name = null) => Build().Type(name ?? "I" + (_metadata.GetValueOrDefault("ClientName") ?? "ApiClient"));

    private MethodInfo ProbeMethod() => Contract().GetMethods().Single(m => m.Name == "ProbeAsync");

    private void Use(string json, string? path = null)
    {
        _json = json;
        _build = null;

        if (path is not null)
            _path = path;
    }

    // ---- background and build ------------------------------------------

    [Given("^a project referencing the JsonCall client generator$")]
    public void GivenProject() { }

    [Given(@"^""api\.openrpc\.json"" is included as an AdditionalFile$")]
    public void GivenAdditionalFile() => _path = "api.openrpc.json";

    [When("^the project is built$")]
    [When("^the client is generated$")]
    public void WhenBuilt() => Build();

    // ---- FR-GEN-01 ----------------------------------------------------

    [Given(@"^the document defines the methods ""add"", ""getPerson"" and the schema ""Person""$")]
    public void GivenDefinesMethods() => Use(ApiDocument());

    [Then(@"^a record ""([^""]*)"" is generated$")]
    public void ThenRecordGenerated(string name)
    {
        var type = Build().Type(name);
        Assert.NotNull(type.GetMethod("<Clone>$"));
    }

    [Then(@"^an interface ""IApiClient"" with AddAsync and GetPersonAsync is generated$")]
    public void ThenInterfaceGenerated()
    {
        var contract = Contract("IApiClient");
        Assert.True(contract.IsInterface);
        Assert.NotNull(contract.GetMethod("AddAsync"));
        Assert.NotNull(contract.GetMethod("GetPersonAsync"));
    }

    [Then(@"^a class ""ApiClient"" implementing it over JsonRpcClient is generated$")]
    public void ThenClientGenerated()
    {
        var client = Build().Type("ApiClient");
        Assert.True(Contract("IApiClient").IsAssignableFrom(client));
        Assert.Contains(client.GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(JsonRpcPeer));
        Assert.True(typeof(JsonRpcPeer).IsAssignableFrom(typeof(JsonRpcClient)));
    }

    [Then("^the project compiles without warnings$")]
    public void ThenCompilesWithoutWarnings() => Assert.Empty(Build().Problems.Select(d => d.ToString()));

    [Given(@"^the AdditionalFile metadata sets Namespace ""(.*)"" and ClientName ""(.*)""$")]
    public void GivenMetadata(string ns, string clientName)
    {
        _metadata["Namespace"] = ns;
        _metadata["ClientName"] = clientName;
        _build = null;
    }

    [Then(@"^the types are generated in ""(.*)"" with the client named ""(.*)""$")]
    public void ThenNamespaceAndName(string ns, string clientName)
    {
        Assert.Equal(ns, Build().Namespace);
        Assert.NotNull(Build().Type(clientName));
        Assert.NotNull(Build().Type("I" + clientName));
        Assert.Equal(ns, Build().Type("Person").Namespace);
    }

    // ---- third-party documents ----------------------------------------

    [Given(@"^the published OpenRPC document ""(.*)""$")]
    public void GivenPublishedDocument(string document)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Support", "Data", document);

        // The generator picks up files named *.openrpc.json, so give the downloaded file such a name.
        Use(File.ReadAllText(path), Path.GetFileNameWithoutExtension(document) + ".openrpc.json");
    }

    [Then("^the generated code compiles with no errors$")]
    public void ThenCompilesWithNoErrors()
    {
        var build = Build();
        Assert.NotEmpty(build.Sources);

        var errors = build.GeneratorDiagnostics.Concat(build.CompileDiagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id + " " + d.GetMessage() + " @ " + d.Location.SourceTree?.GetText().Lines[d.Location.GetLineSpan().StartLinePosition.Line].ToString().Trim())
            .Distinct().Take(6).ToList();

        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.NotNull(build.Assembly);
    }

    // ---- naming -------------------------------------------------------

    [Given(@"^the document method name ""wallet\.get_balance""$")]
    public void GivenDottedName() => Use(Probe("""{"type":"string"}""").Replace("\"probe\"", "\"wallet.get_balance\""));

    [Then(@"^the generated method is ""GetBalanceAsync"" in a ""Wallet"" group or with the original name preserved in an attribute$")]
    public void ThenGeneratedMethodName()
    {
        var method = Contract().GetMethod("GetBalanceAsync");
        Assert.NotNull(method);
        Assert.Equal("wallet.get_balance", method!.GetCustomAttribute<JsonRpcMethodAttribute>()!.Name);
    }

    [Then(@"^the wire method name is still ""(.*)""$")]
    public async Task ThenWireName(string name)
    {
        // Call the generated client and look at what goes over the wire.
        var (client, recording) = Loopback(_ => { });
        var generated = Activator.CreateInstance(Build().Type("ApiClient"), client, null, JsonRpcParamStyle.ByPosition)!;
        var task = (Task)generated.GetType().GetMethod("GetBalanceAsync")!.Invoke(generated, new object?[] { CancellationToken.None })!;

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The loopback has no such method; only the request matters.
        }

        Assert.Contains(recording.SentJson, j => JsonDocument.Parse(j).RootElement.GetProperty("method").GetString() == name);
    }

    // ---- schema mapping -------------------------------------------------

    private string? _construct;

    [Given("^a schema using (.*)$")]
    public void GivenSchemaUsing(string construct)
    {
        _construct = construct;

        if (construct.Contains("patternProperties"))
        {
            Use(Probe("""{"type":"object","patternProperties":{"^x-":{"type":"string"}}}"""));
            return;
        }

        var (schema, schemas) = ConstructSchema(construct);
        Use(Probe(schema, schemas));
    }

    private static (string Schema, string Schemas) ConstructSchema(string construct) => construct switch
    {
        "type string" => ("""{"type":"string"}""", ""),
        "type integer" => ("""{"type":"integer"}""", ""),
        "type number" => ("""{"type":"number"}""", ""),
        "type boolean" => ("""{"type":"boolean"}""", ""),
        "type array of string" => ("""{"type":"array","items":{"type":"string"}}""", ""),
        "type object with properties" => ("""{"$ref":"#/components/schemas/Box"}""",
            """ "Box": {"type":"object","required":["width"],"properties":{"width":{"type":"integer","format":"int32"},"label":{"type":"string"}}} """),
        "additionalProperties of integer" => ("""{"type":"object","additionalProperties":{"type":"integer","format":"int32"}}""", ""),
        "enum of strings" => ("""{"$ref":"#/components/schemas/Mood"}""",
            """ "Mood": {"type":"string","enum":["happy","very-sad"]} """),
        "type [\"string\",\"null\"]" => ("""{"type":["string","null"]}""", ""),
        "format date-time" => ("""{"type":"string","format":"date-time"}""", ""),
        "format uuid" => ("""{"type":"string","format":"uuid"}""", ""),
        _ => throw new ArgumentException("Unknown construct " + construct)
    };

    [Then("^the C# type is (.*)$")]
    public void ThenCSharpType(string expected)
    {
        var build = Build();
        var returns = ProbeMethod().ReturnType;
        var generic = returns.GetGenericArguments()[0];

        switch (_construct)
        {
            case "type string": Assert.Equal(typeof(string), generic); break;
            case "type integer": Assert.Equal(typeof(long), generic); break; // int32 would give int; see the next case
            case "type number": Assert.Equal(typeof(double), generic); break;
            case "type boolean": Assert.Equal(typeof(bool), generic); break;
            case "type array of string": Assert.Equal(typeof(IReadOnlyList<string>), generic); break;
            case "additionalProperties of integer": Assert.Equal(typeof(IReadOnlyDictionary<string, int>), generic); break;
            case "format date-time": Assert.Equal(typeof(DateTimeOffset), generic); break;
            case "format uuid": Assert.Equal(typeof(Guid), generic); break;
            case "type object with properties":
            {
                Assert.Equal("Box", generic.Name);
                var properties = generic.GetProperties().Select(p => p.Name).Order().ToArray();
                Assert.Equal(new[] { "Label", "Width" }, properties);
                break;
            }

            case "enum of strings":
            {
                Assert.True(generic.IsEnum);
                Assert.Equal(new[] { "Happy", "VerySad" }, Enum.GetNames(generic));

                // The JSON names come from the document, not from the C# member names.
                var json = JsonSerializer.Serialize(Enum.Parse(generic, "VerySad"));
                Assert.Equal("\"very-sad\"", json);
                break;
            }

            default:
                // type ["string","null"]: string? (a nullable reference type shows in the generated source).
                Assert.Contains("Task<string?> ProbeAsync(", build.AllSource);
                break;
        }
    }

    [Then(@"^""Id"" is non-nullable and ""Nickname"" is nullable$")]
    public void ThenNullability()
    {
        var person = Build().Type("Person");
        var context = new NullabilityInfoContext();

        Assert.Equal(NullabilityState.NotNull, context.Create(person.GetProperty("Id")!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(person.GetProperty("Nickname")!).ReadState);
    }

    [Given(@"^an object schema with required \[""id""\] and optional property ""nickname""$")]
    public void GivenRequiredSchema() => Use(ApiDocument());

    [Given(@"^a method result of \{""\$ref"":""#/components/schemas/Person""\}$")]
    public void GivenRefResult() => Use(Probe("""{"$ref":"#/components/schemas/Person"}""", " \"Person\": " + PersonSchema));

    [Then("^the generated return type is Person$")]
    public void ThenReturnTypePerson() => Assert.Equal(Build().Type("Person"), ProbeMethod().ReturnType.GetGenericArguments()[0]);

    [Given(@"^schema ""Node"" with property ""children"" as an array of \$ref Node$")]
    public void GivenNodeSchema() => Use(Probe("""{"$ref":"#/components/schemas/Node"}""", """
        "Node": {"type":"object","required":["name","children"],"properties":{"name":{"type":"string"},"children":{"type":"array","items":{"$ref":"#/components/schemas/Node"}}}}
        """));

    [Then("^it compiles and round-trips a three-level tree$")]
    public void ThenTreeRoundTrips()
    {
        const string tree = """{"name":"root","children":[{"name":"a","children":[{"name":"leaf","children":[]}]},{"name":"b","children":[]}]}""";
        var node = Build().Type("Node");
        var value = JsonSerializer.Deserialize(tree, node, JsonRpcSerializer.DefaultOptions);

        Assert.NotNull(value);
        Assert.Equal(tree, JsonSerializer.Serialize(value, node, JsonRpcSerializer.DefaultOptions));
    }

    [Given(@"^schema ""Employee"" is allOf \[\$ref Person, \{properties: \{salary\}\}\]$")]
    public void GivenAllOf() => Use(Probe("""{"$ref":"#/components/schemas/Employee"}""",
        " \"Person\": " + PersonSchema + """
        , "Employee": {"allOf":[{"$ref":"#/components/schemas/Person"},{"type":"object","required":["salary"],"properties":{"salary":{"type":"integer","format":"int32"}}}]}
        """));

    [Then(@"^Employee exposes the members of Person and ""Salary""$")]
    public void ThenEmployeeMembers()
    {
        var names = Build().Type("Employee").GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "Id", "Name", "Nickname", "Salary" }, names);
    }

    private const string ShapeSchemas = """
        "Circle": {"type":"object","required":["kind","radius"],"properties":{"kind":{"const":"circle"},"radius":{"type":"number"}}},
        "Square": {"type":"object","required":["kind","side"],"properties":{"kind":{"const":"square"},"side":{"type":"number"}}},
        "Shape": {"oneOf":[{"$ref":"#/components/schemas/Circle"},{"$ref":"#/components/schemas/Square"}]}
        """;

    [Given(@"^a oneOf schema whose variants share a ""kind"" const property$")]
    public void GivenDiscriminated() => Use(Probe("""{"$ref":"#/components/schemas/Shape"}""", ShapeSchemas));

    [Then("^an abstract base type with one subtype per variant is generated$")]
    public void ThenPolymorphicBase()
    {
        var shape = Build().Type("Shape");
        Assert.True(shape.IsAbstract);
        Assert.Equal(shape, Build().Type("Circle").BaseType);
        Assert.Equal(shape, Build().Type("Square").BaseType);
    }

    [Then(@"^deserialization selects the subtype by ""kind""$")]
    public void ThenSubtypeByKind()
    {
        var shape = Build().Type("Shape");
        var circle = JsonSerializer.Deserialize("""{"kind":"circle","radius":2.5}""", shape, JsonRpcSerializer.DefaultOptions);
        var square = JsonSerializer.Deserialize("""{"kind":"square","side":4}""", shape, JsonRpcSerializer.DefaultOptions);

        Assert.Equal("Circle", circle!.GetType().Name);
        Assert.Equal("Square", square!.GetType().Name);
        Assert.Equal("""{"kind":"circle","radius":2.5}""", JsonSerializer.Serialize(circle, shape, JsonRpcSerializer.DefaultOptions));
    }

    [Given("^a oneOf of string and integer$")]
    public void GivenUnion() => Use(Probe("""{"oneOf":[{"type":"string"},{"type":"integer","format":"int32"}]}"""));

    [Then("^a union wrapper exposing the matched variant is generated$")]
    public void ThenUnionWrapper()
    {
        var union = ProbeMethod().ReturnType.GetGenericArguments()[0];
        Assert.NotEqual(typeof(string), union);

        var five = JsonSerializer.Deserialize("5", union, JsonRpcSerializer.DefaultOptions)!;
        var text = JsonSerializer.Deserialize("\"five\"", union, JsonRpcSerializer.DefaultOptions)!;

        Assert.Equal(1, union.GetProperty("Index")!.GetValue(five));
        Assert.Equal(5, union.GetProperty("Value")!.GetValue(five));
        Assert.Equal(0, union.GetProperty("Index")!.GetValue(text));
        Assert.Equal("five", union.GetProperty("Value")!.GetValue(text));
        Assert.Equal("5", JsonSerializer.Serialize(five, union, JsonRpcSerializer.DefaultOptions));
    }

    [Then("^a diagnostic of severity Info explains the fallback$")]
    public void ThenInfoDiagnostic() =>
        Assert.Contains(Build().GeneratorDiagnostics, d => d.Severity == DiagnosticSeverity.Info && d.Id == "JSONCALL004");

    [Then("^a warning with the JSON path is reported$")]
    public void ThenWarningWithPath()
    {
        var warning = Assert.Single(Build().GeneratorDiagnostics, d => d.Id == "JSONCALL002");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("methods[0].result.schema.patternProperties", warning.GetMessage());
    }

    [Then("^the member falls back to JsonElement$")]
    public void ThenFallsBack() => Assert.Equal(typeof(JsonElement), ProbeMethod().ReturnType.GetGenericArguments()[0]);

    [Given(@"^a param schema of true$")]
    public void GivenBooleanSchema() => Use(Probe("""{"type":"string"}""", paramsJson: """[{"name":"anything","required":true,"schema":true}]"""));

    [Then("^the generated parameter type is JsonElement$")]
    public void ThenParameterJsonElement() =>
        Assert.Equal(typeof(JsonElement), ProbeMethod().GetParameters().Single(p => p.Name == "anything").ParameterType);

    // ---- parameters and wire shape ------------------------------------

    /// <summary>A client over an in-memory server whose recording shows what was sent.</summary>
    private (JsonRpcClient Client, RecordingTransport Recording) Loopback(Action<JsonCall.Server.JsonRpcServerOptions> configure)
    {
        var (clientSide, serverSide) = InMemoryTransport.CreatePair();
        var options = new JsonCall.Server.JsonRpcServerOptions();
        configure(options);
        var server = new JsonCall.Server.JsonRpcServer(options);
        var peer = server.Attach(serverSide);
        var recording = new RecordingTransport(clientSide);
        var client = new JsonRpcClient(recording);

        _world.OnCleanup(async () =>
        {
            await client.DisposeAsync();
            await peer.DisposeAsync();
        });

        return (client, recording);
    }

    private const string GreetDocument = """
        {"openrpc":"1.2.6","info":{"title":"g","version":"1"},"methods":[
          {"name":"greet","params":[
            {"name":"name","required":true,"schema":{"type":"string"}},
            {"name":"greeting","required":false,"schema":{"type":"string"}}],
           "result":{"name":"result","schema":{"type":"string"}} __STRUCTURE__ }]}
        """;

    private string GreetWith(string structure) =>
        GreetDocument.Replace("__STRUCTURE__", structure.Length == 0 ? "" : $", \"paramStructure\": \"{structure}\"");

    private static void AddGreet(JsonCall.Server.JsonRpcServerOptions options) =>
        options.AddMethod("greet", (string name, string? greeting) => $"{greeting ?? "Hello"} {name}");

    private async Task<string> CallGreetAsync(string? greeting, JsonRpcParamStyle style = JsonRpcParamStyle.ByPosition)
    {
        var (client, recording) = Loopback(AddGreet);
        var generated = Activator.CreateInstance(Build().Type("ApiClient"), client, null, style)!;
        var method = Contract().GetMethod("GreetAsync")!;
        var task = (Task)method.Invoke(generated, new object?[] { "Ada", greeting, CancellationToken.None })!;
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        var request = JsonDocument.Parse(recording.SentJson.First()).RootElement;
        return request.GetProperty("params").GetRawText();
    }

    [Given(@"^the document param ""greeting"" is not required$")]
    public void GivenOptionalParam() => Use(GreetWith(""));

    [Then(@"^the generated method has ""string\? greeting = null""$")]
    public void ThenOptionalSignature() => Assert.Contains("string? greeting = null", Build().AllSource);

    [Then(@"^a call omitting it sends params without ""greeting""$")]
    public async Task ThenOmittedParam()
    {
        Assert.Equal("""["Ada"]""", await CallGreetAsync(null));
        Assert.Equal("""{"name":"Ada"}""", await CallGreetAsync(null, JsonRpcParamStyle.ByName));
        Assert.Equal("""["Ada","Hi"]""", await CallGreetAsync("Hi"));
    }

    private string _structure = "";

    [Given(@"^the method declares paramStructure ""(.*)""$")]
    public void GivenStructure(string structure)
    {
        _structure = structure;
        Use(GreetWith(structure));
    }

    [When("^the generated client calls it$")]
    public async Task WhenGeneratedClientCalls() => _world.Values["params"] = await CallGreetAsync("Hi");

    [Then("^the wire params are (.*)$")]
    public void ThenWireParams(string shape)
    {
        var raw = (string)_world.Values["params"]!;

        switch (shape)
        {
            case "an object": Assert.StartsWith("{", raw); break;
            case "an array": Assert.StartsWith("[", raw); break;
            default: Assert.StartsWith("[", raw); break; // per client default: by position
        }
    }

    [Given("^a method with no result$")]
    public void GivenNoResult() => Use(Probe(null, paramsJson: """[{"name":"message","required":true,"schema":{"type":"string"}}]"""));

    [Then("^the generated method sends a notification and returns a Task$")]
    public async Task ThenNotification()
    {
        var method = ProbeMethod();
        Assert.Equal(typeof(Task), method.ReturnType);

        var received = new TaskCompletionSource<string>();
        var (client, recording) = Loopback(o => o.AddMethod("probe", (string message) => received.TrySetResult(message)));
        var generated = Activator.CreateInstance(Build().Type("ApiClient"), client, null, JsonRpcParamStyle.ByPosition)!;

        await ((Task)method.Invoke(generated, new object?[] { "ping", CancellationToken.None })!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ping", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var sent = JsonDocument.Parse(recording.SentJson.First()).RootElement;
        Assert.False(sent.TryGetProperty("id", out _));
    }

    [Given("^the method documents an error with code 404$")]
    public void GivenDocumentedError() => Use("""
        {"openrpc":"1.2.6","info":{"title":"e","version":"1"},"methods":[
          {"name":"probe","params":[],"result":{"name":"result","schema":{"type":"string"}},
           "errors":[{"code":404,"message":"Not found"}]}]}
        """);

    [When("^the server returns code 404$")]
    public async Task WhenServerReturns404()
    {
        var (client, _) = Loopback(o => o.AddMethod("probe", string () =>
            throw new JsonRpcApplicationException(404, "Not found", JsonDocument.Parse("""{"id":7}""").RootElement)));

        var generated = Activator.CreateInstance(Build().Type("ApiClient"), client, null, JsonRpcParamStyle.ByPosition)!;

        try
        {
            await (Task)ProbeMethod().Invoke(generated, new object?[] { CancellationToken.None })!;
        }
        catch (Exception ex)
        {
            _world.Error = ex;
        }
    }

    [Then("^the generated client throws a typed exception exposing the error data$")]
    public void ThenTypedException()
    {
        var error = Assert.IsAssignableFrom<JsonRpcException>(_world.Error);
        Assert.Equal("ProbeError404Exception", error.GetType().Name);
        Assert.Equal(404, error.Code);
        Assert.Equal(7, ((JsonElement)error.ErrorData!).GetProperty("id").GetInt32());
    }

    [When("^the generated client is constructed over a websocket JsonRpcClient and then a tcp JsonRpcClient$")]
    public async Task WhenOverTwoTransports()
    {
        Use(ApiDocument());
        var results = new List<int>();

        foreach (var transport in new[] { "websocket", "tcp" })
        {
            await using var connection = await Connections.ConnectAsync(transport, _world.State);
            var generated = Activator.CreateInstance(Build().Type("ApiClient"), connection.Client, null, JsonRpcParamStyle.ByPosition)!;
            var task = (Task<int>)Contract().GetMethod("AddAsync")!.Invoke(generated, new object?[] { 2, 3, CancellationToken.None })!;
            results.Add(await task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        _world.Values["transportResults"] = results;
    }

    [Then("^the same calls succeed over both$")]
    public void ThenSameCalls() => Assert.Equal(new[] { 5, 5 }, (List<int>)_world.Values["transportResults"]!);

    [Given(@"^a method with ""x-jsoncall-stream"": true and result schema Tick$")]
    public void GivenStreamMethod() => Use(Probe("""{"$ref":"#/components/schemas/Tick"}""",
        """ "Tick": {"type":"object","required":["index"],"properties":{"index":{"type":"integer","format":"int32"}}} """,
        extraMethodFields: """, "x-jsoncall-stream": true"""));

    [Then("^the generated method returns IAsyncEnumerable<Tick> and accepts a CancellationToken$")]
    public void ThenStreamMethod()
    {
        var method = ProbeMethod();
        var tick = Build().Type("Tick");

        Assert.Equal(typeof(IAsyncEnumerable<>).MakeGenericType(tick), method.ReturnType);
        Assert.Contains(method.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
    }

    [Given(@"^a method with ""deprecated"": true$")]
    public void GivenDeprecated() => Use(Probe("""{"type":"string"}""", extraMethodFields: """, "deprecated": true"""));

    [Then(@"^the generated method has \[Obsolete\]$")]
    public void ThenObsolete() => Assert.NotNull(ProbeMethod().GetCustomAttribute<ObsoleteAttribute>());

    [Given("^method and param summaries in the document$")]
    public void GivenSummaries() => Use("""
        {"openrpc":"1.2.6","info":{"title":"d","version":"1"},"methods":[
          {"name":"probe","summary":"Does the probe.","params":[{"name":"x","required":true,"description":"The x value.","schema":{"type":"integer","format":"int32"}}],
           "result":{"name":"result","schema":{"type":"string"}}}]}
        """);

    [Then("^the generated members carry matching XML doc comments$")]
    public void ThenXmlDocs()
    {
        var source = Build().AllSource;
        Assert.Contains("/// Does the probe.", source);
        Assert.Contains("""<param name="x">The x value.</param>""", source);
    }

    [Then("^a JsonSerializerContext covering all generated DTOs is generated$")]
    public async Task ThenJsonContext()
    {
        // A source generator cannot feed the System.Text.Json generator, so the context is produced by the
        // command line tool (or by setting JsonContext metadata), whose output the project compiles as ordinary source.
        var directory = NewDirectory();
        var file = Path.Combine(directory, "api.openrpc.json");
        await File.WriteAllTextAsync(file, _json);

        var exit = await JsonCall.Tool.Cli.RunAsync(new[] { "generate", file, "--out", Path.Combine(directory, "gen"), "--json-context" },
            TextWriter.Null, TextWriter.Null, CancellationToken.None);

        Assert.Equal(0, exit);
        var context = await File.ReadAllTextAsync(Path.Combine(directory, "gen", "ApiClient.JsonContext.g.cs"));
        Assert.Contains("[JsonSerializable(typeof(Person))]", context);
        Assert.Contains("partial class ApiClientJsonContext : JsonSerializerContext", context);
        _world.Values["client"] = await File.ReadAllTextAsync(Path.Combine(directory, "gen", "ApiClient.g.cs"));
    }

    [Then("^the client uses it by default$")]
    public void ThenClientUsesContext() =>
        Assert.Contains("options ?? ApiClientJsonContext.Default.Options", (string)_world.Values["client"]!);

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jsoncall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _world.OnCleanup(() =>
        {
            try { Directory.Delete(directory, true); } catch { }
            return ValueTask.CompletedTask;
        });
        return directory;
    }

    // ---- determinism and diagnostics ----------------------------------

    [When("^the generator runs twice on the same document$")]
    public void WhenRunsTwice()
    {
        Use(ApiDocument());
        _world.Values["first"] = GeneratorHarness.Build(_json, _path, null, compile: false).Sources;
        _world.Values["second"] = GeneratorHarness.Build(_json, _path, null, compile: false).Sources;
    }

    [Then("^the outputs are byte-identical$")]
    public void ThenIdentical()
    {
        var first = (IReadOnlyList<(string HintName, string Text)>)_world.Values["first"]!;
        var second = (IReadOnlyList<(string HintName, string Text)>)_world.Values["second"]!;

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);

        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].HintName, second[i].HintName);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(first[i].Text), System.Text.Encoding.UTF8.GetBytes(second[i].Text));
        }
    }

    [Given("^the document has a method with a duplicate param name$")]
    public void GivenDuplicateParam() => Use("""
        {
          "openrpc": "1.2.6",
          "info": { "title": "d", "version": "1" },
          "methods": [
            { "name": "a", "params": [], "result": { "name": "r", "schema": { "type": "string" } } },
            { "name": "b", "params": [], "result": { "name": "r", "schema": { "type": "string" } } },
            { "name": "c", "params": [
                { "name": "x", "schema": { "type": "string" } },
                { "name": "x", "schema": { "type": "string" } } ],
              "result": { "name": "r", "schema": { "type": "string" } } }
          ]
        }
        """);

    [Then(@"^error ""JSONCALL0xx"" is reported at the JSON path ""(.*)""$")]
    public void ThenErrorAtPath(string path)
    {
        var error = Assert.Single(Build().GeneratorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Matches("^JSONCALL0\\d\\d$", error.Id);
        Assert.Contains("JSON path: " + path + ")", error.GetMessage());
        _world.Values["error"] = error;
    }

    [Then("^the file and line of the AdditionalFile are included$")]
    public void ThenFileAndLine()
    {
        var error = (Diagnostic)_world.Values["error"]!;
        var span = error.Location.GetLineSpan();

        Assert.Equal("api.openrpc.json", span.Path);

        // The duplicate "x" is the second parameter of the third method: line 9 of the document (zero-based 8).
        Assert.Equal(8, span.StartLinePosition.Line);
    }

    [Given(@"^the document declares ""openrpc"": ""9\.0\.0""$")]
    public void GivenFutureVersion() => Use(ApiDocument().Replace("\"openrpc\": \"1.2.6\"", "\"openrpc\": \"9.0.0\""));

    [Then("^a warning states the supported versions and generation continues best-effort$")]
    public void ThenVersionWarning()
    {
        var warning = Assert.Single(Build().GeneratorDiagnostics, d => d.Id == "JSONCALL003");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("1.0 to 1.4", warning.GetMessage());
        Assert.NotEmpty(Build().Sources);
        Assert.NotNull(Build().Assembly);
    }

    // ---- incrementality -------------------------------------------------

    private Microsoft.CodeAnalysis.GeneratorDriver? _driver;
    private Microsoft.CodeAnalysis.CSharp.CSharpCompilation? _compilation;

    [Given("^the project has been built once$")]
    public void GivenBuiltOnce()
    {
        _compilation = GeneratorHarness.CreateCompilation();
        _driver = GeneratorHarness.CreateDriver(ApiDocument(), "api.openrpc.json", track: true).RunGenerators(_compilation);
    }

    [When("^an unrelated source file is edited$")]
    public void WhenUnrelatedEdit()
    {
        var edited = _compilation!.AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class Unrelated { }"));
        _driver = _driver!.RunGenerators(edited);
    }

    [Then("^the generator does not execute again$")]
    public void ThenNotExecutedAgain()
    {
        var steps = _driver!.GetRunResult().Results.Single().TrackedSteps[OpenRpcClientGenerator.InputStepName];
        var reasons = steps.SelectMany(s => s.Outputs).Select(o => o.Reason).ToList();

        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.True(reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, reason.ToString()));

        // The output step is skipped as well: no source was regenerated.
        Assert.All(_driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs),
            o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    // ---- command line tool -------------------------------------------

    [When(@"^""jsoncall generate api\.openrpc\.json --out \./gen"" is run$")]
    public async Task WhenCliRuns()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "api.openrpc.json");
        await File.WriteAllTextAsync(file, ApiDocument());

        var output = new StringWriter();
        var exit = await JsonCall.Tool.Cli.RunAsync(new[] { "generate", file, "--out", Path.Combine(directory, "gen") },
            output, output, CancellationToken.None);

        Assert.True(exit == 0, output.ToString());
        _world.Values["cliDirectory"] = Path.Combine(directory, "gen");
    }

    [Then("^the files equal the source generator output$")]
    public async Task ThenCliMatchesGenerator()
    {
        var generated = GeneratorHarness.Build(ApiDocument(), "api.openrpc.json", null, compile: false).Sources;
        var directory = (string)_world.Values["cliDirectory"]!;
        var files = Directory.GetFiles(directory).Order().ToArray();

        Assert.Equal(generated.Count, files.Length);

        foreach (var source in generated)
        {
            // The generator prefixes hint names with the document name; the tool does not.
            var name = source.HintName.Substring("api.".Length);
            Assert.Equal(source.Text, await File.ReadAllTextAsync(Path.Combine(directory, name)));
        }
    }

    [When(@"^""jsoncall generate --url https://example\.test/rpc --discover"" is run$")]
    public async Task WhenCliDiscovers()
    {
        // example.test is a placeholder; the specs serve rpc.discover from a local host.
        _world.State.ConfigureServer = o => o.AddOpenRpc();
        var host = await TestHost.StartRpcAsync(_world.State);
        _world.OnCleanup(host.DisposeAsync);

        var directory = NewDirectory();
        var output = new StringWriter();
        var exit = await JsonCall.Tool.Cli.RunAsync(
            new[] { "generate", "--url", new Uri(host.BaseAddress, "/rpc").ToString(), "--discover", "--out", directory, "--client-name", "Remote" },
            output, output, CancellationToken.None);

        Assert.True(exit == 0, output.ToString());
        _world.Values["cliDirectory"] = directory;
    }

    [Then(@"^the CLI calls ""rpc\.discover"", saves the document and generates the client$")]
    public void ThenCliDiscovered()
    {
        var directory = (string)_world.Values["cliDirectory"]!;
        var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "Remote.openrpc.json"))).RootElement;

        Assert.Equal(OpenRpc.OpenRpcDocumentGenerator.SupportedVersion, saved.GetProperty("openrpc").GetString());

        var client = File.ReadAllText(Path.Combine(directory, "Remote.g.cs")); // named by --client-name
        Assert.Contains("public interface IRemote", client);
        Assert.Contains("AddAsync", client);
    }

    // ---- round trip ----------------------------------------------------

    private sealed class MathApiService : IMathApi
    {
        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

        public Task<string> GreetAsync(string name, string greeting = "Hello") => Task.FromResult($"{greeting} {name}");

        public Task<int> PlusAsync(int a, int b) => Task.FromResult(a + b);

        public Task LogAsync(string message) => Task.CompletedTask;

        public Task<Person> GetPersonAsync(int id) => Task.FromResult(new Person("Ada", 36));

        public Task<IReadOnlyList<Person>> ListPeopleAsync(Person? filter = null) =>
            Task.FromResult<IReadOnlyList<Person>>(new[] { new Person("Ada", 36), new Person("Grace", 45) });
    }

    [Given("^the reference server contract IMath is hosted$")]
    public void GivenReferenceServer() { }

    [When("^its OpenRPC document is generated and a client is generated from it$")]
    public void WhenDocumentAndClient()
    {
        var document = OpenRpc.OpenRpcDocumentGenerator.ToJson(new OpenRpc.OpenRpcDocumentGenerator().Generate(typeof(IMathApi)));
        Use(document);
        _metadata["ClientName"] = "MathClient";
        Build();
    }

    [Then("^every generated client method calls the live server successfully$")]
    public async Task ThenEveryMethodCalls()
    {
        var (client, recording) = Loopback(o => o.AddContract<IMathApi>(new MathApiService()));
        var generated = Activator.CreateInstance(Build().Type("MathClient"), client, null, JsonRpcParamStyle.ByPosition)!;
        var type = generated.GetType();

        async Task<object?> Call(string name, params object?[] args)
        {
            var method = type.GetMethod(name)!;
            var task = (Task)method.Invoke(generated, args.Append(CancellationToken.None).ToArray())!;
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
        }

        Assert.Equal(5, await Call("AddAsync", 2, 3));
        Assert.Equal("Hello Ada", await Call("GreetAsync", "Ada", null));
        Assert.Equal("Hi Ada", await Call("GreetAsync", "Ada", "Hi"));
        Assert.Equal(5, await Call("PlusAsync", 2, 3));
        await Call("LogAsync", "hello");

        var person = await Call("GetPersonAsync", 1);
        Assert.Equal("Ada", person!.GetType().GetProperty("Name")!.GetValue(person));

        var people = (System.Collections.IEnumerable)(await Call("ListPeopleAsync", new object?[] { null }))!;
        Assert.Equal(2, people.Cast<object>().Count());

        // The notification was sent without an id.
        Assert.Contains(recording.SentJson, j => !JsonDocument.Parse(j).RootElement.TryGetProperty("id", out _));
    }

    [Then("^generated DTOs serialize to the same JSON as the server's DTOs$")]
    public void ThenSameJson()
    {
        var serverJson = JsonSerializer.Serialize(new Person("Ada", 36), JsonRpcSerializer.DefaultOptions);

        var person = Build().Type("Person");
        var generated = Activator.CreateInstance(person, "Ada", 36)!;

        Assert.Equal(serverJson, JsonSerializer.Serialize(generated, person, JsonRpcSerializer.DefaultOptions));
        Assert.Equal(serverJson, JsonSerializer.Serialize(JsonSerializer.Deserialize(serverJson, person, JsonRpcSerializer.DefaultOptions), person, JsonRpcSerializer.DefaultOptions));
    }

    // ---- sibling schema files -----------------------------------------

    private static string ObjectSchema(string? title, string first, string second) =>
        (title is null ? "" : $"\"title\":\"{title}\",")
        + $"\"type\":\"object\",\"required\":[\"{first}\"],\"properties\":{{\"{first}\":{{\"type\":\"string\"}},\"{second}\":{{\"type\":\"string\"}}}}";

    [Given(@"^a method ""probe"" whose result schema is (\{.*\})$")]
    public void GivenProbeWithResult(string schema) => Use(Probe(schema), "api.openrpc.json");

    [Given(@"^the sibling schema ""(.*)"" is included as an AdditionalFile with title ""(.*)"" and properties ""(.*)"" and ""(.*)""$")]
    public void GivenSibling(string name, string title, string first, string second) =>
        _siblings[name] = "{" + ObjectSchema(title, first, second) + "}";

    [Given(@"^the sibling schema ""(.*)"" is included as an AdditionalFile with a definition ""(.*)"" with properties ""(.*)"" and ""(.*)""$")]
    public void GivenSiblingDefinition(string name, string definition, string first, string second) =>
        _siblings[name] = "{\"definitions\":{\"" + definition + "\":{" + ObjectSchema(null, first, second) + "}}}";

    [Given(@"^the sibling schemas ""a\.schema\.json"" and ""b\.schema\.json"" refer to each other$")]
    public void GivenCycle()
    {
        _siblings["a.schema.json"] =
            """{"type":"object","properties":{"b":{"$ref":"b.schema.json"},"self":{"$ref":"#"},"name":{"type":"string"}}}""";
        _siblings["b.schema.json"] =
            """{"type":"object","properties":{"a":{"$ref":"a.schema.json"},"name":{"type":"string"}}}""";
    }

    [Then(@"^a record ""(.*)"" with the properties ""(.*)"" and ""(.*)"" is generated$")]
    public void ThenRecordWithProperties(string record, string first, string second)
    {
        var names = Build().Type(record).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains(first, names);
        Assert.Contains(second, names);
    }

    [Then(@"^the records ""(.*)"" and ""(.*)"" are generated$")]
    public void ThenRecordsGenerated(string first, string second)
    {
        Assert.NotNull(Build().Type(first));
        Assert.NotNull(Build().Type(second));
    }

    [Then(@"^the probe result type is ""(.*)""$")]
    public void ThenProbeResultType(string name) =>
        Assert.Equal(name, ProbeMethod().ReturnType.GetGenericArguments().Single().Name);

    [Then("^no JSONCALL006 diagnostic is reported$")]
    public void ThenNoUnresolved() =>
        Assert.DoesNotContain(Build().GeneratorDiagnostics, d => d.Id == "JSONCALL006");

    [Then(@"^a JSONCALL006 warning is reported at the JSON path ""(.*)""$")]
    public void ThenUnresolvedAt(string path)
    {
        var warning = Assert.Single(Build().GeneratorDiagnostics, d => d.Id == "JSONCALL006");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.EndsWith("(JSON path: " + path + ")", warning.GetMessage());
    }

    [Given("^the project has been built once with the sibling schema$")]
    public void GivenBuiltWithSibling()
    {
        _compilation = GeneratorHarness.CreateCompilation();
        _texts = new List<AdditionalText> { GeneratorHarness.Text(_path, _json) };
        _texts.AddRange(_siblings.Select(s => GeneratorHarness.Text(s.Key, s.Value)));
        _driver = GeneratorHarness.CreateDriver(_texts, _path, track: true).RunGenerators(_compilation);
    }

    private List<AdditionalText>? _texts;

    [When(@"^the sibling schema gains the property ""(.*)""$")]
    public void WhenSiblingEdited(string property)
    {
        var old = _texts![1];
        var edited = GeneratorHarness.Text(old.Path, old.GetText()!.ToString().Replace("\"properties\":{", $"\"properties\":{{\"{property}\":{{\"type\":\"string\"}},"));
        _driver = _driver!.ReplaceAdditionalText(old, edited).RunGenerators(_compilation!);
        _siblings[old.Path] = edited.GetText()!.ToString();
        _build = null;
    }

    [Then(@"^the generator executes again and the record has the property ""(.*)""$")]
    public void ThenExecutedAgain(string property)
    {
        var steps = _driver!.GetRunResult().Results.Single().TrackedSteps[OpenRpcClientGenerator.InputStepName];
        Assert.Contains(steps.SelectMany(s => s.Outputs), o => o.Reason == IncrementalStepRunReason.Modified);
        Assert.Contains(property, Build().Type("DaemonEvent").GetProperties().Select(p => p.Name));
    }

    // ---- sibling schemas through the command line tool ----------------

    [Given(@"^a directory holding ""api\.openrpc\.json"" with a method ""probe"" whose result is (\{.*\})$")]
    public async Task GivenDirectoryWithDocument(string schema)
    {
        var directory = NewDirectory();
        _world.Values["cliSource"] = directory;
        await File.WriteAllTextAsync(Path.Combine(directory, "api.openrpc.json"), Probe(schema));
    }

    [Given(@"^the directory also holds ""(.*)"" with title ""(.*)"" and properties ""(.*)"" and ""(.*)""$")]
    public async Task GivenDirectorySibling(string name, string title, string first, string second) =>
        await File.WriteAllTextAsync(Path.Combine((string)_world.Values["cliSource"]!, name), "{" + ObjectSchema(title, first, second) + "}");

    [When(@"^""jsoncall generate api\.openrpc\.json --out \./gen"" is run in that directory$")]
    public async Task WhenCliRunsInDirectory()
    {
        var directory = (string)_world.Values["cliSource"]!;
        var output = new StringWriter();

        // Absolute paths: the process-wide current directory is shared with scenarios running in parallel.
        var exit = await JsonCall.Tool.Cli.RunAsync(
            new[] { "generate", Path.Combine(directory, "api.openrpc.json"), "--out", Path.Combine(directory, "gen") },
            output, output, CancellationToken.None);

        Assert.True(exit == 0, output.ToString());

        _world.Values["cliOutput"] = output.ToString();
        _world.Values["cliSource"] = Path.Combine(directory, "gen");
    }

    [Then(@"^the generated client has a record ""(.*)""$")]
    public async Task ThenCliRecord(string record)
    {
        var generated = await File.ReadAllTextAsync(Path.Combine((string)_world.Values["cliSource"]!, "ApiClient.g.cs"));
        Assert.Contains("public sealed record " + record + "(", generated);
    }

    [Then(@"^the tool output reports that ""(.*)"" was loaded$")]
    public void ThenCliReportsLoaded(string document) =>
        Assert.Contains("JSONCALL007: Loaded the referenced document '" + document + "'", (string)_world.Values["cliOutput"]!);

    [Then("^the tool output has no JSONCALL006 warning$")]
    public void ThenCliNoUnresolved() =>
        Assert.DoesNotContain("JSONCALL006", (string)_world.Values["cliOutput"]!);

    // ---- standard protocol contracts (LSP, ACP) ------------------------

    [Given(@"^the contract ""(.*)"" with its sibling schemas (.*)$")]
    public void GivenContract(string document, string siblings)
    {
        // The AdditionalFiles keep the checked-in layout, so the document's relative $refs resolve by exact path.
        foreach (var sibling in System.Text.RegularExpressions.Regex.Matches(siblings, "\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            _siblings[sibling] = ContractData.Read(sibling);

        Use(ContractData.Read(document), document);
    }

    [Given(@"^the client is named ""(.*)"" in the namespace ""(.*)""$")]
    public void GivenClientName(string client, string ns)
    {
        _metadata["ClientName"] = client;
        _metadata["Namespace"] = ns;
        _build = null;
    }

    [Then(@"^the generated client matches the checked-in golden ""(.*)"" byte for byte$")]
    public void ThenMatchesGolden(string golden)
    {
        var actual = System.Text.Encoding.UTF8.GetBytes(Build().AllSource);

        // Set JSONCALL_UPDATE_GOLDEN=1 to rewrite the checked-in file after an intentional change, then review the diff.
        if (Environment.GetEnvironmentVariable("JSONCALL_UPDATE_GOLDEN") == "1")
            File.WriteAllBytes(ContractData.Resolve(ContractData.SourceDirectory(), golden), actual);

        Assert.Equal(ContractData.ReadBytes(golden), actual);
    }
}
