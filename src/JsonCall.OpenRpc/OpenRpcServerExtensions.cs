using System.Text.Json;
using JsonCall.Server;

namespace JsonCall.OpenRpc;

public static class OpenRpcServerExtensions
{
    /// <summary>
    /// Serves the OpenRPC document of every registered contract on <c>rpc.discover</c>.
    /// The document is generated on first use and then cached.
    /// </summary>
    public static JsonRpcServerOptions AddOpenRpc(this JsonRpcServerOptions options, Action<OpenRpcOptions>? configure = null)
    {
        var openRpc = new OpenRpcOptions();
        configure?.Invoke(openRpc);

        var gate = new object();
        JsonElement? cached = null;

        JsonElement Discover()
        {
            lock (gate)
            {
                if (cached is { } existing)
                    return existing;

                var generator = new OpenRpcDocumentGenerator(openRpc, options.SerializerOptions);
                var document = generator.Generate(options.Contracts.Select(c => c.Contract));

                cached = JsonDocument.Parse(OpenRpcDocumentGenerator.ToJson(document)).RootElement;
                return cached.Value;
            }
        }

        return options.AddReservedMethod("rpc.discover", Discover, openRpc.Metadata);
    }
}
