---
type: Capability
title: "07 OpenRPC generation"
description: "OpenRPC document from contracts, served on rpc.discover."
tags: [capability, FR-ORPC, openrpc]
feature: specs/features/07-openrpc-generation.feature
---

# Requirements

- **FR-ORPC-01** The document is generated from registered contracts: `openrpc`, `info`, `methods` (name, params,
  result, errors, deprecated, tags, summary and description from XML docs or attributes).
- **FR-ORPC-02** Required params come before optional ones; a param is optional when it has a default, or is
  nullable with nothing required after it, so wire positions never change. `paramStructure` comes from contract
  settings.
- **FR-ORPC-03** Void and notification methods omit `result`.
- **FR-ORPC-04** Schemas are JSON Schema Draft 07. Shared types are hoisted into `components.schemas` with `$ref`;
  recursive types are supported.
- **FR-ORPC-05** `[Obsolete]` maps to `deprecated`; declared errors map to `errors` with unique codes outside the
  reserved range.
- **FR-ORPC-06** Served on `rpc.discover`; the document validates against the official OpenRPC meta-schema in CI.
- **FR-ORPC-07** Streaming methods carry `x-jsoncall-stream`.

# Notes

- Documents declare `openrpc: 1.3.2` ([0008](../decisions/0008-openrpc-version.md)); schemas come from our own
  mapper ([0004](../decisions/0004-draft07-schema-generator.md)).
- `JsonRpcServerOptions.AddOpenRpc(...)` registers `rpc.discover` through `AddReservedMethod`, so it works on every
  transport. `OpenRpcOptions.Metadata` takes attributes such as `[Authorize]`.

# Related

[OpenRPC guide](../guides/openrpc.md)
