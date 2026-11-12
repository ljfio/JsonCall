---
type: Capability
title: "02 Transports"
description: "One transport seam over WebSocket, TCP, Unix sockets, stdio and HTTP."
tags: [capability, FR-TRANS, transport]
feature: specs/features/02-transports.feature
---

# Requirements

- **FR-TRANS-01** A single `IMessageTransport` seam (send a message, receive messages, close) with identical client
  behaviour across WebSocket, TCP, stdio and HTTP.
- **FR-TRANS-02** TCP and stdio framing is correct for split, coalesced and large messages.
- **FR-TRANS-03** Stream transports (TCP, Unix domain socket, stdio) take a framing choice:
  `MessageFraming.ContentLength` (default) or `MessageFraming.Newline`, one message per `\n` (`\r\n` accepted, blank
  lines ignored), no headers. Newline framing has the same failure semantics and maximum message size as
  Content-Length: `ConnectionLostException` for a close mid-message, `TransportFramingException` for a
  Content-Length header received by a newline reader, `MessageTooLargeException`. It is correct for split, coalesced
  and large messages, and refuses to send a message containing a raw newline.
- **FR-TRANS-04** `UnixSocketMessageTransport.ConnectAsync(path, framing)` and
  `UnixSocketMessageListener(path, framing)` work with either framing. A connect failure (nothing listening, stale
  socket file) is a `ConnectionLostException` at connect, as for TCP. The listener removes its socket file on
  dispose, replaces a stale file, and refuses a path another listener serves.

The feature file also proves FR-CLIENT-05 (connection loss) on each transport.

# Notes

- Newline framing is JSON only; CBOR and MessagePack need Content-Length ([0007](../decisions/0007-package-layout.md)).
- TCP connect failures surface as `ConnectionLostException`, not `SocketException`, so TCP and Unix sockets behave
  the same.
- Unix socket scenarios are skipped, not failed, on Windows.
- Over HTTP, a failed POST is only reported for a single request (issue #2).

# Related

[Transports guide](../guides/transports.md) · [Architecture](../product/architecture.md)
