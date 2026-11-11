using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace JsonCall.Server;

/// <summary>Raised when params cannot be bound to a method's parameters.</summary>
internal sealed class InvalidParamsException : Exception
{
    public InvalidParamsException(string message) : base(message)
    {
    }
}

/// <summary>
/// Transport-neutral dispatcher: a method table, parameter binding, error mapping and stream producers.
/// </summary>
public sealed class JsonRpcServer : IJsonRpcHandler
{
    private static readonly NullabilityInfoContext Nullability = new();

    private readonly Dictionary<string, RegisteredMethod> _methods = new(StringComparer.Ordinal);
    private readonly JsonRpcServerOptions _options;
    private readonly IServiceScopeFactory? _scopeFactory;

    public JsonRpcServer(JsonRpcServerOptions options, IServiceScopeFactory? scopeFactory = null)
    {
        _options = options;
        _scopeFactory = scopeFactory;

        foreach (var registration in options.Contracts)
            RegisterContract(registration);

        foreach (var registration in options.Delegates)
            Register(
                new RegisteredMethod(
                    WithName(ContractReflection.Describe(registration.Handler.Method), registration.Name),
                    "delegate '" + registration.Name + "'", null, null, null, registration.Handler)
                {
                    Metadata = registration.Metadata ?? Array.Empty<object>()
                },
                registration.AllowReserved);
    }

    public IReadOnlyCollection<RegisteredMethod> Methods => _methods.Values;

    public JsonRpcServerOptions Options => _options;

    public bool TryGetMethod(string name, out RegisteredMethod method) => _methods.TryGetValue(name, out method!);

    private static ContractMethod WithName(ContractMethod d, string name) => new()
    {
        Method = d.Method,
        Name = name,
        IsNotification = d.IsNotification,
        Parameters = d.Parameters,
        Kind = d.Kind,
        ResultType = d.ResultType,
        ReturnsValueTask = d.ReturnsValueTask,
        ReturnsAwaitable = d.ReturnsAwaitable,
        StreamIsAwaited = d.StreamIsAwaited
    };

    private void RegisterContract(ContractRegistration registration)
    {
        foreach (var description in ContractReflection.GetMethods(registration.Contract))
        {
            _options.EnsureNotReserved(description.Name);

            Register(new RegisteredMethod(description, registration.Contract.FullName ?? registration.Contract.Name,
                registration.Contract, registration.Implementation ?? registration.Instance?.GetType(),
                registration.Instance, null));
        }
    }

    private void Register(RegisteredMethod method, bool allowReserved = false)
    {
        if (!allowReserved)
            _options.EnsureNotReserved(method.Name);

        if (_methods.TryGetValue(method.Name, out var existing))
        {
            throw new InvalidOperationException(
                $"The JSON-RPC method '{method.Name}' is declared by both {existing.Owner} and {method.Owner}.");
        }

        _methods[method.Name] = method;
    }

    /// <summary>Serves a transport: starts a peer that dispatches incoming requests to this server.</summary>
    public JsonRpcPeer Attach(IMessageTransport transport, IMessageSerializer? serializer = null,
        Action<JsonRpcPeerOptions>? configure = null)
    {
        var options = new JsonRpcPeerOptions { Handler = this, SerializerOptions = _options.SerializerOptions };

        if (serializer is not null)
            options.Serializer = serializer;

        configure?.Invoke(options);

        var peer = new JsonRpcPeer(transport, options);
        peer.Start();
        return peer;
    }

