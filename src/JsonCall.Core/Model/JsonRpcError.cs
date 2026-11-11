using System.Text.Json;

namespace JsonCall.Core.Model;

/// <summary>The error object of a JSON-RPC response.</summary>
public sealed class JsonRpcError
{
    public JsonRpcError(int code, string message, JsonElement? data = null)
    {
        Code = code;
        Message = message;
        Data = data;
    }

    public int Code { get; }

    public string Message { get; }

    public JsonElement? Data { get; }
}
