using System.Text.Json;

namespace JsonCall.Core.Model;

public sealed class JsonRpcRequest
{
    public JsonRpcRequest(string method, JsonElement? @params, JsonRpcId? id)
    {
        Method = method;
        Params = @params;
        Id = id;
    }

    public string Method { get; }

    /// <summary>The structured params (array or object), or null when omitted.</summary>
    public JsonElement? Params { get; }

    /// <summary>The id, or null for a notification.</summary>
    public JsonRpcId? Id { get; }

    public bool IsNotification => Id is null;
}
