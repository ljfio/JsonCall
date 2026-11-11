using System.Text;
using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;
using Reqnroll;

namespace JsonCall.Specs.Steps;

[Binding]
public sealed class SmokeSteps
{
    private DefaultMessageSerializer? _serializer;
    private string? _json;

    [Given("the default message serializer")]
    public void GivenTheDefaultMessageSerializer() => _serializer = new DefaultMessageSerializer();

    [When(@"the params ""(.*)"" and (\d+) are serialized by position")]
    public async Task WhenParamsSerializedByPosition(string text, int number) =>
        _json = await SerializeAsync(new JsonRpcParams(new object[] { text, number }));

    [When(@"the params (\{.*\}) are serialized by name")]
    public async Task WhenParamsSerializedByName(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, int>>(json)!
            .ToDictionary(p => p.Key, p => (object)p.Value);

        _json = await SerializeAsync(new JsonRpcParams(values));
    }

    [Then("the JSON is '(.*)'")]
    public void ThenTheJsonIs(string expected) => Assert.Equal(expected, _json);

    private async Task<string> SerializeAsync(JsonRpcParams parameters)
    {
        using var stream = new MemoryStream();
        await _serializer!.SerializeAsync(stream, parameters);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
