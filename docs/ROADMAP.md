# Roadmap

Where JsonCall is, what stands between it and a 1.0 that teams can depend on, and what it will not try to be.
Written from a review of the implementation in November 2026. The [PRD](PRD.md) remains the design record; this
file is the plan.

## Direction

JsonCall aims to be the .NET library you reach for when a JSON-RPC API has to be **hosted** (ASP.NET Core, every
transport), **described** (OpenRPC out) and **consumed from its description** (OpenRPC in). It does not try to
replace StreamJsonRpc for in-process-pair and IDE scenarios. See [the comparison](comparison.md).

Three commitments follow from that:

1. **Conformance first.** JSON-RPC 2.0 and OpenRPC behaviour is specified in `specs/features` and never regresses.
2. **Interoperate by default.** Anything beyond the two specifications (streaming, cancellation) is a documented,
   versioned extension, and plain JSON-RPC peers keep working without it.
3. **Boring to operate.** Logs, traces, limits and predictable failure, before more features.

## Where it stands

All six phases of the PRD are implemented and 274 scenarios pass. Measured against the PRD's own non-functional
requirements:

| Requirement | Target | Today |
| --- | --- | --- |
| NFR-01 Performance | Benchmarks in CI, one buffer per message | No benchmarks. Each message is copied at least three times. |
| NFR-02 Trimming and AOT | Zero trim warnings in Core, Client, Server | 60 warnings with `IsAotCompatible` on: Core 19, Client 6, Server 20, OpenRpc 15. Transport and AspNetCore are clean. |
| NFR-04 Compatibility | .NET 10 | Met. Revisit below. |
| NFR-05 Quality gates | 85% line coverage on Core, Server, Client | Not measured. |
| NFR-06 Documentation | XML docs on every public API, a quickstart per package | 233 public members undocumented: Core 143, Transport 28, Server 27, OpenRpc 12, AspNetCore 10, Client 5, serializers 8. |
| §8 Interop testing | StreamJsonRpc as a conformance peer | Not present. |
| D2 Streaming convention | Ratified | Draft. Now written down in [streaming.md](streaming.md). |

## 0.1: first preview on NuGet

The goal is a package that is safe to try. Nothing here changes the public API.

### Fix

