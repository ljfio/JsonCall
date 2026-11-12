---
type: Capability
title: "10 Generated client notifications"
description: "Typed On<Name> handlers for server-pushed notifications."
tags: [capability, FR-GEN, generator]
feature: specs/features/10-generated-client-notifications.feature
---

# Requirements

- **FR-GEN-07** A result-less method marked `"x-jsoncall-direction": "server-to-client"` generates a typed
  `On<Name>` handler registration instead of a call, and is left off the call interface. Handler arguments follow
  the document's param order whether pushed by name or by position; omitted optional params arrive as `null`;
  registering again replaces the previous handler.

The feature file also proves FR-GEN-03 for these clients.

# Notes

- Without the marker a result-less method is a call that sends a notification; an `...EventAsync` method is not a
  subscription.
- Requests from the server to the client (for example ACP `fs/read_text_file`) are not covered; they need a handler,
  not a subscription.

# Related

[OpenRPC guide](../guides/openrpc.md) · [09 Broadcast](09-broadcast.md) for the sending side
