using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ScopePathPolicyTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "sourcegraph-scope-path-" + Guid.NewGuid().ToString("N"));

    public ScopePathPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DoubleStarDirectoryPattern_excludesTheDirectoryAndEveryDescendant()
    {
        var policy = new ScopePathPolicy(_root, ["**/generated"]);

        policy.IsExcluded(Path.Join(_root, "generated")).Should().BeTrue();
        policy.IsExcluded(Path.Join(_root, "src", "Generated")).Should().BeTrue();
        policy.IsExcluded(Path.Join(_root, "src", "Generated", "Bridge.cs")).Should().BeTrue();
        policy.IsExcluded(Path.Join(_root, "src", "generated-models", "Bridge.cs")).Should().BeFalse();
    }

    [Fact]
    public void GeneratedDocuments_onlyExemptBuildOutputDirectories()
    {
        var policy = new ScopePathPolicy(_root, ["**/scope-generated/**"]);
        var outside = Path.Join(
            Path.GetDirectoryName(_root)!,
            "outside-" + Guid.NewGuid().ToString("N"),
            "obj",
            "Outside.g.cs");

        policy.IsGeneratedDocumentExcluded(
            Path.Join(_root, "src", "obj", "Debug", "net10.0", "Allowed.g.cs"))
            .Should().BeFalse();
        policy.IsGeneratedDocumentExcluded(
            Path.Join(_root, "src", "bin", "Release", "Allowed.g.cs"))
            .Should().BeFalse();

        policy.IsGeneratedDocumentExcluded(
            Path.Join(_root, "src", "Debug", "NotUnderBuildRoot.g.cs"))
            .Should().BeTrue();
        policy.IsGeneratedDocumentExcluded(
            Path.Join(_root, "src", "obj", "PatientData", "Secret.g.cs"))
            .Should().BeTrue();
        policy.IsGeneratedDocumentExcluded(
            Path.Join(_root, "src", "bin", "iMaGeS", "Preview.g.cs"))
            .Should().BeTrue();
        policy.IsGeneratedDocumentExcluded(
            Path.Join(_root, "src", "obj", "study.DcM"))
            .Should().BeTrue();
        policy.IsGeneratedDocumentExcluded(
            Path.Join(_root, "src", "obj", "scope-generated", "Hidden.g.cs"))
            .Should().BeTrue();
        policy.IsGeneratedDocumentExcluded(outside).Should().BeTrue();
    }

    [Fact]
    public void IncludesAndRegisteredExtensions_cannotBypassMandatoryPrivacyFloor()
    {
        var projectSet = new ScopeProjectSet.Paths(
            Globs: ["**/*", "**/*.DCM", "**/*.PnG"],
            Exclude: ["**/generated/**"]);
        var policy = new ScopePathPolicy(_root, projectSet.Exclude);

        policy.IsExcluded(Path.Join(_root, "pAtIeNtDaTa", "Record.cs")).Should().BeTrue();
        policy.IsExcluded(Path.Join(_root, "src", "study.DcM")).Should().BeTrue();
        policy.IsExcluded(Path.Join(_root, "src", "preview.pNg")).Should().BeTrue();
        policy.IsExcluded(Path.Join(_root, "src", "ordinary.cs")).Should().BeFalse();
    }

    [Fact]
    public void DeletedPath_isMatchedLexically_withoutRequiringTheFileToExist()
    {
        var deletedPath = Path.Join(_root, "src", "generated", "Deleted.xaml");
        File.Exists(deletedPath).Should().BeFalse();

        new ScopePathPolicy(_root, ["**/generated/**"])
            .IsExcluded(deletedPath)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../generated/**")]
    [InlineData("src/../generated/**")]
    [InlineData("/generated/**")]
    [InlineData(@"\generated\**")]
    [InlineData("/")]
    [InlineData(@"C:\absolute\generated\**")]
    public void InvalidExcludePatterns_throwClearConfigurationError(string? pattern)
    {
        var act = () => new ScopePathPolicy(_root, [pattern!]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("excludePatterns")
            .WithMessage("Invalid scope exclude pattern at index 0:*");
    }
}
