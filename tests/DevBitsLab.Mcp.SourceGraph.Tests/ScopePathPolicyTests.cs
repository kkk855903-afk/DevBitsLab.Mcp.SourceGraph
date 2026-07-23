using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ScopePathPolicyTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "sourcegraph-scope-path-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _externalDirectories = [];

    public ScopePathPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        foreach (var path in _externalDirectories)
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
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

    [SkippableFact]
    public void ExistingDirectoryLink_outsideRepository_failsClosed()
    {
        var outside = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-scope-outside-" + Guid.NewGuid().ToString("N"));
        _externalDirectories.Add(outside);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "Secret.cs"), "OUTSIDE-CANARY");

        var link = Path.Join(_root, "src", "External");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, outside),
            "This environment does not permit symbolic-link creation.");

        var policy = new ScopePathPolicy(_root);

        policy.IsExcluded(link).Should().BeTrue();
        policy.IsExcluded(Path.Join(link, "Secret.cs")).Should().BeTrue();
    }

    [SkippableFact]
    public void ExistingDirectoryLinks_applyPrivacyAndScopeExcludesToPhysicalTargets()
    {
        var patientTarget = Path.Join(_root, "PatientData");
        var generatedTarget = Path.Join(_root, "src", "Generated");
        Directory.CreateDirectory(patientTarget);
        Directory.CreateDirectory(generatedTarget);
        File.WriteAllText(Path.Join(patientTarget, "Secret.cs"), "PATIENT-CANARY");
        File.WriteAllText(Path.Join(generatedTarget, "Hidden.cs"), "SCOPE-CANARY");

        var patientLink = Path.Join(_root, "src", "PatientAlias");
        var generatedLink = Path.Join(_root, "src", "GeneratedAlias");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(patientLink, patientTarget)
            && PhysicalPathTestSupport.TryCreateDirectoryLink(generatedLink, generatedTarget),
            "This environment does not permit symbolic-link creation.");

        var policy = new ScopePathPolicy(_root, ["**/generated/**"]);

        policy.IsExcluded(Path.Join(patientLink, "Secret.cs")).Should().BeTrue();
        policy.IsExcluded(Path.Join(generatedLink, "Hidden.cs")).Should().BeTrue();
    }

    [SkippableFact]
    public void ExistingDirectoryLink_toAllowedRepositoryTarget_remainsAllowed()
    {
        var target = Path.Join(_root, "src", "Shared");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Join(target, "Allowed.cs"), "class Allowed {}");

        var link = Path.Join(_root, "src", "SharedAlias");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, target),
            "This environment does not permit symbolic-link creation.");

        new ScopePathPolicy(_root)
            .IsExcluded(Path.Join(link, "Allowed.cs"))
            .Should().BeFalse();
    }

    [SkippableFact]
    public void DanglingDirectoryLink_failsClosed()
    {
        var target = Path.Join(_root, "src", "TemporaryTarget");
        Directory.CreateDirectory(target);
        var link = Path.Join(_root, "src", "DanglingAlias");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, target),
            "This environment does not permit symbolic-link or junction creation.");
        Directory.Delete(target, recursive: true);

        new ScopePathPolicy(_root)
            .IsExcluded(Path.Join(link, "Future.cs"))
            .Should().BeTrue();
    }

    [SkippableFact]
    public void GeneratedDocumentBuildOutputLink_cannotEscapeRepository()
    {
        var outside = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-generated-outside-" + Guid.NewGuid().ToString("N"));
        _externalDirectories.Add(outside);
        Directory.CreateDirectory(outside);
        var objLink = Path.Join(_root, "src", "obj");
        Directory.CreateDirectory(Path.GetDirectoryName(objLink)!);
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(objLink, outside),
            "This environment does not permit symbolic-link or junction creation.");

        new ScopePathPolicy(_root)
            .IsGeneratedDocumentExcluded(Path.Join(objLink, "Debug", "Synthetic.g.cs"))
            .Should().BeTrue();
    }

    [Fact]
    public void MissingGeneratedDocument_underOrdinaryBuildOutput_remainsAllowed()
    {
        var generatedPath = Path.Join(
            _root,
            "src",
            "obj",
            "Debug",
            "net10.0",
            "Synthetic.g.cs");
        File.Exists(generatedPath).Should().BeFalse();

        new ScopePathPolicy(_root)
            .IsGeneratedDocumentExcluded(generatedPath)
            .Should().BeFalse();
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
