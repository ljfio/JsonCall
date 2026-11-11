using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;

namespace JsonCall.Server;

/// <summary>Runs before a method is invoked. Return an error to reject the call.</summary>
public interface IJsonRpcCallFilter
{
    ValueTask<JsonRpcError?> OnInvokingAsync(RegisteredMethod method, JsonRpcCallContext context);
}

public sealed record ContractRegistration(Type Contract, Type? Implementation, object? Instance);

public sealed record DelegateRegistration(string Name, Delegate Handler, IReadOnlyList<object>? Metadata = null,
    bool AllowReserved = false);

public sealed class JsonRpcServerOptions
{
    private readonly List<ContractRegistration> _contracts = new();
    private readonly List<DelegateRegistration> _delegates = new();

    public JsonSerializerOptions SerializerOptions { get; set; } = JsonRpcSerializer.DefaultOptions;

    public IList<IJsonRpcCallFilter> Filters { get; } = new List<IJsonRpcCallFilter>();

    /// <summary>Called for exceptions that are mapped to an internal error.</summary>
    public Action<Exception>? OnUnhandledException { get; set; }

    /// <summary>Library modules may register methods in the reserved "rpc." namespace.</summary>
    public bool AllowReservedNames { get; set; }

    public IReadOnlyList<ContractRegistration> Contracts => _contracts;

    public IReadOnlyList<DelegateRegistration> Delegates => _delegates;

    /// <summary>Registers a contract whose implementation is resolved from the service provider on each call.</summary>
    public JsonRpcServerOptions AddContract<TContract>() where TContract : class
    {
        _contracts.Add(new ContractRegistration(typeof(TContract), null, null));
        return this;
    }

    /// <summary>Registers a contract with an implementation type (registered in DI by the host).</summary>
    public JsonRpcServerOptions AddContract<TContract, TImplementation>()
        where TContract : class where TImplementation : class, TContract
    {
        _contracts.Add(new ContractRegistration(typeof(TContract), typeof(TImplementation), null));
        return this;
    }

    /// <summary>Registers a contract served by a fixed instance.</summary>
    public JsonRpcServerOptions AddContract<TContract>(TContract instance) where TContract : class
    {
        _contracts.Add(new ContractRegistration(typeof(TContract), null, instance));
        return this;
    }

    /// <summary>Registers a single method implemented by a delegate.</summary>
    public JsonRpcServerOptions AddMethod(string name, Delegate handler)
    {
        EnsureNotReserved(name);
        _delegates.Add(new DelegateRegistration(name, handler));
        return this;
    }

    /// <summary>
    /// Registers a method in the reserved "rpc." namespace. Intended for library modules such as OpenRPC discovery.
    /// <paramref name="metadata"/> (for example an <c>AuthorizeAttribute</c>) is treated like attributes on the method.
    /// </summary>
    public JsonRpcServerOptions AddReservedMethod(string name, Delegate handler, IEnumerable<object>? metadata = null)
    {
        _delegates.Add(new DelegateRegistration(name, handler, metadata?.ToArray(), AllowReserved: true));
        return this;
    }

    internal void EnsureNotReserved(string name)
    {
        if (!AllowReservedNames && name.StartsWith("rpc.", StringComparison.Ordinal))
            throw new ArgumentException($"The method name '{name}' is invalid: the \"rpc.\" prefix is reserved.", nameof(name));
    }

    public JsonRpcServerOptions Clone()
    {
        var clone = new JsonRpcServerOptions
        {
            SerializerOptions = SerializerOptions,
            OnUnhandledException = OnUnhandledException,
            AllowReservedNames = AllowReservedNames
        };

        clone._contracts.AddRange(_contracts);
        clone._delegates.AddRange(_delegates);

        foreach (var filter in Filters)
            clone.Filters.Add(filter);

        return clone;
    }
}
