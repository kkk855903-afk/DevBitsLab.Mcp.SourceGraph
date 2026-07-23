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
    IReadOnlyList<string>? ExcludePatterns = null);

/// <summary>Pure extraction result. No graph or persistence side effects are performed.</summary>
public sealed record ClangNativeExtractionResult(
    IReadOnlyList<NativeFunctionFact> Functions,
    IReadOnlyList<NativeTypeDeclarationFact> Types,
    IReadOnlyList<NativeExport> Exports,
    IReadOnlyList<AbiRecordLayout> RecordLayouts,
    IReadOnlyList<ClangExtractionDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(
        diagnostic => diagnostic.Severity
            is ClangExtractionDiagnosticSeverity.Error
            or ClangExtractionDiagnosticSeverity.Fatal);
}
