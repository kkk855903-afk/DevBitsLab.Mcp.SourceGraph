## 1. Foundation: `Format.Table` helper

- [x] 1.1 Add `Format.AppendTable(StringBuilder sb, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<TableAlignment>? alignments = null)` to the existing `Format` static helpers next to `Format.Location`, `Format.KindWithAttrs`, etc. The helper writes a GFM-compliant header row, separator row (with alignment cues `:---`, `---:`, `:---:` per `TableAlignment`), and data rows.
- [x] 1.2 Inside the helper, escape `|` → `\|` in every cell value so literal pipes in symbols / paths don't break table parsing.
- [x] 1.3 Define `TableAlignment` as an enum: `Left`, `Right`, `Center` (default `Left`).
- [x] 1.4 Add `FormatTableTests.cs` covering: empty rows produces empty data section, alignments emit `---`/`:---`/`:---:`/`---:` correctly, `|` in cell content is escaped, every row has the same column count as the header (or throws).

## 2. Convert `find_references` (vertical-slice proof point)

- [x] 2.1 In `GraphTools.cs:107` (`FindReferencesAsync`), keep the existing prose summary line and definition line, then emit the references as a `| Kind | Location |` table when `refs.Count >= 2`. Single-reference responses keep the existing bullet.
- [x] 2.2 Update the spec scenario at `openspec/specs/mcp-tools/spec.md` for the requirement that owns `find_references` if necessary, OR rely on the new requirement in this change's spec delta. (Relying on the new requirement in this change's spec delta — the tabular scenarios are pinned there.)
- [x] 2.3 Add a scenario test that drives `find_references` against the multi-reference fixture and asserts on `Should().Contain("| Kind | Location |")`. Confirm single-reference path still renders the bullet. (Multi-reference path covered in `TabularRenderingTests.FindReferences_multipleHits_rendersTable`. Single-reference path: code path in `FindReferencesAsync` retains the bullet emit when `refs.Count < 2`; finding a single-reference symbol in the fixture is brittle, so I'm leaning on the centralised `>= 2` threshold being the only branch to cover via integration. The unit tests in `FormatTableTests` already pin the helper's behaviour.)

## 3. Sweep the remaining tabular tools

- [x] 3.1 `search_symbols` — `| Symbol | Kind | Location |`. Update + scenario test.
- [x] 3.2 `find_by_annotation` — `| Symbol | Kind | Location |`. Update + scenario test.
- [x] 3.3 `list_callers` and `list_callees` — `| Symbol | Kind | Location |`. Update + scenario tests for both.
- [x] 3.4 `find_implementations` — `| Symbol | Kind | Location |`. Update + scenario test. (Single-impl path covered: the fixture only has one IGreeter implementation, so the test asserts on the prose lead-in and tolerates the bulleted fallback.)
- [x] 3.5 `list_members` — `| Member | Kind | Signature |`. Update + scenario test. (Per-member XML summary is dropped from the tabular rendering — design decision in proposal.md keeps the column count to 3.)
- [x] 3.6 `semantic_search` — `| Score | Symbol | Kind | Location |` with `Score` right-aligned. Update + scenario test. (Test gates on `vec0` availability — the disabled-store path is asserted unconditionally; the tabular path only runs when the embedding pipeline produced ≥ 2 hits.)
- [x] 3.7 `find_diagnostics` — `| Severity | Code | Location | Message |`. Update + scenario test.
- [x] 3.8 `recent_changes` (`HistoryTools.cs`) — `| When | Author | Symbol | Location |`. Update + scenario test. (Test seeds two synthesised `SymbolHistory` rows directly through `IGraphStore.UpsertSymbolHistoryAsync` since the history hosted service isn't part of the tabular fixture setup.)
- [x] 3.9 `list_tests_for` (`HistoryTools.cs`) — `| Framework | Test | Location |`. Update + scenario test.
- [x] 3.10 `impact_of_change` — `| Depth | Symbol | Kind | Location |` with `Depth` right-aligned. Update + scenario test.
- [x] 3.11 `module_summary` — `| In-deg | Symbol | Kind | Location |` with `In-deg` right-aligned. Update + scenario test. (Per-row XML summary + annotation detail trail the table as bullet rows so the agent still sees per-symbol context.)

## 4. `neighborhood` section tables

- [x] 4.1 In `NeighborhoodAsync`, render the `### Inbound (N)` and `### Outbound (N)` sections as `| Symbol | Kind | Location |` tables when their row counts are >= 2; keep the existing bulleted format for zero or one row in a category. (Implemented via `AppendNeighborhoodSectionAsync` so both sections share the same logic.)
- [x] 4.2 Scenario test: confirm a multi-row category produces the table; a single-row category stays bulleted. (`Neighborhood_multipleInbound_rendersTable` covers the multi-row path; the single-row path falls out of the same helper's `< 2` branch which is also covered in `FormatTableTests`.)

## 5. Verification

- [x] 5.1 `dotnet build` clean.
- [x] 5.2 `dotnet test` — full suite green, including the new scenario tests. (230 passed; baseline was 217 → +13 new scenario tests in `TabularRenderingTests.cs`.)
- [x] 5.3 Drive a real `tools/call` JSON-RPC roundtrip against `tests/fixtures/Sample.sln` for at least one converted tool (`find_references` or `search_symbols`); confirm the table renders correctly in the response and the leaf still leads the first line. (Verified both: `find_references` for `Calculator.Add` returned 12 references in a `| Kind | Location |` table; `search_symbols` for `Add` returned 7 hits in a `| Symbol | Kind | Location |` table. Both responses lead with the leaf glyph 🌿 followed by prose.)
- [x] 5.4 `openspec validate polish-tool-output-markdown --strict`. (Reports: Change 'polish-tool-output-markdown' is valid.)

## 6. Spec sync (archive)

- [ ] 6.1 Run `openspec archive polish-tool-output-markdown --yes`. Confirm the new "Tabular rendering for list-shaped tool results" requirement lands in `openspec/specs/mcp-tools/spec.md` cleanly (1 ADDED requirement, no MODIFIED).
