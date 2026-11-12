# OpenRPC

[OpenRPC](https://open-rpc.org) is to JSON-RPC what OpenAPI is to REST: a machine-readable description of an API.
JsonCall works with it in both directions.

| Direction | Package | What it does |
| --- | --- | --- |
| Out | `JsonCall.OpenRpc` | Generates a document from your contracts and serves it on `rpc.discover`. |
| In | `JsonCall.Generator`, `JsonCall.Tool` | Generates a typed C# client from a document. |

## Describing your service

```csharp
using JsonCall.OpenRpc;

builder.Services.AddJsonRpc(o => o
    .AddContract<IOrders, OrderService>()
    .AddOpenRpc(r =>
    {
        r.Title = "Orders";
        r.Version = "2.1.0";
        r.PublicBaseUrl = "https://api.example.com/rpc";     // listed under "servers"
        r.Metadata.Add(new AuthorizeAttribute());            // optional: protect rpc.discover
    }));
```

`AddOpenRpc` works on any `JsonRpcServerOptions`, so a stdio or TCP server can be discovered the same way. The
document is generated on first request and cached.

### What goes into the document

| In your code | In the document |
| --- | --- |
| Contract method | A `methods` entry, named as on the wire. |
| Parameter | A `params` entry. Required params come before optional ones. |
| Default value, or nullable with nothing required after it | `required: false`. |
| `Task<T>` / `ValueTask<T>` | `result`, with the schema of `T`. |
| `Task`, `void`, `[JsonRpcNotification]` | No `result`. |
| `IAsyncEnumerable<T>` | `result` with the schema of one `T`, and `"x-jsoncall-stream": true`. |
| XML `<summary>`, `<remarks>`, `<param>` | `summary`, `description` and param descriptions. |
| `[Obsolete]` | `deprecated: true`. |
| `[JsonRpcError(code, message)]` | An `errors` entry. |
| `[JsonRpcTag("name")]` on a method or contract | `tags`. |
| `[JsonRpcContract(Title, Version, ParamStyle)]` | `info.title`, `info.version`, `paramStructure`. |
| A type used in more than one place, or recursively | Hoisted into `components.schemas` and referenced with `$ref`. |

XML comments are read from the documentation file next to the assembly, so the project that declares the contracts
needs `<GenerateDocumentationFile>true</GenerateDocumentationFile>`.

Schemas are JSON Schema Draft 07. The document declares `openrpc: 1.3.2`, the newest version the official OpenRPC
meta-schema accepts; the test suite validates generated documents against that meta-schema.

To produce a document without a server, for a build step or a docs site:

```csharp
var generator = new OpenRpcDocumentGenerator(new OpenRpcOptions { Title = "Orders" });
var json = OpenRpcDocumentGenerator.ToJson(generator.Generate(new[] { typeof(IOrders) }));
```

## Generating a client

### At build time

```xml
<ItemGroup>
  <PackageReference Include="JsonCall.Generator" Version="0.1.0-preview.1" PrivateAssets="all" />
  <PackageReference Include="JsonCall.Client" Version="0.1.0-preview.1" />
  <AdditionalFiles Include="orders.openrpc.json" Namespace="Acme.Orders" ClientName="OrdersClient" />
</ItemGroup>
```

The generator picks up every `AdditionalFiles` item named `openrpc.json` or ending in `.openrpc.json`,
`-openrpc.json` or `_openrpc.json`. The generated code calls
into `JsonCall.Core`, which `JsonCall.Client` brings in.

| Metadata | Effect | Default |
| --- | --- | --- |
| `Namespace` | Namespace of the generated types. | `JsonCall.Generated` |
| `ClientName` | Name of the client class. The interface is `I<ClientName>`. | From the file name |
| `NumberType="decimal"` | Map JSON `number` to `decimal`. | `double` |

### On the command line

```sh
dotnet tool install --global JsonCall.Tool --prerelease

jsoncall generate orders.openrpc.json --out ./Generated --namespace Acme.Orders --client-name OrdersClient
jsoncall generate --url https://api.example.com/rpc --discover --out ./Generated
```

| Option | Effect |
| --- | --- |
| `--out <dir>` | Output directory. Default `./gen`. |
| `--namespace <name>`, `--client-name <name>`, `--number-type decimal` | As the metadata above. |
| `--url <endpoint> --discover` | Fetch the document by calling `rpc.discover` over HTTP. |
| `--json-context` | Also emit a `JsonSerializerContext` for the generated types, for trimming and Native AOT. |

The tool and the source generator share one implementation and produce identical code. Use the tool when you want
the generated files in source control, or need `--json-context`: one source generator cannot see another's output,
so the System.Text.Json generator can only complete a context that exists as a file in your project.

### What you get

```csharp
public sealed record Order(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] OrderStatus Status);

public interface IOrdersClient
{
    [JsonRpcMethod("get")]
    Task<Order> GetAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class OrdersClient : IOrdersClient { /* ... */ }
```

```csharp
await using var client = new JsonRpcClient(transport);
IOrdersClient orders = new OrdersClient(client);
```

- The client takes any `JsonRpcPeer`, so it works over every transport and wire format.
- It uses no reflection of its own, and output is deterministic: the same document always yields the same code.
- Params are sent by name or by position as the document's `paramStructure` says.
- Each documented error becomes an exception class deriving from `JsonRpcException`, so you can catch it by type.
- Streaming methods (`x-jsoncall-stream`) return `IAsyncEnumerable<T>`.
- Depend on the interface in your own code so it can be faked in tests.

### Schema mapping

| JSON Schema | C# |
| --- | --- |
| `object` with `properties` | `sealed record`. Properties not in `required` are nullable and omitted when null. |
| `object` with only `additionalProperties` | `IReadOnlyDictionary<string, T>` |
| `array` | `IReadOnlyList<T>` |
| `string` | `string`; with `format`: `date-time` → `DateTimeOffset`, `date` → `DateOnly`, `time` → `TimeOnly`, `uuid` → `Guid`, `uri` → `Uri`; with `contentEncoding: base64` → `byte[]` |
| `integer` | `long`; `format: int32` → `int` |
| `number` | `double` (or `decimal`); `format: float` → `float` |
| `enum` of strings | `enum` with the wire names preserved |
| `$ref` | The referenced type. Cycles are supported. |
| `allOf` | One record with the merged properties. |
| `oneOf` / `anyOf` where every variant pins a common property to a `const` | An abstract record with `[JsonPolymorphic]` and one derived record per variant. |
| `oneOf` / `anyOf` whose variants all map to the same C# type | That type. |
| Any other `oneOf` / `anyOf` | A generated union type with `Index`, `Value`, and `From...` / `As...` members (reported as `JSONCALL004`). |
| `patternProperties`, `not`, `if`, `propertyNames` and similar | `JsonElement` (reported as `JSONCALL002`). |

Nothing in a document stops generation. Whatever cannot be typed becomes `JsonElement` and is reported, so you can
still call the rest of the API.

### Schemas in separate files

A document may refer to sibling JSON Schema files: `{"$ref": "order.schema.json"}` or
`{"$ref": "common.schema.json#/definitions/money"}`. References are resolved relative to the file that contains
them, so a schema file may refer to further files.

The command line tool reads them from the document's directory. A source generator can only see `AdditionalFiles`,
so list the schemas there too:

```xml
<AdditionalFiles Include="lsp/lsp.openrpc.json" Namespace="Lsp" ClientName="LanguageServerClient" />
<AdditionalFiles Include="lsp/basic.schema.json" />
<AdditionalFiles Include="lsp/diagnostic.schema.json" />
```

Remote references (`https://...`) are not fetched.

### Notifications pushed by the server

OpenRPC has no way to say which side sends a method. By default, a method with no `result` generates a method that
*sends* a notification. For a notification the server pushes to the client, mark it in the document:

```json
{
  "name": "textDocument/publishDiagnostics",
  "x-jsoncall-direction": "server-to-client",
  "params": [{ "name": "uri", "schema": { "type": "string" } }, { "name": "diagnostics", "schema": { "...": "..." } }]
}
```

The generator then emits a typed handler registration instead of a call, and leaves the method off the interface:

```csharp
var lsp = new LanguageServerClient(client);
lsp.OnTextDocumentPublishDiagnostics((uri, diagnostics) => Show(uri, diagnostics));
```

Handler arguments follow the document's param order, whether the server sends them by name or by position. Omitted
optional params arrive as `null`. Registering again replaces the previous handler.

### Diagnostics

Each diagnostic points at the file, line and JSON path that caused it.

| Id | Severity | Meaning | What to do |
| --- | --- | --- | --- |
| `JSONCALL001` | Error | The document is not valid OpenRPC: a method or param without a name, a duplicate method name. | Fix the document. |
| `JSONCALL002` | Warning | A JSON Schema keyword is not supported. The value is generated as `JsonElement`. | Usually nothing. Handle the `JsonElement` yourself. |
| `JSONCALL003` | Warning | The document's `openrpc` version is outside 1.0 to 1.4. | Check the generated client against the server. |
| `JSONCALL004` | Info | A `oneOf` / `anyOf` had no discriminator and became a generated union type. | Nothing. Add a `const` discriminator to the schema if you own it. |
| `JSONCALL005` | Error | The file is not valid JSON. | Fix the file. |
| `JSONCALL006` | Warning | A `$ref` could not be resolved. The value is generated as `JsonElement`. | Add the schema file to `AdditionalFiles`, or fix the reference. |
| `JSONCALL007` | Info | Lists the sibling schema files that were loaded. | Nothing. |

## Extensions

JsonCall adds two fields to OpenRPC. Both are ignored by other tools.

| Field | On | Meaning |
| --- | --- | --- |
| `x-jsoncall-stream: true` | A method | The method streams. `result` describes one item. See [Streaming](streaming.md). |
| `x-jsoncall-direction: "server-to-client"` | A method with no result | The server sends this notification; the client handles it. |

## Round trip

A contract, turned into a document, turned back into a client, yields a client that is wire-compatible with the
original contract. This is tested (`FR-GEN-05`), along with generation from real third-party documents: the Ethereum
execution API, the OpenRPC Petstore, and OpenRPC renderings of parts of the Language Server Protocol and the Agent
Client Protocol.
