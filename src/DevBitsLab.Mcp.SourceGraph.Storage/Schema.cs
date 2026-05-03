namespace DevBitsLab.Mcp.SourceGraph.Storage;

internal static class Schema
{
    /// <summary>
    /// Bumped when the on-disk schema layout changes. <see cref="SqliteGraphStore.EnsureSchemaAsync"/>
    /// drops all data tables when the on-disk version is below this, since the index can always be
    /// rebuilt from source.
    /// </summary>
    public const int Version = 9;

    /// <summary>
    /// V5 enriches the symbol row with metadata that Roslyn already exposes per symbol but which
    /// previously required a Read-the-source round-trip to surface to an agent: a comma-joined
    /// <c>modifiers</c> token string (canonically ordered: <c>static, async, virtual, abstract,
    /// sealed, override, extern, readonly, partial</c>), the <c>DeclaredAccessibility</c> as an
    /// integer enum, and the parsed <c>&lt;summary&gt;</c> text from the XML doc comment (with
    /// <c>&lt;inheritdoc/&gt;</c> resolved up the override chain). The <c>xml_summary</c> column
    /// also feeds the FTS5 virtual table so <c>search_symbols</c> matches against documented
    /// behaviour, not just identifiers.
    ///
    /// V3 removes the foreign-key constraints from <c>refs</c> and <c>edges</c> that we previously
    /// relied on for cascade-on-delete behaviour. In real-world solutions (multi-target,
    /// linked files, shared projects, source generators) the same source path can be processed
    /// multiple times in a single index pass; the <c>ON DELETE CASCADE</c> would race with our
    /// in-memory <c>_symbolIdByKey</c> map and produce FK violations on insert. Cleanup is now
    /// done explicitly in <see cref="SqliteGraphStore.ClearFileAsync"/>.
    ///
    /// V6 adds the <c>attributes</c> table + <c>attributes_fts</c> trigram index on top of V5,
    /// so the indexer can record every <c>[Attribute]</c> attached to a symbol and answer
    /// <c>find_by_attribute</c> queries (with optional argument-substring filtering).
    ///
    /// V7 wires in <c>sqlite-vec</c>: an optional <c>symbol_embeddings</c> <c>vec0</c> virtual
    /// table holds 768-dim float vectors keyed by <c>symbol_id</c>, and a sibling
    /// <c>embedding_meta(symbol_id PK, content_hash BLOB, model_version TEXT)</c> tracks the
    /// SHA-256 of the synthesised text and the active model identity so swapping models or
    /// editing a symbol's body re-embeds correctly. The vec0 table is created only when the
    /// extension is loadable; <c>embedding_meta</c> is unconditional so we can persist
    /// (or re-stage) the metadata even on hosts where the extension is missing.
    ///
    /// V8 adds <c>files.is_generated</c> (1 for documents obtained from
    /// <c>Project.GetSourceGeneratedDocumentsAsync()</c>) and the <c>diagnostics</c> table
    /// keyed by file/symbol so <c>find_diagnostics</c> can answer "show me every CS0618"
    /// or "what's wrong in this file?" without re-running a build.
    ///
    /// V9 introduces test/history awareness: a <c>test_framework</c> column on
    /// <c>symbols</c> (values <c>xunit | nunit | mstest | NULL</c>) populated by the indexer
    /// based on attribute discrimination, and a new <c>symbol_history</c> table that caches
    /// the most recent commit / author / authored-time for each indexed symbol from
    /// <c>git blame --line-porcelain</c>. The cache is keyed against the source file's
    /// <c>content_sha256</c> so we don't re-blame on every reindex. A new
    /// <see cref="Core.EdgeKind.Tests"/> edge kind links each test method to the first
    /// non-trivial production call it exercises.
    /// </summary>
    public const string V1 = """
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
            kind INTEGER NOT NULL,
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
            kind INTEGER NOT NULL,
            PRIMARY KEY (src, dst, kind)
        );
        CREATE INDEX IF NOT EXISTS idx_edges_dst ON edges(dst, kind);

        CREATE TABLE IF NOT EXISTS attributes (
            id INTEGER PRIMARY KEY,
            symbol_id INTEGER NOT NULL,
            name TEXT NOT NULL,
            full_name TEXT NOT NULL,
            args_json TEXT,
            attribute_symbol_id INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_attributes_symbol ON attributes(symbol_id);
        CREATE INDEX IF NOT EXISTS idx_attributes_name ON attributes(name);
        CREATE INDEX IF NOT EXISTS idx_attributes_attribute_symbol_id ON attributes(attribute_symbol_id);

        CREATE TABLE IF NOT EXISTS embedding_meta (
            symbol_id INTEGER PRIMARY KEY,
            content_hash BLOB NOT NULL,
            model_version TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_embedding_meta_model ON embedding_meta(model_version);

        -- v8: Roslyn diagnostics. severity matches Microsoft.CodeAnalysis.DiagnosticSeverity
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

        -- v9: per-symbol git blame cache. last_authored_at is unix-millis. blamed_content_sha
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
                   s.kind      AS kind,
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
    public const string V2 = """
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

        -- FTS5 trigram index over the synthesised args_text column for an attribute.
        -- Triggers on `attributes` push every insert/delete into the virtual table.
        -- Note: `args_text` is not a real column on `attributes`; it's computed on the fly
        -- from `args_json` by stripping JSON punctuation. This keeps the schema simple
        -- (no second column to maintain) while still letting `find_by_attribute(argValue=...)`
        -- run a trigram match over the values that appear in attribute arguments.
        CREATE VIRTUAL TABLE IF NOT EXISTS attributes_fts USING fts5(
            args_text,
            content='',
            tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS attributes_ai AFTER INSERT ON attributes BEGIN
            INSERT INTO attributes_fts(rowid, args_text)
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

        CREATE TRIGGER IF NOT EXISTS attributes_ad AFTER DELETE ON attributes BEGIN
            INSERT INTO attributes_fts(attributes_fts, rowid, args_text)
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
    public static string V7Embeddings(int dim) => $$"""
        CREATE VIRTUAL TABLE IF NOT EXISTS symbol_embeddings USING vec0(
            symbol_id INTEGER PRIMARY KEY,
            embedding FLOAT[{{dim}}]
        );
        """;

    /// <summary>
    /// Drops every data table and trigger so a fresh schema can be applied.
    /// <c>EnsureSchemaAsync</c> calls this only when the on-disk version is below
    /// <see cref="Version"/>. The index always rebuilds from source on next pass.
    /// </summary>
    public const string DropAll = """
        DROP VIEW    IF EXISTS vw_recent_changes;
        DROP TABLE   IF EXISTS symbol_history;
        DROP TABLE   IF EXISTS diagnostics;
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
        DROP TABLE   IF EXISTS edges;
        DROP TABLE   IF EXISTS refs;
        DROP TABLE   IF EXISTS symbols;
        DROP TABLE   IF EXISTS files;
        """;
}
