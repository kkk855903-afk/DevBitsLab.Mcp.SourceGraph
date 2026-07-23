using ClangSharp;
using ClangSharp.Interop;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation = DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Clang;

/// <summary>
/// Extracts target-aware C/C++ declarations and ABI layouts from a real libclang translation
/// unit. The adapter never infers a module name, export, layout, or unknown type from text.
/// </summary>
public static class ClangNativeExtractor
{
    private const string Producer = "clang-native";
    private const string NativeUnavailableCode = "CLANG0001";
    private const string InvalidRequestCode = "CLANG0002";
    private const string ParseFailedCode = "CLANG0003";
    private const string TargetMismatchCode = "CLANG0004";
    private const string ClangDiagnosticCode = "CLANG1000";

    private static readonly ClangNativeExtractionResult _emptyResult = new(
        Array.Empty<NativeFunctionFact>(),
        Array.Empty<NativeTypeDeclarationFact>(),
        Array.Empty<NativeExport>(),
        Array.Empty<AbiRecordLayout>(),
        Array.Empty<ClangExtractionDiagnostic>());

    /// <summary>
    /// Parses one translation unit and returns only declarations whose physical paths are inside
    /// the requested scope and pass its mandatory privacy/configured exclusions.
    /// </summary>
    public static ClangNativeExtractionResult Extract(
        ClangNativeExtractionRequest request) =>
        Extract(
            request,
            static () => CXIndex.Create(
                excludeDeclarationsFromPch: true,
                displayDiagnostics: false));

