namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// Normalized, third-party-independent domain objects used at managed/native, ABI, and gRPC
/// boundaries. Adapters from Roslyn, Clang, PE metadata, and protobuf descriptors must convert
/// into these objects before rule evaluation.
/// </summary>
public enum InteropArchitecture
{
    X86,
    X64,
    Arm64,
}

public enum InteropCompilerAbi
{
    Msvc,
    Itanium,
}

public enum InteropCallingConvention
{
    Unknown,
    PlatformDefault,
    Cdecl,
    StdCall,
    ThisCall,
    FastCall,
    VectorCall,
}

public enum ManagedImportKind
{
    DllImport,
    LibraryImport,
}

public enum AbiTypeCategory
{
    Void,
    Boolean,
    SignedInteger,
    UnsignedInteger,
    FloatingPoint,
    Enum,
    Record,
    Pointer,
    FunctionPointer,
    String,
    Array,
    Opaque,
}

public enum AbiParameterDirection
{
    In,
    Out,
    InOut,
}

public enum AbiRecordKind
{
    Sequential,
    Explicit,
    Native,
}

public enum InteropMatchStatus
{
    Matched,
    Unmatched,
    Ambiguous,
    Unknown,
}

public enum InteropCompatibility
{
    Unknown,
    Compatible,
    Warning,
    Error,
}

public enum InteropFindingSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One explicit ABI evaluation target. ABI conclusions are meaningless without this provenance;
/// in particular C <c>long</c>, pointers, default packing, and calling conventions vary by target.
/// </summary>
public sealed record InteropTarget
{
    public InteropTarget(
        string runtimeIdentifier,
        InteropArchitecture architecture,
        InteropCompilerAbi compilerAbi,
        int pointerSizeBytes,
        int defaultPack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        if (pointerSizeBytes is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pointerSizeBytes),
                pointerSizeBytes,
                "Pointer size must be 4 or 8 bytes.");
        }
        if (defaultPack is < 1 or > 128 || (defaultPack & (defaultPack - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultPack),
                defaultPack,
                "Default pack must be a power of two between 1 and 128.");
        }

        RuntimeIdentifier = runtimeIdentifier;
        Architecture = architecture;
        CompilerAbi = compilerAbi;
        PointerSizeBytes = pointerSizeBytes;
        DefaultPack = defaultPack;
    }

    public string RuntimeIdentifier { get; }
    public InteropArchitecture Architecture { get; }
    public InteropCompilerAbi CompilerAbi { get; }
    public int PointerSizeBytes { get; }
    public int DefaultPack { get; }

    public static InteropTarget WindowsX64Msvc { get; } =
        new("win-x64", InteropArchitecture.X64, InteropCompilerAbi.Msvc, 8, 8);

    public static InteropTarget WindowsX86Msvc { get; } =
        new("win-x86", InteropArchitecture.X86, InteropCompilerAbi.Msvc, 4, 8);
}

/// <summary>
/// A normalized type as it crosses an ABI. Unknown size/alignment is represented by
/// <see langword="null"/> and must propagate to an Unknown result rather than being guessed.
/// </summary>
public sealed record AbiTypeRef
{
    public AbiTypeRef(
        string canonicalName,
        AbiTypeCategory category,
        int pointerDepth = 0,
        int? sizeBytes = null,
        int? alignmentBytes = null,
        bool? isSigned = null,
        string? stringEncoding = null,
        int? fixedArrayLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        ArgumentOutOfRangeException.ThrowIfNegative(pointerDepth);
        if (sizeBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                sizeBytes,
                "Known ABI sizes must be positive.");
        }
        if (alignmentBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alignmentBytes),
                alignmentBytes,
                "Known ABI alignments must be positive.");
        }
        if (fixedArrayLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixedArrayLength),
                fixedArrayLength,
                "Known fixed-array lengths must be positive.");
        }

        CanonicalName = canonicalName;
        Category = category;
        PointerDepth = pointerDepth;
        SizeBytes = sizeBytes;
        AlignmentBytes = alignmentBytes;
        IsSigned = isSigned;
        StringEncoding = stringEncoding;
        FixedArrayLength = fixedArrayLength;
    }

    public string CanonicalName { get; }
    public AbiTypeCategory Category { get; }
    public int PointerDepth { get; }
    public int? SizeBytes { get; }
    public int? AlignmentBytes { get; }
    public bool? IsSigned { get; }
    public string? StringEncoding { get; }
    public int? FixedArrayLength { get; }
}

public sealed record AbiParameter(
    int Position,
    string Name,
    AbiTypeRef Type,
    AbiParameterDirection Direction,
    SourceLocation Location);

/// <summary>Normalized DllImport/LibraryImport declaration.</summary>
public sealed record ManagedImport(
    string SymbolCanonicalKey,
    ManagedImportKind ImportKind,
    string LibraryName,
    string EntryPoint,
    InteropCallingConvention CallingConvention,
    AbiTypeRef ReturnType,
    IReadOnlyList<AbiParameter> Parameters,
    string? CharacterSet,
    bool SetLastError,
    InteropTarget Target,
    Evidence Evidence);

/// <summary>Normalized exported C ABI function, independent of the parser that discovered it.</summary>
public sealed record NativeExport(
    string SymbolCanonicalKey,
    string ExportName,
    InteropCallingConvention CallingConvention,
    AbiTypeRef ReturnType,
    IReadOnlyList<AbiParameter> Parameters,
    bool HasCLinkage,
    bool IsBinaryVerified,
    InteropTarget Target,
    Evidence Evidence);

public sealed record AbiFieldLayout(
    int Order,
    string Name,
    AbiTypeRef Type,
    int? OffsetBytes,
    int? SizeBytes,
    Evidence Evidence);

/// <summary>Managed or native record layout for one explicit target ABI.</summary>
public sealed record AbiRecordLayout(
    string SymbolCanonicalKey,
    AbiRecordKind Kind,
    int? SizeBytes,
    int? AlignmentBytes,
    int? Pack,
    IReadOnlyList<AbiFieldLayout> Fields,
    InteropTarget Target,
    Evidence Evidence);

/// <summary>
/// Provenance-bearing boundary match. <see cref="Reasons"/> explains why a candidate matched,
/// failed, or remained unknown; consumers must not infer certainty from symbol-name similarity.
/// </summary>
public sealed record InteropMatch(
    string ManagedSymbolCanonicalKey,
    string? NativeSymbolCanonicalKey,
    InteropMatchStatus Status,
    EvidenceConfidence Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<Evidence> Evidence);

public sealed record AbiCompatibilityResult(
    string ManagedSymbolCanonicalKey,
    string NativeSymbolCanonicalKey,
    InteropCompatibility Compatibility,
    IReadOnlyList<string> Differences,
    EvidenceConfidence Confidence,
    IReadOnlyList<Evidence> Evidence);

/// <summary>Stable rule-engine result for Interop001–Interop006 and future rule packs.</summary>
public sealed record InteropFinding(
    string RuleId,
    InteropFindingSeverity Severity,
    string Message,
    string? ManagedSymbolCanonicalKey,
    string? NativeSymbolCanonicalKey,
    EvidenceConfidence Confidence,
    IReadOnlyList<Evidence> Evidence);
