## ADDED Requirements

> **Implementation reconciliation note (post-archive)**: the original "Typed exception for SQLite corruption" requirement below described a storage-boundary wrapping contract — every `IGraphStore` method translating `SqliteException` codes 11/26 into `GraphStoreCorruptedException`. The shipped implementation does NOT wrap at the storage boundary; raw `SqliteException` propagates and the dispatch layer's `CorruptionGuard.IsCorruptionError` recognises both forms. Wrapping 46 `IGraphStore` methods would have been a high-churn rewrite; the dispatch-layer recognition delivers the same user-facing behaviour ("corrupt DB → first call fails → subsequent calls return degraded short-circuit") with much smaller diff. The live baseline at `openspec/specs/storage/spec.md` ("Typed exception type for SQLite corruption") describes the actual contract. The archived requirement text below is preserved for change-history fidelity but should be read as superseded by the live baseline.

### Requirement: Typed exception for SQLite corruption

`SqliteGraphStore` SHALL wrap every `IGraphStore` public method body so that any `SqliteException` whose `SqliteErrorCode` is `11` (`SQLITE_CORRUPT`) or `26` (`SQLITE_NOTADB`) is rethrown as `GraphStoreCorruptedException`. The exception SHALL carry:
- `ScopeId` (string) — the scope id of the store that surfaced the error
- `InnerSqliteException` (`SqliteException`) — the original SQLite exception, preserved unmodified

The wrapping SHALL be uniform across all method bodies; no `IGraphStore` entry point may surface a raw `SqliteException` with `SqliteErrorCode in {11, 26}` to its caller.

Other SQLite error codes (transient locks, busy timeouts, etc.) SHALL continue to propagate as `SqliteException` unchanged.

#### Scenario: SQLITE_CORRUPT translates to GraphStoreCorruptedException
- **GIVEN** a `SqliteGraphStore` whose underlying file has been physically corrupted (random bytes overwritten at a SQLite page boundary)
- **WHEN** any read method (e.g. `FindSymbolsAsync`) is called
- **THEN** the method throws `GraphStoreCorruptedException` whose `ScopeId` matches the store's scope id and whose `InnerSqliteException.SqliteErrorCode == 11`

#### Scenario: SQLITE_NOTADB translates to GraphStoreCorruptedException
- **GIVEN** a `SqliteGraphStore` opened against a file whose first 16 bytes are not the SQLite magic header
- **WHEN** the schema check runs in `EnsureSchemaAsync`
- **THEN** the method throws `GraphStoreCorruptedException` whose `InnerSqliteException.SqliteErrorCode == 26`

#### Scenario: Other SQLite errors propagate unchanged
- **GIVEN** a `SqliteGraphStore` whose underlying connection raises `SqliteException { SqliteErrorCode = 5 (SQLITE_BUSY) }` on a write
- **WHEN** the call surfaces the exception
- **THEN** the original `SqliteException` propagates to the caller; no `GraphStoreCorruptedException` is thrown

### Requirement: Reactive integrity check on corruption suspicion

`ScopedExecution` SHALL catch `GraphStoreCorruptedException` from any tool body before propagating it. On catch, it SHALL run `IGraphStore.IntegrityCheckAsync` (which executes `PRAGMA integrity_check` AND the FTS5 integrity-check) on the affected scope's store and dispatch on the result:

- **Integrity check returned `"ok"`** (false alarm — the corruption error was transient): emit a heal event with `kind = "corruption-suspected-but-clean"`, `ok = true`, `details = "integrity_check passed; treating as transient"`. Rethrow the original `GraphStoreCorruptedException` so the agent's call still fails. Do NOT mark the scope `degraded`.

- **Integrity check returned a non-`"ok"` string** (corruption confirmed): emit a heal event with `kind = "corruption-detected"`, `ok = true`, `details = $"integrity_check failed: {result}"`. Mark the scope `degraded` with `status_message = $"corruption detected: {result}; call repair_scope mode=rebuild"`. Rethrow the original exception. If the autonomous-rebuild env var is enabled (per `Autonomous corrupt-DB rebuild gated by env var` in the `mcp-tools` capability), additionally fire the rebuild on a background task before rethrow.

- **Integrity check itself threw** (the DB is so broken the check can't complete): log at warning level. Emit a heal event with `kind = "corruption-detected"`, `ok = false`, `details = $"integrity_check itself failed: {ex.Message}"`. Mark the scope `degraded` with the same details. Rethrow the original exception.

The dispatch SHALL execute synchronously inside the `ScopedExecution` catch — the verification adds wall-clock to the failed call's response time (typically single-digit seconds for the integrity check), but the agent already saw the call fail; the structured `degraded` state is what subsequent calls benefit from.

Subsequent tool calls against a scope that this dispatch marked `degraded` SHALL hit the existing `degraded` short-circuit in `ScopedExecution.WaitForReadyAsync` and return the structured diagnostic without contacting SQLite again.

#### Scenario: False alarm — clean integrity check after suspicion
- **GIVEN** a tool call throws `GraphStoreCorruptedException` for scope `backend`, but `IntegrityCheckAsync` against `backend`'s DB returns `"ok"`
- **WHEN** `ScopedExecution` catches the exception and runs verification
- **THEN** `heals.jsonl` contains one line with `kind = "corruption-suspected-but-clean"`, `scope = "backend"`, `ok = true`; the `backend` registry row is NOT modified (still `"ok"`); the original `GraphStoreCorruptedException` propagates to the agent; the next tool call against `backend` is dispatched normally (no degraded short-circuit)

#### Scenario: Confirmed corruption marks scope degraded
- **GIVEN** a tool call throws `GraphStoreCorruptedException` for scope `backend` and `IntegrityCheckAsync` returns the string `"*** in database main *** Page 42: invalid header"`
- **WHEN** `ScopedExecution` catches and verifies
- **THEN** `heals.jsonl` contains one line with `kind = "corruption-detected"`, `ok = true`, `details = "integrity_check failed: *** in database main *** Page 42: invalid header"`; the `backend` registry row is updated to `Status = "degraded"` with `StatusMessage = "corruption detected: *** in database main *** Page 42: invalid header; call repair_scope mode=rebuild"`; the original exception propagates; the next tool call returns the degraded short-circuit without touching SQLite

#### Scenario: Integrity check itself fails
- **GIVEN** a tool call throws `GraphStoreCorruptedException` and `IntegrityCheckAsync` itself throws (e.g. file unreadable)
- **WHEN** `ScopedExecution` catches and the verification call also throws
- **THEN** `heals.jsonl` contains one line with `kind = "corruption-detected"`, `ok = false`, `details` carrying the verification exception's message; the registry row is marked `degraded` with the same details; the original `GraphStoreCorruptedException` propagates to the agent
