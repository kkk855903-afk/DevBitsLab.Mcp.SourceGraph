using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Clang;

/// <summary>The native declaration kinds retained by the Clang adapter.</summary>
public enum NativeTypeDeclarationKind
{
    Struct,
    Union,
    Enum,
    Typedef,
}

/// <summary>Severity of a parser or adapter diagnostic.</summary>
public enum ClangExtractionDiagnosticSeverity
{
    Info,
    Warning,
    Error,
    Fatal,
}

/// <summary>
/// One source-backed native function declaration. This includes functions that are not ABI
/// exports so callers can distinguish a C++ overload or ordinary declaration from a proven
/// exported C entry point.
/// </summary>
public sealed record NativeFunctionFact(
    string SymbolCanonicalKey,
    string Name,
    string QualifiedName,
    InteropCallingConvention CallingConvention,
    AbiTypeRef ReturnType,
    IReadOnlyList<AbiParameter> Parameters,
    bool HasCLinkage,
    bool IsExported,
    bool IsDefinition,
    Evidence Evidence)
{
    /// <summary>
    /// Clang's declaration identity. Unlike a spelling, the USR binds an out-of-line definition
    /// to the declaration referenced by a call in another translation unit.
    /// </summary>
    public string DeclarationUsr { get; init; } = string.Empty;

    /// <summary>
    /// Graph endpoint used by direct calls. Exported C definitions use their <c>c:E:</c> key so
    /// a managed P/Invoke edge can continue through the native call graph; all other definitions
    /// use <see cref="SymbolCanonicalKey"/>.
    /// </summary>
    public string GraphCanonicalKey { get; init; } = SymbolCanonicalKey;

    /// <summary>Whether this declaration is a C++ member function rather than a free function.</summary>
    public bool IsMethod { get; init; }

    /// <summary>The ABI target under which Clang parsed this declaration.</summary>
    public InteropTarget? Target { get; init; }
}

/// <summary>
/// One source-backed direct call. <see cref="ReferencedDeclarationUsr"/> is always the exact
/// declaration referenced by Clang. <see cref="CalleeSymbolCanonicalKey"/> remains null when the
/// definition is in another translation unit and is resolved only by the content-bound snapshot
/// aggregator; indirect and unresolved calls are never represented by this record.
/// </summary>
public sealed record NativeCallFact(
    string CallerSymbolCanonicalKey,
    string ReferencedDeclarationUsr,
    string? CalleeSymbolCanonicalKey,
    InteropTarget Target,
    Evidence Evidence);

/// <summary>
/// One named struct, union, enum, or typedef declaration. <see cref="DeclaredType"/> preserves
/// Clang's target-specific size and alignment; incomplete or invalid types remain unknown.
/// </summary>
public sealed record NativeTypeDeclarationFact(
    string SymbolCanonicalKey,
    NativeTypeDeclarationKind Kind,
    string Name,
    string QualifiedName,
    AbiTypeRef DeclaredType,
    bool IsDefinition,
    Evidence Evidence);

/// <summary>A diagnostic returned by libclang or by request/target validation.</summary>
public sealed record ClangExtractionDiagnostic(
    string Code,
    ClangExtractionDiagnosticSeverity Severity,
    string Message,
    SourceLocation? Location = null);

/// <summary>
/// Explicit inputs for one Clang translation unit. Compiler arguments are passed directly to
/// libclang; callers should include their real <c>-x</c>, <c>--target</c>, include paths, macros,
/// language standard, and ABI-affecting switches.
/// </summary>
public sealed record ClangNativeExtractionRequest(
    string SourceFilePath,
    string ScopeRoot,
    long ProducingFileId,
    InteropTarget Target,
    IReadOnlyList<string> CompilerArguments,
    string? LibraryName = null,
    IReadOnlyList<string>? ExcludePatterns = null)
{
    /// <summary>
    /// Runtime-discovered compiler and platform-SDK include roots. Files below these roots may
    /// satisfy system includes, but are omitted from repository evidence and content hashes.
    /// </summary>
    public IReadOnlyList<string> SystemIncludeDirectories { get; init; } = [];

    /// <summary>
    /// Optional, bounded logical file views prepared by the trusted parent process. This is used
    /// only when an endpoint-protection filter exposes protected physical bytes to the isolated
    /// native worker. Paths remain subject to the same scope and exclusion checks, and contents
    /// are consumed in memory through libclang unsaved files.
    /// </summary>
    public IReadOnlyList<ClangInMemoryInput> InMemoryInputs { get; init; } = [];
}

public sealed record ClangInMemoryInput(string Path, byte[] Contents);

/// <summary>Pure extraction result. No graph or persistence side effects are performed.</summary>
public sealed record ClangNativeExtractionResult(
    IReadOnlyList<NativeFunctionFact> Functions,
    IReadOnlyList<NativeTypeDeclarationFact> Types,
    IReadOnlyList<NativeExport> Exports,
    IReadOnlyList<AbiRecordLayout> RecordLayouts,
    IReadOnlyList<ClangExtractionDiagnostic> Diagnostics)
{
    /// <summary>Direct, source-backed call occurrences discovered in this translation unit.</summary>
    public IReadOnlyList<NativeCallFact> Calls { get; init; } =
        Array.Empty<NativeCallFact>();

    /// <summary>
    /// False when traversal encountered an indirect/unresolved call or exceeded a hard bound.
    /// Consumers must retain their prior complete call projection in that case.
    /// </summary>
    public bool IsCallGraphComplete { get; init; } = true;

    /// <summary>
    /// Stable, deduplicated physical paths that own this translation unit's dependency graph.
    /// A successful extraction includes the translation-unit source file itself as well as every
    /// transitively included repository file observed by libclang. Paths that cannot be proven
    /// inside the scope/privacy boundary are never returned.
    /// </summary>
    public IReadOnlyList<string> IncludedFiles { get; init; } = Array.Empty<string>();

    public bool HasErrors => Diagnostics.Any(
        diagnostic => diagnostic.Severity
            is ClangExtractionDiagnosticSeverity.Error
            or ClangExtractionDiagnosticSeverity.Fatal);
}
