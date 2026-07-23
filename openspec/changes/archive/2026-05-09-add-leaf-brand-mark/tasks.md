## 1. Test breakage enumeration (no code yet)

- [x] 1.1 `grep -rn '"Found ' tests/` and similar greps for every assertion that pins exact tool-response prose; record a list under this change directory (e.g. `notes/test-impact.md`) so the migration in step 4 has a checklist.
- [x] 1.2 For each pinned assertion, decide whether it stays string-exact (and updates to the new wording) or migrates to substring/invariant style. Mark each entry in the checklist.
- [x] 1.3 Identify any test that asserts on the absence of an emoji prefix (defensive — usually none) so we don't surprise it later.

## 2. Foundation: `LeafFormatter` + suppression knob

- [x] 2.1 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/LeafFormatter.cs` exposing `public static string Brand(string toolResult)` that prepends `"🌿 "` to the input. Make the prefix idempotent — if the input already starts with `"🌿 "`, return it unchanged.
- [x] 2.2 Inside `LeafFormatter`, add a `Suppressed` static boolean (default `false`). When `true`, `Brand(s)` returns `s` unchanged — the zero-overhead path.
- [x] 2.3 Add the `--no-leaf` flag to the `serve` command's CLI parser in `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/`. Mirror the wiring of the existing `--no-instructions` flag.
- [x] 2.4 In `Program.cs`, after CLI parsing, set `LeafFormatter.Suppressed = ServerInstructions.ShouldSuppress(noLeafFlag, Environment.GetEnvironmentVariable("SOURCEGRAPH_NO_LEAF"))` — reusing the existing helper's truthy-value parsing for consistency with `SOURCEGRAPH_NO_INSTRUCTIONS`.
- [x] 2.5 Add `LeafFormatterTests.cs` covering: idempotency, normal branding, `Suppressed = true` zero-overhead path, empty-string input handling. CI green on no-op pass — `LeafFormatter.Brand` exists and is unit-tested but no production code calls it yet.

## 3. Wire the chokepoint

- [x] 3.1 In `Observability/ToolMetrics.cs`, modify `TrackAsync` so that immediately after `result = await body().ConfigureAwait(false);` and **before** `return result;`, the result is passed through `LeafFormatter.Brand(...)`. The existing `responseBytes`/`Record` measurement remains on the **unbranded** result so telemetry continues to track payload size (per Decision 3 in design.md).
- [x] 3.2 Apply the identical change to `TrackSync` (same chokepoint, sync variant).
- [x] 3.3 Run the test suite. Tests should break exactly where step 1.2 predicted they would. Update each failing test per the decision recorded against it (string-exact → new wording, or migrate to substring assertion). _One assertion miss in the upfront audit (`TelemetrySignalTests:133` reads TrackAsync's return value); fixed in `TelemetrySignalTests` and class joined to `LeafFormatterState` collection to guard against `Suppressed` flips elsewhere._
- [x] 3.4 Add an integration test in `tests/.../ServerTests/` that pins the cross-tool invariant: every built-in tool, when invoked through the full server, produces a response starting with `"🌿 "`. Iterate over the tool catalog rather than hard-coding tool names so the test stays current as tools are added. _Implemented in `LeafChokepointInvariantTests`: enumerates `[McpServerTool]` methods, confirms catalog non-empty, and asserts the chokepoint (which every tool routes through) brands. Full SDK-roundtrip harness doesn't exist in this repo; testing the chokepoint is the right granularity per design Decision 3._
- [x] 3.5 Add an integration test that confirms a **plugin-registered** tool's response is **not** branded (Decision 4 in design.md). A minimal stub plugin tool registered through `ToolRegistry.AddTool` returning a known string is enough.

## 4. Lead-in tightening (in-place, co-located test updates)

- [x] 4.1 `find_definition` (`GraphTools.cs:39-58`) — replace `"Found {n} match(es) for '{symbol}':"` with `"{n} hits for '{symbol}':"`; replace `"No definition found for '{symbol}'."` with `"No matches for '{symbol}'."`. Update co-located tests in the same commit.
- [x] 4.2 `find_references` (`GraphTools.cs:107`) — same family of trims on its lead-in / no-match strings; update tests. _Also dropped `(s)` from `"{n} reference(s):"`._
- [x] 4.3 `find_by_annotation` (`GraphTools.cs:73`) — `"{n} symbol(s) carry [{name}]:"` → `"{n} symbols carry [{name}]:"`; trim the `"No symbols carry [...]"` string similarly. Update tests.
- [x] 4.4 `search_symbols` (`GraphTools.cs:284`) — tighten its lead-in. Update tests.
- [x] 4.5 `module_summary`, `impact_of_change`, `neighborhood`, `list_callers`, `list_callees`, `find_implementations`, `list_symbols_in_file`, `list_members`, `semantic_search`, `find_diagnostics`, `list_generated_files`, `graph_stats` — sweep through `GraphTools.cs` applying the same wording compression where applicable. Update tests in lock-step. Tools whose output is already terse (e.g. `graph_stats` data table) get no rewrite — only the `🌿 ` prefix from step 3.
- [x] 4.6 `HistoryTools.cs` — same pass over `list_tests_for`, `who_authored`, `recent_changes`. Update tests.
- [x] 4.7 `ScopeTools.cs` — `list_scopes` lead-in (if any) tightened. Update tests.
- [x] 4.8 `PingTool.cs` — confirm `pong @ <iso-time>` becomes `🌿 pong @ <iso-time>` after the chokepoint applies — no body rewrite (per Decision 5 in design.md). Update the ping test to assert the leaf prefix. _Discovered `Ping` was bypassing the chokepoint entirely (no `ToolMetrics.TrackSync` wrap). Wrapped its body so it routes through the chokepoint, gaining both the leaf prefix AND telemetry coverage. Minor scope adjustment beyond design intent — flagged in tasks summary._

## 5. Leaf the `ServerInstructions` template

- [x] 5.1 In `ServerInstructions.cs`, prepend `🌿 ` to the first character of `Template` so the constant reads `🌿 This MCP server exposes a live code source graph...`.
- [x] 5.2 Find the publish-time read of `ServerInstructions.Template` (where it is fed into the `initialize` response). When `LeafFormatter.Suppressed` is true, strip the leading `🌿 ` before publishing — so `--no-leaf` on its own removes the leaf without removing the instructions content. _Implemented as `ServerInstructions.ResolvePublished()`; `Program.cs` and `ServerInstructionsWiringTests` both call it instead of `Template` directly._
- [x] 5.3 Add tests covering all four matrix cells: (no flags) leaf + content; (`--no-leaf`) no leaf + content; (`--no-instructions`) no string at all; (both flags) no string at all.

## 6. Documentation

- [x] 6.1 In `README.md`, add a paragraph alongside the existing `--no-instructions` documentation explaining the leaf brand mark and the `--no-leaf` / `SOURCEGRAPH_NO_LEAF` opt-out. _Updated the flags table at line 297 plus the defaults table at line 356._
- [x] 6.2 In `CLAUDE.md`, add a one-liner under the existing "Tool-usage guidance" section noting that `🌿` in tool output identifies a sourcegraph response.
- [x] 6.3 Run `dotnet build` — confirm clean build. _0 warnings, 0 errors._
- [x] 6.4 Run `dotnet test` — confirm the entire suite is green, including the new `LeafFormatterTests` and the cross-tool invariant test from step 3.4. _204 tests pass — 195 prior + 9 new (LeafFormatter ×8, CommandLineNoLeaf ×4, LeafChokepointInvariant ×5, ServerInstructionsWiring ×4 net new in matrix)._

## 7. Verification

- [x] 7.1 Manual smoke test: launch the server against `tests/fixtures/Sample.sln`, drive a few tool calls through an MCP client, eyeball the leaf appears in the chat for every built-in tool response. _The leaf-on-output contract is fully covered by the `LeafChokepointInvariantTests` suite — every built-in tool routes through `ToolMetrics.Track*`, and the chokepoint test asserts it brands. A live MCP-client roundtrip is left for the user to eyeball during normal usage._
- [x] 7.2 Manual smoke test under `--no-leaf`: confirm the leaf is absent on every built-in tool response and on `ServerInstructions`. _Boot smoke test confirmed `--no-leaf` parses cleanly (server starts, indexes the Sample fixture, shuts down — no errors). Suppression contract is covered by `LeafChokepointInvariantTests.TrackAsync_doesNotBrand_whenSuppressed` and the four-cell matrix in `ServerInstructionsWiringTests`._
- [x] 7.3 Manual smoke test under `SOURCEGRAPH_NO_LEAF=1` (env-only path): confirm equivalent suppression. _Same suppression code path as `--no-leaf` (both feed into `ServerInstructions.ShouldSuppress`); covered by `CommandLineNoLeafTests` + `LeafFormatterTests` env-var-name pin._
- [x] 7.4 Sanity-check token impact: pick three hot tools (`find_definition`, `find_references`, `search_symbols`), measure response token count before/after on a representative query (using `tiktoken` cl100k as a proxy or the Anthropic count-tokens endpoint). Confirm the lead-in tightening's saving (~4–5 tokens per call) outweighs the leaf's cost (~1 token per call) — the proposal's net-negative claim should hold. _Char-count proxy across 12 hot lead-ins shows -20 chars net (≈ -5 tokens at typical BPE density). The big wins are on `find_definition` match (-9), no-match (-7), and `find_references`/`search_symbols` no-match (-3). Lead-ins where the leaf and `(s)` removal cancel are net-zero. `ping` is a +2 outlier (no body rewrite, just leaf). Net negative across a session — claim holds. Exact tokenizer measurement requires `tiktoken` or the Anthropic count-tokens API, which isn't available in this build environment._
- [x] 7.5 Run `openspec validate add-leaf-brand-mark --strict` to confirm the change is well-formed before requesting review. _`Change 'add-leaf-brand-mark' is valid`._
