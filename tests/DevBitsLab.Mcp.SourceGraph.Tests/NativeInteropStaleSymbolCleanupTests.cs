using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeInteropStaleSymbolCleanupTests : IAsyncLifetime
{
    private string _temporaryDirectory = string.Empty;
    private string _databasePath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-native-stale-cleanup-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        _databasePath = Path.Join(_temporaryDirectory, "graph.db");
        _store = new SqliteGraphStore(_databasePath);
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
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Deletes_only_proven_orphaned_native_declarations()
    {
        var export = await SeedSymbolAsync(
            "old/export.h",
            "c:E:old/export.h::scan_run",
            SymbolKinds.NativeExport);
        var record = await SeedSymbolAsync(
            "old/types.hpp",
            "cpp:T:old/types.hpp::ScanResult",
            SymbolKinds.Struct);
        var managed = await SeedSymbolAsync(
            "Managed.cs",
            "csharp:M:Managed.Run",
            SymbolKinds.Method);
        await _store!.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                managed.SymbolId,
                "Obsolete",
                "System.ObsoleteAttribute",
                "csharp-attribute",
                "{}",
                AttributeSymbolId: null),
        ]);
        const string missing = "c:E:old/missing.h::missing";

        var result = await _store.DeleteOrphanedNativeInteropSymbolsAsync(
            [record.CanonicalKey, missing, export.CanonicalKey]);

        result.DeletedCanonicalKeys.Should().Equal(
            export.CanonicalKey,
            record.CanonicalKey);
        result.RetainedCanonicalKeys.Should().BeEmpty();
        result.MissingCanonicalKeys.Should().Equal(missing);
        (await _store.GetSymbolByIdAsync(export.SymbolId)).Should().BeNull();
        (await _store.GetSymbolByIdAsync(record.SymbolId)).Should().BeNull();
        (await _store.GetSymbolByIdAsync(managed.SymbolId)).Should().NotBeNull();
        (await _store.ListAnnotationsByFlavorAsync(
                "csharp-attribute",
                afterId: 0,
                limit: 10))
            .Should().ContainSingle();
        (await _store.IntegrityCheckAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task Retains_a_symbol_that_still_has_a_native_annotation()
    {
        var native = await SeedSymbolAsync(
            "old/export.h",
            "c:E:old/export.h::scan_run",
            SymbolKinds.NativeExport);
        await _store!.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                native.SymbolId,
                "NativeExport",
                "MedInterop.NativeExport",
                InteropAnnotationFlavors.NativeExport,
                "{}",
                AttributeSymbolId: null),
        ]);

        var result = await _store.DeleteOrphanedNativeInteropSymbolsAsync(
            [native.CanonicalKey]);

        result.DeletedCanonicalKeys.Should().BeEmpty();
        result.RetainedCanonicalKeys.Should().Equal(native.CanonicalKey);
        result.MissingCanonicalKeys.Should().BeEmpty();
        (await _store.GetSymbolByIdAsync(native.SymbolId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Retains_a_native_target_still_referenced_by_pinvoke_edge()
    {
        var managed = await SeedSymbolAsync(
            "Managed.cs",
            "csharp:M:Managed.NativeCall",
            SymbolKinds.Method);
        var native = await SeedSymbolAsync(
            "old/export.h",
            "c:E:old/export.h::scan_run",
            SymbolKinds.NativeExport);
        await _store!.BulkInsertEdgesAsync(
        [
            new Edge(
                managed.SymbolId,
                native.SymbolId,
                EdgeKinds.PInvokeMapsTo),
        ]);

        var result = await _store.DeleteOrphanedNativeInteropSymbolsAsync(
            [native.CanonicalKey]);

        result.DeletedCanonicalKeys.Should().BeEmpty();
        result.RetainedCanonicalKeys.Should().Equal(native.CanonicalKey);
        (await _store.GetSymbolByIdAsync(native.SymbolId)).Should().NotBeNull();
        (await _store.ListEdgeEvidenceAsync(
                managed.SymbolId,
                native.SymbolId,
                EdgeKinds.PInvokeMapsTo))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Retains_a_key_whose_stored_symbol_kind_is_not_native_projection_kind()
    {
        var symbol = await SeedSymbolAsync(
            "old/export.h",
            "c:E:old/export.h::scan_run",
            SymbolKinds.Method);

        var result = await _store!.DeleteOrphanedNativeInteropSymbolsAsync(
            [symbol.CanonicalKey]);

        result.DeletedCanonicalKeys.Should().BeEmpty();
        result.RetainedCanonicalKeys.Should().Equal(symbol.CanonicalKey);
        (await _store.GetSymbolByIdAsync(symbol.SymbolId)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("csharp:M:Managed.Run")]
    [InlineData("c:E:native.h")]
    [InlineData("c:E:../native.h::run")]
    [InlineData("cpp:T:/native.hpp::Payload")]
    public async Task Rejects_non_projection_or_malformed_keys(string invalidKey)
    {
        var existing = await SeedSymbolAsync(
            "old/export.h",
            "c:E:old/export.h::scan_run",
            SymbolKinds.NativeExport);

        var act = () => _store!.DeleteOrphanedNativeInteropSymbolsAsync(
            [existing.CanonicalKey, invalidKey]);

        await act.Should().ThrowAsync<ArgumentException>();
        (await _store!.GetSymbolByIdAsync(existing.SymbolId))
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("c:F:native.c::helper(int)", SymbolKinds.Function)]
    [InlineData(
        "cpp:F:native.cpp::medical::Algorithm::Run(int)",
        SymbolKinds.Method)]
    public async Task Deletes_proven_orphaned_native_function_symbols(
        string canonicalKey,
        string kind)
    {
        var symbol = await SeedSymbolAsync(
            kind == SymbolKinds.Method
                ? "native.cpp"
                : "native.c",
            canonicalKey,
            kind);

        var result = await _store!.DeleteOrphanedNativeInteropSymbolsAsync(
            [canonicalKey]);

        result.DeletedCanonicalKeys.Should().Equal(canonicalKey);
        (await _store.GetSymbolByIdAsync(symbol.SymbolId)).Should().BeNull();
    }

    [Fact]
    public async Task Rejects_duplicate_keys_before_mutating_the_store()
    {
        var existing = await SeedSymbolAsync(
            "old/export.h",
            "c:E:old/export.h::scan_run",
            SymbolKinds.NativeExport);

        var act = () => _store!.DeleteOrphanedNativeInteropSymbolsAsync(
            [existing.CanonicalKey, existing.CanonicalKey]);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*duplicated*");
        (await _store!.GetSymbolByIdAsync(existing.SymbolId))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Rejects_an_oversized_key_set_before_mutating_the_store()
    {
        var keys = Enumerable.Range(0, 100_001)
            .Select(index => $"c:E:native/{index}.h::export_{index}")
            .ToArray();

        var act = () => _store!.DeleteOrphanedNativeInteropSymbolsAsync(keys);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*100000-key limit*");
    }

    [Fact]
    public async Task Precancelled_cleanup_leaves_the_symbol_untouched()
    {
        var existing = await SeedSymbolAsync(
            "old/export.h",
            "c:E:old/export.h::scan_run",
            SymbolKinds.NativeExport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => _store!.DeleteOrphanedNativeInteropSymbolsAsync(
            [existing.CanonicalKey],
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _store!.GetSymbolByIdAsync(existing.SymbolId))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Database_failure_rolls_back_the_complete_cleanup()
    {
        var first = await SeedSymbolAsync(
            "old/first.h",
            "c:E:old/first.h::first",
            SymbolKinds.NativeExport);
        var second = await SeedSymbolAsync(
            "old/second.hpp",
            "cpp:T:old/second.hpp::Second",
            SymbolKinds.Struct);
        await using (var triggerConnection = new SqliteConnection(
                         $"Data Source={_databasePath}"))
        {
            await triggerConnection.OpenAsync();
            await triggerConnection.ExecuteAsync(
                """
                CREATE TRIGGER fail_stale_native_symbol_delete
                BEFORE DELETE ON symbols
                WHEN OLD.canonical_key = 'cpp:T:old/second.hpp::Second'
                BEGIN
                    SELECT RAISE(ABORT, 'injected stale cleanup failure');
                END;
                """);
        }

        var act = () => _store!.DeleteOrphanedNativeInteropSymbolsAsync(
            [first.CanonicalKey, second.CanonicalKey]);

        await act.Should().ThrowAsync<SqliteException>()
            .WithMessage("*injected stale cleanup failure*");
        (await _store!.GetSymbolByIdAsync(first.SymbolId)).Should().NotBeNull();
        (await _store.GetSymbolByIdAsync(second.SymbolId)).Should().NotBeNull();
        (await _store.IntegrityCheckAsync()).Should().Be("ok");
    }

    private async Task<SeededSymbol> SeedSymbolAsync(
        string relativePath,
        string canonicalKey,
        string kind)
    {
        var path = Path.Join(
            _temporaryDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "// fixture");
        var fileId = await _store!.UpsertFileAsync(
            path,
            new byte[32],
            DateTimeOffset.UtcNow);
        var name = canonicalKey[
            (canonicalKey.LastIndexOf(
                "::",
                StringComparison.Ordinal) + 2)..];
        var symbolId = await _store.UpsertSymbolAsync(
            canonicalKey,
            new Symbol(
                0,
                name,
                name,
                kind,
                fileId,
                1,
                1,
                1,
                5,
                null,
                null));
        return new SeededSymbol(
            fileId,
            symbolId,
            canonicalKey,
            path);
    }

    private sealed record SeededSymbol(
        long FileId,
        long SymbolId,
        string CanonicalKey,
        string Path);
}
