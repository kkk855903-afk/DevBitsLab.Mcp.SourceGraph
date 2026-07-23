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

    [Fact]
    public void Interop004_fixtureFacts_reportRetainingNativeAgainstUnrootedManagedCaller()
    {
        const string caller =
            "csharp:M:MedInteropChain.NegativeCases.Interop004.CallbackGcRisk.RegisterUnrootedCallback";
        var callbackBoundary = CallbackBoundary();
        var boundary = callbackBoundary with
        {
            Native = callbackBoundary.Native with
            {
                RetainedCallbacks =
                [
                    new NativeCallbackRetention(
                        0,
                        InteropTarget.WindowsX64Msvc,
                        EvidenceAt(
                            2,
                            "NegativeCases/Interop004/Native.cpp",
                            EvidenceConfidence.Semantic,
                            "clang-dataflow")),
                ],
            },
            CallbackUsages =
            [
                new ManagedCallbackUsage(
                    0,
                    caller,
                    CallbackGcRooting.Unrooted,
                    InteropTarget.WindowsX64Msvc,
                    EvidenceAt(
                        1,
                        "NegativeCases/Interop004/Managed.cs",
                        EvidenceConfidence.Inferred,
                        "roslyn-callback-lifetime")),
            ],
        };

        var finding = new CallbackGcRiskRule().Evaluate(boundary)
            .Should().ContainSingle().Which;

        finding.RuleId.Should().Be("Interop004");
        finding.Severity.Should().Be(InteropFindingSeverity.Warning);
        finding.ManagedSymbolCanonicalKey.Should().Be(caller);
        finding.NativeSymbolCanonicalKey.Should().Be(boundary.Native.SymbolCanonicalKey);
        finding.Confidence.Should().Be(EvidenceConfidence.Inferred);
        finding.Evidence.Should().HaveCount(4);
        finding.Evidence.Select(item => item.Location.FilePath).Should().Contain(
            "NegativeCases/Interop004/Managed.cs",
            "NegativeCases/Interop004/Native.cpp");
    }

    [Fact]
    public void Interop004_requiresBothRetentionAndUnrootedUsageProof()
    {
        var boundary = CallbackBoundary();
        var retention = new NativeCallbackRetention(
            0,
            boundary.Native.Target,
            EvidenceAt(2, "Native.cpp", EvidenceConfidence.Semantic, "clang-dataflow"));
        var rootedUsage = new ManagedCallbackUsage(
            0,
            "csharp:M:Fixture.Rooted",
            CallbackGcRooting.Rooted,
            boundary.Managed.Target,
            EvidenceAt(1, "Managed.cs", EvidenceConfidence.Semantic, "roslyn-dataflow"));
        var unrootedUsage = rootedUsage with
        {
            CallerSymbolCanonicalKey = "csharp:M:Fixture.Unrooted",
            Rooting = CallbackGcRooting.Unrooted,
        };
        var unknownUsage = rootedUsage with
        {
            CallerSymbolCanonicalKey = "csharp:M:Fixture.Unknown",
            Rooting = CallbackGcRooting.Unknown,
        };

        new CallbackGcRiskRule().Evaluate(boundary with
        {
            CallbackUsages = [unrootedUsage],
        }).Should().BeEmpty("native retention is not proven");
        new CallbackGcRiskRule().Evaluate(boundary with
        {
            Native = boundary.Native with { RetainedCallbacks = [retention] },
        }).Should().BeEmpty("a managed unrooted invocation is not proven");
        new CallbackGcRiskRule().Evaluate(boundary with
        {
            Native = boundary.Native with { RetainedCallbacks = [retention] },
            CallbackUsages = [rootedUsage],
        }).Should().BeEmpty("a proven GC root prevents this risk");
        new CallbackGcRiskRule().Evaluate(boundary with
        {
            Native = boundary.Native with { RetainedCallbacks = [retention] },
            CallbackUsages = [unknownUsage],
        }).Should().BeEmpty("unknown rooting must not be treated as unrooted");
    }

    [Fact]
    public void Interop005_fixtureFact_reportsProvenNativeExceptionEscape()
    {
        var boundary = Boundary(
            InteropTarget.WindowsX64Msvc,
            InteropCallingConvention.Cdecl,
            InteropCallingConvention.Cdecl,
            parameters: []);
        boundary = boundary with
        {
            Native = boundary.Native with
            {
                ExceptionEscape = new NativeExceptionEscape(
                    boundary.Native.Target,
                    EvidenceAt(
                        2,
                        "NegativeCases/Interop005/Native.cpp",
                        EvidenceConfidence.Semantic,
                        "clang-exception-flow")),
            },
        };

        var finding = new NativeExceptionRule().Evaluate(boundary)
            .Should().ContainSingle().Which;

        finding.RuleId.Should().Be("Interop005");
        finding.Severity.Should().Be(InteropFindingSeverity.Error);
        finding.ManagedSymbolCanonicalKey.Should().Be(boundary.Managed.SymbolCanonicalKey);
        finding.Confidence.Should().Be(EvidenceConfidence.Semantic);
        finding.Evidence.Should().HaveCount(3);
    }

    [Fact]
    public void Interop006_fixtureFacts_reportAllocatorMismatchAgainstReleaseCaller()
    {
        const string caller =
            "csharp:M:MedInteropChain.NegativeCases.Interop006.AllocatorMismatchRisk.FreeWithWrongAllocator";
        var boundary = Boundary(
            InteropTarget.WindowsX64Msvc,
            InteropCallingConvention.Cdecl,
            InteropCallingConvention.Cdecl,
            parameters: []);
        boundary = boundary with
        {
            Native = boundary.Native with
            {
                ReturnAllocation = new NativeReturnAllocation(
                    InteropAllocatorFamily.CrtHeap,
                    boundary.Native.Target,
                    EvidenceAt(
                        2,
                        "NegativeCases/Interop006/Native.cpp",
                        EvidenceConfidence.Exact,
                        "clang-allocation-flow")),
            },
            ReturnReleases =
            [
                new ManagedReturnRelease(
                    caller,
                    InteropAllocatorFamily.CoTaskMem,
                    boundary.Managed.Target,
                    EvidenceAt(
                        1,
                        "NegativeCases/Interop006/Managed.cs",
                        EvidenceConfidence.Semantic,
                        "roslyn-release-flow")),
            ],
        };

        var finding = new AllocatorMismatchRule().Evaluate(boundary)
            .Should().ContainSingle().Which;

        finding.RuleId.Should().Be("Interop006");
        finding.Severity.Should().Be(InteropFindingSeverity.Warning);
        finding.ManagedSymbolCanonicalKey.Should().Be(caller);
        finding.Confidence.Should().Be(EvidenceConfidence.Semantic);
        finding.Evidence.Should().HaveCount(4);
        finding.Message.Should().Contain(nameof(InteropAllocatorFamily.CrtHeap))
            .And.Contain(nameof(InteropAllocatorFamily.CoTaskMem));
    }

    [Fact]
    public void RiskRules_doNotGuessFromUnknownCompatibleOrWrongTargetFacts()
    {
        var boundary = CallbackBoundary();
        var wrongTarget = InteropTarget.WindowsX86Msvc;
        var retention = new NativeCallbackRetention(
            0,
            wrongTarget,
            EvidenceAt(2, "Native.cpp", EvidenceConfidence.Exact, "clang-dataflow"));
        var unrooted = new ManagedCallbackUsage(
            0,
            "csharp:M:Fixture.Caller",
            CallbackGcRooting.Unrooted,
            boundary.Managed.Target,
            EvidenceAt(1, "Managed.cs", EvidenceConfidence.Exact, "roslyn-dataflow"));
        var sameFamilyRelease = new ManagedReturnRelease(
            "csharp:M:Fixture.Release",
            InteropAllocatorFamily.CrtHeap,
            boundary.Managed.Target,
            EvidenceAt(1, "Managed.cs", EvidenceConfidence.Exact, "roslyn-release-flow"));
        boundary = boundary with
        {
            Native = boundary.Native with
            {
                RetainedCallbacks = [retention],
                ExceptionEscape = new NativeExceptionEscape(
                    wrongTarget,
                    EvidenceAt(
                        2,
                        "Native.cpp",
                        EvidenceConfidence.Exact,
                        "clang-exception-flow")),
                ReturnAllocation = new NativeReturnAllocation(
                    InteropAllocatorFamily.CrtHeap,
                    boundary.Native.Target,
                    EvidenceAt(
                        2,
                        "Native.cpp",
                        EvidenceConfidence.Exact,
                        "clang-allocation-flow")),
            },
            CallbackUsages = [unrooted],
            ReturnReleases = [sameFamilyRelease],
        };

        new CallbackGcRiskRule().Evaluate(boundary).Should().BeEmpty(
            "the retention fact belongs to another ABI target");
        new NativeExceptionRule().Evaluate(boundary).Should().BeEmpty(
            "the escape fact belongs to another ABI target");
        new AllocatorMismatchRule().Evaluate(boundary).Should().BeEmpty(
            "the proven release family is compatible");

        var unknownBoundary = CallbackBoundary();
        var unknown = unknownBoundary with
        {
            Native = unknownBoundary.Native with
            {
                ReturnAllocation = new NativeReturnAllocation(
                    InteropAllocatorFamily.Unknown,
                    unknownBoundary.Native.Target,
                    EvidenceAt(
                        2,
                        "Native.cpp",
                        EvidenceConfidence.Inferred,
                        "clang-allocation-flow")),
            },
            ReturnReleases =
            [
                sameFamilyRelease with
                {
                    ReleaseFamily = InteropAllocatorFamily.CoTaskMem,
                },
            ],
        };
        new CallbackGcRiskRule().Evaluate(unknown).Should().BeEmpty();
        new NativeExceptionRule().Evaluate(unknown).Should().BeEmpty();
        new AllocatorMismatchRule().Evaluate(unknown).Should().BeEmpty();
    }

    [Fact]
    public void Phase2Engine_dispatches001And003Through006ButLeaves002ForPhase3()
    {
        var boundary = CallbackBoundary();
        boundary = boundary with
        {
            Native = boundary.Native with
            {
                RetainedCallbacks =
                [
                    new NativeCallbackRetention(
                        0,
                        boundary.Native.Target,
                        EvidenceAt(2, "Native.cpp", EvidenceConfidence.Exact, "clang-dataflow")),
                ],
                ExceptionEscape = new NativeExceptionEscape(
                    boundary.Native.Target,
                    EvidenceAt(
                        2,
                        "Native.cpp",
                        EvidenceConfidence.Exact,
                        "clang-exception-flow")),
                ReturnAllocation = new NativeReturnAllocation(
                    InteropAllocatorFamily.CrtHeap,
                    boundary.Native.Target,
                    EvidenceAt(
                        2,
                        "Native.cpp",
                        EvidenceConfidence.Exact,
                        "clang-allocation-flow")),
            },
            CallbackUsages =
            [
                new ManagedCallbackUsage(
                    0,
                    "csharp:M:Fixture.CallbackCaller",
                    CallbackGcRooting.Unrooted,
                    boundary.Managed.Target,
                    EvidenceAt(1, "Managed.cs", EvidenceConfidence.Exact, "roslyn-dataflow")),
            ],
            ReturnReleases =
            [
                new ManagedReturnRelease(
                    "csharp:M:Fixture.ReleaseCaller",
                    InteropAllocatorFamily.CoTaskMem,
                    boundary.Managed.Target,
                    EvidenceAt(1, "Managed.cs", EvidenceConfidence.Exact, "roslyn-release-flow")),
            ],
        };

        var findings = InteropRuleEngine.CreatePhase2().Evaluate(boundary);

        findings.Select(item => item.RuleId).Should().Equal(
            "Interop004",
            "Interop005",
            "Interop006");
        findings.Should().NotContain(item => item.RuleId == "Interop002");
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

    private static InteropBoundary CallbackBoundary()
    {
        var callback = new AbiTypeRef(
            "callback(void,int32)",
            AbiTypeCategory.FunctionPointer,
            pointerDepth: 1,
            sizeBytes: InteropTarget.WindowsX64Msvc.PointerSizeBytes,
            alignmentBytes: InteropTarget.WindowsX64Msvc.PointerSizeBytes);
        return Boundary(
            InteropTarget.WindowsX64Msvc,
            InteropCallingConvention.Cdecl,
            InteropCallingConvention.Cdecl,
            managedParameters:
            [
                Parameter(
                    0,
                    "callback",
                    callback,
                    AbiParameterDirection.In,
                    "NegativeCases/Interop004/Managed.cs"),
            ],
            nativeParameters:
            [
                Parameter(
                    0,
                    "callback",
                    callback,
                    AbiParameterDirection.In,
                    "NegativeCases/Interop004/Native.cpp"),
            ]);
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
