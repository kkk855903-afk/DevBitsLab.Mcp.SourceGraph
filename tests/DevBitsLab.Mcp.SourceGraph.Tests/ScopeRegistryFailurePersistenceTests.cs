using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Verifies that <see cref="SqliteScopeRegistry"/> persists <c>failed_projects_json</c> and
/// <c>failed_files_json</c> as part of the scope row and round-trips them back via
/// <c>GetAsync</c> / <c>ListAsync</c>. Operators querying <c>list_scopes</c> after a server
/// restart must still see the last-known failure detail without waiting for a re-index.
/// </summary>
public sealed class ScopeRegistryFailurePersistenceTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteScopeRegistry? _registry;

    public async Task InitializeAsync()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "registry-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "_meta.db");
        _registry = new SqliteScopeRegistry(_dbPath);
        await _registry.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_registry is not null) await _registry.DisposeAsync();
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Round_trip_preserves_failed_projects_and_failed_files()
    {
        var failedProjects = new[]
        {
            new ProjectFailure("Legacy.WebForms", "compilation null"),
            new ProjectFailure("Vendor.Generated", "could not find SDK"),
        };
        var failedFiles = new[]
        {
            new FileFailure("/repo/Quirky.cs", "Pass 1 walk failed: NullReferenceException"),
        };
        var input = new ScopeRow(
            Id: "backend",
            Name: "Backend",
            Root: "/repo",
            ProjectSetJson: "{\"kind\":\"solutions\",\"items\":[\"backend.sln\"]}",
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "partial",
            StatusMessage: "2 project(s), 1 file(s) failed.",
            FailedProjects: failedProjects,
            FailedFiles: failedFiles);

        await _registry!.UpsertAsync(input);
        var fetched = await _registry.GetAsync("backend");

        fetched.Should().NotBeNull();
        fetched!.Status.Should().Be("partial");
        fetched.FailedProjects.Should().BeEquivalentTo(failedProjects,
            "the JSON column round-trips ProjectFailure records identically");
        fetched.FailedFiles.Should().BeEquivalentTo(failedFiles,
            "the JSON column round-trips FileFailure records identically");
    }

    [Fact]
    public async Task Empty_failure_lists_round_trip_as_empty()
    {
        var input = new ScopeRow(
            Id: "frontend",
            Name: "Frontend",
            Root: "/repo",
            ProjectSetJson: "{\"kind\":\"solutions\",\"items\":[\"frontend.sln\"]}",
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "ok",
            StatusMessage: null,
            FailedProjects: Array.Empty<ProjectFailure>(),
            FailedFiles: Array.Empty<FileFailure>());

        await _registry!.UpsertAsync(input);
        var fetched = await _registry.GetAsync("frontend");

        fetched.Should().NotBeNull();
        fetched!.FailedProjects.Should().BeEmpty();
        fetched.FailedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_failure_lists_normalise_to_empty_on_round_trip()
    {
        // Older callers that don't pass the new fields end up with null values; the registry
        // serialises null/empty identically as `[]` so reads always come back as empty
        // collections rather than null. Keeps every consumer's foreach safe.
        var input = new ScopeRow(
            Id: "vendor",
            Name: "Vendor",
            Root: "/repo",
            ProjectSetJson: "{\"kind\":\"paths\",\"globs\":[\"third_party/**/*.csproj\"]}",
            Isolated: true,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "ok",
            StatusMessage: null,
            FailedProjects: null,
            FailedFiles: null);

        await _registry!.UpsertAsync(input);
        var fetched = await _registry.GetAsync("vendor");

        fetched.Should().NotBeNull();
        fetched!.FailedProjects.Should().NotBeNull().And.BeEmpty();
        fetched.FailedFiles.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ListAsync_returns_failure_lists_for_every_row()
    {
        var partialRow = new ScopeRow(
            Id: "backend",
            Name: "Backend",
            Root: "/repo",
            ProjectSetJson: "{\"kind\":\"solutions\",\"items\":[\"backend.sln\"]}",
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "partial",
            StatusMessage: "1 project failed.",
            FailedProjects: new[] { new ProjectFailure("Bad", "compilation null") },
            FailedFiles: Array.Empty<FileFailure>());

        var okRow = new ScopeRow(
            Id: "frontend",
            Name: "Frontend",
            Root: "/repo",
            ProjectSetJson: "{\"kind\":\"solutions\",\"items\":[\"frontend.sln\"]}",
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "ok",
            StatusMessage: null,
            FailedProjects: Array.Empty<ProjectFailure>(),
            FailedFiles: Array.Empty<FileFailure>());

        await _registry!.UpsertAsync(partialRow);
        await _registry.UpsertAsync(okRow);

        var rows = await _registry.ListAsync();
        rows.Should().HaveCount(2);

        var backend = rows.Single(r => r.Id == "backend");
        backend.FailedProjects.Should().ContainSingle().Which.Name.Should().Be("Bad");

        var frontend = rows.Single(r => r.Id == "frontend");
        frontend.FailedProjects.Should().BeEmpty();
    }
}
