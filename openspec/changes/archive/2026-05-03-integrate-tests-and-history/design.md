## Context

Two unrelated integrations bundled because both are "free" derived data. Tests are derived from existing graph (with help from the attribute index). History is derived from git, a process call. Both fail safely when the dependency is absent.

## Goals / Non-Goals

**Goals:**
- A `Tests` edge that reflects the strongest single connection between a test and the production code it exercises (not all transitive callees).
- Git history per symbol that's accurate, fast, and gracefully disabled outside git working trees.
- Both deltas are file-scoped and reconcilable on edit.

**Non-Goals:**
- Test coverage in the runtime sense (which lines executed). That's a coverage tool's job; we'd just be guessing without running the tests.
- Mapping integration tests to all symbols they touch via N-th-degree call analysis. That gets noisy fast.
- Deep git history (all changes, blame at all revisions). v1 stores last touch only.

## Decisions

**1. Test detection by attribute (xUnit/NUnit/MSTest).**
When `index-attributes` records `[Fact]`, `[Theory]`, `[Test]`, `[TestMethod]`, `[TestCase]`, etc. on a method, that method is a test. We persist a `test_framework` field on the method's symbol row (`xunit | nunit | mstest`) for quick filtering.

**2. `Tests` edge target = first non-trivial production call.**
Walk the test method's call graph; the first call that crosses into a non-test-project symbol becomes the edge target. "Non-test" means the file's project doesn't end in `.Tests` and the containing type isn't itself a test fixture. If no such call is found, no edge is emitted.

**3. Git blame cached on `(file_path, content_sha256)`.**
On indexing a (re)changed file, we run `git blame --line-porcelain --no-progress <path>` once, parse it into per-line `(commit_sha, author, authored_at)` tuples, and for each indexed symbol pick the line range from `start_line` to `end_line`. Pick the most-recent commit in that range. Store as one row in `symbol_history` keyed by `symbol_id`. Cache key = `(file_path, content_sha256)` — we don't re-run blame if the file content hasn't changed since last time.

**4. History pipeline is async and best-effort.**
The blame call is launched in a background `Channel<HistoryRequest>` so it doesn't block the indexer's hot path. If git isn't available or the repo isn't a git working tree (`.git` missing), the pipeline disables itself with a one-time warning; `who_authored` returns "git history unavailable for this graph" without erroring.

## Risks / Trade-offs

- **Heuristic test→code edge.** Some tests don't directly call the code under test (they use a service container, a fixture, etc.). The first-non-trivial-production-call heuristic gets the obvious cases; for the long tail, the agent falls back to `find_references(symbol = "TestedSymbol")` which lists all callers including tests.
- **Multi-symbol per line.** One `[Theory]` may exercise multiple subjects via parameterised inputs. We emit one `Tests` edge per identifiable production call. Overcounts if the test uses many calls; undercounts on highly indirected tests. Acceptable.
- **Git blame on large files** (>10k lines) takes a few hundred ms. Mitigated by SHA gating and the async pipeline. CI environments shouldn't run git history pipeline; `--no-history` is the escape valve.
- **Author privacy.** Stored authors are the same data that `git log` produces locally. No PII leak vs. existing repo state.