    public async ValueTask<JsonRpcReply?> HandleAsync(JsonRpcRequest request, JsonRpcCallContext context)
    {
        if (!_methods.TryGetValue(request.Method, out var method))
        {
            return request.IsNotification
                ? null
                : JsonRpcReply.FromError(JsonRpcErrorCodes.MethodNotFound, "Method not found");
        }

        if (request.IsNotification && method.IsStream)
            return null;

        if (method.IsStream && !context.StreamsAllowed)
        {
            return JsonRpcReply.FromError(JsonRpcErrorCodes.StreamRequired,
                $"The method '{method.Name}' streams its result. Call it over a transport that supports streams.");
        }

        try
        {
            if (context.Services is null && _scopeFactory is not null)
            {
                var scope = _scopeFactory.CreateAsyncScope();
                context.Services = scope.ServiceProvider;
                context.OnCompleted(() => scope.DisposeAsync());
            }

            foreach (var filter in _options.Filters)
            {
                if (await filter.OnInvokingAsync(method, context).ConfigureAwait(false) is { } rejection)
                    return request.IsNotification ? null : JsonRpcReply.FromError(rejection);
            }

            var arguments = Bind(method, context);
            var target = ResolveTarget(method, context);
            var returned = method.Description.Method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, arguments, null);

            if (method.IsStream)
            {
                var source = method.Description.StreamIsAwaited
                    ? await AwaitValueAsync(returned, method.Description).ConfigureAwait(false)
                    : returned;

                if (source is null)
                    return JsonRpcReply.FromResult(JsonSerializer.SerializeToElement<object?>(null));

                var adapt = typeof(JsonRpcServer).GetMethod(nameof(AdaptStream), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(method.Description.ResultType!);

                var items = (IAsyncEnumerable<JsonElement>)adapt.Invoke(null,
                    new[] { source, _options.SerializerOptions, CancellationToken.None })!;

                return JsonRpcReply.FromStream(items, MapException);
            }

            var value = await AwaitValueAsync(returned, method.Description).ConfigureAwait(false);

            if (request.IsNotification)
                return null;

            return JsonRpcReply.FromResult(method.Description.Kind == ResultKind.Void
                ? JsonSerializer.SerializeToElement<object?>(null)
                : JsonSerializer.SerializeToElement(value, method.Description.ResultType!, _options.SerializerOptions));
        }
        catch (Exception ex)
        {
            if (request.IsNotification)
                return null;

            return JsonRpcReply.FromError(MapException(ex));
        }
    }

    public JsonRpcError MapException(Exception exception)
    {
        switch (exception)
        {
            case JsonRpcException rpc:
                return new JsonRpcError(rpc.Code, rpc.Message, ToElement(rpc.ErrorData));
            case InvalidParamsException invalid:
                return new JsonRpcError(JsonRpcErrorCodes.InvalidParams, invalid.Message);
            case OperationCanceledException:
                return new JsonRpcError(JsonRpcErrorCodes.RequestCancelled, "Request cancelled");
            default:
                _options.OnUnhandledException?.Invoke(exception);
                return new JsonRpcError(JsonRpcErrorCodes.InternalError, "Internal error");
        }
    }

    private JsonElement? ToElement(object? data) => data switch
    {
        null => null,
        JsonElement element => element.Clone(),
        _ => JsonSerializer.SerializeToElement(data, data.GetType(), _options.SerializerOptions)
    };

    private object? ResolveTarget(RegisteredMethod method, JsonRpcCallContext context)
    {
        if (method.Delegate is { } @delegate)
            return @delegate.Target;

        if (method.Instance is { } instance)
            return instance;

        var contract = method.ContractType!;

        return context.Services?.GetService(contract)
               ?? throw new InvalidOperationException(
                   $"No implementation of {contract.FullName} is registered with the service provider.");
    }

    private object?[] Bind(RegisteredMethod method, JsonRpcCallContext context)
    {
        var parameters = method.Description.Method.GetParameters();
        var arguments = new object?[parameters.Length];
        var options = _options.SerializerOptions;
        var @params = context.Request.Params;

        var wireParameters = parameters.Where(p => !IsInjected(p)).ToArray();

        Dictionary<string, JsonElement>? named = null;
        JsonElement[]? positional = null;

        if (@params is { ValueKind: JsonValueKind.Array } array)
        {
            positional = array.EnumerateArray().ToArray();

            if (positional.Length > wireParameters.Length && !HasParamArray(wireParameters))
            {
                throw new InvalidParamsException(
                    $"Too many params: '{method.Name}' takes {wireParameters.Length} but {positional.Length} were given.");
            }
        }
        else if (@params is { ValueKind: JsonValueKind.Object } obj)
        {
            named = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in obj.EnumerateObject())
                named[property.Name] = property.Value;

