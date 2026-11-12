---
type: Capability
title: "05 Streaming"
description: "IAsyncEnumerable<T> results streamed over every transport."
tags: [capability, FR-STREAM, streaming]
feature: specs/features/05-streaming.feature
status: draft
---

# Requirements

- **FR-STREAM-01** A contract method returning `IAsyncEnumerable<T>` streams items as they are produced, not
  buffered.
- **FR-STREAM-02** Over HTTP the stream is delivered as Server-Sent Events (`TypedResults.ServerSentEvents`).
- **FR-STREAM-03** Over WebSocket, TCP, Unix sockets and stdio the stream is delivered as `$/stream.*`
  notifications.
- **FR-STREAM-04** Caller cancellation or disconnect stops the producer and disposes the enumerator (finally-block
  semantics).
- **FR-STREAM-05** An exception mid-stream ends the stream with a JSON-RPC error; items already delivered stay
  delivered.
- **FR-STREAM-06** The client consumes a stream as `IAsyncEnumerable<T>` with back-pressure.

# Notes

- The wire convention is a draft ([0002](../decisions/0002-stream-wire-convention.md)), hence `status: draft`.
- Back-pressure applies to the connection's read loop, so an unread stream stalls its whole connection. Per-stream
  flow control is on the [roadmap](../product/roadmap.md).
- A streaming method inside a batch returns error `-32001`.

# Related

[Streaming guide](../guides/streaming.md), including the wire convention for implementers.
