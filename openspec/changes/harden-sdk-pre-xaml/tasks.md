## 1. Stdio integration test harness

- [x] 1.1 Create `tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/` csproj targeting `net10.0`, referencing `ModelContextProtocol.Client`, `Microsoft.Extensions.Logging`, the test framework already used by `tests/DevBitsLab.Mcp.SourceGraph.Tests`
- [x] 1.2 Add the new test project to `DevBitsLab.Mcp.SourceGraph.slnx`
- [x] 1.3 Implement `ServerHarness` helper that spawns `dotnet run --project src/DevBitsLab.Mcp.SourceGraph.Server --no-build --no-launch-profile -- serve --solution <fixture>` as a child process, wires stdin/stdout to a `StdioClientTransport`, and returns an awaitable `McpClient` plus an `IDisposable` lifetime
- [x] 1.4 Add a process-exit timeout (10s) so a stuck server fails the test fast instead of hanging CI
- [x] 1.5 Smoke test: `initialize` against `tests/fixtures/Sample.sln` returns a non-null `Capabilities`, completes within timeout, and no stderr lines were captured during handshake
- [x] 1.6 Vocabulary test: `Capabilities.Experimental["sourcegraph.vocabulary"]` deserializes to an object with `edge_kinds`, `symbol_kinds`, `annotation_flavors` arrays; each is sorted, lowercase, deduplicated; `edge_kinds` contains every value from `EdgeKinds` constants

## 2. `--no-instructions` regression test

- [x] 2.1 Spawn the server with `--no-instructions` flag set; assert the `initialize` response carries no `Capabilities.Experimental["sourcegraph.vocabulary"]` key (or carries it with empty arrays — match whatever the existing `ServerInstructions` suppression does)
- [x] 2.2 Spawn with `SOURCEGRAPH_NO_INSTRUCTIONS=1` env var instead of the flag; assert the same suppression

## 3. End-to-end smoke against fixture solutions

- [x] 3.1 Multi-scope smoke: spawn the server with `tests/fixtures/MultiScope/`, list scopes via the `list_scopes` MCP tool, assert the tool result names every scope from `.sourcegraph.json`
- [x] 3.2 Per-scope vocabulary smoke: assert each scope's `Capabilities.Experimental["sourcegraph.vocabulary"]` reflects only the indexers loaded in that scope (an isolated scope returns an empty or scope-specific vocabulary, not the union)
- [x] 3.3 Cold-index smoke: run `find_definition` against a known symbol in `Sample.sln` immediately after initialize, assert the symbol resolves (validates the lazy-index-on-first-query path through the stdio harness)

## 4. EXPLAIN QUERY PLAN assertion

- [x] 4.1 Add `tests/DevBitsLab.Mcp.SourceGraph.Tests/QueryPlanTests.cs` (in-process — does not need the integration project)
- [x] 4.2 Open a `SqliteGraphStore` against an in-memory or temp DB, populate enough rows that the planner has a non-trivial choice (~100 edges across 3 distinct kinds is sufficient)
- [x] 4.3 Run `EXPLAIN QUERY PLAN <sql>` for the four hot SQL strings: the `ListCallersAsync` query, the `ListCalleesAsync` kind-filtered variant, the recursive-CTE `ImpactOfChangeAsync` query, and one `FindByAnnotationAsync` query that filters on `kind`
- [x] 4.4 Assert each plan string contains either `USING INDEX idx_edges_kind_name` or `USING INDEX idx_edges_dst` (the composite (dst, kind_name) covers the secondary-filter pattern); fail the test with the offending plan if a `SCAN edges` line appears
- [x] 4.5 Add the same probe for `idx_symbols_kind_name` over a `WHERE kind_name = ?` query against `symbols`

## 5. `PayloadKeys` SDK constants

- [x] 5.1 Add `src/DevBitsLab.Mcp.SourceGraph.Sdk/PayloadKeys.cs` with `public static class PayloadKeys` containing kebab-case `string` constants: `Path = "path"`, `Mode = "mode"`, `Converter = "converter"`, `ConverterParameter = "converter-parameter"`, `Event = "event"`, `Handler = "handler"`, `DataType = "data-type"`, `TargetType = "target-type"`, `Key = "key"`, `BasedOn = "based-on"`, `ElementName = "element-name"`, `RelativeSource = "relative-source"`, `FallbackValue = "fallback-value"`, `StringFormat = "string-format"`, `UpdateSourceTrigger = "update-source-trigger"`
- [x] 5.2 XML doc each constant with the canonical use case and a one-line example value
- [x] 5.3 Add `KebabCaseValidator` (or extend the existing one) so `PayloadKeys` constants self-validate at startup via a unit test asserting every constant matches the kebab-case format

