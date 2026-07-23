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
        match.Evidence.Should().HaveCount(2);
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

    private static ManagedImport Managed(
        string library,
        string entryPoint) =>
        new(
            "csharp:M:Fixture.Native.Run",
            ManagedImportKind.DllImport,
            library,
            entryPoint,
            InteropCallingConvention.Cdecl,
            Int32(),
            [],
            CharacterSet: null,
            SetLastError: false,
            InteropTarget.WindowsX64Msvc,
            EvidenceAt(1, "Managed.cs", EvidenceConfidence.Semantic));

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
