# Index

Directory: `capabilities/`

## Concepts

| Title | Type | Description |
|-------|------|-------------|
| [01 Protocol](01-protocol.md) | Capability | JSON-RPC 2.0 conformance: ids, notifications, params, batches, errors. |
| [02 Transports](02-transports.md) | Capability | One transport seam over WebSocket, TCP, Unix sockets, stdio and HTTP. |
| [03 Client](03-client.md) | Capability | Correlated calls, cancellation, notifications, batches, typed proxy. |
| [04 Serialization](04-serialization.md) | Capability | JSON, CBOR and MessagePack with identical round trips. |
| [05 Streaming](05-streaming.md) | Capability | IAsyncEnumerable<T> results streamed over every transport. |
| [06 ASP.NET Core hosting](06-aspnetcore.md) | Capability | MapJsonRpc: POST, SSE and WebSocket on one route, DI and auth. |
| [07 OpenRPC generation](07-openrpc-generation.md) | Capability | OpenRPC document from contracts, served on rpc.discover. |
| [08 OpenRPC client generation](08-openrpc-client-generation.md) | Capability | Typed C# client generated from an OpenRPC document. |
| [09 Broadcast](09-broadcast.md) | Capability | Push notifications to every connected peer, or a filtered subset. |
| [10 Generated client notifications](10-generated-client-notifications.md) | Capability | Typed On<Name> handlers for server-pushed notifications. |