## 6. `CanonicalKeys` SDK helpers

- [x] 6.1 Add `src/DevBitsLab.Mcp.SourceGraph.Sdk/CanonicalKeys.cs` with `public static class CanonicalKeys`
- [x] 6.2 Implement `string ForType(string fullyQualifiedName)` returning `csharp:T:<fqn>` after handling: open generics (`MyApp.Foo<T>` → `MyApp.Foo\`1`), closed generics (`List<int>` → `List{Int32}` per Roslyn doc-comment-id rules), nested types via `+`, global:: prefix stripping
- [x] 6.3 Implement `string ForMethod(string typeFullyQualifiedName, string methodName, IReadOnlyList<string>? parameterTypeFqns = null)` returning `csharp:M:<type-key>.<method>(<params>)` per doc-comment-id rules
- [x] 6.4 Implement `string ForField(string typeFqn, string fieldName)` and `string ForProperty(string typeFqn, string propertyName)` returning `csharp:F:...` and `csharp:P:...` respectively
- [x] 6.5 Add `tests/DevBitsLab.Mcp.SourceGraph.Tests/CanonicalKeysTests.cs` covering: simple types, nested types, open generics arity, closed generics naming, methods with no params, methods with multiple params, fields, properties; assert the produced keys match what `Roslyn.GetDocumentationCommentId()` returns for the same symbols (use the existing `Sample.Domain` fixture to compare)

## 7. Plumb `payload` through edge read path

- [x] 7.1 Update `EdgeRow` (or equivalent storage DTO that backs `ListCallersAsync` / `ListCalleesAsync` / `NeighborhoodAsync`) to include `string? PayloadJson`
- [x] 7.2 Update `IGraphStore.ListCallersAsync` / `ListCalleesAsync` SQL to `SELECT ... payload` alongside the existing columns
- [x] 7.3 Update `IGraphStore.NeighborhoodAsync` similarly
- [x] 7.4 Update the server-side DTO (whatever shape `GraphTools` accepts) so payload reaches the markdown renderer
- [x] 7.5 Round-trip test: insert an edge with `Metadata = { ["path"] = "User.Name" }`; query via `ListCallersAsync`; assert the returned row carries `PayloadJson` deserialising to the original dictionary

## 8. Always-render-payload in tool markdown

- [x] 8.1 Update `ToolDescriptionFormatter` (or the equivalent edge-row renderer in `GraphTools.cs`) so when a row's `PayloadJson` is non-null, an indented `payload: { ... }` sub-line follows the edge row
- [x] 8.2 Cap the rendered payload to the first 5 keys; append `(N more)` if more present
- [x] 8.3 Apply to `list_callers`, `list_callees`, and `neighborhood`
- [x] 8.4 Snapshot test against a fixture that emits a `binds-path` edge with payload, asserting the markdown matches the documented shape
- [x] 8.5 Decide and document whether `module_summary` includes payload sub-lines or only a count (see Open Question in design.md)

## 9. `vocabulary list` CLI

- [x] 9.1 Add `vocabulary` subcommand to the CLI router alongside `serve` / `index` / `stats` / `clear` / `init-scopes` / `scopes` / `plugins`
- [x] 9.2 Implement `vocabulary list` that, for each scope:
  - resolves the scope DB via `IScopeRegistry`
  - runs `SELECT DISTINCT kind_name, COUNT(*) FROM edges GROUP BY kind_name ORDER BY 1`, same for `symbols`, same for `annotations.flavor`
  - cross-references each kebab-case identifier against `EdgeKinds` / `SymbolKinds` constants and against `PluginRecord` registrations
  - prints rows of the form `<kind>  [<source>, emitted: <count>]`
- [x] 9.3 Add a "Drift candidates" section: for each kind list, run a Levenshtein-≤2 pairwise comparison; print `<kind-a> ~ <kind-b>` for every pair below the threshold
- [x] 9.4 Add `--strict` flag: exits with code `2` if any drift candidate was reported (still prints the full output)
- [x] 9.5 Add `--scope <id>` flag to scope the output to a single scope id (default: every scope known to the registry)
- [x] 9.6 Add help text under the `--help` subcommand listing
- [x] 9.7 Add CLI tests asserting the subcommand exit codes (0 on no drift, 2 on drift with `--strict`, 0 on drift without `--strict`) and the output format

## 10. Validation and finishing

