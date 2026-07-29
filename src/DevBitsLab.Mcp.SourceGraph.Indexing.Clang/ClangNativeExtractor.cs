using ClangSharp;
using ClangSharp.Interop;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using System.Runtime.InteropServices;
using System.Text;
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
    private const string UnsafeInputCode = "CLANG0005";
    private const string ClangDiagnosticCode = "CLANG1000";
    private const string IncompleteCallGraphCode = "CLANG2000";
    private const string CallGraphLimitCode = "CLANG2001";
    private const string CallProducer = "clang-native-call";
    private const string RetentionProducer = "clang-native-retention";
    private const string ExceptionProducer = "clang-native-exception";
    private const string AllocationProducer = "clang-native-allocation";
    internal const int MaximumExtractedFunctions = 4096;
    internal const int MaximumExtractedCalls = 8192;
    internal const int MaximumRetainedCallbacksPerExport = 4096;
    internal const int MaximumDeclarationDepth = 128;
    internal const int MaximumStatementDepth = 256;
    internal const int MaximumVisitedStatements = 100_000;
    internal const int MaximumCallDiagnostics = 4096;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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

        var lexicalScopeRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.ScopeRoot));
        var lexicalScopePolicy = new ScopePathPolicy(
            lexicalScopeRoot,
            request.ExcludePatterns);
        if (!ScopePathPolicy.TryResolvePhysicalPath(
                lexicalScopeRoot,
                out var scopeRoot)
            || !ClangInputPreflight.TryResolveAllowedFile(
                request.SourceFilePath,
                lexicalScopePolicy,
                out var sourceFilePath))
        {
            return WithDiagnostic(UnsafeInput(
                "The translation-unit source could not be resolved inside the approved scope."));
        }
        var scopePolicy = new ScopePathPolicy(
            scopeRoot,
            request.ExcludePatterns);
        if (!ClangInputPreflight.TryNormalizeCompilerArguments(
                request.CompilerArguments,
                lexicalScopePolicy,
                request.SystemIncludeDirectories,
                out var compilerArguments,
                out var includeDirectories,
                out var systemIncludeDirectories,
                out var argumentRejection))
        {
            return WithDiagnostic(UnsafeInput(argumentRejection));
        }
        if (!ClangInputPreflight.TryValidateExplicitIncludeGraph(
                sourceFilePath,
                includeDirectories,
                systemIncludeDirectories,
                scopePolicy,
                out var approvedInputFiles,
                out var includeRejection))
        {
            return WithDiagnostic(UnsafeInput(includeRejection));
        }

        try
        {
            if (!ClangUnsavedFileSet.TryCreate(
                    approvedInputFiles,
                    request.InMemoryInputs,
                    scopePolicy,
                    out var unsavedFiles,
                    out var unsavedDiagnostic))
            {
                return WithDiagnostic(unsavedDiagnostic!);
            }
            using var unsavedFilesScope = unsavedFiles;
            using var index = createIndex();
            var error = CXTranslationUnit.TryParse(
                index,
                sourceFilePath,
                compilerArguments,
                unsavedFilesScope.Files,
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
                    if (!TryReadIncludedFiles(
                            handle,
                            sourceFilePath,
                            scopePolicy,
                            systemIncludeDirectories,
                            out var includedFiles,
                            out var inclusionDiagnostic))
                    {
                        return WithDiagnostic(inclusionDiagnostic!);
                    }

                    var diagnostics = ReadDiagnostics(
                        handle,
                        scopePolicy,
                        systemIncludeDirectories);
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
                            diagnostics)
                        {
                            IncludedFiles = includedFiles,
                        };
                    }

                    var collector = new Collector(
                        request,
                        sourceFilePath,
                        scopeRoot,
                        scopePolicy,
                        includeDirectories,
                        diagnostics);
                    collector.Visit(translationUnit.TranslationUnitDecl.Decls);
                    return collector.BuildResult() with
                    {
                        IncludedFiles = includedFiles,
                    };
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
        if (request.SystemIncludeDirectories is null
            || request.SystemIncludeDirectories.Any(directory =>
                string.IsNullOrWhiteSpace(directory)))
        {
            return InvalidRequest(
                "SystemIncludeDirectories must not contain null or blank values.");
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

    private static ClangExtractionDiagnostic UnsafeInput(string message) =>
        new(
            UnsafeInputCode,
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
        ScopePathPolicy scopePolicy,
        IReadOnlyList<string> systemIncludeDirectories)
    {
        var result = new List<ClangExtractionDiagnostic>(
            checked((int)translationUnit.NumDiagnostics));
        for (var index = 0U; index < translationUnit.NumDiagnostics; index++)
        {
            using var diagnostic = translationUnit.GetDiagnostic(index);
            var hasLocation = TryReadLocation(
                    diagnostic.Location,
                    out var diagnosticPath,
                    out _,
                    out _);
            if (hasLocation
                && scopePolicy.IsExcluded(diagnosticPath)
                && !ClangInputPreflight.TryResolveSystemFile(
                    diagnosticPath,
                    systemIncludeDirectories,
                    out _))
            {
                continue;
            }

            var location = hasLocation
                && !ClangInputPreflight.TryResolveSystemFile(
                    diagnosticPath,
                    systemIncludeDirectories,
                    out _)
                ? TryCreatePointLocation(
                    diagnostic.Location,
                    scopePolicy)
                : null;
            result.Add(new ClangExtractionDiagnostic(
                ClangDiagnosticCode,
                MapDiagnosticSeverity(diagnostic.Severity),
                diagnostic.Spelling.ToString(),
                location));
        }
        return result;
    }

    private static unsafe bool TryReadIncludedFiles(
        CXTranslationUnit translationUnit,
        string sourceFilePath,
        ScopePathPolicy scopePolicy,
        IReadOnlyList<string> systemIncludeDirectories,
        out IReadOnlyList<string> includedFiles,
        out ClangExtractionDiagnostic? diagnostic)
    {
        var observedPaths = new List<string>();
        var visitorFailed = false;
        CXInclusionVisitor visitor = (includedFile, _, _, _) =>
        {
            if (includedFile is null)
            {
                visitorFailed = true;
                return;
            }

            try
            {
                var path = new CXFile((IntPtr)includedFile).Name.ToString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    visitorFailed = true;
                    return;
                }
                observedPaths.Add(path);
            }
            catch
            {
                // Exceptions must not cross the unmanaged callback boundary.
                visitorFailed = true;
            }
        };

        try
        {
            translationUnit.GetInclusions(visitor, default);
            GC.KeepAlive(visitor);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or NotSupportedException
                or OverflowException)
        {
            visitorFailed = true;
        }

        if (visitorFailed)
        {
            includedFiles = Array.Empty<string>();
            diagnostic = UnsafeInput(
                "libclang returned an inclusion path that could not be validated safely.");
            return false;
        }

        var allowedPaths = new HashSet<string>(PathComparer)
        {
            sourceFilePath,
        };
        foreach (var observedPath in observedPaths)
        {
            if (!ClangInputPreflight.TryResolveAllowedFile(
                    observedPath,
                    scopePolicy,
                    out var physicalPath))
            {
                if (ClangInputPreflight.TryResolveSystemFile(
                        observedPath,
                        systemIncludeDirectories,
                        out _))
                {
                    continue;
                }
                includedFiles = Array.Empty<string>();
                diagnostic = UnsafeInput(
                    "libclang observed an inclusion outside the approved scope.");
                return false;
            }
            allowedPaths.Add(physicalPath);
        }

        includedFiles = allowedPaths
            .OrderBy(path => path, PathComparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        diagnostic = null;
        return true;
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
        private readonly string _sourceFilePath;
        private readonly string _scopeRoot;
        private readonly ScopePathPolicy _scopePolicy;
        private readonly IReadOnlyList<string> _trustedIncludeDirectories;
        private readonly List<ClangExtractionDiagnostic> _diagnostics;
        private readonly List<NativeFunctionFact> _functions = [];
        private readonly List<NativeTypeDeclarationFact> _types = [];
        private readonly List<NativeExportCandidate> _exportCandidates = [];
        private readonly List<AbiRecordLayout> _recordLayouts = [];
        private readonly List<NativeCallCandidate> _callCandidates = [];
        private readonly Dictionary<string, string> _definitionGraphKeysByUsr =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _exportedDeclarationUsrs =
            new(StringComparer.Ordinal);
        private bool _isCallGraphComplete = true;
        private int _callDiagnosticCount;
        private int _visitedStatements;

        public Collector(
            ClangNativeExtractionRequest request,
            string sourceFilePath,
            string scopeRoot,
            ScopePathPolicy scopePolicy,
            IReadOnlyList<string> trustedIncludeDirectories,
            List<ClangExtractionDiagnostic> diagnostics)
        {
            _request = request;
            _sourceFilePath = sourceFilePath;
            _scopeRoot = scopeRoot;
            _scopePolicy = scopePolicy;
            _trustedIncludeDirectories = trustedIncludeDirectories;
            _diagnostics = diagnostics;
        }

        public void Visit(
            IReadOnlyList<Decl> declarations,
            bool hasCLinkageContext = false)
        {
            foreach (var declaration in declarations)
            {
                Visit(declaration, hasCLinkageContext, depth: 0);
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

            var functions = _functions
                .GroupBy(
                    function => function.SymbolCanonicalKey,
                    StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(function => function.IsDefinition)
                    .ThenBy(
                        function => function.Evidence.Location.FilePath,
                        PathComparer)
                    .ThenBy(function => function.Evidence.Location.StartLine)
                    .ThenBy(function => function.Evidence.Location.StartColumn)
                    .First())
                .OrderBy(
                    function => function.SymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ToArray();
            var retainedDefinitionGraphKeysByUsr = functions
                .Where(function =>
                    function.IsDefinition
                    && !string.IsNullOrWhiteSpace(function.DeclarationUsr))
                .ToDictionary(
                    function => function.DeclarationUsr,
                    function => function.GraphCanonicalKey,
                    StringComparer.Ordinal);
            var calls = _callCandidates
                .Select(candidate => new NativeCallFact(
                    candidate.CallerSymbolCanonicalKey,
                    candidate.ReferencedDeclarationUsr,
                    retainedDefinitionGraphKeysByUsr.GetValueOrDefault(
                        candidate.ReferencedDeclarationUsr),
                    _request.Target,
                    candidate.Evidence))
                .DistinctBy(call => (
                    call.CallerSymbolCanonicalKey,
                    call.ReferencedDeclarationUsr,
                    call.Evidence.Location.FilePath,
                    call.Evidence.Location.StartLine,
                    call.Evidence.Location.StartColumn,
                    call.Evidence.Location.EndLine,
                    call.Evidence.Location.EndColumn))
                .OrderBy(
                    call => call.CallerSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(
                    call => call.ReferencedDeclarationUsr,
                    StringComparer.Ordinal)
                .ThenBy(
                    call => call.Evidence.Location.FilePath,
                    PathComparer)
                .ThenBy(call => call.Evidence.Location.StartLine)
                .ThenBy(call => call.Evidence.Location.StartColumn)
                .ToArray();

            return new ClangNativeExtractionResult(
                functions,
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
                _diagnostics
                    .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .ThenBy(
                        diagnostic => diagnostic.Location?.FilePath,
                        PathComparer)
                    .ThenBy(diagnostic => diagnostic.Location?.StartLine ?? 0)
                    .ThenBy(diagnostic => diagnostic.Location?.StartColumn ?? 0)
                    .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
                    .ToArray())
            {
                Calls = calls,
                IsCallGraphComplete = _isCallGraphComplete,
            };
        }

        private void Visit(
            Decl declaration,
            bool hasCLinkageContext,
            int depth)
        {
            if (depth > MaximumDeclarationDepth)
            {
                MarkCallGraphIncomplete(
                    CallGraphLimitCode,
                    $"Declaration nesting exceeds the {MaximumDeclarationDepth}-level limit.",
                    location: null);
                return;
            }

            if (declaration is CXXRecordDecl lambdaClosure
                && !lambdaClosure.Handle.LambdaCallOperator.IsNull)
            {
                return;
            }

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
                foreach (var child in context.Decls)
                {
                    Visit(child, childHasCLinkage, checked(depth + 1));
                }
            }
        }

        private void AddFunction(
            FunctionDecl function,
            bool hasCLinkageContext)
        {
            var definitionProbe = function.Definition;
            if (_functions.Count >= MaximumExtractedFunctions)
            {
                MarkCallGraphIncomplete(
                    CallGraphLimitCode,
                    $"Function extraction exceeds the {MaximumExtractedFunctions}-item limit.",
                    location: null);
                return;
            }
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
            var usr = function.Handle.Usr.ToString();
            var hasExportAttribute = HasExportAttribute(function);
            if (hasExportAttribute && !string.IsNullOrWhiteSpace(usr))
            {
                _exportedDeclarationUsrs.Add(usr);
            }
            var isExported = hasExportAttribute
                || (!string.IsNullOrWhiteSpace(usr)
                    && _exportedDeclarationUsrs.Contains(usr));
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
            var qualifiedName = NormalizeQualifiedName(
                function.QualifiedName,
                function.Name);
            var signature = qualifiedName
                + "("
                + string.Join(",", parameters.Select(
                    parameter => parameter.Type.CanonicalName))
                + ")"
                + MethodQualifierSuffix(function);
            signature = signature.Replace('\\', '/');
            var scheme = hasCLinkage ? "c" : sourceScheme;
            var functionKey = NativeCanonicalKeys.ForFunction(
                scheme,
                repoRelativePath,
                signature);
            var isGraphExport = hasCLinkage && isExported && function.IsGlobal;
            var graphKey = isGraphExport
                ? NativeCanonicalKeys.ForExport(
                    "c",
                    repoRelativePath,
                    function.Name)
                : functionKey;
            var functionFact = new NativeFunctionFact(
                functionKey,
                function.Name,
                qualifiedName,
                callingConvention,
                MapType(function.ReturnType),
                parameters,
                hasCLinkage,
                isExported,
                function.IsThisDeclarationADefinition,
                evidence)
            {
                DeclarationUsr = usr,
                GraphCanonicalKey = graphKey,
                IsMethod = function is CXXMethodDecl,
                Target = _request.Target,
            };
            _functions.Add(functionFact);

            if (function.IsThisDeclarationADefinition)
            {
                if (string.IsNullOrWhiteSpace(usr))
                {
                    MarkCallGraphIncomplete(
                        IncompleteCallGraphCode,
                        "A function definition has no stable Clang declaration identity.",
                        evidence.Location);
                }
                else if (_definitionGraphKeysByUsr.TryGetValue(
                             usr,
                             out var existingGraphKey)
                         && !string.Equals(
                             existingGraphKey,
                             graphKey,
                             StringComparison.Ordinal))
                {
                    MarkCallGraphIncomplete(
                        IncompleteCallGraphCode,
                        "One Clang declaration identity maps to conflicting function definitions.",
                        evidence.Location);
                }
                else
                {
                    _definitionGraphKeysByUsr[usr] = graphKey;
                }
            }

            var bodyFacts = NativeExportBodyFacts.Empty;
            if (function.IsThisDeclarationADefinition
                && function.HasBody
                && function.Body is { } body)
            {
                bodyFacts = CollectBodyFacts(
                    body,
                    graphKey,
                    isGraphExport ? function : null,
                    parameters);
            }

            if (isGraphExport)
            {
                var nativeExport = new NativeExport(
                    graphKey,
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
                    ModuleIdentitySource = _request.LibraryName is null
                        ? NativeModuleIdentitySource.Unknown
                        : NativeModuleIdentitySource.Configuration,
                    RetainedCallbacks = bodyFacts.RetainedCallbacks,
                    ExceptionEscape = bodyFacts.ExceptionEscape,
                    ReturnAllocation = bodyFacts.ReturnAllocation,
                };
                _exportCandidates.Add(new NativeExportCandidate(
                    string.IsNullOrWhiteSpace(usr)
                        ? functionFact.SymbolCanonicalKey
                        : usr,
                    function.IsThisDeclarationADefinition,
                    nativeExport));
            }
            if (!function.IsThisDeclarationADefinition
                && definitionProbe is FunctionDecl definition
                && definition.IsThisDeclarationADefinition)
            {
                AddFunction(definition, hasCLinkageContext);
            }
        }

        private NativeExportBodyFacts CollectBodyFacts(
            Stmt body,
            string callerGraphKey,
            FunctionDecl? exportFunction,
            IReadOnlyList<AbiParameter> exportParameters)
        {
            var callbackParameters =
                new Dictionary<CXCursor, int>();
            if (exportFunction is not null)
            {
                for (var parameterIndex = 0;
                     parameterIndex < exportFunction.Parameters.Count;
                     parameterIndex++)
                {
                    if (exportParameters[parameterIndex].Type.Category
                        == AbiTypeCategory.FunctionPointer)
                    {
                        callbackParameters[
                            exportFunction.Parameters[parameterIndex].Handle] =
                            parameterIndex;
                    }
                }
            }

            var callbackStorageWrites =
                new Dictionary<CXCursor, CallbackStorageWriteState>();
            NativeExceptionEscape? exceptionEscape = null;
            var returnedAllocations =
                new List<NativeReturnAllocation>();
            var returnStatementCount = 0;
            var returnFlowProven = true;
            var traversalComplete = true;
            var canThrowAcrossBoundary =
                exportFunction is not null
                && CanProveThrowMayCrossBoundary(exportFunction);
            var pending =
                new Stack<(
                    Stmt Statement,
                    int Depth,
                    bool InsideTryCatch,
                    bool ControlFlowProven)>();
            pending.Push((body, 0, false, true));
            while (pending.Count > 0)
            {
                var (
                    statement,
                    depth,
                    insideTryCatch,
                    controlFlowProven) = pending.Pop();
                if (depth > MaximumStatementDepth)
                {
                    MarkCallGraphIncomplete(
                        CallGraphLimitCode,
                        $"Statement nesting exceeds the {MaximumStatementDepth}-level limit.",
                        TryCreatePointLocation(statement.Location, _scopePolicy));
                    traversalComplete = false;
                    continue;
                }
                if (_visitedStatements >= MaximumVisitedStatements)
                {
                    MarkCallGraphIncomplete(
                        CallGraphLimitCode,
                        $"Statement traversal exceeds the {MaximumVisitedStatements}-node limit.",
                        TryCreatePointLocation(statement.Location, _scopePolicy));
                    traversalComplete = false;
                    break;
                }
                _visitedStatements++;

                if (statement is LambdaExpr)
                {
                    continue;
                }
                if (exportFunction is not null
                    && statement is DeclRefExpr
                    {
                        Decl: VarDecl referencedStorage,
                    }
                    && referencedStorage is not ParmVarDecl
                    && referencedStorage.HasGlobalStorage
                    && IsFunctionPointerStorage(
                        referencedStorage.Type)
                    && TryGetOrCreateCallbackStorageState(
                        referencedStorage,
                        statement,
                        callbackStorageWrites,
                        ref traversalComplete,
                        out var referencedState))
                {
                    referencedState.ObservedReferences++;
                }
                if (statement is CallExpr call)
                {
                    AddDirectCall(call, callerGraphKey);
                    if (exportFunction is not null)
                    {
                        // A later call may mutate any globally-addressable callback storage.
                        // Without interprocedural side-effect proof, a prior assignment cannot
                        // establish that the callback is still retained at an exit.
                        foreach (var state in callbackStorageWrites.Values)
                        {
                            state.ProvenRetention = null;
                        }
                    }
                }
                if (exportFunction is not null)
                {
                    if (statement is BinaryOperator
                        {
                            Opcode:
                                CXBinaryOperatorKind.CXBinaryOperator_Assign,
                        } assignment)
                    {
                        if (!RecordCallbackStorageWrite(
                                assignment,
                                callbackParameters,
                                callbackStorageWrites,
                                controlFlowProven,
                                ref traversalComplete))
                        {
                            // An assignment through a local/reference/pointer can alias a
                            // previously proven global callback slot. Without alias proof,
                            // preserve Unknown rather than claiming the slot survives to exit.
                            foreach (var state in
                                     callbackStorageWrites.Values)
                            {
                                state.ProvenRetention = null;
                            }
                        }
                    }

                    if (statement is CXXThrowExpr throwExpression
                        && !insideTryCatch
                        && controlFlowProven
                        && canThrowAcrossBoundary
                        && exceptionEscape is null)
                    {
                        if (TryCreateNativeFactEvidence(
                                throwExpression.Extent,
                                ExceptionProducer,
                                new Dictionary<string, string>(
                                    StringComparer.Ordinal)
                                {
                                    ["escapeKind"] = "direct-throw",
                                },
                                out var exceptionEvidence))
                        {
                            exceptionEscape = new NativeExceptionEscape(
                                _request.Target,
                                exceptionEvidence);
                        }
                        else
                        {
                            MarkCallGraphIncomplete(
                                IncompleteCallGraphCode,
                                "A direct native throw has no approved source location.",
                                TryCreatePointLocation(
                                    throwExpression.Location,
                                    _scopePolicy));
                            traversalComplete = false;
                        }
                    }

                    if (statement is ReturnStmt returnStatement)
                    {
                        if (!controlFlowProven)
                        {
                            returnFlowProven = false;
                        }
                        else
                        {
                            returnStatementCount++;
                            var allocationAnalysis =
                                AnalyzeKnownReturnAllocation(
                                    returnStatement,
                                    out var allocation);
                            if (allocationAnalysis
                                == ReturnAllocationAnalysis.Known)
                            {
                                returnedAllocations.Add(allocation);
                            }
                            else if (allocationAnalysis
                                     == ReturnAllocationAnalysis.Incomplete)
                            {
                                traversalComplete = false;
                                returnFlowProven = false;
                            }
                        }
                    }
                }

                if (statement is CXXTryStmt tryStatement)
                {
                    var handlerEntryProven =
                        controlFlowProven
                        && TryBlockStartsWithDirectThrow(
                            tryStatement.TryBlock);
                    for (var handlerIndex =
                             tryStatement.Handlers.Count - 1;
                         handlerIndex >= 0;
                         handlerIndex--)
                    {
                        var handler =
                            tryStatement.Handlers[handlerIndex];
                        pending.Push((
                            handler,
                            checked(depth + 1),
                            insideTryCatch,
                            handlerEntryProven
                            && handlerIndex == 0
                            && IsCatchAll(handler)));
                    }
                    pending.Push((
                        tryStatement.TryBlock,
                        checked(depth + 1),
                        InsideTryCatch: true,
                        controlFlowProven));
                    continue;
                }

                IReadOnlyList<Stmt> children;
                try
                {
                    children = statement.Children
                        ?? Array.Empty<Stmt>();
                }
                catch (NullReferenceException)
                {
                    MarkCallGraphIncomplete(
                        IncompleteCallGraphCode,
                        "Clang did not expose a stable child-statement collection for this body.",
                        location: null);
                    traversalComplete = false;
                    continue;
                }
                if (statement is CompoundStmt)
                {
                    var childControlFlow =
                        new bool[children.Count];
                    var nextChildIsProven = controlFlowProven;
                    for (var childIndex = 0;
                         childIndex < children.Count;
                         childIndex++)
                    {
                        childControlFlow[childIndex] =
                            nextChildIsProven;
                        nextChildIsProven =
                            nextChildIsProven
                            && CanProveSimpleFallthrough(
                                children[childIndex]);
                    }
                    for (var childIndex = children.Count - 1;
                         childIndex >= 0;
                         childIndex--)
                    {
                        pending.Push((
                            children[childIndex],
                            checked(depth + 1),
                            insideTryCatch,
                            childControlFlow[childIndex]));
                    }
                    continue;
                }

                var childControlFlowProven =
                    controlFlowProven
                    && statement is not CallExpr
                    && !IntroducesConditionalControlFlow(statement);
                for (var childIndex = children.Count - 1;
                     childIndex >= 0;
                     childIndex--)
                {
                    pending.Push((
                        children[childIndex],
                        checked(depth + 1),
                        insideTryCatch,
                        childControlFlowProven));
                }
            }

            var retainedCallbacks =
                new Dictionary<int, NativeCallbackRetention>();
            foreach (var state in callbackStorageWrites.Values)
            {
                if (state.WriteCount != 1
                    || state.ObservedReferences
                        != state.DirectWriteReferences
                    || state.ProvenRetention is not { } retention)
                {
                    continue;
                }
                retainedCallbacks.TryAdd(
                    retention.ParameterPosition,
                    retention);
            }
            NativeReturnAllocation? returnAllocation = null;
            if (traversalComplete
                && returnFlowProven
                && returnStatementCount > 0
                && returnedAllocations.Count == returnStatementCount
                && returnedAllocations.All(allocation =>
                    allocation.AllocatorFamily
                    == returnedAllocations[0].AllocatorFamily))
            {
                returnAllocation = returnedAllocations[0];
            }

            return new NativeExportBodyFacts(
                retainedCallbacks.Values
                    .OrderBy(
                        retention => retention.ParameterPosition)
                    .ToArray(),
                exceptionEscape,
                returnAllocation);
        }

        private bool RecordCallbackStorageWrite(
            BinaryOperator assignment,
            IReadOnlyDictionary<CXCursor, int> callbackParameters,
            IDictionary<CXCursor, CallbackStorageWriteState> storageWrites,
            bool controlFlowProven,
            ref bool traversalComplete)
        {
            var left = IgnoreTransparentExpression(assignment.LHS);
            if (left is not DeclRefExpr
                {
                    Decl: VarDecl storage,
                }
                || storage is ParmVarDecl
                || !storage.HasGlobalStorage
                || !IsFunctionPointerStorage(storage.Type))
            {
                return false;
            }

            if (!TryGetOrCreateCallbackStorageState(
                    storage,
                    assignment,
                    storageWrites,
                    ref traversalComplete,
                    out var state))
            {
                return true;
            }

            state.WriteCount++;
            state.DirectWriteReferences++;
            if (state.WriteCount != 1 || !controlFlowProven)
            {
                state.ProvenRetention = null;
                return true;
            }

            var right = IgnoreTransparentExpression(assignment.RHS);
            if (right is not DeclRefExpr
                {
                    Decl: ParmVarDecl parameter,
                }
                || !callbackParameters.TryGetValue(
                    parameter.Handle,
                    out var parameterPosition))
            {
                return true;
            }

            if (TryCreateNativeFactEvidence(
                    assignment.Extent,
                    RetentionProducer,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["parameterPosition"] =
                            parameterPosition.ToString(
                                System.Globalization
                                    .CultureInfo.InvariantCulture),
                    },
                    out var retentionEvidence))
            {
                state.ProvenRetention =
                    new NativeCallbackRetention(
                        parameterPosition,
                        _request.Target,
                        retentionEvidence);
                return true;
            }

            MarkCallGraphIncomplete(
                IncompleteCallGraphCode,
                "A proven callback-retention assignment has no approved source location.",
                TryCreatePointLocation(
                    assignment.Location,
                    _scopePolicy));
            traversalComplete = false;
            return true;
        }

        private bool TryGetOrCreateCallbackStorageState(
            VarDecl storage,
            Stmt occurrence,
            IDictionary<CXCursor, CallbackStorageWriteState> storageWrites,
            ref bool traversalComplete,
            out CallbackStorageWriteState state)
        {
            if (storageWrites.TryGetValue(storage.Handle, out state!))
            {
                return true;
            }
            if (storageWrites.Count
                >= MaximumRetainedCallbacksPerExport)
            {
                MarkCallGraphIncomplete(
                    CallGraphLimitCode,
                    $"Callback-storage tracking exceeds the {MaximumRetainedCallbacksPerExport}-item limit.",
                    TryCreatePointLocation(
                        occurrence.Location,
                        _scopePolicy));
                traversalComplete = false;
                state = null!;
                return false;
            }
            state = new CallbackStorageWriteState();
            storageWrites.Add(storage.Handle, state);
            return true;
        }

        private ReturnAllocationAnalysis AnalyzeKnownReturnAllocation(
            ReturnStmt returnStatement,
            out NativeReturnAllocation allocation)
        {
            allocation = null!;
            if (returnStatement.RetValue is not { } returnedExpression
                || IgnoreTransparentExpression(returnedExpression)
                    is not CallExpr call)
            {
                return ReturnAllocationAnalysis.NotKnown;
            }

            FunctionDecl? directCallee;
            InteropAllocatorFamily allocatorFamily;
            string allocatorIdentity;
            try
            {
                directCallee = call.DirectCallee;
                if (directCallee is null
                    || !TryMapKnownAllocator(
                        call,
                        directCallee,
                        out allocatorFamily,
                        out allocatorIdentity))
                {
                    return ReturnAllocationAnalysis.NotKnown;
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or NotSupportedException
                    or OverflowException)
            {
                MarkCallGraphIncomplete(
                    IncompleteCallGraphCode,
                    "A direct return allocator could not be resolved completely.",
                    TryCreatePointLocation(
                        returnStatement.Location,
                        _scopePolicy));
                return ReturnAllocationAnalysis.Incomplete;
            }
            if (!TryCreateNativeFactEvidence(
                    returnStatement.Extent,
                    AllocationProducer,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["allocator"] = allocatorIdentity,
                        ["allocatorFamily"] = "crt_heap",
                    },
                    out var allocationEvidence))
            {
                MarkCallGraphIncomplete(
                    IncompleteCallGraphCode,
                    "A proven native return allocation has no approved source location.",
                    TryCreatePointLocation(
                        returnStatement.Location,
                        _scopePolicy));
                return ReturnAllocationAnalysis.Incomplete;
            }

            allocation = new NativeReturnAllocation(
                allocatorFamily,
                _request.Target,
                allocationEvidence);
            return ReturnAllocationAnalysis.Known;
        }

        private bool TryMapKnownAllocator(
            CallExpr call,
            FunctionDecl declaration,
            out InteropAllocatorFamily allocatorFamily,
            out string allocatorIdentity)
        {
            allocatorFamily = InteropAllocatorFamily.Unknown;
            allocatorIdentity = string.Empty;
            var function = declaration.CanonicalDecl;
            if (!function.IsGlobal
                || !function.IsExternC
                || function.IsVariadic
                || function.LinkageInternal
                    != CXLinkageKind.CXLinkage_External
                || function.IsDefined
                || !IsTrustedStandardAllocatorReference(
                    call,
                    function))
            {
                return false;
            }

            var qualifiedName = NormalizeQualifiedName(
                function.QualifiedName,
                function.Name);
            if (function.Name is not (
                    "malloc"
                    or "calloc"
                    or "realloc")
                || !IsVoidPointer(function.ReturnType))
            {
                return false;
            }

            var name = function.Name;
            var hasKnownSignature = name switch
            {
                "malloc" =>
                    function.Parameters.Count == 1
                    && IsPointerWidthUnsignedInteger(
                        function.Parameters[0].Type,
                        _request.Target.PointerSizeBytes),
                "calloc" =>
                    function.Parameters.Count == 2
                    && IsPointerWidthUnsignedInteger(
                        function.Parameters[0].Type,
                        _request.Target.PointerSizeBytes)
                    && IsPointerWidthUnsignedInteger(
                        function.Parameters[1].Type,
                        _request.Target.PointerSizeBytes),
                "realloc" =>
                    function.Parameters.Count == 2
                    && IsVoidPointer(function.Parameters[0].Type)
                    && IsPointerWidthUnsignedInteger(
                        function.Parameters[1].Type,
                        _request.Target.PointerSizeBytes),
                _ => false,
            };
            if (!hasKnownSignature)
            {
                return false;
            }

            allocatorFamily = InteropAllocatorFamily.CrtHeap;
            allocatorIdentity = qualifiedName;
            return true;
        }

        private bool IsTrustedStandardAllocatorReference(
            CallExpr call,
            FunctionDecl declaration)
        {
            var callee = IgnoreTransparentExpression(call.Callee);
            if (callee is not DeclRefExpr reference
                || reference.FoundDecl is not NamedDecl found
                || !found.IsInStdNamespace)
            {
                return false;
            }

            var declarationLocation = TryCreatePointLocation(
                declaration.Location,
                _scopePolicy);
            var usingLocation = TryCreatePointLocation(
                found.Location,
                _scopePolicy);
            return declarationLocation is not null
                && usingLocation is not null
                && IsTrustedAllocatorHeader(
                    declarationLocation.FilePath)
                && IsTrustedAllocatorHeader(usingLocation.FilePath);
        }

        private bool IsTrustedAllocatorHeader(string filePath)
        {
            if (PathEquals(filePath, _sourceFilePath))
            {
                return false;
            }

            var fileName = Path.GetFileName(filePath);
            if (!PathComparer.Equals(fileName, "cstdlib")
                && !PathComparer.Equals(fileName, "stdlib.h")
                && !PathComparer.Equals(fileName, "malloc.h"))
            {
                return false;
            }

            return _trustedIncludeDirectories.Any(
                directory => IsPathInsideDirectory(
                    filePath,
                    directory));
        }

        private static bool IsPathInsideDirectory(
            string filePath,
            string directoryPath)
        {
            try
            {
                var relative = Path.GetRelativePath(
                    directoryPath,
                    filePath);
                return !Path.IsPathFullyQualified(relative)
                    && !string.Equals(
                        relative,
                        "..",
                        StringComparison.Ordinal)
                    && !relative.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    && !relative.StartsWith(
                        $"..{Path.AltDirectorySeparatorChar}",
                        StringComparison.Ordinal);
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                return false;
            }
        }

        private static bool IsPointerWidthUnsignedInteger(
            ClangSharp.Type type,
            int pointerSizeBytes)
        {
            var canonical = type.Handle.CanonicalType;
            return canonical.kind is
                    CXTypeKind.CXType_UChar
                    or CXTypeKind.CXType_UShort
                    or CXTypeKind.CXType_UInt
                    or CXTypeKind.CXType_ULong
                    or CXTypeKind.CXType_ULongLong
                    or CXTypeKind.CXType_UInt128
                && canonical.SizeOf > 0
                && canonical.SizeOf == pointerSizeBytes;
        }

        private static bool IsFunctionPointerStorage(
            ClangSharp.Type type)
        {
            var canonical = type.Handle.CanonicalType;
            return canonical.kind == CXTypeKind.CXType_Pointer
                && canonical.PointeeType.CanonicalType.kind
                    is CXTypeKind.CXType_FunctionProto
                        or CXTypeKind.CXType_FunctionNoProto;
        }

        private static bool IsVoidPointer(ClangSharp.Type type)
        {
            var canonical = type.Handle.CanonicalType;
            return canonical.kind == CXTypeKind.CXType_Pointer
                && canonical.PointeeType.CanonicalType.kind
                    == CXTypeKind.CXType_Void;
        }

        private static bool CanProveThrowMayCrossBoundary(
            FunctionDecl function) =>
            function.ExceptionSpecType is
                CXCursor_ExceptionSpecificationKind
                    .CXCursor_ExceptionSpecificationKind_None
                or CXCursor_ExceptionSpecificationKind
                    .CXCursor_ExceptionSpecificationKind_MSAny;

        private static bool IntroducesConditionalControlFlow(
            Stmt statement) =>
            statement is IfStmt
                or ForStmt
                or CXXForRangeStmt
                or WhileStmt
                or DoStmt
                or SwitchStmt
                or ConditionalOperator
                or BinaryConditionalOperator
            || statement is BinaryOperator
            {
                Opcode:
                    CXBinaryOperatorKind.CXBinaryOperator_LAnd
                    or CXBinaryOperatorKind.CXBinaryOperator_LOr,
            };

        private static bool CanProveSimpleFallthrough(
            Stmt statement)
        {
            if (statement is NullStmt)
            {
                return true;
            }
            if (statement is DeclStmt
                {
                    IsSingleDecl: true,
                    SingleDecl: VarDecl
                    {
                        HasInit: true,
                    } variable,
                })
            {
                return IgnoreTransparentExpression(variable.Init)
                    is CXXNullPtrLiteralExpr;
            }
            if (statement is not BinaryOperator
                {
                    Opcode:
                        CXBinaryOperatorKind.CXBinaryOperator_Assign,
                } assignment)
            {
                return false;
            }
            return IgnoreTransparentExpression(assignment.LHS)
                    is DeclRefExpr
                && IgnoreTransparentExpression(assignment.RHS)
                    is DeclRefExpr;
        }

        private static bool TryBlockStartsWithDirectThrow(
            Stmt statement)
        {
            if (statement is LambdaExpr)
            {
                return false;
            }
            if (statement is CXXThrowExpr)
            {
                return true;
            }
            if (statement is not CompoundStmt)
            {
                return false;
            }
            foreach (var child in statement.Children)
            {
                if (child is NullStmt)
                {
                    continue;
                }
                return child is CXXThrowExpr;
            }
            return false;
        }

        private static bool IsCatchAll(
            CXXCatchStmt handler)
        {
            try
            {
                return handler.ExceptionDecl is null;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or NotSupportedException)
            {
                return false;
            }
        }

        private static Expr IgnoreTransparentExpression(Expr expression)
        {
            while (true)
            {
                var unwrapped = expression.IgnoreParens.IgnoreImplicit;
                if (unwrapped.Handle.Equals(expression.Handle))
                {
                    return expression;
                }
                expression = unwrapped;
            }
        }

        private void AddDirectCall(
            CallExpr call,
            string callerGraphKey)
        {
            var callLocation = TryCreatePointLocation(
                call.Location,
                _scopePolicy);
            FunctionDecl? directCallee;
            try
            {
                directCallee = call.DirectCallee;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or NotSupportedException)
            {
                MarkCallGraphIncomplete(
                    IncompleteCallGraphCode,
                    "Clang could not resolve a call expression to a direct declaration.",
                    callLocation);
                return;
            }

            if (directCallee is null)
            {
                MarkCallGraphIncomplete(
                    IncompleteCallGraphCode,
                    "An indirect or dependent call has no exact referenced declaration.",
                    callLocation);
                return;
            }

            var referencedUsr = directCallee.Handle.Usr.ToString();
            if (string.IsNullOrWhiteSpace(referencedUsr))
            {
                MarkCallGraphIncomplete(
                    IncompleteCallGraphCode,
                    "A direct call's referenced declaration has no stable Clang identity.",
                    callLocation);
                return;
            }
            if (_callCandidates.Count >= MaximumExtractedCalls)
            {
                MarkCallGraphIncomplete(
                    CallGraphLimitCode,
                    $"Direct-call extraction exceeds the {MaximumExtractedCalls}-item limit.",
                    callLocation);
                return;
            }
            if (!TryCreateCallEvidence(call.Extent, out var evidence))
            {
                MarkCallGraphIncomplete(
                    IncompleteCallGraphCode,
                    "A direct call has no approved source location.",
                    callLocation);
                return;
            }

            _callCandidates.Add(new NativeCallCandidate(
                callerGraphKey,
                referencedUsr,
                evidence));
        }

        private static string MethodQualifierSuffix(FunctionDecl function)
        {
            if (function is not CXXMethodDecl method)
            {
                return string.Empty;
            }

            var suffix = method.IsConst ? " const" : string.Empty;
            var functionType = function.Type as FunctionProtoType
                ?? function.Type.CanonicalType as FunctionProtoType;
            return functionType?.RefQualifier switch
            {
                CXRefQualifierKind.CXRefQualifier_LValue => suffix + " &",
                CXRefQualifierKind.CXRefQualifier_RValue => suffix + " &&",
                _ => suffix,
            };
        }

        private bool TryCreateCallEvidence(
            CXSourceRange range,
            out Evidence evidence)
        {
            evidence = null!;
            if (!TryCreateSourceLocation(range, _scopePolicy, out var location))
            {
                return false;
            }
            evidence = new Evidence(
                _request.ProducingFileId,
                location,
                CoreEvidenceConfidence.Exact,
                CallProducer,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["callKind"] = "direct",
                    ["target"] = _request.Target.RuntimeIdentifier,
                });
            return true;
        }

        private bool TryCreateNativeFactEvidence(
            CXSourceRange range,
            string producer,
            IReadOnlyDictionary<string, string> factMetadata,
            out Evidence evidence)
        {
            evidence = null!;
            if (!TryCreateSourceLocation(range, _scopePolicy, out var location))
            {
                return false;
            }

            var metadata = new Dictionary<string, string>(
                factMetadata.Count + 1,
                StringComparer.Ordinal)
            {
                ["target"] = _request.Target.RuntimeIdentifier,
            };
            foreach (var item in factMetadata)
            {
                metadata.Add(item.Key, item.Value);
            }
            evidence = new Evidence(
                _request.ProducingFileId,
                location,
                CoreEvidenceConfidence.Exact,
                producer,
                metadata);
            return true;
        }

        private void MarkCallGraphIncomplete(
            string code,
            string message,
            CoreSourceLocation? location)
        {
            _isCallGraphComplete = false;
            if (_callDiagnosticCount >= MaximumCallDiagnostics)
            {
                return;
            }
            _callDiagnosticCount++;
            _diagnostics.Add(new ClangExtractionDiagnostic(
                code,
                ClangExtractionDiagnosticSeverity.Warning,
                message,
                location));
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

            var qualifiedName = NormalizeQualifiedName(
                record.QualifiedName,
                record.Name);
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

            var qualifiedName = NormalizeQualifiedName(
                enumDeclaration.QualifiedName,
                enumDeclaration.Name);
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

            var qualifiedName = NormalizeQualifiedName(
                typedef.QualifiedName,
                typedef.Name);
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

        private static string NormalizeQualifiedName(
            string? qualifiedName,
            string fallback)
        {
            var value = string.IsNullOrWhiteSpace(qualifiedName)
                ? fallback
                : qualifiedName;
            return value
                .Trim()
                .Replace('\\', '/');
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
                : AbiParameterDirection.Unknown;
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
        bool? isPointeeConst = null;
        while (pointed.kind is CXTypeKind.CXType_Pointer
            or CXTypeKind.CXType_LValueReference
            or CXTypeKind.CXType_RValueReference)
        {
            pointerDepth++;
            var pointee = pointed.PointeeType.CanonicalType;
            isPointeeConst = pointee.IsConstQualified;
            pointed = pointee;
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
            fixedArrayLength,
            pointeeType: pointerDepth > 0
                ? MapTerminalType(pointed)
                : null,
            elementType: canonical.kind is CXTypeKind.CXType_ConstantArray
                or CXTypeKind.CXType_IncompleteArray
                or CXTypeKind.CXType_VariableArray
                or CXTypeKind.CXType_DependentSizedArray
                    ? MapTerminalType(canonical.ArrayElementType)
                    : null,
            isPointeeConst: isPointeeConst);
    }

    private static AbiTypeRef MapTerminalType(CXType type)
    {
        var canonical = type.CanonicalType;
        var category = MapTypeCategory(canonical, canonical, pointerDepth: 0);
        bool? signed = category switch
        {
            AbiTypeCategory.SignedInteger => true,
            AbiTypeCategory.UnsignedInteger => false,
            AbiTypeCategory.Enum when canonical.IsSigned => true,
            AbiTypeCategory.Enum when canonical.IsUnsigned => false,
            _ => null,
        };
        var name = type.Spelling.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = canonical.Spelling.ToString();
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "<unknown>";
        }
        return new AbiTypeRef(
            name,
            category,
            sizeBytes: KnownPositive(type.SizeOf),
            alignmentBytes: KnownPositive(type.AlignOf),
            isSigned: signed);
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
            || !ClangInputPreflight.TryResolveAllowedFile(
                startPath,
                scopePolicy,
                out var physicalStartPath)
            || !ClangInputPreflight.TryResolveAllowedFile(
                endPath,
                scopePolicy,
                out var physicalEndPath)
            || !PathEquals(physicalStartPath, physicalEndPath))
        {
            return false;
        }

        location = new CoreSourceLocation(
            physicalStartPath,
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
            || !ClangInputPreflight.TryResolveAllowedFile(
                path,
                scopePolicy,
                out var physicalPath))
        {
            return null;
        }
        return new CoreSourceLocation(
            physicalPath,
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

    /// <summary>
    /// Supplies the already-authorized managed-reader view of repository inputs to libclang.
    /// This keeps parsing in memory and avoids writing plaintext shadow files when an endpoint
    /// protection driver exposes different logical and physical byte streams to managed and
    /// native readers.
    /// </summary>
    private unsafe sealed class ClangUnsavedFileSet : IDisposable
    {
        private const int MaximumFiles = 4096;
        private const long MaximumFileBytes = 32L * 1024 * 1024;
        private const long MaximumTotalBytes = 256L * 1024 * 1024;

        private static readonly UTF8Encoding _strictUtf8 =
            new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly CXUnsavedFile[] _files;
        private readonly List<nint> _allocations;

        private ClangUnsavedFileSet(
            CXUnsavedFile[] files,
            List<nint> allocations)
        {
            _files = files;
            _allocations = allocations;
        }

        public ReadOnlySpan<CXUnsavedFile> Files => _files;

        public static bool TryCreate(
            IReadOnlyList<string> approvedInputFiles,
            IReadOnlyList<ClangInMemoryInput> inMemoryInputs,
            ScopePathPolicy scopePolicy,
            out ClangUnsavedFileSet files,
            out ClangExtractionDiagnostic? diagnostic)
        {
            ArgumentNullException.ThrowIfNull(approvedInputFiles);
            ArgumentNullException.ThrowIfNull(inMemoryInputs);
            ArgumentNullException.ThrowIfNull(scopePolicy);
            files = new ClangUnsavedFileSet([], []);
            diagnostic = null;
            var supplied = new Dictionary<string, byte[]>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            foreach (var input in inMemoryInputs)
            {
                if (input is null
                    || string.IsNullOrWhiteSpace(input.Path)
                    || input.Contents is null
                    || !ClangInputPreflight.TryResolveAllowedFile(
                        input.Path,
                        scopePolicy,
                        out var approvedPath)
                    || !supplied.TryAdd(approvedPath, input.Contents))
                {
                    diagnostic = UnsafeInput(
                        "An in-memory native input is duplicated or outside the approved scope.");
                    return false;
                }
            }
            var allInputs = approvedInputFiles
                .Concat(supplied.Keys)
                .Distinct(supplied.Comparer)
                .OrderBy(path => path, PathComparer)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (allInputs.Length > MaximumFiles)
            {
                diagnostic = UnsafeInput(
                    $"The approved native include graph exceeds the {MaximumFiles}-file in-memory input limit.");
                return false;
            }

            var entries = new CXUnsavedFile[allInputs.Length];
            var allocations = new List<nint>(
                checked(allInputs.Length * 2));
            long totalBytes = 0;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var cp936 = Encoding.GetEncoding(
                    936,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                for (var index = 0; index < allInputs.Length; index++)
                {
                    var path = allInputs[index];
                    var sourceBytes = supplied.TryGetValue(
                        path,
                        out var suppliedBytes)
                        ? suppliedBytes
                        : File.ReadAllBytes(path);
                    if (sourceBytes.LongLength > MaximumFileBytes
                        || checked(totalBytes + sourceBytes.LongLength)
                            > MaximumTotalBytes)
                    {
                        diagnostic = UnsafeInput(
                            "The approved native include graph exceeds the bounded in-memory input size.");
                        Free(allocations);
                        return false;
                    }
                    totalBytes += sourceBytes.LongLength;
                    if (StartsWithHsKey(sourceBytes)
                        || (sourceBytes.Contains((byte)0)
                            && !HasUtf16Bom(sourceBytes)))
                    {
                        diagnostic = UnsafeInput(
                            "An approved native source still exposes protected or NUL-containing logical bytes.");
                        Free(allocations);
                        return false;
                    }

                    byte[] utf8Bytes;
                    if (HasUtf16Bom(sourceBytes))
                    {
                        var encoding = sourceBytes[0] == 0xff
                            ? new UnicodeEncoding(
                                bigEndian: false,
                                byteOrderMark: true,
                                throwOnInvalidBytes: true)
                            : new UnicodeEncoding(
                                bigEndian: true,
                                byteOrderMark: true,
                                throwOnInvalidBytes: true);
                        utf8Bytes = Encoding.UTF8.GetBytes(
                            encoding.GetString(sourceBytes));
                    }
                    else try
                    {
                        _strictUtf8.GetString(sourceBytes);
                        utf8Bytes = sourceBytes;
                    }
                    catch (DecoderFallbackException)
                    {
                        try
                        {
                            utf8Bytes = Encoding.UTF8.GetBytes(
                                cp936.GetString(sourceBytes));
                        }
                        catch (DecoderFallbackException)
                        {
                            diagnostic = UnsafeInput(
                                "An approved native source is neither strict UTF-8 nor CP936 text.");
                            Free(allocations);
                            return false;
                        }
                    }

                    var fileNameBytes = Encoding.UTF8.GetBytes(path + "\0");
                    var fileNamePointer =
                        Marshal.AllocHGlobal(fileNameBytes.Length);
                    allocations.Add(fileNamePointer);
                    Marshal.Copy(
                        fileNameBytes,
                        0,
                        fileNamePointer,
                        fileNameBytes.Length);
                    var contentsPointer =
                        Marshal.AllocHGlobal(checked(utf8Bytes.Length + 1));
                    allocations.Add(contentsPointer);
                    Marshal.Copy(
                        utf8Bytes,
                        0,
                        contentsPointer,
                        utf8Bytes.Length);
                    Marshal.WriteByte(contentsPointer, utf8Bytes.Length, 0);
                    entries[index] = new CXUnsavedFile
                    {
                        Filename = (sbyte*)fileNamePointer,
                        Contents = (sbyte*)contentsPointer,
                        Length = (nuint)utf8Bytes.Length,
                    };
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or OverflowException)
            {
                Free(allocations);
                diagnostic = UnsafeInput(
                    $"An approved native input could not be prepared safely ({ex.GetType().Name}).");
                return false;
            }

            files = new ClangUnsavedFileSet(entries, allocations);
            return true;
        }

        public void Dispose()
        {
            Free(_allocations);
            _allocations.Clear();
        }

        private static bool StartsWithHsKey(ReadOnlySpan<byte> bytes) =>
            bytes.Length >= 5
            && bytes[0] == (byte)'H'
            && bytes[1] == (byte)'S'
            && bytes[2] == (byte)'K'
            && bytes[3] == (byte)'e'
            && bytes[4] == (byte)'y';

        private static bool HasUtf16Bom(ReadOnlySpan<byte> bytes) =>
            bytes.Length >= 2
            && ((bytes[0] == 0xff && bytes[1] == 0xfe)
                || (bytes[0] == 0xfe && bytes[1] == 0xff));

        private static void Free(IEnumerable<nint> allocations)
        {
            foreach (var allocation in allocations)
            {
                if (allocation != 0)
                {
                    Marshal.FreeHGlobal(allocation);
                }
            }
        }
    }

    private sealed record NativeExportCandidate(
        string Usr,
        bool IsDefinition,
        NativeExport Export);

    private sealed record NativeCallCandidate(
        string CallerSymbolCanonicalKey,
        string ReferencedDeclarationUsr,
        Evidence Evidence);

    private enum ReturnAllocationAnalysis
    {
        NotKnown,
        Known,
        Incomplete,
    }

    private sealed class CallbackStorageWriteState
    {
        public int WriteCount { get; set; }

        public int DirectWriteReferences { get; set; }

        public int ObservedReferences { get; set; }

        public NativeCallbackRetention? ProvenRetention { get; set; }
    }

    private sealed record NativeExportBodyFacts(
        IReadOnlyList<NativeCallbackRetention> RetainedCallbacks,
        NativeExceptionEscape? ExceptionEscape,
        NativeReturnAllocation? ReturnAllocation)
    {
        public static NativeExportBodyFacts Empty { get; } =
            new([], null, null);
    }
}
