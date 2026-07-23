## 1. OutputBudget helper

- [x] 1.1 Create `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/Output/OutputBudget.cs` with `DefaultBudgetChars`, `BaseOverheadChars`, `CompactRowChars`, `RichRowChars`, `SnippetRowChars` constants and a single pure `ChooseKeep(totalItems, perItemChars, baseChars, budget)` returning `(Kept, OmittedDueToSize)`
- [x] 1.2 Add unit tests at `tests/DevBitsLab.Mcp.SourceGraph.Tests/OutputBudgetTests.cs` pinning: empty input returns (0,0); under-budget returns full count; over-budget trims the tail and conserves total accounting; tighter per-row budget keeps fewer items; explicit `budget` override is honoured; the worst-case invariant `kept × perItemChars + baseChars ≤ budget` holds

## 2. Lower default limits

- [x] 2.1 Change `find_references` default `limit` from 200 to 50 in `GraphTools.cs:299` and update the parameter `[Description]` accordingly
- [x] 2.2 Change `list_members` default `limit` from 200 to 100 in `GraphTools.cs:1890` and update the parameter `[Description]` accordingly

## 3. Wire the cap into find_references

- [x] 3.1 Add `int omittedSize = 0` parameter to `BuildFindReferencesResult` and conditionally append `("omitted_size", N.ToString())` to the `AudienceMetadata.Build` extras when `omittedSize > 0`
- [x] 3.2 In the `find_references` tool body, after the empty-result short-circuit, call `OutputBudget.ChooseKeep(refs.Count, OutputBudget.CompactRowChars)`, slice `refs` to the kept count when omitted > 0, and pass `omittedSize` to `BuildFindReferencesResult`

## 4. Wire the cap into list_members

- [x] 4.1 Add `int omittedSize = 0` parameter to `BuildListMembersResult` and conditionally append `("omitted_size", N.ToString())` to the metadata extras
- [x] 4.2 In the `list_members` tool body, after the `members` fetch, call `OutputBudget.ChooseKeep(members.Count, OutputBudget.RichRowChars)` and slice + pass `omittedSize` through

## 5. Wire the cap into list_symbols_in_file

- [x] 5.1 Add `int omittedSize = 0` parameter to `BuildListSymbolsInFileResult` and conditionally append `("omitted_size", N.ToString())` to the metadata extras (alongside the existing `symbols` and `file` keys)
- [x] 5.2 In the `list_symbols_in_file` tool body, after the empty-result short-circuit and **before** the per-symbol annotation/history queries, call `OutputBudget.ChooseKeep(hits.Count, OutputBudget.RichRowChars)` and slice `hits` so dropped rows do not incur DB work

## 6. Wire the cap into semantic_search

- [x] 6.1 Add `int omittedSize = 0` parameter to `BuildSemanticSearchResult` and conditionally append `("omitted_size", N.ToString())` to the metadata extras
- [x] 6.2 In the `semantic_search` tool body, after building the `resolved` list, call `OutputBudget.ChooseKeep(resolved.Count, OutputBudget.SnippetRowChars)` and slice + pass `omittedSize` through

## 7. Verification

- [x] 7.1 Run `dotnet build src/DevBitsLab.Mcp.SourceGraph.Server/DevBitsLab.Mcp.SourceGraph.Server.csproj` — must succeed with zero warnings, zero errors
- [x] 7.2 Run the unit test suite (`dotnet test tests/DevBitsLab.Mcp.SourceGraph.Tests`) — all pre-existing tests stay green and the new `OutputBudgetTests` pass
- [x] 7.3 Run the integration test suite (`dotnet test tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests`) — stays green
- [x] 7.4 Confirm `StructuredContentInvariantTests.FindReferences_structuredArrayLength_matchesProseRowCount` and `ListMembers_structuredArrayLength_matchesProseRowCount` continue to pass — verifies the in-lockstep invariant holds across the trim
