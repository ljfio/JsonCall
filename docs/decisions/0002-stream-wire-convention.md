---
type: Decision
title: "0002: Streaming wire convention"
description: "$/stream.* notifications, SSE over HTTP, x-jsoncall-stream."
tags: [decision, streaming, FR-STREAM]
status: draft
---

# Context

JSON-RPC 2.0 and OpenRPC define no streaming. JsonCall streams `IAsyncEnumerable<T>` results over every transport,
so it needs a convention that plain JSON-RPC peers tolerate.

# Decision (draft)

- **Stateful transports**: the response to the request is `{"stream": "<id>"}`. The server then sends
  `$/stream.item {stream, seq, value}` notifications, and ends with `$/stream.end {stream}` or
  `$/stream.error {stream, error}`. The caller sends `$/cancel {stream}` to stop.
- **HTTP**: a POST with `Accept: text/event-stream` is answered with SSE. Unnamed events carry items;
  `event: complete` and `event: error` end the stream.
- **OpenRPC**: the method carries `x-jsoncall-stream: true` and its result schema describes one item.

The full convention, for implementers, is in the [streaming guide](../guides/streaming.md). Behaviour is specified in
[05 Streaming](../capabilities/05-streaming.md).

# Open questions before ratifying

- Per-stream flow control instead of back-pressure on the connection's read loop.
- Whether `seq` is checked.
- Whether HTTP should also offer newline-delimited JSON.
- Alignment with LSP's `$/cancelRequest` and `$/progress`.

Ratification is planned for the 0.3 API freeze ([roadmap](../product/roadmap.md)). Scenarios depending on the
convention are tagged `@draft`.
