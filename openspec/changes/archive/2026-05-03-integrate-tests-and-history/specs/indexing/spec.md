## ADDED Requirements

### Requirement: Test framework detection
The indexer SHALL set `symbols.test_framework` to one of `xunit | nunit | mstest` on every method whose attached attributes match the corresponding framework's discriminator (e.g. `[Fact]`, `[Theory]`, `[Test]`, `[TestCase]`, `[TestMethod]`).

#### Scenario: xUnit test method
- **WHEN** a method is decorated `[Fact]`
- **THEN** its symbol row has `test_framework = "xunit"`

#### Scenario: NUnit test method
- **WHEN** a method is decorated `[Test]` and lives inside a `[TestFixture]` class
- **THEN** its symbol row has `test_framework = "nunit"`

### Requirement: Tests edge from test methods to first non-trivial production call
The indexer SHALL emit a `Tests` edge from each test method to the first non-trivial production-code symbol it calls; "non-trivial" excludes other test methods, test fixtures, and test-helper utilities.

#### Scenario: Direct call into production code
- **WHEN** an `[Fact]` test calls `var c = new Calculator(); c.Add(2, 3);`
- **THEN** an edge `(test.id, Calculator.Add.id, Tests)` is emitted

#### Scenario: Test that calls only into test helpers
- **WHEN** a test only calls test-fixture or arrange/assert utilities
- **THEN** no `Tests` edge is emitted; agents fall back to `find_references` for analysis

### Requirement: Git history per symbol
The indexer SHALL maintain a `symbol_history` row per symbol containing the most recent commit sha, author, authored time, and blamed line count, derived from `git blame --line-porcelain` over the symbol's span and cached against `(file_path, content_sha256)`.

#### Scenario: First-time blame
- **WHEN** a file is first indexed in a git working tree
- **THEN** for each indexed symbol in that file, `symbol_history` has a row whose `last_commit_sha` and `last_author` match `git blame` output, and `blamed_content_sha` equals the file's current `content_sha256`

#### Scenario: Cache hit on unchanged file
- **WHEN** the file's `content_sha256` matches `blamed_content_sha`
- **THEN** no `git blame` invocation occurs

#### Scenario: Disable history
- **WHEN** the server is started with `--no-history` or the repo isn't a git working tree
- **THEN** `symbol_history` rows are not written; `who_authored` returns "git history unavailable" and no `git` subprocess is invoked
