using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class XamlCodeBehindProjectionTests
{
    [Fact]
    public async Task Generated_field_access_projects_exact_code_behind_element_edge()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-xaml-code-behind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var xamlPath = Path.Combine(root, "Views", "CameraView.xaml");
            Directory.CreateDirectory(Path.GetDirectoryName(xamlPath)!);
            await File.WriteAllTextAsync(
                xamlPath,
                """
                <Window x:Class="Sample.CameraView"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Slider x:Name="valueSlider" />
                </Window>
                """);

            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var projectPath = Path.Combine(root, "Sample.csproj");
            var userPath = Path.Combine(root, "Views", "CameraView.xaml.cs");
            var generatedPath = Path.Combine(
                root,
                "obj",
                "Debug",
                "CameraView.g.cs");
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "Sample",
                    "Sample",
                    LanguageNames.CSharp,
                    filePath: projectPath,
                    parseOptions: new CSharpParseOptions(
                        LanguageVersion.Preview)))
                .AddMetadataReference(
                    projectId,
                    MetadataReference.CreateFromFile(
                        typeof(object).Assembly.Location))
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "CameraView.xaml.cs",
                    SourceText.From(
                        """
                        namespace Sample;
                        public partial class CameraView
                        {
                            public void Refresh()
                            {
                                _ = valueSlider;
                            }
                        }
                        """),
                    filePath: userPath)
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "CameraView.g.cs",
                    SourceText.From(
                        """
                        namespace Sample;
                        public partial class CameraView
                        {
                            internal object valueSlider = new();
                        }
                        """),
                    filePath: generatedPath);
            workspace.TryApplyChanges(solution).Should().BeTrue();
            var roslynProject = workspace.CurrentSolution.GetProject(projectId)!;
            var xamlProject = new XamlLanguageProject(
                projectPath,
                [xamlPath],
                new XamlResourceSnapshot(
                    new Dictionary<string, IReadOnlyList<ResourceDefinition>>(),
                    [],
                    isComplete: true,
                    []),
                resourceSnapshotBuilder: null,
                roslynProjectProvider: () => roslynProject);

            var result = await XamlCodeBehindProjection.BuildAsync(
                xamlProject,
                root,
                default);

            result.IsComplete.Should().BeTrue();
            result.ProducingFilePaths.Should().ContainSingle()
                .Which.Should().Be(userPath);
            var edge = result.Edges.Should().ContainSingle().Subject;
            edge.SourceCanonicalKey.Should().Be(
                "csharp:M:Sample.CameraView.Refresh");
            edge.TargetCanonicalKey.Should().Be(
                "xaml:element:Views/CameraView.xaml#valueSlider");
            edge.Kind.Should().Be("code-behind-uses-element");
            edge.Evidence.Location.FilePath.Should().Be(userPath);
            edge.Evidence.Location.StartLine.Should().Be(6);
            edge.Evidence.Producer.Should().Be(
                XamlCodeBehindProjection.Producer);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the assertion remains the useful signal.
            }
        }
    }
}
