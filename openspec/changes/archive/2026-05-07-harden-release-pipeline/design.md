## Context

The repo ships a single GitHub Actions workflow today: `publish-nuget.yml`. It does double duty as both the version-bump driver (`workflow_dispatch`) and the NuGet publisher (`v*.*.*` tag push). It does not run tests. Pull requests get no CI coverage at all.

Two adjacent gaps drag on adoption:

1. **Supply-chain hygiene.** No CodeQL means transitive vulnerabilities surface only when a Dependabot bumps them — but there is no Dependabot either. The pinned `System.Security.Cryptography.Xml` 10.0.7 in `Directory.Packages.props` is the kind of fix-up that should be automatic.
2. **Reproducibility.** No `Deterministic` flag, no `ContinuousIntegrationBuild`, no SourceLink. Every build embeds local file paths and timestamps; the published `.snupkg` is less useful than it should be for `step into` debugging from a consumer's IDE.

This change does not touch runtime behaviour. It hardens the release/CI infrastructure and the produced package metadata.

## Goals / Non-Goals

**Goals:**

- A failing test blocks a release. Period.
- Build + test runs on Linux, macOS, and Windows for every push and PR.
- Static-analysis findings show up on the GitHub "Security" tab.
- NuGet-feed dependencies and GitHub Actions are auto-updated on a weekly cadence, grouped to keep PR noise low.
- The published `.snupkg` carries SourceLink-resolvable paths and the `.nupkg` is reproducible from the same source revision.
- The Plugin SDK package ships from the same workflow as the Tool package — a maintainer flipping `<Version>` on the SDK csproj and triggering the workflow is enough to release it.

**Non-Goals:**

- Authenticode signing (deferred — see [GOVERNANCE.md](../../../GOVERNANCE.md#roadmap-items-currently-parked)).
- Strong-naming the assemblies (deferred — same place).
- Multi-TFM (deferred — same place).
- A separate "verify" workflow that re-builds tagged releases to prove byte-for-byte determinism. Worth adding once we hit 1.0; out of scope for this change.
- Codecov integration. Coverage is collected as an artefact; piping it to a third-party service is a follow-up if a maintainer wants the badge.

## Decisions

**1. The release workflow stays as one file (`publish-nuget.yml`).**

Splitting "bump + tag" from "publish" is tempting but doubles the secret-management surface and breaks the one-click `workflow_dispatch` UX. Keep it monolithic; just insert a `dotnet test` step.

**2. The test gate runs on `ubuntu-latest` only inside the release workflow.**

Cross-platform coverage is the CI workflow's job. Re-running matrix tests inside the release workflow would add ~3 minutes for marginal value (the full matrix already ran on the PR that produced the tagged commit). The release workflow tests the *exact* tagged tree, but on one OS.

**3. The CI workflow uses `actions/cache@v4` keyed on `Directory.Packages.props` + `**/*.csproj`.**

Restore time on a cold runner is ~15 seconds; with cache, ~3. Keying on the central props file means a dependency bump invalidates the cache cleanly.

**4. Coverlet output is uploaded as an artefact, not pushed to a third-party service.**

The artefact is enough for a maintainer to download a Cobertura report locally. Wiring up Codecov / Coveralls is reversible later; uploading the file is the lowest-cost path that doesn't bake in a third-party dependency.

**5. CodeQL runs on push + PR + weekly cron.**

The cron handles the case where no PRs land for weeks but a CVE drops on a transitive dep. `security-and-quality` query suite is broader than `security-extended` but still tractable on a small codebase.

**6. Dependabot groups updates by family.**

Single PRs for `Microsoft.Extensions.*`, `Microsoft.Build.*`, `Microsoft.CodeAnalysis.*`, `xunit*`. The Roslyn family in particular wants atomic version bumps because the `Microsoft.CodeAnalysis.*` packages share a major.

**7. Deterministic build / SourceLink wires through `Directory.Build.props`, not per-csproj.**

Three of the seven src projects produce shipping artefacts (Tool, SDK, plus the Server's `.snupkg`); centralising the wiring means none of them has to opt in. `Microsoft.SourceLink.GitHub` is a build-time-only package (`PrivateAssets="all"`) and does not flow into transitive consumers.

**8. The SDK pack step is part of the release workflow but doesn't gate on a separate version.**

The SDK has its own `<Version>` field on its csproj. The release workflow always packs both projects; if neither version changed since the last release, `dotnet nuget push --skip-duplicate` no-ops on the SDK and pushes the Tool. That's the cheapest way to get "SDK release = bump version, push tag".

## Risks / Trade-offs

- **CI matrix triples action minutes.** GitHub provides 2,000 min/month free for public repos; ubuntu-only release + tri-OS PR CI fits comfortably.
- **CodeQL's `security-and-quality` suite is noisier than `security-extended`.** We accept a small triage burden in exchange for catching style/quality smells alongside vulnerabilities.
- **Dependabot grouping can mask a regression in one package within a group.** Mitigation: the CI test gate runs against every Dependabot PR, so a regression surfaces as a red check, not a silent merge.
- **Test gate on the release workflow is single-OS.** A platform-specific regression that slipped past PR CI would still ship. We accept that — it's a tail risk and the alternative (matrix in release) materially slows the release loop.
