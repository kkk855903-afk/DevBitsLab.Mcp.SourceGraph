namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Storage-layer schema definitions. The class itself is public so callers outside the storage
/// assembly can read <see cref="Version"/> (e.g. <c>verify_scope</c> reports it in its structured
/// snapshot), but the SQL DDL strings (<see cref="V1"/>, <see cref="V2"/>,
/// <see cref="V7Embeddings"/>, <see cref="DropAll"/>) are <c>internal</c> — they are
/// implementation details of <see cref="SqliteGraphStore.EnsureSchemaAsync"/> and not a public
/// contract. Exposing the DDL would freeze it into the public surface; downstream consumers
/// should use the <see cref="IGraphStore"/> API to write data, not the raw SQL.
/// </summary>
public static class Schema
{
    /// <summary>
    /// Bumped when the on-disk schema layout changes. <see cref="SqliteGraphStore.EnsureSchemaAsync"/>
    /// drops all data tables when the on-disk version is below this, since the index can always be
    /// rebuilt from source.
    /// </summary>
    public const int Version = 13;

    /// <summary>
    /// V13 separates logical edges from their one-to-many evidence rows. The
    /// <c>edge_evidence</c> table records the producing file, exact range, confidence, producer,
    /// and occurrence-specific payload, allowing repeated call/binding sites to survive
    /// deduplication and to be removed independently during incremental indexing.
    ///
    /// V12 is an intentional destructive privacy boundary with no layout change: opening any
    /// v11-or-older graph drops all indexed rows before rebuilding, so data captured before the
    /// medical privacy path policy (including PatientData, DICOM, and image-derived records)
    /// cannot survive an upgrade.
    ///
    /// V11 (open-language-contract): kind columns flip from <c>INTEGER</c> to <c>TEXT NOT NULL</c>
    /// holding kebab-case identifiers (<c>"calls"</c>, <c>"class"</c>, …); <c>edges</c> gains a
    /// <c>payload TEXT NULL</c> column carrying JSON metadata for binding paths / event names /
    /// prop names; <c>attributes</c> / <c>attributes_fts</c> are renamed to <c>annotations</c> /
    /// <c>annotations_fts</c> with a new <c>flavor TEXT NOT NULL</c> column discriminating
    /// <c>csharp-attribute</c> from future <c>ts-decorator</c> / <c>vue-directive</c> /
    /// <c>svelte-action</c>. New <c>idx_edges_kind_name</c>, <c>idx_symbols_kind_name</c>, and
    /// <c>idx_annotations_flavor</c> indexes preserve query plans against the wider TEXT columns.
    ///
    /// V10 was the storage-layout flip required by add-scoping (per-scope DBs under
    /// <c>.sourcegraph/scopes/&lt;id&gt;.db</c> + <c>_meta.db</c> registry). V9 introduced
    /// test/history awareness, V8 added <c>files.is_generated</c> and <c>diagnostics</c>,
    /// V7 wired in <c>sqlite-vec</c>, V6 added <c>attributes</c> + <c>attributes_fts</c>,
    /// V5 enriched symbols with modifiers/accessibility/xml_summary, V3 dropped FK constraints
    /// from refs/edges. V13 is the cumulative drop-and-rebuild target — there is no preserved
    /// data path; <see cref="SqliteGraphStore.EnsureSchemaAsync"/> calls <see cref="DropAll"/>
    /// when the on-disk version is anything below 13.
    /// </summary>
    internal const string V1 = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY,
            path TEXT UNIQUE NOT NULL,
            content_sha256 BLOB NOT NULL,
            last_indexed_at INTEGER NOT NULL,
            is_generated INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS symbols (
            id INTEGER PRIMARY KEY,
            canonical_key TEXT UNIQUE NOT NULL,
            name TEXT NOT NULL,
            fqn TEXT NOT NULL,
            kind_name TEXT NOT NULL,
            file_id INTEGER NOT NULL,
            start_line INTEGER NOT NULL,
            start_col INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_col INTEGER NOT NULL,
            signature TEXT,
            container_id INTEGER,
            modifiers TEXT,
            accessibility INTEGER NOT NULL DEFAULT 0,
            xml_summary TEXT,
            test_framework TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON symbols(fqn);
        CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
        CREATE INDEX IF NOT EXISTS idx_symbols_container ON symbols(container_id);
        CREATE INDEX IF NOT EXISTS idx_symbols_kind_name ON symbols(kind_name);

        CREATE TABLE IF NOT EXISTS refs (
            id INTEGER PRIMARY KEY,
            symbol_id INTEGER NOT NULL,
            file_id INTEGER NOT NULL,
            line INTEGER NOT NULL,
            col INTEGER NOT NULL,
            kind INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_refs_symbol ON refs(symbol_id);
        CREATE INDEX IF NOT EXISTS idx_refs_file ON refs(file_id);

        CREATE TABLE IF NOT EXISTS edges (
            src INTEGER NOT NULL,
            dst INTEGER NOT NULL,
            kind_name TEXT NOT NULL,
            payload TEXT,
            PRIMARY KEY (src, dst, kind_name)
        );
        CREATE INDEX IF NOT EXISTS idx_edges_dst ON edges(dst, kind_name);
        CREATE INDEX IF NOT EXISTS idx_edges_kind_name ON edges(kind_name);

        CREATE TABLE IF NOT EXISTS edge_evidence (
            id INTEGER PRIMARY KEY,
            src INTEGER NOT NULL,
            dst INTEGER NOT NULL,
            kind_name TEXT NOT NULL,
            producing_file_id INTEGER NOT NULL,
            file_path TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            start_col INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_col INTEGER NOT NULL,
            confidence INTEGER NOT NULL,
            producer TEXT NOT NULL,
            payload TEXT NOT NULL DEFAULT '',
            UNIQUE (
                src, dst, kind_name, producing_file_id, file_path,
                start_line, start_col, end_line, end_col,
                confidence, producer, payload
            )
        );
        CREATE INDEX IF NOT EXISTS idx_edge_evidence_edge
            ON edge_evidence(src, dst, kind_name);
        CREATE INDEX IF NOT EXISTS idx_edge_evidence_file
            ON edge_evidence(producing_file_id);

        CREATE TABLE IF NOT EXISTS annotations (
            id INTEGER PRIMARY KEY,
            symbol_id INTEGER NOT NULL,
            name TEXT NOT NULL,
            full_name TEXT NOT NULL,
            flavor TEXT NOT NULL,
            args_json TEXT,
            attribute_symbol_id INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_annotations_symbol ON annotations(symbol_id);
        CREATE INDEX IF NOT EXISTS idx_annotations_name ON annotations(name);
        CREATE INDEX IF NOT EXISTS idx_annotations_flavor ON annotations(flavor);
        CREATE INDEX IF NOT EXISTS idx_annotations_attribute_symbol_id ON annotations(attribute_symbol_id);

        CREATE TABLE IF NOT EXISTS embedding_meta (
            symbol_id INTEGER PRIMARY KEY,
            content_hash BLOB NOT NULL,
            model_version TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_embedding_meta_model ON embedding_meta(model_version);

        -- Roslyn diagnostics. severity matches Microsoft.CodeAnalysis.DiagnosticSeverity
        -- (Hidden=0, Info=1, Warning=2, Error=3) so callers can filter "WHERE severity >= 2"
        -- to get warnings and errors. symbol_id is NULL when the diagnostic's source span
        -- doesn't fall inside any indexed declaration (e.g. unused-using on a using directive).
        CREATE TABLE IF NOT EXISTS diagnostics (
            id INTEGER PRIMARY KEY,
            symbol_id INTEGER,
            file_id INTEGER NOT NULL,
            severity INTEGER NOT NULL,
            code TEXT NOT NULL,
            message TEXT NOT NULL,
            line INTEGER NOT NULL,
            col INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_diagnostics_severity ON diagnostics(severity);
        CREATE INDEX IF NOT EXISTS idx_diagnostics_code ON diagnostics(code);
        CREATE INDEX IF NOT EXISTS idx_diagnostics_file ON diagnostics(file_id);
        CREATE INDEX IF NOT EXISTS idx_diagnostics_symbol ON diagnostics(symbol_id);

        -- Per-symbol git blame cache. last_authored_at is unix-millis. blamed_content_sha
        -- is the source file's content_sha256 at blame time so we can skip re-blaming when the
        -- file is unchanged.
        CREATE TABLE IF NOT EXISTS symbol_history (
            symbol_id INTEGER PRIMARY KEY,
            last_commit_sha TEXT,
            last_author TEXT,
            last_authored_at INTEGER,
            line_count INTEGER,
            blamed_content_sha BLOB
        );
        CREATE INDEX IF NOT EXISTS idx_symbol_history_authored_at ON symbol_history(last_authored_at);

        -- Convenience view of "recently changed" symbols. Window is parameterised at query time
        -- via WHERE clauses; this view just keeps the joins ready so callers can stay declarative.
        CREATE VIEW IF NOT EXISTS vw_recent_changes AS
            SELECT s.id        AS symbol_id,
                   s.fqn       AS fqn,
                   s.name      AS name,
                   s.kind_name AS kind_name,
                   f.path      AS file_path,
                   s.start_line AS start_line,
                   h.last_commit_sha AS last_commit_sha,
                   h.last_author     AS last_author,
                   h.last_authored_at AS last_authored_at,
                   h.line_count       AS line_count
            FROM symbol_history h
            JOIN symbols s ON s.id = h.symbol_id
            JOIN files   f ON f.id = s.file_id;
        """;

    /// <summary>FTS5 trigram-tokenized index over symbols.name/fqn/signature/xml_summary, kept in sync via triggers.</summary>
    internal const string V2 = """
        CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
            name,
            fqn,
            signature,
            xml_summary,
            content='symbols',
            content_rowid='id',
            tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS symbols_ai AFTER INSERT ON symbols BEGIN
            INSERT INTO symbols_fts(rowid, name, fqn, signature, xml_summary)
            VALUES (new.id, new.name, new.fqn, new.signature, new.xml_summary);
        END;

        CREATE TRIGGER IF NOT EXISTS symbols_ad AFTER DELETE ON symbols BEGIN
            INSERT INTO symbols_fts(symbols_fts, rowid, name, fqn, signature, xml_summary)
            VALUES ('delete', old.id, old.name, old.fqn, old.signature, old.xml_summary);
        END;

        CREATE TRIGGER IF NOT EXISTS symbols_au AFTER UPDATE ON symbols BEGIN
            INSERT INTO symbols_fts(symbols_fts, rowid, name, fqn, signature, xml_summary)
            VALUES ('delete', old.id, old.name, old.fqn, old.signature, old.xml_summary);
            INSERT INTO symbols_fts(rowid, name, fqn, signature, xml_summary)
            VALUES (new.id, new.name, new.fqn, new.signature, new.xml_summary);
        END;

        -- FTS5 trigram index over the synthesised args_text column for an annotation.
        -- Triggers on `annotations` push every insert/delete into the virtual table.
        -- Note: `args_text` is not a real column on `annotations`; it's computed on the fly
        -- from `args_json` by stripping JSON punctuation. This keeps the schema simple
        -- (no second column to maintain) while still letting `find_by_annotation(argValue=...)`
        -- run a trigram match over the values that appear in annotation arguments.
        CREATE VIRTUAL TABLE IF NOT EXISTS annotations_fts USING fts5(
            args_text,
            content='',
            tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS annotations_ai AFTER INSERT ON annotations BEGIN
            INSERT INTO annotations_fts(rowid, args_text)
            VALUES (
                new.id,
                COALESCE(
                    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        new.args_json,
                        '{', ' '), '}', ' '), '[', ' '), ']', ' '),
                        '"', ' '), ':', ' '), ',', ' '), '\\', ' '),
                    ''
                )
            );
        END;

        CREATE TRIGGER IF NOT EXISTS annotations_ad AFTER DELETE ON annotations BEGIN
            INSERT INTO annotations_fts(annotations_fts, rowid, args_text)
            VALUES (
                'delete',
                old.id,
                COALESCE(
                    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        old.args_json,
                        '{', ' '), '}', ' '), '[', ' '), ']', ' '),
                        '"', ' '), ':', ' '), ',', ' '), '\\', ' '),
                    ''
                )
            );
        END;
        """;

    /// <summary>
    /// Vector index virtual table. Created only when the <c>sqlite-vec</c> extension loaded
    /// successfully (see <see cref="SqliteGraphStore.TryLoadVectorExtension"/>). Embedding dim
    /// is parameterised so a future <c>--model</c> override with a different dimension can
    /// rebuild this part of the schema without dropping every other table.
    /// </summary>
    internal static string V7Embeddings(int dim) => $$"""
        CREATE VIRTUAL TABLE IF NOT EXISTS symbol_embeddings USING vec0(
            symbol_id INTEGER PRIMARY KEY,
            embedding FLOAT[{{dim}}]
        );
        """;

    /// <summary>
    /// Drops every data table and trigger so a fresh schema can be applied.
    /// <c>EnsureSchemaAsync</c> calls this only when the on-disk version is below
    /// <see cref="Version"/>. The index always rebuilds from source on next pass.
    ///
    /// Includes drops for the legacy v10-and-earlier <c>attributes</c> / <c>attributes_fts</c>
    /// names so all supported prior DBs cleanly rebuild into the current <c>annotations</c>
    /// tables.
    /// </summary>
    internal const string DropAll = """
        DROP VIEW    IF EXISTS vw_recent_changes;
        DROP TABLE   IF EXISTS symbol_history;
        DROP TABLE   IF EXISTS diagnostics;
        DROP TRIGGER IF EXISTS annotations_ad;
        DROP TRIGGER IF EXISTS annotations_ai;
        DROP TABLE   IF EXISTS annotations_fts;
        DROP TABLE   IF EXISTS annotations;
        DROP TRIGGER IF EXISTS attributes_ad;
        DROP TRIGGER IF EXISTS attributes_ai;
        DROP TABLE   IF EXISTS attributes_fts;
        DROP TABLE   IF EXISTS attributes;
        DROP TRIGGER IF EXISTS symbols_au;
        DROP TRIGGER IF EXISTS symbols_ad;
        DROP TRIGGER IF EXISTS symbols_ai;
        DROP TABLE   IF EXISTS symbols_fts;
        DROP TABLE   IF EXISTS symbol_embeddings;
        DROP TABLE   IF EXISTS embedding_meta;
        DROP TABLE   IF EXISTS edge_evidence;
        DROP TABLE   IF EXISTS edges;
        DROP TABLE   IF EXISTS refs;
        DROP TABLE   IF EXISTS symbols;
        DROP TABLE   IF EXISTS files;
        """;
}
