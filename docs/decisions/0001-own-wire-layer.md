---
type: Decision
title: "0001: Own wire layer rather than building on StreamJsonRpc"
description: "Implement JSON-RPC ourselves instead of wrapping StreamJsonRpc."
tags: [decision, architecture]
status: stable
---

# Context

StreamJsonRpc is the established .NET JSON-RPC library. Building on it would save the protocol layer, but it has no
batches, no HTTP server, no ASP.NET Core endpoint model and no OpenRPC, and its transport and serialization seams
are shaped around its own needs.

# Decision

JsonCall owns its wire layer. It does not wrap or depend on StreamJsonRpc. Decided by the product owner.

# Consequences

- JsonCall can implement all of JSON-RPC 2.0, including batches, and choose its own seams.
- It must earn the trust StreamJsonRpc already has: conformance scenarios, interop tests, performance numbers. See
  the [roadmap](../product/roadmap.md).
- StreamJsonRpc may still be used in tests as a conformance peer.
