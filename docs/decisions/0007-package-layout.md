---
type: Decision
title: "0007: One transport package, and wire formats as JSON transcoders"
description: "One transport package; wire formats transcode to canonical JSON."
tags: [decision, architecture, transport, serialization]
status: stable
---

# Context

The original plan had one package per transport and per serializer, with each serializer producing the message
model directly.

# Decision

- All transports ship in a single `JsonCall.Transport` package.
- An `IMessageSerializer` converts wire bytes to and from canonical JSON. JSON is built into `JsonCall.Core`; CBOR
  and MessagePack are add-on packages. The correlating `JsonRpcPeer` lives in Core.

# Consequences

- The protocol layer is independent of the wire format and is tested once.
- Binary formats pay for a transcoding copy. Moving to pooled buffers is on the
  [roadmap](../product/roadmap.md) for the 0.3 API freeze.
- Newline framing is JSON only; CBOR and MessagePack need `Content-Length` framing.
