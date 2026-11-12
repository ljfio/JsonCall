---
type: Decision
title: "0005: Mapping oneOf and anyOf to C#"
description: "oneOf/anyOf: polymorphic records, collapse, or a union wrapper."
tags: [decision, generator, FR-GEN]
status: stable
---

# Decision

In order of preference:

1. Every variant pins a common property to a `const`: an abstract record with `[JsonPolymorphic]` and one derived
   record per variant.
2. Every variant maps to the same C# type: that type.
3. Otherwise: a generated union type with `Index`, `Value` and `From...` / `As...` members, reported as Info
   diagnostic `JSONCALL004`.

# Consequences

Documents written by others always generate. Schema owners can get idiomatic types by adding a `const`
discriminator.
