namespace JsonCall.Core.Model;

public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>Generic server error.</summary>
    public const int ServerError = -32000;

    /// <summary>The method streams its result but the transport/client cannot receive a stream.</summary>
    public const int StreamRequired = -32001;

    /// <summary>The caller is not authenticated.</summary>
    public const int Unauthorized = -32002;

    /// <summary>The caller is authenticated but not permitted to call the method.</summary>
    public const int Forbidden = -32003;

    /// <summary>The server is shutting down.</summary>
    public const int ShuttingDown = -32005;

    /// <summary>The request was cancelled by the caller.</summary>
    public const int RequestCancelled = -32800;

    public static bool IsReserved(int code) => code is >= -32768 and <= -32000;
}
