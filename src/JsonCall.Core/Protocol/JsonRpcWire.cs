using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonCall.Core.Model;

namespace JsonCall.Core.Protocol;

/// <summary>Reads and writes JSON-RPC message objects as canonical JSON.</summary>
public static class JsonRpcWire
{
    public static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);

        return buffer.WrittenSpan.ToArray();
    }

    public static void WriteRequest(Utf8JsonWriter writer, string method, JsonRpcId? id, JsonRpcParams? @params,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("method", method);

        if (@params is { } p)
        {
            writer.WritePropertyName("params");
            p.Node.WriteTo(writer, options);
        }

        if (id is { } i)
        {
            writer.WritePropertyName("id");
            i.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    public static void WriteRequestNode(Utf8JsonWriter writer, string method, JsonRpcId? id, JsonNode? @params,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("method", method);

        if (@params is not null)
        {
            writer.WritePropertyName("params");
            @params.WriteTo(writer, options);
        }

        if (id is { } i)
        {
            writer.WritePropertyName("id");
            i.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    public static void WriteResponse(Utf8JsonWriter writer, JsonRpcResponse response)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");

        if (response.Error is { } error)
        {
            writer.WritePropertyName("error");
            WriteError(writer, error);
        }
        else
        {
            writer.WritePropertyName("result");

            if (response.Result is { } result)
                result.WriteTo(writer);
            else
                writer.WriteNullValue();
        }

        writer.WritePropertyName("id");
        response.Id.WriteTo(writer);
        writer.WriteEndObject();
    }

    public static void WriteError(Utf8JsonWriter writer, JsonRpcError error)
    {
        writer.WriteStartObject();
        writer.WriteNumber("code", error.Code);
        writer.WriteString("message", error.Message);

        if (error.Data is { } data)
        {
            writer.WritePropertyName("data");
            data.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    public static byte[] SerializeResponse(JsonRpcResponse response) => Write(w => WriteResponse(w, response));

    public static byte[] SerializeResponses(IReadOnlyList<JsonRpcResponse> responses) => Write(w =>
    {
        w.WriteStartArray();

        foreach (var response in responses)
            WriteResponse(w, response);

        w.WriteEndArray();
    });

    public static JsonRpcError ReadError(JsonElement element)
    {
        var code = element.TryGetProperty("code", out var c) && c.TryGetInt32(out var i)
            ? i
            : JsonRpcErrorCodes.InternalError;

        var message = element.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : string.Empty;

        JsonElement? data = element.TryGetProperty("data", out var d) ? d.Clone() : null;

        return new JsonRpcError(code, message, data);
    }
}

/// <summary>A classified incoming message item.</summary>
public abstract record IncomingItem
{
    public sealed record Request(JsonRpcRequest Value) : IncomingItem;

    public sealed record Response(JsonRpcResponse Value) : IncomingItem;

    /// <summary>An item that is not a valid request. <see cref="Id"/> is null when it could not be determined.</summary>
    public sealed record Invalid(JsonRpcId Id, int Code, string Message) : IncomingItem;

    public static bool LooksLikeResponse(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && !element.TryGetProperty("method", out _)
        && (element.TryGetProperty("result", out _) || element.TryGetProperty("error", out _));

    public static IncomingItem Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new Invalid(JsonRpcId.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request");

        if (!element.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String
            || version.GetString() != "2.0")
        {
            return new Invalid(IdOrNull(element), JsonRpcErrorCodes.InvalidRequest,
                "Invalid Request: \"jsonrpc\" must be \"2.0\"");
        }

        if (LooksLikeResponse(element))
        {
            if (!element.TryGetProperty("id", out var responseId) || !JsonRpcId.TryFrom(responseId, out var rid))
                rid = JsonRpcId.Null;

            return element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? new Response(JsonRpcResponse.Failure(rid, JsonRpcWire.ReadError(error)))
                : new Response(JsonRpcResponse.Success(rid,
                    element.TryGetProperty("result", out var result) ? result.Clone() : default));
        }

        if (!element.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            return new Invalid(IdOrNull(element), JsonRpcErrorCodes.InvalidRequest, "Invalid Request: missing method");

        JsonRpcId? id = null;

        if (element.TryGetProperty("id", out var idElement))
        {
            if (!JsonRpcId.TryFrom(idElement, out var parsed))
                return new Invalid(JsonRpcId.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: invalid id");

            id = parsed;
        }

        JsonElement? @params = null;

        if (element.TryGetProperty("params", out var p))
        {
            if (p.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            {
                return new Invalid(id ?? JsonRpcId.Null, JsonRpcErrorCodes.InvalidParams,
                    "Invalid params: params must be an array or an object");
            }

            @params = p;
        }

        return new Request(new JsonRpcRequest(method.GetString()!, @params, id));
    }

    private static JsonRpcId IdOrNull(JsonElement element) =>
        element.TryGetProperty("id", out var id) && JsonRpcId.TryFrom(id, out var parsed) ? parsed : JsonRpcId.Null;
}
