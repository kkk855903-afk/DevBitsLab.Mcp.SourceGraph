## Why

Two pieces of context an agent constantly needs but the graph can't answer today:

1. **What tests cover this method?** Critical for safe refactoring. Right now the agent has to grep for the method name across `tests/` and read each match. With Roslyn we can detect xUnit/NUnit/MSTest test methods (`[Fact]`, `[Theory]`, `[Test]`, `[TestMethod]`) and the symbols they exercise via the existing call graph; an explicit `Tests` edge captures the relationship.
2. **Who last touched this method, and when?** When triaging a regression or evaluating ownership of a piece of code, `git blame` is the answer — but the agent has to spawn `git` per file or read raw blame output. Indexing per-symbol last-author / last-commit-sha as part of the graph turns this into one structured query.

Both are local, deterministic, cheap to compute, and require no external services.

## What Changes

- New `Tests` edge kind: source = test method, dst = the method/property the test most directly exercises (heuristic: closest deterministic call into the code under test).
- Test-framework detection via attribute inspection — relies on the `index-attributes` change; `[Fact]`, `[Theory]`, `[Test]`, `[TestMethod]`, `[TestCase]` discriminate test methods; their containing classes can carry `[TestFixture]` / `[TestClass]`.
- New table `symbol_history(symbol_id, last_commit_sha, last_author, last_authored_at, line_count)` populated by parsing `git blame --line-porcelain` for the symbol's span, run lazily and cached against `(file_path, content_sha256)` so we don't re-blame on every reindex.
- New tools `list_tests_for(symbol)` and `who_authored(symbol)`.
- A `--no-history` CLI flag to disable git-blame integration in environments without git or with very large repos.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `indexing`: pass 2 detects test methods and emits `Tests` edges; pass 4 (new) runs git blame per changed file and updates `symbol_history`.
- `storage`: new `symbol_history` table with cache fields.
- `mcp-tools`: new `list_tests_for` and `who_authored` tools.
- `cli`: new `--no-history` flag.

## Impact

- Test framework detection depends on `index-attributes` landing first.
- Git-blame call is on the order of 10-50 ms per file; gated by SHA so it only runs on changes. Negligible.
- `symbol_history` is small: one row per indexed symbol with content roughly 100 bytes. ~50 MB on a 500k-symbol monorepo.
- Repos that don't use git, or are running in a sandbox without git, gracefully degrade: `who_authored` returns "git history unavailable".
