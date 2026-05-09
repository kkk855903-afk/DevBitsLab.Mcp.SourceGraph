## 1. Foundation: helpers + DTO scaffolding (no behavior change yet)

- [x] 1.1 `LeafFormatter.BrandFirstText(IReadOnlyList<ContentBlock>)` and `BrandFirstText(CallToolResult)` overloads added. First user-visible (non-audience-restricted) `TextContentBlock` gets the `🌿 ` prefix; audience-restricted blocks are skipped. Idempotent and suppression-aware.
- [x] 1.2 `LeafFormatterTests` extended with 8 new scenarios covering both overloads (brands first user-visible text, skips assistant-only blocks, no-op when no text block, no-op on empty list, idempotency, suppression pass-through, CallToolResult variant).
- [x] 1.3 `ToolMetrics.TrackAsync` _additive_ generic-by-overload (not generic-by-T). Two new overloads added: `Func<Task<IReadOnlyList<ContentBlock>>>` and `Func<Task<CallToolResult>>`. _Decision: kept the existing `Func<Task<string>>` overload unchanged so all current tools continue working without modification. Tools opt into richer return types per-method. Cleaner than a single `Track<T>` with runtime dispatch and avoids type-inference surprises at call sites._
- [x] 1.4 Anonymous-type guard **NOT NEEDED — removed.** _On implementation the SDK turned out to type `CallToolResult.StructuredContent` as `JsonElement?` and `CallToolResult.Meta` as `JsonObject?`. Both reject anonymous types at **compile** time; the runtime guard would be unreachable. Updated design decision in design.md and dropped both the guard code and its corresponding test._
- [x] 1.5 `TelemetrySignalTests` extended with three scenarios for the new overloads: content-list path brands the first text block; CallToolResult path brands and preserves StructuredContent; CallToolResult with `IsError = true` records as ok=false in OTel telemetry.
- [x] 1.6 `Resources/GraphResourceUris.cs` added with `Symbol(long id)`, `File(string path)`, `Namespace(string name)` helpers — the canonical URI shape that `GraphResources.cs` will check inbound URIs against in Group 2. _Refactor of `Resources/GraphResources.cs` to use the helpers deferred to Group 2 (where the first tool actually emits resource_links and the round-trip is exercised end-to-end)._
- [ ] 1.7 `Tools/Output/` namespace + `ToolOutputJsonContext` skeleton — **deferred to Group 2.** _Source-gen JsonSerializerContext requires at least one `[JsonSerializable]` attribute to compile; an empty placeholder fails the build. Lands alongside the first DTO (`FindDefinitionResult`) in step 2.1._
- [x] 1.8 `dotnet build` + `dotnet test` clean. **247/247 passing** (was 236 before this group; +11 new tests for the new overloads). Foundation is in place; no tool conversions yet.

## 2. Vertical slice: convert `find_definition`

