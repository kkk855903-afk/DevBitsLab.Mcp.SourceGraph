using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ToolPackageContractTests
{
    private static readonly string[] SupportedRuntimeIdentifiers =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-arm64",
    ];

    [Fact]
    public void Release_package_projects_declare_audited_metadata_and_content()
    {
        var root = FindRepositoryRoot();
        var projectPaths = new[]
        {
            Path.Join(
                root,
                "src",
                "DevBitsLab.Mcp.SourceGraph.Server",
                "DevBitsLab.Mcp.SourceGraph.Server.csproj"),
            Path.Join(
                root,
                "src",
                "DevBitsLab.Mcp.SourceGraph.Sdk",
                "DevBitsLab.Mcp.SourceGraph.Sdk.csproj"),
        };

        foreach (var projectPath in projectPaths)
        {
            var project = XDocument.Load(projectPath);
            project.Descendants("PackageLicenseExpression")
                .Single()
                .Value
                .Should()
                .Be("MIT");
            project.Descendants("RepositoryUrl")
                .Single()
                .Value
                .Should()
                .Be(
                    "https://github.com/Jak3b0/"
                    + "DevBitsLab.Mcp.SourceGraph.git");
            project.Descendants("RepositoryType")
                .Single()
                .Value
                .Should()
                .Be("git");
            project.Descendants("PackageReadmeFile")
                .Single()
                .Value
                .Should()
                .Be("README.md");

            var readme = project
                .Descendants("None")
                .Single(item =>
                    string.Equals(
                        (string?)item.Attribute("Include"),
                        @"..\..\README.md",
                        StringComparison.Ordinal));
            ((string?)readme.Attribute("Pack")).Should().Be("true");
            ((string?)readme.Attribute("PackagePath")).Should().Be(@"\");
        }

        var sharedBuild = XDocument.Load(
            Path.Join(root, "Directory.Build.props"));
        var license = sharedBuild
            .Descendants("None")
            .Single(item =>
                string.Equals(
                    (string?)item.Attribute("Include"),
                    "$(MSBuildThisFileDirectory)LICENSE",
                    StringComparison.Ordinal));
        ((string?)license.Attribute("Pack")).Should().Be("true");
        ((string?)license.Attribute("PackagePath")).Should().Be(@"\");
    }

    [Fact]
    public void Every_advertised_tool_rid_has_both_clang_native_runtime_packages()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(
            Path.Join(
                root,
                "src",
                "DevBitsLab.Mcp.SourceGraph.Server",
                "DevBitsLab.Mcp.SourceGraph.Server.csproj"));
        var declared = project
            .Descendants("ToolPackageRuntimeIdentifiers")
            .Single()
            .Value
            .Split(
                ';',
                StringSplitOptions.TrimEntries
                    | StringSplitOptions.RemoveEmptyEntries);

        declared.Should().Equal(SupportedRuntimeIdentifiers);

        using var lockFile = JsonDocument.Parse(
            File.ReadAllText(
                Path.Join(
                    root,
                    "src",
                    "DevBitsLab.Mcp.SourceGraph.Server",
                    "packages.lock.json")));
        var lockedTargets = lockFile.RootElement.GetProperty("dependencies");
        foreach (var rid in declared)
        {
            var targetName = $"net10.0/{rid}";
            lockedTargets.TryGetProperty(targetName, out var target)
                .Should()
                .BeTrue($"{targetName} must be restored and locked");
            target.TryGetProperty($"libclang.runtime.{rid}", out _)
                .Should()
                .BeTrue($"{rid} must ship libclang");
            target.TryGetProperty($"libClangSharp.runtime.{rid}", out _)
                .Should()
                .BeTrue($"{rid} must ship libClangSharp");
        }
    }

    [Fact]
    public void Server_project_owns_rid_specific_protoc_packaging()
    {
        var root = FindRepositoryRoot();
        var serverPath = Path.Join(
            root,
            "src",
            "DevBitsLab.Mcp.SourceGraph.Server",
            "DevBitsLab.Mcp.SourceGraph.Server.csproj");
        var project = XDocument.Load(serverPath);

        var grpcTools = project
            .Descendants("PackageReference")
            .Single(reference =>
                string.Equals(
                    (string?)reference.Attribute("Include"),
                    "Grpc.Tools",
                    StringComparison.Ordinal));
        ((string?)grpcTools.Attribute("GeneratePathProperty"))
            .Should()
            .Be("true");
        ((string?)grpcTools.Attribute("PrivateAssets"))
            .Should()
            .Be("all");

        var mappings = project
            .Descendants("BundledProtocPlatform")
            .Select(element => (
                Condition: (string?)element.Attribute("Condition")
                    ?? string.Empty,
                Platform: element.Value))
            .ToArray();
        AssertRidMapping(mappings, "win-x64", "windows_x64");
        AssertRidMapping(mappings, "win-arm64", "windows_x64");
        AssertRidMapping(mappings, "linux-x64", "linux_x64");
        AssertRidMapping(mappings, "linux-arm64", "linux_arm64");
        AssertRidMapping(mappings, "osx-arm64", "macosx_x64");
        mappings.Should().NotContain(
            mapping => mapping.Condition.Contains(
                "osx-x64",
                StringComparison.Ordinal));

        var compiler = project
            .Descendants("None")
            .Single(item =>
                string.Equals(
                    (string?)item.Attribute("Include"),
                    @"$(PkgGrpc_Tools)\tools\$(BundledProtocPlatform)\$(BundledProtocFileName)",
                    StringComparison.Ordinal));
        ((string?)compiler.Attribute("Link"))
            .Should()
            .Be(@"protoc\$(BundledProtocFileName)");
        ((string?)compiler.Attribute("CopyToPublishDirectory"))
            .Should()
            .Be("PreserveNewest");
        compiler.Attribute("Pack").Should().BeNull(
            "the RID tool package already collects publish output");
        compiler.Attribute("PackagePath").Should().BeNull(
            "direct pack metadata would duplicate the compiler outside tools/net10.0/<rid>");

        var indexingProject = File.ReadAllText(
            Path.Join(
                root,
                "src",
                "DevBitsLab.Mcp.SourceGraph.Indexing",
                "DevBitsLab.Mcp.SourceGraph.Indexing.csproj"));
        indexingProject.Should().NotContain(
            "BundledProtocPlatform",
            "RID-conditioned content must not flow through a host-evaluated ProjectReference");
        indexingProject.Should().NotContain(
            "PkgGrpc_Tools",
            "the RID-bearing Server project must select the package asset directly");
    }

    [Theory]
    [InlineData("win-x64", "windows_x64", "protoc.exe", "pe-x64")]
    [InlineData("win-arm64", "windows_x64", "protoc.exe", "pe-x64")]
    [InlineData("linux-x64", "linux_x64", "protoc", "elf-x64")]
    [InlineData("linux-arm64", "linux_arm64", "protoc", "elf-arm64")]
    [InlineData("osx-arm64", "macosx_x64", "protoc", "macho-x64")]
    public void Protoc_mapping_selects_the_expected_non_host_package_asset(
        string rid,
        string platform,
        string fileName,
        string binaryKind)
    {
        SupportedRuntimeIdentifiers.Should().Contain(rid);

        var root = FindRepositoryRoot();
        var versions = XDocument.Load(
            Path.Join(root, "Directory.Packages.props"));
        var version = versions
            .Descendants("PackageVersion")
            .Single(package =>
                string.Equals(
                    (string?)package.Attribute("Include"),
                    "Grpc.Tools",
                    StringComparison.Ordinal))
            .Attribute("Version")!
            .Value;
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            packageRoot = Path.Join(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        var compilerPath = Path.Join(
            Path.GetFullPath(packageRoot),
            "grpc.tools",
            version,
            "tools",
            platform,
            fileName);
        File.Exists(compilerPath).Should().BeTrue(
            $"{rid} must map to a restored Grpc.Tools compiler asset");

        Span<byte> header = stackalloc byte[20];
        using var stream = File.OpenRead(compilerPath);
        stream.ReadExactly(header);
        AssertBinaryKind(header, binaryKind);
    }

    private static void AssertRidMapping(
        IEnumerable<(string Condition, string Platform)> mappings,
        string rid,
        string platform)
    {
        mappings.Should().ContainSingle(
            mapping =>
                mapping.Condition.Contains(
                    $"'$(RuntimeIdentifier)' == '{rid}'",
                    StringComparison.Ordinal)
                && string.Equals(
                    mapping.Platform,
                    platform,
                    StringComparison.Ordinal));
    }

    private static void AssertBinaryKind(
        ReadOnlySpan<byte> header,
        string binaryKind)
    {
        switch (binaryKind)
        {
            case "pe-x64":
                header[..2].ToArray().Should().Equal(0x4d, 0x5a);
                break;
            case "elf-x64":
                header[..4].ToArray().Should().Equal(0x7f, 0x45, 0x4c, 0x46);
                header[18..20].ToArray().Should().Equal(0x3e, 0x00);
                break;
            case "elf-arm64":
                header[..4].ToArray().Should().Equal(0x7f, 0x45, 0x4c, 0x46);
                header[18..20].ToArray().Should().Equal(0xb7, 0x00);
                break;
            case "macho-x64":
                header[..4].ToArray().Should().Equal(0xcf, 0xfa, 0xed, 0xfe);
                header[4..8].ToArray().Should().Equal(0x07, 0x00, 0x00, 0x01);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(binaryKind),
                    binaryKind,
                    "Unknown binary kind.");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(
                    Path.Join(
                        directory.FullName,
                        "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }
}
