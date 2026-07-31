using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

internal static class IndexCompleteness
{
    private const int MaximumReportedMissingFiles = 100;

    internal static async Task<IndexCompletenessReport> BuildAsync(
        ScopeHost host,
        bool queryTraversalComplete,
        bool requireGrpcProjection,
        bool requireNativeInteropProjection,
        CancellationToken ct)
    {
        var stored = await host.Store.GetSourceDocumentCoverageAsync(ct).ConfigureAwait(false);
        var eligible = CollectKnownEligibleFiles(host, stored.EligibleGraphFiles);
        var indexed = stored.IndexedSourceDocuments
            .Select(NormalizePath)
            .ToHashSet(PathComparer);
        var missing = eligible
            .Where(path => !indexed.Contains(path))
            .Concat(stored.MissingSourceDocuments.Select(NormalizePath))
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToList();

        var hasNonRoslynIndexer = host.LoadedIndexers.Any(name =>
            !string.Equals(name, "roslyn", StringComparison.Ordinal)
            && !string.Equals(name, "interop", StringComparison.Ordinal));
        var projectDiscoveryComplete = !hasNonRoslynIndexer || host.ProjectMapReady;
        var sourceComplete = host.FailedProjects.Count == 0
            && host.FailedFiles.Count == 0
            && projectDiscoveryComplete
            && missing.Count == 0
            && host.Status is not ("degraded" or "indexing");
        var languageComplete = host.FailedProjects.Count == 0
            && host.FailedFiles.Count == 0
            && projectDiscoveryComplete
            && host.ManagedInteropInputComplete
            && host.Status is not ("degraded" or "indexing");

        var grpcComplete = !requireGrpcProjection || host.GrpcLinkState is
        {
            Status: GrpcLinkRuntimeStatus.Complete,
            RetainedLastGood: false,
            FailureCount: 0,
        };
        var nativeComplete = !requireNativeInteropProjection || host.NativeInteropState is
        {
            Status: NativeInteropRuntimeStatus.Complete,
            RetainedLastGood: false,
            IsExportUniverseComplete: true,
            PendingStaleSymbols: 0,
            Failures.Count: 0,
        };
        var relationComplete = languageComplete && grpcComplete && nativeComplete;
        var reportedMissing = missing.Take(MaximumReportedMissingFiles).ToList();
        return new IndexCompletenessReport(
            sourceComplete,
            languageComplete,
            relationComplete,
            queryTraversalComplete,
            IndexedFiles: eligible.Count(path => indexed.Contains(path)),
            EligibleFiles: eligible.Count,
            MissingFiles: reportedMissing,
            MissingFileCount: missing.Count,
            MissingFilesTruncated: missing.Count > reportedMissing.Count,
            LoadedIndexers: host.LoadedIndexers,
            IndexGeneration: host.IndexGeneration,
            IndexedAt: host.LastIndexedAt == default ? null : host.LastIndexedAt.ToString("O"));
    }

    private static HashSet<string> CollectKnownEligibleFiles(
        ScopeHost host,
        IReadOnlyList<string> storedGraphFiles)
    {
        var candidates = new List<string>();
        candidates.AddRange(storedGraphFiles);
        if (host.Indexer.SanitizedSolution is { } solution)
        {
            candidates.AddRange(solution.Projects
                .SelectMany(project => project.Documents.Concat(project.AdditionalDocuments))
                .Select(document => document.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))!);
        }
        candidates.AddRange(host.ProjectByFilePath.Keys);
        candidates.AddRange(host.LanguageProjects.SelectMany(project => project.FilePaths));
        candidates.AddRange(host.RegisteredLanguageEligibleFiles);
        candidates.AddRange(host.FailedFiles.Select(failure => failure.Path));
        if (host.Scope.Interop is { } interop)
        {
            candidates.AddRange(interop.TranslationUnits.Select(unit =>
                Path.IsPathFullyQualified(unit.Path)
                    ? unit.Path
                    : Path.Join(host.Scope.Root, unit.Path)));
        }

        var policy = new ScopePathPolicy(
            Path.GetFullPath(host.Scope.Root),
            host.Scope.ProjectSet.Exclude);
        var eligible = new HashSet<string>(PathComparer);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string fullPath;
            try
            {
                fullPath = Path.IsPathFullyQualified(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(candidate, host.Scope.Root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!policy.IsExcluded(fullPath)) eligible.Add(NormalizePath(fullPath));
        }
        return eligible;
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
