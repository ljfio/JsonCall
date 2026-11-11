using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Serializer.Cbor.Converters;
using Dahomey.Cbor;

namespace JsonCall.Serializer.Cbor;

public class CborMessageSerializer : IMessageSerializer
{
    private static readonly CborOptions _options = new();

    static CborMessageSerializer()
    {
        _options.Registry.ConverterRegistry.RegisterConverter(typeof(JsonRpcParams), new JsonRpcParamsConverter());
    }
    
    public async Task SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        await Dahomey.Cbor.Cbor.SerializeAsync(value, stream, _options, cancellationToken);
    }

    public async Task<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        return await Dahomey.Cbor.Cbor.DeserializeAsync<T>(stream, _options, cancellationToken);
    }
}