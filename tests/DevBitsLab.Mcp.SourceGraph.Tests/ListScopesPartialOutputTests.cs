using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Verifies <c>list_scopes</c> rendering and structured output for partial-status scopes.
/// Synthesises a <see cref="ScopeHost"/> with non-empty <c>FailedProjects</c> /
/// <c>FailedFiles</c> and asserts that:
/// <list type="bullet">
///   <item>The status cell renders the partial status + message inline.</item>
///   <item>A "Failed projects / files (last cold index)" sub-list is emitted with the failure detail.</item>
///   <item>The structured output's <c>failed_projects</c> / <c>failed_files</c> arrays carry
///         the same data so MCP clients consuming <c>structuredContent</c> see it without
///         parsing prose.</item>
///   <item>Healthy scopes' rendering stays unchanged — no failure sub-list, empty arrays.</item>
/// </list>
/// </summary>
public sealed class ListScopesPartialOutputTests : IAsyncLifetime
{
    private string _tmpDir = string.Empty;
    private SqliteGraphStore? _store;
    private RoslynIndexer? _indexer;

    public async Task InitializeAsync()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "list-scopes-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _store = new SqliteGraphStore(Path.Combine(_tmpDir, "graph.db"));
        await _store.EnsureSchemaAsync();
        _indexer = new RoslynIndexer(_store);
    }

    public async Task DisposeAsync()
    {
        if (_indexer is not null) await _indexer.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Build a minimally-populated <see cref="ScopeHost"/> without running an index. The host's
    /// store/indexer are real instances so the disposal chain works; FailedProjects /
    /// FailedFiles / Status are set directly via the public setters because we're testing the
    /// rendering, not the indexing pipeline that produces them.
    /// </summary>
    private ScopeHost BuildHost(
        string id,
        string status,
        string? statusMessage,
        IReadOnlyList<ProjectFailure>? failedProjects = null,
        IReadOnlyList<FileFailure>? failedFiles = null)
    {
        var scope = new Scope(
            Id: id,
            Name: id,
            Root: _tmpDir,
            ProjectSet: new ScopeProjectSet.Solutions(
                Items: new[] { "synthetic.sln" },
                Exclude: Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            _store!,
            new DisabledEmbeddingsStore(dimension: 0),
            _indexer!,
            solutionPath: Path.Combine(_tmpDir, "synthetic.sln"))
        {
            Status = status,
            StatusMessage = statusMessage,
            FailedProjects = failedProjects ?? Array.Empty<ProjectFailure>(),
            FailedFiles = failedFiles ?? Array.Empty<FileFailure>(),
            LastIndexedAt = DateTimeOffset.UtcNow,
        };
        host.MarkReady();
        return host;
    }

    private static async Task<(string Prose, ListScopesResult Structured)> RenderViaTool(ScopeRouter router)
    {
        var result = await ScopeTools.ListScopesAsync(router).ConfigureAwait(false);

        // The result has two content blocks in order: (1) the markdown table+failure detail,
        // (2) the audience-restricted metadata block (latency/scopes count). Grab the first
        // non-audience text block to assert against the user-visible prose.
        var prose = "";
        if (result.Content is { } blocks)
        {
            foreach (var block in blocks)
            {
                if (block is TextContentBlock t)
                {
                    var isAudienceMeta = t.Annotations?.Audience is { Count: > 0 };
                    if (!isAudienceMeta)
                    {
                        prose = t.Text;
                        break;
                    }
                }
            }
        }
        // The tool's structured content is serialised by ToolOutputJsonContext with a snake_case
        // property naming policy. Mirror that here so deserialization picks up the right names.
        var deserOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        var structured = result.StructuredContent.HasValue
            ? JsonSerializer.Deserialize<ListScopesResult>(result.StructuredContent.Value.GetRawText(), deserOpts)!
            : throw new InvalidOperationException("list_scopes did not emit structuredContent");
        return (prose, structured);
    }

    [Fact]
    public async Task Partial_scope_renders_failed_projects_and_files_in_prose_and_structured()
    {
        var router = new ScopeRouter();
        var failedProjects = new[]
        {
            new ProjectFailure("Legacy.WebForms", "compilation null"),
        };
        var failedFiles = new[]
        {
            new FileFailure("/repo/Quirky.cs", "Pass 1 walk failed: NullReferenceException at GetSemanticModelAsync"),
        };
        router.Register(BuildHost(
            id: "backend",
            status: "partial",
            statusMessage: "1 project(s), 1 file(s) failed to index.",
            failedProjects: failedProjects,
            failedFiles: failedFiles));

        var (prose, structured) = await RenderViaTool(router);

        // Markdown: status cell shows partial(message); failure sub-list carries each entry.
        prose.Should().Contain("partial (1 project(s), 1 file(s) failed to index.)",
            "the status cell must surface the status + message inline so operators reading the table see why");
        prose.Should().Contain("**Failed projects / files (last cold index):**",
            "non-empty failure lists trigger the sub-list section");
        prose.Should().Contain("`Legacy.WebForms`", "failed project name must be in the prose");
        prose.Should().Contain("compilation null", "failed project reason must be in the prose");
        prose.Should().Contain("`/repo/Quirky.cs`", "failed file path must be in the prose");
        prose.Should().Contain("Pass 1 walk failed", "failed file reason must be in the prose");

        // StructuredContent: typed shape carries the same data for programmatic consumers.
        structured.Scopes.Should().ContainSingle();
        var row = structured.Scopes[0];
        row.Status.Should().Be("partial");
        row.StatusMessage.Should().Be("1 project(s), 1 file(s) failed to index.");
        row.FailedProjects.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ListScopesProjectFailure("Legacy.WebForms", "compilation null"));
        row.FailedFiles.Should().ContainSingle()
            .Which.Path.Should().Be("/repo/Quirky.cs");
    }

    [Fact]
    public async Task Healthy_scope_renders_without_failure_sublist_and_with_empty_arrays()
    {
        var router = new ScopeRouter();
        router.Register(BuildHost(id: "frontend", status: "ok", statusMessage: null));

        var (prose, structured) = await RenderViaTool(router);

        // Healthy scopes don't trigger the failure sub-list — keeps the prose clean.
        prose.Should().NotContain("**Failed projects / files (last cold index):**",
            "healthy scopes must not emit the failure sub-list section");
        prose.Should().NotContain("partial",
            "healthy scopes must not show partial in the status cell");

        // Structured output is consistent: empty arrays, not omitted, so consumers can probe
        // `failed_projects.length === 0` rather than handling missing properties.
        structured.Scopes.Should().ContainSingle()
            .Which.Status.Should().Be("ok");
        structured.Scopes[0].FailedProjects.Should().BeEmpty();
        structured.Scopes[0].FailedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Project_count_uses_projects_loaded_from_solution_instead_of_solution_file_count()
    {
        var firstProjectDirectory = Path.Combine(_tmpDir, "First");
        var secondProjectDirectory = Path.Combine(_tmpDir, "Second");
        Directory.CreateDirectory(firstProjectDirectory);
        Directory.CreateDirectory(secondProjectDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(firstProjectDirectory, "First.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(
            Path.Combine(secondProjectDirectory, "Second.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(
            Path.Combine(firstProjectDirectory, "First.cs"),
            "namespace First; public sealed class One;");
        await File.WriteAllTextAsync(
            Path.Combine(secondProjectDirectory, "Second.cs"),
            "namespace Second; public sealed class Two;");
        var solutionPath = Path.Combine(_tmpDir, "Synthetic.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="First/First.csproj" />
              <Project Path="Second/Second.csproj" />
            </Solution>
            """);

        await _indexer!.OpenAsync(solutionPath);
        var router = new ScopeRouter();
        router.Register(BuildHost(id: "multi-project", status: "ok", statusMessage: null));

        var (prose, structured) = await RenderViaTool(router);

        prose.Should().Contain("| 2 |");
        structured.Scopes.Should().ContainSingle()
            .Which.ProjectCount.Should().Be(2);
    }

    [Fact]
    public async Task Failure_sublist_sanitises_backticks_and_newlines_in_names_and_reasons()
    {
        // Failure detail lives inside a markdown bullet list with inline-code spans wrapping
        // names/paths. A backtick inside a name would terminate the span; a newline inside a
        // reason would split the bullet. The MarkdownTable.EscapeInlineCode / CollapseLines
        // helpers guard against both.
        var router = new ScopeRouter();
        router.Register(BuildHost(
            id: "backend",
            status: "partial",
            statusMessage: "1 project, 1 file failed.",
            failedProjects: new[]
            {
                new ProjectFailure("Has`Backtick.Project", "compilation null"),
            },
            failedFiles: new[]
            {
                new FileFailure(
                    "/repo/Quirky.cs",
                    "Pass 1 walk failed:\nNullReferenceException at line 42\nat call site"),
            }));

        var (prose, _) = await RenderViaTool(router);

        // Backtick in the project name is replaced with the modifier-letter apostrophe so the
        // surrounding inline-code span stays intact.
        prose.Should().Contain("`Hasʼbacktick`".Replace("backtick", "Backtick.Project"),
            "the backtick in the project name must be neutralised so the inline-code wrapper isn't terminated mid-cell");
        prose.Should().NotContain("Has`Backtick.Project",
            "the raw backtick must not survive into the rendered prose");

        // Newlines in the reason are collapsed to spaces so the bullet stays on one line and
        // doesn't accidentally start a new list item from the second half of the message.
        var reasonStart = prose.IndexOf("Pass 1 walk failed", StringComparison.Ordinal);
        reasonStart.Should().BeGreaterThan(-1);
        var reasonEnd = prose.IndexOf("\n", reasonStart, StringComparison.Ordinal);
        var reasonLine = reasonEnd > reasonStart ? prose.Substring(reasonStart, reasonEnd - reasonStart) : prose.Substring(reasonStart);
        reasonLine.Should().Contain("NullReferenceException at line 42",
            "the second line of the multi-line exception message must be folded into the same bullet, not split off");
        reasonLine.Should().Contain("at call site",
            "the third line of the message must also be folded onto the same bullet");
    }

    [Fact]
    public async Task Mixed_scope_set_only_emits_failure_sublist_for_partial_scopes()
    {
        var router = new ScopeRouter();
        router.Register(BuildHost(id: "frontend", status: "ok", statusMessage: null));
        router.Register(BuildHost(
            id: "backend",
            status: "partial",
            statusMessage: "1 project failed.",
            failedProjects: new[] { new ProjectFailure("Bad.Project", "compilation null") }));

        var (prose, structured) = await RenderViaTool(router);

        // Only the partial scope shows a failure entry.
        prose.Should().Contain("**Failed projects / files (last cold index):**");
        prose.Should().Contain("`Bad.Project`");
        // The healthy scope's row in the table doesn't get a failure-detail section.
        var failureSectionStart = prose.IndexOf("**Failed projects / files (last cold index):**", StringComparison.Ordinal);
        prose.Substring(failureSectionStart).Should().NotContain("- `frontend`:",
            "the healthy scope must not appear under the failure sub-list");

        structured.Scopes.Should().HaveCount(2);
    }
}
