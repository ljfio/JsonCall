using System.Text.Unicode;
using JsonCall.Core.Model;

namespace JsonCall.Core;

/// <summary>The default text JSON wire format.</summary>
public sealed class JsonMessageSerializer : IMessageSerializer
{
    public static JsonMessageSerializer Instance { get; } = new();

    public string ContentType => "application/json";

    public bool IsBinary => false;

    public byte[] FromJson(ReadOnlySpan<byte> json) => json.ToArray();

    public byte[] ToJson(ReadOnlySpan<byte> wire) =>
        Utf8.IsValid(wire) ? wire.ToArray() : throw new JsonRpcParseException("The message is not valid UTF-8.");
}
