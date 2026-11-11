using JsonCall.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace JsonCall.AspNetCore;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a JSON-RPC endpoint serving HTTP POST (single and batch), Server-Sent Events for streaming
    /// methods and WebSocket upgrades on the same route.
    /// </summary>
    public static IEndpointConventionBuilder MapJsonRpc(this IEndpointRouteBuilder endpoints, string pattern,
        Action<JsonRpcEndpointOptions>? configure = null)
    {
        var services = endpoints.ServiceProvider;
        var endpointOptions = new JsonRpcEndpointOptions();
        configure?.Invoke(endpointOptions);

        var server = services.GetRequiredService<JsonRpcServer>();

        if (endpointOptions.Methods.Count > 0)
        {
            var extended = server.Options.Clone();

            foreach (var (name, handler) in endpointOptions.Methods)
                extended.AddMethod(name, handler);

            server = new JsonRpcServer(extended, services.GetRequiredService<IServiceScopeFactory>());
        }

        var endpoint = new JsonRpcEndpoint(server, services.GetRequiredService<JsonRpcHostOptions>(),
            endpointOptions, services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());

        var pipeline = endpoints.CreateApplicationBuilder();
        pipeline.UseWebSockets();
        pipeline.Run(endpoint.InvokeAsync);

        return endpoints.Map(pattern, pipeline.Build())
            .WithDisplayName("JSON-RPC")
            .WithMetadata(new JsonRpcEndpointMetadata(server.Methods.Select(m => m.Name).OrderBy(n => n).ToArray()));
    }
}
