---
type: Decision
title: "0011: Release tags publish to NuGet; every push to main is a preview"
description: "vX.Y.Z tags publish to nuget.org; pushes to main publish previews to GitHub."
tags: [decision, release, packaging]
status: stable
---

# Context

[0010](0010-versioning-and-release.md) published every `v*` tag, so a pre-release tag such as `v0.1.0-preview.1`
went to NuGet. The owner wants nuget.org to hold only complete versions, signalling early instability with a
`0.x` major version rather than a suffix, while still being able to try any change before it is released.

# Decision

- **MinVer** computes the version of every package under `src/` from the nearest `v*` tag. All packages share one
  version.
- Tag `v0.1.0` and that commit is `0.1.0`. Untagged commits after it are `0.1.1-preview.0.N`, where N is the number
  of commits since the tag. Before any tag, commits are `0.1.0-preview.0.N`.
- Every push to `main` that passes CI publishes its height version to the repository's **GitHub Packages** feed
  (the `preview` job in `.github/workflows/dotnet.yml`). These never go to nuget.org.
- Only a tag of the form `vMAJOR.MINOR.PATCH` runs `.github/workflows/release.yml`: build, test, pack, check every
  package matches the tag, push to NuGet, then create the GitHub Release with the packages attached and generated
  notes. A tag with a suffix does not match the trigger, and the job also refuses one.
- NuGet publishing uses **trusted publishing** (OIDC). There is no long-lived API key in the repository secrets.
- The job runs in the `nuget` environment, so required reviewers can approve each publish.

# Consequences

- Releasing is `git tag v0.1.0 && git push origin v0.1.0`. No version is edited by hand.
- nuget.org has no pre-releases. To try a change before release, add the GitHub Packages feed
  (`https://nuget.pkg.github.com/ljfio/index.json`) and reference the preview version. GitHub Packages needs a
  token with `read:packages` to restore, even for a public repository.
- While the major version is 0, a minor version may break the API. 1.0 is the stability commitment in the
  [roadmap](../product/roadmap.md).
- Builds need full git history. CI checks out with `fetch-depth: 0`; a shallow clone cannot see the tag and builds
  the wrong version.
- `AssemblyVersion` is `major.0.0.0`, so binding redirects are only needed across major versions.
- One-time setup before the first release: a trusted publishing policy on nuget.org for `ljfio/JsonCall`,
  workflow `release.yml`, environment `nuget`; the repository variable `NUGET_USER` set to the nuget.org user
  name; and the `nuget` environment created on GitHub.
