namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Stable agent-facing view layer over the per-scope SQLite tables. See
/// openspec/changes/add-graph-query/design.md (Decision 2) for the contract.
///
/// View names are stable; their column shape is the public API. Bump
/// <see cref="SchemaVersion"/> on any view-set change — addition, removal, column
/// rename, or column-type change — so clients that cache <c>describe_schema</c> by
/// version always re-introspect after a server upgrade.
///
/// The underlying tables (<c>symbols</c>, <c>edges</c>, <c>refs</c>, <c>files</c>,
/// <c>edge_evidence</c>, <c>annotations</c>, <c>diagnostics</c>,
/// <c>symbol_history</c>) remain implementation details and may evolve without bumping
/// <see cref="SchemaVersion"/> — only <see cref="Schema.Version"/> moves for those.
/// </summary>
public static class Views
{
    /// <summary>
    /// View-layer schema version. Independent from <see cref="Schema.Version"/> (the on-disk
    /// table schema). Bumps on any view-set change — addition, removal, column rename, or
    /// column-type change — so clients that cache <c>describe_schema</c> by version always
    /// re-introspect after a server upgrade.
    /// </summary>
    public const int SchemaVersion = 3;

    /// <summary>
    /// The CREATE TEMP VIEW scaffolding loaded from <c>Views.sql</c>. Contains
    /// <c>{{SCOPE_UNION_BLOCK_&lt;view&gt;}}</c> placeholder tokens that the connection helper
    /// substitutes with per-scope UNION ALL blocks.
    /// </summary>
    public static string Sql { get; }

    /// <summary>
    /// Per-view per-scope SELECT template, keyed by view name. The template contains a
    /// <c>{SCOPE_ID}</c> placeholder; the connection helper formats one block per attached
    /// scope and joins them with <c>UNION ALL</c>, then substitutes the joined text into the
    /// matching <c>{{SCOPE_UNION_BLOCK_&lt;view&gt;}}</c> token in <see cref="Sql"/>.
    ///
    /// Keys: <c>"v_symbols"</c>, <c>"v_files"</c>, <c>"v_edges"</c>,
    /// <c>"v_edge_evidence"</c>, <c>"v_references"</c>, <c>"v_annotations"</c>,
    /// <c>"v_diagnostics"</c>, <c>"v_history"</c>.
    /// (<c>v_scopes</c> is single-source from <c>meta.scopes</c> and has no per-scope template.)
    ///
    /// The ATTACH alias <c>"{SCOPE_ID}"</c> is double-quoted because scope ids can contain
    /// hyphens (<c>my-scope</c>) which SQLite would otherwise misparse.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PerScopeBlockTemplates { get; }

    /// <summary>
    /// Hand-curated descriptors for <c>describe_schema</c>'s response. The contract is
    /// "9 views, names match the live <c>tools/list</c> output"; the specific iteration order
    /// is documentation, not contract.
    /// </summary>
    public static IReadOnlyList<ViewDescriptor> All { get; }

    static Views()
    {
        Sql = LoadEmbedded("Views.sql");
        PerScopeBlockTemplates = BuildTemplates();
        All = BuildDescriptors();
    }

