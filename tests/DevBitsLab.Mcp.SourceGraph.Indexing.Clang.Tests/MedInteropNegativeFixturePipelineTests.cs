using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Interop;
using DevBitsLab.Mcp.SourceGraph.Interop;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Clang.Tests;

public sealed class MedInteropNegativeFixturePipelineTests
{
    [Fact]
    public void RealRoslynAndLibClangFactsProduceEveryInteropRule()
    {
        var negativeRoot = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "MedInteropChain",
            "NegativeCases");
        var managedPaths = Directory
            .EnumerateFiles(negativeRoot, "Managed.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var compilation = Compile(managedPaths);
        var fileIds = managedPaths
            .Select((path, index) => (path, id: (long)index + 1))
            .ToDictionary(item => item.path, item => item.id, PathComparer);
        var pathsById = fileIds.ToDictionary(item => item.Value, item => item.Key);
        var imports = ExtractManagedImports(compilation, fileIds);
        var usages = ExtractManagedUsages(
            compilation,
            fileIds,
            pathsById);

        using var native = new NativeFixtureWorkspace();
        var nativeFacts = ExtractNativeFacts(negativeRoot, native);
        var findings = new List<InteropFinding>();
        var matcher = new InteropMatcher();

        foreach (var (entryPoint, managed) in imports)
        {
            var nativeExport = nativeFacts.Exports[entryPoint];
            matcher.Match(managed, [nativeExport]).Status.Should()
                .Be(InteropMatchStatus.SourceMatched);

            var boundary = new InteropBoundary(managed, nativeExport)
            {
                CallbackUsages = usages.CallbackUsages
                    .Where(item =>
                        item.ManagedImportSymbolCanonicalKey
                        == managed.SymbolCanonicalKey)
                    .Select(item => item.Usage)
                    .ToArray(),
                ReturnReleases = usages.ReturnReleases
                    .Where(item =>
                        item.ManagedImportSymbolCanonicalKey
                        == managed.SymbolCanonicalKey)
                    .Select(item => item.Release)
                    .ToArray(),
            };
            findings.AddRange(
                InteropRuleEngine.CreatePhase2().Evaluate(boundary));
        }

        var managedRecord = compilation.GetTypeByMetadataName(
            "MedInteropChain.NegativeCases.Interop002.WrongLayout");
        managedRecord.Should().NotBeNull();
        var managedLayout = ManagedRecordLayoutExtractor.TryExtract(
            managedRecord!,
            InteropTarget.WindowsX64Msvc,
            fileIds[managedRecord!.Locations.Single().SourceTree!.FilePath]);
        managedLayout.Should().NotBeNull();
        var layoutResult = new AbiStructCompatibilityEngine().Compare(
            managedLayout!,
            nativeFacts.WrongLayout);
        var layoutFinding = new Interop002FindingAdapter().CreateFinding(
            layoutResult);
        layoutFinding.Should().NotBeNull();
        findings.Add(layoutFinding!);

        findings.Select(finding => finding.RuleId)
            .Distinct(StringComparer.Ordinal)
            .Should().BeEquivalentTo(
                "Interop001",
                "Interop002",
                "Interop003",
                "Interop004",
                "Interop005",
                "Interop006");
        findings.Should().OnlyContain(finding =>
            finding.Evidence.Count >= 2
            && finding.Evidence.All(evidence =>
                Path.IsPathFullyQualified(evidence.Location.FilePath)));
        findings.Where(finding => finding.RuleId != "Interop002")
            .Should().OnlyContain(finding =>
                finding.Evidence.Any(evidence =>
                    evidence.Producer == "roslyn-managed-interop")
                && finding.Evidence.Any(evidence =>
                    evidence.Producer.StartsWith(
                        "clang-native",
                        StringComparison.Ordinal)));
        layoutFinding!.Evidence.Should().Contain(evidence =>
            evidence.Producer == "roslyn-managed-layout");
        layoutFinding.Evidence.Should().Contain(evidence =>
            evidence.Producer == "clang-native");
    }

    private static CSharpCompilation Compile(
        IReadOnlyList<string> managedPaths)
    {
        var trees = managedPaths
            .Select(path => CSharpSyntaxTree.ParseText(
                File.ReadAllText(path),
                new CSharpParseOptions(LanguageVersion.Preview),
                path))
            .ToArray();
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException(
                "The test host did not expose trusted platform assemblies.");
        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "MedInteropNegativeFixtures",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        return compilation;
    }

