# PRD: JsonCall — a typed, transport-agnostic JSON-RPC 2.0 toolkit for .NET

Status: Draft v0.1 · Name: **JsonCall** (see §12)
Target framework: **.NET 10** (C# 14) · Acceptance specs: `specs/features/*.feature`

## 1. Problem

.NET has good JSON-RPC 2.0 wire libraries (StreamJsonRpc) but nothing that covers the full loop a service author wants:

1. A JSON-RPC server that is a first-class **ASP.NET Core** citizen (endpoints, DI, auth, middleware), including **`IAsyncEnumerable<T>` streaming**.
2. A client whose **transport is interchangeable** (WebSocket, TCP, stdio, HTTP/SSE) and whose **serializer is interchangeable** (JSON, CBOR, MessagePack).
3. A machine-readable **interface description** (OpenRPC) generated from code, served via `rpc.discover`.
4. **Strongly typed clients generated from OpenRPC** documents, including documents for servers we do not own. No C# generator for OpenRPC exists today (the OpenRPC generator ecosystem covers TypeScript, Rust and Python).

The existing prototype (`JsonCall`) proves the serializer split but has: a client welded to `ClientWebSocket`, no request/response correlation (one call at a time, notifications would be mistaken for responses), no server, no streaming, and no description format.

## 2. Goals

| ID | Goal |
|----|------|
| G1 | Fully conformant JSON-RPC 2.0 (requests, notifications, batches, error codes, by-name and by-position params). |
| G2 | Transport and serializer are independent, swappable seams. Adding a transport never touches protocol code. |
| G3 | Concurrent, correlated calls on one connection; server-to-client notifications; cancellation and timeouts. |
| G4 | ASP.NET Core add-on: `MapJsonRpc(...)` over HTTP, WebSocket and SSE, with DI-resolved handlers and `IAsyncEnumerable<T>` streaming responses. |
| G5 | Code-first contract: a C# interface is the single source of truth for server dispatch, client proxy and OpenRPC output. |
| G6 | OpenRPC 1.4 document generation from contracts, served on `rpc.discover`, validated against the official meta-schema. |
| G7 | Spec-first typed client generation: Roslyn source generator turns `openrpc.json` into DTOs, an interface and a client. |
| G8 | Trim/AOT friendly: System.Text.Json source generation supported, no required runtime reflection on the hot path. |

## 3. Non-goals (v1)

- JSON-RPC 1.0 / 1.1 compatibility.
- A custom binary RPC protocol, service mesh features, or load balancing.
- Generating clients for languages other than C# (the OpenRPC document enables other ecosystems' generators).
- Authentication schemes. Hosting uses standard ASP.NET Core auth and middleware.
- Wrapping or depending on StreamJsonRpc (see §11, decision D1).

## 4. Users

- **Service author**: writes a C# interface and handler, wants an endpoint, docs and a client with no boilerplate.
- **Integrator**: consumes a third-party JSON-RPC service that publishes OpenRPC, wants typed C# calls from the document.
- **Tooling author**: wants a small transport-agnostic core to embed (LSP-style stdio tools, MCP-style servers).

## 5. Package architecture

| Package (working names) | Responsibility | Depends on |
|---|---|---|
| `JsonCall.Core` | Message model, params model, error codes, `IMessageSerializer`, `IMessageTransport`, contract attributes | none |
| `JsonCall.Serializer.SystemTextJson` / `.Cbor` / `.MessagePack` | Wire serializers | Core |
| `JsonCall.Transport.WebSocket` / `.Tcp` / `.Stdio` / `.Http` | Client transports and their framing | Core |
| `JsonCall.Client` | `JsonRpcClient`: correlation, notifications, cancellation, batching, streaming consumption, `DispatchProxy` typed proxy | Core |
| `JsonCall.Server` | Transport-neutral dispatcher: method table, param binding, error mapping, streaming producers | Core |
| `JsonCall.AspNetCore` | `MapJsonRpc`, WebSocket and SSE adapters, DI integration, endpoint metadata | Server |
| `JsonCall.OpenRpc` | OpenRPC object model, generator from contracts, `rpc.discover` handler | Core, Server |
| `JsonCall.OpenRpc.ClientGenerator` | Roslyn incremental source generator and CLI: `openrpc.json` to C# | none at runtime |

Transport owns **framing**: WebSocket and SSE are already message-oriented; TCP and stdio use `Content-Length` header framing by default (newline-delimited selectable). The core only ever sees whole messages.

## 6. Functional requirements

IDs are referenced from feature-file tags (`@FR-xxx`) so every requirement is traceable to executable scenarios.

### 6.1 Protocol (`specs/features/01-protocol.feature`)
- **FR-PROTO-01** Requests, success responses and error responses follow JSON-RPC 2.0 exactly, including `"jsonrpc":"2.0"`.
- **FR-PROTO-02** Ids may be string, number or null. Server echoes the id verbatim. Client generates unique ids.
- **FR-PROTO-03** Notifications (no `id`) never produce a response.
- **FR-PROTO-04** Params by position (array) and by name (object); omitted params allowed. Both are accepted by default.
- **FR-PROTO-05** Batches: array of requests returns array of responses (notifications omitted); empty batch is Invalid Request; all-notification batch returns nothing.
- **FR-PROTO-06** Standard errors: -32700 parse, -32600 invalid request, -32601 method not found, -32602 invalid params, -32603 internal. Application errors are outside -32768..-32000.
- **FR-PROTO-07** Method names beginning `rpc.` are reserved for the library.

### 6.2 Transports and client (`02-transports.feature`, `03-client.feature`)
- **FR-TRANS-01** A single `IMessageTransport` seam (send message, receive message stream, close) with identical client behaviour across WebSocket, TCP, stdio and HTTP.
- **FR-TRANS-02** TCP and stdio framing is correct for split, coalesced and large messages.
- **FR-CLIENT-01** Concurrent calls on one connection are correlated by id, in any response order.
- **FR-CLIENT-02** Per-call timeout and `CancellationToken`; cancelling sends a cancel notification when the transport is stateful (see FR-STREAM-04).
- **FR-CLIENT-03** Server-to-client notifications are delivered to registered handlers and never confused with responses. On bidirectional transports the server may also call methods the client exposes.
- **FR-CLIENT-04** Batching API returns typed results per entry.
- **FR-CLIENT-05** Connection loss fails all in-flight calls with a typed exception; no hangs.
- **FR-CLIENT-06** Typed proxy: an interface with `[JsonRpcContract]` becomes a client; `Task`/`ValueTask` methods are calls, void or `Task`-returning methods marked `[JsonRpcNotification]` are notifications.

### 6.3 Serialization (`04-serialization.feature`)
- **FR-SER-01** Identical logical messages round-trip through JSON, CBOR and MessagePack.
- **FR-SER-02** `params` round-trips by position and by name in every serializer.
- **FR-SER-03** Custom `JsonSerializerOptions` / `JsonSerializerContext` (source generated) are honoured.
- **FR-SER-04** Malformed input yields a parse error, never an unhandled exception.

### 6.4 Streaming (`05-streaming.feature`)
- **FR-STREAM-01** A contract method returning `IAsyncEnumerable<T>` streams items to the client as produced, not buffered.
- **FR-STREAM-02** Over HTTP the stream is delivered as Server-Sent Events (using ASP.NET Core `TypedResults.ServerSentEvents`).
- **FR-STREAM-03** Over WebSocket/TCP/stdio the stream is delivered as `$/stream` notifications (draft convention below).
- **FR-STREAM-04** Client cancellation or disconnect stops the producer and disposes the enumerator and any subscription (finally-block semantics, same pattern as the EventSystem `WrappedResult` unsubscribe).
- **FR-STREAM-05** An exception mid-stream terminates the stream with a JSON-RPC error; items already delivered stay delivered.
- **FR-STREAM-06** Client consumes a stream as `IAsyncEnumerable<T>` with back-pressure.

**Draft stream convention (decision D2, to be ratified):**
- *SSE/HTTP*: request is a normal POST with `Accept: text/event-stream`. Each item is an SSE `data:` event holding the item JSON; `event: error` carries a JSON-RPC error object; `event: complete` closes the stream.
- *Message transports*: after the initial response `{"result":{"stream":"<streamId>"}}` the server sends notifications `$/stream.item {stream, seq, value}`, then `$/stream.end {stream}` or `$/stream.error {stream, error}`. The client sends `$/cancel {stream}` to stop.
- OpenRPC marks such methods with `x-jsoncall-stream: true` and describes the item schema as the result schema.

### 6.5 ASP.NET Core hosting (`06-aspnetcore.feature`)
- **FR-HOST-01** `app.MapJsonRpc("/rpc")` exposes POST (single/batch), WebSocket upgrade and SSE on one route, returning an `IEndpointConventionBuilder` so auth, CORS and rate limiting apply.
- **FR-HOST-02** Handlers resolve through DI per call (scoped lifetime per request/message).
- **FR-HOST-03** Contract implementations are registered via `AddJsonRpc(...)`; duplicate method names fail at startup with a clear error.
- **FR-HOST-04** Endpoint metadata is visible to ASP.NET Core (names, route) and supports `[Authorize]` on contracts or methods.
- **FR-HOST-05** Graceful shutdown completes in-flight calls and closes streams.

### 6.6 OpenRPC generation (`07-openrpc-generation.feature`)
- **FR-ORPC-01** Document generated from registered contracts: `openrpc`, `info`, `methods` (name, params, result, errors, deprecated, tags, summary/description from XML docs or attributes).
- **FR-ORPC-02** Param order places required params before optional; optional is derived from nullability/default values. `paramStructure` emitted from contract settings.
- **FR-ORPC-03** Void/notification methods omit `result`.
- **FR-ORPC-04** Schemas are JSON Schema Draft 07, shared types hoisted into `components.schemas` with `$ref`; recursive types supported.
- **FR-ORPC-05** `[Obsolete]` maps to `deprecated`; declared exceptions map to `errors` with unique codes outside the reserved range.
- **FR-ORPC-06** Served via `rpc.discover`; document validates against the official OpenRPC meta-schema in CI.
- **FR-ORPC-07** Streaming methods carry `x-jsoncall-stream`.

### 6.7 Client generation (`08-openrpc-client-generation.feature`)
- **FR-GEN-01** Source generator reads `openrpc.json` from `AdditionalFiles` and emits DTOs, a contract interface and a client class.
- **FR-GEN-02** Draft 07 constructs map to C#: objects, arrays, enums, nullable, `required`, `$ref` (including cycles), `allOf`; `oneOf`/`anyOf` map to a documented union strategy.
- **FR-GEN-03** Generated clients target the same `JsonRpcClient` and are transport/serializer agnostic.
- **FR-GEN-04** Output is deterministic and diagnostics point at the offending JSON path.
- **FR-GEN-05** A generate-then-regenerate round trip (contract to OpenRPC to generated client) yields a client wire-compatible with the original contract.

## 7. Non-functional requirements

- **NFR-01 Performance**: client allocates no more than one buffer per message beyond serializer output (uses `System.IO.Pipelines`/`ReadOnlySequence`). Benchmarks (BenchmarkDotNet) track calls/sec and allocations; regressions >10% fail CI.
- **NFR-02 AOT/trim**: Core, Client, Server and the STJ serializer publish with `PublishAot` and zero trim warnings when using source-generated contexts.
- **NFR-03 Reliability**: no unobserved task exceptions; no hangs on disconnect; all public async APIs honour cancellation.
- **NFR-04 Compatibility**: .NET 10 only for v1. Multi-targeting is revisited only on request.
- **NFR-05 Quality gates**: unit and integration tests run Gherkin features through Reqnroll (xUnit); line coverage ≥ 85% on Core/Server/Client.
- **NFR-06 Docs**: every public API has XML docs; a quickstart per package.

## 8. Test and validation strategy

- Each `.feature` file under `specs/features/` is executable via **Reqnroll** (maintained SpecFlow successor) with xUnit. Step definitions live in `test/JsonCall.Specs`.
- Transport scenarios run against an in-memory pair, real loopback WebSocket/TCP, and a stdio child process, using one shared step set (scenario outlines over transport name).
- Interop scenarios call the server with a reference client (a raw JSON string test harness, and StreamJsonRpc as an external conformance peer in tests only).
- OpenRPC output is validated against the published meta-schema; golden-file tests lock generated documents and generated C#.
- Scenario tags: `@FR-*` traceability, `@draft` for conventions awaiting ratification, `@phase1..3` for delivery order. CI reports requirements with zero passing scenarios.

## 9. Milestones

| Phase | Scope | Exit criteria |
|---|---|---|
| 0. Foundation | Repo on .NET 10 (done), rename, Reqnroll harness, CI, rename packages | Harness runs a trivial feature in CI |
| 1. Core + client | Protocol model, serializers, `IMessageTransport`, WebSocket/TCP/stdio, correlating client | `01`-`04` features green |
| 2. Server + hosting | Dispatcher, `JsonCall.AspNetCore`, HTTP/WS, typed proxy | `03`, `06` green |
| 3. Streaming | SSE and `$/stream` convention, client consumption, cleanup | `05` green, D2 ratified |
| 4. OpenRPC out | Generator, `rpc.discover`, meta-schema validation | `07` green |
| 5. OpenRPC in | Source generator and CLI | `08` green, round-trip proven |

## 10. Risks

| Risk | Mitigation |
|---|---|
| Schema to C# mapping (`oneOf`, cycles) is the hardest piece | Spike Corvus.Text.Json vs NJsonSchema vs own mapper on 3 real-world OpenRPC documents before Phase 5 (decision D3). |
| Streaming convention is not in JSON-RPC or OpenRPC | Keep behind `x-jsoncall-stream`, ship `@draft`, interop-test against peers, document as extension. |
| Scope: this is a large surface for one library | Strict phase gates; each package independently useful and shippable. |
| Draft 07 schema generation from .NET types | Evaluate System.Text.Json's `JsonSchemaExporter` (2020-12 output) plus a down-leveling pass versus a Draft 07 library (decision D4). |
| Reinventing StreamJsonRpc | Explicit differentiation: ASP.NET Core-native, OpenRPC in/out, SSE streaming, pluggable transports and serializers. Revisit D1 if Phase 1 cost balloons. |

## 11. Decisions and open questions

| ID | Question | Current stance |
|----|----------|----------------|
| D1 | Own wire layer vs build on StreamJsonRpc | **Own library** (decided by product owner). |
| D2 | Streaming wire convention | Draft in §6.4; ratify at Phase 3. |
| D3 | Schema to C# engine | Open; spike before Phase 5. |
| D4 | Draft 07 schema source for server-side generation | Open; spike in Phase 4. |
| D5 | Union mapping for `oneOf`/`anyOf` | Open; proposal: abstract base plus discriminator when present, else `JsonElement`-backed wrapper. |
| D6 | Reqnroll vs plain xUnit with Gherkin parser | Reqnroll proposed. |

## 12. Name

**JsonCall** (decided). It is a pun on Jason and JSON-RPC: JavaScript Object Notation Remote Procedure *Call*.

- Plain `Jason` and `Jason.Client`/`Jason.Server`/`Jason.WebAPI`/`Jason.Tools` are taken on NuGet by an unrelated older CQRS framework, so the `Jason` prefix is avoided.
- `JsonCall`, `JsonCall.Core`, `JsonCall.Client` and `JsonCall.AspNetCore` were unclaimed on NuGet when checked (2026-11-11). Prefix reservations and trademarks were not checked; reserve the `JsonCall.` prefix on first publish.
- Namespaces and package IDs use the `JsonCall.*` prefix. CLI tool: `jsoncall`. OpenRPC extension prefix: `x-jsoncall-`. Generator diagnostic prefix: `JSONCALL`.
- Rejected alternatives: CallJason, JaySong, JsonCalls, JsonCalling, Colchis, Iolcus, Aeaea, Medea, Pelias, JasonRpc (Argonaut is taken).