    private static string LoadEmbedded(string name)
    {
        var assembly = typeof(Views).Assembly;
        var resourceId = $"{typeof(Views).Namespace}.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceId)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceId}' not found. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> BuildTemplates()
    {
        // {SCOPE_ID} is the only placeholder. The double-quoted "{SCOPE_ID}" alias references
        // the per-scope ATTACHed DB by its id (which can contain hyphens).
        const string vSymbols = """
            SELECT '{SCOPE_ID}' AS scope, s.id AS id, s.name AS name, s.fqn AS fqn,
                   s.kind_name AS kind, s.accessibility AS accessibility,
                   (CASE WHEN s.accessibility = 6 THEN 1 ELSE 0 END) AS is_public,
                   (CASE WHEN s.kind_name IN ('class','interface','struct','record','enum','delegate') THEN 1 ELSE 0 END) AS is_type,
                   s.modifiers AS modifiers, s.xml_summary AS xml_summary,
                   s.container_id AS container_id, s.file_id AS file_id,
                   s.start_line AS start_line, s.start_col AS start_column
            FROM "{SCOPE_ID}".symbols s
            """;

        const string vFiles = """
            SELECT '{SCOPE_ID}' AS scope, f.id AS id, f.path AS path, f.content_sha256 AS sha,
                   f.last_indexed_at AS last_indexed_at, f.is_generated AS is_generated
            FROM "{SCOPE_ID}".files f
            """;

        const string vEdges = """
            SELECT '{SCOPE_ID}' AS scope, e.src AS src, e.dst AS dst,
                   e.kind_name AS kind, e.payload AS payload
            FROM "{SCOPE_ID}".edges e
            """;

        const string vEdgeEvidence = """
            SELECT '{SCOPE_ID}' AS scope, ev.id AS id, ev.src AS src, ev.dst AS dst,
                   ev.kind_name AS kind, ev.producing_file_id AS producing_file_id,
                   ev.file_path AS file_path, ev.start_line AS start_line,
                   ev.start_col AS start_column, ev.end_line AS end_line,
                   ev.end_col AS end_column,
                   (CASE ev.confidence WHEN 0 THEN 'inferred'
                                       WHEN 1 THEN 'semantic'
                                       WHEN 2 THEN 'exact'
                                       ELSE CAST(ev.confidence AS TEXT) END) AS confidence,
                   ev.confidence AS confidence_level, ev.producer AS producer,
                   NULLIF(ev.payload, '') AS payload
            FROM "{SCOPE_ID}".edge_evidence ev
            """;

        const string vReferences = """
            SELECT '{SCOPE_ID}' AS scope, r.symbol_id AS symbol_id, r.file_id AS file_id,
                   r.line AS line, r.col AS column_number,
                   (CASE r.kind WHEN 0 THEN 'def' WHEN 1 THEN 'ref' WHEN 2 THEN 'call'
                                WHEN 3 THEN 'impl' WHEN 4 THEN 'inherit'
                                WHEN 5 THEN 'read' WHEN 6 THEN 'write'
                                ELSE CAST(r.kind AS TEXT) END) AS kind
            FROM "{SCOPE_ID}".refs r
            """;

        const string vAnnotations = """
            SELECT '{SCOPE_ID}' AS scope, a.id AS id, a.symbol_id AS symbol_id,
                   a.name AS name, a.full_name AS full_name, a.flavor AS flavor,
                   a.args_json AS args_json, a.attribute_symbol_id AS attribute_symbol_id
            FROM "{SCOPE_ID}".annotations a
            """;

        const string vDiagnostics = """
            SELECT '{SCOPE_ID}' AS scope, d.id AS id, d.symbol_id AS symbol_id, d.file_id AS file_id,
                   d.severity AS severity,
                   (CASE d.severity WHEN 0 THEN 'hidden' WHEN 1 THEN 'info'
                                    WHEN 2 THEN 'warning' WHEN 3 THEN 'error'
                                    ELSE CAST(d.severity AS TEXT) END) AS severity_name,
                   d.code AS code, d.message AS message, d.line AS line, d.col AS column_number
            FROM "{SCOPE_ID}".diagnostics d
            """;

        const string vHistory = """
            SELECT '{SCOPE_ID}' AS scope, h.symbol_id AS symbol_id,
                   h.last_commit_sha AS last_commit_sha, h.last_author AS last_author,
                   h.last_authored_at AS last_authored_at, h.line_count AS line_count,
                   h.blamed_content_sha AS blamed_content_sha
            FROM "{SCOPE_ID}".symbol_history h
            """;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v_symbols"] = vSymbols,
            ["v_files"] = vFiles,
            ["v_edges"] = vEdges,
            ["v_edge_evidence"] = vEdgeEvidence,
            ["v_references"] = vReferences,
            ["v_annotations"] = vAnnotations,
            ["v_diagnostics"] = vDiagnostics,
            ["v_history"] = vHistory,
        };
    }

