## ADDED Requirements

### Requirement: Embeddings subcommand group
The CLI SHALL accept a `sourcegraph-mcp embeddings <verb>` top-level subcommand group that exposes inspection and management of the embedding model cache. At v1 the supported verbs are `status`, `pull`, `remove`, and `verify`. An unknown nested verb SHALL exit with code `2` and an error message naming the supported verbs.

#### Scenario: Default model status
- **WHEN** `sourcegraph-mcp embeddings status` is invoked with no `--model` override
- **THEN** the command prints the cache directory path, the active model id and dimension, one row per manifest file (`localName`, presence flag, size in bytes when present, computed SHA-256 when present, pinned SHA when the manifest specifies one and a `match` indicator), and the free-disk bytes on the cache volume; the exit code is `0`

#### Scenario: Explicit pull
- **WHEN** `sourcegraph-mcp embeddings pull` is invoked with no `--model` override and an empty cache
- **THEN** the command synchronously downloads the active model's manifest files into the cache directory, prints a final status snapshot identical to the `status` verb's output, and exits `0`

#### Scenario: Pull is idempotent
- **WHEN** `sourcegraph-mcp embeddings pull` is invoked against a populated cache
- **THEN** no HTTP request is issued, the existing files are left untouched, the status snapshot is printed, and the command exits `0`

#### Scenario: Remove the active model
- **WHEN** `sourcegraph-mcp embeddings remove` is invoked with no flags and the active model's cache directory is populated
- **THEN** the command deletes the active model's per-id directory under `models/`, prints `{ "modelId": "<active>", "removedDirs": [...], "freedBytes": N }` (or the equivalent prose), and exits `0`

#### Scenario: Remove all cached models
- **WHEN** `sourcegraph-mcp embeddings remove --all` is invoked
- **THEN** every per-id directory under `models/` is deleted (the `models/` parent itself is preserved), the printed report names every removed directory and the total bytes freed, and the command exits `0`

#### Scenario: Conflicting --model and --all rejected
- **WHEN** `sourcegraph-mcp embeddings remove --model jinaai/foo --all` is invoked
- **THEN** the command prints an `ArgumentException` message naming both flags, prints the `embeddings remove` usage line, and exits `2` without touching disk

#### Scenario: Verify, no pinned SHA in manifest
- **WHEN** `sourcegraph-mcp embeddings verify` is invoked against a populated cache and the active model's manifest has no pinned SHA-256 strings (today's state)
- **THEN** the command prints the computed SHA of every cached file alongside a `(no pinned SHA — informational only)` note and exits `0`

#### Scenario: Verify, pinned SHA matches
- **WHEN** `sourcegraph-mcp embeddings verify` is invoked against a populated cache and every cached file's computed SHA matches its manifest pinned SHA
- **THEN** every row carries `match: true` and the command exits `0`

#### Scenario: Verify, pinned SHA mismatch
- **WHEN** `sourcegraph-mcp embeddings verify` is invoked against a populated cache where at least one cached file's computed SHA does not match its manifest pinned SHA
- **THEN** the affected rows carry `match: false`, the prose names the failing files, and the command exits `2`

#### Scenario: Inspect a non-active cached model
- **WHEN** the user passes `sourcegraph-mcp embeddings status --model someorg/other-model` against a cache containing both the active model and `someorg/other-model`
- **THEN** the printed status reflects the `someorg/other-model` directory only; the active model's data is not included in the report
