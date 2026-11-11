using System.Reflection;

namespace JsonCall.Core;

public enum ResultKind
{
    /// <summary>No result value.</summary>
    Void,

    /// <summary>A single value.</summary>
    Value,

    /// <summary>An IAsyncEnumerable of items.</summary>
    Stream
}

/// <summary>Reflection description of a contract method.</summary>
public sealed class ContractMethod
{
    public required MethodInfo Method { get; init; }

    public required string Name { get; init; }

    public required bool IsNotification { get; init; }

    /// <summary>The wire-visible parameters (CancellationToken excluded).</summary>
    public required ParameterInfo[] Parameters { get; init; }

    public required ResultKind Kind { get; init; }

    /// <summary>The value type (<see cref="ResultKind.Value"/>) or item type (<see cref="ResultKind.Stream"/>).</summary>
    public Type? ResultType { get; init; }

    /// <summary>The declared return type is a ValueTask or ValueTask&lt;T&gt;.</summary>
    public bool ReturnsValueTask { get; init; }

    /// <summary>The declared return type is a Task or Task&lt;T&gt;, or a ValueTask.</summary>
    public bool ReturnsAwaitable { get; init; }

    /// <summary>The declared return type is Task&lt;IAsyncEnumerable&lt;T&gt;&gt;.</summary>
    public bool StreamIsAwaited { get; init; }
}

public static class ContractReflection
{
    /// <summary>The default wire name: camelCase, with a trailing "Async" removed.</summary>
    public static string DefaultName(string clrName)
    {
        var name = clrName.EndsWith("Async", StringComparison.Ordinal) && clrName.Length > 5
            ? clrName[..^5]
            : clrName;

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static IReadOnlyList<ContractMethod> GetMethods(Type contractType)
    {
        var types = new List<Type> { contractType };
        types.AddRange(contractType.GetInterfaces());

        var methods = new List<ContractMethod>();

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                    continue;

                methods.Add(Describe(method));
            }
        }

        return methods;
    }

    public static JsonRpcParamStyle GetParamStyle(Type contractType) =>
        contractType.GetCustomAttribute<JsonRpcContractAttribute>()?.ParamStyle ?? JsonRpcParamStyle.ByPosition;

    public static ContractMethod Describe(MethodInfo method)
    {
        var name = method.GetCustomAttribute<JsonRpcMethodAttribute>()?.Name ?? DefaultName(method.Name);
        var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
        var returnType = method.ReturnType;

        var kind = ResultKind.Value;
        Type? resultType = null;
        var valueTask = false;
        var awaitable = false;
        var streamAwaited = false;

        if (returnType == typeof(void))
        {
            kind = ResultKind.Void;
        }
        else if (returnType == typeof(Task))
        {
            kind = ResultKind.Void;
            awaitable = true;
        }
        else if (returnType == typeof(ValueTask))
        {
            kind = ResultKind.Void;
            awaitable = valueTask = true;
        }
        else
        {
            var inner = returnType;

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                inner = returnType.GetGenericArguments()[0];
                awaitable = true;
            }
            else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                inner = returnType.GetGenericArguments()[0];
                awaitable = valueTask = true;
            }

            if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            {
                kind = ResultKind.Stream;
                resultType = inner.GetGenericArguments()[0];
                streamAwaited = awaitable;
            }
            else
            {
                resultType = inner;
            }
        }

        var notification = method.GetCustomAttribute<JsonRpcNotificationAttribute>() is not null;

        return new ContractMethod
        {
            Method = method,
            Name = name,
            IsNotification = notification,
            Parameters = parameters,
            Kind = kind,
            ResultType = resultType,
            ReturnsValueTask = valueTask,
            ReturnsAwaitable = awaitable,
            StreamIsAwaited = streamAwaited
        };
    }
}
