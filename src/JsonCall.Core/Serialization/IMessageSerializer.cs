namespace JsonCall.Core;

/// <summary>
/// Converts between a wire format and canonical JSON (UTF-8). The protocol layer always works in JSON,
/// so every wire format is a transcoding of the JSON data model.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>The media type used on HTTP and similar transports.</summary>
    string ContentType { get; }

    /// <summary>True when the wire format is binary (WebSocket binary frames).</summary>
    bool IsBinary { get; }

    /// <summary>Encodes canonical JSON into wire bytes.</summary>
    byte[] FromJson(ReadOnlySpan<byte> json);

    /// <summary>Decodes wire bytes into canonical JSON. Throws <see cref="Model.JsonRpcParseException"/> if malformed.</summary>
    byte[] ToJson(ReadOnlySpan<byte> wire);
}
