using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// MCP tools for inspecting the scope registry. Pairs with the optional <c>scope</c> parameter
/// every other query tool gained.
/// </summary>
[McpServerToolType]
public static class ScopeTools
{
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListScopesResult))]
    [ToolTrigger("\"what scopes are configured?\" — call before passing the `scope` parameter to other tools, or after a 'no default_scope' error")]
    [Description("List every registered scope: id, name, root, persistent index generation, last-indexed timestamp, current watcher lag, status (ok | partial | degraded | indexing), isolation flag, and cold-index failures. Pair with the optional `scope` parameter on every other tool.")]
    public static Task<CallToolResult> ListScopesAsync(ScopeRouter router) =>
        // Body is sync but tracking goes through the async overload that knows how to brand-mark
        // the first user-visible TextContentBlock and serialise StructuredContent. Wrap in
        // Task.FromResult so the existing TrackAsync(CallToolResult) overload picks it up; no need
        // to introduce a sync-CallToolResult sibling for this one tool. The SDK's tool-name
        // derivation strips the `Async` suffix, so the wire-level tool name remains `list_scopes`
        // — matches every other converted tool's naming convention while keeping the contract
        // visible to C# callers.
        ToolMetrics.TrackAsync("list_scopes", null, () => Task.FromResult(BuildListScopesResultFromRouter(router)));

    private static CallToolResult BuildListScopesResultFromRouter(ScopeRouter router)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hosts = router.All();
        if (hosts.Count == 0)
        {
            // Empty-registry diagnostic still ships through the structured shape so consumers that
            // depend on `outputSchema` can probe `.scopes.length === 0` instead of parsing prose.
            return BuildListScopesResult(
                prose: "No scopes registered. Run `sourcegraph-mcp init-scopes` to scaffold .sourcegraph.json, or pass --solution to register a single-scope default.",
                hosts: Array.Empty<ScopeHost>(),
                defaultScope: router.DefaultScope,
                elapsedMs: sw.ElapsedMilliseconds);
        }

        var sb = new StringBuilder();
        var scopeNoun = hosts.Count == 1 ? "scope" : "scopes";
        sb.AppendLine($"{hosts.Count} {scopeNoun} registered:");
        sb.AppendLine();
        sb.AppendLine("| Id | Name | Status | Generation | Watcher lag | Isolated | Projects | Last indexed | Root |");
        sb.AppendLine("|----|------|--------|-----------:|------------:|---------:|---------:|--------------|------|");
        foreach (var host in hosts)
        {
            var scope = host.Scope;
            var projectCount = ProjectCount(scope.ProjectSet);
            var lastIndexed = host.LastIndexedAt == DateTimeOffset.MinValue
                ? "_never_"
                : host.LastIndexedAt.ToString("yyyy-MM-dd HH:mm:ss UTC");
            var statusCell = (host.Status == "degraded" || host.Status == "partial") && !string.IsNullOrEmpty(host.StatusMessage)
                ? $"{host.Status} ({host.StatusMessage})"
                : host.Status;
            // Status messages, names, and roots can contain user data (exception messages, file
            // paths from `.sourcegraph.json`). A literal `|` or newline in any cell would break
            // the GFM table renderer; escape both via the shared MarkdownTable helper.
            sb.AppendLine($"| `{scope.Id}` | {MarkdownTable.EscapeCell(scope.Name)} | {MarkdownTable.EscapeCell(statusCell)} | {host.IndexGeneration} | {host.WatcherLagMs} ms | {(scope.Isolated ? "yes" : "no")} | {projectCount} | {lastIndexed} | `{MarkdownTable.EscapeCell(scope.Root)}` |");
        }

        // Per-scope failure detail: only emit when at least one scope has non-empty failure
        // lists. Healthy installs see a clean table with no trailing failure section. The
        // markdown rendering surfaces the same data carried in StructuredContent so operators
        // reading the prose see the attribution without inspecting the typed shape. Note: the
        // failure lists reflect the most recent COLD index — watcher-driven incremental
        // re-indexes don't currently refresh them. Restart the server to force a refresh.
        if (hosts.Any(h => h.FailedProjects.Count > 0 || h.FailedFiles.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine("**Failed projects / files (last cold index):**");
            foreach (var host in hosts)
            {
                if (host.FailedProjects.Count == 0 && host.FailedFiles.Count == 0) continue;
                // Names + paths go inside inline-code spans; reason strings sit as plain
                // prose. Both populations come from arbitrary user-controlled text (csproj
                // names, file paths, exception messages), so a literal backtick or newline
                // would otherwise break the bullet structure.
                sb.AppendLine($"- `{MarkdownTable.EscapeInlineCode(host.Scope.Id)}`:");
                foreach (var pf in host.FailedProjects)
                {
                    sb.AppendLine($"  - project `{MarkdownTable.EscapeInlineCode(pf.Name)}` — {MarkdownTable.CollapseLines(pf.Reason)}");
                }
                foreach (var ff in host.FailedFiles)
                {
                    sb.AppendLine($"  - file `{MarkdownTable.EscapeInlineCode(ff.Path)}` — {MarkdownTable.CollapseLines(ff.Reason)}");
                }
            }
        }

        if (router.DefaultScope is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"_default_scope: `{router.DefaultScope}`_");
        }

        return BuildListScopesResult(
            prose: sb.ToString(),
            hosts: hosts,
            defaultScope: router.DefaultScope,
            elapsedMs: sw.ElapsedMilliseconds);
    }

    private static CallToolResult BuildListScopesResult(
        string prose,
        IReadOnlyList<ScopeHost> hosts,
        string? defaultScope,
        long elapsedMs)
    {
        // Per spec: no resource link per row — scopes don't have a graph:// URI scheme yet.
        // scopeId = null because list_scopes IS the scope listing (no single resolved scope).
        var content = new List<ContentBlock>(capacity: 2)
        {
            new TextContentBlock { Text = prose },
            AudienceMetadata.Build(
                scopeId: null,
                latencyMs: elapsedMs,
                ("scopes", hosts.Count.ToString())),
        };

        var rows = hosts
            .Select(host => new ListScopesRow(
                Id: host.Scope.Id,
                Name: host.Scope.Name,
                Root: host.Scope.Root,
                Status: host.Status,
                StatusMessage: string.IsNullOrEmpty(host.StatusMessage) ? null : host.StatusMessage,
                Isolated: host.Scope.Isolated,
                Generation: host.IndexGeneration,
                LastIndexedAt: host.LastIndexedAt == DateTimeOffset.MinValue
                    ? null
                    : host.LastIndexedAt.ToString("o"),
                WatcherLagMs: host.WatcherLagMs,
                ProjectCount: ProjectCount(host.Scope.ProjectSet),
                FailedProjects: host.FailedProjects
                    .Select(pf => new ListScopesProjectFailure(pf.Name, pf.Reason))
                    .ToList(),
                FailedFiles: host.FailedFiles
                    .Select(ff => new ListScopesFileFailure(ff.Path, ff.Reason))
                    .ToList()))
            .ToList();
        var dto = new ListScopesResult(DefaultScope: defaultScope, Scopes: rows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListScopesResult),
        };
    }

    private static int ProjectCount(Core.ScopeProjectSet projectSet) => projectSet switch
    {
        Core.ScopeProjectSet.Solutions s => s.Items.Count,
        Core.ScopeProjectSet.Projects p => p.Items.Count,
        Core.ScopeProjectSet.Paths g => g.Globs.Count,
        _ => 0,
    };

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(VerifyScopeResult))]
    [ToolTrigger("\"is the index stale or corrupt?\" — read-only health snapshot returning row counts, integrity_check result, and a 20-file drift sample per scope. Call before deciding whether to invoke repair tools.")]
    [Description("Read-only health snapshot for one or more scopes: schema version, status + status_message, per-table row counts, PRAGMA integrity_check result, and a 20-file drift sample (count of files whose on-disk SHA-256 differs from the DB). Doesn't mutate state. Use when a scope looks stale, returns empty results, or you want to verify integrity before calling repair_scope.")]
    public static Task<CallToolResult> VerifyScopeAsync(
        ScopeRouter router,
        [Description("Scope id, comma-separated list, or `*` for all non-isolated scopes (default).")]
        string? scope = "*",
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("verify_scope", new { scope }, async () =>
        {
            var sw = Stopwatch.StartNew();
            var resolution = router.Resolve(scope);
            if (resolution.IsError)
            {
                return DiagnosticResult.StructuredError(
                    toolName: "verify_scope",
                    error: "no_scopes",
                    message: resolution.ErrorMessage!,
                    hint: "register a scope (or name an isolated one explicitly) before calling verify_scope",
                    scope: scope ?? "*",
                    elapsedMs: sw.ElapsedMilliseconds);
            }
            var hosts = resolution.Hosts;
            if (hosts.Count == 0)
            {
                return DiagnosticResult.StructuredError(
                    toolName: "verify_scope",
                    error: "no_scopes",
                    message: "No scopes matched.",
                    hint: "pass an explicit scope id, or register a non-isolated scope",
                    scope: scope ?? "*",
                    elapsedMs: sw.ElapsedMilliseconds);
            }

            var rows = new List<VerifyScopeRow>(hosts.Count);
            for (var i = 0; i < hosts.Count; i++)
            {
                var host = hosts[i];
                var fraction = (double)i / hosts.Count;
                progress?.Report(Format.Progress(fraction, "reading row counts"));
                var row = await BuildVerifyRowAsync(host, progress, fraction, hosts.Count, ct).ConfigureAwait(false);
                rows.Add(row);
            }

            return BuildVerifyScopeResult(rows);
        });

    private static async Task<VerifyScopeRow> BuildVerifyRowAsync(
        ScopeHost host,
        IProgress<ProgressNotificationValue>? progress,
        double baseFraction,
        int hostCount,
        CancellationToken ct)
    {
        // Degraded scopes can't serve row-count / integrity-check queries reliably; surface the
        // registry's status_message and skip the heavy fields. The agent uses repair_scope to
        // recover.
        if (host.Status == "degraded")
        {
            return new VerifyScopeRow(
                Scope: host.Scope.Id,
                Status: host.Status,
                StatusMessage: host.StatusMessage,
                SchemaVersion: Schema.Version,
                ViewsSchemaVersion: Views.SchemaVersion,
                LastIndexedAt: host.LastIndexedAt == DateTimeOffset.MinValue ? null : host.LastIndexedAt.ToString("o"),
                RowCounts: null,
                IntegrityCheck: null,
                DriftSample: null,
                SourceCoverageComplete: null,
                LanguageProjectionComplete: null,
                RelationProjectionComplete: null,
                QueryTraversalComplete: null,
                IndexedFiles: null,
                EligibleFiles: null,
                MissingFiles: null,
                MissingFileCount: null,
                MissingFilesTruncated: null,
                LoadedIndexers: null,
                IndexGeneration: null,
                IndexedAt: null);
        }

        // Run integrity check first. If it fails, the DB's btree pages are unreliable —
        // running row-counts and drift queries against them risks SqliteException(SQLITE_CORRUPT)
        // mid-tool. Surface the integrity result + skip the heavy queries; the agent has the
        // signal it needs to escalate to repair_scope.
        progress?.Report(Format.Progress(baseFraction + 0.0 / hostCount, "running integrity_check"));
        var integrity = await host.Store.IntegrityCheckAsync(ct).ConfigureAwait(false);

        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            return new VerifyScopeRow(
                Scope: host.Scope.Id,
                Status: host.Status,
                StatusMessage: host.StatusMessage,
                SchemaVersion: Schema.Version,
                ViewsSchemaVersion: Views.SchemaVersion,
                LastIndexedAt: host.LastIndexedAt == DateTimeOffset.MinValue ? null : host.LastIndexedAt.ToString("o"),
                RowCounts: null,
                IntegrityCheck: integrity,
                DriftSample: null,
                SourceCoverageComplete: null,
                LanguageProjectionComplete: null,
                RelationProjectionComplete: null,
                QueryTraversalComplete: null,
                IndexedFiles: null,
                EligibleFiles: null,
                MissingFiles: null,
                MissingFileCount: null,
                MissingFilesTruncated: null,
                LoadedIndexers: null,
                IndexGeneration: null,
                IndexedAt: null);
        }

        progress?.Report(Format.Progress(baseFraction + 0.4 / hostCount, "reading row counts"));
        var counts = await host.Store.RowCountsAsync(ct).ConfigureAwait(false);

        progress?.Report(Format.Progress(baseFraction + 0.8 / hostCount, "sampling drift"));
        var drift = await SampleDriftAsync(host, counts.Files, ct).ConfigureAwait(false);
        var completeness = await IndexCompleteness.BuildAsync(
            host,
            queryTraversalComplete: true,
            requireGrpcProjection: true,
            requireNativeInteropProjection: host.Scope.Interop is not null,
            ct).ConfigureAwait(false);

        return new VerifyScopeRow(
            Scope: host.Scope.Id,
            Status: host.Status,
            StatusMessage: host.StatusMessage,
            SchemaVersion: Schema.Version,
            ViewsSchemaVersion: Views.SchemaVersion,
            LastIndexedAt: host.LastIndexedAt == DateTimeOffset.MinValue ? null : host.LastIndexedAt.ToString("o"),
            RowCounts: new VerifyScopeCounts(counts.Symbols, counts.Refs, counts.Edges, counts.Files, counts.Annotations, counts.Diagnostics),
            IntegrityCheck: integrity,
            DriftSample: drift,
            SourceCoverageComplete: completeness.SourceCoverageComplete,
            LanguageProjectionComplete: completeness.LanguageProjectionComplete,
            RelationProjectionComplete: completeness.RelationProjectionComplete,
            QueryTraversalComplete: completeness.QueryTraversalComplete,
            IndexedFiles: completeness.IndexedFiles,
            EligibleFiles: completeness.EligibleFiles,
            MissingFiles: completeness.MissingFiles,
            MissingFileCount: completeness.MissingFileCount,
            MissingFilesTruncated: completeness.MissingFilesTruncated,
            LoadedIndexers: completeness.LoadedIndexers,
            IndexGeneration: completeness.IndexGeneration,
            IndexedAt: completeness.IndexedAt);
    }

    private static async Task<VerifyScopeDriftSample> SampleDriftAsync(ScopeHost host, long totalFiles, CancellationToken ct)
    {
        const int sampleSize = 20;
        var sampled = (int)Math.Min(sampleSize, totalFiles);
        if (sampled == 0)
        {
            return new VerifyScopeDriftSample(0, (int)totalFiles, 0, Array.Empty<string>());
        }

        var files = await host.Store.SampleFileShasAsync(sampled, ct).ConfigureAwait(false);
        var pathPolicy = new ScopePathPolicy(
            Path.GetFullPath(host.Scope.Root),
            host.Scope.ProjectSet.Exclude);

        var changedPaths = new List<string>(capacity: 5);
        var changedCount = 0;
        var comparedCount = 0;
        foreach (var fileRow in files)
        {
            var path = fileRow.Path;
            var dbSha = fileRow.ContentSha256;
            ct.ThrowIfCancellationRequested();
            if (pathPolicy.IsExcluded(path)) continue;
            comparedCount++;
            byte[] onDiskSha;
            try
            {
                if (!File.Exists(path))
                {
                    // File deleted on disk after indexing; that's drift.
                    changedCount++;
                    if (changedPaths.Count < 5) changedPaths.Add(path);
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                onDiskSha = SHA256.HashData(bytes);
            }
            catch (IOException)
            {
                // Read failure is not drift per se, but we can't compare; skip silently.
                continue;
            }
            if (!ByteArrayEquals(onDiskSha, dbSha))
            {
                changedCount++;
                if (changedPaths.Count < 5) changedPaths.Add(path);
            }
        }

        return new VerifyScopeDriftSample(comparedCount, (int)totalFiles, changedCount, changedPaths);
    }

    private static bool ByteArrayEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static CallToolResult BuildVerifyScopeResult(IReadOnlyList<VerifyScopeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Verified {rows.Count} scope(s):");
        sb.AppendLine();
        sb.AppendLine("| Scope | Status | Schema | Symbols | Files | Integrity | Drift |");
        sb.AppendLine("|-------|--------|-------:|--------:|------:|-----------|-------|");
        foreach (var row in rows)
        {
            var integrityCell = row.IntegrityCheck switch
            {
                null => "_(degraded)_",
                "ok" => "ok",
                var failure => $"failed: {Truncate(failure, 40)}",
            };
            var driftCell = row.DriftSample is null
                ? "_(degraded)_"
                : row.DriftSample.Changed == 0
                    ? $"0/{row.DriftSample.Sampled}"
                    : $"**{row.DriftSample.Changed}**/{row.DriftSample.Sampled}";
            var symbolsCell = row.RowCounts?.Symbols.ToString() ?? "_(degraded)_";
            var filesCell = row.RowCounts?.Files.ToString() ?? "_(degraded)_";
            var statusCell = row.Status == "degraded" && !string.IsNullOrEmpty(row.StatusMessage)
                ? $"degraded ({Truncate(row.StatusMessage, 60)})"
                : row.Status;
            sb.AppendLine($"| `{row.Scope}` | {statusCell} | {row.SchemaVersion}/{row.ViewsSchemaVersion} | {symbolsCell} | {filesCell} | {integrityCell} | {driftCell} |");
        }

        var dto = new VerifyScopeResult(rows);
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = sb.ToString() } },
            StructuredContent = JsonSerializer.SerializeToElement(dto, ToolOutputJsonContext.Default.VerifyScopeResult),
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(RepairScopeResult))]
    [ToolTrigger("\"this scope looks broken — try to fix it\". `mode = \"minimal\"` (cheap: prune + retry) or `mode = \"rebuild\"` (nuclear: archive + cold-index from sources). Always operates on a single named scope; reject `*` and lists.")]
    [Description("Recover one named scope. `minimal`: integrity check (refuses if non-`ok`) → prune orphan embeddings → retry-wrap workspace reload + index. `rebuild`: cold-index and validate a shadow DB → atomically promote it while retaining the old DB under orphans/. A failed shadow build leaves the active DB unchanged. Returns structured before/after status, elapsed_ms, and a free-form message. Use `verify_scope` first to decide which mode is appropriate.")]
    public static Task<CallToolResult> RepairScopeAsync(
        ScopeRouter router,
        LiveIndexService liveIndex,
        [Description("The single scope id to repair. `*` and comma-separated lists are rejected — repair acts at scope grain and the destructive intent must be explicit.")]
        string scope,
        [Description("`minimal` (cheap, no DB drop) or `rebuild` (archive + cold-index from sources). Default: `minimal`.")]
        string? mode = "minimal",
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("repair_scope", new { scope, mode }, async () =>
        {
            // Reject "*" / comma-list / empty.
            if (string.IsNullOrWhiteSpace(scope) ||
                scope == "*" ||
                scope.Contains(',', StringComparison.Ordinal))
            {
                return DiagnosticResult.Error(
                    $"`scope` must name a single scope id. Got `{scope}`. `*` and comma-separated lists are rejected; repair_scope is destructive at scope grain.");
            }
            var resolvedMode = (mode ?? "minimal").Trim();
            if (resolvedMode is not ("minimal" or "rebuild"))
            {
                return DiagnosticResult.Error($"`mode` must be `minimal` or `rebuild`. Got `{resolvedMode}`.");
            }
            if (!router.TryGet(scope, out var host))
            {
                return DiagnosticResult.Error($"Unknown scope `{scope}`.");
            }

            var beforeStatus = host.Status;
            var sw = Stopwatch.StartNew();
            string afterStatus;
            string message;

            if (resolvedMode == "minimal")
            {
                // MinimalRepairScopeAsync emits progress at each phase boundary internally
                // (integrity_check 0.0, prune 0.5, reopen 0.8) so checkpoints fire BEFORE the
                // slow work, not after it returns.
                var result = await liveIndex.MinimalRepairScopeAsync(scope, ct, progress).ConfigureAwait(false);
                sw.Stop();
                afterStatus = router.TryGet(scope, out var post) ? post.Status : beforeStatus;
                message = result.Message;
                HealLog.Append(kind: "repair-scope-invoked", scope: scope, ok: !result.Refused && afterStatus == "ok",
                    ms: sw.Elapsed.TotalMilliseconds, details: $"mode=minimal; {message}");
            }
            else
            {
                progress?.Report(Format.Progress(0.0, "building and validating shadow index"));
                var post = await liveIndex.RebuildScopeAsync(scope, archiveDiscriminator: "rebuild", ct).ConfigureAwait(false);
                progress?.Report(Format.Progress(0.95, "finalising"));
                sw.Stop();
                if (post is null)
                {
                    afterStatus = router.TryGet(scope, out var retained)
                        ? retained.Status
                        : "degraded";
                    message = "shadow rebuild was rejected or could not be promoted; existing index was retained";
                }
                else
                {
                    afterStatus = post.Status;
                    var counts = post.Status == "ok"
                        ? await post.Store.RowCountsAsync(ct).ConfigureAwait(false)
                        : null;
                    message = post.Status == "ok"
                        ? $"rebuilt; new symbol_count={counts!.Symbols}"
                        : $"rebuild ended in degraded state: {post.StatusMessage ?? "(no message)"}";
                }
                HealLog.Append(kind: "repair-scope-invoked", scope: scope, ok: afterStatus == "ok",
                    ms: sw.Elapsed.TotalMilliseconds, details: $"mode=rebuild; {message}");
            }

            var dto = new RepairScopeResult(
                Scope: scope,
                Mode: resolvedMode,
                BeforeStatus: beforeStatus,
                AfterStatus: afterStatus,
                ElapsedMs: sw.ElapsedMilliseconds,
                Message: message);

            var prose = new StringBuilder()
                .AppendLine($"`repair_scope` {resolvedMode} on scope `{scope}`")
                .AppendLine()
                .AppendLine($"- before: `{beforeStatus}`")
                .AppendLine($"- after: `{afterStatus}`")
                .AppendLine($"- elapsed: {sw.ElapsedMilliseconds} ms")
                .AppendLine($"- {message}")
                .ToString();

            return new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = prose } },
                StructuredContent = JsonSerializer.SerializeToElement(dto, ToolOutputJsonContext.Default.RepairScopeResult),
            };
        });

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ReconcileDriftResult))]
    [ToolTrigger("\"results look stale — the watcher missed events while the server was offline\". Walks the scope's source tree, compares each file's on-disk SHA-256 to the DB, and applies the symmetric difference (reindex changed, index added, remove vanished).")]
    [Description("Walk a scope's source tree and reconcile drift between disk and DB. `dry_run = true` returns the diff without applying. `max_files` caps the walk (default 1000, hard cap 50000). Single-scope only — `*` and comma-lists are rejected. Use after a long offline period or when find_references / search_symbols return suspicious empty results.")]
    public static Task<CallToolResult> ReconcileDriftAsync(
        ScopeRouter router,
        [Description("The single scope id to reconcile. `*` and comma-separated lists are rejected.")]
        string scope,
        [Description("Maximum files to walk. Default 1000, hard cap 50000. Beyond the cap, `partial = true` in the response.")]
        int max_files = 1000,
        [Description("When true, computes the diff without applying it. Default false.")]
        bool dry_run = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("reconcile_drift", new { scope, max_files, dry_run }, async () =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(scope) ||
                scope == "*" ||
                scope.Contains(',', StringComparison.Ordinal))
            {
                return DiagnosticResult.StructuredError(
                    toolName: "reconcile_drift",
                    error: "bad_scope",
                    message: $"`scope` must name a single scope id. Got `{scope}`. `*` and comma-separated lists are rejected.",
                    hint: "pass exactly one scope id as the `scope` argument",
                    scope: scope ?? "",
                    elapsedMs: sw.ElapsedMilliseconds);
            }
            if (max_files < 1)
            {
                return DiagnosticResult.StructuredError(
                    toolName: "reconcile_drift",
                    error: "bad_argument",
                    message: $"`max_files` must be >= 1. Got {max_files}.",
                    hint: "pass a positive integer (default 1000, hard cap 50000)",
                    scope: scope,
                    elapsedMs: sw.ElapsedMilliseconds,
                    extras: new Dictionary<string, object?> { ["max_files"] = max_files });
            }
            if (!router.TryGet(scope, out var host))
            {
                return DiagnosticResult.StructuredError(
                    toolName: "reconcile_drift",
                    error: "unknown_scope",
                    message: $"Unknown scope `{scope}`.",
                    hint: "call list_scopes to see registered scope ids",
                    scope: scope,
                    elapsedMs: sw.ElapsedMilliseconds);
            }

            var clampedMax = Math.Min(max_files, 50000);

            // ComputeAsync emits its own progress checkpoints (walking source tree → comparing
            // hashes) BEFORE each phase, so the chat panel sees motion during the slow work.
            var diff = await DriftReconciler.ComputeAsync(host, clampedMax, ct, progress).ConfigureAwait(false);

            if (!dry_run)
            {
                progress?.Report(Format.Progress(0.7, "applying changes"));
                await host.RecordSourceChangeAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                await DriftReconciler.ApplyAsync(host, diff, ct).ConfigureAwait(false);
                await host.CompleteIndexGenerationAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                HealLog.Append(kind: "reconcile-drift-invoked", scope: scope, ok: true,
                    ms: sw.Elapsed.TotalMilliseconds,
                    details: $"scanned={diff.Scanned}, reindexed={diff.Changed.Count}, added={diff.Added.Count}, removed={diff.Removed.Count}, unchanged={diff.Unchanged}");
            }

            sw.Stop();
            var dto = new ReconcileDriftResult(
                Scope: scope,
                ScannedCount: diff.Scanned,
                ReindexedCount: diff.Changed.Count,
                AddedCount: diff.Added.Count,
                RemovedCount: diff.Removed.Count,
                UnchangedCount: diff.Unchanged,
                Partial: diff.Partial,
                DryRun: dry_run,
                ElapsedMs: sw.ElapsedMilliseconds);

            var sb = new StringBuilder()
                .AppendLine($"`reconcile_drift` on scope `{scope}` ({(dry_run ? "dry run" : "applied")})")
                .AppendLine()
                .AppendLine($"- scanned: {diff.Scanned}{(diff.Partial ? $" (partial — capped at max_files={clampedMax})" : "")}")
                .AppendLine($"- reindexed (changed SHA): {diff.Changed.Count}")
                .AppendLine($"- added (on disk, not in DB): {diff.Added.Count}")
                .AppendLine($"- removed (in DB, not on disk): {diff.Removed.Count}")
                .AppendLine($"- unchanged: {diff.Unchanged}")
                .AppendLine($"- elapsed: {sw.ElapsedMilliseconds} ms");

            return new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = sb.ToString() } },
                StructuredContent = JsonSerializer.SerializeToElement(dto, ToolOutputJsonContext.Default.ReconcileDriftResult),
            };
        });
}