    private static Dictionary<string, ManagedImport> ExtractManagedImports(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, long> fileIds)
    {
        var imports = new Dictionary<string, ManagedImport>(
            StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not
                    IMethodSymbol method)
                {
                    continue;
                }
                var target = tree.FilePath.Contains(
                    $"{Path.DirectorySeparatorChar}Interop001"
                    + Path.DirectorySeparatorChar,
                    PathComparison)
                    ? InteropTarget.WindowsX86Msvc
                    : InteropTarget.WindowsX64Msvc;
                var import = ManagedInteropExtractor.TryExtract(
                    method,
                    target,
                    fileIds[tree.FilePath],
                    tree.FilePath);
                if (import is not null)
                {
                    imports.Add(import.EntryPoint, import);
                }
            }
        }
        imports.Should().HaveCount(5);
        return imports;
    }

    private static ManagedInteropUsageExtraction ExtractManagedUsages(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, long> fileIds,
        IReadOnlyDictionary<long, string> pathsById)
    {
        var callbacks = new List<ManagedCallbackUsageProjection>();
        var releases = new List<ManagedReturnReleaseProjection>();

        long? ResolveImportFileId(IMethodSymbol method)
        {
            var path = method.Locations.FirstOrDefault(location =>
                location.IsInSource)?.SourceTree?.FilePath;
            return path is not null && fileIds.TryGetValue(path, out var id)
                ? id
                : null;
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var extraction = ManagedInteropUsageExtractor.Extract(
                tree.GetRoot(),
                compilation.GetSemanticModel(tree),
                InteropTarget.WindowsX64Msvc,
                fileIds[tree.FilePath],
                tree.FilePath,
                ResolveImportFileId,
                id => pathsById.GetValueOrDefault(id));
            callbacks.AddRange(extraction.CallbackUsages);
            releases.AddRange(extraction.ReturnReleases);
        }

        callbacks.Should().ContainSingle();
        releases.Should().ContainSingle();
        return new ManagedInteropUsageExtraction(callbacks, releases);
    }

    private static NativeFixtureFacts ExtractNativeFacts(
        string negativeRoot,
        NativeFixtureWorkspace workspace)
    {
        var includeRoot = Path.Combine(workspace.Root, "toolchain");
        Directory.CreateDirectory(includeRoot);
        File.WriteAllText(
            Path.Combine(includeRoot, "cstdint"),
            """
            #pragma once
            namespace std { using int32_t = int; }
            """);
        File.WriteAllText(
            Path.Combine(includeRoot, "stdexcept"),
            """
            #pragma once
            namespace std {
            class runtime_error {
            public:
                explicit runtime_error(const char*);
            };
            }
            """);
        File.WriteAllText(
            Path.Combine(includeRoot, "cstdlib"),
            """
            #pragma once
            using size_t = unsigned long long;
            extern "C" void* malloc(size_t);
            namespace std { using ::malloc; }
            """);

        var exports = new Dictionary<string, NativeExport>(
            StringComparer.Ordinal);
        AbiRecordLayout? wrongLayout = null;
        for (var rule = 1; rule <= 6; rule++)
        {
            var ruleName = $"Interop{rule:000}";
            var sourcePath = workspace.Copy(
                Path.Combine(
                    negativeRoot,
                    ruleName,
                    "Native.cpp"),
                Path.Combine(ruleName, "Native.cpp"));
            var target = rule == 1
                ? InteropTarget.WindowsX86Msvc
                : InteropTarget.WindowsX64Msvc;
            var result = ClangNativeExtractor.Extract(
                new ClangNativeExtractionRequest(
                    sourcePath,
                    workspace.Root,
                    ProducingFileId: 100 + rule,
                    target,
                    WindowsArguments(target, includeRoot),
                    LibraryName: "medalgo"));
            result.Diagnostics.Should().NotContain(diagnostic =>
                diagnostic.Severity
                    == ClangExtractionDiagnosticSeverity.Error
                || diagnostic.Severity
                    == ClangExtractionDiagnosticSeverity.Fatal);

            foreach (var export in result.Exports)
            {
                exports.Add(export.ExportName, export);
            }
            if (rule == 2)
            {
                wrongLayout = result.RecordLayouts.Should()
                    .ContainSingle(layout =>
                        layout.SymbolCanonicalKey.EndsWith(
                            "::WrongLayout",
                            StringComparison.Ordinal))
                    .Subject;
            }
        }

        exports.Should().HaveCount(5);
        wrongLayout.Should().NotBeNull();
        return new NativeFixtureFacts(exports, wrongLayout!);
    }

    private static string[] WindowsArguments(
        InteropTarget target,
        string includeRoot) =>
        [
            "-x",
            "c++",
            "-std=c++17",
            target.Architecture == InteropArchitecture.X86
                ? "--target=i686-pc-windows-msvc"
                : "--target=x86_64-pc-windows-msvc",
            "-fms-extensions",
            "-D_WIN32=1",
            "-I",
            includeRoot,
        ];

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "Directory.Packages.props"))
                && Directory.Exists(Path.Combine(
                    directory.FullName,
                    "tests",
                    "fixtures",
                    "MedInteropChain",
                    "NegativeCases")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record NativeFixtureFacts(
        IReadOnlyDictionary<string, NativeExport> Exports,
        AbiRecordLayout WrongLayout);

    private sealed class NativeFixtureWorkspace : IDisposable
    {
        public NativeFixtureWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "sourcegraph-negative-pipeline-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Copy(string sourcePath, string relativePath)
        {
            var destination = Path.GetFullPath(
                Path.Combine(Root, relativePath));
            if (!destination.StartsWith(
                    Root + Path.DirectorySeparatorChar,
                    PathComparison))
            {
                throw new InvalidOperationException(
                    "The fixture copy escaped its temporary root.");
            }
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination);
            return destination;
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (resolved.StartsWith(
                    temporaryRoot,
                    PathComparison)
                && Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
