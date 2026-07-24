using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class RoslynSemanticInputCompletenessTests
{
    [Fact]
    public void SpoofedGeneratedSourceNameUnderObjMakesProjectIncomplete()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-semantic-spoofed-generated-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            using var workspace = new AdhocWorkspace();
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            var projectId = ProjectId.CreateNewId();
            var spoofedSourceId = DocumentId.CreateNewId(projectId);
            var raw = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "App",
                    "App",
                    LanguageNames.CSharp,
                    filePath: projectPath))
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "App.cs",
                    "internal sealed class AppAnchor { }",
                    filePath: Path.Combine(projectDirectory, "App.cs"))
                .AddDocument(
                    spoofedSourceId,
                    "Private.g.cs",
                    "internal sealed class PrivateMember { }",
                    filePath: Path.Combine(
                        projectDirectory,
                        "obj",
                        "Debug",
                        "net10.0",
                        "Private.g.cs"));

            RoslynIndexer.IsProjectSemanticInputComplete(
                    raw,
                    raw.RemoveDocument(spoofedSourceId),
                    projectPath)
                .Should().BeFalse(
                    "an attacker-controlled .g.cs name and obj path cannot prove provenance");
            RoslynIndexer.IsProjectXamlPositiveResolutionSafe(
                    raw,
                    raw.RemoveDocument(spoofedSourceId),
                    projectPath,
                    new ScopePathPolicy(root))
                .Should().BeFalse(
                    "a path and generated-looking name are not build provenance");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; assertion failures remain the useful signal.
            }
        }
    }

    [Fact]
    public void BuildGeneratedSourceUnderObjOnlyAllowsPositiveXamlResolution()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-semantic-build-generated-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            using var workspace = new AdhocWorkspace();
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            var projectId = ProjectId.CreateNewId();
            var generatedId = DocumentId.CreateNewId(projectId);
            var generatedPath = Path.Combine(
                projectDirectory,
                "obj",
                "Debug",
                "net10.0-windows",
                "MainWindow.g.cs");
            var raw = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "App",
                    "App",
                    LanguageNames.CSharp,
                    filePath: projectPath))
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "App.cs",
                    "internal sealed class AppAnchor { }",
                    filePath: Path.Combine(projectDirectory, "App.cs"))
                .AddDocument(DocumentInfo.Create(
                    generatedId,
                    "MainWindow.g.cs",
                    sourceCodeKind: SourceCodeKind.Regular,
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(
                            "internal partial class MainWindow { }"),
                        VersionStamp.Create(),
                        generatedPath)),
                    filePath: generatedPath,
                    isGenerated: true));
            var sanitized = raw.RemoveDocument(generatedId);

            RoslynIndexer.IsProjectSemanticInputComplete(
                    raw,
                    sanitized,
                    projectPath)
                .Should().BeFalse(
                    "a build side effect is still absent from the authoritative compilation");
            RoslynIndexer.IsProjectXamlPositiveResolutionSafe(
                    raw,
                    sanitized,
                    projectPath,
                    new ScopePathPolicy(root))
                .Should().BeTrue(
                    "Roslyn build provenance permits only direct positive XAML facts");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; assertion failures remain the useful signal.
            }
        }
    }

    [Fact]
    public void SpoofedSdkConfigPathMakesProjectIncomplete()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-semantic-spoofed-sdk-config-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            using var workspace = new AdhocWorkspace();
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            var projectId = ProjectId.CreateNewId();
            var spoofedConfigId = DocumentId.CreateNewId(projectId);
            var raw = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "App",
                    "App",
                    LanguageNames.CSharp,
                    filePath: projectPath))
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "App.cs",
                    "internal sealed class AppAnchor { }",
                    filePath: Path.Combine(projectDirectory, "App.cs"))
                .AddAnalyzerConfigDocument(
                    spoofedConfigId,
                    "analysislevel_10_default.globalconfig",
                    SourceText.From("is_global = true"),
                    filePath: Path.Combine(
                        root,
                        "fake-sdk",
                        "Sdks",
                        "Microsoft.NET.Sdk",
                        "analyzers",
                        "build",
                        "config",
                        "analysislevel_10_default.globalconfig"));

            RoslynIndexer.IsProjectSemanticInputComplete(
                    raw,
                    raw.RemoveAnalyzerConfigDocument(spoofedConfigId),
                    projectPath)
                .Should().BeFalse(
                    "an attacker-controlled SDK-looking path cannot prove provenance");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; assertion failures remain the useful signal.
            }
        }
    }

    [Fact]
    public void OrdinarySourceUnderObjStillMakesProjectIncomplete()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-semantic-obj-source-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            using var workspace = new AdhocWorkspace();
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            var projectId = ProjectId.CreateNewId();
            var excludedId = DocumentId.CreateNewId(projectId);
            var raw = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "App",
                    "App",
                    LanguageNames.CSharp,
                    filePath: projectPath))
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "App.cs",
                    "internal sealed class AppAnchor { }",
                    filePath: Path.Combine(projectDirectory, "App.cs"))
                .AddDocument(
                    excludedId,
                    "OrdinaryGenerated.cs",
                    "internal sealed class PrivateMember { }",
                    filePath: Path.Combine(
                        projectDirectory,
                        "obj",
                        "Debug",
                        "net10.0",
                        "OrdinaryGenerated.cs"));

            RoslynIndexer.IsProjectSemanticInputComplete(
                    raw,
                    raw.RemoveDocument(excludedId),
                    projectPath)
                .Should().BeFalse(
                    "a build-output directory alone cannot bless an ordinary source document");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; assertion failures remain the useful signal.
            }
        }
    }

    [Fact]
    public void ExcludedPartialDocumentInReferencedProjectMakesConsumerIncomplete()
    {
        using var workspace = new AdhocWorkspace();
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-semantic-completeness-" + Guid.NewGuid().ToString("N"));
        var appPath = Path.Combine(root, "App", "App.csproj");
        var viewModelsPath = Path.Combine(root, "ViewModels", "ViewModels.csproj");
        var appId = ProjectId.CreateNewId();
        var viewModelsId = ProjectId.CreateNewId();
        var excludedMemberId = DocumentId.CreateNewId(viewModelsId);
        var raw = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                appId,
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp,
                filePath: appPath))
            .AddProject(ProjectInfo.Create(
                viewModelsId,
                VersionStamp.Create(),
                "ViewModels",
                "ViewModels",
                LanguageNames.CSharp,
                filePath: viewModelsPath))
            .AddProjectReference(appId, new ProjectReference(viewModelsId))
            .AddDocument(
                DocumentId.CreateNewId(appId),
                "App.cs",
                "internal sealed class AppAnchor { }",
                filePath: Path.Combine(root, "App", "App.cs"))
            .AddDocument(
                DocumentId.CreateNewId(viewModelsId),
                "PatientViewModel.cs",
                "public sealed partial class PatientViewModel { }",
                filePath: Path.Combine(
                    root,
                    "ViewModels",
                    "PatientViewModel.cs"))
            .AddDocument(
                excludedMemberId,
                "PatientViewModel.Private.cs",
                "public sealed partial class PatientViewModel { public string Name => \"\"; }",
                filePath: Path.Combine(
                    root,
                    "ViewModels",
                    "Private",
                    "PatientViewModel.Private.cs"));
        var sanitized = raw.RemoveDocument(excludedMemberId);

        RoslynIndexer.IsProjectSemanticInputComplete(raw, raw, appPath)
            .Should().BeTrue();
        RoslynIndexer.IsProjectSemanticInputComplete(raw, sanitized, appPath)
            .Should().BeFalse(
                "binding members can live in privacy-filtered partial documents of project references");
    }

    [Fact]
    public void AnalyzerReferenceLoadStateIsRequiredAcrossProjectReferenceClosure()
    {
        using var workspace = new AdhocWorkspace();
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-semantic-analyzer-state-" + Guid.NewGuid().ToString("N"));
        var appPath = Path.Combine(root, "App", "App.csproj");
        var dependencyPath = Path.Combine(
            root,
            "Dependency",
            "Dependency.csproj");
        var appId = ProjectId.CreateNewId();
        var dependencyId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                appId,
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp,
                filePath: appPath))
            .AddProject(ProjectInfo.Create(
                dependencyId,
                VersionStamp.Create(),
                "Dependency",
                "Dependency",
                LanguageNames.CSharp,
                filePath: dependencyPath))
            .AddProjectReference(appId, new ProjectReference(dependencyId));

        RoslynIndexer.IsProjectSemanticInputComplete(
                solution,
                solution,
                appPath,
                new Dictionary<ProjectId, bool>
                {
                    [appId] = true,
                    [dependencyId] = true,
                })
            .Should().BeTrue();
        RoslynIndexer.IsProjectSemanticInputComplete(
                solution,
                solution,
                appPath,
                new Dictionary<ProjectId, bool>
                {
                    [appId] = true,
                    [dependencyId] = false,
                })
            .Should().BeFalse(
                "a referenced generator can change the consumer's semantic universe");
        RoslynIndexer.IsProjectSemanticInputComplete(
                solution,
                solution,
                appPath,
                new Dictionary<ProjectId, bool>
                {
                    [appId] = true,
                })
            .Should().BeFalse(
                "missing first-probe evidence must fail closed");
    }

    [Fact]
    public async Task FirstCompilationProbeCapturesAnalyzerLoadFailure()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-semantic-analyzer-probe-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var analyzerPath = Path.Combine(root, "BrokenGenerator.dll");
            CompileThrowingConstructorGenerator(analyzerPath);
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <GenerateMSBuildEditorConfigFile>false</GenerateMSBuildEditorConfigFile>
                    <EnableNETAnalyzers>false</EnableNETAnalyzers>
                    <AnalysisLevel>none</AnalysisLevel>
                  </PropertyGroup>
                  <ItemGroup>
                    <Analyzer Include="..\BrokenGenerator.dll" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "Program.cs"),
                "internal static class Program { public static int Value => 1; }");
            var solutionPath = Path.Combine(root, "Fixture.sln");
            await File.WriteAllTextAsync(
                solutionPath,
                """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{A1E9DA2D-5AB5-4B5F-B424-469C3C1B6B80}"
                EndProject
                Global
                    GlobalSection(SolutionConfigurationPlatforms) = preSolution
                        Debug|Any CPU = Debug|Any CPU
                        Release|Any CPU = Release|Any CPU
                    EndGlobalSection
                    GlobalSection(ProjectConfigurationPlatforms) = postSolution
                        {A1E9DA2D-5AB5-4B5F-B424-469C3C1B6B80}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                        {A1E9DA2D-5AB5-4B5F-B424-469C3C1B6B80}.Debug|Any CPU.Build.0 = Debug|Any CPU
                        {A1E9DA2D-5AB5-4B5F-B424-469C3C1B6B80}.Release|Any CPU.ActiveCfg = Release|Any CPU
                        {A1E9DA2D-5AB5-4B5F-B424-469C3C1B6B80}.Release|Any CPU.Build.0 = Release|Any CPU
                    EndGlobalSection
                EndGlobal
                """);

            await using var store = new SqliteGraphStore(
                Path.Combine(root, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                privacyRoot: root);
            await indexer.OpenAsync(solutionPath);

            RoslynIndexer.IsProjectSemanticInputComplete(
                    indexer.Workspace!.CurrentSolution,
                    indexer.SanitizedSolution!,
                    projectPath)
                .Should().BeTrue(
                    "the test isolates analyzer-load evidence from privacy filtering");

            var result = await indexer.IndexAllAsync();

            result.FailedProjects.Should().BeEmpty();
            result.FailedFiles.Should().BeEmpty();
            RoslynIndexer.IsProjectSemanticInputComplete(
                    indexer.Workspace.CurrentSolution,
                    indexer.SanitizedSolution!,
                    projectPath)
                .Should().BeTrue(
                    "the raw and sanitized inputs still match after indexing");
            indexer.IsProjectSemanticInputComplete(projectPath)
                .Should().BeFalse(
                    "the first Roslyn probe observed the one-shot analyzer load failure before it was cached");

            var secondResult = await indexer.IndexAllAsync();

            secondResult.FailedProjects.Should().BeEmpty();
            secondResult.FailedFiles.Should().BeEmpty();
            indexer.IsProjectSemanticInputComplete(projectPath)
                .Should().BeFalse(
                    "negative first-probe evidence must remain sticky while Roslyn reuses the same cached AnalyzerFileReference");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; assertion failures remain the useful signal.
            }
        }
    }

    private static void CompileThrowingConstructorGenerator(string assemblyPath)
    {
        var codeAnalysisPath = typeof(ISourceGenerator).Assembly.Location;
        var references = PlatformReferences()
            .Where(reference => !string.Equals(
                reference.Display,
                codeAnalysisPath,
                StringComparison.OrdinalIgnoreCase))
            .Append(MetadataReference.CreateFromFile(codeAnalysisPath));
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(assemblyPath),
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    using Microsoft.CodeAnalysis;

                    [Generator]
                    public sealed class BrokenGenerator : ISourceGenerator
                    {
                        public BrokenGenerator() =>
                            throw new System.InvalidOperationException(
                                "Intentional analyzer load failure.");

                        public void Initialize(GeneratorInitializationContext context)
                        {
                        }

                        public void Execute(GeneratorExecutionContext context)
                        {
                        }
                    }
                    """),
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(assemblyPath);
        result.Success.Should().BeTrue(
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
         ?? throw new InvalidOperationException(
             "Trusted platform assemblies are unavailable."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();
}