Found in review. Items 1 to 4 were reproduced with a small program; 5 and 6 are from reading the code. None is
covered by a scenario yet; each should get one. Each is tracked as the GitHub issue with the same number
([#1](https://github.com/ljfio/JsonCall/issues/1) to [#6](https://github.com/ljfio/JsonCall/issues/6)).

| # | Problem | Where | Consequence |
| --- | --- | --- | --- |
| 1 | `JsonRpcCallContext.Cancel()` throws `ObjectDisposedException` once the call has completed, and a `$/cancel` can arrive in that window. | `Handler.cs` `Cancel`; `JsonRpcPeer.cs` `$/cancel` handling and `Shutdown` | A cancel that races a completion faults the read loop and drops the whole connection. In `Shutdown` it can also leave `Completion` never completing. |
| 2 | Over HTTP, a failed POST is only reported when the message is a single request. For a batch it is swallowed. | `HttpMessageTransport.PostAsync` | A batch against an unreachable or failing server hangs until the timeout instead of failing at once. Failed notifications are silent. |
| 3 | An exception thrown by an `OnNotification` handler is swallowed. `OnError` is not called. | `JsonRpcRequestProcessor.HandleItemAsync` | Handler bugs are invisible. |
| 4 | A contract method returning `Task<IAsyncEnumerable<T>>` is accepted by the server but the typed proxy returns the wrong type for it. No test covers it. | `JsonRpcProxy.Invoke` | `InvalidCastException` on the client. Support it or reject it at registration. |
| 5 | On application shutdown a WebSocket connection is disposed at once. | `JsonRpcEndpoint.HandleWebSocketAsync` | In-flight WebSocket calls are cancelled, not completed. FR-HOST-05 promises completion; the scenario exercises HTTP. |
| 6 | No limit on batch length or on concurrent calls per connection. | `JsonRpcRequestProcessor`, `JsonRpcPeer` | One 4 MB batch can start tens of thousands of concurrent calls. Add `MaxBatchSize` and `MaxConcurrentCalls` with safe defaults. |

### Ship

- [x] Pre-release version (`0.1.0-preview.1`) instead of the SDK default `1.0.0`.
- [x] XML documentation files in the packages.
- [x] Source Link, embedded symbols, deterministic CI builds.
- [x] `JsonCall.Tool` as the tool's package id (it was `jsoncall`).
- [x] User documentation: getting started, transports, streaming, OpenRPC, comparison.
- [ ] Reserve the `JsonCall.` prefix on NuGet.
- [ ] Release workflow: a tag builds in Release, packs, and publishes with NuGet trusted publishing.
- [ ] CI matrix on Linux, macOS and Windows. Today only Linux runs, and the Unix socket scenarios skip on Windows.
- [ ] `CHANGELOG.md`, and release notes generated from it.
- [ ] `samples/`: an ASP.NET Core service with a generated client, and a stdio server.
- [ ] Package icon. A short README per package, so each NuGet page opens with that package's own quick start.

## 0.2: operable

What a team needs before running it in production.

- **Logging.** `ILogger` in the peer, the server and the endpoint: connection opened and closed, call failed,
  message dropped. Replace the `OnError` and `OnUnhandledException` callbacks.
- **Tracing.** An `ActivitySource` with one span per call on client and server, following the OpenTelemetry RPC
  conventions, and `traceparent` propagation.
- **Metrics.** A `Meter`: call duration, calls in flight, errors by code, open connections, open streams.
- **Limits.** Batch size and concurrency (fix 6), plus per-connection idle timeout.
- **Stream flow control.** Back-pressure is applied on the connection's read loop, so one unread stream stalls
  every call on its connection. Move to per-stream credit so a slow stream only slows itself.
- **Options.** Bind `JsonRpcServerOptions` and `JsonRpcHostOptions` through `IOptions<T>` so they can come from
  configuration, and allow more than one server per application (keyed).
- **Coverage** measured and gated in CI (NFR-05).
- **Benchmarks** with BenchmarkDotNet against StreamJsonRpc for call throughput and allocations (NFR-01).

## 0.3: the API freeze

Breaking changes belong here, all at once, before anyone depends on the shapes.

- **Decide what is public.** `JsonRpcWire`, `IncomingItem`, `JsonRpcRequestProcessor`, `ProcessResult`,
  `StreamingResult`, `ContractReflection` and `JsonRpcProxy.ProxyImplementation` are plumbing that is public
  today. Make them internal unless there is a reason, then document everything that remains and turn `CS1591` back
  on.
- **Lock it.** Add `Microsoft.CodeAnalysis.PublicApiAnalyzers` and `EnablePackageValidation`, so an accidental
  breaking change fails the build.
- **Namespaces.** A first program needs `JsonCall.Core`, `JsonCall.Core.Model`, `JsonCall.Core.Protocol`,
  `JsonCall.Client` and `JsonCall.Transport`. Consider one `JsonCall` namespace for the types users touch.
- **Transport interface.** `IMessageTransport.ReceiveAsync` returns `byte[]`, and `IMessageSerializer` transcodes
  through a full copy. Move to `ReadOnlySequence<byte>` and `IBufferWriter<byte>` so messages can be pooled.
- **Ratify the stream convention** (D2). Open questions: per-stream flow control, whether `seq` is checked, whether
  HTTP streaming should also be offered as newline-delimited JSON, and alignment with the `$/cancelRequest` and
  `$/progress` names used by LSP.
- **Target frameworks.** Decide whether to add `net8.0` (LTS) for the runtime packages. It widens the audience
  considerably and costs little, because nothing in Core depends on .NET 10 APIs beyond language features.
- **Notifications.** Ordering of concurrent incoming requests is only guaranteed up to a handler's first `await`.
  Document it, or offer an ordered mode for protocols such as LSP that depend on notification order.

## 1.0

- **Native AOT end to end** (NFR-02). A source generator for contract interfaces that emits the client proxy and
  the server dispatch table, replacing `DispatchProxy` and `MethodInfo.Invoke`. Then `IsAotCompatible` on every
  package, enforced in CI by publishing a sample with `PublishAot`.
- **Interop suite.** Run the server against StreamJsonRpc and a JavaScript JSON-RPC client, and the generated
  clients against a non-.NET OpenRPC server, in CI (PRD §8).
- **OpenRPC 1.4** when its meta-schema is published; until then the document stays at 1.3.2.
- **Documentation site** with an API reference generated from the XML docs.
- A stability statement: semantic versioning, and which extensions are stable.

## After 1.0

Ideas, not commitments.

- `IProgress<T>` parameters, mapped onto the stream convention.
- A resumable HTTP transport (`Last-Event-ID`) and compatibility with MCP's Streamable HTTP.
- Named pipes on Windows as a first-class transport.
- An OpenRPC document viewer endpoint, as Swagger UI is for OpenAPI.
- OpenRPC `links`, `examples` and `servers` variables in generated clients.
- An analyzer for contract mistakes: duplicate wire names, unsupported parameter types, `rpc.` prefixes.

## Out of scope

- JSON-RPC 1.0 and 1.1.
- Clients in languages other than C#. The OpenRPC document is the bridge to other ecosystems' generators.
- Authentication schemes. Hosting uses ASP.NET Core's.
- Wrapping StreamJsonRpc (PRD decision D1).
