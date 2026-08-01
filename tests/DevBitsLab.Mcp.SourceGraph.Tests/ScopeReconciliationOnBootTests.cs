using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="BootReconciler.ReconcileAsync"/> — the boot-time orphan / missing-DB /
/// stuck-`indexing` detection pass introduced by add-scope-health-surface. Each test stands up a
/// synthetic registry + filesystem fixture in a temp dir, runs the reconciler, and asserts both
/// the registry post-state and the corresponding <c>heals.jsonl</c> line shape.
///
/// HealLog is process-static; tests configure a unique log path and use a unique scope-id prefix
/// so concurrent tests don't see each other's lines. The previous log path is restored on dispose.
/// Joins the <c>HealLogState</c> collection so the process-static path doesn't race with other
/// heal-aware tests.
/// </summary>
[Collection("HealLogState")]
public sealed class ScopeReconciliationOnBootTests : IDisposable
{
    private readonly string _previousHealPath = HealLog.LogPath ?? "";
    private readonly string _repoRoot;
    private readonly string _healPath;

    public ScopeReconciliationOnBootTests()
    {
        _repoRoot = Path.Join(Path.GetTempPath(), "sourcegraph-reconcile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(ScopeLayout.SourcegraphDir(_repoRoot));
        Directory.CreateDirectory(ScopeLayout.ScopesDirectory(_repoRoot));
        _healPath = Path.Join(ScopeLayout.SourcegraphDir(_repoRoot), "heals.jsonl");
        HealLog.Configure(_healPath);
    }

    public void Dispose()
    {
        HealLog.Configure(string.IsNullOrEmpty(_previousHealPath) ? null : _previousHealPath);
        try { if (Directory.Exists(_repoRoot)) Directory.Delete(_repoRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task OrphanDb_isMovedToOrphans_andHealEventEmitted()
    {
        // Plant a DB file with no matching registry row.
        var orphanId = $"reconcile-test-orphan-{Guid.NewGuid():N}";
        var orphanDbPath = ScopeLayout.ScopeDbPath(_repoRoot, orphanId);
        var originalBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await File.WriteAllBytesAsync(orphanDbPath, originalBytes);

        await using var registry = await NewRegistryAsync();

        await BootReconciler.ReconcileAsync(registry, _repoRoot, DateTimeOffset.UtcNow,
            NullLogger.Instance, CancellationToken.None);

        // Source file gone, archived file present with same bytes.
        File.Exists(orphanDbPath).Should().BeFalse();
        var orphansDir = ScopeLayout.OrphansDirectory(_repoRoot);
        Directory.Exists(orphansDir).Should().BeTrue();
        var archived = Directory.EnumerateFiles(orphansDir, $"{orphanId}-*.db").Single();
        (await File.ReadAllBytesAsync(archived)).Should().Equal(originalBytes);

        // Heal line shape.
        var line = HealLineForScope(orphanId);
        line.GetProperty("kind").GetString().Should().Be("orphan-db-archived");
        line.GetProperty("ok").GetBoolean().Should().BeTrue();
        line.GetProperty("details").GetString().Should().Contain($"{orphanId}-");
    }

    [Fact]
    public async Task MissingDb_marksScopeDegraded_andHealEventEmitted()
    {
        // Insert a registry row whose corresponding DB file does not exist.
        var missingId = $"reconcile-test-missing-{Guid.NewGuid():N}";
        await using var registry = await NewRegistryAsync();
        await registry.UpsertAsync(NewRow(missingId, status: "ok"));

        await BootReconciler.ReconcileAsync(registry, _repoRoot, DateTimeOffset.UtcNow,
            NullLogger.Instance, CancellationToken.None);

        var post = await registry.GetAsync(missingId);
        post.Should().NotBeNull();
        post!.Status.Should().Be("degraded");
        post.StatusMessage.Should().Be("scope DB file missing — call repair_scope or restart");

        var line = HealLineForScope(missingId);
        line.GetProperty("kind").GetString().Should().Be("missing-db-detected");
        line.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task StuckIndexing_marksScopeDegraded_andHealEventEmitted()
    {
        // Plant DB file + registry row with status=indexing, last_indexed_at before process start.
        var stuckId = $"reconcile-test-stuck-{Guid.NewGuid():N}";
        var dbPath = ScopeLayout.ScopeDbPath(_repoRoot, stuckId);
        await File.WriteAllBytesAsync(dbPath, new byte[] { 1 });

        await using var registry = await NewRegistryAsync();
        var olderRow = NewRow(stuckId, status: "indexing", lastIndexedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await registry.UpsertAsync(olderRow);

        var processStart = DateTimeOffset.UtcNow.AddMinutes(-1);
        await BootReconciler.ReconcileAsync(registry, _repoRoot, processStart,
            NullLogger.Instance, CancellationToken.None);

        var post = await registry.GetAsync(stuckId);
        post.Should().NotBeNull();
        post!.Status.Should().Be("degraded");
        post.StatusMessage.Should().Be("previous index interrupted — call repair_scope or restart");

        var line = HealLineForScope(stuckId);
        line.GetProperty("kind").GetString().Should().Be("stuck-indexing-detected");
        line.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task InterruptedShadow_isQuarantinedWithSidecars_withoutChangingPrimary()
    {
        var scopeId = $"reconcile-test-shadow-{Guid.NewGuid():N}";
        var primary = ScopeLayout.ScopeDbPath(_repoRoot, scopeId);
        var primaryBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(primary, primaryBytes);
        var shadow = primary + ".shadow-interrupted";
        await File.WriteAllBytesAsync(shadow, [5, 6, 7]);
        await File.WriteAllBytesAsync(shadow + "-wal", [8, 9]);
        await File.WriteAllBytesAsync(shadow + "-shm", [10]);

        await using var registry = await NewRegistryAsync();
        await registry.UpsertAsync(NewRow(
            scopeId,
            status: "indexing",
            lastIndexedAt: DateTimeOffset.UtcNow.AddMinutes(-10)));

        await BootReconciler.ReconcileAsync(
            registry,
            _repoRoot,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            NullLogger.Instance,
            CancellationToken.None);

        (await File.ReadAllBytesAsync(primary)).Should().Equal(primaryBytes);
        File.Exists(shadow).Should().BeFalse();
        File.Exists(shadow + "-wal").Should().BeFalse();
        File.Exists(shadow + "-shm").Should().BeFalse();
        var archived = Path.Join(ScopeLayout.OrphansDirectory(_repoRoot), Path.GetFileName(shadow));
        (await File.ReadAllBytesAsync(archived)).Should().Equal(5, 6, 7);
        File.Exists(archived + "-wal").Should().BeTrue();
        File.Exists(archived + "-shm").Should().BeTrue();

        var post = await registry.GetAsync(scopeId);
        post!.Status.Should().Be("degraded");
        post.StatusMessage.Should().Contain("previous index interrupted");
        var events = HealLinesForScope(scopeId).ToList();
        events.Select(line => line.GetProperty("kind").GetString()).Should().Contain([
            "interrupted-shadow-archived",
            "stuck-indexing-detected",
        ]);
    }

    [Fact]
    public async Task InFlightIndexing_isLeftAlone()
    {
        // Registry says indexing AND last_indexed_at is AFTER process start — the current process
        // owns this row; the reconciler must not touch it.
        var inFlightId = $"reconcile-test-inflight-{Guid.NewGuid():N}";
        var dbPath = ScopeLayout.ScopeDbPath(_repoRoot, inFlightId);
        await File.WriteAllBytesAsync(dbPath, new byte[] { 1 });

        await using var registry = await NewRegistryAsync();
        var processStart = DateTimeOffset.UtcNow.AddMinutes(-1);
        await registry.UpsertAsync(NewRow(inFlightId, status: "indexing",
            lastIndexedAt: DateTimeOffset.UtcNow));

        await BootReconciler.ReconcileAsync(registry, _repoRoot, processStart,
            NullLogger.Instance, CancellationToken.None);

        var post = await registry.GetAsync(inFlightId);
        post!.Status.Should().Be("indexing"); // unchanged
        AnyHealLineForScope(inFlightId).Should().BeNull();
    }

    [Fact]
    public async Task HealthyScope_isLeftAlone()
    {
        // Registry row + DB file present + status=ok — reconciler is a no-op.
        var healthyId = $"reconcile-test-healthy-{Guid.NewGuid():N}";
        var dbPath = ScopeLayout.ScopeDbPath(_repoRoot, healthyId);
        await File.WriteAllBytesAsync(dbPath, new byte[] { 1 });

        await using var registry = await NewRegistryAsync();
        await registry.UpsertAsync(NewRow(healthyId, status: "ok"));

        await BootReconciler.ReconcileAsync(registry, _repoRoot, DateTimeOffset.UtcNow,
            NullLogger.Instance, CancellationToken.None);

        var post = await registry.GetAsync(healthyId);
        post!.Status.Should().Be("ok");
        AnyHealLineForScope(healthyId).Should().BeNull();
        File.Exists(dbPath).Should().BeTrue(); // not archived
    }

    private async Task<SqliteScopeRegistry> NewRegistryAsync()
    {
        var registry = new SqliteScopeRegistry(ScopeLayout.MetaDbPath(_repoRoot), NullLogger<SqliteScopeRegistry>.Instance);
        await registry.EnsureSchemaAsync();
        return registry;
    }

    private static ScopeRow NewRow(string id, string status, DateTimeOffset? lastIndexedAt = null) => new(
        Id: id,
        Name: id,
        Root: "/tmp/fake-root",
        ProjectSetJson: """{"Kind":"solutions","Items":[],"Exclude":[]}""",
        Isolated: false,
        LastIndexedAt: lastIndexedAt ?? DateTimeOffset.UtcNow,
        Status: status,
        StatusMessage: null);

    private JsonElement HealLineForScope(string scopeId)
    {
        var found = AnyHealLineForScope(scopeId);
        found.Should().NotBeNull($"expected one heals.jsonl line with scope = `{scopeId}`");
        return found!.Value;
    }

    private JsonElement? AnyHealLineForScope(string scopeId)
    {
        if (!File.Exists(_healPath)) return null;
        foreach (var raw in File.ReadAllLines(_healPath))
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.GetProperty("scope").GetString() == scopeId)
            {
                return doc.RootElement.Clone();
            }
        }
        return null;
    }

    private IEnumerable<JsonElement> HealLinesForScope(string scopeId)
    {
        if (!File.Exists(_healPath)) yield break;
        foreach (var raw in File.ReadAllLines(_healPath))
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.GetProperty("scope").GetString() == scopeId)
                yield return doc.RootElement.Clone();
        }
    }
}
