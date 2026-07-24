using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class FullOfflineBundleTests
{
    [Fact]
    public async Task Builder_creates_a_self_verifying_win_x64_bundle()
    {
        var root = FindRepositoryRoot();
        var testRoot = Path.Join(
            Path.GetTempPath(),
            $"sourcegraph-full-bundle-test-{Guid.NewGuid():N}");
        var packages = Path.Join(testRoot, "packages");
        var model = Path.Join(testRoot, "model");
        var output = Path.Join(testRoot, "output");
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(model);

        try
        {
            const string version = "9.8.7";
            File.WriteAllText(
                Path.Join(
                    packages,
                    $"DevBitsLab.Mcp.SourceGraph.Tool.{version}.nupkg"),
                "outer package fixture");
            File.WriteAllText(
                Path.Join(
                    packages,
                    $"DevBitsLab.Mcp.SourceGraph.Tool.win-x64.{version}.nupkg"),
                "runtime package fixture");

            var onnxPath = Path.Join(model, "model.onnx");
            var tokenizerPath = Path.Join(model, "tokenizer.json");
            File.WriteAllText(onnxPath, "small ONNX fixture");
            File.WriteAllText(tokenizerPath, """{"fixture":true}""");
            var manifestPath = Path.Join(testRoot, "model-manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        formatVersion = 1,
                        modelId = "example/test-model",
                        cacheDirectoryName = "example_test-model",
                        license = "Apache-2.0",
                        sourceUrl = "https://example.invalid/test-model",
                        files = new[]
                        {
                            new
                            {
                                name = "model.onnx",
                                sha256 = Sha256(onnxPath),
                            },
                            new
                            {
                                name = "tokenizer.json",
                                sha256 = Sha256(tokenizerPath),
                            },
                        },
                    }));

            var build = await RunPowerShellAsync(
                root,
                Path.Join(
                    root,
                    ".github",
                    "scripts",
                    "build-full-offline-bundle.ps1"),
                "-PackageDirectory",
                packages,
                "-ModelDirectory",
                model,
                "-OutputDirectory",
                output,
                "-ModelManifest",
                manifestPath);

            build.ExitCode.Should().Be(0, build.Output);
            var archivePath = Path.Join(
                output,
                $"SourceGraph-MCP-Full-win-x64-v{version}.zip");
            File.Exists(archivePath).Should().BeTrue();
            File.Exists($"{archivePath}.sha256").Should().BeTrue();
            File.ReadAllText($"{archivePath}.sha256")
                .Should()
                .StartWith(Sha256(archivePath));

            var extracted = Path.Join(testRoot, "extracted");
            ZipFile.ExtractToDirectory(archivePath, extracted);
            var expectedEntries = new[]
            {
                "APACHE-2.0.txt",
                "PROJECT-LICENSE.txt",
                "README-zh-CN.txt",
                "THIRD-PARTY-NOTICES.txt",
                "bundle-manifest.json",
                "install-sourcegraph-mcp.ps1",
                "model/model.onnx",
                "model/tokenizer.json",
                $"packages/DevBitsLab.Mcp.SourceGraph.Tool.{version}.nupkg",
                $"packages/DevBitsLab.Mcp.SourceGraph.Tool.win-x64.{version}.nupkg",
                "setup-sourcegraph-mcp.ps1",
            };
            foreach (var entry in expectedEntries)
            {
                File.Exists(
                        Path.Join(
                            extracted,
                            entry.Replace(
                                '/',
                                Path.DirectorySeparatorChar)))
                    .Should()
                    .BeTrue($"the bundle must contain {entry}");
            }

            var verify = await RunPowerShellAsync(
                extracted,
                Path.Join(extracted, "install-sourcegraph-mcp.ps1"),
                "-VerifyBundleOnly");
            verify.ExitCode.Should().Be(0, verify.Output);
            verify.Output.Should().Contain(
                "Full offline bundle verified");

            File.AppendAllText(
                Path.Join(extracted, "model", "tokenizer.json"),
                "tampered");
            var tampered = await RunPowerShellAsync(
                extracted,
                Path.Join(extracted, "install-sourcegraph-mcp.ps1"),
                "-VerifyBundleOnly");
            tampered.ExitCode.Should().NotBe(0);
            tampered.Output.Should().Contain("SHA-256 mismatch");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Release_workflow_builds_and_publishes_the_full_bundle()
    {
        var workflow = File.ReadAllText(
            Path.Join(
                FindRepositoryRoot(),
                ".github",
                "workflows",
                "publish-nuget.yml"));

        workflow.Should().Contain(
            "./.github/scripts/build-full-offline-bundle.ps1");
        workflow.Should().Contain("sourcegraph-mcp embeddings pull");
        workflow.Should().Contain("gh release upload");
        workflow.Should().Contain("gh release create");
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string workingDirectory,
        string script,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
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

    private sealed record ProcessResult(int ExitCode, string Output);
}