- [x] 2.1 Define `FindDefinitionResult` and `FindDefinitionHit` records in `Tools/Output/FindDefinitionResult.cs`. Wrapping record structure (`{ Hits: [...] }`) so `outputSchema` can be `"type":"object"` per SDK constraint.
- [x] 2.2 Register both records on `ToolOutputJsonContext` with `[JsonSerializable]` attributes. _Single `[JsonSerializable(typeof(FindDefinitionResult))]` is sufficient — the source generator picks up nested record properties (`FindDefinitionHit`) transitively, no separate registration needed._
- [x] 2.3 Refactor `FindDefinitionAsync` in `GraphTools.cs` — return type now `Task<CallToolResult>`, decorated with `[McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindDefinitionResult))]`, body assembles a leading user-visible prose `TextContentBlock` + one `ResourceLinkBlock` per hit (URIs from `GraphResourceUris.Symbol`) + a trailing audience-restricted `_meta` block with `scope`, `latency_ms`, and `hits` count. `StructuredContent` is serialised via `ToolOutputJsonContext.Default.FindDefinitionResult` (snake_case wire names). _Side-quest: `ScopedExecution.RunAsync` was hard-coded to `Task<string>` — added a sibling `Task<CallToolResult>` overload (handles error / empty / single-host / multi-host fan-out) so the tool can route through the existing scope-resolution logic. No existing call sites changed; the new overload is additive, mirroring the foundation's `ToolMetrics.TrackAsync` pattern (Group 1.3)._
- [x] 2.4 Added `FindDefinitionStructuredOutputTests.cs` — six scenarios pinning: brand-marked prose lead-in, one `ResourceLinkBlock` per hit with `graph://symbol/<id>` URI shape, audience-restricted `_meta` block with `Audience = [Role.Assistant]` and `Priority < 0.5`, `structuredContent` round-trips through `ToolOutputJsonContext` with hit count matching prose, empty-result case still ships `{"hits": []}`, and every emitted resource-link id resolves through `GraphResources.GetSymbolAsync`.
- [x] 2.5 Resource-link resolution test included in 2.4's scenario set (`FindDefinition_everyEmittedResourceLinkUri_resolvesViaGraphResources`); follows each emitted URI through `GraphResources.GetSymbolAsync` and asserts the response isn't the "Symbol id N not found" / "Invalid symbol id" / "No scope" sentinel.
- [x] 2.6 `dotnet test` — **253/253 passing** (was 247 before this group; +6 new scenario tests).
- [x] 2.7 JSON-RPC roundtrip captured against `tests/fixtures/Sample.sln` via in-repo `dotnet run`. Verified: `tools/list` declares `outputSchema.type: object` with a `hits` property for `find_definition`; `tools/call` returns `content` as a 12-block array (1 leading text + 10 resource_link + 1 trailing audience-restricted text), `structuredContent` populated with 10 typed hits; snake_case field names on the wire (`file_path`, `xml_summary`); empty-result case ships `structuredContent.hits = []` and matches the no-match prose.
- [ ] 2.8 **Pause for review.** This is the proof-of-pattern checkpoint. Spec/design choices that need adjustment based on what the vertical slice reveals get captured before the sweep.

## 3. Sweep — symbol-list tools (template work)

Each of the following follows the same pattern as `find_definition`'s vertical slice. Group by similarity to keep diffs scannable.

- [ ] 3.1 `find_references` — `FindReferencesResult { References: [...] }`, each item has `kind`, `filePath`, `line`, `column`, `isGenerated`, `scope?`. Resource link per reference's symbol id.
- [ ] 3.2 `search_symbols` — `SearchSymbolsResult { Hits: [...] }`. Same hit shape as find_definition. Resource link per hit.
- [ ] 3.3 `find_by_annotation` — `FindByAnnotationResult { Hits: [...] }`. Each hit carries `annotations: [...]`. Resource link per hit.
- [ ] 3.4 `list_callers` and `list_callees` — `ListCallersResult { Callers: [...] }`, `ListCalleesResult { Callees: [...] }`. Resource link per row.
- [ ] 3.5 `find_implementations` — `FindImplementationsResult { Implementations: [...] }`. Resource link per row.
- [ ] 3.6 `list_members` — `ListMembersResult { Container, Members: [...] }`. Resource link per member; container link in metadata.
- [ ] 3.7 `list_symbols_in_file` — `ListSymbolsInFileResult { File, Symbols: [...] }`. Resource link per symbol; file link in metadata.
- [ ] 3.8 `neighborhood` — `NeighborhoodResult { Symbol, Inbound: [...], Outbound: [...] }`. Resource link per inbound/outbound row.
- [ ] 3.9 `module_summary` — `ModuleSummaryResult { Namespace, Top: [...] }`. Each row has `inDegree`, `symbol`. Resource link per row.
- [ ] 3.10 `impact_of_change` — `ImpactOfChangeResult { Symbol, Upstream: [...] }`. Each row has `depth`, `symbol`. Resource link per row.
- [ ] 3.11 `semantic_search` — `SemanticSearchResult { Hits: [...] }`. Each hit has `score`, `symbol`. Resource link per hit.

## 4. Sweep — diagnostics, history, and singleton tools

