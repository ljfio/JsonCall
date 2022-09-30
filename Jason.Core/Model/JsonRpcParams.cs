using System.Text.Json.Serialization;

namespace Jason.Core.Model;

[JsonConverter(typeof(JsonRpcParamsConverter))]
public class JsonRpcParams
{
    internal JsonRpcParamsType Type { get; }

    internal IDictionary<string, object> ByNameValue { get; }
    
    internal IEnumerable<object> ByPositionValue { get; }

    internal JsonRpcParams(IDictionary<string, object> value)
    {
        Type = JsonRpcParamsType.ByName;
        ByNameValue = value;
    }

    internal JsonRpcParams(IEnumerable<object> value)
    {
        Type = JsonRpcParamsType.ByPosition;
        ByPositionValue = value;
    }
}