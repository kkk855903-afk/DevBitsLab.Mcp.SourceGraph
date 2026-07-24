using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

/// <summary>
/// Isolation properties the native parser worker can honestly guarantee on every supported
/// platform. Network denial and reduced-privilege execution are deliberately reported as
/// unavailable until a platform sandbox implements them.
/// </summary>
public sealed record NativeWorkerIsolationCapabilities(
    bool SeparateProcess,
    bool SanitizedEnvironment,
    bool ProcessTreeTermination,
    bool NetworkIsolation,
    bool ReducedPrivilege)
{
    public static NativeWorkerIsolationCapabilities Baseline { get; } = new(
        SeparateProcess: true,
        SanitizedEnvironment: true,
        ProcessTreeTermination: true,
        NetworkIsolation: false,
        ReducedPrivilege: false);
}

/// <summary>Optional hard requirements checked before a native worker process is started.</summary>
public sealed record NativeWorkerIsolationRequirements(
    bool RequireNetworkIsolation = false,
    bool RequireReducedPrivilege = false);

/// <summary>A stable, structured native worker failure.</summary>
public sealed record NativeWorkerFailure(
    string Code,
    string Message,
    int? ExitCode = null,
    string? StandardError = null,
    bool StandardErrorTruncated = false);

/// <summary>Result of one isolated native extraction attempt.</summary>
public sealed record NativeWorkerClientResult(
    ClangNativeExtractionResult? Extraction,
    NativeWorkerFailure? Failure,
    NativeWorkerIsolationCapabilities Isolation)
{
    public bool IsSuccess => Extraction is not null && Failure is null;
}

internal sealed record NativeWorkerRequestEnvelope(
    int Version,
    string Kind,
    ClangNativeExtractionRequest Request);

internal sealed record NativeWorkerResponseEnvelope(
    int Version,
    string Kind,
    bool Success,
    ClangNativeExtractionResult? Result,
    NativeWorkerFailure? Failure,
    NativeWorkerIsolationCapabilities Isolation);

internal sealed class NativeWorkerProtocolException : Exception
{
    public NativeWorkerProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Strict, one-request/one-response protocol shared by the server client and its child process.
/// A four-byte big-endian length prefix prevents newline ambiguity and caps allocation before
/// JSON parsing.
/// </summary>
internal static class NativeWorkerProtocol
{
    public const int CurrentVersion = 2;
    public const string RequestKind = "native-extraction-request";
    public const string ResponseKind = "native-extraction-response";
    public const int MaximumRequestBytes = 1024 * 1024;
    public const int MaximumResponseBytes = 16 * 1024 * 1024;
    public const int MaximumStandardErrorBytes = 64 * 1024;

    private const int MaximumCollectionItems = 16 * 1024;
    private const int MaximumFunctions = 4096;
    private const int MaximumCalls = 8192;
    private const int MaximumCompilerArguments = 4096;
    private const int MaximumExcludePatterns = 1024;
    private const int MaximumMetadataEntries = 256;
    private const int MaximumStringCharacters = 32 * 1024;
    private const int MaximumTypeDepth = 32;
    private const int FrameHeaderBytes = sizeof(int);

