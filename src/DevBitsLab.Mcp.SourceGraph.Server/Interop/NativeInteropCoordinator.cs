using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

public enum NativeInteropRuntimeStatus
{
    NotStarted,
    Indexing,
    Complete,
    Partial,
}

public sealed record NativeInteropRuntimeFailure(
    string Stage,
    string Code,
    string Message,
    int? TranslationUnitIndex = null,
    string? ConfiguredPath = null);

/// <summary>
/// Small query-safe view of the latest native interop attempt. A partial state never turns
/// retained facts into a current absence claim; callers can use <see cref="RetainedLastGood"/>
/// to disclose that the database still contains the last complete projection.
/// </summary>
public sealed record NativeInteropRuntimeState(
    NativeInteropRuntimeStatus Status,
    InteropTarget Target,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessfulAt,
    bool RetainedLastGood,
    bool IsExportUniverseComplete,
    int TranslationUnits,
    int IncludedFiles,
    int NativeSymbols,
    int ManagedMatches,
    int Findings,
    int BoundaryEdges,
    int PendingStaleSymbols,
    IReadOnlyList<NativeInteropRuntimeFailure> Failures)
{
    public static NativeInteropRuntimeState NotStarted(InteropTarget target) =>
        new(
            NativeInteropRuntimeStatus.NotStarted,
            target,
            LastAttemptAt: null,
            LastSuccessfulAt: null,
            RetainedLastGood: false,
            IsExportUniverseComplete: false,
            TranslationUnits: 0,
            IncludedFiles: 0,
            NativeSymbols: 0,
            ManagedMatches: 0,
            Findings: 0,
            BoundaryEdges: 0,
            PendingStaleSymbols: 0,
            Failures: []);
}

internal sealed record NativeInteropRunResult(
    NativeInteropRuntimeState State,
    NativeInteropSnapshot? Snapshot,
    NativeInteropSnapshotPublicationResult? NativePublication,
    InteropAnalysisPublicationResult? AnalysisPublication);

internal sealed class NativeWorkerInvocationException : Exception
{
    public NativeWorkerInvocationException(NativeWorkerFailure failure)
        : base(failure?.Message)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public NativeWorkerFailure Failure { get; }
}

/// <summary>
/// Serializes the production native pipeline for one scope:
/// trust gate → isolated extraction → content-bound candidate → atomic native publication →
/// per-managed-file analysis publication. Any incomplete stage retains the last-good analysis.
/// </summary>
internal sealed class NativeInteropCoordinator : IAsyncDisposable
{
    private const int MaximumRuntimeFailures = 256;
    private const int MaximumFailureCharacters = 512;

    private readonly string _scopeRoot;
    private readonly ScopeInteropConfig _configuration;
    private readonly ScopePathPolicy _pathPolicy;
    private readonly IExecutionTrustPolicy _trustPolicy;
    private readonly NativeWorkerClient? _workerClient;
    private readonly NativeInteropExtractor? _testExtractor;
    private readonly NativeInteropBinaryVerifier _binaryVerifier;
    private readonly IGraphStore _store;
    private readonly NativeInteropSnapshotPublisher _nativePublisher;
    private readonly InteropAnalysisPublisher _analysisPublisher;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _stateGate = new();
    private readonly HashSet<string> _configuredInputPaths;
    private readonly string[] _watchExtensions;
    private readonly HashSet<string> _pendingStaleKeys =
        new(StringComparer.Ordinal);

    private NativeInteropRuntimeState _state;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>
        _lastGoodDependencyFanout =
            new Dictionary<string, IReadOnlyList<string>>(
                PathComparer);

    public NativeInteropCoordinator(
        string scopeRoot,
        ScopeInteropConfig configuration,
        ScopePathPolicy pathPolicy,
        IGraphStore store,
        IExecutionTrustPolicy trustPolicy,
        NativeWorkerClient? workerClient = null)
        : this(
            scopeRoot,
            configuration,
            pathPolicy,
            store,
            trustPolicy,
            workerClient ?? new NativeWorkerClient(trustPolicy),
            testExtractor: null,
            BinaryExportVerifier.VerifyAsync)
    {
    }

