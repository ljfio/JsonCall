---
type: Capability
title: "04 Serialization"
description: "JSON, CBOR and MessagePack with identical round trips."
tags: [capability, FR-SER, serialization]
feature: specs/features/04-serialization.feature
---

# Requirements

- **FR-SER-01** Identical logical messages round-trip through JSON, CBOR and MessagePack.
- **FR-SER-02** `params` round-trips by position and by name in every wire format.
- **FR-SER-03** Custom `JsonSerializerOptions` and source-generated `JsonSerializerContext` are honoured.
- **FR-SER-04** Malformed input yields a parse error, never an unhandled exception.

# Notes

Wire formats transcode to canonical JSON ([0007](../decisions/0007-package-layout.md)).

# Related

[Transports and wire formats guide](../guides/transports.md)
