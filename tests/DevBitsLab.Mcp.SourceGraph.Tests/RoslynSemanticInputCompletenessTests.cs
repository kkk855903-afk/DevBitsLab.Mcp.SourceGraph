using DevBitsLab.Mcp.SourceGraph.Indexing;
using FluentAssertions;
using Microsoft.CodeAnalysis;
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
}
