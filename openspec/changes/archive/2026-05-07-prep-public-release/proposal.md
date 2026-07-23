## Why

The repo is otherwise clean for a public flip on GitHub, but a security-readiness audit
turned up four blockers / hygiene gaps that need to land before the visibility toggle:
no top-level `LICENSE` (so legally "all rights reserved"), the already-spec'd
`PackageLicenseExpression` is missing from the published `.nupkg`, `CLAUDE.md` leaks a
personal local plan path, and `.claude/scheduled_tasks.lock` is unguarded by `.gitignore`.

## What Changes

- Add a top-level `LICENSE` file containing the full MIT license text (copyright
  "DevBitsLab"), so GitHub's license detector picks it up and the repo's About sidebar
  shows "MIT License".
- Wire `<PackageLicenseExpression>MIT</PackageLicenseExpression>` into
  `Directory.Build.props` so every produced `.nupkg` carries its SPDX license — closes
  an existing implementation gap against the `distribution` spec's "Complete NuGet
  listing metadata" requirement.
- Pack the LICENSE into the `.nupkg` via `<None Include="LICENSE" Pack="true"
  PackagePath="\" />` so consumers see it on `nuget.org` and inside their local
  package cache.
- Remove the personal local-path leak `/Users/jacques/.claude/plans/create-a-plan-to-soft-pizza.md`
  from `CLAUDE.md` (currently the last line of the file).
- Add `.claude/scheduled_tasks.lock` to `.gitignore` so the per-session lock file
  cannot drift into a commit.

No spec changes for `mcp-tools`, `indexing`, `storage`, etc. — implementation behaviour is
unchanged.

## Capabilities

### New Capabilities
<!-- None. The LICENSE / .gitignore / CLAUDE.md edits are repo-policy artifacts
     rather than MCP-server capabilities. -->

### Modified Capabilities
- `distribution`: adds a new requirement that the repo SHALL ship a top-level `LICENSE`
  file (full MIT text) and that the file SHALL be packed into every produced `.nupkg`.
  The existing "Complete NuGet listing metadata" requirement already mandates
  `PackageLicenseExpression=MIT`; that wording is unchanged — we are wiring up the
  long-missing implementation in `Directory.Build.props`.

## Impact

- **Repo root**: new `LICENSE` file (MIT, ~1 KB).
- **`Directory.Build.props`**: `<PackageLicenseExpression>` property + `<None Include="LICENSE" …>`
  item so the license file lands in every package. No code changes.
- **`CLAUDE.md`**: drop the "Plan / milestones" line at the bottom of the file.
- **`.gitignore`**: one new line under the existing `.claude/` block.
- **No source code, no public API, no runtime behaviour changes.** Tests are unaffected.
- **Downstream nupkg consumers** see a license expression and the LICENSE file inside
  the package starting from the next release tag.
