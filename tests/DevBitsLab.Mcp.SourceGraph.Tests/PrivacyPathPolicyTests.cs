using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class PrivacyPathPolicyTests
{
    private static readonly string _repoRoot =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "medinterop-privacy-root"));

    public static TheoryData<string> ExcludedDirectoryNames => new()
    {
        "bin",
        "obj",
        ".vs",
        "Debug",
        "Release",
        "Images",
        "PatientData",
        "Database",
        "Logs",
        ".git",
        ".sourcegraph",
        "node_modules",
    };

    public static TheoryData<string> ExcludedFileNames => new()
    {
        "study.dcm",
        "portrait.jpg",
        "portrait.jpeg",
        "screenshot.png",
    };

    [Theory]
    [MemberData(nameof(ExcludedDirectoryNames))]
    public void IsExcluded_rejectsExactDirectorySegments(string directoryName)
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("src", directoryName, "secret.cs")).Should().BeTrue();
        policy.IsExcluded(directoryName).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_matchesDirectorySegmentsCaseInsensitively()
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("src", "pAtIeNtDaTa", "secret.cs")).Should().BeTrue();
        policy.IsExcluded(Path.Combine("src", "iMaGeS", "scan.cs")).Should().BeTrue();
        policy.IsExcluded(Path.Combine("src", ".SoUrCeGrApH", "graph.db")).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(ExcludedFileNames))]
    public void IsExcluded_rejectsMedicalAndImageExtensions(string fileName)
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("src", fileName)).Should().BeTrue();
        policy.IsExcluded(Path.Combine("src", fileName.ToUpperInvariant())).Should().BeTrue();
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("objects")]
    [InlineData(".vscode")]
    [InlineData("Debugger")]
    [InlineData("ReleaseNotes")]
    [InlineData("ImagesBackup")]
    [InlineData("PatientDataModels")]
    [InlineData("Databases")]
    [InlineData("LogsArchive")]
    [InlineData(".github")]
    [InlineData(".sourcegraph-cache")]
    [InlineData("node_modules_cache")]
    public void IsExcluded_doesNotMatchDirectoryNamePrefixes(string directoryName)
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("src", directoryName, "ordinary.cs")).Should().BeFalse();
    }

    [Theory]
    [InlineData("study.dcm.bak")]
    [InlineData("portrait.jpgg")]
    [InlineData("portrait.jpeg.cs")]
    [InlineData("screenshot.png.txt")]
    public void IsExcluded_doesNotMatchExtensionPrefixesOrMiddleSuffixes(string fileName)
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("src", fileName)).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_acceptsOrdinarySourceInsideRepository()
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("src", "Interop", "NativeBridge.cs")).Should().BeFalse();
        policy.IsExcluded(Path.Combine(_repoRoot, "src", "Interop", "NativeBridge.cs")).Should().BeFalse();
        policy.IsExcluded(_repoRoot).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_resolvesRelativePathsAgainstRepositoryRoot()
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("src", "..", "PatientData", "record.cs")).Should().BeTrue();
        policy.IsExcluded(Path.Combine("src", "..", "src", "record.cs")).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_rejectsRelativePathsThatEscapeRepository()
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(Path.Combine("..", "outside", "ordinary.cs")).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_rejectsAbsolutePathsOutsideRepository()
    {
        var policy = new PrivacyPathPolicy(_repoRoot);
        var parent = Path.GetDirectoryName(_repoRoot)!;

        policy.IsExcluded(Path.Combine(parent, "another-repository", "ordinary.cs")).Should().BeTrue();
        policy.IsExcluded(_repoRoot + "-backup" + Path.DirectorySeparatorChar + "ordinary.cs")
            .Should().BeTrue("repository-name prefixes are not path-segment containment");
    }

    [Fact]
    public void IsExcluded_usesWindowsStyleCaseInsensitiveRootContainment()
    {
        var policy = new PrivacyPathPolicy(_repoRoot);
        var differentlyCasedPath =
            Path.Combine(_repoRoot.ToUpperInvariant(), "src", "ordinary.cs");

        policy.IsExcluded(differentlyCasedPath).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsExcluded_failsClosedForMissingPaths(string? path)
    {
        var policy = new PrivacyPathPolicy(_repoRoot);

        policy.IsExcluded(path).Should().BeTrue();
    }

    [Fact]
    public void Constructor_rejectsRelativeRepositoryRoot()
    {
        var act = () => new PrivacyPathPolicy("relative-root");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("repoRoot");
    }
}
