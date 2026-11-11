using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Server;
using JsonCall.Specs.Support;
using JsonCall.Transport;
using Reqnroll;

namespace JsonCall.Specs.Steps;

public enum Colour
{
    Red,
    Green
}

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal partial class TestJsonContext : JsonSerializerContext
{
}

[Binding]
public sealed class SerializationSteps
{
    private readonly World _world;

    public SerializationSteps(World world) => _world = world;

    private IMessageSerializer Serializer => (IMessageSerializer)_world.Values["ser"]!;

    private string Original => (string)_world.Values["original"]!;

    private string RoundTrip(string json, IMessageSerializer? serializer = null)
    {
        serializer ??= Serializer;
        var wire = serializer.FromJson(Encoding.UTF8.GetBytes(json));
        return Encoding.UTF8.GetString(serializer.ToJson(wire));
    }

    [Given("^the (json|cbor|messagepack) serializer$")]
    public void GivenSerializer(string name) => _world.Values["ser"] = Serializers.ByName(name);

    // ---------------------------------------------------------------- round trips

    [Given(@"^a request with method ""sum"", id ""42"" and params (.*)$")]
    public void GivenRequest(string @params) =>
        _world.Values["original"] = $"{{\"jsonrpc\":\"2.0\",\"method\":\"sum\",\"params\":{@params},\"id\":\"42\"}}";

    [When("^the request is serialized and deserialized$")]
    public void WhenRoundTripRequest() => _world.Values["roundtrip"] = RoundTrip(Original);

    [Then("^the deserialized request equals the original$")]
    public void ThenRequestEquals()
    {
        JsonAssert.Equal(Original, (string)_world.Values["roundtrip"]!);

        var item = IncomingItem.Parse(JsonAssert.Parse((string)_world.Values["roundtrip"]!));
        var request = Assert.IsType<IncomingItem.Request>(item).Value;
        Assert.Equal("sum", request.Method);
        Assert.Equal("\"42\"", request.Id!.Value.RawJson);
    }

    [Given(@"^a success response with id 1 and result (.*)$")]
    public void GivenSuccess(string result) =>
        _world.Values["original"] = $"{{\"jsonrpc\":\"2.0\",\"result\":{result},\"id\":1}}";

    [When("^the response is serialized and deserialized as a typed Person$")]
    public void WhenTypedPerson()
    {
        var json = RoundTrip(Original);
        var result = JsonAssert.Parse(json).GetProperty("result");
        _world.Values["person"] = result.Deserialize<Person>(JsonRpcSerializer.DefaultOptions);
    }

    [Then(@"^the Person has name ""Ada"" and age 36$")]
    public void ThenPerson() => Assert.Equal(new Person("Ada", 36), _world.Values["person"]);

    [Given(@"^an error response with id 1, code 1001, message ""Quota exceeded"" and data (.*)$")]
    public void GivenError(string data) =>
        _world.Values["original"] =
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":1001,\"message\":\"Quota exceeded\",\"data\":{data}}},\"id\":1}}";

    [When("^the response is serialized and deserialized$")]
    public void WhenRoundTripResponse() => _world.Values["roundtrip"] = RoundTrip(Original);

    [Then("^the error code, message and data are preserved$")]
    public void ThenErrorPreserved()
    {
        var item = IncomingItem.Parse(JsonAssert.Parse((string)_world.Values["roundtrip"]!));
        var response = Assert.IsType<IncomingItem.Response>(item).Value;
        Assert.Equal(1001, response.Error!.Code);
        Assert.Equal("Quota exceeded", response.Error.Message);
        Assert.Equal(5, response.Error.Data!.Value.GetProperty("limit").GetInt32());
    }

    // ---------------------------------------------------------------- params

    [When("^a request with by-(position|name) params is round-tripped$")]
    public void WhenParamsRoundTripped(string structure)
    {
        var @params = structure == "position" ? "[3,\"x\",true]" : "{\"b\":1,\"a\":\"x\"}";
        var original = $"{{\"jsonrpc\":\"2.0\",\"method\":\"m\",\"params\":{@params},\"id\":1}}";
        _world.Values["original"] = original;
        _world.Values["roundtrip"] = RoundTrip(original);
    }

