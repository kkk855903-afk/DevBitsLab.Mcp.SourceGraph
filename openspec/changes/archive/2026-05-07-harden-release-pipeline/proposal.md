## Why

The existing `Release` workflow restores, builds, packs, and pushes to NuGet — but it doesn't run `dotnet test`. A bad release can ship to NuGet today as long as the *compile* succeeds. That's a real supply-chain risk for a tool published under `DevBitsLab.Mcp.SourceGraph.Tool` that consumers `dotnet tool install -g`.

Beyond the immediate test gate, the release pipeline lacks several artefacts an enterprise consumer expects from a NuGet package they're about to install in their own build infrastructure:

- **No CI on push / PR.** Bugs catch only at release time.
- **No multi-OS coverage.** The server targets developer workstations, but only `ubuntu-latest` ever sees a build.
- **No CodeQL or Dependabot.** Standard supply-chain hygiene.
- **No deterministic-build / SourceLink wiring.** Regulated procurement teams check for both.
- **The Plugin SDK doesn't ship from the same workflow** even though it lives in this repo and versions independently.

The ROADMAP positions the project past Phase 4; pushing to a 1.0 (and earning enterprise installs) needs the release pipeline to match the maturity of the runtime code.

## What Changes

- **`Release` workflow gains a `dotnet test` gate** between build and pack. A failing test blocks the publish.
- **`Release` workflow now packs both the Tool and the SDK** in the same job, so an SDK release is just a `<Version>` bump on `Sdk.csproj`.
- **`Release` workflow uploads the test-results trx** as an artefact so a failed release can be triaged from the run page.
- **New `CI` workflow** (`.github/workflows/ci.yml`) running `dotnet build` + `dotnet test` on `ubuntu-latest`, `macos-latest`, `windows-latest` for every push to `main` and every PR. Coverage is collected via `coverlet.collector` and uploaded as an artefact.
- **New `CodeQL` workflow** (`.github/workflows/codeql.yml`) running on push, PR, and a weekly Monday cron, using the `csharp` language pack and the `security-and-quality` query suite.
- **New Dependabot config** (`.github/dependabot.yml`) covering NuGet (grouped by family) and `github-actions`. Weekly cadence.
- **Deterministic builds + SourceLink** wired centrally in `Directory.Build.props`: `Deterministic=true`, `EmbedUntrackedSources=true`, `Microsoft.SourceLink.GitHub` package reference. CI invocations pass `/p:ContinuousIntegrationBuild=true` so source paths are normalised in the produced PE files.
- **Test project picks up `coverlet.collector`** via `Directory.Packages.props` and a `<PackageReference>` in the test csproj so `dotnet test --collect:"XPlat Code Coverage"` produces a Cobertura report.
- **README adds a "Platform support" section** documenting which platforms run in CI and how to consume the published packages.

## Capabilities

### Modified Capabilities

- `distribution`: the existing "Tag-driven NuGet publish workflow" requirement is rewritten to reflect the test gate and the dual-package pack step. New requirements ADDED for the CI / CodeQL / Dependabot workflows, the deterministic-build wiring, and the Plugin SDK packaging.

## Impact

- **Behaviour at install time**: zero. Consumers running `dotnet tool install` see the same package layout. SourceLink + deterministic builds are only visible through the symbol package and PDB.
- **Workflow runtime**: the release workflow now runs ~8 seconds of tests in addition to the existing build/pack steps. Acceptable.
- **First-failure mode**: the test gate will catch a buggy release at the cost of one rerun. The previous mode silently published it.
- **Repository overhead**: ~5 new files in `.github/`, ~30 lines in `Directory.Build.props` / `Directory.Packages.props`. No source-tree changes outside `tests/` (coverlet) and the central props files.
