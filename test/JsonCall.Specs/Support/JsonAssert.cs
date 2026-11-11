using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonCall.Specs.Support;

public static class JsonAssert
{
    public static void Equal(string expected, string? actual)
    {
        Assert.NotNull(actual);
        var e = JsonNode.Parse(expected);
        var a = JsonNode.Parse(actual!);
        Assert.True(JsonNode.DeepEquals(e, a), $"Expected {expected} but was {actual}");
    }

    public static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json!).RootElement.Clone();
    }
}
