using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropRuleEngineTests
{
    [Fact]
    public void Interop001_reportsX86CdeclStdCallMismatchWithBothEvidenceLocations()
    {
        var boundary = Boundary(
            InteropTarget.WindowsX86Msvc,
            InteropCallingConvention.StdCall,
            InteropCallingConvention.Cdecl,
            parameters: []);

        var findings = new CallingConventionRule().Evaluate(boundary);

        var finding = findings.Should().ContainSingle().Which;
        finding.RuleId.Should().Be("Interop001");
        finding.Severity.Should().Be(InteropFindingSeverity.Error);
        finding.Confidence.Should().Be(EvidenceConfidence.Semantic);
        finding.Evidence.Select(item => item.Location.FilePath)
            .Should().BeEquivalentTo("Managed.cs", "Native.cpp");
    }

    [Fact]
    public void Interop001_treatsOrdinaryCallingConventionTokensAsUnifiedOnWindowsX64()
    {
        var boundary = Boundary(
            InteropTarget.WindowsX64Msvc,
            InteropCallingConvention.StdCall,
            InteropCallingConvention.Cdecl,
            parameters: []);

        new CallingConventionRule().Evaluate(boundary).Should().BeEmpty();
    }

    [Fact]
    public void Interop003_reportsCountDirectionPointerWidthSignednessAndEncodingRisks()
    {
        var managedString = new AbiTypeRef(
            "managed-string",
            AbiTypeCategory.String,
            pointerDepth: 0,
            sizeBytes: 8,
            isSigned: true,
            stringEncoding: "utf-8");
        var nativeString = new AbiTypeRef(
            "wchar_t*",
            AbiTypeCategory.String,
            pointerDepth: 1,
            sizeBytes: 4,
            isSigned: false,
            stringEncoding: "utf-16");
        var boundary = Boundary(
            InteropTarget.WindowsX86Msvc,
            InteropCallingConvention.Cdecl,
            InteropCallingConvention.Cdecl,
            managedParameters:
            [
                Parameter(0, "text", managedString, AbiParameterDirection.In, "Managed.cs"),
                Parameter(1, "extra", Int32(), AbiParameterDirection.In, "Managed.cs"),
            ],
            nativeParameters:
            [
                Parameter(0, "text", nativeString, AbiParameterDirection.Out, "Native.cpp"),
            ]);

        var findings = new ParameterTypeRiskRule().Evaluate(boundary);

        findings.Should().HaveCount(6);
        findings.Should().OnlyContain(finding =>
            finding.RuleId == "Interop003"
            && finding.Evidence.Count >= 2);
        findings.Select(finding => finding.Message).Should()
            .Contain(message => message.Contains("Parameter count mismatch", StringComparison.Ordinal))
            .And.Contain(message => message.Contains("direction mismatch", StringComparison.Ordinal))
            .And.Contain(message => message.Contains("pointer depth mismatch", StringComparison.Ordinal))
            .And.Contain(message => message.Contains("size mismatch", StringComparison.Ordinal))
            .And.Contain(message => message.Contains("signedness", StringComparison.Ordinal))
            .And.Contain(message => message.Contains("encoding mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Phase2Engine_rejectsDuplicateRuleIds()
    {
        var act = () => new InteropRuleEngine(
            [new CallingConventionRule(), new CallingConventionRule()]);

        act.Should().Throw<ArgumentException>().WithMessage("*unique*");
    }

    [Fact]
    public void Interop003_surfacesUnknownOpaqueTypesInsteadOfAssumingCompatibility()
    {
        var opaque = new AbiTypeRef("custom-marshaler", AbiTypeCategory.Opaque);
        var boundary = Boundary(
            InteropTarget.WindowsX64Msvc,
            InteropCallingConvention.Cdecl,
            InteropCallingConvention.Cdecl,
            managedParameters:
            [
                Parameter(0, "value", opaque, AbiParameterDirection.In, "Managed.cs"),
            ],
            nativeParameters:
            [
                Parameter(0, "value", opaque, AbiParameterDirection.In, "Native.cpp"),
            ]);

        var finding = new ParameterTypeRiskRule().Evaluate(boundary)
            .Should().ContainSingle().Which;

        finding.Severity.Should().Be(InteropFindingSeverity.Warning);
        finding.Confidence.Should().Be(EvidenceConfidence.Inferred);
        finding.Message.Should().Contain("unknown");
    }

    private static InteropBoundary Boundary(
        InteropTarget target,
        InteropCallingConvention managedConvention,
        InteropCallingConvention nativeConvention,
        IReadOnlyList<AbiParameter>? parameters = null,
        IReadOnlyList<AbiParameter>? managedParameters = null,
        IReadOnlyList<AbiParameter>? nativeParameters = null)
    {
        var managedEvidence = EvidenceAt(
            1,
            "Managed.cs",
            EvidenceConfidence.Exact,
            "managed-interop");
        var nativeEvidence = EvidenceAt(
            2,
            "Native.cpp",
            EvidenceConfidence.Semantic,
            "clang");
        var managed = new ManagedImport(
            "csharp:M:Risk",
            ManagedImportKind.DllImport,
            "medalgo",
            "risk",
            managedConvention,
            Int32(),
            managedParameters ?? parameters ?? [],
            CharacterSet: null,
            SetLastError: false,
            target,
            managedEvidence);
        var native = new NativeExport(
            "c:E:Native.cpp::risk",
            "risk",
            nativeConvention,
            Int32(),
            nativeParameters ?? parameters ?? [],
            HasCLinkage: true,
            IsBinaryVerified: true,
            target,
            nativeEvidence);
        return new InteropBoundary(managed, native);
    }

    private static AbiTypeRef Int32() =>
        new(
            "int32",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static AbiParameter Parameter(
        int position,
        string name,
        AbiTypeRef type,
        AbiParameterDirection direction,
        string path) =>
        new(position, name, type, direction, new SourceLocation(path, 5, 10, 5, 20));

    private static Evidence EvidenceAt(
        long fileId,
        string path,
        EvidenceConfidence confidence,
        string producer) =>
        new(
            fileId,
            new SourceLocation(path, 2, 1, 8, 2),
            confidence,
            producer);
}
