namespace JsonCall.Core;

public enum JsonRpcParamStyle
{
    ByPosition,
    ByName,

    /// <summary>
    /// Documents that both styles are accepted (OpenRPC "either"). The proxy sends params by position.
    /// </summary>
    Either
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

/// <summary>Documents an application error a method can return (OpenRPC <c>errors</c>).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class JsonRpcErrorAttribute : Attribute
{
    public JsonRpcErrorAttribute(int code, string message)
    {
        Code = code;
        Message = message;
    }

    public int Code { get; }

    public string Message { get; }
}

/// <summary>Adds an OpenRPC tag to a method, or to every method of a contract.</summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true)]
public sealed class JsonRpcTagAttribute : Attribute
{
    public JsonRpcTagAttribute(string name) => Name = name;

    public string Name { get; }
}
