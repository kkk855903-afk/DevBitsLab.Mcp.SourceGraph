namespace DevBitsLab.Mcp.SourceGraph.Storage;

internal static class Schema
{
    public const int Version = 2;

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
            name TEXT NOT NULL,
            fqn TEXT NOT NULL,
            kind INTEGER NOT NULL,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            start_line INTEGER NOT NULL,
            start_col INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_col INTEGER NOT NULL,
            signature TEXT,
            container_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON symbols(fqn);
        CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);

        CREATE TABLE IF NOT EXISTS refs (
            id INTEGER PRIMARY KEY,
            symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            line INTEGER NOT NULL,
            col INTEGER NOT NULL,
            kind INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_refs_symbol ON refs(symbol_id);
        CREATE INDEX IF NOT EXISTS idx_refs_file ON refs(file_id);

        CREATE TABLE IF NOT EXISTS edges (
            src INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
            dst INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            PRIMARY KEY (src, dst, kind)
        );
        CREATE INDEX IF NOT EXISTS idx_edges_dst ON edges(dst, kind);
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
        """;
}
