using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class RoslynPrivacyBoundaryTests : IAsyncLifetime
{
    private const string SymbolCanary = "MEDINTEROP_PRIVACY_SYMBOL_CANARY_7F8E61";
    private const string DiagnosticCanary = "MEDINTEROP_PRIVACY_DIAGNOSTIC_CANARY_7F8E61";

    private string _tempDir = string.Empty;
    private string _solutionPath = string.Empty;
    private SqliteGraphStore? _store;
    private RoslynIndexer? _indexer;

    public async Task InitializeAsync()
    {
        _solutionPath = LocateSolution();
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-privacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _store = new SqliteGraphStore(Path.Join(_tempDir, "graph.db"));
        _indexer = new RoslynIndexer(
            _store,
            privacyRoot: Path.GetDirectoryName(_solutionPath)!);
        await _indexer.OpenAsync(_solutionPath);
        await _indexer.IndexAllAsync();
    }

    public async Task DisposeAsync()
    {
        if (_indexer is not null) await _indexer.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ColdIndex_neverPersistsExcludedDocumentCanaries()
    {
        var sampleApp = _indexer!.SanitizedSolution!.Projects
            .Single(project => project.Name == "Sample.App");
        sampleApp.Documents.Select(document => document.FilePath ?? string.Empty)
            .Should().NotContain(path =>
                path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));
        sampleApp.AdditionalDocuments.Select(document => document.FilePath ?? string.Empty)
            .Should().NotContain(path =>
                path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));
        sampleApp.AnalyzerConfigDocuments.Select(document => document.FilePath ?? string.Empty)
            .Should().NotContain(path =>
                path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));

        var compilation = await sampleApp.GetCompilationAsync();
        compilation.Should().NotBeNull();
        compilation!.SyntaxTrees.Should().NotContain(tree =>
            tree.FilePath.Contains("PatientData", StringComparison.OrdinalIgnoreCase));
        compilation.GetDiagnostics().Should().NotContain(diagnostic =>
            diagnostic.GetMessage().Contains(DiagnosticCanary, StringComparison.Ordinal));

        var files = await _store!.GetAllFilesAsync();
        files.Should().NotContain(f =>
            f.Path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));

        var symbols = await _store.FindSymbolsAsync(SymbolCanary);
        symbols.Should().BeEmpty();

        var diagnostics = await _store.FindDiagnosticsAsync(
            severity: null,
            code: null,
            symbolId: null,
            limit: 500);
        diagnostics.Should().NotContain(d =>
            d.Message.Contains(DiagnosticCanary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncrementalIndex_rejectsExcludedPathBeforeDocumentLookup()
    {
        var secretPath = Path.Join(
            Path.GetDirectoryName(_solutionPath)!,
            "Sample.App",
            "PatientData",
            "Secret.cs");

        var result = await _indexer!.IndexChangedFilesAsync(new[] { secretPath });

        result.FilesIndexed.Should().Be(0);
        (await _store!.GetAllFilesAsync()).Should().NotContain(f =>
            string.Equals(f.Path, secretPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reload_rebuildsTheSanitizedSnapshot()
    {
        await _indexer!.ReloadAndIndexAllAsync();

        (await _store!.GetAllFilesAsync()).Should().NotContain(f =>
            f.Path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));
        (await _store.FindSymbolsAsync(SymbolCanary)).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectFactory_exposesOnlySanitizedSnapshot()
    {
        var root = Path.GetDirectoryName(_solutionPath)!;
        var projects = await new MSBuildLanguageProjectFactory(_indexer!)
            .DiscoverAsync(root, CancellationToken.None);

        projects.SelectMany(p => p.FilePaths).Should().NotContain(path =>
            path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScopeExclude_appliesToColdSnapshotAndIncrementalChangeOrDeletePaths()
    {
        var root = Path.GetDirectoryName(_solutionPath)!;
        var dbPath = Path.Join(_tempDir, "scope-exclude.db");
        await using var store = new SqliteGraphStore(dbPath);
        await using var indexer = new RoslynIndexer(
            store,
            logger: null,
            embeddingsSink: null,
            privacyRoot: root,
            excludePatterns: ["**/Sample.App/**"]);

        await indexer.OpenAsync(_solutionPath);
        indexer.SanitizedSolution!.Projects.Should().NotContain(project =>
            project.FilePath != null
            && project.FilePath.Contains("Sample.App", StringComparison.OrdinalIgnoreCase));

        var coldResult = await indexer.IndexAllAsync();
        coldResult.FilesIndexed.Should().BeGreaterThan(0);
        (await store.GetAllFilesAsync()).Should().NotContain(file =>
            file.Path.Contains("Sample.App", StringComparison.OrdinalIgnoreCase));

        var changedPath = Path.Join(root, "Sample.App", "Program.cs");
        var deletedPath = Path.Join(root, "Sample.App", "Deleted.cs");
        File.Exists(changedPath).Should().BeTrue();
        File.Exists(deletedPath).Should().BeFalse();

        var incrementalResult = await indexer.IndexChangedFilesAsync([changedPath, deletedPath]);

        incrementalResult.FilesIndexed.Should().Be(0);
        (await store.GetAllFilesAsync()).Should().NotContain(file =>
            file.Path.Contains("Sample.App", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sanitizer_removesProjectsAndEveryDiskBackedRoslynInputKind()
    {
        var root = Path.GetFullPath(Path.Join(_tempDir, "repo"));
        var allowedProjectPath = Path.Join(root, "src", "Allowed.csproj");
        var excludedProjectPath = Path.Join(root, "PatientData", "Secret.csproj");
        var allowedDocumentPath = Path.Join(root, "src", "Allowed.cs");
        var excludedDocumentPath = Path.Join(root, "PatientData", "Secret.cs");
        var excludedObjDocumentPath = Path.Join(root, "src", "obj", "OrdinaryGenerated.cs");
        var excludedAdditionalPath = Path.Join(root, "PatientData", "Secret.additional.txt");
        var excludedConfigPath = Path.Join(root, "PatientData", "Secret.editorconfig");

        using var workspace = new AdhocWorkspace();
        var allowedProjectId = ProjectId.CreateNewId();
        var excludedProjectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                allowedProjectId,
                VersionStamp.Create(),
                "Allowed",
                "Allowed",
                LanguageNames.CSharp,
                filePath: allowedProjectPath))
            .AddProject(ProjectInfo.Create(
                excludedProjectId,
                VersionStamp.Create(),
                "Secret",
                "Secret",
                LanguageNames.CSharp,
                filePath: excludedProjectPath))
            .AddDocument(
                DocumentId.CreateNewId(allowedProjectId),
                "Allowed.cs",
                SourceText.From("internal sealed class Allowed {}"),
                filePath: allowedDocumentPath)
            .AddDocument(
                DocumentId.CreateNewId(allowedProjectId),
                "Secret.cs",
                SourceText.From($"internal sealed class {SymbolCanary} {{}}"),
                filePath: excludedDocumentPath)
            .AddDocument(
                DocumentId.CreateNewId(allowedProjectId),
                "OrdinaryGenerated.cs",
                SourceText.From("internal sealed class OrdinaryGenerated {}"),
                filePath: excludedObjDocumentPath)
            .AddAdditionalDocument(
                DocumentId.CreateNewId(allowedProjectId),
                "Secret.additional.txt",
                SourceText.From("MEDINTEROP_PRIVACY_ADDITIONAL_CANARY_7F8E61"),
                filePath: excludedAdditionalPath)
            .AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(allowedProjectId),
                "Secret.editorconfig",
                SourceText.From("[*.cs]"),
                filePath: excludedConfigPath);

        var sanitized = SolutionPrivacySanitizer.Sanitize(
            solution,
            new PrivacyPathPolicy(root));

        sanitized.GetProject(excludedProjectId).Should().BeNull();
        var allowedProject = sanitized.GetProject(allowedProjectId);
        allowedProject.Should().NotBeNull();
        allowedProject!.Documents.Should().ContainSingle(d => d.FilePath == allowedDocumentPath);
        allowedProject.AdditionalDocuments.Should().BeEmpty();
        allowedProject.AnalyzerConfigDocuments.Should().BeEmpty();
    }

    [Fact]
    public async Task ScopeSanitizer_keepsSyntaxRestrictedSdkGlobalUsingsForCompilationOnly()
    {
        var root = Path.GetFullPath(Path.Join(_tempDir, "global-usings-repo"));
        var projectDirectory = Path.Join(root, "WebApp");
        var projectPath = Path.Join(projectDirectory, "WebApp.csproj");
        var programPath = Path.Join(projectDirectory, "Program.cs");
        var generatedPath = Path.Join(
            projectDirectory,
            "obj",
            "Debug",
            "net10.0",
            "WebApp.GlobalUsings.g.cs");

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var globalUsingsId = DocumentId.CreateNewId(projectId);
        var raw = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "WebApp",
                "WebApp",
                LanguageNames.CSharp,
                filePath: projectPath))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Program.cs",
                SourceText.From("internal sealed class ProgramAnchor { }"),
                filePath: programPath)
            .AddDocument(
                globalUsingsId,
                "WebApp.GlobalUsings.g.cs",
                SourceText.From(
                    """
                    // <auto-generated/>
                    global using System.Threading;
                    global using Microsoft.AspNetCore.Builder;
                    global using Microsoft.Extensions.Logging;
                    """),
                filePath: generatedPath);

        var sanitized = SolutionPrivacySanitizer.SanitizeForScope(
            raw,
            new ScopePathPolicy(root),
            isBuildGenerated: _ => false);
        var project = sanitized.GetProject(projectId)!;

        project.Documents.Should().Contain(document =>
            document.Id == globalUsingsId,
            "SDK global usings are compilation inputs even when MSBuildWorkspace loses IsGenerated");
        var compilation = await project.GetCompilationAsync();
        compilation.Should().NotBeNull();
        compilation!.SyntaxTrees.Should().Contain(tree =>
            string.Equals(
                tree.FilePath,
                generatedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScopeSanitizer_rejectsGeneratedLookingGlobalUsingsWithDeclarations()
    {
        var root = Path.GetFullPath(Path.Join(_tempDir, "spoofed-global-usings-repo"));
        var projectDirectory = Path.Join(root, "WebApp");
        var projectPath = Path.Join(projectDirectory, "WebApp.csproj");
        var programPath = Path.Join(projectDirectory, "Program.cs");
        var generatedPath = Path.Join(
            projectDirectory,
            "obj",
            "Debug",
            "net10.0",
            "WebApp.GlobalUsings.g.cs");

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var spoofedId = DocumentId.CreateNewId(projectId);
        var raw = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "WebApp",
                "WebApp",
                LanguageNames.CSharp,
                filePath: projectPath))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Program.cs",
                SourceText.From("internal sealed class ProgramAnchor { }"),
                filePath: programPath)
            .AddDocument(
                spoofedId,
                "WebApp.GlobalUsings.g.cs",
                SourceText.From(
                    """
                    global using System.Threading;
                    internal sealed class ExcludedCanary { }
                    """),
                filePath: generatedPath);

        var sanitized = SolutionPrivacySanitizer.SanitizeForScope(
            raw,
            new ScopePathPolicy(root),
            isBuildGenerated: _ => false);

        sanitized.GetProject(projectId)!.Documents.Should().NotContain(document =>
            document.Id == spoofedId,
            "a generated-looking name and obj path cannot admit excluded declarations");
    }

    [SkippableFact]
    public void ScopeSanitizer_removesDocumentReachedThroughOutOfRepositoryDirectoryLink()
    {
        var root = Path.Join(_tempDir, "linked-repo");
        var outside = Path.Join(_tempDir, "linked-outside");
        Directory.CreateDirectory(Path.Join(root, "src"));
        Directory.CreateDirectory(outside);
        var link = Path.Join(root, "src", "External");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, outside),
            "This environment does not permit symbolic-link or junction creation.");

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var allowedPath = Path.Join(root, "src", "Allowed.cs");
        var linkedPath = Path.Join(link, "Secret.cs");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Allowed",
                "Allowed",
                LanguageNames.CSharp,
                filePath: Path.Join(root, "Allowed.csproj")))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Allowed.cs",
                SourceText.From("internal sealed class Allowed {}"),
                filePath: allowedPath)
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Secret.cs",
                SourceText.From($"internal sealed class {SymbolCanary} {{}}"),
                filePath: linkedPath);

        var sanitized = SolutionPrivacySanitizer.SanitizeForScope(
            solution,
            new ScopePathPolicy(root));

        sanitized.GetProject(projectId)!.Documents
            .Should().ContainSingle(document => document.FilePath == allowedPath);
    }

    private static string LocateSolution()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null;
             dir = dir.Parent)
        {
            var candidate = Path.Join(dir.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln.");
    }
}
