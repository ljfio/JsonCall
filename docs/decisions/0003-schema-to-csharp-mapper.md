---
type: Decision
title: "0003: Own JSON Schema to C# mapper in the generator"
description: "The generator maps JSON Schema to C# with its own mapper."
tags: [decision, generator, FR-GEN]
status: stable
---

# Context

Client generation needs a JSON Schema to C# mapping that handles `oneOf`, cycles and sibling files. Candidates were
Corvus.Text.Json, NJsonSchema, or our own.

# Decision

Our own mapper, in `JsonCall.Generator/Core`. Analyzers cannot reliably ship System.Text.Json or NJsonSchema, so it
includes a small position-tracking JSON parser.

# Consequences

- Diagnostics carry the file, line and JSON path of the offending schema (FR-GEN-04).
- The generator and the `jsoncall` tool share the code and produce identical output.
- Every supported schema construct is ours to maintain; unsupported ones fall back to `JsonElement` with a
  diagnostic.
