using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Interop;
using DevBitsLab.Mcp.SourceGraph.Interop;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Clang.Tests;

public sealed class MedInteropBoundaryPipelineTests
{
    [Fact]
    public void FixtureExtractors_sourceMatchOneBoundary_withoutPhase2SignatureFindings()
    {
        var fixtureRoot = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "MedInteropChain",
            "GrpcService",
            "Interop");
        using var fixture = ExtractFixtureBoundary(
            (
                "NativeTypes.cs",
                File.ReadAllText(Path.Combine(fixtureRoot, "NativeTypes.cs"))),
            (
                "NativeMethods.cs",
                File.ReadAllText(Path.Combine(fixtureRoot, "NativeMethods.cs"))));

        fixture.Match.Status.Should().Be(InteropMatchStatus.SourceMatched);
        fixture.Match.NativeSymbolCanonicalKey.Should()
            .Be(fixture.Native.SymbolCanonicalKey);
        fixture.Match.Reasons.Should().Contain(reason =>
            reason.Contains("not been verified", StringComparison.OrdinalIgnoreCase));

        InteropRuleEngine.CreatePhase2()
            .Evaluate(new InteropBoundary(fixture.Managed, fixture.Native))
            .Should().NotContain(finding =>
                finding.RuleId == "Interop001"
                || finding.RuleId == "Interop003");
    }

    [Fact]
    public void FixtureExtractors_byValueRecordAgainstNativePointer_reportsPointerDepthMismatch()
    {
        using var fixture = ExtractFixtureBoundary(
            (
                "WrongSignature.cs",
                """
                using System.Runtime.InteropServices;

                namespace MedInteropChain.GrpcService.Interop;

                internal struct NativeInput
                {
                    public int PatientAge;
                    public double Scale;
                }

                internal struct NativeOutput
                {
                    public int Value;
                }

                internal static class NativeMethods
                {
                    [DllImport(
                        "medalgo",
                        EntryPoint = "medalgo_calculate",
                        CallingConvention = CallingConvention.Cdecl,
                        ExactSpelling = true)]
                    internal static extern int Calculate(
                        NativeInput input,
                        out NativeOutput output);
                }
                """));

        InteropRuleEngine.CreatePhase2()
            .Evaluate(new InteropBoundary(fixture.Managed, fixture.Native))
            .Should().ContainSingle(finding =>
                finding.RuleId == "Interop003"
                && finding.Message.Contains(
                    "pointer depth mismatch",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static FixtureBoundary ExtractFixtureBoundary(
        params (string Path, string Source)[] managedSources)
    {
        var repoRoot = FindRepositoryRoot();
        var nativeRoot = Path.Combine(
            repoRoot,
            "tests",
            "fixtures",
            "MedInteropChain",
            "NativeLibrary");
        var toolchainRoot = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-clang-tests",
            Guid.NewGuid().ToString("N"));
        var isolatedNativeRoot = Path.Combine(toolchainRoot, "native");
        Directory.CreateDirectory(Path.Combine(isolatedNativeRoot, "include"));
        Directory.CreateDirectory(Path.Combine(isolatedNativeRoot, "src"));
        File.Copy(
            Path.Combine(nativeRoot, "include", "medalgo.h"),
            Path.Combine(isolatedNativeRoot, "include", "medalgo.h"));
        File.Copy(
            Path.Combine(nativeRoot, "src", "algorithm.hpp"),
            Path.Combine(isolatedNativeRoot, "src", "algorithm.hpp"));
        File.Copy(
            Path.Combine(nativeRoot, "src", "exports.cpp"),
            Path.Combine(isolatedNativeRoot, "src", "exports.cpp"));
        File.WriteAllText(
            Path.Combine(isolatedNativeRoot, "include", "cstdint"),
            """
            #pragma once
            namespace std { using int32_t = int; }
            """);
        var sourcePath = Path.Combine(isolatedNativeRoot, "src", "exports.cpp");

        var nativeResult = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                isolatedNativeRoot,
                ProducingFileId: 41,
                InteropTarget.WindowsX64Msvc,
                [
                    "-x",
                    "c++",
                    "-std=c++17",
                    "--target=x86_64-pc-windows-msvc",
                    "-fms-extensions",
                    "-D_WIN32=1",
                    "-I",
                    Path.Combine(isolatedNativeRoot, "include"),
                    "-I",
                    Path.Combine(isolatedNativeRoot, "src"),
                ],
                LibraryName: "medalgo.dll"));
        nativeResult.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
            || diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal);
        var native = nativeResult.Exports.Should().ContainSingle().Subject;

        var method = CompileMethod(managedSources);
        var managed = ManagedInteropExtractor.TryExtract(
            method,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 17);
        managed.Should().NotBeNull();
        var match = new InteropMatcher().Match(managed!, [native]);

        return new FixtureBoundary(toolchainRoot, managed!, native, match);
    }

    private static IMethodSymbol CompileMethod(
        IReadOnlyList<(string Path, string Source)> sources)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Source,
                new CSharpParseOptions(LanguageVersion.Preview),
                path: source.Path))
            .ToArray();
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException(
                "The test host did not expose trusted platform assemblies.");
        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "MedInteropBoundaryFixture",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        return compilation
            .GetTypeByMetadataName(
                "MedInteropChain.GrpcService.Interop.NativeMethods")!
            .GetMembers("Calculate")
            .OfType<IMethodSymbol>()
            .Should().ContainSingle().Subject;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props"))
                && File.Exists(Path.Combine(
                    directory.FullName,
                    "tests",
                    "fixtures",
                    "MedInteropChain",
                    "NativeLibrary",
                    "include",
                    "medalgo.h")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record FixtureBoundary(
        string ToolchainRoot,
        ManagedImport Managed,
        NativeExport Native,
        InteropMatch Match) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(ToolchainRoot))
            {
                Directory.Delete(ToolchainRoot, recursive: true);
            }
        }
    }
}
