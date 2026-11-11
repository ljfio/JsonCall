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

## Specifications

Acceptance criteria live in [`specs/features`](specs/features) as Gherkin and run via Reqnroll + xUnit in `test/JsonCall.Specs`.
All specs have step definitions. `dotnet test` reports any spec without one as skipped.
Step definitions go in `test/JsonCall.Specs/Steps`.
