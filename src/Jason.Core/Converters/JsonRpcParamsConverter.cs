using System.Text.Json;
using System.Text.Json.Serialization;
using Jason.Core.Model;

namespace Jason.Core.Converters;

public class JsonRpcParamsConverter : JsonConverter<JsonRpcParams>
{
    public override JsonRpcParams? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartArray)
        {
            var array = JsonSerializer.Deserialize<IEnumerable<object>>(ref reader);

            return new JsonRpcParams(array);
        }
        else if (reader.TokenType is JsonTokenType.StartObject)
        {
            var map = JsonSerializer.Deserialize<IDictionary<string, object>>(ref reader);

            return new JsonRpcParams(map);
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, JsonRpcParams value, JsonSerializerOptions options)
    {
        if (value.Type == JsonRpcParamsType.ByPosition && value is IEnumerable<object> collection)
        {
            writer.WriteStartArray();

            foreach (var item in collection)
            {
                writer.WriteRawValue(JsonSerializer.Serialize(item));
            }

            writer.WriteEndArray();
        }
        else if (value.Type == JsonRpcParamsType.ByName && value is IDictionary<string, object> dictionary)
        {
            writer.WriteStartObject();

            foreach (var item in dictionary)
            {
                writer.WritePropertyName(item.Key);
                writer.WriteRawValue(JsonSerializer.Serialize(item.Value));
            }
            
            writer.WriteEndObject();
        }

        writer.Flush();
    }
}