using System.Buffers;
using System.Formats.Cbor;
using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;

namespace JsonCall.Serializer.Cbor;

/// <summary>CBOR (RFC 8949) wire format, transcoded from and to the JSON data model.</summary>
public sealed class CborMessageSerializer : IMessageSerializer
{
    public string ContentType => "application/cbor";

    public bool IsBinary => true;

    public byte[] FromJson(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var writer = new CborWriter(CborConformanceMode.Lax);
        WriteElement(writer, document.RootElement);
        return writer.Encode();
    }

    public byte[] ToJson(ReadOnlySpan<byte> wire)
    {
        try
        {
            var reader = new CborReader(wire.ToArray(), CborConformanceMode.Lax);
            var buffer = new ArrayBufferWriter<byte>();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteValue(reader, writer, 0);
            }

            if (reader.BytesRemaining != 0)
                throw new JsonRpcParseException("Unexpected data after the CBOR value.");

            return buffer.WrittenSpan.ToArray();
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or FormatException
                                       or OverflowException or ArgumentException or JsonException)
        {
            throw new JsonRpcParseException("Malformed CBOR: " + ex.Message, ex);
        }
    }

    private static void WriteElement(CborWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartMap(element.EnumerateObject().Count());

                foreach (var property in element.EnumerateObject())
                {
                    writer.WriteTextString(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndMap();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray(element.GetArrayLength());

                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item);

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteTextString(element.GetString()!);
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                    writer.WriteInt64(l);
                else if (element.TryGetUInt64(out var u))
                    writer.WriteUInt64(u);
                else
                    writer.WriteDouble(element.GetDouble());
                break;

            case JsonValueKind.True:
                writer.WriteBoolean(true);
                break;

            case JsonValueKind.False:
                writer.WriteBoolean(false);
                break;

            default:
                writer.WriteNull();
                break;
        }
    }

    private static void WriteValue(CborReader reader, Utf8JsonWriter writer, int depth)
    {
        if (depth > 256)
            throw new JsonRpcParseException("CBOR nesting is too deep.");

        switch (reader.PeekState())
        {
            case CborReaderState.StartMap:
            {
                var length = reader.ReadStartMap();
                writer.WriteStartObject();

                for (var i = 0; length is null ? reader.PeekState() != CborReaderState.EndMap : i < length; i++)
                {
                    if (reader.PeekState() != CborReaderState.TextString)
                        throw new JsonRpcParseException("CBOR map keys must be text strings.");

                    writer.WritePropertyName(reader.ReadTextString());
                    WriteValue(reader, writer, depth + 1);
                }

                reader.ReadEndMap();
                writer.WriteEndObject();
                break;
            }

            case CborReaderState.StartArray:
            {
                var length = reader.ReadStartArray();
                writer.WriteStartArray();

                for (var i = 0; length is null ? reader.PeekState() != CborReaderState.EndArray : i < length; i++)
                    WriteValue(reader, writer, depth + 1);

                reader.ReadEndArray();
                writer.WriteEndArray();
                break;
            }

            case CborReaderState.TextString:
                writer.WriteStringValue(reader.ReadTextString());
                break;

            case CborReaderState.ByteString:
                writer.WriteStringValue(Convert.ToBase64String(reader.ReadByteString()));
                break;

            case CborReaderState.UnsignedInteger:
                writer.WriteNumberValue(reader.ReadUInt64());
                break;

            case CborReaderState.NegativeInteger:
                writer.WriteNumberValue(reader.ReadInt64());
                break;

            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                writer.WriteNumberValue(reader.ReadDouble());
                break;

            case CborReaderState.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;

            case CborReaderState.Null:
                reader.ReadNull();
                writer.WriteNullValue();
                break;

            case CborReaderState.Undefined:
                reader.ReadSimpleValue();
                writer.WriteNullValue();
                break;

            case CborReaderState.Tag:
                reader.ReadTag();
                WriteValue(reader, writer, depth + 1);
                break;

            default:
                throw new JsonRpcParseException("Unsupported or truncated CBOR data.");
        }
    }
}
