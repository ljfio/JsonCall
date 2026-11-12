---
type: Product
title: Product overview
description: "Problem, goals, non-goals, users and name."
tags: [product]
---

# Problem

.NET has good JSON-RPC 2.0 wire libraries (StreamJsonRpc) but nothing that covers the whole loop a service author
wants:

1. A JSON-RPC server that is a first-class **ASP.NET Core** citizen (endpoints, DI, auth, middleware), including
   **`IAsyncEnumerable<T>` streaming**.
2. A client whose **transport is interchangeable** (WebSocket, TCP, stdio, HTTP with SSE) and whose **wire format is
   interchangeable** (JSON, CBOR, MessagePack).
3. A machine-readable **interface description** (OpenRPC) generated from code and served on `rpc.discover`.
4. **Strongly typed clients generated from OpenRPC** documents, including documents for servers we do not own. The
   OpenRPC generator ecosystem covers TypeScript, Rust and Python, but not C#.

# Goals

| ID | Goal |
| --- | --- |
| G1 | Fully conformant JSON-RPC 2.0: requests, notifications, batches, error codes, by-name and by-position params. |
| G2 | Transport and wire format are independent, swappable seams. Adding a transport never touches protocol code. |
| G3 | Concurrent, correlated calls on one connection; server-to-client notifications; cancellation and timeouts. |
| G4 | ASP.NET Core add-on: `MapJsonRpc(...)` over HTTP, WebSocket and SSE, with DI-resolved handlers and streaming. |
| G5 | Code first: a C# interface is the single source of truth for server dispatch, client proxy and OpenRPC output. |
| G6 | OpenRPC document generation from contracts, served on `rpc.discover`, valid against the official meta-schema. |
| G7 | Spec first: a Roslyn source generator turns an OpenRPC document into records, an interface and a client. |
| G8 | Trimming and Native AOT friendly: no required runtime reflection on the hot path. |

# Non-goals

- JSON-RPC 1.0 and 1.1.
- A custom binary RPC protocol, service mesh features or load balancing.
- Clients in languages other than C#. The OpenRPC document is the bridge to other ecosystems' generators.
- Authentication schemes. Hosting uses ASP.NET Core's.
- Wrapping or depending on StreamJsonRpc ([0001](../decisions/0001-own-wire-layer.md)).

# Users

- **Service author**: writes a C# interface and a handler, and wants an endpoint, a description and a client with
  no boilerplate.
- **Integrator**: consumes a third-party JSON-RPC service that publishes OpenRPC, and wants typed C# calls.
- **Tooling author**: wants a small transport-agnostic core to embed: language servers over stdio, MCP-style servers,
  daemons.

# Name

**JsonCall**: a pun on Jason and JSON-RPC, JavaScript Object Notation Remote Procedure *Call*. See
[0009](../decisions/0009-name.md).
