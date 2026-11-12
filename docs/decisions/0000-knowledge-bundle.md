---
type: Decision
title: "0000: Project knowledge is an OKF bundle; changes are GitHub issues"
description: "docs/ is an OKF bundle; changes are tracked as GitHub issues."
tags: [decision, process, docs]
status: stable
---

# Context

The design record was one product requirements document. It mixed goals, requirements, decisions and
implementation notes, and grew past the point where a person or an agent could load only the part they needed.
Executable Gherkin scenarios already specify behaviour in detail.

# Decision

- `docs/` is an [Open Knowledge Format](https://github.com/GoogleCloudPlatform/open-knowledge-format) v0.2 bundle:
  one concept per file, YAML frontmatter with a `type`, and a generated `index.md` in every folder. The layout and
  rules are in `AGENTS.md` at the repository root.
- Capabilities state requirements, one file per feature file in `specs/features`. Scenarios live only in Gherkin.
- Decisions are numbered records from 0000, never edited after they are `stable` except to mark them superseded.
- Changes are proposed and tracked as GitHub issues. A design document in `docs/designs/` is written only when an
  issue needs more design than fits in it.
- The bundle has no `log.md`, which OKF allows but does not require. Release notes go on GitHub Releases.
- The `okf` command line tool (okfcli/okf) generates the indexes and validates the bundle, locally and in CI.

# Consequences

- An agent reads `AGENTS.md`, then `docs/index.md`, and follows links to the one file it needs.
- The former PRD is retired; its content is split across `product/`, `capabilities/` and `decisions/`. It remains in
  git history.
- Requirement IDs (`FR-PROTO-01`) are unchanged, so existing scenario tags still trace.
