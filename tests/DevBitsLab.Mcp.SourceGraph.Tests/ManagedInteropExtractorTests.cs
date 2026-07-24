using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Interop;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ManagedInteropExtractorTests
{
    [Fact]
    public void DllImport_extractsTargetAwareSignatureMarshalFactsAndEvidence()
    {
        const string source = """
            using System.Runtime.InteropServices;

            namespace Fixture;

            internal struct NativeInput
            {
                public int Value;
            }

            internal static class Native
            {
                [return: MarshalAs(UnmanagedType.I1)]
                [DllImport(
                    "medalgo",
                    EntryPoint = "medalgo_run",
                    CallingConvention = CallingConvention.Cdecl,
                    CharSet = CharSet.Unicode,
                    SetLastError = true)]
                internal static extern bool Run(
                    [In] in NativeInput input,
                    [Out, MarshalAs(UnmanagedType.LPUTF8Str)] out string text,
                    [MarshalAs(
                        UnmanagedType.LPArray,
                        SizeConst = 4,
                        ArraySubType = UnmanagedType.U2)] ushort[] values);
            }
            """;
        var method = CompileMethod(source, "Fixture.Native", "Run");

        var import = ManagedInteropExtractor.TryExtract(
            method,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 17);

        import.Should().NotBeNull();
        import!.ImportKind.Should().Be(ManagedImportKind.DllImport);
        import.LibraryName.Should().Be("medalgo");
        import.EntryPoint.Should().Be("medalgo_run");
        import.ExactSpelling.Should().BeFalse();
        import.CallingConvention.Should().Be(InteropCallingConvention.Cdecl);
        import.CharacterSet.Should().Be("utf-16");
        import.SetLastError.Should().BeTrue();
        import.SymbolCanonicalKey.Should().StartWith("csharp:M:Fixture.Native.Run");
        import.Evidence.ProducingFileId.Should().Be(17);
        import.Evidence.Producer.Should().Be("roslyn-managed-interop");
        import.Evidence.Confidence.Should().Be(EvidenceConfidence.Semantic);
        import.Evidence.Location.FilePath.Should().Be("Managed.cs");
        import.Evidence.Location.EndColumn.Should()
            .BeGreaterThan(import.Evidence.Location.StartColumn);

        import.ReturnType.Category.Should().Be(AbiTypeCategory.Boolean);
        import.ReturnType.SizeBytes.Should().Be(1);
        import.Parameters.Should().HaveCount(3);

        import.Parameters[0].Direction.Should().Be(AbiParameterDirection.In);
        import.Parameters[0].Type.Category.Should().Be(AbiTypeCategory.Record);
        import.Parameters[0].Type.PointerDepth.Should().Be(1);
        import.Parameters[0].Type.SizeBytes.Should().Be(8);

        import.Parameters[1].Direction.Should().Be(AbiParameterDirection.Out);
        import.Parameters[1].Type.Category.Should().Be(AbiTypeCategory.String);
        import.Parameters[1].Type.PointerDepth.Should().Be(2);
        import.Parameters[1].Type.StringEncoding.Should().Be("utf-8");

        import.Parameters[2].Type.Category.Should().Be(AbiTypeCategory.Array);
        import.Parameters[2].Type.PointerDepth.Should().Be(1);
        import.Parameters[2].Type.FixedArrayLength.Should().Be(4);
        import.Parameters[2].Type.SizeBytes.Should().Be(8);
        import.Parameters.Should().OnlyContain(parameter =>
            parameter.Location.FilePath == "Managed.cs"
            && parameter.Location.StartLine > 0);
    }

    [Fact]
    public void LibraryImport_extractsStringMarshallingAndUnmanagedCallConv()
    {
        const string source = """
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;

            namespace Fixture;

            internal static partial class Native
            {
                [LibraryImport(
                    "medalgo",
                    EntryPoint = "medalgo_text",
                    StringMarshalling = StringMarshalling.Utf8,
                    SetLastError = true)]
                [UnmanagedCallConv(
                    CallConvs = new[] {
                        typeof(CallConvStdcall),
                        typeof(CallConvSuppressGCTransition)
                    })]
                internal static partial int Send(string text);
            }
            """;
        var method = CompileMethod(source, "Fixture.Native", "Send");

        var import = ManagedInteropExtractor.TryExtract(
            method,
            InteropTarget.WindowsX86Msvc,
            producingFileId: 23);

        import.Should().NotBeNull();
        import!.ImportKind.Should().Be(ManagedImportKind.LibraryImport);
        import.ExactSpelling.Should().BeTrue();
        import.CallingConvention.Should().Be(InteropCallingConvention.StdCall);
        import.CharacterSet.Should().Be("utf-8");
        import.Parameters.Should().ContainSingle();
        import.Parameters[0].Type.Category.Should().Be(AbiTypeCategory.String);
        import.Parameters[0].Type.PointerDepth.Should().Be(1);
        import.Parameters[0].Type.SizeBytes.Should().Be(4);
        import.Parameters[0].Type.StringEncoding.Should().Be("utf-8");
    }

    [Fact]
    public void OrdinaryMethod_isNotAnInteropImport()
    {
        const string source = """
            namespace Fixture;
            internal static class Native
            {
                internal static int Run(int value) => value;
            }
            """;
        var method = CompileMethod(source, "Fixture.Native", "Run");

        ManagedInteropExtractor.TryExtract(
                method,
                InteropTarget.WindowsX64Msvc,
                producingFileId: 1)
            .Should().BeNull();
    }

    [Fact]
    public void DllImport_defaultsToAnsiLookup_andExtractsExplicitExactSpelling()
    {
        const string source = """
            using System.Runtime.InteropServices;

            namespace Fixture;

            internal static class Native
            {
                [DllImport("medalgo", ExactSpelling = true)]
                internal static extern int Run();
            }
            """;
        var method = CompileMethod(source, "Fixture.Native", "Run");

        var import = ManagedInteropExtractor.TryExtract(
            method,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 29);

        import.Should().NotBeNull();
        import!.ExactSpelling.Should().BeTrue();
        import.CharacterSet.Should().Be("ansi");
    }

    [Fact]
    public void UnsupportedMarshalAs_remainsOpaque()
    {
        const string source = """
            using System;
            using System.Runtime.InteropServices;

            namespace Fixture;

            internal static class Native
            {
                [DllImport("medalgo")]
                internal static extern void Run(
                    [MarshalAs(UnmanagedType.Currency)] decimal value);
            }
            """;
        var method = CompileMethod(source, "Fixture.Native", "Run");

        var import = ManagedInteropExtractor.TryExtract(
            method,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 31);

        import.Should().NotBeNull();
        import!.Parameters.Should().ContainSingle();
        import.Parameters[0].Type.Category.Should().Be(AbiTypeCategory.Opaque);
    }

    [Fact]
    public void DuplicateMarshalAs_remainsOpaque_insteadOfUsingClrDefault()
    {
        const string source = """
            using System.Runtime.InteropServices;

            namespace Fixture;

            internal static class Native
            {
                [DllImport("medalgo")]
                internal static extern void Run(
                    [MarshalAs(UnmanagedType.I1)]
                    [MarshalAs(UnmanagedType.I4)]
                    int value);
            }
            """;
        var method = CompileMethod(
            source,
            "Fixture.Native",
            "Run",
            "CS0579");

        var import = ManagedInteropExtractor.TryExtract(
            method,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 32);

        import.Should().NotBeNull();
        import!.Parameters.Should().ContainSingle();
        import.Parameters[0].Type.Category.Should().Be(AbiTypeCategory.Opaque);
        import.Parameters[0].Type.SizeBytes.Should().BeNull();
    }

    [Fact]
    public void OverflowingByValTStr_remainsUnknown_withoutThrowing()
    {
        const string source = """
            using System.Runtime.InteropServices;

            namespace Fixture;

            internal struct NativeBuffer
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = int.MaxValue)]
                internal string Text;
            }
            """;
        var field = Compile(source, "CS0599")
            .GetTypeByMetadataName("Fixture.NativeBuffer")!
            .GetMembers("Text")
            .OfType<IFieldSymbol>()
            .Should().ContainSingle().Subject;

        var action = () => ManagedInteropExtractor.MapType(
            field.Type,
            ManagedInteropExtractor.FindMarshalInfo(field.GetAttributes()),
            characterSet: "utf-16",
            InteropTarget.WindowsX64Msvc);

        var type = action.Should().NotThrow().Which;
        type.SizeBytes.Should().BeNull();
        type.FixedArrayLength.Should().Be(int.MaxValue);
    }

    private static IMethodSymbol CompileMethod(
        string source,
        string containingType,
        string methodName,
        params string[] ignoredErrorIds)
    {
        var compilation = Compile(source, ignoredErrorIds);
        return compilation.GetTypeByMetadataName(containingType)!
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Should()
            .ContainSingle()
            .Subject;
    }

    private static CSharpCompilation Compile(
        string source,
        params string[] ignoredErrorIds)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: "Managed.cs");
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException(
                "The test host did not expose trusted platform assemblies.");
        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "ManagedInteropFixture",
            [tree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Id != "CS8795"
                && !ignoredErrorIds.Contains(
                    diagnostic.Id,
                    StringComparer.Ordinal))
            .ToArray();
        errors.Should().BeEmpty();
        return compilation;
    }
}
