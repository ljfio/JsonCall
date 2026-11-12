---
type: Requirements
title: Quality requirements and test strategy
description: "Non-functional requirements and test strategy."
tags: [product, quality, testing]
---

# Non-functional requirements

Where the implementation stands against each one is tracked in the [roadmap](roadmap.md).

- **NFR-01 Performance.** The client allocates no more than one buffer per message beyond serializer output.
  BenchmarkDotNet tracks calls per second and allocations; a regression over 10% fails CI.
- **NFR-02 Trimming and AOT.** Core, Client and Server publish with `PublishAot` and zero trim warnings when using
  source-generated contexts.
- **NFR-03 Reliability.** No unobserved task exceptions, no hangs on disconnect, and every public async API honours
  cancellation.
- **NFR-04 Compatibility.** .NET 10. Additional target frameworks are an open question for the 0.3 API freeze.
- **NFR-05 Quality gates.** Every capability is specified as Gherkin and runs in CI; line coverage at least 85% on
  Core, Server and Client.
- **NFR-06 Documentation.** XML docs on every public API, and a quick start per package.

# Test strategy

- Each file in `specs/features/` is executed by Reqnroll with xUnit ([0006](../decisions/0006-reqnroll.md)). Step
  definitions live in `test/JsonCall.Specs/Steps`. A scenario without step definitions is reported as skipped.
- Scenarios are tagged with the requirement they prove (`@FR-PROTO-03`). Every requirement in a
  [capability](../capabilities/index.md) has at least one scenario.
- Transport scenarios run against an in-memory pair, real loopback WebSocket, TCP and Unix sockets, and a stdio child
  process, through one shared step set.
- Interop: a raw-JSON harness calls the server. StreamJsonRpc as an external conformance peer is planned, not
  present.
- Generated OpenRPC documents are validated against the official meta-schema. Golden files lock generated documents
  and generated C#.
- Other tags: `@draft` for conventions awaiting ratification, `@phaseN` for delivery order, `@lsp` and `@acp` for
  scenarios using those protocols' contracts.
