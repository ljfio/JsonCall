using JsonCall.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JsonCall.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds JSON-RPC services. Contract implementations passed to <c>AddContract&lt;TContract, TImplementation&gt;</c>
    /// are registered as scoped services unless already registered.
    /// </summary>
    public static IServiceCollection AddJsonRpc(this IServiceCollection services,
        Action<JsonRpcServerOptions>? configure = null, Action<JsonRpcHostOptions>? configureHost = null)
    {
        var options = new JsonRpcServerOptions();
        configure?.Invoke(options);

        foreach (var registration in options.Contracts)
        {
            if (registration.Implementation is { } implementation)
                services.TryAddScoped(registration.Contract, implementation);
        }

        var hostOptions = new JsonRpcHostOptions();
        configureHost?.Invoke(hostOptions);

        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton(hostOptions);
        services.AddSingleton(sp =>
        {
            var serverOptions = sp.GetRequiredService<JsonRpcServerOptions>().Clone();
            serverOptions.Filters.Add(new JsonRpcAuthorizationFilter());
            return new JsonRpcServer(serverOptions, sp.GetRequiredService<IServiceScopeFactory>());
        });
        services.AddHostedService<JsonRpcStartupValidator>();

        return services;
    }
}

/// <summary>Fails application start-up for configuration errors instead of failing at the first call.</summary>
internal sealed class JsonRpcStartupValidator : IHostedService
{
    private readonly IServiceProvider _services;

    public JsonRpcStartupValidator(IServiceProvider services) => _services = services;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Building the server detects duplicate method names.
        _ = _services.GetRequiredService<JsonRpcServer>();

        var isService = _services.GetRequiredService<IServiceProviderIsService>();

        foreach (var registration in _services.GetRequiredService<JsonRpcServerOptions>().Contracts)
        {
            if (registration.Instance is null && !isService.IsService(registration.Contract))
            {
                throw new InvalidOperationException(
                    $"The JSON-RPC contract {registration.Contract.FullName} has no implementation registered. " +
                    "Use AddContract<TContract, TImplementation>() or register the implementation with the service collection.");
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
