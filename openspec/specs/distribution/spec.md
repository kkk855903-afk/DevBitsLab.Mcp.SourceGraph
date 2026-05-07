# Distribution

## Purpose

Ship the MCP server as a `dotnet tool` package on NuGet with a CI-driven
release flow, so consumers can install it globally
(`dotnet tool install -g`) or pin it per-repo via
`.config/dotnet-tools.json`.

## Requirements

### Requirement: Pack as a .NET tool
The Server project SHALL pack as a NuGet `DotnetTool` package with
`PackAsTool=true`, `ToolCommandName=sourcegraph-mcp`, and the canonical
package id `DevBitsLab.Mcp.SourceGraph.Tool`.

#### Scenario: dotnet pack produces a tool nupkg
- **WHEN** `dotnet pack src/DevBitsLab.Mcp.SourceGraph.Server -c Release`
  is run
- **THEN** a `DevBitsLab.Mcp.SourceGraph.Tool.<version>.nupkg` is emitted
  with `<packageType name="DotnetTool" />` in its nuspec and the
  `sourcegraph-mcp` command exposed

### Requirement: Complete NuGet listing metadata
The package SHALL include `Description`, `Authors`, `PackageTags`,
`PackageLicenseExpression=MIT`, `PackageProjectUrl`, `RepositoryUrl`
(with type=`git`), a top-level `README.md` packed as `PackageReadmeFile`,
and a `.snupkg` symbols package.

#### Scenario: Inspect the published nuspec
- **WHEN** the produced `.nupkg` is unzipped and the nuspec is read
- **THEN** the `<readme>`, `<projectUrl>`, `<repository>` (with commit SHA
  via SourceLink), `<license>`, `<tags>`, and `<packageTypes>` elements are
  all present and non-empty

### Requirement: License artifact ships at repo root and inside the package

The repository SHALL contain a top-level `LICENSE` file (no extension) holding the
canonical MIT license text with copyright attributed to `DevBitsLab`, so GitHub's
license detector recognises the project as MIT and surfaces it on the repository's
About sidebar.

The same `LICENSE` file SHALL be packed into every produced `.nupkg` as ordinary
package content at the package root, so consumers see the license text inside their
local NuGet cache without a network round-trip. This is in addition to — not a
replacement for — the existing `<PackageLicenseExpression>MIT</PackageLicenseExpression>`
metadata required by the "Complete NuGet listing metadata" requirement.

#### Scenario: GitHub recognises the repository license

- **WHEN** the repository is browsed on github.com after this change is merged
- **THEN** the About sidebar displays "MIT License" (rather than "View license"
  or no license badge), confirming Licensee successfully classified the file

#### Scenario: Inspect a produced nupkg for the license file

- **WHEN** `dotnet pack -c Release` is run and the resulting
  `DevBitsLab.Mcp.SourceGraph.Tool.<version>.nupkg` (or `.Sdk.<version>.nupkg`) is
  unzipped
- **THEN** a `LICENSE` file containing the full MIT text is present at the package
  root **AND** the nuspec's `<license type="expression">MIT</license>` element is
  also present

### Requirement: Tag-driven NuGet publish workflow
A GitHub Actions workflow SHALL publish to nuget.org whenever a tag matching
`v*.*.*` is pushed to the repository, using a `NUGET_API_KEY` secret.

#### Scenario: Tag push triggers a publish
- **WHEN** a tag like `v0.2.0` is pushed to `main`
- **THEN** the `Release` workflow restores, builds, packs, and runs
  `dotnet nuget push *.nupkg --source https://api.nuget.org/v3/index.json
  --skip-duplicate`

#### Scenario: Missing API key fails fast
- **WHEN** the workflow runs without `NUGET_API_KEY` set as a secret
- **THEN** the publish step exits with code `1` and a `::error::` message
  pointing at Settings → Secrets

### Requirement: One-click bump-and-release via workflow_dispatch
The same workflow SHALL accept a `workflow_dispatch` input named `version`
that is either `patch | minor | major` or an explicit `x.y.z`, then bump
the csproj `<Version>`, commit as `github-actions[bot]`, tag, and push
before continuing into the publish steps.

#### Scenario: Bump patch from the UI
- **WHEN** the workflow is dispatched with input `version = patch` while
  the csproj reads `<Version>0.1.8</Version>`
- **THEN** the working tree's csproj is updated to `<Version>0.1.9</Version>`,
  a commit `Release 0.1.9` is created, a tag `v0.1.9` is pushed, and the
  publish steps run against the freshly tagged version

#### Scenario: Reject invalid version input
- **WHEN** the workflow is dispatched with `version = nope` or
  `version = 1.2`
- **THEN** the bump step fails with a `::error::` and the publish steps
  do not run