    [Then("^the params are still by-(position|name)$")]
    public void ThenParamsStill(string structure)
    {
        var request = ((IncomingItem.Request)IncomingItem.Parse(JsonAssert.Parse((string)_world.Values["roundtrip"]!))).Value;
        Assert.Equal(structure == "position" ? JsonValueKind.Array : JsonValueKind.Object, request.Params!.Value.ValueKind);
    }

    [Then("^values keep their order or keys$")]
    public void ThenOrderKept() => Assert.Equal(Original, (string)_world.Values["roundtrip"]!);

    [When("^params are created from a single value, a params array and a dictionary$")]
    public void WhenParamsCreated()
    {
        _world.Values["single"] = JsonRpcParams.Single(5);
        _world.Values["array"] = JsonRpcParams.ByPosition("a", 1);
        _world.Values["dictionary"] = JsonRpcParams.From(new Dictionary<string, object?> { ["k"] = 1 });
    }

    [Then("^each produces the expected by-position or by-name structure$")]
    public void ThenParamsStructure()
    {
        var single = (JsonRpcParams)_world.Values["single"]!;
        var array = (JsonRpcParams)_world.Values["array"]!;
        var dictionary = (JsonRpcParams)_world.Values["dictionary"]!;

        Assert.Equal(JsonRpcParamsType.ByPosition, single.Type);
        Assert.Equal("[5]", single.Node.ToJsonString());
        Assert.Equal(JsonRpcParamsType.ByPosition, array.Type);
        Assert.Equal("[\"a\",1]", array.Node.ToJsonString());
        Assert.Equal(JsonRpcParamsType.ByName, dictionary.Type);
        Assert.Equal("{\"k\":1}", dictionary.Node.ToJsonString());
    }

    [Given("^a request without params$")]
    public void GivenNoParams() =>
        _world.Values["original"] = "{\"jsonrpc\":\"2.0\",\"method\":\"m\",\"id\":1}";

    [When("^it is serialized with each serializer$")]
    public void WhenEachSerializer() =>
        _world.Values["all"] = Serializers.Names.Select(n => RoundTrip(Original, Serializers.ByName(n))).ToList();

    [Then("^no \"params\" member is written$")]
    public void ThenNoParamsMember() =>
        Assert.All((List<string>)_world.Values["all"]!, json =>
            Assert.False(JsonAssert.Parse(json).TryGetProperty("params", out _)));

    // ---------------------------------------------------------------- options

    [Given("^serializer options with camelCase naming and string enums$")]
    public void GivenOptions() =>
        _world.Values["options"] = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

    [When("^a request with an enum parameter is serialized$")]
    public void WhenEnumSerialized()
    {
        var options = (JsonSerializerOptions)_world.Values["options"]!;
        var @params = JsonRpcParams.ByName(options, new[]
        {
            new KeyValuePair<string, object?>("filter", new { FavouriteColour = Colour.Green })
        });

        _world.Values["json"] = Encoding.UTF8.GetString(JsonRpcWire.Write(w => @params.Node.WriteTo(w, options)));
    }

    [Then("^the JSON uses camelCase member names and the enum as a string$")]
    public void ThenCamelCase() => Assert.Equal("{\"filter\":{\"favouriteColour\":\"green\"}}", _world.Values["json"]);

    [Given("^a JsonSerializerContext covering the contract's types$")]
    public void GivenContext() =>
        _world.Values["options"] = new JsonSerializerOptions { TypeInfoResolver = TestJsonContext.Default };

    [When("^a call is made with trimming and reflection-based serialization disabled$")]
    public async Task WhenSourceGeneratedCall()
    {
        var options = (JsonSerializerOptions)_world.Values["options"]!;
        var serverOptions = new JsonRpcServerOptions { SerializerOptions = options };
        serverOptions.AddContract<IMath>(new TestService(_world.State));

        var server = new JsonRpcServer(serverOptions);
        var (clientSide, serverSide) = InMemoryTransport.CreatePair();
        _world.OnCleanup(server.Attach(serverSide).DisposeAsync);

        var client = new JsonRpcClient(clientSide, new JsonRpcPeerOptions { SerializerOptions = options });
        _world.OnCleanup(client.DisposeAsync);

        _world.Values["sum"] = await client.InvokeAsync<int>("add", JsonRpcParams.ByPosition(options, new object?[] { 2, 3 }));
    }

