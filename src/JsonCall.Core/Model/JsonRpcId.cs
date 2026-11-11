using System.Globalization;
using System.Text.Json;

namespace JsonCall.Core.Model;

/// <summary>A JSON-RPC id (string, number or null), kept as raw JSON so it is echoed verbatim.</summary>
public readonly struct JsonRpcId : IEquatable<JsonRpcId>
{
    private readonly string? _raw;

    private JsonRpcId(string raw) => _raw = raw;

    public static JsonRpcId Null => new("null");

    public static JsonRpcId FromNumber(long value) => new(value.ToString(CultureInfo.InvariantCulture));

    public static JsonRpcId FromString(string value) => new(JsonSerializer.Serialize(value));

    public static bool TryFrom(JsonElement element, out JsonRpcId id)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
            case JsonValueKind.String:
            case JsonValueKind.Null:
                id = new JsonRpcId(element.GetRawText());
                return true;
            default:
                id = Null;
                return false;
        }
    }

    public string RawJson => _raw ?? "null";

    public bool IsNull => RawJson == "null";

    public void WriteTo(Utf8JsonWriter writer) => writer.WriteRawValue(RawJson);

    public bool Equals(JsonRpcId other) => RawJson == other.RawJson;

    public override bool Equals(object? obj) => obj is JsonRpcId other && Equals(other);

    public override int GetHashCode() => RawJson.GetHashCode();

    public override string ToString() => RawJson;
}
