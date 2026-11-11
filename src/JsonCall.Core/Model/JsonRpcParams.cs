using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonCall.Core.Model;

public enum JsonRpcParamsType
{
    ByPosition,
    ByName
}

/// <summary>Structured request params, by position (array) or by name (object).</summary>
public readonly struct JsonRpcParams
{
    private JsonRpcParams(JsonNode node, JsonRpcParamsType type)
    {
        Node = node;
        Type = type;
    }

    public JsonNode Node { get; }

    public JsonRpcParamsType Type { get; }

    public static JsonRpcParams ByPosition(params object?[] values) =>
        ByPosition(JsonRpcSerializer.DefaultOptions, values);

    public static JsonRpcParams ByPosition(JsonSerializerOptions options, IEnumerable<object?> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
            array.Add(JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), options));

        return new JsonRpcParams(array, JsonRpcParamsType.ByPosition);
    }

    public static JsonRpcParams ByName(IEnumerable<KeyValuePair<string, object?>> values) =>
        ByName(JsonRpcSerializer.DefaultOptions, values);

    public static JsonRpcParams ByName(JsonSerializerOptions options, IEnumerable<KeyValuePair<string, object?>> values)
    {
        var obj = new JsonObject();

        foreach (var (key, value) in values)
            obj[key] = JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), options);

        return new JsonRpcParams(obj, JsonRpcParamsType.ByName);
    }

    /// <summary>A single value becomes a one element positional array.</summary>
    public static JsonRpcParams Single(object? value) => ByPosition(value);

    /// <summary>
    /// Dictionaries become by-name params, other enumerables become by-position params
    /// and any other value becomes a single positional param.
    /// </summary>
    public static JsonRpcParams From(object? value, JsonSerializerOptions? options = null)
    {
        options ??= JsonRpcSerializer.DefaultOptions;

        return value switch
        {
            IDictionary<string, object?> dictionary => ByName(options, dictionary),
            IEnumerable<KeyValuePair<string, object?>> pairs => ByName(options, pairs),
            string => ByPosition(options, new[] { value }),
            IEnumerable enumerable => ByPosition(options, enumerable.Cast<object?>()),
            _ => ByPosition(options, new[] { value })
        };
    }
}
