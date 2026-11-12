# AGENTS.md

Instructions for anyone, human or agent, working in this repository. Read this first, then `docs/index.md`, then
only the files your task needs.

JsonCall is a JSON-RPC 2.0 toolkit for .NET 10 with OpenRPC in both directions. Start with
`docs/product/overview.md` for what it is and `docs/product/architecture.md` for how it is built.

## Commands

```sh
dotnet build                    # whole solution (JsonCall.slnx)
dotnet test                     # unit tests and every Gherkin scenario
okf index docs                  # regenerate every docs/**/index.md
okf validate docs               # check the knowledge bundle; must report no errors
```

`okf` is the Open Knowledge Format command line tool (`brew install okfcli/okf/okf`, or a release from
github.com/okfcli/okf). CI pins the version.

## Where things live

| Path | What | Rule |
| --- | --- | --- |
| `src/<Package>/` | One folder per NuGet package | Public APIs get XML docs. |
| `specs/features/NN-name.feature` | Gherkin scenarios, executed by Reqnroll | The only place behaviour is specified in detail. Used only by the tests. |
| `test/JsonCall.Specs/Steps/` | Step definitions | A scenario without steps is reported as skipped. |
| `docs/` | The knowledge bundle (Open Knowledge Format v0.2) | See below. |
| GitHub issues | Bugs, enhancements, proposed changes | Every change starts here. |

## The knowledge bundle: `docs/`

| Folder | `type` | One file per | Holds |
| --- | --- | --- | --- |
| `product/` | `Product`, `Architecture`, `Requirements`, `Roadmap` | topic | Overview, architecture, quality requirements, roadmap. |
| `capabilities/` | `Capability` | feature file | The requirements (`FR-*`) that feature file proves. |
| `decisions/` | `Decision` | decision | Why something is the way it is. |
| `designs/` | `Design` | issue that needs one | A design too large for its issue. Created on demand. |
| `guides/` | `Guide` | topic | User documentation, linked from the README. |
| `index.md` | (reserved) | | **Generated** by `okf index`. Never edit by hand; the root keeps only `okf_version`. |

### Every file

Frontmatter, then a markdown body with `#` headings:

```yaml
---
type: Capability                 # required; one of the types above
title: "05 Streaming"            # required here
description: "One line, at most 80 characters, quoted."   # required here; shown in the index
tags: [capability, FR-STREAM]    # required here
status: draft                    # optional: draft | stable (default) | deprecated
---
```

- Link to other bundle files with relative links (`../decisions/0002-stream-wire-convention.md`) so they work on
  GitHub and in `okf`.
- Refer to anything outside `docs/` as code (`` `specs/features/05-streaming.feature` ``), not a link; `okf` reports
  links outside the bundle as broken.
- One concept per file. If a file passes about 150 lines, split it.

### Capabilities

`capabilities/NN-name.md` mirrors `specs/features/NN-name.feature`: same number, same name, plus
`feature: specs/features/NN-name.feature` in the frontmatter. `00-smoke.feature` tests the harness and has none.

Body: `# Requirements` (each `**FR-AREA-NN** statement`, one or two sentences), then optionally `# Notes`
(limits, implementation facts, links to open issues) and `# Related`.

- State **what** must hold, not how it is tested. Do not copy scenarios; the feature file is the detail.
- Each requirement lives in exactly one capability. A feature file may also prove requirements owned elsewhere;
  say so in one line.
- Every requirement has at least one scenario tagged with its ID (`@FR-STREAM-04`). Never renumber or reuse an
  ID; retire one by marking it `(withdrawn)`.

### Decisions

`decisions/NNNN-slug.md`, numbered from `0000`, next number = highest + 1. Body: `# Context`, `# Decision`,
`# Consequences`. Title is `"NNNN: The decision"`.

- `status: draft` while open, `stable` once decided. A stable decision is not rewritten. To change it, write a new
  decision and mark the old one `status: deprecated` with a line `Superseded by NNNN.`

## Making a change

1. **Issue.** Find or open a GitHub issue. If the change needs real design, write `docs/designs/<issue>-slug.md`
   (`type: Design`) and link it from the issue.
2. **Scenarios first.** Add or change scenarios in `specs/features`, tagged with the requirement ID. A new
   requirement gets the next ID in its capability. A new capability gets a new feature file and a matching
   capability file.
3. **Steps, then code**, until `dotnet test` passes.
4. **Knowledge.** If a requirement changed, update its capability. If something was decided, add a decision. If
   users see the change, update the guide. If it moves the plan, update `product/roadmap.md`.
5. **Bundle.** Run `okf index docs` and `okf validate docs`.
6. **Commit** referencing the issue (`Fixes #12`).

## Releasing

Versions come from git tags through MinVer; never set `Version` in a project file
(`docs/decisions/0010-versioning-and-release.md`). To release, tag `main` and push the tag:

```sh
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

`.github/workflows/release.yml` tests, packs, publishes to NuGet and creates the GitHub Release. Edit the generated
notes there.

## Conventions

- Error codes, wire names and OpenRPC extensions (`x-jsoncall-*`) are public contracts. Changing one is a decision.
- The streaming convention is a draft (`docs/decisions/0002-stream-wire-convention.md`); scenarios depending on it
  are tagged `@draft`.
- No `log.md` and no `CHANGELOG.md`. What changed is in git history and issues; release notes are written on
  GitHub Releases.
- Prose style in docs: short sentences, tables for reference material, no marketing.
