using System.Text;
using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Specs.Support;
using Reqnroll;

namespace JsonCall.Specs.Steps;

[Binding]
public sealed class SmokeSteps
{
    private readonly World _world;

    public SmokeSteps(World world) => _world = world;

    [Given("^the default message serializer$")]
    public void GivenTheDefaultMessageSerializer() => _world.Values["serializer"] = JsonMessageSerializer.Instance;

    [When(@"^the params ""(.*)"" and (\d+) are serialized by position$")]
    public void WhenParamsSerializedByPosition(string text, int number) =>
        _world.Values["json"] = Serialize(JsonRpcParams.ByPosition(text, number));

    [When(@"^the params (\{.*\}) are serialized by name$")]
    public void WhenParamsSerializedByName(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, int>>(json)!
            .Select(p => new KeyValuePair<string, object?>(p.Key, p.Value));

        _world.Values["json"] = Serialize(JsonRpcParams.ByName(values));
    }

    [Then("^the JSON is '(.*)'$")]
    public void ThenTheJsonIs(string expected) => Assert.Equal(expected, _world.Values["json"]);

    private static string Serialize(JsonRpcParams parameters) =>
        Encoding.UTF8.GetString(JsonRpcWire.Write(w => parameters.Node.WriteTo(w)));
}