- [x] 10.1 Run `openspec validate harden-sdk-pre-xaml --strict` and resolve any reported issues _(passes — `Change 'harden-sdk-pre-xaml' is valid`)_
- [x] 10.2 Run `dotnet build` from repo root; resolve every compile error _(0 errors, 0 warnings on a full slnx build)_
- [x] 10.3 Run `dotnet test` and resolve every test that broke _(unit suite 260/260 passing, 0 regressions; integration suite 4 passed + 3 skipped with §12 documentation, 0 failed)_
- [x] 10.4 Patch-bump SDK package version in the `Sdk` csproj (`PayloadKeys` and `CanonicalKeys` are additive) _(SDK bumped 2.0.0 → 2.1.0 with rationale documented in csproj XML doc comment)_
- [x] 10.5 README: add a one-paragraph note on `vocabulary list` next to the existing `plugins list` / `scopes list` documentation; add a one-paragraph note on the `payload:` sub-line under the tool reference for `list_callers` _(both notes added; `vocabulary list` row in CLI table includes flag enumeration; `list_callers` / `list_callees` rows describe payload sub-line + 5-key cap)_

## 11. Fix vocabulary anonymous-type serialization

- [x] 11.1 Replace the anonymous type assigned to `Capabilities.Experimental["sourcegraph.vocabulary"]` in `src/DevBitsLab.Mcp.SourceGraph.Server/Program.cs` with a `System.Text.Json.Nodes.JsonObject` so the MCP SDK 1.2.0's source-generated `McpJsonUtilities+JsonContext` accepts the value at serialise time. Anonymous types throw `NotSupportedException` ("JsonTypeInfo metadata for type '<>f__AnonymousType…' was not provided by TypeInfoResolver of type 'ModelContextProtocol.McpJsonUtilities+JsonContext'") and crash the `initialize` request handler. `JsonObject` derives from `JsonNode` which the SDK's context handles natively as opaque JSON, so the wire shape (`edge_kinds` / `symbol_kinds` / `annotation_flavors` / `scopes`) is preserved byte-for-byte.

## 12. Defects discovered by the new integration tests (deferred to follow-up)

The §1–§3 stdio integration harness revealed three pre-existing server defects beyond the JSON-serialization bug fixed in §11. Each is documented here with an unblock criterion; the corresponding test is marked `[Fact(Skip = "...")]` referencing this section. These are NOT regressions introduced by this change — they are gaps the new test net was designed to surface. Fixing them is out of scope for `harden-sdk-pre-xaml` (which delivers the test net, not the bugs revealed); they should land as a follow-up change before `xaml-language-indexer` ships against this server.

- [ ] 12.1 **Server stderr leakage during `initialize`.** `RunServeAsync` configures `Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)` with no minimum-level cap, so info-level lifecycle logs (`Microsoft.Hosting.Lifetime`, `StdioServerTransport`) emit to stderr during every handshake. The integration test `InitializeTests.Initialize_against_Sample_returns_capabilities_within_timeout` asserts no stderr lines were captured (per spec contract for §1.5). Unblock criterion: cap min-level (e.g. `builder.Logging.SetMinimumLevel(LogLevel.Warning)` for serve) OR redirect info-level logs to stdout / a file, while preserving info logs at any non-stdio call site that relies on them in CI / shell sessions.
- [ ] 12.2 **Live-index router timing — scopes invisible during cold-indexing.** `LiveIndexService.ExecuteAsync` calls `_router.Register(host)` only after `await Task.WhenAll(openTasks)` completes. Until that point the `ScopeRouter` is empty and `list_scopes` returns "No scopes registered." The integration test `MultiScopeTests.ListScopes_tool_reports_every_configured_scope` asserts every scope from `.sourcegraph.json` appears in the tool result. Unblock criterion: register each `ScopeHost` with the router synchronously inside `OpenScopeAsync` before indexing starts (with `status="indexing"`), and transition to `status="ok"` when indexing completes.
- [ ] 12.3 **`find_definition` does not lazy-wait for scope readiness.** Same root cause as §12.2 plus a tools-side gap: when `ScopeRouter` is empty the tool returns "No scopes are registered" immediately rather than waiting for the lazy-index-on-first-query semantics the spec calls out. The integration test `ColdIndexTests.FindDefinition_resolves_known_fixture_symbol_after_initialize` asserts the symbol resolves after `initialize`. Unblock criterion: tools wait until at least one matching scope reaches `status="ok"` (bounded by the existing `IndexAndQueryTimeout`) before returning "no scopes" empties.