    private static List<ViewDescriptor> BuildDescriptors()
    {
        return new List<ViewDescriptor>
        {
            new(
                "v_symbols",
                "Every declared symbol across the resolved scopes. One row per (scope, id). "
                + "Cross-scope joins use the composite (scope, id) tuple; single-scope queries "
                + "see a constant scope column and can join on bare id.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this row lives in."),
                    new("id", "INTEGER", false, "Per-scope symbol id; combine with `scope` for cross-scope uniqueness."),
                    new("name", "TEXT", false, "Unqualified symbol name (e.g. `Calculator`)."),
                    new("fqn", "TEXT", false, "Fully-qualified name (e.g. `Sample.Domain.Calculator`)."),
                    new("kind", "TEXT", false, "Symbol kind: `class`, `interface`, `struct`, `record`, `enum`, `delegate`, `method`, `field`, `property`, `event`, `namespace`, `xaml-view`, ... See `describe_schema.symbol_kinds` for the live vocabulary."),
                    new("accessibility", "INTEGER", false, "Roslyn `Accessibility`: 0=NotApplicable, 1=Private, 2=ProtectedAndInternal, 3=Protected, 4=Internal, 5=ProtectedOrInternal, 6=Public."),
                    new("is_public", "INTEGER", false, "1 when accessibility = 6 (Public); 0 otherwise. Convenience for the common filter."),
                    new("is_type", "INTEGER", false, "1 when kind in {class, interface, struct, record, enum, delegate}; 0 otherwise."),
                    new("modifiers", "TEXT", true, "Space-separated modifier list (e.g. `static abstract sealed`); NULL when no modifiers apply."),
                    new("xml_summary", "TEXT", true, "Plain-text body of the XML doc-comment `<summary>` tag, if present."),
                    new("container_id", "INTEGER", true, "Per-scope id of the enclosing symbol (containing type/method); NULL for top-level symbols. Join back to `v_symbols.id` within the same scope."),
                    new("file_id", "INTEGER", false, "Per-scope file id; join to `v_files.id` within the same scope to resolve the source path."),
                    new("start_line", "INTEGER", false, "1-based start line of the declaration in the source file."),
                    new("start_column", "INTEGER", false, "1-based start column of the declaration. (Renamed from underlying `start_col`.)"),
                }),

