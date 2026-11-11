using System.Text.Json;

namespace JsonCall.Core.Model;

public sealed class JsonRpcResponse
{
    private JsonRpcResponse(JsonRpcId id, JsonElement? result, JsonRpcError? error)
    {
        Id = id;
        Result = result;
        Error = error;
    }

    public JsonRpcId Id { get; }

    public JsonElement? Result { get; }

    public JsonRpcError? Error { get; }

    public static JsonRpcResponse Success(JsonRpcId id, JsonElement result) => new(id, result, null);

    public static JsonRpcResponse Failure(JsonRpcId id, JsonRpcError error) => new(id, null, error);

    public static JsonRpcResponse Failure(JsonRpcId id, int code, string message) =>
        new(id, null, new JsonRpcError(code, message));
}
