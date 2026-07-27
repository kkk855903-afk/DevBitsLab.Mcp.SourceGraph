using ClangSharp.Interop;
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
        var fixtureNativeRoot = Path.Combine(
            repoRoot,
            "tests",
            "fixtures",
            "MedInteropChain",
            "NativeLibrary");
        using var workspace = new NativeTestWorkspace();
        var nativeRoot = Path.Combine(workspace.Root, "native");
        var sourcePath = workspace.Write(
            "native/src/exports.cpp",
            File.ReadAllText(Path.Combine(fixtureNativeRoot, "src", "exports.cpp")));
        workspace.Write(
            "native/src/algorithm.hpp",
            File.ReadAllText(Path.Combine(fixtureNativeRoot, "src", "algorithm.hpp")));
        var algorithmPath = workspace.Write(
            "native/src/algorithm.cpp",
            File.ReadAllText(Path.Combine(fixtureNativeRoot, "src", "algorithm.cpp")));
        workspace.Write(
            "native/include/medalgo.h",
            File.ReadAllText(Path.Combine(fixtureNativeRoot, "include", "medalgo.h")));
        var cstdintPath = workspace.Write(
            "native/toolchain/cstdint",
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
        nativeExport.ExceptionEscape.Should().BeNull(
            "a throwable callee inside try/catch is not direct escape proof");
        nativeExport.RetainedCallbacks.Should().BeEmpty();
        nativeExport.ReturnAllocation.Should().BeNull();
        result.IsCallGraphComplete.Should().BeTrue();
        var directCall = result.Calls.Should()
            .ContainSingle(call =>
                call.ReferencedDeclarationUsr.Contains(
                    "Calculate",
                    StringComparison.Ordinal))
            .Subject;
        directCall.CallerSymbolCanonicalKey.Should().Be(
            nativeExport.SymbolCanonicalKey);
        directCall.CalleeSymbolCanonicalKey.Should().BeNull(
            "the referenced definition lives in another translation unit");
        directCall.Evidence.Producer.Should().Be("clang-native-call");

        var algorithm = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                algorithmPath,
                nativeRoot,
                ProducingFileId: 42,
                InteropTarget.WindowsX64Msvc,
                WindowsX64Arguments(
                    Path.GetDirectoryName(cstdintPath)!,
                    Path.Combine(nativeRoot, "include"),
                    Path.Combine(nativeRoot, "src")),
                LibraryName: "medalgo.dll"));
        var calculateDefinition = algorithm.Functions.Should()
            .ContainSingle(function =>
                function.IsDefinition
                && function.QualifiedName == "Algorithm::Calculate")
            .Subject;
        directCall.ReferencedDeclarationUsr.Should().Be(
            calculateDefinition.DeclarationUsr,
            "Clang USRs bind a referenced declaration to its out-of-line definition");

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
    public void MedInterop_negative_fixtures_extract_exact_native_risk_facts()
    {
        var negativeRoot = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "MedInteropChain",
            "NegativeCases");
        using var workspace = new NativeTestWorkspace();
        var callbackPath = workspace.Write(
            "native/Interop004.cpp",
            File.ReadAllText(Path.Combine(
                negativeRoot,
                "Interop004",
                "Native.cpp")));
        var exceptionPath = workspace.Write(
            "native/Interop005.cpp",
            File.ReadAllText(Path.Combine(
                negativeRoot,
                "Interop005",
                "Native.cpp")));
        var allocationPath = workspace.Write(
            "native/Interop006.cpp",
            File.ReadAllText(Path.Combine(
                negativeRoot,
                "Interop006",
                "Native.cpp")));
        var toolchainDirectory = Path.GetDirectoryName(workspace.Write(
            "native/toolchain/stdexcept",
            """
            #pragma once
            namespace std {
            class runtime_error {
            public:
                explicit runtime_error(const char*);
            };
            }
            """))!;
        workspace.Write(
            "native/toolchain/cstdlib",
            """
            #pragma once
            using size_t = unsigned long long;
            extern "C" void* malloc(size_t);
            extern "C" void* calloc(size_t, size_t);
            extern "C" void* realloc(void*, size_t);
            namespace std {
            using ::malloc;
            using ::calloc;
            using ::realloc;
            }
            """);

        ClangNativeExtractionResult Extract(string sourcePath) =>
            ClangNativeExtractor.Extract(
                new ClangNativeExtractionRequest(
                    sourcePath,
                    workspace.Root,
                    ProducingFileId: 51,
                    InteropTarget.WindowsX64Msvc,
                    WindowsX64Arguments(toolchainDirectory),
                    LibraryName: "risk.dll"));

        var callbackResult = Extract(callbackPath);
        var callbackExport = callbackResult.Exports.Should()
            .ContainSingle(export =>
                export.ExportName == "risk_register_callback")
            .Subject;
        var retention = callbackExport.RetainedCallbacks.Should()
            .ContainSingle()
            .Subject;
        retention.ParameterPosition.Should().Be(0);
        retention.Target.Should().BeSameAs(InteropTarget.WindowsX64Msvc);
        retention.Evidence.Confidence.Should().Be(EvidenceConfidence.Exact);
        retention.Evidence.Producer.Should().Be("clang-native-retention");
        retention.Evidence.Location.FilePath.Should().Be(callbackPath);
        retention.Evidence.Location.StartLine.Should().Be(7);
        retention.Evidence.Metadata.Should().Contain(
            "parameterPosition",
            "0");
        retention.Evidence.Metadata.Should().Contain(
            "target",
            "win-x64");

        var exceptionResult = Extract(exceptionPath);
        var exceptionExport = exceptionResult.Exports.Should()
            .ContainSingle(export => export.ExportName == "risk_throws")
            .Subject;
        exceptionExport.ExceptionEscape.Should().NotBeNull();
        exceptionExport.ExceptionEscape!.Target.Should()
            .BeSameAs(InteropTarget.WindowsX64Msvc);
        exceptionExport.ExceptionEscape.Evidence.Producer.Should()
            .Be("clang-native-exception");
        exceptionExport.ExceptionEscape.Evidence.Location.FilePath.Should()
            .Be(exceptionPath);
        exceptionExport.ExceptionEscape.Evidence.Location.StartLine.Should()
            .Be(5);

        var allocationResult = Extract(allocationPath);
        var allocationExport = allocationResult.Exports.Should()
            .ContainSingle(export => export.ExportName == "risk_allocate")
            .Subject;
        allocationExport.ReturnAllocation.Should().NotBeNull();
        allocationExport.ReturnAllocation!.AllocatorFamily.Should()
            .Be(InteropAllocatorFamily.CrtHeap);
        allocationExport.ReturnAllocation.Target.Should()
            .BeSameAs(InteropTarget.WindowsX64Msvc);
        allocationExport.ReturnAllocation.Evidence.Producer.Should()
            .Be("clang-native-allocation");
        allocationExport.ReturnAllocation.Evidence.Location.FilePath.Should()
            .Be(allocationPath);
        allocationExport.ReturnAllocation.Evidence.Location.StartLine.Should()
            .Be(5);
    }

    [Fact]
    public void Native_risk_facts_remain_unknown_without_direct_proof()
    {
        using var workspace = new NativeTestWorkspace();
        var toolchainDirectory = Path.GetDirectoryName(workspace.Write(
            "native/toolchain/cstdlib",
            """
            #pragma once
            using size_t = unsigned long long;
            extern "C" void* malloc(size_t);
            namespace std { using ::malloc; }
            """))!;
        var sourcePath = workspace.Write(
            "native/conservative.cpp",
            """
            #include <cstdlib>

            #define API extern "C" __declspec(dllexport)
            using Callback = void (*)(int);

            int throwing_helper() { throw 1; }
            void* allocation_wrapper() { return std::malloc(8); }

            API int caught_throw()
            {
                try { throw 2; }
                catch (...) { return 0; }
            }

            API int called_throw()
            {
                return throwing_helper();
            }

            API int lambda_throw()
            {
                auto deferred = []() { throw 3; };
                return 0;
            }

            API void local_assignment(Callback callback)
            {
                Callback local = nullptr;
                local = callback;
            }

            API void local_static_assignment(Callback& callback)
            {
                static Callback retained = nullptr;
                retained = callback;
            }

            API void lambda_assignment(Callback callback)
            {
                static Callback retained = nullptr;
                auto deferred = [=]() { retained = callback; };
            }

            API void* wrapped_allocation()
            {
                return allocation_wrapper();
            }

            namespace fake { void* malloc(size_t); }
            API void* unrelated_malloc()
            {
                return fake::malloc(8);
            }

            API void* global_malloc()
            {
                return ::malloc(8);
            }
            """);

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                workspace.Root,
                ProducingFileId: 52,
                InteropTarget.WindowsX64Msvc,
                WindowsX64Arguments(toolchainDirectory),
                LibraryName: "risk.dll"));

        result.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
            || diagnostic.Severity
                == ClangExtractionDiagnosticSeverity.Fatal);
        result.Exports.Single(export =>
                export.ExportName == "caught_throw")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "called_throw")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "lambda_throw")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "local_assignment")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export =>
                export.ExportName == "lambda_assignment")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export =>
                export.ExportName == "local_static_assignment")
            .RetainedCallbacks.Should().ContainSingle(retention =>
                retention.ParameterPosition == 0);
        result.Exports.Single(export =>
                export.ExportName == "wrapped_allocation")
            .ReturnAllocation.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "unrelated_malloc")
            .ReturnAllocation.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "global_malloc")
            .ReturnAllocation.Should().BeNull(
                "an unqualified global declaration cannot prove standard CRT ownership");
    }

    [Fact]
    public void Native_callback_and_exception_facts_require_exit_path_proof()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/control-flow.cpp",
            """
            #define API extern "C" __declspec(dllexport)
            using Callback = void (*)(int);

            static Callback cleared_callback;
            static Callback overwritten_callback;
            static Callback conditional_callback;
            static Callback helper_callback;
            static Callback alias_callback;
            static Callback argument_callback;
            static Callback short_callback;
            static Callback first_callback;
            static Callback second_callback;

            void clear_helper_callback()
            {
                helper_callback = nullptr;
            }

            void clear_argument_callback(Callback)
            {
                argument_callback = nullptr;
            }

            API void cleared(Callback callback)
            {
                cleared_callback = callback;
                cleared_callback = nullptr;
            }

            API void overwritten(Callback callback, Callback replacement)
            {
                overwritten_callback = callback;
                overwritten_callback = replacement;
            }

            API void conditional(Callback callback)
            {
                if (false)
                {
                    conditional_callback = callback;
                }
            }

            API void cleared_by_helper(Callback callback)
            {
                helper_callback = callback;
                clear_helper_callback();
            }

            API void cleared_by_alias(Callback callback)
            {
                alias_callback = callback;
                Callback& alias = alias_callback;
                alias = nullptr;
            }

            API void cleared_by_call_argument(Callback callback)
            {
                clear_argument_callback(argument_callback = callback);
            }

            API bool short_circuit_retention(Callback callback)
            {
                return false && ((short_callback = callback) != nullptr);
            }

            API void retained_twice(Callback first, Callback second)
            {
                first_callback = first;
                second_callback = second;
            }

            API int direct_throw()
            {
                throw 1;
            }

            API int noexcept_throw() noexcept
            {
                throw 2;
            }

            API int unreachable_throw()
            {
                return 0;
                throw 3;
            }

            API int conditional_throw()
            {
                if (false)
                {
                    throw 4;
                }
                return 0;
            }

            API bool short_circuit_throw()
            {
                return false && (throw 8, true);
            }

            API int catch_rethrow()
            {
                try
                {
                    throw 5;
                }
                catch (...)
                {
                    throw;
                }
            }

            API int outer_catch_translates_rethrow()
            {
                try
                {
                    try
                    {
                        throw 6;
                    }
                    catch (...)
                    {
                        throw;
                    }
                }
                catch (...)
                {
                    return 0;
                }
            }

            API int earlier_handler_translates_throw()
            {
                try
                {
                    throw 7;
                }
                catch (int)
                {
                    return 0;
                }
                catch (...)
                {
                    throw;
                }
            }
            """);

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                workspace.Root,
                ProducingFileId: 53,
                InteropTarget.WindowsX64Msvc,
                WindowsX64Arguments(),
                LibraryName: "control.dll"));

        result.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Severity
                == ClangExtractionDiagnosticSeverity.Error
            || diagnostic.Severity
                == ClangExtractionDiagnosticSeverity.Fatal);
        result.Exports.Single(export => export.ExportName == "cleared")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export => export.ExportName == "overwritten")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export => export.ExportName == "conditional")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export =>
                export.ExportName == "cleared_by_helper")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export =>
                export.ExportName == "cleared_by_alias")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export =>
                export.ExportName == "cleared_by_call_argument")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export =>
                export.ExportName == "short_circuit_retention")
            .RetainedCallbacks.Should().BeEmpty();
        result.Exports.Single(export => export.ExportName == "retained_twice")
            .RetainedCallbacks.Select(retention =>
                retention.ParameterPosition)
            .Should().Equal(0, 1);

        result.Exports.Single(export =>
                export.ExportName == "direct_throw")
            .ExceptionEscape.Should().NotBeNull();
        result.Exports.Single(export =>
                export.ExportName == "noexcept_throw")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "unreachable_throw")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "conditional_throw")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "short_circuit_throw")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "catch_rethrow")
            .ExceptionEscape.Should().NotBeNull();
        result.Exports.Single(export =>
                export.ExportName == "outer_catch_translates_rethrow")
            .ExceptionEscape.Should().BeNull();
        result.Exports.Single(export =>
                export.ExportName == "earlier_handler_translates_throw")
            .ExceptionEscape.Should().BeNull();
    }

    [Fact]
    public void Crt_allocator_fact_requires_standard_reference_and_exact_external_signature()
    {
        using var workspace = new NativeTestWorkspace();
        var toolchainDirectory = Path.GetDirectoryName(workspace.Write(
            "native/toolchain/cstdlib",
            """
            #pragma once
            using size_t = unsigned long long;
            extern "C" void* malloc(size_t);
            namespace std { using ::malloc; }
            """))!;

        ClangNativeExtractionResult Extract(
            string fileName,
            string source,
            bool withToolchain = false)
        {
            var sourcePath = workspace.Write(
                "native/" + fileName,
                source);
            return ClangNativeExtractor.Extract(
                new ClangNativeExtractionRequest(
                    sourcePath,
                    workspace.Root,
                    ProducingFileId: 54,
                    InteropTarget.WindowsX64Msvc,
                    withToolchain
                        ? WindowsX64Arguments(toolchainDirectory)
                        : WindowsX64Arguments(),
                    LibraryName: "allocator.dll"));
        }

        var standard = Extract(
            "standard.cpp",
            """
            #include <cstdlib>
            extern "C" __declspec(dllexport) void* standard_allocate()
            {
                return std::malloc(8);
            }
            """,
            withToolchain: true);
        var unqualified = Extract(
            "unqualified.cpp",
            """
            extern "C" void* malloc(unsigned long long);
            extern "C" __declspec(dllexport) void* unqualified_allocate()
            {
                return ::malloc(8);
            }
            """);
        var sameSourceDeclaration = Extract(
            "same-source.cpp",
            """
            extern "C" void* malloc(unsigned long long);
            namespace std { using ::malloc; }
            extern "C" __declspec(dllexport) void* same_source_allocate()
            {
                return std::malloc(8);
            }
            """);
        var userDefined = Extract(
            "defined.cpp",
            """
            extern "C" void* malloc(unsigned long long)
            {
                return nullptr;
            }
            namespace std { using ::malloc; }
            extern "C" __declspec(dllexport) void* defined_allocate()
            {
                return std::malloc(8);
            }
            """);
        var variadic = Extract(
            "variadic.cpp",
            """
            extern "C" void* malloc(unsigned long long, ...);
            namespace std { using ::malloc; }
            extern "C" __declspec(dllexport) void* variadic_allocate()
            {
                return std::malloc(8);
            }
            """);
        var signed = Extract(
            "signed.cpp",
            """
            extern "C" void* malloc(long long);
            namespace std { using ::malloc; }
            extern "C" __declspec(dllexport) void* signed_allocate()
            {
                return std::malloc(8);
            }
            """);
        var narrow = Extract(
            "narrow.cpp",
            """
            extern "C" void* malloc(unsigned int);
            namespace std { using ::malloc; }
            extern "C" __declspec(dllexport) void* narrow_allocate()
            {
                return std::malloc(8);
            }
            """);
        var char32Path = workspace.Write(
            "native/char32.cpp",
            """
            extern "C" void* malloc(char32_t);
            namespace std { using ::malloc; }
            extern "C" __declspec(dllexport) void* char32_allocate()
            {
                return std::malloc(8);
            }
            """);
        var char32 = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                char32Path,
                workspace.Root,
                ProducingFileId: 55,
                InteropTarget.WindowsX86Msvc,
                WindowsX86Arguments(),
                LibraryName: "allocator.dll"));

        standard.Exports.Single().ReturnAllocation.Should()
            .Match<NativeReturnAllocation>(allocation =>
                allocation.AllocatorFamily
                == InteropAllocatorFamily.CrtHeap);
        unqualified.Exports.Single().ReturnAllocation.Should().BeNull();
        sameSourceDeclaration.Exports.Single()
            .ReturnAllocation.Should().BeNull();
        userDefined.Exports.Single().ReturnAllocation.Should().BeNull();
        variadic.Exports.Single().ReturnAllocation.Should().BeNull();
        signed.Exports.Single().ReturnAllocation.Should().BeNull();
        narrow.Exports.Single().ReturnAllocation.Should().BeNull();
        char32.Exports.Single().ReturnAllocation.Should().BeNull();
    }

    [Fact]
    public void Crt_allocator_fact_rejects_source_symlinked_to_standard_header_name()
    {
        using var workspace = new NativeTestWorkspace();
        var physicalSourcePath = workspace.Write(
            "include/cstdlib",
            """
            using size_t = unsigned long long;
            extern "C" void* malloc(size_t);
            namespace std { using ::malloc; }
            extern "C" __declspec(dllexport) void* forged_allocate()
            {
                return std::malloc(8);
            }
            """);
        var aliasPath = Path.Combine(
            workspace.Root,
            "native",
            "forged.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(aliasPath)!);
        try
        {
            File.CreateSymbolicLink(aliasPath, physicalSourcePath);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or IOException
                or NotSupportedException)
        {
            return;
        }

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                aliasPath,
                workspace.Root,
                ProducingFileId: 56,
                InteropTarget.WindowsX64Msvc,
                WindowsX64Arguments(
                    Path.GetDirectoryName(physicalSourcePath)!),
                LibraryName: "allocator.dll"));

        result.HasErrors.Should().BeFalse();
        result.Exports.Should().ContainSingle()
            .Which.ReturnAllocation.Should().BeNull();
    }

    [Fact]
    public void Crt_allocator_header_name_uses_platform_path_case_semantics()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new NativeTestWorkspace();
        var includeDirectory = Path.GetDirectoryName(workspace.Write(
            "include/CSTDLIB",
            """
            #pragma once
            using size_t = unsigned long long;
            extern "C" void* malloc(size_t);
            namespace std { using ::malloc; }
            """))!;
        var sourcePath = workspace.Write(
            "native/case-sensitive.cpp",
            """
            #include <CSTDLIB>
            extern "C" __attribute__((visibility("default")))
            void* forged_allocate()
            {
                return std::malloc(8);
            }
            """);

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                workspace.Root,
                ProducingFileId: 57,
                new InteropTarget(
                    "linux-x64",
                    InteropArchitecture.X64,
                    InteropCompilerAbi.Itanium,
                    pointerSizeBytes: 8,
                    defaultPack: 8),
                [
                    "-x",
                    "c++",
                    "-std=c++17",
                    "--target=x86_64-unknown-linux-gnu",
                    "-I",
                    includeDirectory,
                ],
                LibraryName: "allocator.so"));

        result.HasErrors.Should().BeFalse();
        result.Exports.Should().ContainSingle()
            .Which.ReturnAllocation.Should().BeNull();
    }

    [Fact]
    public void Indirect_call_is_diagnostic_only_and_marks_projection_partial()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/indirect.cpp",
            """
            using Callback = int(*)(int);
            int invoke(Callback callback, int value)
            {
                return callback(value);
            }
            """);

        var result = ExtractWindows(workspace.Root, sourcePath);

        result.Calls.Should().BeEmpty(
            "an indirect target must never be guessed from its spelling or type");
        result.IsCallGraphComplete.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "CLANG2000"
            && diagnostic.Severity
                == ClangExtractionDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Uninvoked_lambda_calls_are_not_attributed_to_enclosing_function()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/lambda.cpp",
            """
            int deferred_work() { return 7; }
            int configure()
            {
                auto deferred = []() { return deferred_work(); };
                return 0;
            }
            """);

        var result = ExtractWindows(workspace.Root, sourcePath);

        result.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
            || diagnostic.Severity
                == ClangExtractionDiagnosticSeverity.Fatal);
        var deferredWork = result.Functions.Should()
            .ContainSingle(function =>
                function.Name == "deferred_work" && function.IsDefinition)
            .Subject;
        var configure = result.Functions.Should()
            .ContainSingle(function =>
                function.Name == "configure" && function.IsDefinition)
            .Subject;
        result.Calls.Should().NotContain(call =>
            call.CallerSymbolCanonicalKey == configure.GraphCanonicalKey
            && call.CalleeSymbolCanonicalKey
                == deferredWork.GraphCanonicalKey);
        result.IsCallGraphComplete.Should().BeTrue();
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
    public void Cpp_method_cv_and_ref_qualifiers_keep_distinct_keys_and_calls()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/qualified-methods.cpp",
            """
            struct Qualified {
                int f() { return 1; }
                int f() const { return 2; }
                int ref() & { return 3; }
                int ref() && { return 4; }
            };

            int call_qualified(
                Qualified& left,
                Qualified&& right,
                const Qualified& constant)
            {
                return left.f()
                    + constant.f()
                    + left.ref()
                    + static_cast<Qualified&&>(right).ref();
            }
            """);

        var result = ExtractWindows(workspace.Root, sourcePath);

        var mutableF = result.Functions.Single(function =>
            function.SymbolCanonicalKey.EndsWith(
                "::Qualified::f()",
                StringComparison.Ordinal));
        var constF = result.Functions.Single(function =>
            function.SymbolCanonicalKey.EndsWith(
                "::Qualified::f() const",
                StringComparison.Ordinal));
        var lvalueRef = result.Functions.Single(function =>
            function.SymbolCanonicalKey.EndsWith(
                "::Qualified::ref() &",
                StringComparison.Ordinal));
        var rvalueRef = result.Functions.Single(function =>
            function.SymbolCanonicalKey.EndsWith(
                "::Qualified::ref() &&",
                StringComparison.Ordinal));
        var caller = result.Functions.Single(function =>
            function.Name == "call_qualified");

        result.Functions.Where(function => function.Name == "f")
            .Should().HaveCount(2);
        result.Functions.Where(function => function.Name == "ref")
            .Should().HaveCount(2);
        result.Calls
            .Where(call =>
                call.CallerSymbolCanonicalKey == caller.GraphCanonicalKey)
            .Select(call => call.CalleeSymbolCanonicalKey)
            .Should().BeEquivalentTo(
                mutableF.GraphCanonicalKey,
                constF.GraphCanonicalKey,
                lvalueRef.GraphCanonicalKey,
                rvalueRef.GraphCanonicalKey);
        result.IsCallGraphComplete.Should().BeTrue();
    }

    [Fact]
    public void Forward_declaration_and_definition_keep_the_definition_projection()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/forward.cpp",
            """
            int helper(int value);
            int helper(int value) { return value; }
            int run() { return helper(1); }
            """);

        var result = ExtractWindows(workspace.Root, sourcePath);

        result.Diagnostics.Should().NotContain(
            diagnostic =>
                diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
                || diagnostic.Severity
                    == ClangExtractionDiagnosticSeverity.Fatal);
        result.Functions.Should().ContainSingle(function =>
            function.Name == "helper" && function.IsDefinition);
        result.Calls.Should().ContainSingle(call =>
            call.CalleeSymbolCanonicalKey != null
            && call.CalleeSymbolCanonicalKey.Contains(
                "::helper(",
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
    public void NestedIncludes_areTrackedTransitivelyWithTranslationUnitAndStableOrdering()
    {
        using var workspace = new NativeTestWorkspace();
        var nestedPath = workspace.Write(
            "native/include/detail/nested.h",
            """
            #pragma once
            struct NestedValue { int value; };
            """);
        var publicPath = workspace.Write(
            "native/include/public.h",
            """
            #pragma once
            #include "detail/nested.h"
            """);
        var sourcePath = workspace.Write(
            "native/main.cpp",
            """
            #include "include/public.h"
            extern "C" __declspec(dllexport) int allowed_api(NestedValue value);
            """);

        var first = ExtractWindows(workspace.Root, sourcePath);
        var second = ExtractWindows(workspace.Root, sourcePath);

        first.Diagnostics.Should().NotContain(
            diagnostic =>
                diagnostic.Severity == ClangExtractionDiagnosticSeverity.Error
                || diagnostic.Severity == ClangExtractionDiagnosticSeverity.Fatal);
        first.IncludedFiles.Should().Equal(
            new[] { sourcePath, publicPath, nestedPath }
                .Select(Path.GetFullPath)
                .OrderBy(path => path, PathComparer)
                .ThenBy(path => path, StringComparer.Ordinal));
        second.IncludedFiles.Should().Equal(first.IncludedFiles);
        first.IncludedFiles.Distinct(PathComparer)
            .Should().HaveSameCount(first.IncludedFiles);
    }

    [Fact]
    public void RepositoryRelativeIncludeDirectory_isNormalizedBeforeParse()
    {
        using var workspace = new NativeTestWorkspace();
        var headerPath = workspace.Write(
            "include/api.h",
            "#pragma once");
        var sourcePath = workspace.Write(
            "native/main.cpp",
            """
            #include <api.h>
            extern "C" __declspec(dllexport) int allowed_api();
            """);

        var result = ClangNativeExtractor.Extract(new ClangNativeExtractionRequest(
            sourcePath,
            workspace.Root,
            ProducingFileId: 8,
            InteropTarget.WindowsX64Msvc,
            WindowsX64Arguments("include"),
            LibraryName: "medical.dll"));

        result.HasErrors.Should().BeFalse();
        result.IncludedFiles.Should().Contain(Path.GetFullPath(headerPath));
    }

    [Fact]
    public void IncludedFiles_resolveAllowedHeaderSymlinkToItsPhysicalTarget()
    {
        using var workspace = new NativeTestWorkspace();
        var targetPath = workspace.Write(
            "include/actual.h",
            "#pragma once");
        var aliasPath = Path.Combine(workspace.Root, "include", "alias.h");
        try
        {
            File.CreateSymbolicLink(aliasPath, targetPath);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or IOException
                or NotSupportedException)
        {
            return;
        }
        var sourcePath = workspace.Write(
            "native/main.cpp",
            """
            #include "../include/alias.h"
            extern "C" __declspec(dllexport) int allowed_api();
            """);

        var result = ExtractWindows(workspace.Root, sourcePath);

        result.HasErrors.Should().BeFalse();
        result.IncludedFiles.Should().Contain(Path.GetFullPath(targetPath));
        result.IncludedFiles.Should().NotContain(Path.GetFullPath(aliasPath));
    }

    [Fact]
    public void OutOfRootIncludeDirectory_isRejectedBeforeLibclangParse()
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
        var parseAttempted = false;

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                scope.Root,
                ProducingFileId: 9,
                InteropTarget.WindowsX64Msvc,
                WindowsX64Arguments(external.Root),
                LibraryName: "medical.dll"),
            () =>
            {
                parseAttempted = true;
                return CXIndex.Create(
                    excludeDeclarationsFromPch: true,
                    displayDiagnostics: false);
            });

        parseAttempted.Should().BeFalse();
        result.HasErrors.Should().BeTrue();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "CLANG0005");
        result.Functions.Should().BeEmpty();
        result.Exports.Should().BeEmpty();
        result.IncludedFiles.Should().BeEmpty();
    }

    [Fact]
    public void ExcludedHeader_isRejectedBeforeLibclangParse()
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
        var parseAttempted = false;

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                scope.Root,
                ProducingFileId: 11,
                InteropTarget.WindowsX64Msvc,
                WindowsX64Arguments(),
                LibraryName: "medical.dll"),
            () =>
            {
                parseAttempted = true;
                return CXIndex.Create(
                    excludeDeclarationsFromPch: true,
                    displayDiagnostics: false);
            });

        parseAttempted.Should().BeFalse();
        result.HasErrors.Should().BeTrue();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "CLANG0005"
            && !diagnostic.Message.Contains(
                "PatientData",
                StringComparison.OrdinalIgnoreCase));
        result.Exports.Should().BeEmpty();
        result.IncludedFiles.Should().BeEmpty();
    }

    [Fact]
    public void AbsoluteOutOfRootHeader_isRejectedBeforeLibclangParse()
    {
        using var scope = new NativeTestWorkspace();
        using var external = new NativeTestWorkspace();
        var externalHeader = external.Write(
            "outside.h",
            "struct OutsideRecord { int value; };");
        var sourcePath = scope.Write(
            "native/main.cpp",
            $$"""
            #include "{{externalHeader.Replace('\\', '/')}}"
            extern "C" __declspec(dllexport) int allowed_api();
            """);
        var parseAttempted = false;

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                scope.Root,
                ProducingFileId: 12,
                InteropTarget.WindowsX64Msvc,
                WindowsX64Arguments(),
                LibraryName: "medical.dll"),
            () =>
            {
                parseAttempted = true;
                return CXIndex.Create(
                    excludeDeclarationsFromPch: true,
                    displayDiagnostics: false);
            });

        parseAttempted.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "CLANG0005");
        result.Functions.Should().BeEmpty();
        result.IncludedFiles.Should().BeEmpty();
    }

    [Theory]
    [InlineData("@compiler.rsp")]
    [InlineData("-include")]
    [InlineData("--sysroot=C:/toolchain")]
    [InlineData("-isystem")]
    [InlineData("-Xclang")]
    [InlineData("-fmodule-map-file=module.modulemap")]
    [InlineData("/FIprivate.h")]
    public void UnprovablePathBearingCompilerArgument_isRejectedBeforeLibclangParse(
        string dangerousArgument)
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/main.cpp",
            """extern "C" __declspec(dllexport) int allowed_api();""");
        var parseAttempted = false;
        var arguments = WindowsX64Arguments()
            .Append(dangerousArgument)
            .ToArray();

        var result = ClangNativeExtractor.Extract(
            new ClangNativeExtractionRequest(
                sourcePath,
                workspace.Root,
                ProducingFileId: 13,
                InteropTarget.WindowsX64Msvc,
                arguments,
                LibraryName: "medical.dll"),
            () =>
            {
                parseAttempted = true;
                return CXIndex.Create(
                    excludeDeclarationsFromPch: true,
                    displayDiagnostics: false);
            });

        parseAttempted.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "CLANG0005");
        result.IncludedFiles.Should().BeEmpty();
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
        result.IncludedFiles.Should().BeEmpty();
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

    [Fact]
    public void Injected_class_name_does_not_duplicate_record_declaration()
    {
        using var workspace = new NativeTestWorkspace();
        var sourcePath = workspace.Write(
            "native/camera.h",
            """
            using HRESULT = long;
            using BOOL = int;
            using PgUInt8 = unsigned char;
            using PgUInt32 = unsigned int;
            using PgUInt64 = unsigned long long;
            using PgInt64 = long long;
            #define PG_CAMERA_API extern "C" __declspec(dllexport)

            struct PgCameraFormat
            {
                PgUInt32 width;
                PgUInt32 height;
                PgUInt32 stride;
                PgUInt32 frame_bytes;
            };

            PG_CAMERA_API HRESULT __cdecl pg_camera_start(
                void* camera,
                PgUInt32 camera_index,
                PgUInt32 requested_width,
                PgUInt32 requested_height,
                PgUInt32 requested_fps,
                PgCameraFormat* actual_format);
            """);

        var result = ExtractWindows(workspace.Root, sourcePath);

        result.Types.Where(type => type.Name == "PgCameraFormat")
            .Should().ContainSingle()
            .Which.QualifiedName.Should().Be("PgCameraFormat");
        result.RecordLayouts.Where(layout =>
                layout.SymbolCanonicalKey.EndsWith(
                    "::PgCameraFormat",
                    StringComparison.Ordinal))
            .Should().ContainSingle();
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

    private static string[] WindowsX86Arguments(params string[] includePaths)
    {
        var arguments = new List<string>
        {
            "-x",
            "c++",
            "-std=c++17",
            "--target=i686-pc-windows-msvc",
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

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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
