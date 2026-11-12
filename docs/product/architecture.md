---
type: Architecture
title: Architecture
description: "Packages, dependencies and the three seams."
tags: [product, architecture]
---

# Seams

Three independent seams. A change to one never touches the others.

| Seam | Type | Owns |
| --- | --- | --- |
| Protocol | `JsonRpcPeer` (in `JsonCall.Core`) | Correlation, notifications, batches, cancellation, streams. Sees only whole JSON messages. |
| Wire format | `IMessageSerializer` | Transcoding between wire bytes and canonical JSON, so the protocol is format independent ([0007](../decisions/0007-package-layout.md)). |
| Transport | `IMessageTransport` | Moving whole messages, including framing. |

Framing belongs to the transport. WebSocket and SSE are message oriented already. TCP, Unix domain sockets and stdio
use `Content-Length` header framing by default, or newline framing (`MessageFraming.Newline`).

# Packages

| Package | Responsibility | Depends on |
| --- | --- | --- |
| `JsonCall.Core` | Message model, params, error codes, `IMessageSerializer`, `IMessageTransport`, contract attributes, `JsonRpcPeer` | none |
| `JsonCall.Serializer.Cbor`, `JsonCall.Serializer.MessagePack` | Binary wire formats. JSON is built into Core. | Core |
| `JsonCall.Transport` | WebSocket, TCP, Unix domain socket, stdio and HTTP (with SSE) transports and their framing | Core |
| `JsonCall.Client` | `JsonRpcClient` and the `DispatchProxy` typed proxy | Core |
| `JsonCall.Server` | Transport-neutral dispatcher: method table, parameter binding, error mapping, stream producers, `JsonRpcConnectionSet` | Core |
| `JsonCall.AspNetCore` | `AddJsonRpc`, `MapJsonRpc`, WebSocket and SSE adapters, DI, endpoint metadata | Server |
| `JsonCall.OpenRpc` | OpenRPC object model, document generator, `rpc.discover` | Core, Server |
| `JsonCall.Generator` | Roslyn incremental source generator, `*.openrpc.json` to C# (netstandard2.0 analyzer) | none at runtime |
| `JsonCall.Tool` | The `jsoncall` dotnet tool, sharing the generator's code | none at runtime |

# Repository layout

| Path | Contents |
| --- | --- |
| `src/` | One folder per package. |
| `specs/features/` | Gherkin acceptance scenarios, run by Reqnroll. One file per [capability](../capabilities/index.md). |
| `test/JsonCall.Specs/` | Step definitions (`Steps/`) and test data (`Support/Data/`). |
| `docs/` | This knowledge bundle. |
