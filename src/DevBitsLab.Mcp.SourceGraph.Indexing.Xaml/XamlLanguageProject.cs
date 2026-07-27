using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Plugin-private <see cref="ILanguageProject"/> for one <c>.csproj</c> that contains XAML files.
/// Holds the absolute project file path as <see cref="Id"/>, every <c>.xaml</c> file the project
/// owns, and an immutable <see cref="ResourceCache"/> snapshot populated from <c>App.xaml</c>,
/// same-project <c>MergedDictionaries</c>, and theme <c>Generic.xaml</c>. Per-document indexer
/// calls reuse the snapshot for resource-resolution lookups
/// (<c>{StaticResource AccentBrush}</c> → declaration site); an explicit rebuild atomically
/// refreshes it after an incremental edit.
/// </summary>
public sealed class XamlLanguageProject : IDeclarationFirstLanguageProject
{
    private static readonly MethodInfo? _getSourceGeneratorDiagnosticsMethod =
        typeof(Project)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                method.Name == "GetSourceGeneratorDiagnosticsAsync"
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(CancellationToken)
                && method.ReturnType == typeof(ValueTask<ImmutableArray<Diagnostic>>));

    private XamlResourceSnapshot _resourceSnapshot;
    private readonly Func<XamlResourceSnapshot>? _resourceSnapshotBuilder;
    private readonly Func<IReadOnlyList<Project>>? _roslynProjectsProvider;
    private readonly Func<bool>? _semanticInputCompleteProvider;
    private readonly Func<bool>? _semanticPositiveResolutionSafeProvider;

    public XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        Dictionary<string, ResourceDefinition> resourceCache)
        : this(
            projectFilePath,
            xamlFilePaths,
            new ReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>(
                resourceCache.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<ResourceDefinition>)new[] { pair.Value },
                    StringComparer.Ordinal)),
            resourceSnapshotBuilder: null,
            roslynProjectProvider: null)
    {
    }

    internal XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> resourceCache,
        Func<XamlResourceSnapshot>? resourceSnapshotBuilder,
        Func<Project?>? roslynProjectProvider)
        : this(
            projectFilePath,
            xamlFilePaths,
            new XamlResourceSnapshot(
                resourceCache,
                resourceCache.Values
                    .SelectMany(candidates => candidates)
                    .Select(definition => definition.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                isComplete: true,
                Array.Empty<string>()),
            resourceSnapshotBuilder,
            roslynProjectProvider)
    {
    }

    internal XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        XamlResourceSnapshot resourceSnapshot,
        Func<XamlResourceSnapshot>? resourceSnapshotBuilder,
        Func<Project?>? roslynProjectProvider)
        : this(
            projectFilePath,
            xamlFilePaths,
            resourceSnapshot,
            resourceSnapshotBuilder,
            roslynProjectProvider is null
                ? null
                : () =>
                {
                    var project = roslynProjectProvider();
                    return project is null
                        ? Array.Empty<Project>()
                        : new[] { project };
                },
            semanticInputCompleteProvider: null,
            semanticPositiveResolutionSafeProvider: null)
    {
    }

    internal XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        XamlResourceSnapshot resourceSnapshot,
        Func<XamlResourceSnapshot>? resourceSnapshotBuilder,
        Func<IReadOnlyList<Project>>? roslynProjectsProvider,
        Func<bool>? semanticInputCompleteProvider,
        Func<bool>? semanticPositiveResolutionSafeProvider)
    {
        Id = projectFilePath;
        FilePaths = xamlFilePaths;
        _resourceSnapshot = resourceSnapshot;
        _resourceSnapshotBuilder = resourceSnapshotBuilder;
        _roslynProjectsProvider = roslynProjectsProvider;
        _semanticInputCompleteProvider = semanticInputCompleteProvider;
        _semanticPositiveResolutionSafeProvider =
            semanticPositiveResolutionSafeProvider;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> FilePaths { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> DeclarationFilePaths
    {
        get
        {
            var snapshot = Volatile.Read(ref _resourceSnapshot);
            var ownedPaths = new HashSet<string>(
                FilePaths,
                StringComparer.OrdinalIgnoreCase);
            return snapshot.ContributorPaths
                .Where(ownedPaths.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Resource cascade keyed by <c>x:Key</c>. Each value retains every visible declaration;
    /// callers use <see cref="ResolveResource"/> so duplicate declarations remain ambiguous.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> ResourceCache =>
        Volatile.Read(ref _resourceSnapshot).Definitions;

    /// <summary>The current immutable resource-cascade snapshot.</summary>
    public XamlResourceSnapshot ResourceSnapshot =>
        Volatile.Read(ref _resourceSnapshot);

    /// <summary>
    /// Resolves <paramref name="key"/> against the current project snapshot. Duplicate visible
    /// declarations are reported as ambiguous; discovery order never silently selects a winner.
    /// </summary>
    public ResourceResolution ResolveResource(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new ResourceResolution(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Unsupported,
                    "resource-key-is-empty"),
                Array.Empty<ResourceDefinition>());
        }

        var snapshot = Volatile.Read(ref _resourceSnapshot);
        var candidates = snapshot.Definitions.TryGetValue(key, out var visible)
            ? visible
            : Array.Empty<ResourceDefinition>();
        if (candidates.Count > 1)
        {
            return new ResourceResolution(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Ambiguous,
                    "multiple-visible-resource-declarations"),
                candidates);
        }
        var knownCandidateIsSafe = snapshot.IsComplete
            || snapshot.UnknownReasons.All(reason =>
                reason.StartsWith(
                    "project-xaml-item-missing:",
                    StringComparison.Ordinal)
                || reason.StartsWith(
                    "merged-dictionary-target-unavailable:",
                    StringComparison.Ordinal));
        if (candidates.Count == 1 && knownCandidateIsSafe)
        {
            return new ResourceResolution(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Resolved,
                    snapshot.IsComplete
                        ? "unique-visible-resource-declaration"
                        : "unique-known-resource-declaration"),
                candidates);
        }
        if (!snapshot.IsComplete)
        {
            return new ResourceResolution(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Incomplete,
                    "project-resource-cascade-incomplete"),
                candidates);
        }
        return new ResourceResolution(
            new XamlResolutionOutcome(
                XamlResolutionStatus.Missing,
                "resource-key-not-visible-in-complete-static-cascade"),
            candidates);
    }

    /// <summary>
    /// Builds a replacement resource snapshot without publishing it. Dispatchers use this
    /// prepare step for every affected project before publishing any snapshot, so a later
    /// project's read/parse failure cannot leave an earlier project split from its stored graph.
    /// </summary>
    public XamlResourceSnapshot PrepareResourceSnapshot() =>
        _resourceSnapshotBuilder?.Invoke()
        ?? Volatile.Read(ref _resourceSnapshot);

    /// <summary>
    /// Publishes a fully-built immutable snapshot. This operation cannot invoke project I/O.
    /// </summary>
    public void PublishResourceSnapshot(XamlResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref _resourceSnapshot, snapshot);
    }

    /// <summary>
    /// Atomically rebuilds the project resource snapshot from the same scope-filtered XAML file
    /// set used during discovery. Hosts rebuilding more than one project in a batch SHALL use
    /// <see cref="PrepareResourceSnapshot"/> for every project before publishing any result.
    /// </summary>
    public void RebuildResourceCache() =>
        PublishResourceSnapshot(PrepareResourceSnapshot());

    /// <summary>
    /// Returns the compilation from the host's current privacy-sanitized Roslyn solution.
    /// The provider is evaluated per call so live C# edits are not pinned to the project
    /// snapshot that happened to exist when XAML discovery first ran.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Compilation and generator failures are untrusted project state; XAML resolution must fail closed instead of aborting unrelated indexing.")]
    internal async Task<XamlCompilationState?> GetCompilationStateAsync(
        CancellationToken ct)
    {
        IReadOnlyList<Project>? projects;
        bool semanticInputComplete;
        bool semanticPositiveResolutionSafe;
        try
        {
            projects = _roslynProjectsProvider?.Invoke();
            semanticInputComplete =
                _semanticInputCompleteProvider?.Invoke() ?? true;
            semanticPositiveResolutionSafe =
                semanticInputComplete
                || (_semanticPositiveResolutionSafeProvider?.Invoke()
                    ?? semanticInputComplete);
        }
        catch
        {
            return null;
        }
        if (projects is null || projects.Count == 0) return null;

        var candidates = new List<XamlCompilationCandidate>(projects.Count);
        var semanticChecks = new Dictionary<ProjectId, XamlProjectSemanticCheck>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            XamlProjectSemanticCheck? rootCheck = null;
            var compilationComplete = true;
            var generatorOutputComplete = true;
            var semanticResolutionSafe = true;
            var pending = new Stack<Project>();
            var visited = new HashSet<ProjectId>();
            pending.Push(project);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = pending.Pop();
                if (!visited.Add(current.Id)) continue;

                if (!semanticChecks.TryGetValue(current.Id, out var check))
                {
                    check = await CheckProjectSemanticStateAsync(
                            current,
                            ct)
                        .ConfigureAwait(false);
                    semanticChecks[current.Id] = check;
                }
                if (current.Id == project.Id)
                {
                    rootCheck = check;
                }
                compilationComplete =
                    compilationComplete && check.IsComplete;
                generatorOutputComplete =
                    generatorOutputComplete
                    && check.GeneratorOutputComplete;
                semanticResolutionSafe =
                    semanticResolutionSafe
                    && (current.Id == project.Id
                        ? check.GeneratorOutputComplete
                        : check.IsComplete);

                foreach (var reference in current.ProjectReferences)
                {
                    var referencedProject = current.Solution.GetProject(
                        reference.ProjectId);
                    if (referencedProject is null)
                    {
                        compilationComplete = false;
                        generatorOutputComplete = false;
                        continue;
                    }
                    pending.Push(referencedProject);
                }
            }
            if (rootCheck?.Compilation is not { } compilation) return null;

            candidates.Add(new XamlCompilationCandidate(
                compilation,
                HasWpfEvidence(compilation),
                compilationComplete,
                generatorOutputComplete,
                semanticResolutionSafe));
        }

        XamlCompilationCandidate selected;
        var wpfCandidates = candidates
            .Where(candidate => candidate.HasWpfEvidence)
            .ToArray();
        if (wpfCandidates.Length == 1
            && (candidates.Count == 1
                || candidates.All(candidate => candidate.IsComplete)))
        {
            selected = wpfCandidates[0];
        }
        else if (wpfCandidates.Length == 0 && candidates.Count == 1)
        {
            // Retain semantic support for a single non-WPF XAML target (for example Avalonia).
            selected = candidates[0];
        }
        else
        {
            // Multiple WPF-capable TFMs, or multiple iterations without a uniquely proven WPF
            // target, cannot be collapsed into one authoritative semantic universe.
            return null;
        }

        return new XamlCompilationState(
            selected.Compilation,
            semanticInputComplete && selected.IsComplete,
            semanticPositiveResolutionSafe && selected.CanResolve,
            DirectBindingMembersOnly:
                !semanticInputComplete
                && semanticPositiveResolutionSafe);
    }

    private static bool HasWpfEvidence(Compilation compilation) =>
        compilation.GetTypeByMetadataName("System.Windows.Application") is not null;

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Referenced project compilation and generator failures make the XAML semantic universe incomplete; they must not abort unrelated indexing.")]
    private static async Task<XamlProjectSemanticCheck> CheckProjectSemanticStateAsync(
        Project project,
        CancellationToken ct)
    {
        var analyzerLoadFailed = false;
        EventHandler<AnalyzerLoadFailureEventArgs> loadFailureHandler =
            (_, _) => analyzerLoadFailed = true;
        var fileReferences = project.AnalyzerReferences
            .OfType<AnalyzerFileReference>()
            .ToArray();
        foreach (var reference in fileReferences)
        {
            reference.AnalyzerLoadFailed += loadFailureHandler;
        }

        var generatorDiscoveryComplete = true;
        var hasGenerators = false;
        try
        {
            // Enumerate before asking the workspace for a compilation so AnalyzerFileReference
            // load failures are observed on their first attempt instead of disappearing behind
            // Roslyn's analyzer-reference cache.
            foreach (var reference in project.AnalyzerReferences)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    hasGenerators =
                        reference.GetGenerators(project.Language).Length > 0
                        || hasGenerators;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    generatorDiscoveryComplete = false;
                }
            }

            Compilation? compilation;
            try
            {
                compilation = await project
                    .GetCompilationAsync(ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return XamlProjectSemanticCheck.Unavailable;
            }
            if (compilation is null) return XamlProjectSemanticCheck.Unavailable;

            var generatorOutputComplete =
                generatorDiscoveryComplete && !analyzerLoadFailed;
            if (generatorOutputComplete && hasGenerators)
            {
                try
                {
                    // GetCompilationAsync above materialized the workspace's generator-backed
                    // compilation. Read diagnostics from that same cached run. Re-running a new
                    // driver here would both execute untrusted generators once per XAML file and
                    // could disagree with the compilation when a generator is stateful or
                    // non-deterministic.
                    var generatorDiagnostics =
                        await GetWorkspaceGeneratorDiagnosticsAsync(project, ct)
                            .ConfigureAwait(false);
                    generatorOutputComplete =
                        generatorDiagnostics is not null
                        && !generatorDiagnostics.Value.Any(diagnostic =>
                            diagnostic.Severity == DiagnosticSeverity.Error
                            || diagnostic.Id is "CS8784" or "CS8785");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    generatorOutputComplete = false;
                }
            }

            try
            {
                var diagnostics = compilation.GetDiagnostics(ct);
                return new XamlProjectSemanticCheck(
                    compilation,
                    generatorOutputComplete
                    && !diagnostics.Any(diagnostic =>
                        diagnostic.Severity == DiagnosticSeverity.Error),
                    generatorOutputComplete);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new XamlProjectSemanticCheck(
                    compilation,
                    IsComplete: false,
                    GeneratorOutputComplete: false);
            }
        }
        finally
        {
            foreach (var reference in fileReferences)
            {
                reference.AnalyzerLoadFailed -= loadFailureHandler;
            }
        }
    }

    [SuppressMessage(
        "ReflectionAnalysis",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The pinned Roslyn workspace API has no public accessor for its cached generator diagnostics. Missing or changed internals fail closed.")]
    private static async Task<ImmutableArray<Diagnostic>?>
        GetWorkspaceGeneratorDiagnosticsAsync(
            Project project,
            CancellationToken ct)
    {
        if (_getSourceGeneratorDiagnosticsMethod is null)
        {
            return null;
        }

        object? invocation;
        try
        {
            invocation = _getSourceGeneratorDiagnosticsMethod.Invoke(
                project,
                [ct]);
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is OperationCanceledException)
        {
            throw (OperationCanceledException)ex.InnerException;
        }
        catch
        {
            return null;
        }

        return invocation switch
        {
            ValueTask<ImmutableArray<Diagnostic>> valueTask =>
                await valueTask.ConfigureAwait(false),
            Task<ImmutableArray<Diagnostic>> task =>
                await task.ConfigureAwait(false),
            _ => null,
        };
    }
}

internal sealed record XamlCompilationState(
    Compilation Compilation,
    bool IsComplete,
    bool CanResolve,
    bool DirectBindingMembersOnly);

internal sealed record XamlCompilationCandidate(
    Compilation Compilation,
    bool HasWpfEvidence,
    bool IsComplete,
    bool GeneratorOutputComplete,
    bool CanResolve);

internal sealed record XamlProjectSemanticCheck(
    Compilation? Compilation,
    bool IsComplete,
    bool GeneratorOutputComplete)
{
    public static XamlProjectSemanticCheck Unavailable { get; } =
        new(null, IsComplete: false, GeneratorOutputComplete: false);
}
