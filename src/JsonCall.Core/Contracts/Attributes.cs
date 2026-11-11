namespace JsonCall.Core;

public enum JsonRpcParamStyle
{
    ByPosition,
    ByName
}

/// <summary>Marks an interface as a JSON-RPC contract.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class JsonRpcContractAttribute : Attribute
{
    public string? Title { get; set; }

    public string? Version { get; set; }

    /// <summary>How the proxy sends params. Servers accept both.</summary>
    public JsonRpcParamStyle ParamStyle { get; set; } = JsonRpcParamStyle.ByPosition;
}

/// <summary>Overrides the wire name of a contract method.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class JsonRpcMethodAttribute : Attribute
{
    public JsonRpcMethodAttribute(string name) => Name = name;

    public string Name { get; }
}

/// <summary>Marks a contract method as a notification (no response).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class JsonRpcNotificationAttribute : Attribute
{
}
