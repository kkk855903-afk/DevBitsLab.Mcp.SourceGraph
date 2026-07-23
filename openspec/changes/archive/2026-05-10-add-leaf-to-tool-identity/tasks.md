## 1. Foundation: `ToolIdentityFormatter`

- [x] 1.1 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/ToolIdentityFormatter.cs` exposing `public static void ApplyBrandMark(IEnumerable<McpServerTool> tools)`. Implementation walks the input, filters to built-ins via the declaring-type-`[McpServerToolType]` check, and (when `LeafFormatter.Suppressed` is false) sets `tool.ProtocolTool.Title = "🌿 " + tool.ProtocolTool.Name` and prepends `"🌿 "` to `tool.ProtocolTool.Description` (idempotent on `StartsWith("🌿 ")`).
- [x] 1.2 Add an `IsBuiltInTool(McpServerTool)` private helper on the same class. Logic: `tool.Metadata?.OfType<MethodInfo>().FirstOrDefault()?.DeclaringType?.GetCustomAttribute<McpServerToolTypeAttribute>() is not null`. Mirror the existing pattern in `ToolDescriptionFormatter` for consistency.
- [x] 1.3 Add `ToolIdentityFormatterTests.cs` in `tests/DevBitsLab.Mcp.SourceGraph.Tests/`. Cases: (a) built-in tool with null Title and plain Description gets `🌿 + Name` Title and `🌿 + Description`; (b) idempotency on a Description already starting with `🌿 ` (no double prefix); (c) `LeafFormatter.Suppressed = true` is a no-op (Title stays null, Description unchanged); (d) plugin-style tool (declaring type without `[McpServerToolType]`) is skipped; (e) running the pass twice is equivalent to running once. _Implemented with 8 cases — adds title-already-branded-idempotency, custom-title-prepending, and null-Description-graceful coverage beyond the original five._

## 2. Wire the chokepoint

- [x] 2.1 In `src/DevBitsLab.Mcp.SourceGraph.Server/Program.cs` (around line 349), add a second call immediately after the existing `ToolDescriptionFormatter.ApplyTriggersFromAttributes(...)`:
  ```csharp
  ToolDescriptionFormatter.ApplyTriggersFromAttributes(host.Services.GetServices<McpServerTool>());
  ToolIdentityFormatter.ApplyBrandMark(host.Services.GetServices<McpServerTool>());
  ```
  Sharing the same `IEnumerable<McpServerTool>` source ensures both passes see exactly the registered tool set.
- [x] 2.2 Confirm `LeafFormatter.Suppressed` is set BEFORE this call. Inspect `Program.cs` to verify the suppression-set line (currently around line 255–257 per the existing `add-leaf-brand-mark` wiring) executes earlier in the startup sequence than the post-build mutation pass. _Verified: `LeafFormatter.Suppressed` is set at Program.cs:255, well before the post-build pass at line ~354._

## 3. Wire-level invariant tests

- [x] 3.1 Extend `LeafChokepointInvariantTests.cs` (or a sibling class — design.md leaves this open) with an integration test that drives `tools/list` end-to-end and asserts every built-in tool's `Title` starts with `"🌿 "` and `Description` starts with `"🌿 "`. The catalog enumeration should be reflection-based (find every `[McpServerTool]` method) so it stays current as new tools are added. _Implemented as a sibling class `ToolIdentityInvariantTests.cs` (kept `LeafChokepointInvariantTests` focused on the per-call chokepoint). Catalog enumeration is reflection-based via `[McpServerToolType]` + `[McpServerTool]` — same shape as `ToolTriggerTests.Catalog_everyNonDiagnosticToolDeclaresATrigger`._
- [x] 3.2 Add a sibling test that registers a stub plugin tool through `Plugins.ToolRegistry.AddTool` and asserts its `Title` is null/unbranded and its `Description` does NOT start with `"🌿 "`. Use the same plugin-tool fixture pattern as `LeafChokepointInvariantTests.PluginRegisteredTool_handlerOutput_bypassesChokepoint`. _Implemented in `ToolIdentityInvariantTests.ApplyBrandMark_skipsPluginTool_byDelegatePath`._
- [x] 3.3 Add a suppression test: with `LeafFormatter.Suppressed = true` (joined to `LeafFormatterState` collection to avoid races), drive `tools/list` and confirm every built-in tool's `Title` is null and `Description` does NOT start with `"🌿 "`. Reset `Suppressed` in the `finally` block. _Implemented in `ToolIdentityInvariantTests.ApplyBrandMark_doesNotBrand_whenSuppressed`._

## 4. Suppression matrix extension

- [x] 4.1 In `ServerInstructionsWiringTests.cs` (or the closest equivalent four-cell matrix), extend each of the four cells to also cover `Title` and `Description` of a representative built-in tool (e.g. `find_definition`):
  - (no flags) → ServerInstructions has leaf head; tool has `🌿 ` Title and Description
  - (`--no-leaf`) → ServerInstructions has no leaf; tool has null Title and unbranded Description
  - (`--no-instructions`) → ServerInstructions absent; tool has `🌿 ` Title and Description
  - (both flags) → ServerInstructions absent; tool has null Title and unbranded Description
  _Implemented as parallel file (per task 4.2). Title/Description matrix is orthogonal to `--no-instructions` so the four cells collapse to two distinct outcomes; both pairs asserted explicitly._
- [x] 4.2 If extending the existing matrix bloats the test method, add a parallel `ToolIdentityWiringTests.cs` that mirrors the four-cell shape narrowly for the Title/Description channels. _Implemented in `tests/DevBitsLab.Mcp.SourceGraph.Tests/ToolIdentityWiringTests.cs` (4 cells, all green)._

## 5. Spec delta

- [x] 5.1 Add ADDED requirement *Tool identity brand mark* to `openspec/specs/mcp-tools/spec.md` describing the Title and Description stamping rules. Cover format (`🌿 + Name` for Title, `🌿 ` prefix for Description), built-in scope (declaring-type filter), idempotency, plugin-skip, and the relationship to existing per-call leaf and ServerInstructions head leaf.
- [x] 5.2 Add scenarios: built-in tool gets Title and Description; idempotent re-runs; plugin tool skipped; suppression removes both fields' branding; null Description tolerated; existing trigger-append survives leaf prepend. _All seven scenarios added: built-in catalog entry, plugin skip, idempotency, Title-vs-Name independence, repeatable pass, future tools auto-branded, trigger-append survives._
- [x] 5.3 Modify *Brand-mark suppression* requirement to extend the suppression scope: when `--no-leaf` / `SOURCEGRAPH_NO_LEAF` is set, no built-in tool's `Title` is set to a `🌿 ` value and no `Description` is prefixed with `🌿 ` (in addition to the existing per-call and ServerInstructions suppression).
- [x] 5.4 Leave *Tool response brand mark* unchanged. The per-call chokepoint is unaffected by this proposal.

## 6. Documentation

- [x] 6.1 In `README.md`, add a paragraph alongside the existing leaf documentation explaining the per-tool branding: every built-in tool's `Title` is `🌿 + name` and its `Description` is `🌿 ` prefixed; the `--no-leaf` / `SOURCEGRAPH_NO_LEAF` opt-out covers this surface as well. _Updated the `--no-leaf` flags table entry (line 417) to enumerate all three surfaces, plus the defaults table (line 498)._
- [x] 6.2 Mirror in `CLAUDE.md`: a single sentence under "Tool-usage guidance" noting that per-tool catalog metadata also carries the `🌿 ` brand mark. _Updated the existing leaf paragraph to describe the catalog-identity surface alongside the per-call surface, plus updated the suppression note to cover all three._

## 7. Verification

- [x] 7.1 `dotnet build` — confirm 0 warnings, 0 errors. _0 warnings, 0 errors._
- [x] 7.2 `dotnet test` — confirm the full suite is green with the new test additions. _408/408 passing — was 393 before this change; +15 = 8 ToolIdentityFormatter unit tests + 3 ToolIdentityInvariantTests + 4 ToolIdentityWiringTests._
- [x] 7.3 Manual wire probe: re-run `/tmp/leaf-probe.py` (or its equivalent) capturing `tools/list` and confirm every entry has `title: "🌿 ..."` and a `description` starting with `"🌿 "`. _Default mode: 22/22 titles `"🌿 <name>"`, 22/22 descriptions `🌿 `-prefixed._
- [x] 7.4 Manual smoke under `--no-leaf`: re-run the probe and confirm `title` is null on every entry, `description` does not start with `🌿 `. _With `--no-leaf`: 0 branded titles, 22 null titles, 0 branded descriptions._
- [x] 7.5 Manual smoke under `SOURCEGRAPH_NO_LEAF=1`: same suppression result via env-only path. _With env-var only: 0 branded titles, 22 null titles, 0 branded descriptions — identical to flag-based suppression._
- [x] 7.6 `openspec validate add-leaf-to-tool-identity --strict` — confirm the change is well-formed. _`Change 'add-leaf-to-tool-identity' is valid`._

## 8. Disposition for `move-leaf-to-footer`

- [x] 8.1 Confirm with the user whether to (a) delete `openspec/changes/move-leaf-to-footer/` (never applied; superseded by this change), (b) leave it with a one-line note in `proposal.md` referencing this change, or (c) keep it as-is for a possible future cosmetic move. _User chose (a) delete._
- [x] 8.2 If deleting: remove the directory in the same commit as this change's foundation lands, so reviewers see the supersession in one place. _Directory removed; remaining active changes: `add-leaf-to-tool-identity`, `payload-tooling`, `fix-stranded-reference-edges`._
