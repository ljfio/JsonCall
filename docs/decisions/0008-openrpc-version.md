---
type: Decision
title: "0008: Generated documents declare OpenRPC 1.3.2"
description: "Documents declare OpenRPC 1.3.2; the generator reads 1.0 to 1.4."
tags: [decision, openrpc, FR-ORPC]
status: stable
---

# Context

The goal was OpenRPC 1.4, but no 1.4 meta-schema has been published. The newest official meta-schema
(`open-rpc/meta-schema` 1.14.9) accepts up to 1.3.2.

# Decision

- Generated documents declare `openrpc: 1.3.2` and are validated against that meta-schema.
- The client generator reads documents declaring 1.0 to 1.4, and warns (`JSONCALL003`) about anything else.

# Consequences

Move to 1.4 when its meta-schema is published ([roadmap](../product/roadmap.md)).
