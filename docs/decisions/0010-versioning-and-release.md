---
type: Decision
title: "0010: Versions come from git tags; a tag publishes to NuGet"
description: "MinVer derives versions from v* tags; pushing a tag releases to NuGet."
tags: [decision, release, packaging]
status: stable
---

# Context

The version was hard-coded in `Directory.Build.props` (`0.1.0-preview.1`). Releasing meant editing it, committing,
and remembering to tag. Nothing published the packages, and release notes have to go on GitHub Releases
([0000](0000-knowledge-bundle.md)).

# Decision

- **MinVer** computes the version of every package under `src/` from the nearest `v*` tag. All packages share one
  version.
- Tag `v0.1.0-preview.1` and that commit is `0.1.0-preview.1`. Untagged commits after it are
  `0.1.0-preview.1.N`, where N is the number of commits since the tag. Before any tag, commits are
  `0.1.0-preview.0.N`. A tag without a suffix is a stable release.
- Pushing a `v*` tag runs `.github/workflows/release.yml`: build, test, pack, check every package matches the tag,
  push to NuGet, then create the GitHub Release with the packages attached and generated notes. Tags with a suffix
  become pre-releases.
- NuGet publishing uses **trusted publishing** (OIDC). There is no long-lived API key in the repository secrets.
- The job runs in the `nuget` environment, so required reviewers can approve each publish.

# Consequences

- Releasing is `git tag v0.1.0-preview.1 && git push origin v0.1.0-preview.1`. No version is edited by hand.
- Builds need full git history. CI checks out with `fetch-depth: 0`; a shallow clone cannot see the tag and builds the wrong version.
- `AssemblyVersion` is `major.0.0.0`, so binding redirects are only needed across major versions.
- One-time setup before the first release: a trusted publishing policy on nuget.org for `ljfio/JsonCall`,
  workflow `release.yml`, environment `nuget`; the repository variable `NUGET_USER` set to the nuget.org user
  name; and the `nuget` environment created on GitHub.
