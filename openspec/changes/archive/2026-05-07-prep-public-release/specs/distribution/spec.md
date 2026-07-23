## ADDED Requirements

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