- [ ] 4.1 `find_diagnostics` — `FindDiagnosticsResult { Diagnostics: [...] }`. Each item has `severity`, `code`, `message`, `filePath`, `line`, `column`. No resource link (diagnostics aren't first-class graph entities yet).
- [ ] 4.2 `recent_changes` — `RecentChangesResult { Changes: [...] }`. Each entry has `authoredAt`, `author`, `commitSha`, `symbol`. Resource link per symbol.
- [ ] 4.3 `list_tests_for` — `ListTestsForResult { Symbol, Tests: [...] }`. Each test has `framework`, `testFqn`, `filePath`, `line`. Resource link per test symbol.
- [ ] 4.4 `who_authored` — `WhoAuthoredResult { Symbol, Author, Sha, AuthoredAt, BlamedLines }`. Singleton DTO. No list. Resource link to the symbol.
- [ ] 4.5 `list_generated_files` — `ListGeneratedFilesResult { Files: [...] }`. Each row has `filePath`, `symbolCount`. Resource link per file.
- [ ] 4.6 `list_scopes` — `ListScopesResult { Scopes: [...] }`. Each scope has `id`, `name`, `root`, `status`, `isolated`, `lastIndexedAt`, `projectCount`. No resource link (scopes aren't graph URIs).
- [ ] 4.7 `graph_stats` — `GraphStatsResult { Files, Symbols, References, Edges }`. Singleton DTO. No resource link.

## 5. Audience-restricted metadata blocks across converted tools

- [ ] 5.1 Identify per-tool what should ship as audience-restricted metadata: resolved scope id, query latency, edge-kind defaults, "X of N rows omitted" notices, cache-hit info if surfaced. Document the standard shape in `Tools/Output/AudienceMetadata.cs` (small helper that builds the trailing `TextContentBlock` consistently).
- [ ] 5.2 Audit every converted tool to ensure metadata gets pushed into the audience-restricted block, not into the user-visible prose.
- [ ] 5.3 Scenario test: for one converted tool, assert that the trailing block has `Audience = [Role.Assistant]` and `Priority < 0.5`.

## 6. Test infrastructure migration

- [ ] 6.1 Audit existing tests for any that inspect tool response strings (similar to the `add-leaf-brand-mark` audit). For each, decide: migrate to `CallToolResult.Content[0].Text` substring assertion, OR migrate to `CallToolResult.StructuredContent.Field` typed assertion. Records the decision per test.
- [ ] 6.2 `LeafChokepointInvariantTests` extends to cover both code paths: legacy single-string brands, content-list brands first text block, audience-restricted blocks aren't brand-marked.
- [ ] 6.3 New `StructuredContentInvariantTests`: across every converted tool, `tools/list` returns a non-null `outputSchema`; every `tools/call` response has a non-null `structuredContent`; the structured array length equals the prose row count.
- [ ] 6.4 New `ResourceLinkInvariantTests`: every emitted `ResourceLinkBlock.uri` resolves to a non-null resource via `Resources/GraphResources` (no broken links across the full conversion).

## 7. Documentation

- [ ] 7.1 `README.md` — new section "Structured output and resource links" describing what `structuredContent` agents can consume, the `graph://` URI scheme, and how downstream-tool authors can chain on the structured payload.
- [ ] 7.2 `CLAUDE.md` — one-liner note that built-in `find_*` tools ship typed `structuredContent` for direct programmatic consumption.
- [ ] 7.3 Brief example in `README.md` showing a sample `find_definition` `structuredContent` payload.

## 8. Verification

- [ ] 8.1 `dotnet build` clean.
- [ ] 8.2 `dotnet test` — full suite green.
- [ ] 8.3 Drive a real MCP client roundtrip in VS Code Insiders' Claude extension; eyeball that resource_link cards render (where the extension supports them) and that audience-restricted metadata doesn't appear in the user view.
- [ ] 8.4 Token-cost check: pick three hot tools (`find_definition`, `find_references`, `search_symbols`) and measure response token counts before/after on a representative query. Confirm per-call cost is bounded (~+30 tokens for the structured payload duplicating prose) and that the savings from agents skipping markdown re-parsing are at least directionally evidence-able (proxy: count of follow-up tool calls in a session that previously re-queried the same symbols).
- [ ] 8.5 Run `openspec validate tool-output-content-blocks --strict`.

## 9. Spec sync (archive)

- [ ] 9.1 Run `openspec archive tool-output-content-blocks --yes`. Confirm the new requirements (Multi-content tool responses, Structured content output, Resource-link content items, Audience-restricted metadata content blocks) and the modified `Tool response brand mark` requirement land in `openspec/specs/mcp-tools/spec.md` cleanly.
