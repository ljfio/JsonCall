using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Server;
using JsonCall.Specs.Support;
using Reqnroll;

namespace JsonCall.Specs.Steps;

[Binding]
public sealed class ProtocolSteps
{
    private readonly World _world;

    public ProtocolSteps(World world) => _world = world;

    private RawPeer Raw => _world.Raw!;

    private JsonElement Last => (JsonElement)_world.Values["last"]!;

    [Given(@"^a server exposing the methods ""subtract"", ""sum"", ""get_data"", ""update"" and ""notify_hello""$")]
    public void GivenAServer()
    {
        var options = new JsonRpcServerOptions();
        options.AddContract<IProtocolService>(new TestService(_world.State));
        var server = new JsonRpcServer(options);
        _world.Values["server"] = server;

        var (client, serverSide) = InMemoryTransport.CreatePair();
        var peer = server.Attach(serverSide);
        _world.OnCleanup(peer.DisposeAsync);
        _world.Raw = new RawPeer(client);
    }

    [When("^the client sends '(.*)'$")]
    public async Task WhenTheClientSends(string json) => await Raw.SendJsonAsync(json);

    [When("^the client sends the raw text '(.*)'$")]
    public async Task WhenTheClientSendsRaw(string text) => await Raw.SendWireAsync(System.Text.Encoding.UTF8.GetBytes(text));

    [When("^the client sends the batch$")]
    public async Task WhenTheClientSendsTheBatch(string batch) => await Raw.SendJsonAsync(batch);

    private async Task<JsonElement> ReadAsync()
    {
        var text = await Raw.NextAsync();
        Assert.NotNull(text);
        _world.Values["text"] = text;
        var element = JsonAssert.Parse(text);
        _world.Values["last"] = element;
        return element;
    }

    [Then("^the server responds with '(.*)'$")]
    public async Task ThenTheServerRespondsWith(string json)
    {
        var text = await Raw.NextAsync();
        _world.Values["text"] = text;
        JsonAssert.Equal(json, text);
    }

    [Then(@"^the server responds with a result for id (\d+)$")]
    public async Task ThenResultForId(int id)
    {
        var response = await ReadAsync();
        Assert.True(response.TryGetProperty("result", out _));
        Assert.Equal(id, response.GetProperty("id").GetInt32());
    }

    [Then("^the response id is exactly (.*)$")]
    public async Task ThenIdIsExactly(string id)
    {
        var response = await ReadAsync();
        Assert.Equal(id, response.GetProperty("id").GetRawText());
    }

    [Then("^the server sends no response$")]
    public async Task ThenNoResponse() => Assert.True(await Raw.ReceivesNothingAsync(), "A response was received");

    [Then(@"^the ""update"" handler is invoked once with \[1,2,3\]$")]
    public async Task ThenUpdateInvoked()
    {
        await Wait.UntilAsync(() => _world.State.Updates.Count >= 1);
        await Task.Delay(100);
        var call = Assert.Single(_world.State.Updates);
        Assert.Equal(new[] { 1, 2, 3 }, call);
    }

    [Then(@"^the server responds with error code (-?\d+) and id (.*)$")]
    public async Task ThenErrorWithId(int code, string id)
    {
        var response = await ReadAsync();
        Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(id, response.GetProperty("id").GetRawText());
    }

    [Then(@"^the server responds with error code (-?\d+)$")]
    public async Task ThenError(int code)
    {
        var response = await ReadAsync();
        Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Given(@"^the ""sum"" handler throws an unexpected exception$")]
    public void GivenSumThrows() =>
        _world.State.SumBehavior = () => throw new InvalidOperationException("secret-internal-detail");

    [Then("^the response does not leak the exception stack trace$")]
    public void ThenNoLeak()
    {
        var text = (string)_world.Values["text"]!;
        Assert.DoesNotContain("secret-internal-detail", text);
        Assert.DoesNotContain("   at ", text);
        Assert.DoesNotContain("InvalidOperationException", text);
    }

    [Given(@"^the ""sum"" handler throws an application error with code 1001, message ""Quota exceeded"" and data \{""limit"":5\}$")]
    public void GivenSumThrowsApplicationError() =>
        _world.State.SumBehavior = () =>
            throw new JsonRpcApplicationException(1001, "Quota exceeded", JsonDocument.Parse("{\"limit\":5}").RootElement);

    [Then(@"^the server responds with error code 1001, message ""Quota exceeded"" and data \{""limit"":5\}$")]
    public async Task ThenApplicationError()
    {
        var response = await ReadAsync();
        var error = response.GetProperty("error");
        Assert.Equal(1001, error.GetProperty("code").GetInt32());
        Assert.Equal("Quota exceeded", error.GetProperty("message").GetString());
        Assert.Equal(5, error.GetProperty("data").GetProperty("limit").GetInt32());
    }

    [When(@"^a handler is configured to throw an application error with code -32050$")]
    public void WhenReservedApplicationError() =>
        _world.Error = Record.Exception(() => new JsonRpcApplicationException(-32050, "nope"));

    [Then(@"^configuration fails with a message naming the reserved range -32768 to -32000$")]
    public void ThenReservedRangeMessage()
    {
        Assert.NotNull(_world.Error);
        Assert.Contains("-32768 to -32000", _world.Error!.Message);
    }

    [Then(@"^the server responds with a batch of (\d+) responses$")]
    public async Task ThenBatch(int count)
    {
        var response = await ReadAsync();
        Assert.Equal(JsonValueKind.Array, response.ValueKind);
        Assert.Equal(count, response.GetArrayLength());
    }

    private JsonElement ById(string id) =>
        Last.EnumerateArray().Single(r => r.GetProperty("id").ValueKind == JsonValueKind.String
                                          && r.GetProperty("id").GetString() == id);

    [Then(@"^the response for id ""(.*)"" has result (\d+)$")]
    public void ThenBatchResult(string id, int result) =>
        Assert.Equal(result, ById(id).GetProperty("result").GetInt32());

    [Then(@"^the response for id ""(.*)"" has error code (-?\d+)$")]
    public void ThenBatchError(string id, int code) =>
        Assert.Equal(code, ById(id).GetProperty("error").GetProperty("code").GetInt32());

    [Then(@"^one response has error code (-?\d+) and id null$")]
    public void ThenOneNullError(int code) =>
        Assert.Contains(Last.EnumerateArray(), r =>
            r.GetProperty("id").ValueKind == JsonValueKind.Null
            && r.GetProperty("error").GetProperty("code").GetInt32() == code);

    [Then(@"^the server responds with a single error response with code -32600 and id null$")]
    public async Task ThenSingleInvalid()
    {
        var response = await ReadAsync();
        Assert.Equal(JsonValueKind.Object, response.ValueKind);
        Assert.Equal(-32600, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
    }

    [Then(@"^every response has error code -32600 and id null$")]
    public void ThenEveryInvalid() =>
        Assert.All(Last.EnumerateArray(), r =>
        {
            Assert.Equal(-32600, r.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("id").ValueKind);
        });

    [When(@"^a handler is registered with the method name ""rpc\.custom""$")]
    public void WhenReservedMethod() =>
        _world.Error = Record.Exception(() => new JsonRpcServerOptions().AddMethod("rpc.custom", () => 1));

    [Then(@"^registration fails with a message that the ""rpc\."" prefix is reserved$")]
    public void ThenReservedPrefix()
    {
        Assert.NotNull(_world.Error);
        Assert.Contains("\"rpc.\" prefix is reserved", _world.Error!.Message);
    }
}
