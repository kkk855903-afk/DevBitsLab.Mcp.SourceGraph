# Test impact enumeration

Audit conducted at the start of the apply phase. Greps run from the worktree root:

```bash
grep -rEn '"Found |match\(es\)|symbol\(s\)|"No definition|"No symbol|"No matches|"pong ' tests/
grep -rEn 'snapshot|golden|verifier|VerifyTests' tests/
grep -rEn 'ServerInstructions\.Template|Template\.Should' tests/
```

## Pinned tool-prose assertions: NONE

The current test suite does not pin literal `"Found N match(es)"`, `"N symbol(s) carry"`, `"No definition"`, or `"pong @"` strings anywhere. Tests in `tests/DevBitsLab.Mcp.SourceGraph.Tests/` exercise the storage / SDK / wiring layers and the tool-trigger formatter — none materialise tool response strings to assert on their lead-in.

**Implication for step 3.3:** running the test suite after wiring the chokepoint should be a non-event for assertion failures. The only mechanical breakage risk is at the `Template` level (see below) and in any future MCP-roundtrip tests we add as part of step 3.4 / 3.5.

## `ServerInstructions.Template` assertions

Two call sites read `Template`:

- `tests/DevBitsLab.Mcp.SourceGraph.Tests/ToolTriggerTests.cs:122-123` — asserts `Contain("prefer")` and `Contain("usage_stats")`. **Survives unchanged** after we prepend `🌿 ` because those substrings remain.
- `tests/DevBitsLab.Mcp.SourceGraph.Tests/ServerInstructionsWiringTests.cs:29` — uses `Template` as the value to set on `McpServerOptions.ServerInstructions`. The existing `Should().Contain("prefer")` / `Should().Contain("usage_stats")` (line 40-41) survives. The leaf-prefix scenario added in step 5.3 is new coverage, not a migration of existing coverage.

## Snapshot / golden tests: NONE

No `Verify`/`Snapshooter`/golden-file harness in this repo. So the lead-in tightening can land freely without snapshot regenerations.

## Decisions

| Audit finding | Action |
|---|---|
| No prose-pinning assertions exist | No migration work in step 3.3. Step 3.3 collapses to: run the suite, confirm green. |
| `Template`-substring assertions are leaf-tolerant | Leave as-is. New leaf-prefix scenario in step 5.3 covers the new invariant. |
| `ServerInstructions.Template` is referenced by two test files | Both files re-validated above; no migration. |

## Defensive checks

- `Should().NotContain("🌿")` patterns: NONE (sanity grep `grep -rn '🌿\|herb\|U\+1F33F' tests/` returns nothing). No defensive emoji-absence assertions to surprise us.
- `StartWith` patterns on tool prose: only `SymbolTextBuilderTests.cs:25` which asserts on `SymbolTextBuilder` output (storage layer, not a tool response). Out of scope.
