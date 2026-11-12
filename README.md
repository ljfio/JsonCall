# JsonCall

Typed, transport-agnostic JSON-RPC 2.0 for .NET 10: one contract interface drives the server, a typed client, streaming
(`IAsyncEnumerable<T>`), OpenRPC documentation and generated clients. See [docs/PRD.md](docs/PRD.md).

## Packages

| Package | What it is |
| --- | --- |
| `JsonCall.Core` | Message model, `IMessageTransport`, `IMessageSerializer`, contract attributes, and `JsonRpcPeer` (correlated calls, notifications, batches, streams) |
| `JsonCall.Client` | `JsonRpcClient` and the `DispatchProxy` typed proxy |
| `JsonCall.Server` | Dispatcher: method table, parameter binding, error mapping, stream producers |
| `JsonCall.Transport` | WebSocket, TCP, stdio (Content-Length framing) and HTTP (with SSE streaming) transports |
| `JsonCall.Serializer.Cbor`, `JsonCall.Serializer.MessagePack` | Binary wire formats (JSON is built in) |
| `JsonCall.AspNetCore` | `AddJsonRpc` and `MapJsonRpc`: HTTP POST, SSE and WebSocket on one route |
| `JsonCall.OpenRpc` | OpenRPC document from contracts (Draft 07 schemas, XML docs) and `rpc.discover` |
| `JsonCall.Generator` | Source generator: typed client from `*.openrpc.json` AdditionalFiles |
| `JsonCall.Tool` | `jsoncall` dotnet tool, the same generator on the command line |

## Quick start

```csharp
[JsonRpcContract]
public interface IMath
{
    Task<int> AddAsync(int a, int b, CancellationToken ct = default);   // wire name "add"
}

public interface IEvents
{
    IAsyncEnumerable<Tick> TicksAsync(int count, CancellationToken ct); // streamed
}
```

Server (ASP.NET Core):

```csharp
builder.Services.AddJsonRpc(o => o.AddContract<IMath, MathService>());
app.MapJsonRpc("/rpc");                // POST, SSE and WebSocket on /rpc
```

Client, over any transport:

```csharp
var transport = await WebSocketMessageTransport.ConnectAsync(new Uri("ws://localhost:5000/rpc"));
await using var client = new JsonRpcClient(transport);
var math = client.CreateProxy<IMath>();
var sum = await math.AddAsync(1, 2);
```

## OpenRPC

Serve the document of your contracts on `rpc.discover`:

```csharp
builder.Services.AddJsonRpc(o => o
    .AddContract<IMath, MathService>()
    .AddOpenRpc(r => r.PublicBaseUrl = "https://api.example.com/rpc"));
```

Generate a typed client from a document, at build time:

```xml
<ItemGroup>
  <PackageReference Include="JsonCall.Generator" PrivateAssets="all" />
  <AdditionalFiles Include="api.openrpc.json" Namespace="Acme.Api" ClientName="AcmeClient" />
</ItemGroup>
```

or with the tool: `jsoncall generate api.openrpc.json --out ./gen`, or `jsoncall generate --url https://host/rpc --discover`.

If the document refers to sibling schema files (`{"$ref": "diagnostic.schema.json"}`), the tool reads them from the
document's directory. A source generator can only see `AdditionalFiles`, so add the schemas there too:

```xml
<AdditionalFiles Include="lsp/lsp.openrpc.json" Namespace="Lsp" ClientName="LanguageServerClient" />
<AdditionalFiles Include="lsp/basic.schema.json" />
<AdditionalFiles Include="lsp/diagnostic.schema.json" />
```

### Pushed notifications

A generated method with no result (for example `EventAsync`) is a **call** that sends a notification to the
server. It is not a subscription. To receive notifications the server pushes, mark the method in the document with
`"x-jsoncall-direction": "server-to-client"`. The generator then emits a typed `On<Name>` handler on the client
instead of a call, and leaves the method off the call interface:

```csharp
var client = new LanguageServerClient(peer);
client.OnTextDocumentPublishDiagnostics((uri, version, diagnostics) => Show(uri, diagnostics));
```

Handler arguments follow the document's param order. Params are read by name or by position, and omitted optional
params arrive as `null`. Registering a handler again
replaces the previous one. Without the marker, or with a lower-level peer, use `peer.OnNotification("event", ...)`.

## Broadcasting from a daemon

`JsonRpcServer.Attach` returns an untracked peer. `JsonRpcConnectionSet` tracks the peers, drops them when
their connection ends, and broadcasts notifications:

```csharp
var connections = new JsonRpcConnectionSet();
connections.Attach(server, transport);                       // per accepted connection
await connections.BroadcastAsync("event", JsonRpcParams.Single(payload));
await connections.BroadcastAsync("event", JsonRpcParams.Single(payload), filter: p => IsSubscribed(p));
```

## Stream transports and framing

TCP, Unix domain sockets and stdio default to `Content-Length` framing. Pass `MessageFraming.Newline` for one JSON message
per line (JSON serializer only):

```csharp
await using var transport = await UnixSocketMessageTransport.ConnectAsync("/run/daemon.sock", MessageFraming.Newline);
await using var client = new JsonRpcClient(transport);

using var listener = new UnixSocketMessageListener("/run/daemon.sock", MessageFraming.Newline); // server side
server.Attach(await listener.AcceptAsync());
```

## Specifications

Acceptance criteria live in [`specs/features`](specs/features) as Gherkin and run via Reqnroll + xUnit in `test/JsonCall.Specs`.
All specs have step definitions. `dotnet test` reports any spec without one as skipped.
Step definitions go in `test/JsonCall.Specs/Steps`.