            foreach (var key in named.Keys)
            {
                if (wireParameters.All(p => !string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidParamsException($"Unknown param '{key}' for '{method.Name}'.");
            }
        }

        var position = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                arguments[i] = context.CancellationToken;
                continue;
            }

            if (parameter.ParameterType == typeof(JsonRpcCallContext))
            {
                arguments[i] = context;
                continue;
            }

            JsonElement? supplied = null;

            if (positional is not null && IsParamArray(parameter) && parameter == wireParameters[^1])
            {
                var elementType = parameter.ParameterType.GetElementType()!;
                var rest = positional.Skip(position).ToArray();
                var values = Array.CreateInstance(elementType, rest.Length);

                try
                {
                    for (var k = 0; k < rest.Length; k++)
                        values.SetValue(rest[k].Deserialize(elementType, options), k);
                }
                catch (JsonException ex)
                {
                    throw new InvalidParamsException($"Param '{parameter.Name}' is invalid: {ex.Message}");
                }

                arguments[i] = values;
                position = positional.Length;
                continue;
            }

            if (positional is not null)
            {
                if (position < positional.Length)
                    supplied = positional[position];
            }
            else if (named is not null && parameter.Name is { } name && named.TryGetValue(name, out var value))
            {
                supplied = value;
            }

            position++;

            if (supplied is null)
            {
                if (parameter.HasDefaultValue)
                    arguments[i] = parameter.DefaultValue is DBNull ? null : parameter.DefaultValue;
                else if (IsNullable(parameter))
                    arguments[i] = null;
                else
                    throw new InvalidParamsException($"Missing required param '{parameter.Name}' for '{method.Name}'.");

                continue;
            }

            try
            {
                arguments[i] = supplied.Value.Deserialize(parameter.ParameterType, options);

                if (arguments[i] is null && !IsNullable(parameter))
                    throw new InvalidParamsException($"Param '{parameter.Name}' must not be null.");
            }
            catch (JsonException ex)
            {
                throw new InvalidParamsException($"Param '{parameter.Name}' is invalid: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                throw new InvalidParamsException($"Param '{parameter.Name}' is invalid: {ex.Message}");
            }
        }

        return arguments;
    }

    private static bool IsParamArray(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<ParamArrayAttribute>() is not null && parameter.ParameterType.IsArray;

    private static bool HasParamArray(ParameterInfo[] wireParameters) =>
        wireParameters.Length > 0 && IsParamArray(wireParameters[^1]);

    private static bool IsInjected(ParameterInfo parameter) =>
        parameter.ParameterType == typeof(CancellationToken) || parameter.ParameterType == typeof(JsonRpcCallContext);

    private static bool IsNullable(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;

        if (Nullable.GetUnderlyingType(type) is not null)
            return true;

        if (type.IsValueType)
            return false;

        lock (Nullability)
            return Nullability.Create(parameter).WriteState != NullabilityState.NotNull;
    }

    private static async ValueTask<object?> AwaitValueAsync(object? returned, ContractMethod description)
    {
        switch (returned)
        {
            case null:
                return null;

            case Task task:
                await task.ConfigureAwait(false);
                return description.Kind == ResultKind.Void && !description.StreamIsAwaited
                    ? null
                    : task.GetType().GetProperty("Result")!.GetValue(task);

            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;

            default:
                if (description.ReturnsValueTask)
                {
                    var asTask = (Task)returned.GetType().GetMethod("AsTask")!.Invoke(returned, null)!;
                    await asTask.ConfigureAwait(false);
                    return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
                }

                return returned;
        }
    }

    private static async IAsyncEnumerable<JsonElement> AdaptStream<T>(IAsyncEnumerable<T> source,
        JsonSerializerOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return JsonSerializer.SerializeToElement(item, options);
    }
}
