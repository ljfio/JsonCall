using System.Reflection;
using JsonCall.Core;

namespace JsonCall.Server;

/// <summary>A method known to the dispatcher.</summary>
public sealed class RegisteredMethod
{
    internal RegisteredMethod(ContractMethod description, string owner, Type? contractType, Type? implementationType,
        object? instance, Delegate? @delegate)
    {
        Description = description;
        Owner = owner;
        ContractType = contractType;
        ImplementationType = implementationType;
        Instance = instance;
        Delegate = @delegate;
    }

    public ContractMethod Description { get; }

    public string Name => Description.Name;

    /// <summary>A display name of the declaring contract (or "delegate"), used in error messages.</summary>
    public string Owner { get; }

    public Type? ContractType { get; }

    public Type? ImplementationType { get; }

    internal object? Instance { get; }

    internal Delegate? Delegate { get; }

    public bool IsStream => Description.Kind == ResultKind.Stream;

    /// <summary>Attributes on the method, its contract and (when known) the implementation. Used for authorization.</summary>
    public IEnumerable<object> GetAttributes()
    {
        var result = new List<object>();

        result.AddRange(Description.Method.GetCustomAttributes(inherit: true));

        if (Description.Method.DeclaringType is { } declaring)
            result.AddRange(declaring.GetCustomAttributes(inherit: true));

        if (ContractType is not null && ContractType != Description.Method.DeclaringType)
            result.AddRange(ContractType.GetCustomAttributes(inherit: true));

        if (ImplementationType is not null)
        {
            result.AddRange(ImplementationType.GetCustomAttributes(inherit: true));

            var implementation = ImplementationType.GetMethods()
                .FirstOrDefault(m => m.Name == Description.Method.Name
                                     && m.GetParameters().Select(p => p.ParameterType)
                                         .SequenceEqual(Description.Method.GetParameters().Select(p => p.ParameterType)));

            if (implementation is not null)
                result.AddRange(implementation.GetCustomAttributes(inherit: true));
        }

        return result;
    }
}
