using System.Text.Json;

namespace JsonCall.Core.Model;

/// <summary>A JSON-RPC error, thrown by clients for error responses and by handlers to return one.</summary>
public class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message, object? data = null) : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public int Code { get; }

    /// <summary>The error's data member. A <see cref="JsonElement"/> when received from a peer.</summary>
    public object? ErrorData { get; }
}

/// <summary>An application error raised by a handler. The code must be outside the reserved range.</summary>
public sealed class JsonRpcApplicationException : JsonRpcException
{
    public JsonRpcApplicationException(int code, string message, object? data = null)
        : base(CheckCode(code), message, data)
    {
    }

    private static int CheckCode(int code) =>
        JsonRpcErrorCodes.IsReserved(code)
            ? throw new ArgumentOutOfRangeException(nameof(code), code,
                "Application error codes must be outside the reserved range -32768 to -32000.")
            : code;
}

/// <summary>The connection was lost or closed while calls were in flight.</summary>
public sealed class ConnectionLostException : IOException
{
    public ConnectionLostException(string message = "The JSON-RPC connection was lost.", Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>A transport frame was malformed.</summary>
public sealed class TransportFramingException : IOException
{
    public TransportFramingException(string message) : base(message)
    {
    }
}

/// <summary>An incoming message exceeded the configured maximum size.</summary>
public sealed class MessageTooLargeException : IOException
{
    public MessageTooLargeException(long limit) : base($"The message exceeds the maximum size of {limit} bytes.")
    {
    }
}

/// <summary>Wire bytes could not be decoded by a serializer.</summary>
public sealed class JsonRpcParseException : Exception
{
    public JsonRpcParseException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
