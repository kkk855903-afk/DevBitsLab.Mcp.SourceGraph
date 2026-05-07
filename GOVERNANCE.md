# Governance

`DevBitsLab.Mcp.SourceGraph` is an open-source project published under the
[MIT License](LICENSE). This document describes how decisions are made.

## Roles

- **Maintainers** — the people listed in [MAINTAINERS.md](MAINTAINERS.md).
  Maintainers approve PRs, cut releases, and accept or reject design proposals.
- **Contributors** — anyone who opens an issue, files a PR, helps with triage,
  or improves documentation.
- **Users** — anyone running the published `dotnet tool` package or building a
  plugin against the `Sdk` package.

## Decision making

We default to **lazy consensus**: a proposal is considered accepted if no
maintainer objects within a reasonable review window (typically 3 business
days for routine work, longer for substantial changes).

For non-trivial changes — new MCP tools, schema changes to `.sourcegraph.json`,
breaking changes to the plugin SDK, removal of features — open an issue or an
[OpenSpec proposal](openspec/) first. Code is welcome but consensus on the
shape of the change should land before substantial implementation begins.

If maintainers cannot reach consensus, the project's primary maintainer
([@Jak3b0](https://github.com/Jak3b0)) makes the final call. We expect this to
be rare.

## What requires consensus

| Change                                                           | Required approvals          |
|------------------------------------------------------------------|-----------------------------|
| Bug fix, dependency bump, doc/test improvement                   | 1 maintainer                |
| New MCP tool, new scope-config field, new CLI flag               | 1 maintainer + design issue |
| Breaking change to `Sdk` public surface                          | 2 maintainers + design issue|
| Adding a runtime dependency to `Server` or `Sdk`                 | 1 maintainer + rationale    |
| Removing a published tool or CLI flag (deprecation)              | 2 maintainers + 1 release of deprecation notice in `CHANGELOG.md` |

## Versioning policy

The project follows [Semantic Versioning](https://semver.org). Two packages
ship from this repo and version independently:

- **`DevBitsLab.Mcp.SourceGraph.Tool`** (the server / CLI). Pre-1.0 — minor
  bumps may carry breaking changes, with a `CHANGELOG.md` note.
- **`DevBitsLab.Mcp.SourceGraph.Sdk`** (the plugin contract). Post-1.0 — only
  major bumps may break the public surface. Plugins built against `1.x` keep
  working across `Tool` releases until `Sdk 2.0`.

## Deprecation policy

When a feature is deprecated:

1. The release that deprecates it adds a `### Deprecated` entry to
   `CHANGELOG.md` and a runtime warning where practical (CLI flag, plugin SDK).
2. The deprecation must ship in **at least one minor release** before removal.
3. The removal release adds a `### Removed` entry to `CHANGELOG.md`.

## Roadmap items currently parked

These show up in audits but are deliberately deferred. Each entry records the
trade-off so the decision can be revisited when the cost or demand changes.
Open an issue if any of them blocks you.

- **Strong-naming the published assemblies.** Modern .NET (Core / 5+) has no
  GAC and does not require strong-named assemblies; Microsoft's own guidance
  acknowledges strong-naming is largely a legacy concern on this platform.
  Enabling it here would cost a `[InternalsVisibleTo]` rewrite to embed
  public-key tokens across every `src/` project, plus a committed `.snk` and
  matching `Directory.Build.props` wiring. Will be revisited if a regulated
  consumer requires strong-named references against the `Sdk` package.
- **Authenticode signing of published `.nupkg` files.** Requires a
  code-signing certificate from a trusted CA. Will be revisited if a regulated
  user requests it.
- **Multi-TFM (`net8.0;net9.0;net10.0`) for the published tool.** The Server
  uses `System.Threading.Lock` (net9+) and other modern APIs; supporting net8
  LTS would cost real conditional-compilation churn. Net10 is itself an LTS,
  and `dotnet tool install -g` happily resolves the net10 runtime via
  `rollForward`, so the value of multi-TFM is currently limited. The plugin
  `Sdk` already targets `netstandard2.0` — plugin authors are not affected.
- **Authoritative client-compatibility matrix.** We test against current Claude
  Code; other clients (Cursor, Continue, Claude Desktop) work in practice but
  are not part of CI.

## Amending this document

Changes to `GOVERNANCE.md` require approval from **all active maintainers** and
must be opened as a regular PR for public discussion before merging.
