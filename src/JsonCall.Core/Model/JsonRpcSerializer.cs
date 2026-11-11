using System.Text.Json;
using System.Text.Json.Serialization;

namespace JsonCall.Core.Model;

public static class JsonRpcSerializer
{
    /// <summary>Default System.Text.Json options (web defaults, string enums).</summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
