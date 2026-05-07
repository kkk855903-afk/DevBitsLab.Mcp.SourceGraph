## 1. Guard the local lock file (do this first)

- [x] 1.1 Append `.claude/scheduled_tasks.lock` to the existing `.claude/` block at the bottom of `.gitignore` (the block currently ending with `.claude/settings.local.json`).
- [x] 1.2 Run `git status` and confirm the working-tree copy of `.claude/scheduled_tasks.lock` flips from untracked (`??`) to ignored (absent from output).

## 2. Add the LICENSE file at the repo root

- [x] 2.1 Create `LICENSE` (no extension) at the repo root, pasting the canonical MIT license text from <https://opensource.org/license/mit/> verbatim — do not reflow lines, do not edit punctuation. Licensee is fussy.
- [x] 2.2 Set the copyright line to `Copyright (c) 2026 DevBitsLab` (matches `<Authors>` / `<Company>` already declared in `Directory.Build.props`).
- [x] 2.3 Verify on disk: `wc -l LICENSE` should report ~21 lines and `head -1 LICENSE` should print `MIT License`.

## 3. Wire NuGet license metadata in Directory.Build.props

- [x] 3.1 Inside the existing `<PropertyGroup>` in `Directory.Build.props`, add `<PackageLicenseExpression>MIT</PackageLicenseExpression>` near the other `Authors` / `Company` / `Product` properties.
- [x] 3.2 Add a new `<ItemGroup>` (or extend the existing one) with `<None Include="$(MSBuildThisFileDirectory)LICENSE" Pack="true" PackagePath="\" Visible="false" />` so every project under the props inherits the file and packs it at the package root. Anchor on `$(MSBuildThisFileDirectory)` so the path resolves the same regardless of which project triggered the pack.
- [x] 3.3 Add a one-line MSBuild comment above the `<None Include="LICENSE" …>` element noting that the file is packed *as content* alongside `<PackageLicenseExpression>` — so future readers don't switch to `<PackageLicenseFile>` and create a duplicate-license error.
- [x] 3.4 Build locally: `dotnet pack src/DevBitsLab.Mcp.SourceGraph.Server -c Release -o ./out` and `dotnet pack src/DevBitsLab.Mcp.SourceGraph.Sdk -c Release -o ./out`.
- [x] 3.5 Inspect each `.nupkg` (`unzip -p ./out/<pkg>.nupkg <pkg>.nuspec | grep license` and `unzip -l ./out/<pkg>.nupkg | grep -i license`). Confirm `<license type="expression">MIT</license>` appears in the nuspec **and** a `LICENSE` entry sits at the package root.

## 4. Strip the personal-path leak from CLAUDE.md

- [x] 4.1 Open `CLAUDE.md` and delete line 123 (`Plan / milestones: \`/Users/jacques/.claude/plans/create-a-plan-to-soft-pizza.md\``) along with any blank line above it that was only there to separate it from the previous section.
- [x] 4.2 Skim the rest of `CLAUDE.md` for any other `/Users/jacques/...` strings (`grep -n "/Users/jacques\|jacques\.bourque" CLAUDE.md`) and confirm only the deliberately-public `jacques.bourque@gmail.com` mention (if any) remains.

## 5. Verify and update specs

- [x] 5.1 Run `openspec validate prep-public-release --strict` — must pass.
- [x] 5.2 Run `dotnet build -c Release` and `dotnet test -c Release --no-build` — both must stay green; this change touches no source code so any regression points at a packaging mistake.
- [x] 5.3 On archive: fold the ADDED requirement from `openspec/changes/prep-public-release/specs/distribution/spec.md` into `openspec/specs/distribution/spec.md` so the live distribution spec records the LICENSE-file requirement.

## 6. Public-flip readiness check (no code change)

- [ ] 6.1 After merge, push to `main` and confirm GitHub's About sidebar surfaces "MIT License" (proof that Licensee parsed the file). If it shows "View license" instead, return to task 2.1 — the boilerplate text was likely altered.
- [ ] 6.2 Cut the next release tag (`vX.Y.Z`) so the published `.nupkg` on nuget.org carries the new metadata. Existing pre-1.0 versions stay as-is — no yank.
- [ ] 6.3 Flip the GitHub repo visibility to public.
