using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Interop;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ManagedInteropUsageIndexingTests
{
    [Fact]
    public async Task Proven_callback_and_release_flows_follow_caller_file_lifecycle()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, RiskSource);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            var cold = await indexer.IndexAllAsync();

            cold.FailedFiles.Should().BeEmpty();
            var callback = (await InteropFactStoreReader
                    .ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().ContainSingle().Subject;
            callback.Row.SymbolCanonicalKey.Should().Be(
                callback.Fact.Usage.CallerSymbolCanonicalKey);
            callback.Fact.ManagedImportSymbolCanonicalKey.Should().Contain(
                ".RegisterCallback(");
            callback.Fact.Usage.CallerSymbolCanonicalKey.Should().EndWith(
                ".RegisterUnrootedCallback");
            callback.Fact.Usage.ParameterPosition.Should().Be(0);
            callback.Fact.Usage.Rooting.Should().Be(
                CallbackGcRooting.Unrooted);
            callback.Fact.Usage.Target.Should().BeEquivalentTo(
                InteropTarget.WindowsX64Msvc);
            callback.Fact.Usage.Evidence.ProducingFileId.Should().Be(
                callback.Row.FileId);
            callback.Fact.Usage.Evidence.Location.FilePath.Should().Be(
                sourcePath);
            callback.Fact.Usage.Evidence.Confidence.Should().Be(
                EvidenceConfidence.Semantic);
            callback.Fact.Usage.Evidence.Producer.Should().Be(
                "roslyn-managed-interop-usage");

            var release = (await InteropFactStoreReader
                    .ReadManagedReturnReleasesAsync(store))
                .Facts.Should().ContainSingle().Subject;
            release.Row.SymbolCanonicalKey.Should().Be(
                release.Fact.Release.CallerSymbolCanonicalKey);
            release.Fact.ManagedImportSymbolCanonicalKey.Should().Contain(
                ".Allocate");
            release.Fact.Release.CallerSymbolCanonicalKey.Should().EndWith(
                ".FreeWithWrongAllocator");
            release.Fact.Release.ReleaseFamily.Should().Be(
                InteropAllocatorFamily.CoTaskMem);
            release.Fact.Release.Evidence.ProducingFileId.Should().Be(
                release.Row.FileId);
            release.Fact.Release.Evidence.Location.FilePath.Should().Be(
                sourcePath);

            await File.WriteAllTextAsync(sourcePath, SafeSource);
            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty();

            await File.WriteAllTextAsync(sourcePath, RiskSource);
            (await indexer.IndexChangedFilesAsync([sourcePath]))
                .FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().ContainSingle();
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().ContainSingle();

            File.Delete(sourcePath);
            (await indexer.IndexChangedFilesAsync([sourcePath]))
                .FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Alias_root_and_non_adjacent_release_shapes_fail_closed()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, SafeSource);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();

            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().BeEmpty(
                    "a field-backed callback is not a direct ephemeral delegate");
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty(
                    "non-adjacent alias flow is outside the proven release shape");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Cross_file_calls_bind_usage_to_exact_import_but_follow_caller_file()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, callerPath) =
                await WriteSingleProjectSolutionAsync(root);
            var declarationPath = Path.Join(
                Path.GetDirectoryName(callerPath)!,
                "NativeMethods.cs");
            await File.WriteAllTextAsync(
                declarationPath,
                CrossFileDeclarations);
            await File.WriteAllTextAsync(callerPath, CrossFileCaller);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();

            var imports = (await InteropFactStoreReader
                    .ReadManagedImportsAsync(store))
                .Facts;
            imports.Should().HaveCount(2);
            imports.Should().OnlyContain(item =>
                item.Row.FilePath == declarationPath);
            var callback = (await InteropFactStoreReader
                    .ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().ContainSingle().Subject;
            callback.Row.FilePath.Should().Be(callerPath);
            callback.Fact.Usage.Evidence.Location.FilePath.Should().Be(
                callerPath);
            imports.Select(item => item.Fact.SymbolCanonicalKey)
                .Should()
                .Contain(callback.Fact.ManagedImportSymbolCanonicalKey);
            var release = (await InteropFactStoreReader
                    .ReadManagedReturnReleasesAsync(store))
                .Facts.Should().ContainSingle().Subject;
            release.Row.FilePath.Should().Be(callerPath);
            release.Fact.Release.Evidence.Location.FilePath.Should().Be(
                callerPath);
            imports.Select(item => item.Fact.SymbolCanonicalKey)
                .Should()
                .Contain(release.Fact.ManagedImportSymbolCanonicalKey);

            var retainedFinding = new InteropFindingProjection(
                InteropRuleIds.CallbackGcRisk,
                InteropFindingSeverity.Warning,
                "last-good callback finding",
                callback.Fact.Usage.CallerSymbolCanonicalKey,
                "c:E:native/interop.h::risk_register_callback",
                InteropTarget.WindowsX64Msvc,
                EvidenceConfidence.Semantic,
                [
                    new InteropEvidenceProjection(
                        new SourceLocation(
                            callerPath,
                            10,
                            1,
                            10,
                            20),
                        EvidenceConfidence.Semantic,
                        "interop-analysis"),
                ])
            {
                BoundaryManagedSymbolCanonicalKey =
                    callback.Fact.ManagedImportSymbolCanonicalKey,
            };
            await store.ReplaceFileDerivedProjectionAsync(
                callerPath,
                "interop-analysis",
                [InteropAnnotationFlavors.Finding],
                [
                    new FileAnnotationFact(
                        retainedFinding.ManagedSymbolCanonicalKey,
                        InteropRuleIds.CallbackGcRisk,
                        "MedInterop.Interop004",
                        InteropAnnotationFlavors.Finding,
                        InteropFactPayloadCodec.EncodeFinding(
                            retainedFinding),
                        AttributeCanonicalKey: null),
                ],
                edges: []);

            await File.WriteAllTextAsync(
                callerPath,
                CrossFileSafeCaller);
            (await indexer.IndexChangedFilesAsync([callerPath]))
                .FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader.ReadFindingsAsync(store))
                .Facts.Should().ContainSingle(
                    "source reconciliation must retain the last-good "
                    + "analysis flavor until publication succeeds");

            await File.WriteAllTextAsync(callerPath, CrossFileCaller);
            (await indexer.IndexChangedFilesAsync([callerPath]))
                .FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().ContainSingle();
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().ContainSingle();

            await File.WriteAllTextAsync(
                declarationPath,
                CrossFileManagedImplementations);
            (await indexer.IndexChangedFilesAsync([declarationPath]))
                .FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedImportsAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().BeEmpty(
                    "an import-only edit must fan out to unchanged callers");
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty(
                    "an import-only edit must fan out to unchanged callers");

            await File.WriteAllTextAsync(
                declarationPath,
                CrossFileDeclarations);
            (await indexer.IndexChangedFilesAsync([declarationPath]))
                .FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedImportsAsync(store))
                .Facts.Should().HaveCount(2);
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().ContainSingle();
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().ContainSingle();

            File.Delete(callerPath);
            (await indexer.IndexChangedFilesAsync([callerPath]))
                .FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedImportsAsync(store))
                .Facts.Should().HaveCount(2);
            (await InteropFactStoreReader.ReadFindingsAsync(store))
                .Facts.Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Failed_import_fanout_retains_last_good_analysis_edge()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, callerPath) =
                await WriteSingleProjectSolutionAsync(root);
            var declarationPath = Path.Join(
                Path.GetDirectoryName(callerPath)!,
                "NativeMethods.cs");
            await File.WriteAllTextAsync(
                declarationPath,
                CrossFileDeclarations);
            await File.WriteAllTextAsync(callerPath, CrossFileCaller);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            var proxy =
                DispatchProxy.Create<
                    IGraphStore,
                    ManagedUsagePublicationFailureProxy>();
            var failureProxy =
                (ManagedUsagePublicationFailureProxy)(object)proxy;
            failureProxy.Inner = store;
            await using var indexer = CreateIndexer(proxy, root);
            await indexer.OpenAsync(solutionPath);

            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            var imports = (await InteropFactStoreReader
                    .ReadManagedImportsAsync(store))
                .Facts
                .OrderBy(item => item.Fact.SymbolCanonicalKey)
                .ToArray();
            imports.Should().HaveCount(2);
            var source = imports[0];
            var target = imports[1];
            await store.ReplaceFileDerivedProjectionAsync(
                declarationPath,
                InteropFactProducers.Analysis,
                [InteropAnnotationFlavors.Match],
                annotations: [],
                edges:
                [
                    new ProducerEdgeEvidenceFact(
                        source.Fact.SymbolCanonicalKey,
                        target.Fact.SymbolCanonicalKey,
                        DevBitsLab.Mcp.SourceGraph.Sdk.EdgeKinds
                            .PInvokeMapsTo,
                        Metadata: null,
                        new FileEvidenceFact(
                            new SourceLocation(
                                declarationPath,
                                1,
                                1,
                                1,
                                2),
                            EvidenceConfidence.Semantic,
                            InteropFactProducers.Analysis,
                            Metadata: null)),
                ]);

            failureProxy.FailNextManagedUsagePublication = true;
            await File.WriteAllTextAsync(
                declarationPath,
                CrossFileRetargetedDeclarations);
            var failed = await indexer.IndexChangedFilesAsync(
                [declarationPath]);

            failed.FailedFiles.Should().ContainSingle(failure =>
                failure.Reason.Contains(
                    "managed interop usage publication failed",
                    StringComparison.Ordinal));
            failureProxy.ManagedUsagePublicationFailures.Should().Be(1);
            var evidence = await store.ListEdgeEvidenceAsync(
                source.Row.SymbolId,
                target.Row.SymbolId,
                DevBitsLab.Mcp.SourceGraph.Sdk.EdgeKinds
                    .PInvokeMapsTo);
            evidence.Should().ContainSingle();
            evidence[0].Producer.Should().Be(
                InteropFactProducers.Analysis,
                "an incomplete caller fanout must not clear last-good analysis");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Disappearing_generated_import_fans_out_and_removes_stale_owner()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, markerPath, callerPath) =
                await WriteGeneratedInteropSolutionAsync(
                    root,
                    LocateFixtureGeneratorAssembly());
            await File.WriteAllTextAsync(
                markerPath,
                "// GENERATE_INTEROP_IMPORT");
            await File.WriteAllTextAsync(
                callerPath,
                GeneratedInteropCaller);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            var initial = await indexer.IndexAllAsync();

            initial.FailedFiles.Should().BeEmpty();
            var generatedImport = (await InteropFactStoreReader
                    .ReadManagedImportsAsync(store))
                .Facts.Should().ContainSingle().Subject;
            generatedImport.Row.FilePath.Should().Contain(
                ".sourcegraph-generated");
            generatedImport.Fact.Evidence.Location.FilePath.Should().Be(
                generatedImport.Row.FilePath,
                "generated facts must use their stable persisted owner path");
            (await InteropFactStoreReader
                    .ReadManagedReturnReleasesAsync(store))
                .Facts.Should().ContainSingle(item =>
                    item.Row.FilePath == callerPath);

            await File.WriteAllTextAsync(markerPath, "// disabled");
            var changed = await indexer.IndexChangedFilesAsync(
                [markerPath]);

            changed.FailedProjects.Should().BeEmpty();
            changed.FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedImportsAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader
                    .ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty(
                    "the unchanged caller must be refreshed when its generated import disappears");
            (await store.ListGeneratedFilesAsync(int.MaxValue))
                .Should().NotContain(file =>
                    file.FilePath == generatedImport.Row.FilePath);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Generated_owner_that_removes_import_fans_out_without_disappearing()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, markerPath, callerPath) =
                await WriteGeneratedInteropSolutionAsync(
                    root,
                    LocateFixtureGeneratorAssembly());
            await File.WriteAllTextAsync(
                markerPath,
                """
                // GENERATE_INTEROP_IMPORT
                // KEEP_GENERATED_INTEROP_OWNER
                """);
            await File.WriteAllTextAsync(
                callerPath,
                GeneratedInteropCaller);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            var initial = await indexer.IndexAllAsync();

            initial.FailedFiles.Should().BeEmpty();
            var generatedImport = (await InteropFactStoreReader
                    .ReadManagedImportsAsync(store))
                .Facts.Should().ContainSingle().Subject;
            (await InteropFactStoreReader
                    .ReadManagedReturnReleasesAsync(store))
                .Facts.Should().ContainSingle(item =>
                    item.Row.FilePath == callerPath);

            await File.WriteAllTextAsync(
                markerPath,
                "// KEEP_GENERATED_INTEROP_OWNER");
            var changed = await indexer.IndexChangedFilesAsync(
                [markerPath]);

            changed.FailedProjects.Should().BeEmpty();
            changed.FailedFiles.Should().BeEmpty();
            (await InteropFactStoreReader.ReadManagedImportsAsync(store))
                .Facts.Should().BeEmpty();
            (await InteropFactStoreReader
                    .ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty(
                    "same-owner generated content changes must refresh unchanged callers");
            (await store.ListGeneratedFilesAsync(int.MaxValue))
                .Should().Contain(file =>
                    file.FilePath == generatedImport.Row.FilePath,
                    "the generated owner remains present but no longer declares an import");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Property_and_event_accessors_are_attributed_to_indexed_owner_symbols()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, AccessorSource);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();

            var callbackCallers = (await InteropFactStoreReader
                    .ReadManagedCallbackUsagesAsync(store))
                .Facts
                .Select(item => item.Fact.Usage.CallerSymbolCanonicalKey)
                .ToArray();
            callbackCallers.Should().HaveCount(2);
            callbackCallers.Should().Contain(key =>
                key.StartsWith("csharp:P:", StringComparison.Ordinal)
                && key.EndsWith(".Registered", StringComparison.Ordinal));
            callbackCallers.Should().Contain(key =>
                key.StartsWith("csharp:E:", StringComparison.Ordinal)
                && key.EndsWith(".Changed", StringComparison.Ordinal));

            var releaseCallers = (await InteropFactStoreReader
                    .ReadManagedReturnReleasesAsync(store))
                .Facts
                .Select(item => item.Fact.Release.CallerSymbolCanonicalKey)
                .ToArray();
            releaseCallers.Should().HaveCount(2);
            releaseCallers.Should().Contain(key =>
                key.StartsWith("csharp:P:", StringComparison.Ordinal)
                && key.EndsWith(".Released", StringComparison.Ordinal));
            releaseCallers.Should().Contain(key =>
                key.StartsWith("csharp:E:", StringComparison.Ordinal)
                && key.EndsWith(".Changed", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Nested_anonymous_and_local_function_calls_fail_closed()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, NestedCallableSource);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();

            (await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(store))
                .Facts.Should().BeEmpty(
                    "an uninvoked nested callable is not a proven call by its container");
            (await InteropFactStoreReader.ReadManagedReturnReleasesAsync(store))
                .Facts.Should().BeEmpty(
                    "nested callable flow must not be attributed to its outer member");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static RoslynIndexer CreateIndexer(
        IGraphStore store,
        string root) =>
        new(
            store,
            logger: null,
            embeddingsSink: null,
            privacyRoot: root,
            excludePatterns: [],
            interopTarget: InteropTarget.WindowsX64Msvc);

    private const string RiskSource = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class Risks
        {
            internal delegate void ResultCallback(int value);

            [DllImport("medalgo", EntryPoint = "risk_register_callback")]
            private static extern void RegisterCallback(ResultCallback callback);

            [DllImport("medalgo", EntryPoint = "risk_allocate")]
            private static extern IntPtr Allocate();

            internal static void RegisterUnrootedCallback() =>
                RegisterCallback(value => Console.WriteLine(value));

            internal static void FreeWithWrongAllocator()
            {
                var pointer = Allocate();
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        """;

    private const string SafeSource = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class Risks
        {
            internal delegate void ResultCallback(int value);
            private static readonly ResultCallback Rooted =
                value => Console.WriteLine(value);

            [DllImport("medalgo", EntryPoint = "risk_register_callback")]
            private static extern void RegisterCallback(ResultCallback callback);

            [DllImport("medalgo", EntryPoint = "risk_allocate")]
            private static extern IntPtr Allocate();

            internal static void RegisterRootedCallback() =>
                RegisterCallback(Rooted);

            internal static void ReleaseAfterUnknownFlow()
            {
                var pointer = Allocate();
                Console.WriteLine(pointer);
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        """;

    private const string CrossFileDeclarations = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class NativeMethods
        {
            internal delegate void ResultCallback(int value);

            [DllImport("medalgo", EntryPoint = "risk_register_callback")]
            internal static extern void RegisterCallback(
                ResultCallback callback);

            [DllImport("medalgo", EntryPoint = "risk_allocate")]
            internal static extern IntPtr Allocate();
        }
        """;

    private const string CrossFileCaller = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class Caller
        {
            internal static void Run()
            {
                NativeMethods.RegisterCallback(
                    value => Console.WriteLine(value));
                var pointer = NativeMethods.Allocate();
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        """;

    private const string CrossFileManagedImplementations = """
        using System;

        namespace Fixture;

        internal static class NativeMethods
        {
            internal delegate void ResultCallback(int value);

            internal static void RegisterCallback(
                ResultCallback callback)
            {
            }

            internal static IntPtr Allocate() => IntPtr.Zero;
        }
        """;

    private const string CrossFileRetargetedDeclarations = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class NativeMethods
        {
            internal delegate void ResultCallback(int value);

            [DllImport("medalgo", EntryPoint = "retargeted_callback")]
            internal static extern void RegisterCallback(
                ResultCallback callback);

            [DllImport("medalgo", EntryPoint = "retargeted_allocate")]
            internal static extern IntPtr Allocate();
        }
        """;

    private const string CrossFileSafeCaller = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class Caller
        {
            private static readonly NativeMethods.ResultCallback Rooted =
                value => Console.WriteLine(value);

            internal static void Run()
            {
                NativeMethods.RegisterCallback(Rooted);
                var pointer = NativeMethods.Allocate();
                Console.WriteLine(pointer);
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        """;

    private const string AccessorSource = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class AccessorRisks
        {
            internal delegate void ResultCallback(int value);

            [DllImport("medalgo", EntryPoint = "risk_register_callback")]
            private static extern void RegisterCallback(
                ResultCallback callback);

            [DllImport("medalgo", EntryPoint = "risk_allocate")]
            private static extern IntPtr Allocate();

            internal static ResultCallback Registered
            {
                get
                {
                    RegisterCallback(Handle);
                    return Handle;
                }
            }

            internal static IntPtr Released
            {
                set => Marshal.FreeCoTaskMem(Allocate());
            }

            internal static event Action? Changed
            {
                add => RegisterCallback(Handle);
                remove => Marshal.FreeCoTaskMem(Allocate());
            }

            private static void Handle(int value) =>
                Console.WriteLine(value);
        }
        """;

    private const string NestedCallableSource = """
        using System;
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class NestedRisks
        {
            internal delegate void ResultCallback(int value);

            [DllImport("medalgo", EntryPoint = "risk_register_callback")]
            private static extern void RegisterCallback(
                ResultCallback callback);

            [DllImport("medalgo", EntryPoint = "risk_allocate")]
            private static extern IntPtr Allocate();

            internal static void DeclareOnly()
            {
                Action callbackRisk = () =>
                    RegisterCallback(value => Console.WriteLine(value));

                void ReleaseRisk()
                {
                    var pointer = Allocate();
                    Marshal.FreeCoTaskMem(pointer);
                }

                GC.KeepAlive(callbackRisk);
                GC.KeepAlive((Action)ReleaseRisk);
            }
        }
        """;

    private const string GeneratedInteropCaller = """
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class Caller
        {
            internal static void Release()
            {
                var pointer =
                    Generated.Interop.NativeMethods.Allocate();
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        """;

    private static string CreateTemporaryRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-managed-interop-usage-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(string SolutionPath, string SourcePath)>
        WriteSingleProjectSolutionAsync(string root)
    {
        var projectDirectory = Path.Join(root, "Fixture");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Join(projectDirectory, "Fixture.csproj");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        var sourcePath = Path.Join(projectDirectory, "Risks.cs");
        var solutionPath = Path.Join(root, "Fixture.sln");
        await File.WriteAllTextAsync(solutionPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Fixture", "Fixture\Fixture.csproj", "{5947A5D0-6C40-40EE-80F8-18C551CF0448}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                    Release|Any CPU = Release|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {5947A5D0-6C40-40EE-80F8-18C551CF0448}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {5947A5D0-6C40-40EE-80F8-18C551CF0448}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {5947A5D0-6C40-40EE-80F8-18C551CF0448}.Release|Any CPU.ActiveCfg = Release|Any CPU
                    {5947A5D0-6C40-40EE-80F8-18C551CF0448}.Release|Any CPU.Build.0 = Release|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        return (solutionPath, sourcePath);
    }

    private static async Task<(
        string SolutionPath,
        string MarkerPath,
        string CallerPath)> WriteGeneratedInteropSolutionAsync(
            string root,
            string analyzerPath)
    {
        var projectDirectory = Path.Join(root, "Fixture");
        Directory.CreateDirectory(projectDirectory);
        var escapedAnalyzerPath =
            System.Security.SecurityElement.Escape(analyzerPath)!;
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "Fixture.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <Analyzer Include="{{escapedAnalyzerPath}}" />
              </ItemGroup>
            </Project>
            """);
        var markerPath = Path.Join(projectDirectory, "Marker.cs");
        var callerPath = Path.Join(projectDirectory, "Caller.cs");
        var solutionPath = Path.Join(root, "Fixture.sln");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Fixture", "Fixture\Fixture.csproj", "{6947A5D0-6C40-40EE-80F8-18C551CF0449}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                    Release|Any CPU = Release|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {6947A5D0-6C40-40EE-80F8-18C551CF0449}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {6947A5D0-6C40-40EE-80F8-18C551CF0449}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {6947A5D0-6C40-40EE-80F8-18C551CF0449}.Release|Any CPU.ActiveCfg = Release|Any CPU
                    {6947A5D0-6C40-40EE-80F8-18C551CF0449}.Release|Any CPU.Build.0 = Release|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        return (solutionPath, markerPath, callerPath);
    }

    private static string LocateFixtureGeneratorAssembly()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Join(
                directory.FullName,
                "tests",
                "fixtures",
                "Sample.Generators",
                "bin",
                "Debug",
                "netstandard2.0",
                "Sample.Generators.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException(
            "Could not locate the built Sample.Generators fixture assembly.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public class ManagedUsagePublicationFailureProxy : DispatchProxy
    {
        public IGraphStore Inner { get; set; } = null!;
        public bool FailNextManagedUsagePublication { get; set; }
        public int ManagedUsagePublicationFailures { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }
            if (targetMethod.Name
                    == nameof(IGraphStore.ReplaceFileDerivedProjectionsAsync)
                && args is
                [
                    IReadOnlyList<FileDerivedProjectionReplacement>
                        projections,
                    _,
                ]
                && projections.Any(projection =>
                    string.Equals(
                        projection.Producer,
                        ManagedInteropUsageExtractor.Producer,
                        StringComparison.Ordinal))
                && FailNextManagedUsagePublication)
            {
                FailNextManagedUsagePublication = false;
                ManagedUsagePublicationFailures++;
                return Task.FromException(
                    new InvalidOperationException(
                        "simulated managed usage publication failure"));
            }
            return targetMethod.Invoke(Inner, args);
        }
    }
}
