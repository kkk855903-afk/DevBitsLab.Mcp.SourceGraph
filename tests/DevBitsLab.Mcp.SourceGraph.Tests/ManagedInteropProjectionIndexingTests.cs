using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ManagedInteropProjectionIndexingTests
{
    [Fact]
    public async Task Bounded_projection_read_validates_bounds_empty_sets_and_cancellation()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));

            (await store.ListAnnotationsForFilesByFlavorsAsync(
                    [],
                    [InteropAnnotationFlavors.ManagedImport],
                    limit: 1))
                .Should().BeEmpty();
            (await store.ListAnnotationsForFilesByFlavorsAsync(
                    ["Managed.cs"],
                    [],
                    limit: 1))
                .Should().BeEmpty();

            Func<Task> invalidLimit = async () =>
                await store.ListAnnotationsForFilesByFlavorsAsync(
                    ["Managed.cs"],
                    [InteropAnnotationFlavors.ManagedImport],
                    limit: 0);
            await invalidLimit.Should()
                .ThrowAsync<ArgumentOutOfRangeException>();

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            Func<Task> cancelled = async () =>
                await store.ListAnnotationsForFilesByFlavorsAsync(
                    [],
                    [],
                    limit: 1,
                    cancellation.Token);
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Cold_and_incremental_index_replace_real_managed_import_projection()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, ImportSource("run"));
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            var cold = await indexer.IndexAllAsync();

            cold.FailedFiles.Should().BeEmpty();
            var row = (await ReadImportsAsync(store)).Should()
                .ContainSingle()
                .Subject;
            var import = InteropFactPayloadCodec.DecodeManagedImport(
                row.ArgsJson!,
                row.FileId);
            import.SymbolCanonicalKey.Should().Be(row.SymbolCanonicalKey);
            import.LibraryName.Should().Be("medalgo");
            import.EntryPoint.Should().Be("run");
            import.Target.Should().BeEquivalentTo(
                InteropTarget.WindowsX64Msvc);
            import.Evidence.ProducingFileId.Should().Be(row.FileId);
            import.Evidence.Location.FilePath.Should().Be(sourcePath);

            await File.WriteAllTextAsync(
                sourcePath,
                """
                namespace Fixture;

                internal static class NativeMethods
                {
                    internal static int Run(int value) => value;
                }
                """);
            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().BeEmpty();
            (await ReadImportsAsync(store)).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unchanged_cold_start_regenerates_missing_projection(
        bool multiTarget)
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root, multiTarget);
            await File.WriteAllTextAsync(
                sourcePath,
                ImportSourceWithOutgoingReference());
            var dbPath = Path.Join(root, "graph.db");
            await using var store = new SqliteGraphStore(dbPath);

            await using (var first = CreateIndexer(store, root))
            {
                await first.OpenAsync(solutionPath);
                (await first.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            var original = (await ReadImportsAsync(store))
                .Should().ContainSingle().Subject;
            (await store.HasOutgoingReferencesAsync(original.FileId))
                .Should().BeTrue(
                    "the regression must not be repaired by the legacy zombie-file fallback");
            await store.ReplaceAnnotationsForFileByFlavorAsync(
                original.FilePath,
                InteropAnnotationFlavors.ManagedImport,
                []);
            (await ReadImportsAsync(store)).Should().BeEmpty();

            await using (var restarted = CreateIndexer(store, root))
            {
                await restarted.OpenAsync(solutionPath);
                var result = await restarted.IndexAllAsync();
                result.FailedFiles.Should().BeEmpty();
                result.FilesIndexed.Should().BeGreaterThan(0);
            }

            var regenerated = (await ReadImportsAsync(store))
                .Should().ContainSingle().Subject;
            InteropFactPayloadCodec.DecodeManagedImport(
                    regenerated.ArgsJson!,
                    regenerated.FileId)
                .EntryPoint.Should().Be("run");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Unchanged_cold_start_regenerates_missing_record_projection()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(
                sourcePath,
                RecordStructSourceWithOutgoingReference());
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));

            await using (var first = CreateIndexer(store, root))
            {
                await first.OpenAsync(solutionPath);
                (await first.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            var original = (await ReadRecordsAsync(store))
                .Should().ContainSingle().Subject;
            (await store.HasOutgoingReferencesAsync(original.FileId))
                .Should().BeTrue(
                    "the regression must not be repaired by the legacy zombie-file fallback");
            await store.ReplaceAnnotationsForFileByFlavorAsync(
                original.FilePath,
                InteropAnnotationFlavors.AbiRecord,
                []);
            (await ReadRecordsAsync(store)).Should().BeEmpty();

            await using (var restarted = CreateIndexer(store, root))
            {
                await restarted.OpenAsync(solutionPath);
                (await restarted.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }

            (await ReadRecordsAsync(store)).Should().ContainSingle();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Unchanged_cold_start_regenerates_partially_missing_projection()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(
                sourcePath,
                MultipleInteropProjectionSource());
            var databasePath = Path.Join(root, "graph.db");
            await using var store = new SqliteGraphStore(databasePath);

            await using (var first = CreateIndexer(store, root))
            {
                await first.OpenAsync(solutionPath);
                (await first.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            var imports = await ReadImportsAsync(store);
            var records = await ReadRecordsAsync(store);
            imports.Should().HaveCount(2);
            records.Should().HaveCount(2);
            (await store.HasOutgoingReferencesAsync(imports[0].FileId))
                .Should().BeTrue(
                    "the regression must not be repaired by the legacy zombie-file fallback");

            await DeleteAnnotationAsync(
                databasePath,
                imports[0].AnnotationId);
            await DeleteAnnotationAsync(
                databasePath,
                records[0].AnnotationId);
            (await ReadImportsAsync(store)).Should().ContainSingle();
            (await ReadRecordsAsync(store)).Should().ContainSingle();

            await using (var restarted = CreateIndexer(store, root))
            {
                await restarted.OpenAsync(solutionPath);
                (await restarted.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }

            (await ReadImportsAsync(store)).Should().HaveCount(2);
            (await ReadRecordsAsync(store)).Should().HaveCount(2);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Partial_record_projection_keeps_its_canonical_owner_across_recovery()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, firstSourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            var secondSourcePath = Path.Join(
                Path.GetDirectoryName(firstSourcePath)!,
                "Packet.Part2.cs");
            await File.WriteAllTextAsync(
                firstSourcePath,
                PartialRecordFirstSource());
            await File.WriteAllTextAsync(
                secondSourcePath,
                PartialRecordSecondSource());
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));

            await using (var first = CreateIndexer(store, root))
            {
                await first.OpenAsync(solutionPath);
                (await first.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            var original = (await ReadRecordsAsync(store))
                .Should().ContainSingle().Subject;
            var sourceFiles = (await store.GetAllFilesAsync())
                .Where(file =>
                    file.Path.EndsWith(
                        "NativeMethods.cs",
                        StringComparison.OrdinalIgnoreCase)
                    || file.Path.EndsWith(
                        "Packet.Part2.cs",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            sourceFiles.Should().HaveCount(2);
            foreach (var sourceFile in sourceFiles)
            {
                (await store.HasOutgoingReferencesAsync(sourceFile.Id))
                    .Should().BeTrue(
                        "partial-owner stability must not depend on zombie recovery");
            }

            await using (var unchanged = CreateIndexer(store, root))
            {
                await unchanged.OpenAsync(solutionPath);
                (await unchanged.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            var stable = (await ReadRecordsAsync(store))
                .Should().ContainSingle().Subject;
            stable.FilePath.Should().Be(original.FilePath);
            stable.ArgsJson.Should().Be(original.ArgsJson);

            await store.ReplaceAnnotationsForFileByFlavorAsync(
                original.FilePath,
                InteropAnnotationFlavors.AbiRecord,
                []);
            await using (var recovering = CreateIndexer(store, root))
            {
                await recovering.OpenAsync(solutionPath);
                (await recovering.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }

            var recovered = (await ReadRecordsAsync(store))
                .Should().ContainSingle().Subject;
            recovered.FilePath.Should().Be(original.FilePath);
            recovered.ArgsJson.Should().Be(original.ArgsJson);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Disabling_target_clears_stale_projection_without_source_edit()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, ImportSource("run"));
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));

            await using (var enabled = CreateIndexer(store, root))
            {
                await enabled.OpenAsync(solutionPath);
                (await enabled.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            (await ReadImportsAsync(store)).Should().ContainSingle();

            await using (var disabled = new RoslynIndexer(
                             store,
                             logger: null,
                             embeddingsSink: null,
                             privacyRoot: root,
                             excludePatterns: []))
            {
                await disabled.OpenAsync(solutionPath);
                (await disabled.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }

            (await ReadImportsAsync(store)).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Conflicting_target_framework_projections_fail_closed()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(
                    root,
                    multiTarget: true);
            await File.WriteAllTextAsync(
                sourcePath,
                ImportSource("stable"));
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);
            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            var oldPayload = (await ReadImportsAsync(store))
                .Should().ContainSingle().Subject.ArgsJson;

            await File.WriteAllTextAsync(
                sourcePath,
                """
                using System.Runtime.InteropServices;

                namespace Fixture;

                internal static class NativeMethods
                {
                #if SECOND_TFM
                    [DllImport("medalgo", EntryPoint = "second", ExactSpelling = true)]
                #else
                    [DllImport("medalgo", EntryPoint = "first", ExactSpelling = true)]
                #endif
                    internal static extern int Run(int value);
                }
                """);

            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains(
                    "conflicting target-framework projections",
                    StringComparison.Ordinal));
            (await ReadImportsAsync(store)).Should().ContainSingle()
                .Which.ArgsJson.Should().Be(oldPayload);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Cold_index_persists_target_complete_managed_record_and_rebuild_cleans_it()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, RecordSource());
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            var cold = await indexer.IndexAllAsync();

            cold.FailedFiles.Should().BeEmpty();
            var row = (await ReadRecordsAsync(store))
                .Should().ContainSingle().Subject;
            row.SymbolCanonicalKey.Should().Be("csharp:T:Fixture.Packet");
            var record = InteropFactPayloadCodec.DecodeAbiRecord(
                row.ArgsJson!,
                row.FileId);
            record.SymbolCanonicalKey.Should().Be(row.SymbolCanonicalKey);
            record.Kind.Should().Be(AbiRecordKind.Sequential);
            record.Pack.Should().Be(1);
            record.AlignmentBytes.Should().Be(1);
            record.SizeBytes.Should().Be(15);
            record.Fields.Select(field => field.Name).Should().Equal(
                "Code",
                "Enabled",
                "Count",
                "Values");
            record.Fields.Select(field => field.OffsetBytes).Should().Equal(
                0,
                1,
                5,
                9);
            record.Fields[1].Type.Category.Should().Be(
                AbiTypeCategory.Boolean);
            record.Fields[1].SizeBytes.Should().Be(4);
            record.Fields[3].Type.FixedArrayLength.Should().Be(3);
            record.Target.Should().BeEquivalentTo(
                InteropTarget.WindowsX64Msvc);
            record.Target.RuntimeIdentifier.Should().Be("win-x64");
            record.Target.Architecture.Should().Be(
                InteropArchitecture.X64);
            record.Target.CompilerAbi.Should().Be(
                InteropCompilerAbi.Msvc);
            record.Target.PointerSizeBytes.Should().Be(8);
            record.Target.DefaultPack.Should().Be(8);
            record.Evidence.ProducingFileId.Should().Be(row.FileId);
            record.Evidence.Location.FilePath.Should().Be(sourcePath);
            record.Fields.Should().OnlyContain(field =>
                field.Evidence.ProducingFileId == row.FileId
                && field.Evidence.Location.FilePath == sourcePath);

            await File.WriteAllTextAsync(
                sourcePath,
                """
                using System.Runtime.InteropServices;

                namespace Fixture;

                [StructLayout(LayoutKind.Auto)]
                internal struct Packet
                {
                    public int Value;
                }
                """);
            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().BeEmpty();
            (await ReadRecordsAsync(store)).Should().BeEmpty(
                "a successful no-layout rebuild replaces the prior managed projection");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Deleted_file_cleans_managed_record_projection()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, RecordSource());
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);
            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            (await ReadRecordsAsync(store)).Should().ContainSingle();

            File.Delete(sourcePath);
            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().BeEmpty();
            (await ReadRecordsAsync(store)).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Conflicting_record_target_framework_projections_fail_closed()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(
                    root,
                    multiTarget: true);
            await File.WriteAllTextAsync(sourcePath, RecordSource());
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);
            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            var oldPayload = (await ReadRecordsAsync(store))
                .Should().ContainSingle().Subject.ArgsJson;

            await File.WriteAllTextAsync(
                sourcePath,
                """
                using System.Runtime.InteropServices;

                namespace Fixture;

                #if SECOND_TFM
                [StructLayout(LayoutKind.Sequential, Pack = 1)]
                #else
                [StructLayout(LayoutKind.Sequential, Pack = 8)]
                #endif
                internal struct Packet
                {
                    public byte Code;
                    public int Count;
                }
                """);

            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains(
                    "Managed ABI record",
                    StringComparison.Ordinal)
                && failure.Reason.Contains(
                    "conflicting target-framework projections",
                    StringComparison.Ordinal));
            (await ReadRecordsAsync(store)).Should().ContainSingle()
                .Which.ArgsJson.Should().Be(oldPayload);

            await File.WriteAllTextAsync(
                sourcePath,
                """
                using System.Runtime.InteropServices;

                namespace Fixture;

                #if SECOND_TFM
                [StructLayout(LayoutKind.Auto)]
                #else
                [StructLayout(LayoutKind.Sequential, Pack = 8)]
                #endif
                internal struct Packet
                {
                    public int Count;
                }
                """);

            var presenceConflict =
                await indexer.IndexChangedFilesAsync([sourcePath]);

            presenceConflict.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains(
                    "conflicting target-framework projections",
                    StringComparison.Ordinal));
            (await ReadRecordsAsync(store)).Should().ContainSingle()
                .Which.ArgsJson.Should().Be(oldPayload);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Disabling_target_clears_stale_record_projection_without_source_edit()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, RecordSource());
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));

            await using (var enabled = CreateIndexer(store, root))
            {
                await enabled.OpenAsync(solutionPath);
                (await enabled.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            (await ReadRecordsAsync(store)).Should().ContainSingle();

            await using (var disabled = new RoslynIndexer(
                             store,
                             logger: null,
                             embeddingsSink: null,
                             privacyRoot: root,
                             excludePatterns: []))
            {
                await disabled.OpenAsync(solutionPath);
                (await disabled.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }

            (await ReadRecordsAsync(store)).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Storage_failure_retains_prior_attribute_and_interop_projection()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, ImportSource("stable"));
            var databasePath = Path.Join(root, "graph.db");
            await using var store = new SqliteGraphStore(databasePath);
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);
            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            var oldImport = (await ReadImportsAsync(store))
                .Should().ContainSingle().Subject.ArgsJson;
            var oldAttribute = (await ReadFlavorAsync(
                    store,
                    AttributeExtractor.CSharpAttributeFlavor))
                .Single(row => row.SymbolCanonicalKey.Contains(
                    "NativeMethods.Run",
                    StringComparison.Ordinal))
                .ArgsJson;

            await using (var triggerConnection =
                         new SqliteConnection($"Data Source={databasePath}"))
            {
                await triggerConnection.OpenAsync();
                await using var trigger = triggerConnection.CreateCommand();
                trigger.CommandText =
                    """
                    CREATE TRIGGER fail_managed_interop_projection
                    BEFORE INSERT ON annotations
                    WHEN NEW.flavor = 'interop-managed-import'
                    BEGIN
                        SELECT RAISE(ABORT, 'forced managed interop failure');
                    END;
                    """;
                await trigger.ExecuteNonQueryAsync();
            }

            var downstreamCallbacks = 0;
            indexer.OnFileIndexed = (_, _, _) =>
            {
                downstreamCallbacks++;
                return Task.CompletedTask;
            };
            await File.WriteAllTextAsync(sourcePath, ImportSource("changed"));

            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains(
                    "forced managed interop failure",
                    StringComparison.Ordinal));
            (await ReadImportsAsync(store)).Should().ContainSingle()
                .Which.ArgsJson.Should().Be(oldImport);
            (await ReadFlavorAsync(
                    store,
                    AttributeExtractor.CSharpAttributeFlavor))
                .Single(row => row.SymbolCanonicalKey.Contains(
                    "NativeMethods.Run",
                    StringComparison.Ordinal))
                .ArgsJson.Should().Be(oldAttribute);
            downstreamCallbacks.Should().Be(
                0,
                "a failed projection must not publish its new hash downstream");
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

    private static async Task<IReadOnlyList<StoredAnnotationRow>>
        ReadImportsAsync(IGraphStore store) =>
        await ReadFlavorAsync(
            store,
            InteropAnnotationFlavors.ManagedImport);

    private static async Task<IReadOnlyList<StoredAnnotationRow>>
        ReadRecordsAsync(IGraphStore store) =>
        (await ReadFlavorAsync(
                store,
                InteropAnnotationFlavors.AbiRecord))
            .Where(row => row.SymbolCanonicalKey.StartsWith(
                SymbolMapping.CanonicalKeyScheme,
                StringComparison.Ordinal))
            .ToArray();

    private static async Task<IReadOnlyList<StoredAnnotationRow>>
        ReadFlavorAsync(
            IGraphStore store,
            string flavor) =>
        await store.ListAnnotationsByFlavorAsync(
            flavor,
            afterId: 0,
            limit: 1000);

    private static string ImportSource(string entryPoint) =>
        $$"""
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class NativeMethods
        {
            [DllImport(
                "medalgo",
                EntryPoint = "{{entryPoint}}",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            internal static extern int Run(int value);
        }
        """;

    private static string ImportSourceWithOutgoingReference() =>
        """
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class NativeMethods
        {
            [DllImport(
                "medalgo",
                EntryPoint = "run",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            internal static extern int Run(int value);

            internal static int ExerciseGraph(int value) => Identity(value);

            private static int Identity(int value) => value;
        }
        """;

    private static string RecordStructSourceWithOutgoingReference() =>
        """
        using System.Runtime.InteropServices;

        namespace Fixture;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal record struct Packet
        {
            public int Count;
        }

        internal static class GraphAnchor
        {
            internal static int ExerciseGraph(int value) => Identity(value);

            private static int Identity(int value) => value;
        }
        """;

    private static string MultipleInteropProjectionSource() =>
        """
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class NativeMethods
        {
            [DllImport("medalgo", EntryPoint = "run_a", ExactSpelling = true)]
            internal static extern int RunA(int value);

            [DllImport("medalgo", EntryPoint = "run_b", ExactSpelling = true)]
            internal static extern int RunB(int value);

            internal static int ExerciseGraph(int value) => Identity(value);

            private static int Identity(int value) => value;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FirstPacket
        {
            public int Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal record struct SecondPacket
        {
            public int Value;
        }
        """;

    private static string PartialRecordFirstSource() =>
        """
        using System.Runtime.InteropServices;

        namespace Fixture;

        [StructLayout(LayoutKind.Sequential)]
        internal partial struct Packet
        {
            public int First;
        }

        internal static class FirstGraphAnchor
        {
            internal static int ExerciseGraph(int value) => Identity(value);

            private static int Identity(int value) => value;
        }
        """;

    private static string PartialRecordSecondSource() =>
        """
        namespace Fixture;

        internal partial struct Packet
        {
            public int Second;
        }

        internal static class SecondGraphAnchor
        {
            internal static int ExerciseGraph(int value) => Identity(value);

            private static int Identity(int value) => value;
        }
        """;

    private static string RecordSource() =>
        """
        using System.Runtime.InteropServices;

        namespace Fixture;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct Packet
        {
            public byte Code;

            [MarshalAs(UnmanagedType.Bool)]
            public bool Enabled;

            public int Count;

            [MarshalAs(
                UnmanagedType.ByValArray,
                SizeConst = 3,
                ArraySubType = UnmanagedType.U2)]
            public ushort[] Values;
        }
        """;

    private static async Task DeleteAnnotationAsync(
        string databasePath,
        long annotationId)
    {
        await using var connection =
            new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM annotations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", annotationId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-managed-interop-projection-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(string SolutionPath, string SourcePath)>
        WriteSingleProjectSolutionAsync(
            string root,
            bool multiTarget = false)
    {
        var projectDirectory = Path.Join(root, "App");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Join(projectDirectory, "App.csproj");
        var sourcePath = Path.Join(projectDirectory, "NativeMethods.cs");
        var targetProperties = multiTarget
            ? """
              <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>
              <EnableWindowsTargeting>true</EnableWindowsTargeting>
              """
            : "<TargetFramework>net10.0</TargetFramework>";
        var secondTargetProperties = multiTarget
            ? """
              <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
                <DefineConstants>$(DefineConstants);SECOND_TFM</DefineConstants>
              </PropertyGroup>
              """
            : string.Empty;
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {{targetProperties}}
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              {{secondTargetProperties}}
            </Project>
            """);
        var solutionPath = Path.Join(root, "Fixture.sln");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                    Release|Any CPU = Release|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Release|Any CPU.ActiveCfg = Release|Any CPU
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Release|Any CPU.Build.0 = Release|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        return (solutionPath, sourcePath);
    }

    private static void TryDeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
