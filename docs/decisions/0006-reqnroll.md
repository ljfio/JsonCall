---
type: Decision
title: "0006: Reqnroll for executable specifications"
description: "Gherkin feature files run through Reqnroll on xUnit."
tags: [decision, testing]
status: stable
---

# Decision

Acceptance criteria are Gherkin files in `specs/features`, executed by Reqnroll (the maintained successor to
SpecFlow) with xUnit, in `test/JsonCall.Specs`.

# Consequences

- Each feature file maps to one [capability](../capabilities/index.md); scenarios are tagged with requirement IDs.
- Reqnroll generates `*.feature.cs` code-behind on build; it is not committed.
