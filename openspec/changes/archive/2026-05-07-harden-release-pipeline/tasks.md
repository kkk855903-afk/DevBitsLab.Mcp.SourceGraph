## 1. Add the test gate to the release workflow

- [x] 1.1 Insert a `dotnet test -c Release --no-build --logger "trx;LogFileName=test-results.trx"` step between the existing `Build` and `Pack` steps in `.github/workflows/publish-nuget.yml`.
- [x] 1.2 Add an `actions/upload-artifact@v4` step (with `if: always()`) that publishes `./TestResults/**/*.trx` as `release-test-results`, so a failed release can be triaged from the run page.

## 2. Pack the SDK alongside the Tool

- [x] 2.1 Add a second `dotnet pack` step in `publish-nuget.yml` targeting `src/DevBitsLab.Mcp.SourceGraph.Sdk` with the same `-c Release --no-build -o ./out` shape used for the Tool.
- [x] 2.2 Confirm `dotnet nuget push ./out/*.nupkg --skip-duplicate` already iterates every produced `.nupkg`; no further changes needed for the push step.

## 3. New CI workflow

- [x] 3.1 Add `.github/workflows/ci.yml` triggered on `push` to `main`, `pull_request` against `main`, and `workflow_dispatch`.
- [x] 3.2 Job matrix: `os: [ubuntu-latest, macos-latest, windows-latest]`, `fail-fast: false`.
- [x] 3.3 Steps: checkout (with `fetch-depth: 0` so per-symbol git-blame tests have history), setup .NET 10, NuGet cache keyed on `Directory.Packages.props` + `**/*.csproj`, restore, build with `/p:ContinuousIntegrationBuild=true`, test with `--collect:"XPlat Code Coverage"`.
- [x] 3.4 Upload trx + cobertura artefacts (`if: always()`).
- [x] 3.5 Add a `pack` job downstream of `build-test` that re-builds and re-packs both Tool + SDK as a release-shape smoke test, then uploads the resulting `.nupkg` files. Keeps the release workflow honest by ensuring the `pack` shape works on every PR, not just at tag-push time.

## 4. CodeQL workflow

- [x] 4.1 Add `.github/workflows/codeql.yml` triggered on push, PR, and a weekly Monday cron (`'23 6 * * 1'`).
- [x] 4.2 Use `github/codeql-action/init@v3` with `languages: csharp` and `queries: security-and-quality`.
- [x] 4.3 Build the solution explicitly (`dotnet restore && dotnet build -c Release --no-restore`) before running `analyze@v3` so CodeQL can extract the C# artefacts.

## 5. Dependabot

- [x] 5.1 Add `.github/dependabot.yml` with two `package-ecosystem` blocks: `nuget` (weekly Monday) and `github-actions` (weekly Monday).
- [x] 5.2 Group NuGet updates: `Microsoft.Extensions.*`, `Microsoft.Build.*`, `Microsoft.CodeAnalysis.*`, `xunit*` each in their own group so Roslyn-family bumps land atomically.
- [x] 5.3 Limit open PRs (`open-pull-requests-limit: 10` for nuget, `5` for actions) and label them (`dependencies`, `nuget` / `ci`).

## 6. Deterministic builds + SourceLink

- [x] 6.1 In `Directory.Build.props`, set `Deterministic=true`, `EmbedUntrackedSources=true`, `PublishRepositoryUrl=true`, `IncludeSymbols=true`, `SymbolPackageFormat=snupkg`.
- [x] 6.2 Add a `<PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="all" />` ItemGroup in the same file so every project picks up the SourceLink wiring.
- [x] 6.3 Pin `Microsoft.SourceLink.GitHub` `8.0.0` in `Directory.Packages.props` (central package management is on, so the `<PackageReference>` carries no `Version` attribute).
- [x] 6.4 Pass `/p:ContinuousIntegrationBuild=true` from every `dotnet build` invocation in CI / release workflows so source paths are deterministically normalised.

## 7. Coverage collection in tests

- [x] 7.1 Add `coverlet.collector 6.0.4` to `Directory.Packages.props`.
- [x] 7.2 Add a `<PackageReference Include="coverlet.collector">` block to `tests/DevBitsLab.Mcp.SourceGraph.Tests/DevBitsLab.Mcp.SourceGraph.Tests.csproj` with `PrivateAssets="all"` and the standard `IncludeAssets` payload.
- [x] 7.3 Verify `dotnet test --collect:"XPlat Code Coverage"` emits a `coverage.cobertura.xml` under `TestResults/`.

## 8. Documentation

- [x] 8.1 Add a "Platform support" section to `README.md` listing OS coverage and the published TFM.
- [x] 8.2 Reference the JSON schema (`schema/sourcegraph.schema.json`) under the Platform support section so editors validate `.sourcegraph.json`.
- [x] 8.3 Update `README.md`'s top-of-document TOC to include the new sections.

## 9. Update specs

- [ ] 9.1 On archive, rewrite the existing `Tag-driven NuGet publish workflow` requirement under `openspec/specs/distribution/spec.md` to include the test gate and the dual-package pack step.
- [ ] 9.2 On archive, sync delta into `openspec/specs/distribution/spec.md` (ADDED requirements: PR/main CI matrix, CodeQL static analysis, Dependabot configuration, deterministic-build + SourceLink wiring, Plugin SDK packaging).