    private static readonly UTF8Encoding _strictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false),
        },
    };

    public static byte[] EncodeRequest(ClangNativeExtractionRequest request)
    {
        ValidateRequest(request);
        return Serialize(
            new NativeWorkerRequestEnvelope(
                CurrentVersion,
                RequestKind,
                request),
            MaximumRequestBytes,
            "request-too-large");
    }

    public static NativeWorkerRequestEnvelope DecodeRequest(
        ReadOnlySpan<byte> payload)
    {
        var envelope = Deserialize<NativeWorkerRequestEnvelope>(
            payload,
            MaximumRequestBytes,
            "malformed-request");
        if (envelope.Version != CurrentVersion
            || !string.Equals(envelope.Kind, RequestKind, StringComparison.Ordinal))
        {
            throw new NativeWorkerProtocolException(
                "unsupported-request",
                "The native worker request version or kind is unsupported.");
        }
        if (envelope.Request is null)
        {
            throw new NativeWorkerProtocolException(
                "malformed-request",
                "The native worker request body is required.");
        }

        ValidateRequest(envelope.Request);
        return envelope;
    }

    public static byte[] EncodeResponse(NativeWorkerResponseEnvelope response)
    {
        ValidateResponseShape(response);
        return Serialize(
            response,
            MaximumResponseBytes,
            "response-too-large");
    }

    public static byte[] EncodeWorkerResponse(
        NativeWorkerResponseEnvelope response,
        ClangNativeExtractionRequest? approvedRequest)
    {
        ValidateResponseShape(response);
        if (response.Success)
        {
            if (approvedRequest is null)
            {
                throw InvalidResponse(
                    "A successful worker response requires its approved request.");
            }
            ValidateExtractionResultStrict(response.Result!, approvedRequest);
        }
        return Serialize(
            response,
            MaximumResponseBytes,
            "response-too-large");
    }

    public static NativeWorkerResponseEnvelope DecodeResponse(
        ReadOnlySpan<byte> payload,
        ClangNativeExtractionRequest request)
    {
        var envelope = Deserialize<NativeWorkerResponseEnvelope>(
            payload,
            MaximumResponseBytes,
            "malformed-response");
        ValidateResponseShape(envelope);
        if (envelope.Success)
        {
            ValidateExtractionResultStrict(envelope.Result!, request);
        }
        return envelope;
    }

    public static byte[] AddFrame(ReadOnlySpan<byte> payload, int maximumBytes)
    {
        if (payload.IsEmpty || payload.Length > maximumBytes)
        {
            throw new NativeWorkerProtocolException(
                "frame-too-large",
                "The native worker frame length is invalid.");
        }

        var framed = new byte[FrameHeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(FrameHeaderBytes));
        return framed;
    }

    public static ReadOnlyMemory<byte> RemoveSingleFrame(
        ReadOnlyMemory<byte> framed,
        int maximumBytes)
    {
        if (framed.Length < FrameHeaderBytes)
        {
            throw new NativeWorkerProtocolException(
                "malformed-frame",
                "The native worker frame header is incomplete.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(framed.Span);
        if (length <= 0 || length > maximumBytes)
        {
            throw new NativeWorkerProtocolException(
                "frame-too-large",
                "The native worker frame length is invalid.");
        }
        if (framed.Length != FrameHeaderBytes + length)
        {
            throw new NativeWorkerProtocolException(
                "malformed-frame",
                "The native worker stream must contain exactly one complete frame.");
        }

        return framed[FrameHeaderBytes..];
    }

    public static async Task<byte[]> ReadSingleFrameAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var header = new byte[FrameHeaderBytes];
        await ReadExactlyAsync(input, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > maximumBytes)
        {
            throw new NativeWorkerProtocolException(
                "frame-too-large",
                "The native worker frame length is invalid.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(input, payload, cancellationToken).ConfigureAwait(false);

        var trailing = new byte[1];
        if (await input.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new NativeWorkerProtocolException(
                "malformed-frame",
                "The native worker stream must contain exactly one frame.");
        }

        return payload;
    }

    public static async Task WriteFrameAsync(
        Stream output,
        ReadOnlyMemory<byte> payload,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var framed = AddFrame(payload.Span, maximumBytes);
        await output.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateRepositoryAndRequestRoots(
        string repositoryRoot,
        ClangNativeExtractionRequest request)
    {
        ValidateNonBlankString(repositoryRoot, "RepositoryRoot");
        ValidateRequest(request);
        if (!Path.IsPathFullyQualified(repositoryRoot))
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                "RepositoryRoot must be an absolute path.");
        }

        try
        {
            var repository = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(repositoryRoot));
            var scope = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(request.ScopeRoot));
            if (!IsSameOrDescendant(repository, scope)
                || !ScopePathPolicy.TryResolvePhysicalPath(
                    repository,
                    out var physicalRepository)
                || !ScopePathPolicy.TryResolvePhysicalPath(
                    scope,
                    out var physicalScope)
                || !IsSameOrDescendant(physicalRepository, physicalScope))
            {
                throw new NativeWorkerProtocolException(
                    "invalid-request",
                    "ScopeRoot must be inside the trusted repository root.");
            }
        }
        catch (NativeWorkerProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                "The repository or scope root is invalid.");
        }
    }

    private static byte[] Serialize<T>(
        T value,
        int maximumBytes,
        string oversizedCode)
    {
        using var output = new BoundedWriteStream(maximumBytes);
        try
        {
            JsonSerializer.Serialize(output, value, _jsonOptions);
        }
        catch (PayloadLimitExceededException)
        {
            throw new NativeWorkerProtocolException(
                oversizedCode,
                "The native worker payload exceeds its fixed byte limit.");
        }
        catch (Exception ex) when (
            ex is JsonException
                or NotSupportedException
                or ArgumentException)
        {
            throw new NativeWorkerProtocolException(
                "serialization-failed",
                "The native worker payload could not be serialized.");
        }
        var payload = output.ToArray();
        if (payload.Length == 0 || payload.Length > maximumBytes)
        {
            throw new NativeWorkerProtocolException(
                oversizedCode,
                "The native worker payload exceeds its fixed byte limit.");
        }
        return payload;
    }

    private static T Deserialize<T>(
        ReadOnlySpan<byte> payload,
        int maximumBytes,
        string malformedCode)
    {
        if (payload.IsEmpty || payload.Length > maximumBytes)
        {
            throw new NativeWorkerProtocolException(
                malformedCode,
                "The native worker payload length is invalid.");
        }

        try
        {
            var strictJson = _strictUtf8.GetString(payload);
            using (var document = JsonDocument.Parse(
                       strictJson,
                       new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 64,
                       }))
            {
                RejectDuplicateProperties(document.RootElement);
            }
            return JsonSerializer.Deserialize<T>(payload, _jsonOptions)
                ?? throw new NativeWorkerProtocolException(
                    malformedCode,
                    "The native worker payload is null.");
        }
        catch (NativeWorkerProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is JsonException
                or DecoderFallbackException
                or NotSupportedException
                or ArgumentException)
        {
            throw new NativeWorkerProtocolException(
                malformedCode,
                "The native worker payload is malformed.");
        }
    }

    private static void ValidateResponseShape(NativeWorkerResponseEnvelope response)
    {
        if (response.Version != CurrentVersion
            || !string.Equals(response.Kind, ResponseKind, StringComparison.Ordinal))
        {
            throw new NativeWorkerProtocolException(
                "unsupported-response",
                "The native worker response version or kind is unsupported.");
        }
        if (response.Isolation != NativeWorkerIsolationCapabilities.Baseline)
        {
            throw new NativeWorkerProtocolException(
                "invalid-response",
                "The native worker reported unsupported isolation capabilities.");
        }
        if (response.Success)
        {
            if (response.Result is null || response.Failure is not null)
            {
                throw new NativeWorkerProtocolException(
                    "invalid-response",
                    "A successful native worker response must contain only a result.");
            }
            return;
        }

        if (response.Result is not null || response.Failure is null)
        {
            throw new NativeWorkerProtocolException(
                "invalid-response",
                "A failed native worker response must contain only a failure.");
        }
        if (string.IsNullOrWhiteSpace(response.Failure.Code)
            || response.Failure.Code.Length > MaximumStringCharacters
            || !IsFailureCode(response.Failure.Code)
            || string.IsNullOrWhiteSpace(response.Failure.Message)
            || response.Failure.Message.Length > MaximumStringCharacters
            || response.Failure.ExitCode is not null
            || response.Failure.StandardError is not null
            || response.Failure.StandardErrorTruncated)
        {
            throw InvalidResponse(
                "The native worker failure payload contains invalid process-level fields.");
        }
    }

    private static void ValidateRequest(ClangNativeExtractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateNonBlankString(request.SourceFilePath, "SourceFilePath");
        ValidateNonBlankString(request.ScopeRoot, "ScopeRoot");
        if (!Path.IsPathFullyQualified(request.SourceFilePath)
            || !Path.IsPathFullyQualified(request.ScopeRoot))
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                "SourceFilePath and ScopeRoot must be absolute paths.");
        }
        if (request.ProducingFileId <= 0)
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                "ProducingFileId must be positive.");
        }
        ValidateTarget(request.Target, "Target");
        ValidateCollection(
            request.CompilerArguments,
            MaximumCompilerArguments,
            "CompilerArguments");
        foreach (var argument in request.CompilerArguments)
        {
            ValidateNonBlankString(argument, "CompilerArguments item");
        }
        if (request.LibraryName is not null)
        {
            ValidateNonBlankString(request.LibraryName, "LibraryName");
        }
        if (request.ExcludePatterns is not null)
        {
            ValidateCollection(
                request.ExcludePatterns,
                MaximumExcludePatterns,
                "ExcludePatterns");
            foreach (var pattern in request.ExcludePatterns)
            {
                ValidateNonBlankString(pattern, "ExcludePatterns item");
            }
        }

        try
        {
            var source = Path.GetFullPath(request.SourceFilePath);
            var scope = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(request.ScopeRoot));
            if (!IsSameOrDescendant(scope, source))
            {
                throw new NativeWorkerProtocolException(
                    "invalid-request",
                    "SourceFilePath must be inside ScopeRoot.");
            }
        }
        catch (NativeWorkerProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                "The native extraction paths are invalid.");
        }
    }

    private static void ValidateExtractionResult(
        ClangNativeExtractionResult result,
        ClangNativeExtractionRequest request)
    {
        ValidateCollection(result.Functions, MaximumFunctions, "Functions");
        ValidateCollection(result.Calls, MaximumCalls, "Calls");
        ValidateCollection(result.Types, MaximumCollectionItems, "Types");
        ValidateCollection(result.Exports, MaximumCollectionItems, "Exports");
        ValidateCollection(
            result.RecordLayouts,
            MaximumCollectionItems,
            "RecordLayouts");
        ValidateCollection(
            result.Diagnostics,
            MaximumCollectionItems,
            "Diagnostics");
        ValidateCollection(
            result.IncludedFiles,
            MaximumCollectionItems,
            "IncludedFiles");

        ScopePathPolicy policy;
        string physicalSource;
        try
        {
            if (!ScopePathPolicy.TryResolvePhysicalPath(
                    request.ScopeRoot,
                    out var physicalScope)
                || !ScopePathPolicy.TryResolvePhysicalPath(
                    request.SourceFilePath,
                    out physicalSource))
            {
                throw new NativeWorkerProtocolException(
                    "invalid-response",
                    "The approved native input paths can no longer be resolved.");
            }
            policy = new ScopePathPolicy(physicalScope, request.ExcludePatterns);
        }
        catch (NativeWorkerProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new NativeWorkerProtocolException(
                "invalid-response",
                "The approved native input paths can no longer be resolved.");
        }

        var included = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var file in result.IncludedFiles)
        {
            ValidateApprovedPath(file, policy, "IncludedFiles item", included);
        }
        var errorWithoutFacts = result.HasErrors
            && result.Functions.Count == 0
            && result.Calls.Count == 0
            && result.Types.Count == 0
            && result.Exports.Count == 0
            && result.RecordLayouts.Count == 0;
        if (!included.Contains(physicalSource) && !errorWithoutFacts)
        {
            throw new NativeWorkerProtocolException(
                "invalid-response",
                "The native worker result does not retain its translation-unit source.");
        }

        var definitionGraphKeys = new HashSet<string>(StringComparer.Ordinal);
        var definitionsByUsr =
            new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var function in result.Functions)
        {
            ValidateNonBlankString(function.SymbolCanonicalKey, "Function key");
            if (!IsNativeFunctionKey(function.SymbolCanonicalKey))
            {
                throw InvalidResponse(
                    "A native function key must use the c/cpp F scheme.");
            }
            ValidateNonBlankString(function.Name, "Function name");
            ValidateNonBlankString(function.QualifiedName, "Function qualified name");
            ValidateNonBlankString(function.DeclarationUsr, "Function declaration USR");
            ValidateNonBlankString(function.GraphCanonicalKey, "Function graph key");
            if (!string.Equals(
                    function.GraphCanonicalKey,
                    function.SymbolCanonicalKey,
                    StringComparison.Ordinal)
                && !IsNativeExportKey(function.GraphCanonicalKey))
            {
                throw InvalidResponse(
                    "A function graph key must be its declaration key or a native export key.");
            }
            if (function.IsMethod && function.HasCLinkage)
            {
                throw InvalidResponse(
                    "A C++ member function cannot carry C linkage.");
            }
            ValidateEnum(function.CallingConvention, "Function calling convention");
            ValidateType(function.ReturnType, 0);
            ValidateParameters(function.Parameters, request, policy, included);
            ValidateTargetEquivalent(
                function.Target!,
                request.Target,
                "Function target");
            ValidateEvidence(function.Evidence, request, policy, included);
            if (function.IsDefinition)
            {
                if (!definitionGraphKeys.Add(function.GraphCanonicalKey))
                {
                    throw InvalidResponse(
                        "A native function graph key is duplicated.");
                }
                if (definitionsByUsr.TryGetValue(
                        function.DeclarationUsr,
                        out var existing)
                    && !string.Equals(
                        existing,
                        function.GraphCanonicalKey,
                        StringComparison.Ordinal))
                {
                    throw InvalidResponse(
                        "A Clang declaration identity maps to conflicting definitions.");
                }
                definitionsByUsr[function.DeclarationUsr] =
                    function.GraphCanonicalKey;
            }
        }
        var observedCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in result.Calls)
        {
            ValidateNonBlankString(
                call.CallerSymbolCanonicalKey,
                "Call source key");
            ValidateNonBlankString(
                call.ReferencedDeclarationUsr,
                "Call referenced declaration USR");
            if (!definitionGraphKeys.Contains(call.CallerSymbolCanonicalKey))
            {
                throw InvalidResponse(
                    "A direct call source is not a function definition in the result.");
            }
            if (call.CalleeSymbolCanonicalKey is not null)
            {
                ValidateNonBlankString(
                    call.CalleeSymbolCanonicalKey,
                    "Call target key");
                if (!definitionsByUsr.TryGetValue(
                        call.ReferencedDeclarationUsr,
                        out var expectedTarget)
                    || !string.Equals(
                        call.CalleeSymbolCanonicalKey,
                        expectedTarget,
                        StringComparison.Ordinal))
                {
                    throw InvalidResponse(
                        "A direct call target does not match its referenced definition.");
                }
            }
            ValidateTargetEquivalent(call.Target, request.Target, "Call target");
            ValidateEvidence(call.Evidence, request, policy, included);
            var occurrenceKey = string.Join(
                "\n",
                call.CallerSymbolCanonicalKey,
                call.ReferencedDeclarationUsr,
                call.Evidence.Location.FilePath,
                call.Evidence.Location.StartLine,
                call.Evidence.Location.StartColumn,
                call.Evidence.Location.EndLine,
                call.Evidence.Location.EndColumn);
            if (!observedCalls.Add(occurrenceKey))
            {
                throw InvalidResponse(
                    "A direct call occurrence is duplicated.");
            }
        }
        if (!result.IsCallGraphComplete
            && !result.Diagnostics.Any(diagnostic =>
                diagnostic.Code.StartsWith("CLANG2", StringComparison.Ordinal)))
        {
            throw InvalidResponse(
                "An incomplete native call graph requires a bounded call diagnostic.");
        }
        foreach (var type in result.Types)
        {
            ValidateNonBlankString(type.SymbolCanonicalKey, "Type key");
            ValidateEnum(type.Kind, "Type declaration kind");
            ValidateNonBlankString(type.Name, "Type name");
            ValidateNonBlankString(type.QualifiedName, "Type qualified name");
            ValidateType(type.DeclaredType, 0);
            ValidateEvidence(type.Evidence, request, policy, included);
        }
        foreach (var export in result.Exports)
        {
            ValidateNonBlankString(export.SymbolCanonicalKey, "Export key");
            ValidateNonBlankString(export.ExportName, "Export name");
            ValidateEnum(export.CallingConvention, "Export calling convention");
            ValidateType(export.ReturnType, 0);
            ValidateParameters(export.Parameters, request, policy, included);
            ValidateTargetEquivalent(export.Target, request.Target, "Export target");
            ValidateEvidence(export.Evidence, request, policy, included);
            if (export.LibraryName is not null)
            {
                ValidateNonBlankString(export.LibraryName, "Export library name");
            }
            ValidateEnum(export.ModuleIdentitySource, "Export module identity source");
            ValidateCollection(
                export.RetainedCallbacks,
                MaximumCollectionItems,
                "RetainedCallbacks");
            foreach (var retention in export.RetainedCallbacks)
            {
                if (retention.ParameterPosition < 0)
                {
                    throw InvalidResponse("Callback parameter position is invalid.");
                }
                ValidateTargetEquivalent(
                    retention.Target,
                    request.Target,
                    "Callback target");
                ValidateEvidence(retention.Evidence, request, policy, included);
            }
            if (export.ExceptionEscape is not null)
            {
                ValidateTargetEquivalent(
                    export.ExceptionEscape.Target,
                    request.Target,
                    "Exception target");
                ValidateEvidence(
                    export.ExceptionEscape.Evidence,
                    request,
                    policy,
                    included);
            }
            if (export.ReturnAllocation is not null)
            {
                ValidateEnum(
                    export.ReturnAllocation.AllocatorFamily,
                    "Allocation family");
                ValidateTargetEquivalent(
                    export.ReturnAllocation.Target,
                    request.Target,
                    "Allocation target");
                ValidateEvidence(
                    export.ReturnAllocation.Evidence,
                    request,
                    policy,
                    included);
            }
        }
        foreach (var record in result.RecordLayouts)
        {
            ValidateNonBlankString(record.SymbolCanonicalKey, "Record key");
            ValidateEnum(record.Kind, "Record kind");
            ValidatePositiveOptional(record.SizeBytes, "Record size");
            ValidatePositiveOptional(record.AlignmentBytes, "Record alignment");
            ValidatePositiveOptional(record.Pack, "Record pack");
            ValidateCollection(record.Fields, MaximumCollectionItems, "Record fields");
            foreach (var field in record.Fields)
            {
                if (field.Order < 0 || field.OffsetBytes is < 0)
                {
                    throw InvalidResponse("A record field position is invalid.");
                }
                ValidateNonBlankString(field.Name, "Record field name");
                ValidatePositiveOptional(field.SizeBytes, "Record field size");
                ValidateType(field.Type, 0);
                ValidateEvidence(field.Evidence, request, policy, included);
            }
            ValidateTargetEquivalent(record.Target, request.Target, "Record target");
            ValidateEvidence(record.Evidence, request, policy, included);
        }
        foreach (var diagnostic in result.Diagnostics)
        {
            ValidateNonBlankString(diagnostic.Code, "Diagnostic code");
            ValidateEnum(diagnostic.Severity, "Diagnostic severity");
            ValidateNonBlankString(diagnostic.Message, "Diagnostic message");
            if (diagnostic.Location is not null)
            {
                ValidateLocation(diagnostic.Location, policy, included);
            }
        }
    }

    private static void ValidateExtractionResultStrict(
        ClangNativeExtractionResult result,
        ClangNativeExtractionRequest request)
    {
        try
        {
            ValidateExtractionResult(result, request);
        }
        catch (NativeWorkerProtocolException ex)
            when (ex.Code != "invalid-response")
        {
            throw InvalidResponse(ex.Message);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or NullReferenceException
                or OverflowException)
        {
            throw InvalidResponse(
                "The native extraction result contains malformed values.");
        }
    }

    private static void ValidateParameters(
        IReadOnlyList<AbiParameter> parameters,
        ClangNativeExtractionRequest request,
        ScopePathPolicy policy,
        IReadOnlySet<string> included)
    {
        ValidateCollection(parameters, MaximumCollectionItems, "Parameters");
        foreach (var parameter in parameters)
        {
            if (parameter.Position < 0)
            {
                throw InvalidResponse("A parameter position is invalid.");
            }
            ValidateString(parameter.Name, "Parameter name");
            ValidateType(parameter.Type, 0);
            ValidateEnum(parameter.Direction, "Parameter direction");
            ValidateLocation(parameter.Location, policy, included);
        }
    }

    private static void ValidateType(AbiTypeRef type, int depth)
    {
        if (type is null || depth > MaximumTypeDepth)
        {
            throw InvalidResponse("An ABI type is missing or exceeds the depth limit.");
        }
        ValidateNonBlankString(type.CanonicalName, "ABI type name");
        ValidateEnum(type.Category, "ABI type category");
        if (type.PointerDepth is < 0 or > MaximumTypeDepth)
        {
            throw InvalidResponse("An ABI pointer depth is invalid.");
        }
        ValidatePositiveOptional(type.SizeBytes, "ABI type size");
        ValidatePositiveOptional(type.AlignmentBytes, "ABI type alignment");
        ValidatePositiveOptional(type.FixedArrayLength, "ABI fixed-array length");
        if (type.StringEncoding is not null)
        {
            ValidateNonBlankString(type.StringEncoding, "ABI string encoding");
        }
        if (type.PointeeType is not null)
        {
            ValidateType(type.PointeeType, depth + 1);
        }
        if (type.ElementType is not null)
        {
            ValidateType(type.ElementType, depth + 1);
        }
    }

    private static void ValidateEvidence(
        Evidence evidence,
        ClangNativeExtractionRequest request,
        ScopePathPolicy policy,
        IReadOnlySet<string> included)
    {
        if (evidence is null || evidence.ProducingFileId != request.ProducingFileId)
        {
            throw InvalidResponse(
                "Native evidence does not belong to the approved producing file.");
        }
        ValidateLocation(evidence.Location, policy, included);
        ValidateEnum(evidence.Confidence, "Evidence confidence");
        ValidateNonBlankString(evidence.Producer, "Evidence producer");
        if (evidence.Metadata is null)
        {
            return;
        }
        if (evidence.Metadata.Count > MaximumMetadataEntries)
        {
            throw InvalidResponse("Evidence metadata exceeds the entry limit.");
        }
        foreach (var item in evidence.Metadata)
        {
            ValidateNonBlankString(item.Key, "Evidence metadata key");
            ValidateString(item.Value, "Evidence metadata value");
        }
    }

    private static void ValidateLocation(
        SourceLocation location,
        ScopePathPolicy policy,
        IReadOnlySet<string> included)
    {
        if (location is null
            || location.StartLine <= 0
            || location.StartColumn <= 0
            || location.EndLine <= 0
            || location.EndColumn <= 0
            || location.EndLine < location.StartLine
            || (location.EndLine == location.StartLine
                && location.EndColumn < location.StartColumn))
        {
            throw InvalidResponse("A native source location is invalid.");
        }

        ValidateApprovedPath(location.FilePath, policy, "Source location", null);
        string physical;
        try
        {
            if (!ScopePathPolicy.TryResolvePhysicalPath(location.FilePath, out physical)
                || !included.Contains(physical))
            {
                throw InvalidResponse(
                    "A native source location is not in the approved include graph.");
            }
        }
        catch (NativeWorkerProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            throw InvalidResponse("A native source location path is invalid.");
        }
    }

    private static void ValidateApprovedPath(
        string path,
        ScopePathPolicy policy,
        string name,
        ISet<string>? destination)
    {
        ValidateNonBlankString(path, name);
        try
        {
            if (!Path.IsPathFullyQualified(path)
                || policy.IsExcludedForDiscovery(path, out var resolved)
                || resolved is null
                || !File.Exists(resolved))
            {
                throw InvalidResponse($"{name} is outside the approved scope.");
            }
            destination?.Add(Path.GetFullPath(resolved));
        }
        catch (NativeWorkerProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            throw InvalidResponse($"{name} is invalid.");
        }
    }

    private static void ValidateTargetEquivalent(
        InteropTarget actual,
        InteropTarget expected,
        string name)
    {
        ValidateTarget(actual, name);
        if (!actual.IsAbiEquivalentTo(expected))
        {
            throw InvalidResponse($"{name} differs from the approved target.");
        }
    }

    private static void ValidateTarget(InteropTarget target, string name)
    {
        if (target is null)
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                $"{name} is required.");
        }
        ValidateNonBlankString(target.RuntimeIdentifier, $"{name} runtime identifier");
        ValidateEnum(target.Architecture, $"{name} architecture");
        ValidateEnum(target.CompilerAbi, $"{name} compiler ABI");
        if (target.PointerSizeBytes is not (4 or 8)
            || target.DefaultPack is < 1 or > 128
            || (target.DefaultPack & (target.DefaultPack - 1)) != 0)
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                $"{name} contains invalid ABI dimensions.");
        }
    }

    private static void ValidateCollection<T>(
        IReadOnlyCollection<T> values,
        int maximum,
        string name)
    {
        if (values is null || values.Count > maximum)
        {
            throw new NativeWorkerProtocolException(
                "limit-exceeded",
                $"{name} is missing or exceeds its item limit.");
        }
    }

    private static void ValidateEnum<T>(T value, string name)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw InvalidResponse($"{name} is invalid.");
        }
    }

    private static void ValidatePositiveOptional(int? value, string name)
    {
        if (value is <= 0)
        {
            throw InvalidResponse($"{name} must be positive when known.");
        }
    }

    private static void ValidateNonBlankString(string value, string name)
    {
        ValidateString(value, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new NativeWorkerProtocolException(
                "invalid-request",
                $"{name} must be non-blank.");
        }
    }

    private static void ValidateString(string value, string name)
    {
        if (value is null || value.Length > MaximumStringCharacters)
        {
            throw new NativeWorkerProtocolException(
                "limit-exceeded",
                $"{name} is missing or exceeds its character limit.");
        }
    }

    private static NativeWorkerProtocolException InvalidResponse(string message) =>
        new("invalid-response", message);

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new NativeWorkerProtocolException(
                        "duplicate-json-property",
                        "The native worker payload contains a duplicate JSON property.");
                }
                RejectDuplicateProperties(property.Value);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static bool IsFailureCode(string code) =>
        code.Length is > 0 and <= 128
        && code[0] != '-'
        && code[^1] != '-'
        && code.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');

    private static bool IsNativeFunctionKey(string canonicalKey) =>
        canonicalKey.StartsWith("c:F:", StringComparison.Ordinal)
        || canonicalKey.StartsWith("cpp:F:", StringComparison.Ordinal);

    private static bool IsNativeExportKey(string canonicalKey) =>
        canonicalKey.StartsWith("c:E:", StringComparison.Ordinal)
        || canonicalKey.StartsWith("cpp:E:", StringComparison.Ordinal);

    private static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(
                buffer[offset..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new NativeWorkerProtocolException(
                    "malformed-frame",
                    "The native worker frame ended before its declared length.");
            }
            offset += read;
        }
    }

    private sealed class PayloadLimitExceededException : Exception
    {
    }

    private sealed class BoundedWriteStream : Stream
    {
        private readonly int _maximumBytes;
        private readonly MemoryStream _inner;

        public BoundedWriteStream(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _inner = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _inner.ToArray();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0
                || count < 0
                || buffer.Length - offset < count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0
                || _inner.Length > _maximumBytes - additionalBytes)
            {
                throw new PayloadLimitExceededException();
            }
        }
    }
}
