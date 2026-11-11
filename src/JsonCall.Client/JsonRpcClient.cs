using JsonCall.Core;
using JsonCall.Core.Protocol;

namespace JsonCall.Client;

/// <summary>
/// A JSON-RPC client over any <see cref="IMessageTransport"/>. Calls are correlated by id, so any number of calls
/// can be in flight on one connection and responses may arrive in any order.
/// </summary>
public sealed class JsonRpcClient : JsonRpcPeer
{
    public JsonRpcClient(IMessageTransport transport, JsonRpcPeerOptions? options = null)
        : base(transport, options)
    {
        Start();
    }

    public JsonRpcClient(IMessageTransport transport, IMessageSerializer serializer)
        : this(transport, new JsonRpcPeerOptions { Serializer = serializer })
    {
    }

    /// <summary>Creates a typed proxy for a contract interface over this client.</summary>
    public T CreateProxy<T>() where T : class => JsonRpcProxy.Create<T>(this);
}
