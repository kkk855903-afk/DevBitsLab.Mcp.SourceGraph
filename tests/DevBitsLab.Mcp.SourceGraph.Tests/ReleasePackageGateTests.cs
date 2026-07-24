using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ReleasePackageGateTests
{
    private const string ReleaseCommit =
        "1111111111111111111111111111111111111111";
    private const string OtherCommit =
        "2222222222222222222222222222222222222222";
    private const string RepositoryUrl =
        "https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph.git";
    private const string ForkRepositoryUrl =
        "https://github.com/example-fork/DevBitsLab.Mcp.SourceGraph.git";
    private const string ToolPackageId =
        "DevBitsLab.Mcp.SourceGraph.Tool";
    private const string ToolVersion = "0.9.0";
    private const string SdkPackageId =
        "DevBitsLab.Mcp.SourceGraph.Sdk";
    private const string SdkVersion = "2.5.0";
    private const string RuntimeIdentifiers =
        "win-x64;win-arm64;linux-x64;linux-arm64;osx-arm64";

    private static readonly string[] Rids =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-arm64",
    ];

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/publish-nuget.yml")]
    public void Tool_pack_workflows_rebuild_rid_outputs(string relativePath)
    {
        var workflow = File.ReadAllText(
            Path.Join(
                FindRepositoryRoot(),
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));

        workflow.Should().Contain(
            "dotnet pack src/DevBitsLab.Mcp.SourceGraph.Server "
            + "-c Release --no-restore "
            + "/p:ContinuousIntegrationBuild=true -o ./out");
        workflow.Should().NotContain(
            "dotnet pack src/DevBitsLab.Mcp.SourceGraph.Server "
            + "-c Release --no-build");
    }

    [Fact]
    public async Task Gate_accepts_commit_bound_source_link_and_embedded_source()
    {
        var result = await RunGateScenarioAsync("valid");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().Contain("Verified tool 0.9.0");
    }

    [Fact]
    public async Task Gate_accepts_explicit_fork_source_link_repository()
    {
        var result = await RunGateScenarioAsync(
            "valid",
            ForkRepositoryUrl);

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().Contain("Verified tool 0.9.0");
    }

    [Fact]
    public async Task Gate_accepts_fork_source_link_from_github_environment()
    {
        var result = await RunGateScenarioAsync(
            "valid",
            ForkRepositoryUrl,
            useGitHubEnvironment: true);

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().Contain("Verified tool 0.9.0");
    }

    [Theory]
    [InlineData(
        "missing-pdb-source-link",
        "module-level SourceLink documents; expected exactly one")]
    [InlineData(
        "non-module-source-link",
        "non-module SourceLink documents")]
    [InlineData(
        "repository-commit-mismatch",
        "expected release commit")]
    [InlineData(
        "target-commit-mismatch",
        "does not match repository commit")]
    [InlineData(
        "unrelated-source-pattern",
        "does not cover any PDB document")]
    [InlineData(
        "escaping-source-capture",
        "captures unsafe path")]
    public async Task Gate_rejects_unverifiable_symbol_packages(
        string scenario,
        string expectedError)
    {
        var result = await RunGateScenarioAsync(scenario);
        var normalizedOutput = NormalizePowerShellOutput(result.Output);

        result.ExitCode.Should().NotBe(0, result.Output);
        normalizedOutput.Should().Contain(expectedError);
    }

    private static async Task<GateResult> RunGateScenarioAsync(
        string scenario,
        string sourceLinkRepositoryUrl = RepositoryUrl,
        bool useGitHubEnvironment = false)
    {
        var root = FindRepositoryRoot();
        var packageDirectory = Path.Join(
            Path.GetTempPath(),
            $"sourcegraph-package-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDirectory);

        try
        {
            CreatePackageSet(
                root,
                packageDirectory,
                scenario,
                sourceLinkRepositoryUrl);
            return await RunGateAsync(
                root,
                packageDirectory,
                useGitHubEnvironment || string.Equals(
                    sourceLinkRepositoryUrl,
                    RepositoryUrl,
                    StringComparison.Ordinal)
                    ? null
                    : sourceLinkRepositoryUrl,
                useGitHubEnvironment
                    ? sourceLinkRepositoryUrl
                    : null);
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    private static void CreatePackageSet(
        string root,
        string packageDirectory,
        string scenario,
        string sourceLinkRepositoryUrl)
    {
        var releasePackages = new List<(string Id, string Version)>
        {
            (ToolPackageId, ToolVersion),
            (SdkPackageId, SdkVersion),
        };
        releasePackages.AddRange(
            Rids.Select(rid => ($"{ToolPackageId}.{rid}", ToolVersion)));

        foreach (var package in releasePackages)
        {
            CreateReleasePackage(
                root,
                packageDirectory,
                package.Id,
                package.Version,
                ReleaseCommit);
        }

        var symbolRepositoryCommit =
            scenario == "repository-commit-mismatch"
                ? OtherCommit
                : ReleaseCommit;
        var sourceLinkCommit =
            scenario == "target-commit-mismatch"
                ? OtherCommit
                : symbolRepositoryCommit;
        var sourcePattern =
            scenario == "unrelated-source-pattern"
                ? "/unrelated/*"
                : "/_/*";
        var sourceDocument =
            scenario == "escaping-source-capture"
                ? "/_/../Secret.cs"
                : "/_/src/Example.cs";
        var sourceLinkPdb = CreatePortablePdb(
            sourceDocument,
            sourcePattern,
            sourceLinkCommit,
            sourceLinkRepositoryUrl,
            includeSourceLink: true,
            includeEmbeddedDocument: true,
            sourceLinkOnDocument:
                scenario == "non-module-source-link");
        var missingSourceLinkPdb = CreatePortablePdb(
            "/_/src/Missing.cs",
            sourcePattern,
            sourceLinkCommit,
            sourceLinkRepositoryUrl,
            includeSourceLink: false,
            includeEmbeddedDocument: false,
            sourceLinkOnDocument: false);

        var symbolPackages = new List<(string Id, string Version)>
        {
            (SdkPackageId, SdkVersion),
        };
        symbolPackages.AddRange(
            Rids.Select(rid => ($"{ToolPackageId}.{rid}", ToolVersion)));

        for (var index = 0; index < symbolPackages.Count; index++)
        {
            var package = symbolPackages[index];
            CreateSymbolPackage(
                packageDirectory,
                package.Id,
                package.Version,
                symbolRepositoryCommit,
                sourceLinkPdb,
                scenario == "missing-pdb-source-link" && index == 0
                    ? missingSourceLinkPdb
                    : null);
        }
    }

    private static void CreateReleasePackage(
        string root,
        string packageDirectory,
        string packageId,
        string version,
        string repositoryCommit)
    {
        var packagePath = Path.Join(
            packageDirectory,
            $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(
            packagePath,
            ZipArchiveMode.Create);

        WriteNuspec(
            archive,
            packageId,
            version,
            repositoryCommit,
            isSymbolPackage: false);
        WriteEntry(
            archive,
            "LICENSE",
            File.ReadAllBytes(Path.Join(root, "LICENSE")));
        WriteEntry(
            archive,
            "README.md",
            File.ReadAllBytes(Path.Join(root, "README.md")));
    }

    private static void CreateSymbolPackage(
        string packageDirectory,
        string packageId,
        string version,
        string repositoryCommit,
        byte[] sourceLinkPdb,
        byte[]? additionalPdb)
    {
        var packagePath = Path.Join(
            packageDirectory,
            $"{packageId}.{version}.snupkg");
        using var archive = ZipFile.Open(
            packagePath,
            ZipArchiveMode.Create);

        WriteNuspec(
            archive,
            packageId,
            version,
            repositoryCommit,
            isSymbolPackage: true);
        WriteEntry(archive, "symbols/primary.pdb", sourceLinkPdb);
        if (additionalPdb is not null)
        {
            WriteEntry(archive, "symbols/missing.pdb", additionalPdb);
        }
    }

    private static void WriteNuspec(
        ZipArchive archive,
        string packageId,
        string version,
        string repositoryCommit,
        bool isSymbolPackage)
    {
        XNamespace ns =
            "http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd";
        var metadata = new XElement(
            ns + "metadata",
            new XElement(ns + "id", packageId),
            new XElement(ns + "version", version),
            new XElement(
                ns + "repository",
                new XAttribute("type", "git"),
                new XAttribute("url", RepositoryUrl),
                new XAttribute("commit", repositoryCommit)));
        if (isSymbolPackage)
        {
            metadata.Add(
                new XElement(
                    ns + "packageTypes",
                    new XElement(
                        ns + "packageType",
                        new XAttribute("name", "SymbolsPackage"))));
        }
        else
        {
            metadata.Add(
                new XElement(
                    ns + "license",
                    new XAttribute("type", "expression"),
                    "MIT"),
                new XElement(ns + "readme", "README.md"));
        }

        var document = new XDocument(
            new XElement(ns + "package", metadata));
        var entry = archive.CreateEntry(
            $"{packageId}.nuspec",
            CompressionLevel.Optimal);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string entryName,
        byte[] content)
    {
        var entry = archive.CreateEntry(
            entryName,
            CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] CreatePortablePdb(
        string sourceDocumentName,
        string sourcePattern,
        string sourceLinkCommit,
        string sourceLinkRepositoryUrl,
        bool includeSourceLink,
        bool includeEmbeddedDocument,
        bool sourceLinkOnDocument)
    {
        var metadata = new MetadataBuilder();
        var sourceDocument = metadata.AddDocument(
            metadata.GetOrAddDocumentName(sourceDocumentName),
            default,
            default,
            default);

        if (includeEmbeddedDocument)
        {
            var embeddedDocument = metadata.AddDocument(
                metadata.GetOrAddDocumentName(
                    "/generated/Generated.g.cs"),
                default,
                default,
                default);
            var embeddedSource = Encoding.UTF8.GetBytes(
                "// generated source");
            var embeddedBlob = new byte[sizeof(int) + embeddedSource.Length];
            embeddedSource.CopyTo(embeddedBlob, sizeof(int));
            metadata.AddCustomDebugInformation(
                embeddedDocument,
                metadata.GetOrAddGuid(
                    new Guid(
                        "0E8A571B-6926-466E-B4AD-8AB04611F5FE")),
                metadata.GetOrAddBlob(embeddedBlob));
        }

        if (includeSourceLink)
        {
            var sourceLinkRepositoryPath =
                new Uri(sourceLinkRepositoryUrl)
                    .AbsolutePath
                    .Trim('/');
            if (sourceLinkRepositoryPath.EndsWith(
                    ".git",
                    StringComparison.OrdinalIgnoreCase))
            {
                sourceLinkRepositoryPath =
                    sourceLinkRepositoryPath[..^4];
            }
            var sourceLink = JsonSerializer.Serialize(
                new
                {
                    documents = new Dictionary<string, string>
                    {
                        [sourcePattern] =
                            "https://raw.githubusercontent.com/"
                            + sourceLinkRepositoryPath + "/"
                            + $"{sourceLinkCommit}/*",
                    },
                });
            metadata.AddCustomDebugInformation(
                sourceLinkOnDocument
                    ? sourceDocument
                    : MetadataTokens.EntityHandle(TableIndex.Module, 1),
                metadata.GetOrAddGuid(
                    new Guid(
                        "CC110556-A091-4D38-9FEC-25AB9A351A6A")),
                metadata.GetOrAddBlob(Encoding.UTF8.GetBytes(sourceLink)));
        }

        var typeSystemRowCounts = new int[64];
        typeSystemRowCounts[(int)TableIndex.Module] = 1;
        var builder = new PortablePdbBuilder(
            metadata,
            ImmutableArray.CreateRange(typeSystemRowCounts),
            default,
            idProvider: null);
        var blob = new BlobBuilder();
        builder.Serialize(blob);
        return blob.ToArray();
    }

    private static async Task<GateResult> RunGateAsync(
        string root,
        string packageDirectory,
        string? sourceLinkRepositoryUrl,
        string? githubEnvironmentRepositoryUrl)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(
            Path.Join(
                root,
                ".github",
                "scripts",
                "assert-release-packages.ps1"));
        startInfo.ArgumentList.Add("-PackageDirectory");
        startInfo.ArgumentList.Add(packageDirectory);
        startInfo.ArgumentList.Add("-ToolVersion");
        startInfo.ArgumentList.Add(ToolVersion);
        startInfo.ArgumentList.Add("-SdkVersion");
        startInfo.ArgumentList.Add(SdkVersion);
        startInfo.ArgumentList.Add("-ToolRuntimeIdentifiers");
        startInfo.ArgumentList.Add(RuntimeIdentifiers);
        if (sourceLinkRepositoryUrl is not null)
        {
            startInfo.ArgumentList.Add("-SourceLinkRepositoryUrl");
            startInfo.ArgumentList.Add(sourceLinkRepositoryUrl);
        }
        if (githubEnvironmentRepositoryUrl is not null)
        {
            var repositoryPath = new Uri(githubEnvironmentRepositoryUrl)
                .AbsolutePath
                .Trim('/');
            if (repositoryPath.EndsWith(
                    ".git",
                    StringComparison.OrdinalIgnoreCase))
            {
                repositoryPath = repositoryPath[..^4];
            }
            startInfo.Environment["GITHUB_SERVER_URL"] =
                "https://github.com";
            startInfo.Environment["GITHUB_REPOSITORY"] =
                repositoryPath;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GateResult(
            process.ExitCode,
            $"{await standardOutput}{Environment.NewLine}"
            + $"{await standardError}");
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

    private static string NormalizePowerShellOutput(string output)
    {
        var withoutAnsi = Regex.Replace(
            output,
            @"\x1B\[[0-?]*[ -/]*[@-~]",
            string.Empty,
            RegexOptions.CultureInvariant);
        return Regex.Replace(
            withoutAnsi.Replace(
                "|",
                string.Empty,
                StringComparison.Ordinal),
            @"\s+",
            " ",
            RegexOptions.CultureInvariant);
    }

    private sealed record GateResult(int ExitCode, string Output);
}
