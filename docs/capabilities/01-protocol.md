---
type: Capability
title: "01 Protocol"
description: "JSON-RPC 2.0 conformance: ids, notifications, params, batches, errors."
tags: [capability, FR-PROTO, core, server]
feature: specs/features/01-protocol.feature
---

# Requirements

- **FR-PROTO-01** Requests, success responses and error responses follow JSON-RPC 2.0 exactly, including
  `"jsonrpc": "2.0"`.
- **FR-PROTO-02** Ids may be string, number or null. The server echoes the id verbatim. The client generates unique
  ids.
- **FR-PROTO-03** Notifications (no `id`) never produce a response.
- **FR-PROTO-04** Params by position (array) and by name (object); omitted params allowed. Both are accepted by
  default.
- **FR-PROTO-05** A batch returns an array of responses with notifications omitted. An empty batch is Invalid
  Request. An all-notification batch returns nothing.
- **FR-PROTO-06** Standard errors: -32700 parse, -32600 invalid request, -32601 method not found, -32602 invalid
  params, -32603 internal. Application errors are outside -32768 to -32000.
- **FR-PROTO-07** Method names beginning `rpc.` are reserved for the library.

# Notes

- Batch size and concurrent calls per connection are unbounded today (issue #6).

# Related

[0001 Own wire layer](../decisions/0001-own-wire-layer.md) ·
[Getting started](../guides/getting-started.md)
