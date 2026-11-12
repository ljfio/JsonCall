---
type: Capability
title: "03 Client"
description: "Correlated calls, cancellation, notifications, batches, typed proxy."
tags: [capability, FR-CLIENT, client, core]
feature: specs/features/03-client.feature
---

# Requirements

- **FR-CLIENT-01** Concurrent calls on one connection are correlated by id, in any response order.
- **FR-CLIENT-02** Per-call timeout and `CancellationToken`. Cancelling sends `$/cancel` when the transport is
  stateful (see [05 Streaming](05-streaming.md)).
- **FR-CLIENT-03** Server-to-client notifications reach registered handlers and are never confused with responses.
  On bidirectional transports the server may also call methods the client exposes.
- **FR-CLIENT-04** The batching API returns typed results per entry.
- **FR-CLIENT-05** Connection loss fails every in-flight call with a typed exception; nothing hangs.
- **FR-CLIENT-06** Typed proxy: an interface with `[JsonRpcContract]` becomes a client. `Task` / `ValueTask` methods
  are calls; void or `Task` methods marked `[JsonRpcNotification]` are notifications.

# Notes

- A cancel racing a completed call can drop the connection (issue #1).
- Exceptions from `OnNotification` handlers are swallowed (issue #3).
- `Task<IAsyncEnumerable<T>>` contract methods fail in the proxy (issue #4).

# Related

[Getting started](../guides/getting-started.md)
