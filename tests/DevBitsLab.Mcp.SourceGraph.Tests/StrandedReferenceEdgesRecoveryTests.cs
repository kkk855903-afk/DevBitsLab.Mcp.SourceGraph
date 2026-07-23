using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the fix-stranded-reference-edges change. Verifies that pass 1's integrity
/// check recovers a "zombie" file (SHA matches the store but no outgoing-reference rows) by
/// re-walking it in pass 2; that healthy files are NOT re-walked; and that pass 2's per-file
/// try/catch isolates one file's walk failure from the rest of the loop.
/// </summary>
public sealed class StrandedReferenceEdgesRecoveryTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private string _slnPath = string.Empty;
    private string _calculatorPath = string.Empty;

    public async Task InitializeAsync()
    {
        _slnPath = LocateSolution();
        _calculatorPath = Path.GetFullPath(
            Path.Join(Path.GetDirectoryName(_slnPath)!, "Sample.Domain", "Calculator.cs"));

        _tempDir = Path.Join(Path.GetTempPath(), "stranded-refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");

        await using var store = new SqliteGraphStore(_dbPath);
        await RoslynIndexer.IndexSolutionOnceAsync(_slnPath, store);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { /* WAL sidecar held by AV / antivirus — leave for the OS to clean. */ }
        catch (UnauthorizedAccessException) { /* same, but a permissions flavor. */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ZombieFile_isReWalkedOnNextIndex()
    {
        long calcFileId;
        long beforeRefs;
        await using (var store = new SqliteGraphStore(_dbPath))
        {
            calcFileId = await GetFileIdAsync(store, _calculatorPath);
            calcFileId.Should().BeGreaterThan(0, "Calculator.cs should be indexed in the cold pass");
            beforeRefs = await CountRefsForFileAsync(_dbPath, calcFileId);
            beforeRefs.Should().BeGreaterThan(0, "Calculator.cs body produces calls + uses-type refs");
        }

        // Construct the zombie state: drop the file's outgoing refs AND edges (mirroring
        // what ClearFileOutgoingAsync does in production), but keep the file row + symbols
        // intact. content_sha256 stays put, so the next index sees "unchanged" in pass 1.
        // Both tables are wiped because the integrity check probes refs OR edges — leaving
        // edges in place would (correctly) read as "healthy" and skip the SHA check.
        await ClearOutgoingForFileAsync(_dbPath, calcFileId);
        var zombieRefs = await CountRefsForFileAsync(_dbPath, calcFileId);
        zombieRefs.Should().Be(0, "the deletion should have left the file zombied");

        await using (var store = new SqliteGraphStore(_dbPath))
        {
            await RoslynIndexer.IndexSolutionOnceAsync(_slnPath, store);
        }

        var afterRefs = await CountRefsForFileAsync(_dbPath, calcFileId);
        afterRefs.Should().BeGreaterThan(0,
            "the integrity check should have forced pass 2 to re-walk Calculator.cs");
    }

    [Fact]
    public async Task HealthyUnchangedFile_isNotReWalked()
    {
        long calcFileId;
        long beforeRefs;
        await using (var store = new SqliteGraphStore(_dbPath))
        {
            calcFileId = await GetFileIdAsync(store, _calculatorPath);
            beforeRefs = await CountRefsForFileAsync(_dbPath, calcFileId);
            beforeRefs.Should().BeGreaterThan(0);
        }

        await using (var store = new SqliteGraphStore(_dbPath))
        {
            await RoslynIndexer.IndexSolutionOnceAsync(_slnPath, store);
        }

        var afterRefs = await CountRefsForFileAsync(_dbPath, calcFileId);
        // Bytewise equal because pass 1 SHA-skipped the file (refs intact + EXISTS = true);
        // pass 2 never walked it, so refs were neither cleared nor re-emitted.
        afterRefs.Should().Be(beforeRefs,
            "a healthy file should take the SHA-skip fast path; pass 2 must not re-walk it");
    }

    [Fact]
    public async Task PassTwoCatch_oneFileFails_othersSucceed()
    {
        // Fresh DB so cold-index touches every file (no SHA-skip path); the throwing wrapper
        // makes Calculator.cs's pass-2 BulkInsertReferencesAsync fail. Greeter.cs and the
        // other files should still get refs because the catch keeps the loop going.
        var localTempDir = Path.Join(Path.GetTempPath(), "stranded-refs-pt2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localTempDir);
        var localDbPath = Path.Join(localTempDir, "graph.db");

        var greeterPath = Path.GetFullPath(
            Path.Join(Path.GetDirectoryName(_slnPath)!, "Sample.Domain", "Greeter.cs"));

        var capturingLogger = new CapturingLogger();
        long calcFileId;
        long greeterFileId;

        try
        {
            await using (var realStore = new SqliteGraphStore(localDbPath))
            {
                var proxy = DispatchProxy.Create<IGraphStore, ThrowOnReferencesProxy>();
                var typed = (ThrowOnReferencesProxy)proxy;
                typed.Inner = realStore;
                typed.DbPath = localDbPath;
                typed.TargetPath = _calculatorPath;

                await RoslynIndexer.IndexSolutionOnceAsync(_slnPath, proxy, capturingLogger);

                typed.ThrewAtLeastOnce.Should().BeTrue(
                    "the wrapper should have intercepted Calculator.cs's pass-2 BulkInsertReferencesAsync");
            }

            await using (var realStore = new SqliteGraphStore(localDbPath))
            {
                calcFileId = await GetFileIdAsync(realStore, _calculatorPath);
                greeterFileId = await GetFileIdAsync(realStore, greeterPath);
            }

            calcFileId.Should().BeGreaterThan(0);
            greeterFileId.Should().BeGreaterThan(0);

            var calcRefs = await CountRefsForFileAsync(localDbPath, calcFileId);
            var greeterRefs = await CountRefsForFileAsync(localDbPath, greeterFileId);

            calcRefs.Should().Be(0, "the throw aborted Calculator.cs's pass-2 walk");
            greeterRefs.Should().BeGreaterThan(0,
                "pass 2 must have continued past Calculator.cs's failure to insert Greeter.cs's refs");

            capturingLogger.Entries.Should().Contain(
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains("Pass 2 walk failed for")
                    && e.Message.Contains("Calculator.cs"),
                "the warn-level log line should reference the failed file's path");
        }
        finally
        {
            try { if (Directory.Exists(localTempDir)) Directory.Delete(localTempDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task PassTwoCatch_partialCommitRefsButNotEdges_isReClearedToZombie()
    {
        // Wrap to throw on BulkInsertEdgesAsync. Pass 2 commits refs FIRST then edges
        // SECOND, each in its own SQLite transaction. So the throw lands AFTER refs are
        // already committed — without the catch handler's clear-outgoing call, refs
        // would persist in the store and the next index's HasOutgoingReferencesAsync
        // probe would return true → SHA-skip → edges stranded forever.

        // Step 1: from the healthy IAsyncLifetime cold-index, pick a file we know has
        // both refs AND edges in pass 2. That's the file whose throw path we care about
        // (files with no edges trigger no throw at all, so they're irrelevant here).
        var victimPath = await FindFileWithRefsAndEdgesAsync(_dbPath);
        victimPath.Should().NotBeNullOrEmpty(
            "Sample.sln should have at least one file with both refs and edges (e.g. " +
            "Sample.App/Program.cs which calls Calculator.Add and so emits a Calls edge)");

        // Step 2: cold-index a FRESH DB with the throwing wrapper. Confirm no edges
        // landed (proving the wrapper fired) and the victim file's refs were cleared
        // (proving the catch handler's ClearFileOutgoingAsync ran).
        var localTempDir = Path.Join(Path.GetTempPath(), "stranded-refs-pt3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localTempDir);
        var localDbPath = Path.Join(localTempDir, "graph.db");

        var capturingLogger = new CapturingLogger();

        try
        {
            await using (var realStore = new SqliteGraphStore(localDbPath))
            {
                var proxy = DispatchProxy.Create<IGraphStore, ThrowOnEdgesProxy>();
                var typed = (ThrowOnEdgesProxy)proxy;
                typed.Inner = realStore;

                await RoslynIndexer.IndexSolutionOnceAsync(_slnPath, proxy, capturingLogger);

                typed.ThrewAtLeastOnce.Should().BeTrue(
                    "the wrapper should have intercepted at least one BulkInsertEdgesAsync call");
            }

            long victimFileId;
            await using (var realStore = new SqliteGraphStore(localDbPath))
            {
                victimFileId = await GetFileIdAsync(realStore, victimPath!);
            }
            victimFileId.Should().BeGreaterThan(0);

            var victimRefs = await CountRefsForFileAsync(localDbPath, victimFileId);
            var totalEdges = await CountTableAsync(localDbPath, "edges");

            totalEdges.Should().Be(0,
                "every BulkInsertEdgesAsync call with a non-empty batch was wrapped to throw");
            victimRefs.Should().Be(0,
                "the catch handler must clear the victim file's refs after a partial pass-2 " +
                "commit (refs done, edges threw); otherwise the next index's integrity check " +
                "would SHA-skip the file with refs intact and leave its edges stranded forever");

            capturingLogger.Entries.Should().Contain(
                e => e.Level == LogLevel.Warning && e.Message.Contains("Pass 2 walk failed for"),
                "the warn-level log line should fire for at least one failed file");
        }
        finally
        {
            try { if (Directory.Exists(localTempDir)) Directory.Delete(localTempDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    private static async Task<long> GetFileIdAsync(IGraphStore store, string filePath)
    {
        var files = await store.GetAllFilesAsync();
        var match = files.FirstOrDefault(f =>
            string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? 0;
    }

    private static async Task<long> CountRefsForFileAsync(string dbPath, long fileId)
    {
        await using var c = OpenReadOnly(dbPath);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM refs WHERE file_id = $id;";
        cmd.Parameters.AddWithValue("$id", fileId);
        var v = await cmd.ExecuteScalarAsync();
        return v is long l ? l : Convert.ToInt64(v);
    }

    private static async Task<long> CountTableAsync(string dbPath, string table)
    {
        await using var c = OpenReadOnly(dbPath);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        var v = await cmd.ExecuteScalarAsync();
        return v is long l ? l : Convert.ToInt64(v);
    }

    private static async Task<string?> FindFileWithRefsAndEdgesAsync(string dbPath)
    {
        // Returns the path of a file that has at least one ref AND at least one outgoing
        // edge sourced from one of its declared symbols. Used by the partial-commit test
        // to pick a victim whose throwing pass-2 actually exercises the catch handler's
        // ClearFileOutgoingAsync — files with refs but no edges don't trigger the throw.
        await using var c = OpenReadOnly(dbPath);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT f.path
            FROM files f
            JOIN refs r ON r.file_id = f.id
            WHERE EXISTS (
                SELECT 1 FROM edges e
                JOIN symbols s ON s.id = e.src
                WHERE s.file_id = f.id
            )
            GROUP BY f.id, f.path
            ORDER BY COUNT(r.id) DESC
            LIMIT 1;
            """;
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task ClearOutgoingForFileAsync(string dbPath, long fileId)
    {
        // Mirrors SqliteGraphStore.ClearFileOutgoingAsync: drops both refs (by file_id) and
        // edges (whose src is one of the file's declared symbols). Used by the zombie-state
        // construction in the recovery regression test.
        await using var c = OpenReadWrite(dbPath);
        await c.OpenAsync();
        await using var deleteEdges = c.CreateCommand();
        deleteEdges.CommandText =
            "DELETE FROM edges WHERE src IN (SELECT id FROM symbols WHERE file_id = $id);";
        deleteEdges.Parameters.AddWithValue("$id", fileId);
        await deleteEdges.ExecuteNonQueryAsync();
        await using var deleteRefs = c.CreateCommand();
        deleteRefs.CommandText = "DELETE FROM refs WHERE file_id = $id;";
        deleteRefs.Parameters.AddWithValue("$id", fileId);
        await deleteRefs.ExecuteNonQueryAsync();
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;
        return new SqliteConnection(cs);
    }

    private static SqliteConnection OpenReadWrite(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString;
        return new SqliteConnection(cs);
    }

    private static string LocateSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Join(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    /// <summary>
    /// <see cref="DispatchProxy"/>-backed <see cref="IGraphStore"/> that forwards every call to
    /// an inner store, except <see cref="IGraphStore.BulkInsertReferencesAsync"/> when the
    /// batch's <c>FileId</c> resolves to <see cref="TargetPath"/>: there it returns a faulted
    /// Task so the indexer's pass-2 try/catch fires for that one file.
    /// </summary>
    public class ThrowOnReferencesProxy : DispatchProxy
    {
        public IGraphStore Inner { get; set; } = null!;
        public string DbPath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public bool ThrewAtLeastOnce { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;

            if (targetMethod.Name == nameof(IGraphStore.BulkInsertReferencesAsync))
            {
                // Materialize the batch so we can peek at FileId without consuming it.
                var refs = (IEnumerable<SymbolReference>?)args![0];
                var refList = refs?.ToList() ?? new List<SymbolReference>();
                args[0] = refList;

                if (refList.Count > 0
                    && IsTargetFile(refList[0].FileId))
                {
                    ThrewAtLeastOnce = true;
                    return Task.FromException(
                        new InvalidOperationException("simulated pass-2 failure for test"));
                }
            }

            return targetMethod.Invoke(Inner, args);
        }

        private bool IsTargetFile(long fileId)
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path FROM files WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", fileId);
            var v = cmd.ExecuteScalar() as string;
            return v is not null
                && string.Equals(v, TargetPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <see cref="DispatchProxy"/>-backed <see cref="IGraphStore"/> that forwards every call to
    /// an inner store, except <see cref="IGraphStore.BulkInsertEdgesAsync"/>: there it returns
    /// a faulted Task whenever the batch is non-empty. Used to simulate the "refs committed,
    /// edges threw" failure mode and verify the catch handler re-clears so the next index
    /// re-walks instead of SHA-skipping past a refs-intact-but-edges-empty file.
    /// </summary>
    public class ThrowOnEdgesProxy : DispatchProxy
    {
        public IGraphStore Inner { get; set; } = null!;
        public bool ThrewAtLeastOnce { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;

            if (targetMethod.Name == nameof(IGraphStore.BulkInsertEdgesAsync))
            {
                // Materialize so we can both peek at Count and forward without consuming.
                var edges = (IEnumerable<Edge>?)args![0];
                var edgeList = edges?.ToList() ?? new List<Edge>();
                args[0] = edgeList;

                if (edgeList.Count > 0)
                {
                    ThrewAtLeastOnce = true;
                    return Task.FromException(
                        new InvalidOperationException("simulated edges-insert failure for test"));
                }
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    /// <summary>Captures log entries so the test can assert on the warn-level message.</summary>
    private sealed class CapturingLogger : ILogger<RoslynIndexer>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
