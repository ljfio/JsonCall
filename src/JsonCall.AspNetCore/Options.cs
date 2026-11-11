using JsonCall.Core;

namespace JsonCall.AspNetCore;

[Flags]
public enum JsonRpcTransports
{
    None = 0,
    Http = 1,
    WebSocket = 2,
    All = Http | WebSocket
}

/// <summary>Host wide settings.</summary>
public sealed class JsonRpcHostOptions
{
    private readonly Dictionary<string, IMessageSerializer> _serializers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["json"] = JsonMessageSerializer.Instance
    };

    public IReadOnlyDictionary<string, IMessageSerializer> Serializers => _serializers;

    /// <summary>Largest accepted WebSocket message.</summary>
    public int MaxWebSocketMessageSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>Largest accepted HTTP request body.</summary>
    public int MaxRequestBodySize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Registers a wire format. HTTP selects it by Content-Type; WebSocket by the sub-protocol "jsoncall.{name}".
    /// </summary>
    public JsonRpcHostOptions AddSerializer(string name, IMessageSerializer serializer)
    {
        _serializers[name] = serializer;
        return this;
    }

    internal IMessageSerializer? FindByContentType(string? contentType) =>
        contentType is null
            ? null
            : _serializers.Values.FirstOrDefault(s => string.Equals(s.ContentType, contentType, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Per-endpoint settings.</summary>
public sealed class JsonRpcEndpointOptions
{
    internal List<(string Name, Delegate Handler)> Methods { get; } = new();

    public JsonRpcTransports Transports { get; set; } = JsonRpcTransports.All;

    /// <summary>Adds a method, implemented by a delegate, to this endpoint only.</summary>
    public JsonRpcEndpointOptions Method(string name, Delegate handler)
    {
        Methods.Add((name, handler));
        return this;
    }
}

/// <summary>Endpoint metadata describing the JSON-RPC methods served by an endpoint.</summary>
public sealed record JsonRpcEndpointMetadata(IReadOnlyList<string> Methods);
