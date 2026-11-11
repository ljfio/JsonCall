using System.Text;
using JsonCall.Core;
using JsonCall.Core.Model;
using MessagePack;

namespace JsonCall.Serializer.MessagePack;

/// <summary>MessagePack wire format, transcoded from and to the JSON data model.</summary>
public sealed class MessagePackMessageSerializer : IMessageSerializer
{
    public string ContentType => "application/msgpack";

    public bool IsBinary => true;

    public byte[] FromJson(ReadOnlySpan<byte> json) =>
        MessagePackSerializer.ConvertFromJson(Encoding.UTF8.GetString(json));

    public byte[] ToJson(ReadOnlySpan<byte> wire)
    {
        try
        {
            return Encoding.UTF8.GetBytes(MessagePackSerializer.ConvertToJson(wire.ToArray()));
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or InvalidOperationException
                                       or ArgumentException or EndOfStreamException)
        {
            throw new JsonRpcParseException("Malformed MessagePack: " + ex.Message, ex);
        }
    }
}
