---
type: Capability
title: "08 OpenRPC client generation"
description: "Typed C# client generated from an OpenRPC document."
tags: [capability, FR-GEN, generator, tool]
feature: specs/features/08-openrpc-client-generation.feature
---

# Requirements

- **FR-GEN-01** The source generator reads OpenRPC documents from `AdditionalFiles` and emits records, a contract
  interface and a client class.
- **FR-GEN-02** Draft 07 constructs map to C#: objects, arrays, enums, nullable, `required`, `$ref` (including
  cycles), `allOf`; `oneOf` / `anyOf` follow [0005](../decisions/0005-union-mapping.md).
- **FR-GEN-03** Generated clients wrap any `JsonRpcPeer` and are transport and wire-format agnostic.
- **FR-GEN-04** Output is deterministic, and diagnostics point at the offending file, line and JSON path.
- **FR-GEN-05** A round trip (contract to OpenRPC to generated client) yields a client wire-compatible with the
  original contract.
- **FR-GEN-06** `$ref`s to sibling schema files (`event.schema.json`, `event.schema.json#/definitions/x`) are
  resolved relative to the referring file. The tool reads them from disk; the source generator resolves them
  against other `.json` `AdditionalFiles` (exact relative path, then a unique trailing path, then a unique file
  name). Documents are parsed once, cycles terminate, loaded files are reported (`JSONCALL007`), and an unresolved
  reference is `JSONCALL006` with a `JsonElement` fallback.

# Notes

- Own mapper and parser ([0003](../decisions/0003-schema-to-csharp-mapper.md)). Documents declaring 1.0 to 1.4 are
  read ([0008](../decisions/0008-openrpc-version.md)).
- Generated clients send params by name or by position as `paramStructure` says. Documented errors become exception
  types deriving from `JsonRpcException`.
- A method without a result generates a call that sends a notification.
- Naming: a whole sibling document is named after its file (`event.schema.json` is `Event`), preferring a `title`
  that extends it; a pointer is named after its last segment. Diagnostics inside a sibling point at the `$ref` in
  the main document, with the path continuing as `<document>#<pointer>`. Remote (`https://`) references are not
  fetched.
- Source generators cannot see each other's output, so a `JsonSerializerContext` is only emitted by
  `jsoncall generate --json-context`.
- Third-party documents under test: Petstore and the link example (open-rpc/examples), the Ethereum execution API,
  and OpenRPC renderings of parts of LSP and ACP in `test/JsonCall.Specs/Support/Data/`, locked by golden files.

# Related

[OpenRPC guide](../guides/openrpc.md)
