---
type: Capability
title: "06 ASP.NET Core hosting"
description: "MapJsonRpc: POST, SSE and WebSocket on one route, DI and auth."
tags: [capability, FR-HOST, aspnetcore]
feature: specs/features/06-aspnetcore.feature
---

# Requirements

- **FR-HOST-01** `app.MapJsonRpc("/rpc")` exposes POST (single and batch), WebSocket upgrade and SSE on one route,
  and returns an `IEndpointConventionBuilder` so authorization, CORS and rate limiting apply.
- **FR-HOST-02** Handlers resolve through DI per call, with a scope per request or message.
- **FR-HOST-03** Contract implementations are registered with `AddJsonRpc(...)`; duplicate method names fail at
  startup with a clear error.
- **FR-HOST-04** Endpoint metadata (names, route) is visible to ASP.NET Core, and `[Authorize]` works on contracts
  and methods.
- **FR-HOST-05** Graceful shutdown completes in-flight calls and closes streams.

# Notes

- FR-HOST-05 is exercised over HTTP only; WebSocket calls are cancelled on shutdown (issue #5).

# Related

[Getting started](../guides/getting-started.md)
