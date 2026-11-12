---
type: Decision
title: "0004: Own Draft 07 schema generator for OpenRPC output"
description: "Draft 07 schemas from our own mapper, not JsonSchemaExporter."
tags: [decision, openrpc, FR-ORPC]
status: stable
---

# Context

OpenRPC 1.x expects JSON Schema Draft 07. System.Text.Json's `JsonSchemaExporter` emits 2020-12 style output.

# Decision

Our own reflection-based mapper (`SchemaGenerator`) emits exact Draft 07 and hoists shared and recursive types into
`components.schemas`.

# Consequences

- Output validates against the official OpenRPC meta-schema (FR-ORPC-06).
- The mapper uses reflection, which is one of the AOT warnings to resolve before 1.0.
