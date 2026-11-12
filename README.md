# JsonCall

Typed, transport-agnostic JSON-RPC 2.0 for .NET, with OpenRPC in both directions.

One C# interface drives the server, a typed client, `IAsyncEnumerable<T>` streaming and an OpenRPC document.
An OpenRPC document, yours or someone else's, becomes a typed C# client at build time.

> **Status: preview (0.1).** The JSON-RPC core is covered by 274 executable specifications. The streaming wire
> convention is a draft and the public API is not frozen. See the [roadmap](https://github.com/ljfio/JsonCall/blob/main/docs/ROADMAP.md) for what stands
> between this and 1.0.

## What you get

- **JSON-RPC 2.0, complete**: requests, notifications, batches, by-name and by-position params, standard error codes,
  concurrent correlated calls, cancellation and timeouts, calls in both directions on one connection.
- **Any transport**: WebSocket, TCP, Unix domain sockets, stdio (child process or console) and HTTP. Content-Length
  (LSP style) or newline framing on byte streams.
- **Any wire format**: JSON built in; CBOR and MessagePack as add-on packages.
- **ASP.NET Core hosting**: `MapJsonRpc("/rpc")` serves HTTP POST, Server-Sent Events and WebSocket on one route, with
  dependency injection per call and `[Authorize]` on contracts and methods.
- **Streaming**: a contract method that returns `IAsyncEnumerable<T>` streams over every transport, with
  back-pressure and cancellation.
- **OpenRPC out**: a document generated from your contracts (Draft 07 schemas, XML doc comments), served on
  `rpc.discover`.
- **OpenRPC in**: a Roslyn source generator and the `jsoncall` tool turn an OpenRPC document into records, an
  interface and a client class.

## Install

Requires .NET 10.

| You are writing | Packages |
| --- | --- |
| An ASP.NET Core service | `JsonCall.AspNetCore` (add `JsonCall.OpenRpc` for `rpc.discover`) |
| A client for a contract you share with the server | `JsonCall.Client`, `JsonCall.Transport` |
| A client from an OpenRPC document | `JsonCall.Client`, `JsonCall.Transport`, `JsonCall.Generator` |
| A stdio, TCP or Unix socket server (a daemon, a language server) | `JsonCall.Server`, `JsonCall.Transport` |

```sh
dotnet add package JsonCall.AspNetCore --prerelease
```

All packages:

| Package | What it is |
| --- | --- |
| `JsonCall.Core` | Message model, `IMessageTransport`, `IMessageSerializer`, contract attributes and `JsonRpcPeer` (correlated calls, notifications, batches, streams) |
| `JsonCall.Client` | `JsonRpcClient` and the typed proxy for contract interfaces |
| `JsonCall.Server` | Dispatcher: method table, parameter binding, error mapping, stream producers, `JsonRpcConnectionSet` |
| `JsonCall.Transport` | WebSocket, TCP, Unix domain socket, stdio and HTTP (with SSE) transports |
| `JsonCall.Serializer.Cbor`, `JsonCall.Serializer.MessagePack` | Binary wire formats |
| `JsonCall.AspNetCore` | `AddJsonRpc` and `MapJsonRpc` |
| `JsonCall.OpenRpc` | OpenRPC document from contracts, and `rpc.discover` |
| `JsonCall.Generator` | Source generator: typed client from `*.openrpc.json` |
| `JsonCall.Tool` | The `jsoncall` dotnet tool: the same generator on the command line |

## Quick start

The contract, shared by server and client:

```csharp
using JsonCall.Core;

[JsonRpcContract]
public interface IMath
{
    Task<int> AddAsync(int a, int b, CancellationToken ct = default);          // wire name "add"

    IAsyncEnumerable<int> CountAsync(int to, CancellationToken ct = default);  // streamed, wire name "count"
}
```

The server:

```csharp
using JsonCall.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddJsonRpc(o => o.AddContract<IMath, MathService>());   // MathService is resolved per call

var app = builder.Build();
app.MapJsonRpc("/rpc");                                                  // POST, SSE and WebSocket
app.Run();
```

The client:

```csharp
using JsonCall.Client;
using JsonCall.Transport;

var transport = await WebSocketMessageTransport.ConnectAsync(new Uri("ws://localhost:5000/rpc"));
await using var client = new JsonRpcClient(transport);

var math = client.CreateProxy<IMath>();
var sum = await math.AddAsync(1, 2);

await foreach (var n in math.CountAsync(10))
    Console.WriteLine(n);
```

Swap `WebSocketMessageTransport` for `HttpMessageTransport`, `TcpMessageTransport`, `UnixSocketMessageTransport` or
`StdioMessageTransport` and nothing else changes.

## OpenRPC

Serve the document of your contracts:

```csharp
using JsonCall.OpenRpc;

builder.Services.AddJsonRpc(o => o
    .AddContract<IMath, MathService>()
    .AddOpenRpc(r => r.PublicBaseUrl = "https://api.example.com/rpc"));   // adds rpc.discover
```

Generate a typed client from a document at build time:

```xml
<ItemGroup>
  <PackageReference Include="JsonCall.Generator" Version="0.1.0-preview.1" PrivateAssets="all" />
  <PackageReference Include="JsonCall.Client" Version="0.1.0-preview.1" />
  <AdditionalFiles Include="api.openrpc.json" Namespace="Acme.Api" ClientName="AcmeClient" />
</ItemGroup>
```

```csharp
var acme = new AcmeClient(client);       // any JsonRpcPeer: any transport, any serializer
```

Or on the command line: `jsoncall generate api.openrpc.json --out ./gen`, or straight from a running server with
`jsoncall generate --url https://host/rpc --discover`.

## Documentation

| Guide | Covers |
| --- | --- |
| [Getting started](https://github.com/ljfio/JsonCall/blob/main/docs/getting-started.md) | Contracts, servers with and without ASP.NET Core, clients, errors, cancellation, batches, authorization |
| [Transports and wire formats](https://github.com/ljfio/JsonCall/blob/main/docs/transports.md) | Each transport, framing, CBOR and MessagePack, size limits, broadcasting, writing your own |
| [Streaming](https://github.com/ljfio/JsonCall/blob/main/docs/streaming.md) | `IAsyncEnumerable<T>` end to end, and the wire convention for implementers in other languages |
| [OpenRPC](https://github.com/ljfio/JsonCall/blob/main/docs/openrpc.md) | Document generation, `rpc.discover`, client generation, schema mapping, generator diagnostics |
| [Comparison](https://github.com/ljfio/JsonCall/blob/main/docs/comparison.md) | How JsonCall differs from StreamJsonRpc and other .NET JSON-RPC libraries, and when to choose them |
| [Roadmap](https://github.com/ljfio/JsonCall/blob/main/docs/ROADMAP.md) | Known issues, what is planned before 1.0, and what is out of scope |
| [Product requirements](https://github.com/ljfio/JsonCall/blob/main/docs/PRD.md) | The design record: goals, requirements and decisions |

## Building and testing

```sh
dotnet build
dotnet test
```

Acceptance criteria live in [`specs/features`](https://github.com/ljfio/JsonCall/tree/main/specs/features) as Gherkin and run through Reqnroll and xUnit in
`test/JsonCall.Specs`. Every requirement in the PRD is tagged on the scenarios that prove it (`@FR-...`). Step
definitions go in `test/JsonCall.Specs/Steps`; `dotnet test` reports a scenario without one as skipped.

## License

[MIT](https://github.com/ljfio/JsonCall/blob/main/LICENSE)