    internal NativeInteropCoordinator(
        string scopeRoot,
        ScopeInteropConfig configuration,
        ScopePathPolicy pathPolicy,
        IGraphStore store,
        IExecutionTrustPolicy trustPolicy,
        NativeInteropExtractor extractor,
        NativeInteropBinaryVerifier? binaryVerifier = null)
        : this(
            scopeRoot,
            configuration,
            pathPolicy,
            store,
            trustPolicy,
            workerClient: null,
            extractor,
            binaryVerifier ?? BinaryExportVerifier.VerifyAsync)
    {
    }

    private NativeInteropCoordinator(
        string scopeRoot,
        ScopeInteropConfig configuration,
        ScopePathPolicy pathPolicy,
        IGraphStore store,
        IExecutionTrustPolicy trustPolicy,
        NativeWorkerClient? workerClient,
        NativeInteropExtractor? testExtractor,
        NativeInteropBinaryVerifier binaryVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Target);
        ArgumentNullException.ThrowIfNull(configuration.TranslationUnits);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(trustPolicy);
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        if (workerClient is null && testExtractor is null)
        {
            throw new ArgumentException(
                "A native worker or extraction adapter is required.");
        }

        _scopeRoot = scopeRoot;
        _configuration = configuration;
        _pathPolicy = pathPolicy;
        _trustPolicy = trustPolicy;
        _workerClient = workerClient;
        _testExtractor = testExtractor;
        _binaryVerifier = binaryVerifier;
        _store = store;
        _nativePublisher = new NativeInteropSnapshotPublisher(store);
        _analysisPublisher = new InteropAnalysisPublisher(store);
        _configuredInputPaths = ResolveConfiguredInputPaths(
            scopeRoot,
            configuration);
        _watchExtensions = ResolveWatchExtensions(configuration);
        _state = NativeInteropRuntimeState.NotStarted(configuration.Target);
    }

    public NativeInteropRuntimeState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>
        LastGoodDependencyFanout
    {
        get
        {
            lock (_stateGate)
            {
                return _lastGoodDependencyFanout;
            }
        }
    }

    public IReadOnlyList<string> WatchExtensions => _watchExtensions;

    /// <summary>
    /// Native/header inputs are conservatively scope-wide because a newly introduced include
    /// is not present in the last dependency graph yet. Exact configured and last-good paths
    /// are also recognized for uncommon extensions.
    /// </summary>
    public bool IsRelevantPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
        if (_configuredInputPaths.Contains(fullPath))
        {
            return true;
        }

        lock (_stateGate)
        {
            if (_lastGoodDependencyFanout.ContainsKey(fullPath))
            {
                return true;
            }
        }
        var extension = Path.GetExtension(fullPath);
        return extension.Length > 0
            && Array.BinarySearch(
                _watchExtensions,
                extension,
                StringComparer.OrdinalIgnoreCase) >= 0;
    }

    public async Task<NativeInteropRunResult> RunAsync(
        bool isManagedUniverseComplete = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var priorState = State;
        try
        {
            var attemptedAt = DateTimeOffset.UtcNow;
            SetState(State with
            {
                Status = NativeInteropRuntimeStatus.Indexing,
                LastAttemptAt = attemptedAt,
                Failures = [],
            });

            ExecutionTrustDecision trust;
            try
            {
                trust = _trustPolicy.EvaluateRepositoryCapability(
                    _scopeRoot,
                    ExecutionCapability.NativeParsing);
            }
            catch (Exception ex)
            {
                return Failed(
                    attemptedAt,
                    [
                        RuntimeFailure(
                            "trust",
                            "trust-evaluation-failed",
                            $"Native parsing trust evaluation failed ({ex.GetType().Name})."),
                    ]);
            }
            if (!trust.IsAllowed)
            {
                return Failed(
                    attemptedAt,
                    [
                        RuntimeFailure(
                            "trust",
                            trust.ReasonCode,
                            $"Native parsing was denied ({trust.ReasonCode})."),
                    ]);
            }

            var workerFailures = new List<NativeWorkerFailure>();
            var builder = new NativeInteropSnapshotBuilder(
                CreateExtractor(workerFailures),
                _binaryVerifier);
            NativeInteropSnapshot snapshot;
            try
            {
                snapshot = await builder.BuildAsync(
                        _scopeRoot,
                        _configuration,
                        _pathPolicy,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(
                    attemptedAt,
                    [
                        RuntimeFailure(
                            "snapshot",
                            "snapshot-build-failed",
                            $"Native snapshot construction failed ({ex.GetType().Name})."),
                    ]);
            }

            if (!snapshot.IsComplete)
            {
                return Failed(
                    attemptedAt,
                    SnapshotFailures(snapshot, workerFailures),
                    snapshot);
            }

            NativeInteropSnapshotPublicationResult nativePublication;
            try
            {
                nativePublication = await _nativePublisher.PublishAsync(
                        snapshot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(
                    attemptedAt,
                    [
                        RuntimeFailure(
                            "native-storage",
                            "native-publication-failed",
                            $"Native snapshot publication failed ({ex.GetType().Name})."),
                    ],
                    snapshot);
            }
            if (!nativePublication.IsComplete)
            {
                return Failed(
                    attemptedAt,
                    PublicationFailures(nativePublication),
                    snapshot,
                    nativePublication);
            }

            lock (_stateGate)
            {
                foreach (var key in nativePublication.StaleCanonicalKeys)
                {
                    _pendingStaleKeys.Add(key);
                }
            }

            if (!isManagedUniverseComplete)
            {
                return Failed(
                    attemptedAt,
                    [
                        RuntimeFailure(
                            "managed-snapshot",
                            "managed-snapshot-incomplete",
                            "The managed import universe is incomplete; the last successful analysis projection was retained."),
                    ],
                    snapshot,
                    nativePublication);
            }

            InteropAnalysisPublicationResult analysisPublication;
            try
            {
                analysisPublication = await _analysisPublisher.PublishAsync(
                        snapshot.Target,
                        snapshot.IsExportUniverseComplete,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(
                    attemptedAt,
                    [
                        RuntimeFailure(
                            "analysis-storage",
                            "analysis-publication-failed",
                            $"Interop analysis publication failed ({ex.GetType().Name})."),
                    ],
                    snapshot,
                    nativePublication);
            }
            if (!analysisPublication.IsComplete)
            {
                return Failed(
                    attemptedAt,
                    AnalysisFailures(analysisPublication),
                    snapshot,
                    nativePublication,
                    analysisPublication);
            }

            var cleanupFailures = new List<NativeInteropRuntimeFailure>();
            string[] pendingCleanup;
            lock (_stateGate)
            {
                pendingCleanup = _pendingStaleKeys
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();
            }
            if (pendingCleanup.Length > 0)
            {
                try
                {
                    var cleanup =
                        await _store.DeleteOrphanedNativeInteropSymbolsAsync(
                                pendingCleanup,
                                cancellationToken)
                            .ConfigureAwait(false);
                    lock (_stateGate)
                    {
                        foreach (var key in cleanup.DeletedCanonicalKeys)
                        {
                            _pendingStaleKeys.Remove(key);
                        }
                        foreach (var key in cleanup.MissingCanonicalKeys)
                        {
                            _pendingStaleKeys.Remove(key);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(RuntimeFailure(
                        "stale-cleanup",
                        "stale-cleanup-failed",
                        $"Stale native-symbol cleanup failed ({ex.GetType().Name})."));
                }
            }

            var completedAt = DateTimeOffset.UtcNow;
            NativeInteropRuntimeState completed;
            lock (_stateGate)
            {
                _lastGoodDependencyFanout =
                    CloneDependencyFanout(snapshot.DependencyFanout);
                completed = new NativeInteropRuntimeState(
                    NativeInteropRuntimeStatus.Complete,
                    snapshot.Target,
                    attemptedAt,
                    completedAt,
                    RetainedLastGood: false,
                    snapshot.IsExportUniverseComplete,
                    snapshot.Contributions.Count,
                    snapshot.IncludedFiles.Count,
                    nativePublication.SymbolsPublished,
                    analysisPublication.MatchesPublished,
                    analysisPublication.FindingsPublished,
                    analysisPublication.EdgesPublished,
                    _pendingStaleKeys.Count,
                    Failures: OrderFailures(cleanupFailures));
                _state = completed;
            }
            return new NativeInteropRunResult(
                completed,
                snapshot,
                nativePublication,
                analysisPublication);
        }
        catch (OperationCanceledException)
        {
            SetState(priorState);
            throw;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private NativeInteropExtractor CreateExtractor(
        ICollection<NativeWorkerFailure> workerFailures)
    {
        if (_testExtractor is not null)
        {
            return _testExtractor;
        }

        return async (request, cancellationToken) =>
        {
            var result = await _workerClient!.ExtractAsync(
                    _scopeRoot,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result.Extraction!;
            }

            var failure = result.Failure
                ?? new NativeWorkerFailure(
                    "worker-failed",
                    "The native worker failed without a structured reason.");
            workerFailures.Add(failure);
            throw new NativeWorkerInvocationException(failure);
        };
    }

    private NativeInteropRunResult Failed(
        DateTimeOffset attemptedAt,
        IReadOnlyList<NativeInteropRuntimeFailure> failures,
        NativeInteropSnapshot? snapshot = null,
        NativeInteropSnapshotPublicationResult? nativePublication = null,
        InteropAnalysisPublicationResult? analysisPublication = null)
    {
        NativeInteropRuntimeState failed;
        lock (_stateGate)
        {
            var previous = _state;
            var lastSuccess = previous.LastSuccessfulAt;
            failed = new NativeInteropRuntimeState(
                NativeInteropRuntimeStatus.Partial,
                _configuration.Target,
                attemptedAt,
                lastSuccess,
                RetainedLastGood: lastSuccess is not null,
                IsExportUniverseComplete: false,
                snapshot?.Contributions.Count
                    ?? _configuration.TranslationUnits.Count,
                snapshot?.IncludedFiles.Count ?? 0,
                nativePublication?.SymbolsPublished ?? 0,
                analysisPublication?.MatchesPublished ?? 0,
                analysisPublication?.FindingsPublished ?? 0,
                analysisPublication?.EdgesPublished ?? 0,
                _pendingStaleKeys.Count,
                OrderFailures(failures));
            _state = failed;
        }
        return new NativeInteropRunResult(
            failed,
            snapshot,
            nativePublication,
            analysisPublication);
    }

    private static IReadOnlyList<NativeInteropRuntimeFailure>
        SnapshotFailures(
            NativeInteropSnapshot snapshot,
            IReadOnlyList<NativeWorkerFailure> workerFailures)
    {
        var failures = new List<NativeInteropRuntimeFailure>();
        failures.AddRange(workerFailures.Select(failure =>
            RuntimeFailure(
                "worker",
                failure.Code,
                failure.Message)));
        failures.AddRange(snapshot.Failures.Select(failure =>
            RuntimeFailure(
                "snapshot",
                FailureCode(failure.Kind),
                failure.Message,
                failure.TranslationUnitIndex,
                failure.ConfiguredPath)));
        return OrderFailures(failures);
    }

    private static IReadOnlyList<NativeInteropRuntimeFailure>
        PublicationFailures(
            NativeInteropSnapshotPublicationResult publication)
    {
        var failures = publication.SnapshotFailures
            .Select(failure =>
                RuntimeFailure(
                    "native-publication",
                    FailureCode(failure.Kind),
                    failure.Message,
                    failure.TranslationUnitIndex,
                    failure.ConfiguredPath))
            .ToList();
        if (!string.IsNullOrWhiteSpace(publication.Failure))
        {
            failures.Add(RuntimeFailure(
                "native-publication",
                "native-publication-failed",
                publication.Failure));
        }
        return OrderFailures(failures);
    }

    private static IReadOnlyList<NativeInteropRuntimeFailure>
        AnalysisFailures(
            InteropAnalysisPublicationResult publication) =>
        OrderFailures(publication.Failures.Select(failure =>
            RuntimeFailure(
                failure.Stage,
                "analysis-publication-failed",
                failure.FilePath is null
                    ? failure.Message
                    : $"{failure.FilePath}: {failure.Message}")));

    private static NativeInteropRuntimeFailure RuntimeFailure(
        string stage,
        string code,
        string message,
        int? translationUnitIndex = null,
        string? configuredPath = null) =>
        new(
            Bound(stage),
            Bound(code),
            Bound(message),
            translationUnitIndex,
            configuredPath is null ? null : Bound(configuredPath));

    private static string FailureCode(
        NativeInteropSnapshotFailureKind kind)
    {
        var name = kind.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static IReadOnlyList<NativeInteropRuntimeFailure> OrderFailures(
        IEnumerable<NativeInteropRuntimeFailure> failures) =>
        failures
            .Distinct()
            .OrderBy(failure => failure.TranslationUnitIndex ?? int.MaxValue)
            .ThenBy(failure => failure.Stage, StringComparer.Ordinal)
            .ThenBy(failure => failure.Code, StringComparer.Ordinal)
            .ThenBy(failure => failure.ConfiguredPath, StringComparer.Ordinal)
            .ThenBy(failure => failure.Message, StringComparer.Ordinal)
            .Take(MaximumRuntimeFailures)
            .ToArray();

    private static string Bound(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim();
        return normalized.Length <= MaximumFailureCharacters
            ? normalized
            : normalized[..MaximumFailureCharacters];
    }

    private void SetState(NativeInteropRuntimeState state)
    {
        lock (_stateGate)
        {
            _state = state;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>
        CloneDependencyFanout(
            IReadOnlyDictionary<string, IReadOnlyList<string>> source)
    {
        var clone = new Dictionary<string, IReadOnlyList<string>>(
            PathComparer);
        foreach (var pair in source
                     .OrderBy(pair => pair.Key, PathComparer)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            clone.Add(
                pair.Key,
                pair.Value
                    .OrderBy(path => path, PathComparer)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray());
        }
        return clone;
    }

    private static HashSet<string> ResolveConfiguredInputPaths(
        string scopeRoot,
        ScopeInteropConfig configuration)
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (var unit in configuration.TranslationUnits)
        {
            AddConfiguredPath(paths, scopeRoot, unit.Path);
            if (unit.BinaryPath is not null)
            {
                AddConfiguredPath(paths, scopeRoot, unit.BinaryPath);
            }
        }
        return paths;
    }

    private static void AddConfiguredPath(
        ISet<string> destination,
        string scopeRoot,
        string configuredPath)
    {
        try
        {
            destination.Add(Path.GetFullPath(
                Path.IsPathFullyQualified(configuredPath)
                    ? configuredPath
                    : Path.Join(scopeRoot, configuredPath)));
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            // The strict snapshot builder reports malformed configuration.
        }
    }

    private static string[] ResolveWatchExtensions(
        ScopeInteropConfig configuration)
    {
        var extensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".c",
            ".cc",
            ".cpp",
            ".cxx",
            ".h",
            ".hh",
            ".hpp",
            ".hxx",
            ".inc",
            ".inl",
            ".def",
            ".dll",
            ".so",
            ".dylib",
            ".a",
            ".lib",
        };
        foreach (var unit in configuration.TranslationUnits)
        {
            AddExtension(extensions, unit.Path);
            if (unit.BinaryPath is not null)
            {
                AddExtension(extensions, unit.BinaryPath);
            }
        }
        return extensions
            .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .ThenBy(extension => extension, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddExtension(
        ISet<string> destination,
        string path)
    {
        var extension = Path.GetExtension(path);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            destination.Add(extension);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public async ValueTask DisposeAsync()
    {
        await _runLock.WaitAsync().ConfigureAwait(false);
        _runLock.Release();
        _runLock.Dispose();
    }
}
