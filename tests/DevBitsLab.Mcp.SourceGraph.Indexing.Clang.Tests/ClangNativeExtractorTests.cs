using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Clang.Tests;

public sealed class ClangNativeExtractorTests
{
    [Fact]
    public void MedInteropFixture_extractsExportSignatureAndExactRecordLayouts()
    {
        var repoRoot = FindRepositoryRoot();
        var nativeRoot = Path.Combine(
            repoRoot,
            "tests",
            "fixtures",
            "MedInteropChain",
            "NativeLibrary");
        var sourcePath = Path.Combine(nativeRoot, "src", "exports.cpp");
        using var toolchain = new NativeTestWorkspace();
        var cstdintPath = toolchain.Write(
            "include/cstdint",
            """
            #pragma once
            namespace std { using int32_t = int; }
            """);

        var result = ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            sourcePath,
            nativeRoot,
            ProducingFileId: 41,
            InteropTarget.WindowsX64Msvc,
            WindowsX64Arguments(
                Path.GetDirectoryName(cstdintPath)!,
                Path.Combine(nativeRoot, "include"),
                Path.Combine(nativeRoot, "src")),
            LibraryName: "medalgo.dll"));

        result.Diagnostics.Should().NotContain(
            diagnostic =>
                diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
                || diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal);
        var nativeExport = result.Exports.Should().ContainSingle().Subject;
        nativeExport.ExportName.Should().Be("medalgo_calculate");
        nativeExport.SymbolCanonicalKey.Should()
            .Be("c:E:src/exports.cpp::medalgo_calculate");
        nativeExport.LibraryName.Should().Be("medalgo.dll");
        nativeExport.ModuleIdentitySource.Should()
            .Be(NativeModuleIdentitySource.Configuration);
        nativeExport.HasCLinkage.Should().BeTrue();
        nativeExport.IsBinaryVerified.Should().BeFalse();
        nativeExport.CallingConvention.Should().Be(InteropCallingConvention.Cdecl);
        nativeExport.ReturnType.Should().BeEquivalentTo(new
        {
            Category = AbiTypeCategory.SignedInteger,
            SizeBytes = (int?)4,
            AlignmentBytes = (int?)4,
            IsSigned = (bool?)true,
        });
        nativeExport.Parameters.Should().HaveCount(2)
            .And.OnlyContain(parameter =>
                parameter.Type.Category == AbiTypeCategory.Pointer
                && parameter.Type.PointerDepth == 1
                && parameter.Type.SizeBytes == 8);
        nativeExport.Parameters[0].Direction.Should().Be(AbiParameterDirection.In);
        nativeExport.Parameters[0].Type.PointeeType!.Category.Should()
            .Be(AbiTypeCategory.Record);
        nativeExport.Parameters[0].Type.IsPointeeConst.Should().BeTrue();
        nativeExport.Parameters[1].Direction.Should().Be(AbiParameterDirection.Unknown);
        nativeExport.Parameters[1].Type.PointeeType!.Category.Should()
            .Be(AbiTypeCategory.Record);
        nativeExport.Parameters[1].Type.IsPointeeConst.Should().BeFalse();
        nativeExport.Evidence.Confidence.Should().Be(EvidenceConfidence.Exact);
        nativeExport.Evidence.Producer.Should().Be("clang-native");
        nativeExport.Evidence.Location.FilePath.Should().Be(sourcePath);
        nativeExport.Evidence.Location.StartLine.Should().Be(3);

        var input = result.RecordLayouts.Single(
            layout => layout.SymbolCanonicalKey.EndsWith(
                "::NativeInput",
                StringComparison.Ordinal));
        input.SizeBytes.Should().Be(16);
        input.AlignmentBytes.Should().Be(8);
        input.Pack.Should().BeNull("Clang exposes the exact layout, not a guessed pragma pack");
        input.Fields.Select(field => (field.Name, field.OffsetBytes, field.SizeBytes))
            .Should().Equal(
                ("patient_age", (int?)0, (int?)4),
                ("scale", (int?)8, (int?)8));
        input.Evidence.Location.FilePath.Should()
            .Be(Path.Combine(nativeRoot, "include", "medalgo.h"));

        var output = result.RecordLayouts.Single(
            layout => layout.SymbolCanonicalKey.EndsWith(
                "::NativeOutput",
                StringComparison.Ordinal));
        output.SizeBytes.Should().Be(4);
        output.AlignmentBytes.Should().Be(4);
        output.Fields.Should().ContainSingle(field =>
            field.Name == "value"
            && field.OffsetBytes == 0
            && field.SizeBytes == 4);
    }

    [Fact]
    public void CppDeclarations_keepOverloadsAndDoNotPromoteNonCFunctionsToExports()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/declarations.cpp",
            """
            #define API __declspec(dllexport)

            extern "C" API int c_export(int value);
            extern "C" int not_exported(int value);
            API int overloaded(int value);
            API double overloaded(double value);

            struct Pair {
                int count;
                double scale;
            };

            union Bits {
                int integer_value;
                float float_value;
            };

            enum class Mode : unsigned short {
                Off = 0,
                On = 1
            };

            typedef unsigned long NativeHandle;
            struct Opaque;
            """);

        var result = ExtractWindows(workspace.Root, sourcePath);

        result.Diagnostics.Should().NotContain(
            diagnostic =>
                diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
                || diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal);
        result.Exports.Should().ContainSingle(nativeExport =>
            nativeExport.ExportName == "c_export"
            && nativeExport.LibraryName == null);
        result.Functions.Where(function => function.Name == "overloaded")
            .Should().HaveCount(2)
            .And.OnlyContain(function =>
                function.IsExported && !function.HasCLinkage);
        result.Functions.Should().ContainSingle(function =>
            function.Name == "not_exported"
            && function.HasCLinkage
            && !function.IsExported);

        result.Types.Should().Contain(type =>
            type.Kind == NativeTypeDeclarationKind.Struct
            && type.Name == "Pair"
            && type.DeclaredType.SizeBytes == 16);
        result.Types.Should().Contain(type =>
            type.Kind == NativeTypeDeclarationKind.Union
            && type.Name == "Bits");
        result.Types.Should().Contain(type =>
            type.Kind == NativeTypeDeclarationKind.Enum
            && type.Name == "Mode"
            && type.DeclaredType.SizeBytes == 2);
        result.Types.Should().Contain(type =>
            type.Kind == NativeTypeDeclarationKind.Typedef
            && type.Name == "NativeHandle"
            && type.DeclaredType.SizeBytes == 4);

        var unionLayout = result.RecordLayouts.Single(
            layout => layout.SymbolCanonicalKey.EndsWith(
                "::Bits",
                StringComparison.Ordinal));
        unionLayout.Fields.Should().OnlyContain(field => field.OffsetBytes == 0);

        var opaque = result.Types.Single(type => type.Name == "Opaque");
        opaque.DeclaredType.Category.Should().Be(AbiTypeCategory.Record);
        opaque.DeclaredType.SizeBytes.Should().BeNull();
        opaque.DeclaredType.AlignmentBytes.Should().BeNull();
        result.RecordLayouts.Should().NotContain(
            layout => layout.SymbolCanonicalKey.EndsWith(
                "::Opaque",
                StringComparison.Ordinal));
    }

    [Fact]
    public void VisibilityDefault_isExported_butVisibilityHiddenIsNot()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/visibility.cpp",
            """
            extern "C" __attribute__((visibility("default"))) int visible_api();
            extern "C" __attribute__((visibility("hidden"))) int hidden_api();
            """);
        var linuxTarget = new InteropTarget(
            "linux-x64",
            InteropArchitecture.X64,
            InteropCompilerAbi.Itanium,
            pointerSizeBytes: 8,
            defaultPack: 8);

        var result = ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            sourcePath,
            workspace.Root,
            ProducingFileId: 7,
            linuxTarget,
            [
                "-x",
                "c++",
                "-std=c++17",
                "--target=x86_64-unknown-linux-gnu",
            ],
            LibraryName: "libmedical.so"));

        result.Diagnostics.Should().NotContain(
            diagnostic =>
                diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
                || diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal);
        result.Exports.Should().ContainSingle(export =>
            export.ExportName == "visible_api"
            && export.LibraryName == "libmedical.so");
        result.Functions.Should().ContainSingle(function =>
            function.Name == "hidden_api"
            && !function.IsExported);
    }

    [Fact]
    public void IncludedDeclarationsOutsideScopeAndPrivacyFolders_areIgnored()
    {
        using var scope = new NativeTestWorkspace();
        using var external = new NativeTestWorkspace();
        external.Write(
            "outside.h",
            """
            extern "C" __declspec(dllexport) int outside_api();
            struct OutsideRecord { int value; };
            """);
        scope.Write(
            "PatientData/secret.h",
            """
            extern "C" __declspec(dllexport) int private_api();
            """);
        var sourcePath = scope.Write(
            "native/main.cpp",
            """
            #include "outside.h"
            #include "../PatientData/secret.h"
            extern "C" __declspec(dllexport) int allowed_api();
            """);

        var result = ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            sourcePath,
            scope.Root,
            ProducingFileId: 9,
            InteropTarget.WindowsX64Msvc,
            WindowsX64Arguments(external.Root),
            LibraryName: "medical.dll"));

        result.Diagnostics.Should().NotContain(
            diagnostic =>
                diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
                || diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal);
        result.Exports.Should().ContainSingle(
            nativeExport => nativeExport.ExportName == "allowed_api");
        result.Functions.Should().NotContain(function =>
            function.Name == "outside_api" || function.Name == "private_api");
        result.Types.Should().NotContain(type => type.Name == "OutsideRecord");
    }

    [Fact]
    public void DiagnosticOriginatingInExcludedHeader_isDroppedCompletely()
    {
        using var scope = new NativeTestWorkspace();
        scope.Write(
            "PatientData/private_diagnostic.h",
            """
            #error excluded header diagnostic
            """);
        var sourcePath = scope.Write(
            "native/main.cpp",
            """
            #include "../PatientData/private_diagnostic.h"
            extern "C" __declspec(dllexport) int allowed_api();
            """);

        var result = ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            sourcePath,
            scope.Root,
            ProducingFileId: 11,
            InteropTarget.WindowsX64Msvc,
            WindowsX64Arguments(),
            LibraryName: "medical.dll"));

        result.Diagnostics.Should().BeEmpty();
        result.Exports.Should().ContainSingle(
            nativeExport => nativeExport.ExportName == "allowed_api");
    }

    [Fact]
    public void MissingSource_returnsExplicitDiagnosticWithoutInventedFacts()
    {
        using var workspace = new NativeTestWorkspace();
        var result = ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            Path.Combine(workspace.Root, "missing.cpp"),
            workspace.Root,
            ProducingFileId: 1,
            InteropTarget.WindowsX64Msvc,
            WindowsX64Arguments()));

        result.Functions.Should().BeEmpty();
        result.Types.Should().BeEmpty();
        result.Exports.Should().BeEmpty();
        result.RecordLayouts.Should().BeEmpty();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "CLANG0002"
            && diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error);
    }

    [Fact]
    public void NativeLibraryUnavailable_returnsExplicitFatalDiagnosticWithoutFallback()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/api.cpp",
            """extern "C" __declspec(dllexport) int medical_api();""");
        var request = new ClangNativeExtractionRequest(
            sourcePath,
            workspace.Root,
            ProducingFileId: 2,
            InteropTarget.WindowsX64Msvc,
            WindowsX64Arguments(),
            LibraryName: "medical.dll");

        var result = ClangNativeExtractor.Extract(
            request,
            () => throw new DllNotFoundException("libclang"));

        result.Functions.Should().BeEmpty();
        result.Exports.Should().BeEmpty();
        result.RecordLayouts.Should().BeEmpty();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "CLANG0001"
            && diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal
            && diagnostic.Message.Contains(
                "no textual fallback",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RequestedTargetMismatch_rejectsAllAbiFacts()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/api.cpp",
            """
            extern "C" __declspec(dllexport) int medical_api();
            struct NativeValue { void* pointer; };
            """);

        var result = ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            sourcePath,
            workspace.Root,
            ProducingFileId: 3,
            InteropTarget.WindowsX86Msvc,
            WindowsX64Arguments(),
            LibraryName: "medical.dll"));

        result.Functions.Should().BeEmpty();
        result.Types.Should().BeEmpty();
        result.Exports.Should().BeEmpty();
        result.RecordLayouts.Should().BeEmpty();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "CLANG0004"
            && diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal
            && diagnostic.Message.Contains(
                "win-x86",
                StringComparison.Ordinal));
    }

    private static ClangNativeExtractionResult ExtractWindows(
        string scopeRoot,
        string sourcePath) =>
        ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            sourcePath,
            scopeRoot,
            ProducingFileId: 5,
            InteropTarget.WindowsX64Msvc,
            WindowsX64Arguments()));

    private static string[] WindowsX64Arguments(params string[] includePaths)
    {
        var arguments = new List<string>
        {
            "-x",
            "c++",
            "-std=c++17",
            "--target=x86_64-pc-windows-msvc",
            "-fms-extensions",
            "-D_WIN32=1",
        };
        foreach (var includePath in includePaths)
        {
            arguments.Add("-I");
            arguments.Add(includePath);
        }
        return arguments.ToArray();
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

    private sealed class NativeTestWorkspace : IDisposable
    {
        public NativeTestWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "sourcegraph-clang-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(
                Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