            new(
                "v_files",
                "Every indexed source file across the resolved scopes. One row per (scope, id).",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this row lives in."),
                    new("id", "INTEGER", false, "Per-scope file id; join from `v_symbols.file_id` and `v_references.file_id` within the same scope."),
                    new("path", "TEXT", false, "Absolute or scope-relative path to the source file."),
                    new("sha", "BLOB", false, "SHA-256 of the file's content at last index time. (Renamed from underlying `content_sha256`.)"),
                    new("last_indexed_at", "INTEGER", false, "Unix-millis timestamp of the last successful index pass over this file."),
                    new("is_generated", "INTEGER", false, "1 when the file is generated (e.g. EditorBrowsable hidden, `*.g.cs`); 0 otherwise."),
                }),

            new(
                "v_edges",
                "Every directed edge in the graph (calls / uses-type / inherits / implements / instantiates / throws / tests / binds-path / handles-event / ...) across the resolved scopes. Both `src` and `dst` are per-scope symbol ids; cross-scope edges do not exist (each edge lives in exactly one scope's DB).",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this edge lives in."),
                    new("src", "INTEGER", false, "Source symbol id (the caller / user / inheriter / ...). Join to `v_symbols.id` in the same scope."),
                    new("dst", "INTEGER", false, "Destination symbol id (the callee / used type / base / ...). Join to `v_symbols.id` in the same scope."),
                    new("kind", "TEXT", false, "Edge kind: `calls`, `uses-type`, `inherits`, `implements`, `instantiates`, `throws`, `tests`, `binds-path`, `handles-event`, `uses-resource`, ... See `describe_schema.edge_kinds` for the live vocabulary."),
                    new("payload", "TEXT", true, "Optional JSON payload carrying edge metadata (binding paths, event names, prop names). NULL when the edge kind has no associated metadata."),
                }),

            new(
                "v_edge_evidence",
                "Every independently attributable source occurrence supporting a logical edge. "
                + "Join (scope, src, dst, kind) to v_edges and producing_file_id to v_files.id "
                + "within the same scope. Multiple rows may support the same logical edge.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this evidence row lives in."),
                    new("id", "INTEGER", false, "Per-scope evidence id; combine with `scope` for cross-scope uniqueness."),
                    new("src", "INTEGER", false, "Source symbol id of the supported edge."),
                    new("dst", "INTEGER", false, "Destination symbol id of the supported edge."),
                    new("kind", "TEXT", false, "Kind of the supported logical edge."),
                    new("producing_file_id", "INTEGER", false, "File id whose index pass emitted this proof; join to `v_files.id` in the same scope."),
                    new("file_path", "TEXT", false, "Source file containing the evidence range."),
                    new("start_line", "INTEGER", false, "1-based start line of the evidence range."),
                    new("start_column", "INTEGER", false, "1-based start column of the evidence range."),
                    new("end_line", "INTEGER", false, "1-based inclusive end line of the evidence range."),
                    new("end_column", "INTEGER", false, "1-based inclusive end column of the evidence range."),
                    new("confidence", "TEXT", false, "Confidence name: `inferred`, `semantic`, or `exact`."),
                    new("confidence_level", "INTEGER", false, "Ordered confidence level: 0=inferred, 1=semantic, 2=exact; useful for minimum-confidence filters."),
                    new("producer", "TEXT", false, "Analyzer that established this proof, such as `roslyn` or `xaml`."),
                    new("payload", "TEXT", true, "Optional occurrence-specific JSON metadata; NULL when absent."),
                }),

            new(
                "v_references",
                "Every textual reference site across the resolved scopes (the `refs` table). One row per (scope, file, line, column) that mentions a symbol.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this reference lives in."),
                    new("symbol_id", "INTEGER", false, "Per-scope id of the referenced symbol. Join to `v_symbols.id` within the same scope."),
                    new("file_id", "INTEGER", false, "Per-scope id of the file containing the reference site. Join to `v_files.id` within the same scope."),
                    new("line", "INTEGER", false, "1-based line of the reference site."),
                    new("column_number", "INTEGER", false, "1-based column of the reference site. (Renamed from underlying `col`; SQL reserves the bare identifier `column`.)"),
                    new("kind", "TEXT", false, "Reference kind, mapped from the underlying integer enum: `def` (Definition), `ref` (Reference), `call` (Call), `impl` (Implements), `inherit` (Inherits), `read`, `write`."),
                }),

            new(
                "v_scopes",
                "Every registered scope from the `_meta.db` registry. Single source (no per-scope union). Use this view to discover scope ids, status, and last-indexed times.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id (the primary key from the registry; usable as the ATTACH alias for the scope's per-scope DB)."),
                    new("name", "TEXT", false, "Human-friendly scope name (from `.sourcegraph.json`)."),
                    new("root", "TEXT", false, "Filesystem root the scope's projects live under."),
                    new("isolated", "INTEGER", false, "1 when the scope is isolated (excluded from `scope='*'` fan-out); 0 otherwise."),
                    new("status", "TEXT", false, "One of `ok`, `degraded`, `indexing`."),
                    new("last_indexed_at", "INTEGER", false, "Unix-millis timestamp of the last completed index pass against the scope."),
                }),

            new(
                "v_annotations",
                "Every indexed annotation across the resolved scopes — C# attributes, XAML attached properties, and any future plugin-defined flavor. One row per (scope, id). Join `symbol_id` → `v_symbols.id` within the same scope to reach the decorated symbol.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this row lives in."),
                    new("id", "INTEGER", false, "Per-scope annotation id; combine with `scope` for cross-scope uniqueness."),
                    new("symbol_id", "INTEGER", false, "Per-scope id of the decorated symbol. Join to `v_symbols.id` within the same scope."),
                    new("name", "TEXT", false, "Short annotation name (e.g. `Obsolete`, `HttpPost`, `Grid.Row`); the unqualified identifier as it appears at the use site."),
                    new("full_name", "TEXT", false, "Fully-qualified annotation name (e.g. `System.ObsoleteAttribute`, `Microsoft.AspNetCore.Mvc.HttpPostAttribute`)."),
                    new("flavor", "TEXT", false, "Annotation source / framework: `csharp-attribute`, `xaml-attached-property`. Future plugins can introduce new flavors (e.g. `ts-decorator`, `vue-directive`, `svelte-action`); `describe_schema.annotation_flavors` carries the live vocabulary."),
                    new("args_json", "TEXT", true, "Raw JSON array/object of constructor + named arguments captured from the annotation site; NULL when the annotation has no arguments. This column is raw TEXT — for substring search over argument values, prefer the curated `find_by_annotation` tool (FTS5-indexed via `annotations_fts`); use this view for compositional joins / aggregations."),
                    new("attribute_symbol_id", "INTEGER", true, "Per-scope id of the annotation's defining type when that type is itself indexed (user-defined attribute / decorator); NULL when the defining type lives outside the index. Join to `v_symbols.id` within the same scope."),
                }),

            new(
                "v_diagnostics",
                "Every Roslyn diagnostic across the resolved scopes (warnings / errors / info / hidden). One row per (scope, id). Join `symbol_id` → `v_symbols.id` within the same scope to reach the diagnosed declaration; `symbol_id` is NULL for diagnostics whose source span doesn't fall inside any indexed declaration (e.g. unused-using on a using directive at file scope) — use LEFT JOIN to preserve those rows.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this diagnostic lives in."),
                    new("id", "INTEGER", false, "Per-scope diagnostic id; combine with `scope` for cross-scope uniqueness."),
                    new("symbol_id", "INTEGER", true, "Per-scope id of the diagnosed symbol; NULL when the diagnostic's source span doesn't fall inside any indexed declaration (e.g. unused-using directive at file scope). Join to `v_symbols.id` within the same scope (use LEFT JOIN to keep null-symbol diagnostics)."),
                    new("file_id", "INTEGER", false, "Per-scope id of the file containing the diagnostic site. Join to `v_files.id` within the same scope."),
                    new("severity", "INTEGER", false, "Roslyn `DiagnosticSeverity`: 0=Hidden, 1=Info, 2=Warning, 3=Error. Use this column for ordering / range filters (e.g. `WHERE severity >= 2` for warnings + errors)."),
                    new("severity_name", "TEXT", false, "Convenience text mapping of `severity`: `hidden` (0), `info` (1), `warning` (2), `error` (3); computed via CASE so agents don't have to memorise the integer enum."),
                    new("code", "TEXT", false, "Diagnostic code (e.g. `CS0612`, `CS0618`, `IDE0001`)."),
                    new("message", "TEXT", false, "Human-readable diagnostic message."),
                    new("line", "INTEGER", false, "1-based line of the diagnostic site."),
                    new("column_number", "INTEGER", false, "1-based column of the diagnostic site. (Renamed from underlying `col`; SQL reserves the bare identifier `column`.)"),
                }),

            new(
                "v_history",
                "Per-symbol git-blame metadata across the resolved scopes (last commit sha, last author, last-authored timestamp, line count). One row per (scope, symbol_id). Empty when the server runs with `--no-history` or against an environment without git on PATH. Join `symbol_id` → `v_symbols.id` within the same scope to reach the symbol's name / kind / file.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this history row lives in."),
                    new("symbol_id", "INTEGER", false, "Per-scope id of the symbol whose history this row caches. Join to `v_symbols.id` within the same scope."),
                    new("last_commit_sha", "TEXT", true, "Git commit SHA of the most recent commit that touched any line in the symbol's source span; NULL for symbols whose blame run failed or returned no commits."),
                    new("last_author", "TEXT", true, "Author of `last_commit_sha`; NULL when `last_commit_sha` is NULL."),
                    new("last_authored_at", "INTEGER", true, "Unix-millis timestamp of the most recent authored change to the symbol's source span (matches `v_files.last_indexed_at` and `v_scopes.last_indexed_at` units). For ISO-8601 use `datetime(last_authored_at / 1000, 'unixepoch')`. NULL when `last_commit_sha` is NULL."),
                    new("line_count", "INTEGER", true, "Number of source lines covered by the symbol's declaration span at blame time."),
                    new("blamed_content_sha", "BLOB", true, "SHA-256 of the source file's content at blame time; used by the indexer to skip re-blaming when the file is unchanged. Compare against `v_files.sha` to detect stale blame caches."),
                }),
        };
    }
}

/// <summary>
/// Hand-curated descriptor for one view, surfaced via <c>describe_schema</c>. The
/// <see cref="Columns"/> list is authoritative — agents reading <c>describe_schema</c>'s
/// response treat it as the contract.
/// </summary>
public sealed record ViewDescriptor(string Name, string Description, IReadOnlyList<ViewColumn> Columns);

/// <summary>
/// One column in a <see cref="ViewDescriptor"/>. <see cref="SqliteType"/> is the SQLite
/// affinity name (<c>TEXT</c>, <c>INTEGER</c>, <c>BLOB</c>); <see cref="Nullable"/> reflects
/// whether the column can be NULL when read via the view.
/// </summary>
public sealed record ViewColumn(string Name, string SqliteType, bool Nullable, string Description);
