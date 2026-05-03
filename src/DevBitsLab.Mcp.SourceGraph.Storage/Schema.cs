namespace DevBitsLab.Mcp.SourceGraph.Storage;

internal static class Schema
{
    /// <summary>
    /// Bumped when the on-disk schema layout changes. <see cref="SqliteGraphStore.EnsureSchemaAsync"/>
    /// drops all data tables when the on-disk version is below this, since the index can always be
    /// rebuilt from source.
    /// </summary>
    public const int Version = 5;

    /// <summary>
    /// V3 removes the foreign-key constraints from <c>refs</c> and <c>edges</c> that we previously
    /// relied on for cascade-on-delete behaviour. In real-world solutions (multi-target,
    /// linked files, shared projects, source generators) the same source path can be processed
    /// multiple times in a single index pass; the <c>ON DELETE CASCADE</c> would race with our
    /// in-memory <c>_symbolIdByKey</c> map and produce FK violations on insert. Cleanup is now
    /// done explicitly in <see cref="SqliteGraphStore.ClearFileAsync"/>.
    ///
    /// V5 adds the <c>attributes</c> table for the <c>index-attributes</c> change.
    /// </summary>
    public const string V1 = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY,
            path TEXT UNIQUE NOT NULL,
            content_sha256 BLOB NOT NULL,
            last_indexed_at INTEGER NOT NULL
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
            container_id INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON symbols(fqn);
        CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);

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
        """;

    /// <summary>FTS5 trigram-tokenized index over symbols.name/fqn/signature, kept in sync via triggers.</summary>
    public const string V2 = """
        CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
            name,
            fqn,
            signature,
            content='symbols',
            content_rowid='id',
            tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS symbols_ai AFTER INSERT ON symbols BEGIN
            INSERT INTO symbols_fts(rowid, name, fqn, signature)
            VALUES (new.id, new.name, new.fqn, new.signature);
        END;

        CREATE TRIGGER IF NOT EXISTS symbols_ad AFTER DELETE ON symbols BEGIN
            INSERT INTO symbols_fts(symbols_fts, rowid, name, fqn, signature)
            VALUES ('delete', old.id, old.name, old.fqn, old.signature);
        END;

        CREATE TRIGGER IF NOT EXISTS symbols_au AFTER UPDATE ON symbols BEGIN
            INSERT INTO symbols_fts(symbols_fts, rowid, name, fqn, signature)
            VALUES ('delete', old.id, old.name, old.fqn, old.signature);
            INSERT INTO symbols_fts(rowid, name, fqn, signature)
            VALUES (new.id, new.name, new.fqn, new.signature);
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
    /// Drops every data table and trigger so a fresh schema can be applied.
    /// <c>EnsureSchemaAsync</c> calls this only when the on-disk version is below
    /// <see cref="Version"/>. The index always rebuilds from source on next pass.
    /// </summary>
    public const string DropAll = """
        DROP TRIGGER IF EXISTS attributes_ad;
        DROP TRIGGER IF EXISTS attributes_ai;
        DROP TABLE   IF EXISTS attributes_fts;
        DROP TABLE   IF EXISTS attributes;
        DROP TRIGGER IF EXISTS symbols_au;
        DROP TRIGGER IF EXISTS symbols_ad;
        DROP TRIGGER IF EXISTS symbols_ai;
        DROP TABLE   IF EXISTS symbols_fts;
        DROP TABLE   IF EXISTS edges;
        DROP TABLE   IF EXISTS refs;
        DROP TABLE   IF EXISTS symbols;
        DROP TABLE   IF EXISTS files;
        """;
}
