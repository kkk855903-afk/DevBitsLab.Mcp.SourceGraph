using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeInteropCoordinatorTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-native-coordinator-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteGraphStore(Path.Join(_root, "graph.db"));
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Complete_source_only_run_publishes_without_binary_verification()
    {
        var sourcePath = Write("native/api.cpp");
        var extractionCalls = 0;
        var verifierCalled = false;
        await using var coordinator = Coordinator(
            new FixedTrustPolicy(allowed: true),
            (request, _) =>
            {
                extractionCalls++;
                return Task.FromResult(Extraction(
                    request,
                    Export(request, "c:E:native/api.cpp::run")));
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                throw new InvalidOperationException(
                    "A source-only TU must not invoke the binary verifier.");
            });

        var result = await coordinator.RunAsync();

        result.State.Status.Should().Be(
            NativeInteropRuntimeStatus.Complete);
        result.State.IsExportUniverseComplete.Should().BeTrue();
        result.State.RetainedLastGood.Should().BeFalse();
        result.State.TranslationUnits.Should().Be(1);
        result.State.IncludedFiles.Should().Be(1);
        result.State.NativeSymbols.Should().Be(1);
        result.State.Failures.Should().BeEmpty();
        extractionCalls.Should().Be(2, "facts come only from the stable reparse");
        verifierCalled.Should().BeFalse();
        coordinator.LastGoodDependencyFanout.Keys
            .Should().ContainSingle(path =>
                PathsEqual(path, sourcePath));

        var stored =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store!);
        stored.IsComplete.Should().BeTrue();
        stored.Facts.Should().ContainSingle()
            .Which.Fact.IsBinaryVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Trust_denial_precedes_extraction_and_storage_changes()
    {
        var extractorCalled = false;
        await using var coordinator = Coordinator(
            new FixedTrustPolicy(allowed: false),
            (_, _) =>
            {
                extractorCalled = true;
                throw new InvalidOperationException();
            });

        var result = await coordinator.RunAsync();

        extractorCalled.Should().BeFalse();
        result.State.Status.Should().Be(
            NativeInteropRuntimeStatus.Partial);
        result.State.RetainedLastGood.Should().BeFalse();
        result.State.Failures.Should().ContainSingle()
            .Which.Code.Should().Be("repository-not-trusted");
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task Partial_rebuild_retains_last_complete_native_projection()
    {
        Write("native/api.cpp");
        var fail = false;
        await using var coordinator = Coordinator(
            new FixedTrustPolicy(allowed: true),
            (request, _) =>
            {
                if (fail)
                {
                    throw new IOException("injected extraction failure");
                }
                return Task.FromResult(Extraction(
                    request,
                    Export(request, "c:E:native/api.cpp::run")));
            });
        var complete = await coordinator.RunAsync();
        complete.State.Status.Should().Be(
            NativeInteropRuntimeStatus.Complete);
        fail = true;

        var partial = await coordinator.RunAsync();

        partial.State.Status.Should().Be(
            NativeInteropRuntimeStatus.Partial);
        partial.State.RetainedLastGood.Should().BeTrue();
        partial.State.LastSuccessfulAt.Should().Be(
            complete.State.LastSuccessfulAt);
        partial.State.IsExportUniverseComplete.Should().BeFalse();
        partial.State.Failures.Should().Contain(failure =>
            failure.Code == "extraction-failed");
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(
                "c:E:native/api.cpp::run");
    }

    [Fact]
    public async Task Concurrent_requests_are_serialized_per_scope()
    {
        Write("native/api.cpp");
        var active = 0;
        var maximumActive = 0;
        await using var coordinator = Coordinator(
            new FixedTrustPolicy(allowed: true),
            async (request, cancellationToken) =>
            {
                var now = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, now);
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(20),
                        cancellationToken);
                    return Extraction(
                        request,
                        Export(request, "c:E:native/api.cpp::run"));
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        var runs = await Task.WhenAll(
            coordinator.RunAsync(),
            coordinator.RunAsync());

        runs.Should().OnlyContain(result =>
            result.State.Status == NativeInteropRuntimeStatus.Complete);
        maximumActive.Should().Be(1);
    }

    [Fact]
    public async Task Successful_rematch_cleans_the_prior_orphan_declaration()
    {
        Write("native/api.cpp");
        var canonicalKey = "c:E:native/api.cpp::old";
        await using var coordinator = Coordinator(
            new FixedTrustPolicy(allowed: true),
            (request, _) => Task.FromResult(Extraction(
                request,
                Export(request, canonicalKey))));
        (await coordinator.RunAsync()).State.Status.Should().Be(
            NativeInteropRuntimeStatus.Complete);
        canonicalKey = "c:E:native/api.cpp::run";

        var replacement = await coordinator.RunAsync();

        replacement.State.Status.Should().Be(
            NativeInteropRuntimeStatus.Complete);
        replacement.State.PendingStaleSymbols.Should().Be(0);
        (await _store!.GetAllSymbolKeysAsync())
            .Should().NotContain(item =>
                item.CanonicalKey == "c:E:native/api.cpp::old");
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(canonicalKey);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_claiming_a_partial_result()
    {
        Write("native/api.cpp");
        using var cancellation = new CancellationTokenSource();
        await using var coordinator = Coordinator(
            new FixedTrustPolicy(allowed: true),
            async (_, cancellationToken) =>
            {
                cancellation.Cancel();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new InvalidOperationException();
            });

        Func<Task> act = () => coordinator.RunAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        coordinator.State.Status.Should().Be(
            NativeInteropRuntimeStatus.NotStarted);
    }

    private NativeInteropCoordinator Coordinator(
        IExecutionTrustPolicy trust,
        NativeInteropExtractor extractor,
        NativeInteropBinaryVerifier? verifier = null) =>
        new(
            _root,
            new ScopeInteropConfig(
                Target,
                [
                    new InteropTranslationUnitConfig(
                        "native/api.cpp",
                        "native.dll",
                        ["-x", "c++"],
                        BinaryPath: null),
                ]),
            new ScopePathPolicy(_root),
            _store!,
            trust,
            extractor,
            verifier);

    private static ClangNativeExtractionResult Extraction(
        ClangNativeExtractionRequest request,
        NativeExport export) =>
        new(
            Functions: [],
            Types: [],
            Exports: [export],
            RecordLayouts: [],
            Diagnostics: [])
        {
            IncludedFiles = [request.SourceFilePath],
        };

    private static NativeExport Export(
        ClangNativeExtractionRequest request,
        string key) =>
        new(
            key,
            "run",
            InteropCallingConvention.Cdecl,
            new AbiTypeRef("void", AbiTypeCategory.Void),
            [],
            HasCLinkage: true,
            IsBinaryVerified: false,
            request.Target,
            new Evidence(
                request.ProducingFileId,
                new SourceLocation(
                    request.SourceFilePath,
                    1,
                    1,
                    1,
                    8),
                EvidenceConfidence.Exact,
                "coordinator-test"))
        {
            LibraryName = request.LibraryName,
            ModuleIdentitySource =
                NativeModuleIdentitySource.Configuration,
        };

    private string Write(string relativePath)
    {
        var path = Path.GetFullPath(Path.Join(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// fixture");
        return path;
    }

    private static bool PathsEqual(string left, string right) =>
        (OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal)
        .Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private static void UpdateMaximum(ref int location, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (current >= value
                || Interlocked.CompareExchange(
                    ref location,
                    value,
                    current) == current)
            {
                return;
            }
        }
    }

    private static InteropTarget Target { get; } =
        InteropTarget.WindowsX64Msvc;

    private sealed class FixedTrustPolicy : IExecutionTrustPolicy
    {
        private readonly bool _allowed;

        public FixedTrustPolicy(bool allowed)
        {
            _allowed = allowed;
        }

        public ExecutionTrustDecision EvaluateRepositoryCapability(
            string repositoryRoot,
            ExecutionCapability capability) =>
            _allowed
                ? new ExecutionTrustDecision(
                    true,
                    ExecutionTrustReason.Allowed)
                : new ExecutionTrustDecision(
                    false,
                    ExecutionTrustReason.RepositoryNotTrusted);

        public ExecutionTrustDecision EvaluatePathPluginCapability(
            string repositoryRoot,
            string entryAssemblyPath,
            ExecutionCapability capability,
            string? bundleRoot = null) =>
            throw new NotSupportedException();

        public ExecutionTrustDecision EvaluateNuGetPluginCapability(
            string repositoryRoot,
            string packageId,
            string exactVersion,
            ExecutionCapability capability) =>
            throw new NotSupportedException();
    }
}
