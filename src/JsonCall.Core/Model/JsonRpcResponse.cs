using System.Text.Json.Serialization;

namespace JsonCall.Core.Model;

public class JsonRpcResponse<T> : JsonRpcObjectBase 
{
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonRpcError? Error { get; set; }
    
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public T? Result { get; set; }
}