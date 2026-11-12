# Transports and wire formats

JsonCall separates three things that most JSON-RPC libraries weld together:

| Seam | Interface | Responsibility |
| --- | --- | --- |
| Protocol | `JsonRpcPeer` | Ids, correlation, batches, cancellation, streams. Works in JSON. |
| Wire format | `IMessageSerializer` | Converts a JSON message to and from wire bytes (JSON, CBOR, MessagePack). |
| Transport | `IMessageTransport` | Moves whole messages. Owns framing. |

Any transport works with any wire format, and adding one never touches protocol code.

## Choosing a transport

| Transport | Create it with | Stateful | Notes |
| --- | --- | --- | --- |
| WebSocket | `WebSocketMessageTransport.ConnectAsync(uri)` | Yes | One message per WebSocket message. Served by `MapJsonRpc`. |
| HTTP | `new HttpMessageTransport(httpClient, uri)` | No | One POST per message. Streams arrive as Server-Sent Events. Served by `MapJsonRpc`. |
| TCP | `TcpMessageTransport.ConnectAsync(host, port)` / `TcpMessageListener` | Yes | Framed byte stream. |
| Unix domain socket | `UnixSocketMessageTransport.ConnectAsync(path)` / `UnixSocketMessageListener` | Yes | Framed byte stream. Local daemons. |
| stdio | `StdioMessageTransport.Launch(startInfo)` / `.FromConsole()` | Yes | Framed byte stream. Language servers and tools launched by their client. |
| Any pair of streams | `new StreamMessageTransport(input, output)` | Yes | Named pipes, SSH channels, anything that is a `Stream`. |
| In memory | `InMemoryTransport.CreatePair()` | Yes | Tests. |

**Stateful** transports hold a connection, so the server can send notifications to the client and call methods on it,
and many calls share one connection. HTTP has no connection to push on: use WebSocket when you need server-initiated
messages.

## Framing on byte streams

TCP, Unix sockets, stdio and `StreamMessageTransport` need to know where one message ends. Both ends must agree.

| `MessageFraming` | On the wire | Use it for |
| --- | --- | --- |
| `ContentLength` (default) | `Content-Length: 42\r\n\r\n` then 42 bytes | Language Server Protocol and Debug Adapter Protocol peers. Any wire format. |
| `Newline` | One message per line, ending `\n` | Model Context Protocol stdio, Agent Client Protocol, and most hand-written daemons. JSON only. |

```csharp
await using var transport = await UnixSocketMessageTransport.ConnectAsync("/run/daemon.sock", MessageFraming.Newline);
await using var client = new JsonRpcClient(transport);

using var listener = new UnixSocketMessageListener("/run/daemon.sock", MessageFraming.Newline);   // server side
server.Attach(await listener.AcceptAsync());
```

With newline framing a reader accepts `\r\n` and skips blank lines. A sender refuses a message that contains a raw
newline, which rules out CBOR and MessagePack: use `ContentLength` with those.

`UnixSocketMessageListener` removes its socket file on dispose, replaces a stale file left by a crashed process, and
refuses a path another listener is serving. Unix socket support is tested on Linux and macOS only.

### Launching a child process

```csharp
await using var transport = StdioMessageTransport.Launch(new ProcessStartInfo("my-language-server"));
transport.DiagnosticsReceived += line => logger.LogDebug("{Line}", line);    // the child's stderr
await using var client = new JsonRpcClient(transport);
```

Disposing the transport kills the process tree.

## Failure behaviour

Every transport reports failure the same way, so client code does not depend on which one is underneath.

| Situation | Exception |
| --- | --- |
| Cannot connect (nothing listening, stale socket file) | `ConnectionLostException` |
| The connection closes or breaks, including mid-message | `ConnectionLostException` on every call in flight |
| A message larger than the limit | `MessageTooLargeException` |
| A malformed frame (for example a `Content-Length` header on a newline connection) | `TransportFramingException` |

All three derive from `IOException`. `JsonRpcPeer.Completion` completes when the connection ends, for any reason.

Over HTTP there is no connection to lose. A request that fails, or a non-JSON-RPC HTTP error such as a `401` from
middleware, surfaces on that call as a `JsonRpcException` with code `-32000`.

## Size limits

| Limit | Default | Set it with |
| --- | --- | --- |
| Byte stream message (TCP, Unix socket, stdio) | 64 MB | `StreamMessageTransport(maxMessageSize: ...)` |
| WebSocket message, client | 64 MB | `WebSocketMessageTransport(maxMessageSize: ...)` |
| WebSocket message, ASP.NET Core | 64 MB | `JsonRpcHostOptions.MaxWebSocketMessageSize` |
| HTTP request body, ASP.NET Core | 4 MB | `JsonRpcHostOptions.MaxRequestBodySize` |

If your server faces untrusted clients, lower the 64 MB defaults. The connect helpers for TCP and Unix sockets do not
take a size; construct a `StreamMessageTransport` over the socket's stream when you need a different limit.

## Wire formats

JSON is built in. CBOR and MessagePack are separate packages; each is a transcoding of the JSON data model, so
contracts, params and results behave identically in all three.

```csharp
using JsonCall.Serializer.Cbor;

await using var client = new JsonRpcClient(transport, new CborMessageSerializer());
```

On a byte stream, both ends are simply constructed with the same serializer (`server.Attach(transport, serializer)`).
Under ASP.NET Core, register the formats once and the endpoint negotiates:

```csharp
builder.Services.AddJsonRpc(
    o => o.AddContract<IOrders, OrderService>(),
    host => host
        .AddSerializer("cbor", new CborMessageSerializer())
        .AddSerializer("messagepack", new MessagePackMessageSerializer()));
```

| Transport | How the format is chosen |
| --- | --- |
| HTTP | The request's `Content-Type`: `application/json`, `application/cbor`, `application/msgpack`. An unknown type is `415`. |
| WebSocket | The sub-protocol `jsoncall.<name>`, for example `jsoncall.cbor`. No sub-protocol means JSON. |

```csharp
var cbor = new CborMessageSerializer();

var transport = await WebSocketMessageTransport.ConnectAsync(uri, cbor,
    ws => ws.Options.AddSubProtocol("jsoncall.cbor"));

await using var client = new JsonRpcClient(transport, cbor);
```

## Broadcasting to every connection

`JsonRpcServer.Attach` returns a peer and does not keep track of it. `JsonRpcConnectionSet` does: it holds the live
peers, drops each one when its connection ends, and sends a notification to all of them or to a subset.

```csharp
var connections = new JsonRpcConnectionSet();
connections.Connected += peer => Console.WriteLine("client connected");

while (true)
    connections.Attach(server, await listener.AcceptAsync());

// elsewhere
await connections.BroadcastAsync("jobFinished", JsonRpcParams.ByPosition(jobId));
await connections.BroadcastAsync("jobFinished", JsonRpcParams.ByPosition(jobId), filter: peer => IsSubscribed(peer));
```

`BroadcastAsync` returns the number of peers that received the notification. A peer whose send fails is dropped from
the set. Use `peer.Items` to remember per-connection state such as subscriptions.

## Writing a transport

Implement two methods. The protocol layer serializes calls to `SendAsync`, and calls `ReceiveAsync` from a single
loop.

```csharp
public interface IMessageTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);   // null: closed cleanly
}
```

Return `null` from `ReceiveAsync` for a clean close and throw `ConnectionLostException` for anything else. If your
medium is a `Stream`, you do not need a transport at all: `StreamMessageTransport` already does the framing.
