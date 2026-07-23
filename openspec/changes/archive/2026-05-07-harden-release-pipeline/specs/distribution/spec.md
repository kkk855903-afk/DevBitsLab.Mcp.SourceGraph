## MODIFIED Requirements

### Requirement: Tag-driven NuGet publish workflow
A GitHub Actions workflow SHALL publish to nuget.org whenever a tag matching `v*.*.*` is pushed to the repository, using a `NUGET_API_KEY` secret.

The workflow SHALL run `dotnet test -c Release --no-build` against the full test suite **before** the pack and push steps. A failing test SHALL block the publish.

The workflow SHALL produce two NuGet packages in the same job: the Tool package (`DevBitsLab.Mcp.SourceGraph.Tool`, packed from `src/DevBitsLab.Mcp.SourceGraph.Server`) and the Plugin SDK package (`DevBitsLab.Mcp.SourceGraph.Sdk`, packed from `src/DevBitsLab.Mcp.SourceGraph.Sdk`). Both SHALL be pushed via a single `dotnet nuget push ./out/*.nupkg --skip-duplicate` invocation, so an SDK-only release is a `<Version>` bump on the SDK csproj plus a tag.

The workflow SHALL upload the produced trx test results as a workflow artefact (`release-test-results`) so a failed release can be triaged from the run page.

#### Scenario: Tag push triggers a publish
- **WHEN** a tag like `v0.2.0` is pushed to `main`
- **THEN** the `Release` workflow restores, builds, runs the test suite, packs the Tool and the SDK, and runs `dotnet nuget push *.nupkg --source https://api.nuget.org/v3/index.json --skip-duplicate`

#### Scenario: Failing test blocks the release
- **WHEN** the workflow runs against a tagged commit whose test suite fails
- **THEN** the `Test` step exits non-zero, the workflow halts, the pack and push steps do not execute, and the trx artefact is uploaded so the failure can be inspected from the run page

#### Scenario: Missing API key fails fast
- **WHEN** the workflow runs without `NUGET_API_KEY` set as a secret
- **THEN** the publish step exits with code `1` and a `::error::` message pointing at Settings → Secrets

#### Scenario: SDK-only release
- **WHEN** the SDK csproj's `<Version>` was bumped, the Tool csproj was untouched, and a new tag is pushed
- **THEN** both `dotnet pack` invocations run; the Tool nupkg is pushed but `--skip-duplicate` no-ops it on nuget.org because the version already exists; the SDK nupkg is pushed at its new version

## ADDED Requirements

### Requirement: Push / PR CI workflow with cross-platform matrix
A separate GitHub Actions workflow (`.github/workflows/ci.yml`) SHALL run `dotnet build` and `dotnet test` for every push to `main`, every pull request against `main`, and every manual `workflow_dispatch`, on each of `ubuntu-latest`, `macos-latest`, and `windows-latest`.

The matrix SHALL be configured with `fail-fast: false` so a platform-specific failure does not cancel the others. NuGet packages SHALL be cached, keyed on the contents of `Directory.Packages.props` and every `*.csproj`. Test runs SHALL produce trx logs and Cobertura coverage reports, both uploaded as workflow artefacts.

A downstream `pack` job (running only after the build/test job succeeds) SHALL run the same `dotnet pack` steps the release workflow uses, on a single platform, so the release-shape pack pipeline is exercised on every PR rather than only at tag-push time.

#### Scenario: Pull request opened against main
- **WHEN** a contributor opens a PR against `main`
- **THEN** the `CI` workflow runs three matrix legs (Linux, macOS, Windows), each restoring + building + testing, with the workflow ending green only if every leg ends green; trx + coverage artefacts are attached to the run

#### Scenario: Smoke pack on PR
- **WHEN** the build-test job succeeds for a PR
- **THEN** the `pack` job runs `dotnet pack` for both the Tool and the SDK, uploads the resulting `.nupkg` files as `nupkg`, and exits green

### Requirement: CodeQL static analysis on push, PR, and weekly cron
A GitHub Actions workflow (`.github/workflows/codeql.yml`) SHALL run CodeQL against the C# codebase on every push to `main`, every pull request against `main`, and on a weekly cron (Monday).

The workflow SHALL use `github/codeql-action/init@v3` with `languages: csharp` and `queries: security-and-quality`, build the solution with `dotnet build -c Release` so CodeQL can extract artefacts, then run `analyze@v3` with `category: /language:csharp`.

#### Scenario: Push to main triggers a scan
- **WHEN** a commit is pushed to `main`
- **THEN** the CodeQL workflow runs, uploads its SARIF results to the repository's Security tab, and any new findings appear under the `Code scanning alerts` view

#### Scenario: Weekly cron catches drift
- **WHEN** a Monday rolls over with no PRs in the prior week
- **THEN** the workflow's scheduled run fires, re-evaluates the codebase, and surfaces any newly published query findings against the existing tree

### Requirement: Dependabot configuration for NuGet and Actions
A `.github/dependabot.yml` SHALL declare two ecosystems: `nuget` (rooted at `/`, weekly cadence, Monday) and `github-actions` (rooted at `/`, weekly cadence, Monday).

The `nuget` block SHALL group updates by family — `Microsoft.Extensions.*`, `Microsoft.Build.*`, `Microsoft.CodeAnalysis.*`, `xunit*` — so updates within a family land in a single PR. Both blocks SHALL apply repository labels (`dependencies` plus `nuget` or `ci`) and cap open PRs to 10 (nuget) / 5 (actions).

#### Scenario: Roslyn family bump arrives as one PR
- **WHEN** a new minor of `Microsoft.CodeAnalysis.CSharp.Workspaces` ships and Dependabot runs its weekly check
- **THEN** Dependabot opens a single PR titled like `Bump roslyn group from … to …` listing every `Microsoft.CodeAnalysis.*` package whose version changed, rather than one PR per package

#### Scenario: Action version bump
- **WHEN** `actions/checkout@v4` ships a new minor
- **THEN** Dependabot opens a separate PR under the `github-actions` ecosystem with labels `dependencies` and `ci`

### Requirement: Deterministic builds and SourceLink wiring
The build SHALL produce deterministic, reproducible NuGet packages. The repo's `Directory.Build.props` SHALL set `Deterministic=true`, `EmbedUntrackedSources=true`, and reference the `Microsoft.SourceLink.GitHub` build-time package with `PrivateAssets="all"`. CI builds SHALL pass `/p:ContinuousIntegrationBuild=true` so file paths in the produced PE files are normalised against the repository root.

The Tool and SDK projects SHALL emit a `.snupkg` (`SymbolPackageFormat=snupkg`) carrying SourceLink-resolvable paths so consumers can step into the source from a debugger that supports the SourceLink protocol.

#### Scenario: SourceLink resolves from the published symbol package
- **WHEN** a consumer attaches a debugger configured with SourceLink + nuget.org symbol-server, sets a breakpoint inside `sourcegraph-mcp` shipped as `DevBitsLab.Mcp.SourceGraph.Tool`, and steps into a frame
- **THEN** the debugger pulls the source file from `https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph` at the commit SHA recorded in the `.snupkg`, with line numbers matching the deployed binary

#### Scenario: Two CI builds of the same revision produce identical PE bytes
- **WHEN** the release workflow is run twice against the same git revision (e.g., a re-run from the GitHub Actions UI)
- **THEN** the two emitted `.nupkg` files differ only in their package signature / timestamp metadata; the contained PE files have identical hashes