    [Then("^the call succeeds$")]
    public void ThenCallSucceeds() => Assert.Equal(5, _world.Values["sum"]);

    // ---------------------------------------------------------------- malformed

    [When("^the bytes (.*) are deserialized as a message$")]
    public async Task WhenMalformed(string description)
    {
        var serializer = Serializer;

        byte[] bytes = description switch
        {
            "truncated JSON" => Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"sum\",\"par"),
            "invalid UTF-8" => new byte[] { 0x7B, 0x22, 0xFF, 0xFE, 0x22, 0x3A, 0x31, 0x7D },
            "truncated CBOR" => serializer.FromJson("{\"jsonrpc\":\"2.0\",\"method\":\"sum\",\"id\":1}"u8)[..12],
            "truncated payload" => serializer.FromJson("{\"jsonrpc\":\"2.0\",\"method\":\"sum\",\"id\":1}"u8)[..12],
            _ => throw new ArgumentException(description)
        };

        var (clientSide, serverSide) = InMemoryTransport.CreatePair();
        var options = new JsonRpcServerOptions();
        options.AddContract<IProtocolService>(new TestService(_world.State));
        var server = new JsonRpcServer(options);
        var peer = server.Attach(serverSide, serializer);
        _world.OnCleanup(peer.DisposeAsync);

        _world.Raw = new RawPeer(clientSide, serializer);
        await _world.Raw.SendWireAsync(bytes);
    }

    [Then("^a parse error with code -32700 is produced$")]
    public async Task ThenParseError()
    {
        var text = await _world.Raw!.NextAsync();
        Assert.NotNull(text);
        var response = JsonAssert.Parse(text);
        Assert.Equal(-32700, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
    }

    [Then("^no unhandled exception is thrown$")]
    public async Task ThenNoException()
    {
        // the connection survives: a valid request still gets an answer
        await _world.Raw!.SendJsonAsync("{\"jsonrpc\":\"2.0\",\"method\":\"sum\",\"params\":[1,2],\"id\":1}");
        var text = await _world.Raw.NextAsync();
        Assert.NotNull(text);
        Assert.Equal(3, JsonAssert.Parse(text).GetProperty("result").GetInt32());
    }

    // ---------------------------------------------------------------- size limit

    [Given("^a maximum message size of 1 MB$")]
    public void GivenMaxSize() => _world.Values["max"] = 1024 * 1024;

    [When("^a 5 MB message is received$")]
    public async Task WhenOversized()
    {
        // Only the header is present: the transport must reject the frame without trying to buffer the body.
        var header = Encoding.ASCII.GetBytes($"Content-Length: {5 * 1024 * 1024}\r\n\r\n");
        var input = new MemoryStream(header);
        var transport = new StreamMessageTransport(input, Stream.Null, (int)_world.Values["max"]!);

        _world.Error = await Record.ExceptionAsync(async () => await transport.ReceiveAsync());
        _world.Values["consumed"] = input.Position;
        _world.Values["header"] = header.Length;
    }

    [Then("^the connection reports a message-too-large error$")]
    public void ThenTooLarge() => Assert.IsType<MessageTooLargeException>(_world.Error);

    [Then("^memory use stays bounded$")]
    public void ThenBounded() =>
        Assert.True((long)_world.Values["consumed"]! <= (int)_world.Values["header"]!,
            "The body must not be read for an oversized frame");

    // ---------------------------------------------------------------- equivalence

    [Given(@"^the request ""sum"" with params \[1,2,3\]$")]
    public void GivenSumRequest() =>
        _world.Values["original"] = "{\"jsonrpc\":\"2.0\",\"method\":\"sum\",\"params\":[1,2,3],\"id\":1}";

    [When("^it is encoded with json, cbor and messagepack and decoded again$")]
    public void WhenEncodedThree() =>
        _world.Values["all"] = Serializers.Names.Select(n => RoundTrip(Original, Serializers.ByName(n))).ToList();

    [Then("^all three decoded requests are equal$")]
    public void ThenAllEqual()
    {
        var all = (List<string>)_world.Values["all"]!;
        Assert.Equal(3, all.Count);
        Assert.All(all, json => JsonAssert.Equal(Original, json));
    }
}
