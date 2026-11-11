using System.Text.Json.Serialization;

namespace JsonCall.Core.Model;

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }
}