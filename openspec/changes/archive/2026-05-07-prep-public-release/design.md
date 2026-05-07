## Context

The repo has been built with a "this will be open-sourced" intent from day one — public
contact emails, a `SECURITY.md` pointing at GitHub Security Advisories, the
`distribution` spec already requires `PackageLicenseExpression=MIT` — but the actual
license artifacts were never wired up. A pre-flip security audit surfaced four small
items, three of which are pure repo housekeeping and one of which (NuGet license
metadata) is an implementation gap against an existing requirement.

This change is intentionally narrow: it closes only the launch-blocking gaps. Broader
NuGet-listing polish (`Description`, `PackageProjectUrl`, `RepositoryUrl`,
`PackageReadmeFile`, tags) is also called for by the existing `distribution` spec but
is non-blocking for the GitHub visibility flip and is left for a follow-up change.

## Goals / Non-Goals

**Goals:**

- Make the GitHub repo legally usable by adding a top-level `LICENSE` file with
  full MIT text so GitHub's license detector recognises it.
- Close the implementation gap against the existing `distribution` spec requirement
  for `PackageLicenseExpression=MIT` in the published `.nupkg`.
- Pack the `LICENSE` file into the `.nupkg` so the license follows the binary into
  every consumer's local cache.
- Strip the personal local-path leak from `CLAUDE.md`.
- Defend against future accidental commits of `.claude/scheduled_tasks.lock`.

**Non-Goals:**

- Filling out the rest of the "Complete NuGet listing metadata" requirement
  (`Description`, `PackageProjectUrl`, `RepositoryUrl`, `PackageReadmeFile`, tags) —
  worth doing, not gating the public flip; tracked as a separate follow-up.
- Choosing a non-MIT license. SECURITY.md and the distribution spec already presume
  MIT; this change does not re-litigate that.
- Scrubbing git history. The personal-path string in `CLAUDE.md` has shipped in
  every prior commit; rewriting history would be churny and the leak is not
  sensitive (just a filename).
- Strong-naming, Authenticode signing, or `.nupkg` icon — explicitly parked in
  `GOVERNANCE.md`.

## Decisions

**Decision 1 — Use both `<PackageLicenseExpression>` and a packed `LICENSE` file.**
NuGet permits exactly one of `<PackageLicenseExpression>` *or* `<PackageLicenseFile>`
inside the nuspec. We pick `<PackageLicenseExpression>MIT</PackageLicenseExpression>`
because it's the SPDX-validated form NuGet recommends and because the existing
`distribution` spec already mandates it. We *also* pack the repo's `LICENSE` file as
ordinary content (`<None Include="LICENSE" Pack="true" PackagePath="\" />`) so the
file is discoverable inside the unpacked package — this is permitted alongside
`<PackageLicenseExpression>` and gives consumers the full text without a network
round-trip to spdx.org.

*Alternative considered:* `<PackageLicenseFile>LICENSE</PackageLicenseFile>` instead
of the expression. Rejected because the existing spec already names the expression form
and because expressions are easier for tooling to validate.

**Decision 2 — Place the props in `Directory.Build.props`, not per-csproj.**
Both packable projects (`Server` → `DevBitsLab.Mcp.SourceGraph.Tool`, `Sdk` →
`DevBitsLab.Mcp.SourceGraph.Sdk`) need the same license metadata. Putting it in
`Directory.Build.props` means one source of truth and zero risk of the two packages
drifting apart. The `<None Include="LICENSE" …>` item also goes in
`Directory.Build.props` with a relative path resolved against `$(MSBuildThisFileDirectory)`
so it works no matter which project does the pack.

*Alternative considered:* duplicate the property + item in both `.csproj` files.
Rejected — central `props` is the established pattern in this repo (Authors,
SourceLink, deterministic build are all already there).

**Decision 3 — `LICENSE` (no extension), not `LICENSE.md`.**
GitHub's license detector (Licensee) prefers `LICENSE` or `LICENSE.txt` over
`LICENSE.md` and historical practice across the .NET / NuGet ecosystem is the
extensionless form. `<PackageLicenseFile>` and `<None Include>` accept any name; we
use the form most likely to be auto-detected.

**Decision 4 — Copyright holder is "DevBitsLab".**
Matches the `<Authors>` and `<Company>` already set in `Directory.Build.props`. The
copyright line will read `Copyright (c) 2026 DevBitsLab`. No individual contributor
copyrights — those are tracked via git history, consistent with most modern
single-org open-source projects.

**Decision 5 — Drop the leaked path entirely; do not replace it with a sanitised
pointer.** The `Plan / milestones:` line in `CLAUDE.md` referenced a one-off planning
doc on a single developer's machine. There's no shared replacement to point to;
deletion is cleaner than fabricating one. The `openspec/` tree is the durable
planning surface.

## Risks / Trade-offs

- **[Risk]** Packing the LICENSE file as `<None>` could conflict with future use of
  `<PackageLicenseFile>` if someone later switches the metadata form.
  → **Mitigation**: a comment near the `<None Include>` element documents that the
  file is *also* packed as content, and that `<PackageLicenseFile>` would need that
  item removed.

- **[Risk]** GitHub's Licensee parser is fussy about the MIT text — small
  reformattings of the boilerplate can cause auto-detection to silently fail and the
  About sidebar to show "View license" instead of "MIT License".
  → **Mitigation**: copy the canonical MIT text verbatim from
  `https://opensource.org/license/mit/` (the SPDX-blessed form), do not reflow.

- **[Risk]** Existing `.nupkg` artifacts published before this change have no
  license — anyone who installed `0.6.x` already accepted "no license" implicitly.
  → **Mitigation**: cut a fresh release after this change lands so the next
  published version carries the metadata. No yank of existing versions; the
  practical exposure is tiny (pre-1.0, low download count).

- **[Risk]** The `.claude/scheduled_tasks.lock` file is currently *untracked* but
  not *ignored*; if this change is implemented out of order someone could `git add`
  it before the `.gitignore` line lands.
  → **Mitigation**: tasks.md sequences the `.gitignore` edit before any other
  change, and the file is small enough to inspect before committing.
