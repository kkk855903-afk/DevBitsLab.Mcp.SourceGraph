using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropMatcherTests
{
    [Fact]
    public void ExactEntryPointModuleAndTarget_produceAuditableMatch()
    {
        var managed = Managed("MEDALGO", "run");
        var native = Native("medalgo.dll", "run", "c:E:native.cpp::run");

        var match = new InteropMatcher().Match(managed, [native]);

        match.Status.Should().Be(InteropMatchStatus.Matched);
        match.NativeSymbolCanonicalKey.Should().Be(native.SymbolCanonicalKey);
        match.CandidateCount.Should().Be(1);
        match.Confidence.Should().Be(EvidenceConfidence.Semantic);
        match.Evidence.Should().HaveCount(2);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "Module matches",
            StringComparison.Ordinal));
    }

    [Fact]
    public void EntryPointInDifferentKnownModule_isUnmatched()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [Native("other.dll", "run", "c:E:other.cpp::run")]);

        match.Status.Should().Be(InteropMatchStatus.Unmatched);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        match.CandidateCount.Should().Be(0);
        match.Reasons.Should().ContainSingle(reason => reason.Contains(
            "no candidate belongs",
            StringComparison.Ordinal));
    }

    [Fact]
    public void MissingNativeModule_remainsUnknownRatherThanGuessing()
    {
        var native = Native(null, "run", "c:E:native.cpp::run");

        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [native]);

        match.Status.Should().Be(InteropMatchStatus.Unknown);
        match.Confidence.Should().Be(EvidenceConfidence.Inferred);
        match.CandidateCount.Should().Be(1);
        match.Evidence.Should().HaveCount(2);
    }

    [Fact]
    public void SourceOnlyConfiguredModule_isNotReportedAsBinaryVerifiedMatch()
    {
        var native = Native(
            "medalgo.dll",
            "run",
            "c:E:native.cpp::run") with
        {
            IsBinaryVerified = false,
            ModuleIdentitySource = NativeModuleIdentitySource.Configuration,
        };

        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [native]);

        match.Status.Should().Be(InteropMatchStatus.SourceMatched);
        match.NativeSymbolCanonicalKey.Should().Be(native.SymbolCanonicalKey);
        match.CandidateCount.Should().Be(1);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "not been verified",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownModuleCandidate_preventsFalseUniqueMatch()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [
                Native("medalgo.dll", "run", "c:E:known.cpp::run"),
                Native(null, "run", "c:E:unknown.cpp::run"),
            ]);

        match.Status.Should().Be(InteropMatchStatus.Unknown);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        match.CandidateCount.Should().Be(2);
        match.Evidence.Should().HaveCount(3);
        match.Reasons.Should().ContainSingle(reason => reason.Contains(
            "uniqueness is not proven",
            StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateExactCandidates_areAmbiguous()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [
                Native("medalgo.dll", "run", "c:E:a.cpp::run"),
                Native("medalgo.dll", "run", "c:E:b.cpp::run"),
            ]);

        match.Status.Should().Be(InteropMatchStatus.Ambiguous);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        match.CandidateCount.Should().Be(2);
        match.Evidence.Should().HaveCount(3);
    }

    [Fact]
    public void CandidateForDifferentTarget_isUnknown()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [
                Native(
                    "medalgo.dll",
                    "run",
                    "c:E:native.cpp::run",
                    InteropTarget.WindowsX86Msvc),
            ]);

        match.Status.Should().Be(InteropMatchStatus.Unknown);
        match.CandidateCount.Should().Be(1);
        match.Reasons.Should().ContainSingle(reason => reason.Contains(
            "target ABI",
            StringComparison.Ordinal));
    }

    [Fact]
    public void ExportSpelling_isCaseSensitive()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "Run"),
            [Native("medalgo.dll", "run", "c:E:native.cpp::run")]);

        match.Status.Should().Be(InteropMatchStatus.Unmatched);
    }

    [Fact]
    public void ExactSpelling_onlyAllowsDeclaredEntryPoint()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "run", exactSpelling: true, characterSet: "utf-16"),
            [Native("medalgo.dll", "runW", "c:E:native.cpp::runW")]);

        match.Status.Should().Be(InteropMatchStatus.Unmatched);
        match.Evidence.Should().ContainSingle()
            .Which.Should().Be(Managed(
                "medalgo",
                "run",
                exactSpelling: true,
                characterSet: "utf-16").Evidence);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "exact entry-point spelling 'run'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsX86StdCall_probesUndecoratedBeforeProvenDecoration()
    {
        var managed = Managed(
            "medalgo",
            "run",
            exactSpelling: true,
            characterSet: null,
            target: InteropTarget.WindowsX86Msvc,
            callingConvention: InteropCallingConvention.StdCall,
            parameters:
            [
                Parameter(0, Int32()),
                Parameter(1, Int16()),
            ]);
        var plain = Native(
            "medalgo.dll",
            "run",
            "c:E:plain.cpp::run",
            InteropTarget.WindowsX86Msvc);
        var decorated = Native(
            "medalgo.dll",
            "_run@8",
            "c:E:decorated.cpp::_run@8",
            InteropTarget.WindowsX86Msvc);

        new InteropMatcher()
            .Match(managed, [decorated, plain])
            .NativeSymbolCanonicalKey.Should().Be(plain.SymbolCanonicalKey);

        var fallback = new InteropMatcher().Match(managed, [decorated]);
        fallback.Status.Should().Be(InteropMatchStatus.Matched);
        fallback.NativeSymbolCanonicalKey.Should().Be(
            decorated.SymbolCanonicalKey);
        fallback.Reasons.Should().Contain(reason => reason.Contains(
            "@8",
            StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsX86UnicodeStdCall_decoratesEachRuntimeLookupStep()
    {
        var managed = Managed(
            "medalgo",
            "run",
            exactSpelling: false,
            characterSet: "utf-16",
            target: InteropTarget.WindowsX86Msvc,
            callingConvention: InteropCallingConvention.StdCall,
            parameters: [Parameter(0, Int32())]);
        var wideDecorated = Native(
            "medalgo.dll",
            "_runW@4",
            "c:E:wide.cpp::_runW@4",
            InteropTarget.WindowsX86Msvc);
        var plain = Native(
            "medalgo.dll",
            "run",
            "c:E:plain.cpp::run",
            InteropTarget.WindowsX86Msvc);

        var match = new InteropMatcher().Match(
            managed,
            [plain, wideDecorated]);

        match.Status.Should().Be(InteropMatchStatus.Matched);
        match.NativeSymbolCanonicalKey.Should().Be(
            wideDecorated.SymbolCanonicalKey);
    }

    [Fact]
    public void UnknownX86StdCallStackSize_doesNotGuessDecorationOrAbsence()
    {
        var managed = Managed(
            "medalgo",
            "run",
            exactSpelling: true,
            characterSet: null,
            target: InteropTarget.WindowsX86Msvc,
            callingConvention: InteropCallingConvention.StdCall,
            parameters:
            [
                Parameter(
                    0,
                    new AbiTypeRef(
                        "UnknownRecord",
                        AbiTypeCategory.Record)),
            ]);
        var decorated = Native(
            "medalgo.dll",
            "_run@12",
            "c:E:native.cpp::_run@12",
            InteropTarget.WindowsX86Msvc);

        var match = new InteropMatcher().Match(managed, [decorated]);

        match.Status.Should().Be(InteropMatchStatus.Unknown);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        match.Reasons.Should().Contain(reason => reason.Contains(
            "stack-byte count is unknown",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowsAnsiLookup_usesOriginalNameBeforeAFallback()
    {
        var original = Native("medalgo.dll", "run", "c:E:native.cpp::run");
        var fallback = Native("medalgo.dll", "runA", "c:E:native.cpp::runA");

        var match = new InteropMatcher().Match(
            Managed("medalgo", "run", exactSpelling: false, characterSet: "ansi"),
            [fallback, original]);

        match.Status.Should().Be(InteropMatchStatus.Matched);
        match.NativeSymbolCanonicalKey.Should().Be(original.SymbolCanonicalKey);
        match.Evidence.Should().Equal(
            Managed(
                "medalgo",
                "run",
                exactSpelling: false,
                characterSet: "ansi").Evidence,
            original.Evidence);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "'run', then 'runA'",
            StringComparison.Ordinal));
        match.Reasons.Should().Contain(reason => reason.Contains(
            "resolves to 'run'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsAnsiLookup_fallsBackToASuffix()
    {
        var fallback = Native("medalgo.dll", "runA", "c:E:native.cpp::runA");

        var match = new InteropMatcher().Match(
            Managed("medalgo", "run", exactSpelling: false, characterSet: "ansi"),
            [fallback]);

        match.Status.Should().Be(InteropMatchStatus.Matched);
        match.NativeSymbolCanonicalKey.Should().Be(fallback.SymbolCanonicalKey);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "resolves to 'runA'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsUnicodeLookup_prefersWSuffix_independentOfCandidateOrder()
    {
        var fallback = Native("medalgo.dll", "run", "c:E:native.cpp::run");
        var wide = Native("medalgo.dll", "runW", "c:E:native.cpp::runW");

        var match = new InteropMatcher().Match(
            Managed("medalgo", "run", exactSpelling: false, characterSet: "utf-16"),
            [fallback, wide]);

        match.Status.Should().Be(InteropMatchStatus.Matched);
        match.NativeSymbolCanonicalKey.Should().Be(wide.SymbolCanonicalKey);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "'runW', then 'run'",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, false, "Character set")]
    [InlineData("ansi", null, "lookup policy")]
    public void UnknownLookupFacts_remainUnknown(
        string? characterSet,
        bool? exactSpelling,
        string expectedReason)
    {
        var managed = Managed(
            "medalgo",
            "run",
            exactSpelling,
            characterSet);

        var match = new InteropMatcher().Match(
            managed,
            [
                Native("medalgo.dll", "run", "c:E:native.cpp::run"),
                Native("medalgo.dll", "runA", "c:E:native.cpp::runA"),
            ]);

        match.Status.Should().Be(InteropMatchStatus.Unknown);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        match.Confidence.Should().Be(EvidenceConfidence.Inferred);
        match.Evidence.Should().Equal(managed.Evidence);
        match.Reasons.Should().ContainSingle(reason => reason.Contains(
            expectedReason,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicateCandidatesAtRuntimeSelectedSpelling_areAmbiguous()
    {
        var first = Native("medalgo.dll", "runW", "c:E:a.cpp::runW");
        var second = Native("medalgo.dll", "runW", "c:E:b.cpp::runW");

        var match = new InteropMatcher().Match(
            Managed("medalgo", "run", exactSpelling: false, characterSet: "utf-16"),
            [Native("medalgo.dll", "run", "c:E:fallback.cpp::run"), first, second]);

        match.Status.Should().Be(InteropMatchStatus.Ambiguous);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        match.Evidence.Should().Equal(
            Managed(
                "medalgo",
                "run",
                exactSpelling: false,
                characterSet: "utf-16").Evidence,
            first.Evidence,
            second.Evidence);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "runtime-selected spelling 'runW'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void NonWindowsLookup_doesNotProbeWindowsSuffixes()
    {
        var target = new InteropTarget(
            "linux-x64",
            InteropArchitecture.X64,
            InteropCompilerAbi.Itanium,
            pointerSizeBytes: 8,
            defaultPack: 8);
        var managed = Managed(
            "medalgo.so",
            "run",
            exactSpelling: false,
            characterSet: "ansi",
            target);

        var match = new InteropMatcher().Match(
            managed,
            [Native("medalgo.so", "runA", "c:E:native.cpp::runA", target)]);

        match.Status.Should().Be(InteropMatchStatus.Unmatched);
        match.Evidence.Should().Equal(managed.Evidence);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "without Windows A/W suffix probing",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("wrong-module")]
    public void IncompleteSnapshot_cannotProveAbsence(string candidateShape)
    {
        var exports = candidateShape switch
        {
            "none" => Array.Empty<NativeExport>(),
            "wrong-module" =>
            [
                Native("other.dll", "run", "c:E:other.cpp::run"),
            ],
            _ => throw new InvalidOperationException(),
        };

        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            exports,
            isExportUniverseComplete: false);

        match.Status.Should().Be(InteropMatchStatus.Unknown);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        match.Confidence.Should().Be(EvidenceConfidence.Inferred);
        match.Reasons.Should().Contain(reason => reason.Contains(
            "snapshot is incomplete",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IncompleteSnapshot_preservesConcretePositiveMatch()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [Native("medalgo.dll", "run", "c:E:native.cpp::run")],
            isExportUniverseComplete: false);

        match.Status.Should().Be(InteropMatchStatus.Matched);
        match.NativeSymbolCanonicalKey.Should().Be(
            "c:E:native.cpp::run");
        match.Reasons.Should().Contain(reason => reason.Contains(
            "successfully indexed projects",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IncompleteSnapshot_canStillProveAmbiguity()
    {
        var match = new InteropMatcher().Match(
            Managed("medalgo", "run"),
            [
                Native("medalgo.dll", "run", "c:E:a.cpp::run"),
                Native("medalgo.dll", "run", "c:E:b.cpp::run"),
            ],
            isExportUniverseComplete: false);

        match.Status.Should().Be(InteropMatchStatus.Ambiguous);
        match.NativeSymbolCanonicalKey.Should().BeNull();
    }

    private static ManagedImport Managed(
        string library,
        string entryPoint,
        bool? exactSpelling = true,
        string? characterSet = null,
        InteropTarget? target = null,
        InteropCallingConvention callingConvention =
            InteropCallingConvention.Cdecl,
        IReadOnlyList<AbiParameter>? parameters = null) =>
        new(
            "csharp:M:Fixture.Native.Run",
            ManagedImportKind.DllImport,
            library,
            entryPoint,
            callingConvention,
            Int32(),
            parameters ?? [],
            CharacterSet: characterSet,
            SetLastError: false,
            target ?? InteropTarget.WindowsX64Msvc,
            EvidenceAt(1, "Managed.cs", EvidenceConfidence.Semantic))
        {
            ExactSpelling = exactSpelling,
        };

    private static NativeExport Native(
        string? library,
        string export,
        string canonicalKey,
        InteropTarget? target = null) =>
        new(
            canonicalKey,
            export,
            InteropCallingConvention.Cdecl,
            Int32(),
            [],
            HasCLinkage: true,
            IsBinaryVerified: true,
            target ?? InteropTarget.WindowsX64Msvc,
            EvidenceAt(2, NativePath(canonicalKey), EvidenceConfidence.Exact))
        {
            LibraryName = library,
        };

    private static string NativePath(string canonicalKey)
    {
        var pathStart = canonicalKey.IndexOf("E:", StringComparison.Ordinal) + 2;
        var pathEnd = canonicalKey.IndexOf("::", pathStart, StringComparison.Ordinal);
        return canonicalKey[pathStart..pathEnd];
    }

    private static AbiTypeRef Int32() =>
        new(
            "int32",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static AbiTypeRef Int16() =>
        new(
            "int16",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 2,
            alignmentBytes: 2,
            isSigned: true);

    private static AbiParameter Parameter(
        int position,
        AbiTypeRef type) =>
        new(
            position,
            $"p{position}",
            type,
            AbiParameterDirection.In,
            new SourceLocation(
                "Managed.cs",
                position + 2,
                1,
                position + 2,
                5));

    private static Evidence EvidenceAt(
        long fileId,
        string path,
        EvidenceConfidence confidence) =>
        new(
            fileId,
            new SourceLocation(path, 1, 1, 1, 5),
            confidence,
            path.EndsWith(".cs", StringComparison.Ordinal)
                ? "roslyn-managed-interop"
                : "clang");
}