    internal static ClangNativeExtractionResult Extract(
        ClangNativeExtractionRequest request,
        Func<CXIndex> createIndex)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(createIndex);

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return WithDiagnostic(validation);
        }

        var sourceFilePath = Path.GetFullPath(request.SourceFilePath);
        var scopeRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.ScopeRoot));
        var scopePolicy = new ScopePathPolicy(scopeRoot, request.ExcludePatterns);

        try
        {
            using var index = createIndex();
            var error = CXTranslationUnit.TryParse(
                index,
                sourceFilePath,
                request.CompilerArguments.ToArray(),
                ReadOnlySpan<CXUnsavedFile>.Empty,
                CXTranslationUnit_Flags.CXTranslationUnit_KeepGoing,
                out var handle);
            if (error != CXErrorCode.CXError_Success)
            {
                return WithDiagnostic(new ClangExtractionDiagnostic(
                    ParseFailedCode,
                    ClangExtractionDiagnosticSeverity.Fatal,
                    $"libclang could not parse the translation unit ({error})."));
            }

            TranslationUnit? translationUnit = null;
            try
            {
                translationUnit = TranslationUnit.GetOrCreate(handle);
                using (translationUnit)
                {
                    var diagnostics = ReadDiagnostics(handle, scopePolicy);
                    var targetDiagnostic = ValidateTranslationUnitTarget(
                        handle,
                        request.Target);
                    if (targetDiagnostic is not null)
                    {
                        diagnostics.Add(targetDiagnostic);
                        return new ClangNativeExtractionResult(
                            Array.Empty<NativeFunctionFact>(),
                            Array.Empty<NativeTypeDeclarationFact>(),
                            Array.Empty<NativeExport>(),
                            Array.Empty<AbiRecordLayout>(),
                            diagnostics);
                    }

                    var collector = new Collector(
                        request,
                        scopeRoot,
                        scopePolicy,
                        diagnostics);
                    collector.Visit(translationUnit.TranslationUnitDecl.Decls);
                    return collector.BuildResult();
                }
            }
            finally
            {
                // TranslationUnit owns the handle after GetOrCreate succeeds. Dispose the raw
                // handle only when wrapping itself failed, avoiding both leaks and double free.
                if (translationUnit is null)
                {
                    handle.Dispose();
                }
            }
        }
        catch (Exception ex) when (IsNativeAvailabilityFailure(ex))
        {
            return WithDiagnostic(new ClangExtractionDiagnostic(
                NativeUnavailableCode,
                ClangExtractionDiagnosticSeverity.Fatal,
                "Clang native libraries are unavailable or incompatible; no textual fallback was used."));
        }
        catch (Exception ex)
        {
            return WithDiagnostic(new ClangExtractionDiagnostic(
                ParseFailedCode,
                ClangExtractionDiagnosticSeverity.Fatal,
                $"Clang extraction failed ({ex.GetType().Name}): {ex.Message}"));
        }
    }

    private static ClangExtractionDiagnostic? ValidateRequest(
        ClangNativeExtractionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceFilePath)
            || !Path.IsPathFullyQualified(request.SourceFilePath))
        {
            return InvalidRequest("SourceFilePath must be an absolute path.");
        }
        if (string.IsNullOrWhiteSpace(request.ScopeRoot)
            || !Path.IsPathFullyQualified(request.ScopeRoot))
        {
            return InvalidRequest("ScopeRoot must be an absolute path.");
        }
        if (request.ProducingFileId <= 0)
        {
            return InvalidRequest("ProducingFileId must be positive.");
        }
        if (request.Target is null)
        {
            return InvalidRequest("Target is required.");
        }
        if (request.CompilerArguments is null
            || request.CompilerArguments.Any(argument => argument is null))
        {
            return InvalidRequest("CompilerArguments must not contain null values.");
        }
        if (request.LibraryName is not null
            && string.IsNullOrWhiteSpace(request.LibraryName))
        {
            return InvalidRequest("LibraryName must be null or non-blank.");
        }

        try
        {
            var sourceFilePath = Path.GetFullPath(request.SourceFilePath);
            var scopeRoot = Path.GetFullPath(request.ScopeRoot);
            var policy = new ScopePathPolicy(scopeRoot, request.ExcludePatterns);
            if (!File.Exists(sourceFilePath))
            {
                return InvalidRequest("SourceFilePath does not exist.");
            }
            if (policy.IsExcluded(sourceFilePath))
            {
                return InvalidRequest(
                    "SourceFilePath is outside the allowed scope or is excluded.");
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            return InvalidRequest($"The extraction path is invalid: {ex.Message}");
        }

        return null;
    }

    private static ClangExtractionDiagnostic InvalidRequest(string message) =>
        new(
            InvalidRequestCode,
            ClangExtractionDiagnosticSeverity.Error,
            message);

    private static ClangNativeExtractionResult WithDiagnostic(
        ClangExtractionDiagnostic diagnostic) =>
        _emptyResult with
        {
            Diagnostics = new[] { diagnostic },
        };

    private static List<ClangExtractionDiagnostic> ReadDiagnostics(
        CXTranslationUnit translationUnit,
        ScopePathPolicy scopePolicy)
    {
        var result = new List<ClangExtractionDiagnostic>(
            checked((int)translationUnit.NumDiagnostics));
        for (var index = 0U; index < translationUnit.NumDiagnostics; index++)
        {
            using var diagnostic = translationUnit.GetDiagnostic(index);
            if (TryReadLocation(
                    diagnostic.Location,
                    out var diagnosticPath,
                    out _,
                    out _)
                && scopePolicy.IsExcluded(diagnosticPath))
            {
                continue;
            }

            var location = TryCreatePointLocation(diagnostic.Location, scopePolicy);
            result.Add(new ClangExtractionDiagnostic(
                ClangDiagnosticCode,
                MapDiagnosticSeverity(diagnostic.Severity),
                diagnostic.Spelling.ToString(),
                location));
        }
        return result;
    }

    private static ClangExtractionDiagnostic? ValidateTranslationUnitTarget(
        CXTranslationUnit translationUnit,
        InteropTarget expected)
    {
        using var targetInfo = translationUnit.TargetInfo;
        var triple = targetInfo.Triple.ToString();
        var pointerSizeBytes = targetInfo.PointerWidth > 0
            ? targetInfo.PointerWidth / 8
            : 0;

        var architectureMatches = expected.Architecture switch
        {
            InteropArchitecture.X86 => triple.StartsWith(
                "i386",
                StringComparison.OrdinalIgnoreCase)
                || triple.StartsWith("i486", StringComparison.OrdinalIgnoreCase)
                || triple.StartsWith("i586", StringComparison.OrdinalIgnoreCase)
                || triple.StartsWith("i686", StringComparison.OrdinalIgnoreCase),
            InteropArchitecture.X64 => triple.StartsWith(
                "x86_64",
                StringComparison.OrdinalIgnoreCase)
                || triple.StartsWith("amd64", StringComparison.OrdinalIgnoreCase),
            InteropArchitecture.Arm64 => triple.StartsWith(
                "aarch64",
                StringComparison.OrdinalIgnoreCase)
                || triple.StartsWith("arm64", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        var isMsvc = triple.Contains("msvc", StringComparison.OrdinalIgnoreCase);
        var abiMatches = expected.CompilerAbi switch
        {
            InteropCompilerAbi.Msvc => isMsvc,
            InteropCompilerAbi.Itanium => !isMsvc,
            _ => false,
        };

        if (pointerSizeBytes == expected.PointerSizeBytes
            && architectureMatches
            && abiMatches)
        {
            return null;
        }

        return new ClangExtractionDiagnostic(
            TargetMismatchCode,
            ClangExtractionDiagnosticSeverity.Fatal,
            $"Clang target '{triple}' does not match requested target "
                + $"'{expected.RuntimeIdentifier}' ({expected.Architecture}, "
                + $"{expected.CompilerAbi}, {expected.PointerSizeBytes * 8}-bit).");
    }

    private static ClangExtractionDiagnosticSeverity MapDiagnosticSeverity(
        CXDiagnosticSeverity severity) =>
        severity switch
        {
            CXDiagnosticSeverity.CXDiagnostic_Ignored =>
                ClangExtractionDiagnosticSeverity.Info,
            CXDiagnosticSeverity.CXDiagnostic_Note =>
                ClangExtractionDiagnosticSeverity.Info,
            CXDiagnosticSeverity.CXDiagnostic_Warning =>
                ClangExtractionDiagnosticSeverity.Warning,
            CXDiagnosticSeverity.CXDiagnostic_Error =>
                ClangExtractionDiagnosticSeverity.Error,
            CXDiagnosticSeverity.CXDiagnostic_Fatal =>
                ClangExtractionDiagnosticSeverity.Fatal,
            _ => ClangExtractionDiagnosticSeverity.Warning,
        };

    private static bool IsNativeAvailabilityFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return true;
            }
        }
        return false;
    }

    private sealed class Collector
    {
        private readonly ClangNativeExtractionRequest _request;
        private readonly string _scopeRoot;
        private readonly ScopePathPolicy _scopePolicy;
        private readonly List<ClangExtractionDiagnostic> _diagnostics;
        private readonly List<NativeFunctionFact> _functions = [];
        private readonly List<NativeTypeDeclarationFact> _types = [];
        private readonly List<NativeExportCandidate> _exportCandidates = [];
        private readonly List<AbiRecordLayout> _recordLayouts = [];

        public Collector(
            ClangNativeExtractionRequest request,
            string scopeRoot,
            ScopePathPolicy scopePolicy,
            List<ClangExtractionDiagnostic> diagnostics)
        {
            _request = request;
            _scopeRoot = scopeRoot;
            _scopePolicy = scopePolicy;
            _diagnostics = diagnostics;
        }

        public void Visit(
            IReadOnlyList<Decl> declarations,
            bool hasCLinkageContext = false)
        {
            foreach (var declaration in declarations)
            {
                Visit(declaration, hasCLinkageContext);
            }
        }

        public ClangNativeExtractionResult BuildResult()
        {
            var exports = _exportCandidates
                .GroupBy(candidate => candidate.Usr, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(candidate => candidate.IsDefinition)
                    .ThenBy(candidate => candidate.Export.Evidence.Location.FilePath, PathComparer)
                    .ThenBy(candidate => candidate.Export.Evidence.Location.StartLine)
                    .First()
                    .Export)
                .OrderBy(export => export.SymbolCanonicalKey, StringComparer.Ordinal)
                .ToArray();

            return new ClangNativeExtractionResult(
                _functions
                    .DistinctBy(function => function.SymbolCanonicalKey)
                    .OrderBy(function => function.SymbolCanonicalKey, StringComparer.Ordinal)
                    .ToArray(),
                _types
                    .GroupBy(type => type.SymbolCanonicalKey, StringComparer.Ordinal)
                    .Select(group => group
                        .OrderByDescending(type => type.IsDefinition)
                        .First())
                    .OrderBy(type => type.SymbolCanonicalKey, StringComparer.Ordinal)
                    .ToArray(),
                exports,
                _recordLayouts
                    .GroupBy(layout => layout.SymbolCanonicalKey, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(layout => layout.SymbolCanonicalKey, StringComparer.Ordinal)
                    .ToArray(),
                _diagnostics.ToArray());
        }

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private void Visit(Decl declaration, bool hasCLinkageContext)
        {
            switch (declaration)
            {
                case FunctionDecl function:
                    AddFunction(function, hasCLinkageContext);
                    break;
                case RecordDecl record:
                    AddRecord(record);
                    break;
                case EnumDecl enumDeclaration:
                    AddEnum(enumDeclaration);
                    break;
                case TypedefNameDecl typedef:
                    AddTypedef(typedef);
                    break;
            }

            if (declaration is IDeclContext context)
            {
                var childHasCLinkage = hasCLinkageContext
                    || declaration is LinkageSpecDecl
                    {
                        Language: CXLanguageKind.CXLanguage_C,
                    };
                Visit(context.Decls, childHasCLinkage);
            }
        }

        private void AddFunction(
            FunctionDecl function,
            bool hasCLinkageContext)
        {
            if (string.IsNullOrWhiteSpace(function.Name)
                || !TryCreateEvidence(
                    function.SourceRange,
                    "function",
                    function.IsThisDeclarationADefinition,
                    out var evidence,
                    out var repoRelativePath))
            {
                return;
            }

            var sourceScheme = SchemeForSource(evidence.Location.FilePath);
            var hasCLinkage = sourceScheme == "c"
                || hasCLinkageContext
                || HasUnmangledExternCLinkage(function);
            var isExported = HasExportAttribute(function);
            var parameters = function.Parameters
                .Select((parameter, position) => new AbiParameter(
                    position,
                    parameter.Name,
                    MapType(parameter.Type),
                    MapDirection(parameter.Type),
                    CreateParameterLocation(parameter)))
                .ToArray();
            var callingConvention = MapCallingConvention(
                function.Type.Handle.FunctionTypeCallingConv);
            var qualifiedName = string.IsNullOrWhiteSpace(function.QualifiedName)
                ? function.Name
                : function.QualifiedName;
            var signature = qualifiedName
                + "("
                + string.Join(",", parameters.Select(
                    parameter => parameter.Type.CanonicalName))
                + ")";
            var scheme = hasCLinkage ? "c" : sourceScheme;
            var functionFact = new NativeFunctionFact(
                NativeCanonicalKeys.ForFunction(scheme, repoRelativePath, signature),
                function.Name,
                qualifiedName,
                callingConvention,
                MapType(function.ReturnType),
                parameters,
                hasCLinkage,
                isExported,
                function.IsThisDeclarationADefinition,
                evidence);
            _functions.Add(functionFact);

            if (!hasCLinkage || !isExported || !function.IsGlobal)
            {
                return;
            }

            var nativeExport = new NativeExport(
                NativeCanonicalKeys.ForExport("c", repoRelativePath, function.Name),
                function.Name,
                callingConvention,
                functionFact.ReturnType,
                parameters,
                HasCLinkage: true,
                IsBinaryVerified: false,
                _request.Target,
                evidence)
            {
                LibraryName = _request.LibraryName,
            };
            var usr = function.Handle.Usr.ToString();
            _exportCandidates.Add(new NativeExportCandidate(
                string.IsNullOrWhiteSpace(usr)
                    ? functionFact.SymbolCanonicalKey
                    : usr,
                function.IsThisDeclarationADefinition,
                nativeExport));
        }

        private void AddRecord(RecordDecl record)
        {
            if (string.IsNullOrWhiteSpace(record.Name)
                || !TryCreateEvidence(
                    record.SourceRange,
                    record.IsUnion ? "union" : "record",
                    record.IsCompleteDefinition,
                    out var evidence,
                    out var repoRelativePath))
            {
                return;
            }

            var qualifiedName = string.IsNullOrWhiteSpace(record.QualifiedName)
                ? record.Name
                : record.QualifiedName;
            var scheme = SchemeForSource(evidence.Location.FilePath);
            var canonicalKey = NativeCanonicalKeys.ForType(
                scheme,
                repoRelativePath,
                qualifiedName);
            _types.Add(new NativeTypeDeclarationFact(
                canonicalKey,
                record.IsUnion
                    ? NativeTypeDeclarationKind.Union
                    : NativeTypeDeclarationKind.Struct,
                record.Name,
                qualifiedName,
                MapType(record.TypeForDecl),
                record.IsCompleteDefinition,
                evidence));

            if (!record.IsCompleteDefinition)
            {
                return;
            }

            var fields = record.Fields
                .Select((field, order) => CreateFieldLayout(field, order))
                .ToArray();
            _recordLayouts.Add(new AbiRecordLayout(
                canonicalKey,
                AbiRecordKind.Native,
                KnownPositive(record.TypeForDecl.Handle.SizeOf),
                KnownPositive(record.TypeForDecl.Handle.AlignOf),
                Pack: null,
                fields,
                _request.Target,
                evidence));
        }

        private void AddEnum(EnumDecl enumDeclaration)
        {
            if (string.IsNullOrWhiteSpace(enumDeclaration.Name)
                || !TryCreateEvidence(
                    enumDeclaration.SourceRange,
                    "enum",
                    enumDeclaration.IsCompleteDefinition,
                    out var evidence,
                    out var repoRelativePath))
            {
                return;
            }

            var qualifiedName = string.IsNullOrWhiteSpace(enumDeclaration.QualifiedName)
                ? enumDeclaration.Name
                : enumDeclaration.QualifiedName;
            var scheme = SchemeForSource(evidence.Location.FilePath);
            _types.Add(new NativeTypeDeclarationFact(
                NativeCanonicalKeys.ForType(scheme, repoRelativePath, qualifiedName),
                NativeTypeDeclarationKind.Enum,
                enumDeclaration.Name,
                qualifiedName,
                MapType(enumDeclaration.TypeForDecl),
                enumDeclaration.IsCompleteDefinition,
                evidence));
        }

        private void AddTypedef(TypedefNameDecl typedef)
        {
            if (string.IsNullOrWhiteSpace(typedef.Name)
                || !TryCreateEvidence(
                    typedef.SourceRange,
                    "typedef",
                    isDefinition: true,
                    out var evidence,
                    out var repoRelativePath))
            {
                return;
            }

            var qualifiedName = string.IsNullOrWhiteSpace(typedef.QualifiedName)
                ? typedef.Name
                : typedef.QualifiedName;
            var scheme = SchemeForSource(evidence.Location.FilePath);
            _types.Add(new NativeTypeDeclarationFact(
                NativeCanonicalKeys.ForTypeAlias(
                    scheme,
                    repoRelativePath,
                    qualifiedName),
                NativeTypeDeclarationKind.Typedef,
                typedef.Name,
                qualifiedName,
                MapType(typedef.UnderlyingType),
                IsDefinition: true,
                evidence));
        }

        private AbiFieldLayout CreateFieldLayout(FieldDecl field, int order)
        {
            var evidence = TryCreateEvidence(
                field.SourceRange,
                "field",
                isDefinition: true,
                out var fieldEvidence,
                out _)
                ? fieldEvidence
                : throw new InvalidOperationException(
                    "An in-scope record field must have in-scope evidence.");
            var offsetBits = field.Handle.OffsetOfField;
            var offsetBytes = offsetBits >= 0 && offsetBits % 8 == 0
                ? KnownNonNegative(offsetBits / 8)
                : null;
            var sizeBytes = field.IsBitField
                ? null
                : KnownPositive(field.Type.Handle.SizeOf);
            return new AbiFieldLayout(
                order,
                field.Name,
                MapType(field.Type),
                offsetBytes,
                sizeBytes,
                evidence);
        }

        private CoreSourceLocation CreateParameterLocation(ParmVarDecl parameter)
        {
            if (TryCreateSourceLocation(
                parameter.SourceRange,
                _scopePolicy,
                out var location))
            {
                return location;
            }

            throw new InvalidOperationException(
                "An in-scope function parameter must have in-scope source evidence.");
        }

        private bool TryCreateEvidence(
            CXSourceRange range,
            string declarationKind,
            bool isDefinition,
            out Evidence evidence,
            out string repoRelativePath)
        {
            evidence = null!;
            repoRelativePath = string.Empty;
            if (!TryCreateSourceLocation(range, _scopePolicy, out var location))
            {
                return false;
            }

            repoRelativePath = Path.GetRelativePath(
                    _scopeRoot,
                    location.FilePath)
                .Replace('\\', '/');
            evidence = new Evidence(
                _request.ProducingFileId,
                location,
                CoreEvidenceConfidence.Exact,
                Producer,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["declarationKind"] = declarationKind,
                    ["isDefinition"] = isDefinition ? "true" : "false",
                    ["target"] = _request.Target.RuntimeIdentifier,
                });
            return true;
        }

        private static bool HasExportAttribute(FunctionDecl function)
        {
            foreach (var attribute in function.Attrs)
            {
                if (attribute.Kind == CX_AttrKind.CX_AttrKind_DLLExport)
                {
                    return true;
                }
                if (attribute.Kind == CX_AttrKind.CX_AttrKind_Visibility
                    && attribute.PrettyPrint().Contains(
                        "default",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasUnmangledExternCLinkage(FunctionDecl function)
        {
            if (!function.IsExternC)
            {
                return false;
            }

            var mangling = function.Handle.Mangling.ToString();
            return !string.IsNullOrWhiteSpace(mangling)
                && !mangling.StartsWith("?", StringComparison.Ordinal)
                && !mangling.StartsWith("_Z", StringComparison.Ordinal)
                && !mangling.StartsWith("__Z", StringComparison.Ordinal);
        }

        private static AbiParameterDirection MapDirection(ClangSharp.Type type)
        {
            var canonical = type.Handle.CanonicalType;
            if (canonical.kind is not (
                CXTypeKind.CXType_Pointer
                or CXTypeKind.CXType_LValueReference
                or CXTypeKind.CXType_RValueReference))
            {
                return AbiParameterDirection.In;
            }
            return canonical.PointeeType.IsConstQualified
                ? AbiParameterDirection.In
                : AbiParameterDirection.InOut;
        }

        private string SchemeForSource(string filePath)
        {
            if (string.Equals(
                Path.GetExtension(filePath),
                ".c",
                StringComparison.OrdinalIgnoreCase))
            {
                return "c";
            }

            for (var index = 0; index < _request.CompilerArguments.Count; index++)
            {
                var argument = _request.CompilerArguments[index];
                if (string.Equals(argument, "-x", StringComparison.Ordinal)
                    && index + 1 < _request.CompilerArguments.Count)
                {
                    return IsCLanguage(_request.CompilerArguments[index + 1])
                        ? "c"
                        : "cpp";
                }
                if (argument.StartsWith("-x", StringComparison.Ordinal)
                    && argument.Length > 2)
                {
                    return IsCLanguage(argument[2..]) ? "c" : "cpp";
                }
            }
            return "cpp";
        }

        private static bool IsCLanguage(string language) =>
            string.Equals(language, "c", StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                language,
                "objective-c",
                StringComparison.OrdinalIgnoreCase);
    }

    private static AbiTypeRef MapType(ClangSharp.Type type)
    {
        var original = type.Handle;
        var canonical = original.CanonicalType;
        var canonicalName = original.Spelling.ToString();
        if (string.IsNullOrWhiteSpace(canonicalName))
        {
            canonicalName = canonical.Spelling.ToString();
        }
        if (string.IsNullOrWhiteSpace(canonicalName))
        {
            canonicalName = "<unknown>";
        }

        var pointerDepth = 0;
        var pointed = canonical;
        while (pointed.kind is CXTypeKind.CXType_Pointer
            or CXTypeKind.CXType_LValueReference
            or CXTypeKind.CXType_RValueReference)
        {
            pointerDepth++;
            pointed = pointed.PointeeType.CanonicalType;
        }

        var category = MapTypeCategory(canonical, pointed, pointerDepth);
        bool? signed = category switch
        {
            AbiTypeCategory.SignedInteger => true,
            AbiTypeCategory.UnsignedInteger => false,
            AbiTypeCategory.Enum when pointed.IsSigned => true,
            AbiTypeCategory.Enum when pointed.IsUnsigned => false,
            _ => null,
        };
        var fixedArrayLength = canonical.kind == CXTypeKind.CXType_ConstantArray
            ? KnownArrayLength(canonical.ArraySize)
            : null;

        return new AbiTypeRef(
            canonicalName,
            category,
            pointerDepth,
            KnownPositive(original.SizeOf),
            KnownPositive(original.AlignOf),
            signed,
            stringEncoding: null,
            fixedArrayLength);
    }

    private static AbiTypeCategory MapTypeCategory(
        CXType canonical,
        CXType pointed,
        int pointerDepth)
    {
        if (pointerDepth > 0)
        {
            return pointed.kind is CXTypeKind.CXType_FunctionProto
                or CXTypeKind.CXType_FunctionNoProto
                ? AbiTypeCategory.FunctionPointer
                : AbiTypeCategory.Pointer;
        }

        return canonical.kind switch
        {
            CXTypeKind.CXType_Void => AbiTypeCategory.Void,
            CXTypeKind.CXType_Bool => AbiTypeCategory.Boolean,
            CXTypeKind.CXType_Char_U
                or CXTypeKind.CXType_UChar
                or CXTypeKind.CXType_Char16
                or CXTypeKind.CXType_Char32
                or CXTypeKind.CXType_UShort
                or CXTypeKind.CXType_UInt
                or CXTypeKind.CXType_ULong
                or CXTypeKind.CXType_ULongLong
                or CXTypeKind.CXType_UInt128 =>
                AbiTypeCategory.UnsignedInteger,
            CXTypeKind.CXType_Char_S
                or CXTypeKind.CXType_SChar
                or CXTypeKind.CXType_WChar
                or CXTypeKind.CXType_Short
                or CXTypeKind.CXType_Int
                or CXTypeKind.CXType_Long
                or CXTypeKind.CXType_LongLong
                or CXTypeKind.CXType_Int128 =>
                AbiTypeCategory.SignedInteger,
            CXTypeKind.CXType_Half
                or CXTypeKind.CXType_Float
                or CXTypeKind.CXType_Double
                or CXTypeKind.CXType_LongDouble
                or CXTypeKind.CXType_Float128
                or CXTypeKind.CXType_Float16
                or CXTypeKind.CXType_BFloat16 =>
                AbiTypeCategory.FloatingPoint,
            CXTypeKind.CXType_Enum => AbiTypeCategory.Enum,
            CXTypeKind.CXType_Record => AbiTypeCategory.Record,
            CXTypeKind.CXType_ConstantArray
                or CXTypeKind.CXType_IncompleteArray
                or CXTypeKind.CXType_VariableArray
                or CXTypeKind.CXType_DependentSizedArray =>
                AbiTypeCategory.Array,
            _ => AbiTypeCategory.Opaque,
        };
    }

    private static InteropCallingConvention MapCallingConvention(
        CXCallingConv callingConvention) =>
        callingConvention switch
        {
            CXCallingConv.CXCallingConv_C => InteropCallingConvention.Cdecl,
            CXCallingConv.CXCallingConv_X86StdCall =>
                InteropCallingConvention.StdCall,
            CXCallingConv.CXCallingConv_X86ThisCall =>
                InteropCallingConvention.ThisCall,
            CXCallingConv.CXCallingConv_X86FastCall =>
                InteropCallingConvention.FastCall,
            CXCallingConv.CXCallingConv_X86VectorCall =>
                InteropCallingConvention.VectorCall,
            CXCallingConv.CXCallingConv_Default =>
                InteropCallingConvention.PlatformDefault,
            _ => InteropCallingConvention.Unknown,
        };

    private static bool TryCreateSourceLocation(
        CXSourceRange range,
        ScopePathPolicy scopePolicy,
        out CoreSourceLocation location)
    {
        location = null!;
        if (range.IsNull
            || !TryReadLocation(
                range.Start,
                out var startPath,
                out var startLine,
                out var startColumn)
            || !TryReadLocation(
                range.End,
                out var endPath,
                out var endLine,
                out var endColumn)
            || !PathEquals(startPath, endPath)
            || scopePolicy.IsExcluded(startPath))
        {
            return false;
        }

        location = new CoreSourceLocation(
            Path.GetFullPath(startPath),
            startLine,
            startColumn,
            endLine,
            endColumn);
        return true;
    }

    private static CoreSourceLocation? TryCreatePointLocation(
        CXSourceLocation sourceLocation,
        ScopePathPolicy scopePolicy)
    {
        if (!TryReadLocation(
                sourceLocation,
                out var path,
                out var line,
                out var column)
            || scopePolicy.IsExcluded(path))
        {
            return null;
        }
        return new CoreSourceLocation(
            Path.GetFullPath(path),
            line,
            column,
            line,
            checked(column + 1));
    }

    private static bool TryReadLocation(
        CXSourceLocation sourceLocation,
        out string path,
        out int line,
        out int column)
    {
        sourceLocation.GetFileLocation(
            out var file,
            out var rawLine,
            out var rawColumn,
            out _);
        path = file.Handle == IntPtr.Zero
            ? string.Empty
            : file.Name.ToString();
        line = rawLine <= int.MaxValue ? checked((int)rawLine) : 0;
        column = rawColumn <= int.MaxValue ? checked((int)rawColumn) : 0;
        return !string.IsNullOrWhiteSpace(path) && line > 0 && column > 0;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static int? KnownPositive(long value) =>
        value is > 0 and <= int.MaxValue
            ? checked((int)value)
            : null;

    private static int? KnownNonNegative(long value) =>
        value is >= 0 and <= int.MaxValue
            ? checked((int)value)
            : null;

    private static int? KnownArrayLength(long value) =>
        value is > 0 and <= int.MaxValue
            ? checked((int)value)
            : null;

    private sealed record NativeExportCandidate(
        string Usr,
        bool IsDefinition,
        NativeExport Export);
}
