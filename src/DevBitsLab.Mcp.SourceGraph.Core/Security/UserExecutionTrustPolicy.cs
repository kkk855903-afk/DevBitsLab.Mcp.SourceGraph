using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Core.Security;

/// <summary>
/// Evaluates executable capabilities against a versioned, user-owned JSON trust file.
/// </summary>
/// <remarks>
/// This type only reads metadata and bytes. It never creates or updates the trust file, restores
/// packages, accesses the network, starts a process, or loads an assembly.
/// </remarks>
public sealed class UserExecutionTrustPolicy : IExecutionTrustPolicy
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumTrustFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };
    private static readonly IReadOnlyDictionary<string, ExecutionCapability>
        _capabilitiesByName =
            Enum.GetValues<ExecutionCapability>()
                .ToDictionary(
                    capability => capability.ToString(),
                    capability => capability,
                    StringComparer.Ordinal);

    private readonly string _trustFilePath;

    public UserExecutionTrustPolicy(string? trustFilePath = null)
    {
        _trustFilePath = trustFilePath ?? DefaultTrustFilePath;
    }

    /// <summary>
    /// Default external trust-store location. On Windows this resolves to
    /// <c>%LOCALAPPDATA%\MedInteropLens\trust-v1.json</c>.
    /// </summary>
    public static string DefaultTrustFilePath
    {
        get
        {
            var localApplicationData =
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localApplicationData)
                ? string.Empty
                : Path.Join(localApplicationData, "MedInteropLens", "trust-v1.json");
        }
    }

    public string TrustFilePath => _trustFilePath;

    public ExecutionTrustDecision EvaluateRepositoryCapability(
        string repositoryRoot,
        ExecutionCapability capability)
    {
        if (!IsKnownCapability(capability))
        {
            return Deny(ExecutionTrustReason.InvalidRequest);
        }
        if (!IsRepositoryCapability(capability))
        {
            return Deny(ExecutionTrustReason.CapabilityNotApplicable);
        }

        var load = LoadTrust(repositoryRoot);
        if (load.Denial is not null) return load.Denial;

        var allowed = load.Snapshot!.Repositories.Any(
            grant =>
                string.Equals(
                    grant.Path,
                    load.RepositoryRoot,
                    PathComparison)
                && grant.Capabilities.Contains(capability));
        return allowed
            ? Allow()
            : Deny(ExecutionTrustReason.RepositoryNotTrusted);
    }

    public ExecutionTrustDecision EvaluatePathPluginCapability(
        string repositoryRoot,
        string entryAssemblyPath,
        ExecutionCapability capability,
        string? bundleRoot = null)
    {
        if (!IsKnownCapability(capability))
        {
            return Deny(ExecutionTrustReason.InvalidRequest);
        }
        if (!IsPluginCapability(capability))
        {
            return Deny(ExecutionTrustReason.CapabilityNotApplicable);
        }

        var load = LoadTrust(repositoryRoot);
        if (load.Denial is not null) return load.Denial;

        var fingerprint =
            PathPluginBundleFingerprint.Compute(entryAssemblyPath, bundleRoot);
        if (!fingerprint.IsSuccess)
        {
            return Deny(fingerprint.Reason);
        }

        var allowed = load.Snapshot!.PathPlugins.Any(
            grant =>
                string.Equals(
                    grant.Fingerprint,
                    fingerprint.Fingerprint,
                    StringComparison.OrdinalIgnoreCase)
                && grant.Capabilities.Contains(capability));
        return allowed
            ? Deny(
                ExecutionTrustReason.PathPluginSnapshotRequired,
                fingerprint.Fingerprint)
            : Deny(
                ExecutionTrustReason.PathPluginNotTrusted,
                fingerprint.Fingerprint);
    }

    public ExecutionTrustDecision EvaluateNuGetPluginCapability(
        string repositoryRoot,
        string packageId,
        string exactVersion,
        ExecutionCapability capability)
    {
        if (!IsKnownCapability(capability)
            || !IsValidPackageId(packageId)
            || !IsExactNuGetVersion(exactVersion))
        {
            return Deny(ExecutionTrustReason.InvalidRequest);
        }
        if (!IsPluginCapability(capability))
        {
            return Deny(ExecutionTrustReason.CapabilityNotApplicable);
        }

        var load = LoadTrust(repositoryRoot);
        if (load.Denial is not null) return load.Denial;

        var allowed = load.Snapshot!.NuGetPlugins.Any(
            grant =>
                string.Equals(
                    grant.PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    grant.ExactVersion,
                    exactVersion,
                    StringComparison.Ordinal)
                && grant.Capabilities.Contains(capability));
        return allowed
            ? Allow()
            : Deny(ExecutionTrustReason.NuGetPluginNotTrusted);
    }

    private TrustLoadResult LoadTrust(string repositoryRoot)
    {
        if (!TryNormalizeRepositoryRoot(repositoryRoot, out var normalizedRepositoryRoot)
            || string.IsNullOrWhiteSpace(_trustFilePath)
            || !Path.IsPathFullyQualified(_trustFilePath))
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.InvalidRequest));
        }

        string normalizedTrustFilePath;
        try
        {
            normalizedTrustFilePath = Path.GetFullPath(_trustFilePath);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.InvalidRequest));
        }

        var boundaryDenial = ValidateTrustBoundary(
            normalizedRepositoryRoot,
            normalizedTrustFilePath);
        if (boundaryDenial is not null)
        {
            return TrustLoadResult.Failed(boundaryDenial);
        }

        byte[] json;
        try
        {
            using var stream = new FileStream(
                normalizedTrustFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumTrustFileBytes)
            {
                return TrustLoadResult.Failed(
                    Deny(ExecutionTrustReason.TrustFileTooLarge));
            }

            json = new byte[stream.Length];
            stream.ReadExactly(json);
            if (stream.ReadByte() != -1)
            {
                return TrustLoadResult.Failed(
                    Deny(ExecutionTrustReason.TrustFileReadFailed));
            }
        }
        catch (FileNotFoundException)
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustFileMissing));
        }
        catch (DirectoryNotFoundException)
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustFileMissing));
        }
        catch (Exception ex) when (IsReadException(ex))
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustFileReadFailed));
        }

        boundaryDenial = ValidateTrustBoundary(
            normalizedRepositoryRoot,
            normalizedTrustFilePath);
        if (boundaryDenial is not null)
        {
            return TrustLoadResult.Failed(boundaryDenial);
        }

        TrustDocumentJson? document;
        try
        {
            if (!HasUniqueJsonPropertyNames(json))
            {
                return TrustLoadResult.Failed(
                    Deny(ExecutionTrustReason.TrustFileMalformed));
            }
            document = JsonSerializer.Deserialize<TrustDocumentJson>(
                json,
                _jsonOptions);
        }
        catch (JsonException)
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustFileMalformed));
        }
        catch (NotSupportedException)
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustFileMalformed));
        }

        if (document is null)
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustFileMalformed));
        }
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustSchemaUnsupported));
        }
        if (!TryCreateSnapshot(document, out var snapshot))
        {
            return TrustLoadResult.Failed(
                Deny(ExecutionTrustReason.TrustFileMalformed));
        }

        return TrustLoadResult.Succeeded(
            normalizedRepositoryRoot,
            snapshot);
    }

    private static bool TryCreateSnapshot(
        TrustDocumentJson document,
        out TrustSnapshot snapshot)
    {
        snapshot = TrustSnapshot.Empty;
        if (HasUnknownProperties(document.Extra)
            || document.Repositories is null
            || document.PathPlugins is null
            || document.NuGetPlugins is null)
        {
            return false;
        }

        var repositoryGrants =
            new List<RepositoryGrant>(document.Repositories.Count);
        var repositorySubjects = new HashSet<string>(PathComparer);
        foreach (var grant in document.Repositories)
        {
            if (grant is null
                || HasUnknownProperties(grant.Extra)
                || string.IsNullOrWhiteSpace(grant.Path)
                || !Path.IsPathFullyQualified(grant.Path)
                || !TryParseCapabilities(
                    grant.Capabilities,
                    IsRepositoryCapability,
                    out var capabilities))
            {
                return false;
            }

            string normalizedPath;
            try
            {
                normalizedPath =
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(grant.Path));
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                return false;
            }
            if (!repositorySubjects.Add(normalizedPath))
            {
                return false;
            }
            repositoryGrants.Add(
                new RepositoryGrant(normalizedPath, capabilities));
        }

        var pathPluginGrants =
            new List<PathPluginGrant>(document.PathPlugins.Count);
        var pathPluginSubjects = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var grant in document.PathPlugins)
        {
            if (grant is null
                || HasUnknownProperties(grant.Extra)
                || !IsValidFingerprint(grant.Fingerprint)
                || !TryParseCapabilities(
                    grant.Capabilities,
                    IsPluginCapability,
                    out var capabilities))
            {
                return false;
            }
            if (!pathPluginSubjects.Add(grant.Fingerprint!))
            {
                return false;
            }
            pathPluginGrants.Add(
                new PathPluginGrant(grant.Fingerprint!, capabilities));
        }

        var nuGetPluginGrants =
            new List<NuGetPluginGrant>(document.NuGetPlugins.Count);
        var nuGetPluginSubjects =
            new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var grant in document.NuGetPlugins)
        {
            if (grant is null
                || HasUnknownProperties(grant.Extra)
                || !IsValidPackageId(grant.PackageId)
                || !IsExactNuGetVersion(grant.ExactVersion)
                || !TryParseCapabilities(
                    grant.Capabilities,
                    IsPluginCapability,
                    out var capabilities))
            {
                return false;
            }
            if (!nuGetPluginSubjects.TryGetValue(
                    grant.PackageId!,
                    out var versions))
            {
                versions = new HashSet<string>(StringComparer.Ordinal);
                nuGetPluginSubjects[grant.PackageId!] = versions;
            }
            if (!versions.Add(grant.ExactVersion!))
            {
                return false;
            }
            nuGetPluginGrants.Add(
                new NuGetPluginGrant(
                    grant.PackageId!,
                    grant.ExactVersion!,
                    capabilities));
        }

        snapshot = new TrustSnapshot(
            repositoryGrants,
            pathPluginGrants,
            nuGetPluginGrants);
        return true;
    }

    private static bool TryParseCapabilities(
        IReadOnlyList<string>? values,
        Func<ExecutionCapability, bool> isApplicable,
        out IReadOnlySet<ExecutionCapability> capabilities)
    {
        var parsed = new HashSet<ExecutionCapability>();
        capabilities = parsed;
        if (values is null || values.Count == 0) return false;

        foreach (var value in values)
        {
            if (value is null
                || !_capabilitiesByName.TryGetValue(value, out var capability)
                || !isApplicable(capability)
                || !parsed.Add(capability))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidFingerprint(string? value)
    {
        const string prefix = "sha256:";
        if (value is null
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 64)
        {
            return false;
        }
        return value[prefix.Length..].All(Uri.IsHexDigit);
    }

    private static bool IsValidPackageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 100)
        {
            return false;
        }
        return value.All(
            character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_');
    }

    private static bool IsExactNuGetVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains('+', StringComparison.Ordinal))
        {
            return false;
        }

        var prereleaseSeparator = value.IndexOf('-');
        var core = prereleaseSeparator < 0
            ? value
            : value[..prereleaseSeparator];
        var prerelease = prereleaseSeparator < 0
            ? null
            : value[(prereleaseSeparator + 1)..];
        var coreParts = core.Split('.');
        if (coreParts.Length is not (3 or 4)
            || coreParts.Any(
                part =>
                    part.Length == 0
                    || !part.All(char.IsAsciiDigit)
                    || (part.Length > 1 && part[0] == '0')))
        {
            return false;
        }
        if (prerelease is null) return true;
        if (prerelease.Length == 0) return false;

        return prerelease.Split('.').All(
            identifier =>
                identifier.Length > 0
                && identifier.All(
                    character =>
                        char.IsAsciiLetterOrDigit(character)
                        || character == '-')
                && (!identifier.All(char.IsAsciiDigit)
                    || identifier.Length == 1
                    || identifier[0] != '0'));
    }

    private static bool IsKnownCapability(ExecutionCapability capability) =>
        Enum.IsDefined(capability);

    private static bool IsRepositoryCapability(ExecutionCapability capability) =>
        capability is ExecutionCapability.MsBuildEvaluation
            or ExecutionCapability.ProjectSourceGenerators
            or ExecutionCapability.NativeParsing;

    private static bool IsPluginCapability(ExecutionCapability capability) =>
        capability is ExecutionCapability.PluginLanguageIndexer
            or ExecutionCapability.PluginAnalyzer
            or ExecutionCapability.PluginTool;

    private static bool TryNormalizeRepositoryRoot(
        string repositoryRoot,
        out string normalizedRepositoryRoot)
    {
        normalizedRepositoryRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return false;
        try
        {
            normalizedRepositoryRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(repositoryRoot));
            return Path.IsPathFullyQualified(normalizedRepositoryRoot);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return false;
        }
    }

    private static ExecutionTrustDecision? ValidateTrustBoundary(
        string repositoryRoot,
        string trustFilePath)
    {
        if (IsSameOrDescendant(repositoryRoot, trustFilePath))
        {
            return Deny(ExecutionTrustReason.TrustStoreInsideRepository);
        }

        var reparseInspection = InspectTrustPathForReparsePoint(trustFilePath);
        if (reparseInspection == TrustPathInspection.ContainsReparsePoint)
        {
            return Deny(ExecutionTrustReason.TrustStoreContainsReparsePoint);
        }
        if (reparseInspection == TrustPathInspection.ResolutionFailed
            || !PathPluginBundleFingerprint.HasStandaloneRegularFileIdentity(
                trustFilePath)
            || !ScopePathPolicy.TryResolvePhysicalPath(
                repositoryRoot,
                out var physicalRepositoryRoot)
            || !ScopePathPolicy.TryResolvePhysicalPath(
                trustFilePath,
                out var physicalTrustFilePath))
        {
            return Deny(ExecutionTrustReason.TrustBoundaryResolutionFailed);
        }

        return IsSameOrDescendant(
            physicalRepositoryRoot,
            physicalTrustFilePath)
            ? Deny(ExecutionTrustReason.TrustStoreInsideRepository)
            : null;
    }

    private static TrustPathInspection InspectTrustPathForReparsePoint(
        string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return TrustPathInspection.ResolutionFailed;
        }

        var current = root;
        foreach (var segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return TrustPathInspection.ContainsReparsePoint;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException
                                       or DirectoryNotFoundException)
            {
                return ScopePathPolicy.TryResolvePhysicalPath(path, out _)
                    ? TrustPathInspection.Safe
                    : TrustPathInspection.ResolutionFailed;
            }
            catch (Exception ex) when (IsReadException(ex) || IsPathException(ex))
            {
                return TrustPathInspection.ResolutionFailed;
            }
        }

        return TrustPathInspection.Safe;
    }

    private static bool HasUniqueJsonPropertyNames(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;

                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0
                        || !objectProperties.Peek().Add(reader.GetString()!))
                    {
                        return false;
                    }
                    break;

                case JsonTokenType.EndObject:
                    if (objectProperties.Count == 0)
                    {
                        return false;
                    }
                    objectProperties.Pop();
                    break;
            }
        }
        return objectProperties.Count == 0;
    }

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        if (string.Equals(parent, candidate, PathComparison)) return true;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool HasUnknownProperties(
        IReadOnlyDictionary<string, JsonElement>? properties) =>
        properties is { Count: > 0 };

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static bool IsReadException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    private static ExecutionTrustDecision Allow(
        string? subjectFingerprint = null) =>
        new(true, ExecutionTrustReason.Allowed, subjectFingerprint);

    private static ExecutionTrustDecision Deny(
        ExecutionTrustReason reason,
        string? subjectFingerprint = null) =>
        new(false, reason, subjectFingerprint);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private enum TrustPathInspection
    {
        Safe,
        ContainsReparsePoint,
        ResolutionFailed,
    }

    private sealed record RepositoryGrant(
        string Path,
        IReadOnlySet<ExecutionCapability> Capabilities);

    private sealed record PathPluginGrant(
        string Fingerprint,
        IReadOnlySet<ExecutionCapability> Capabilities);

    private sealed record NuGetPluginGrant(
        string PackageId,
        string ExactVersion,
        IReadOnlySet<ExecutionCapability> Capabilities);

    private sealed record TrustSnapshot(
        IReadOnlyList<RepositoryGrant> Repositories,
        IReadOnlyList<PathPluginGrant> PathPlugins,
        IReadOnlyList<NuGetPluginGrant> NuGetPlugins)
    {
        public static TrustSnapshot Empty { get; } =
            new([], [], []);
    }

    private sealed record TrustLoadResult(
        string? RepositoryRoot,
        TrustSnapshot? Snapshot,
        ExecutionTrustDecision? Denial)
    {
        public static TrustLoadResult Succeeded(
            string repositoryRoot,
            TrustSnapshot snapshot) =>
            new(repositoryRoot, snapshot, null);

        public static TrustLoadResult Failed(ExecutionTrustDecision denial) =>
            new(null, null, denial);
    }

    private sealed record TrustDocumentJson
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("repositories")]
        public IReadOnlyList<RepositoryGrantJson?>? Repositories { get; init; } = [];

        [JsonPropertyName("pathPlugins")]
        public IReadOnlyList<PathPluginGrantJson?>? PathPlugins { get; init; } = [];

        [JsonPropertyName("nugetPlugins")]
        public IReadOnlyList<NuGetPluginGrantJson?>? NuGetPlugins { get; init; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }

    private sealed record RepositoryGrantJson
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("capabilities")]
        public IReadOnlyList<string>? Capabilities { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }

    private sealed record PathPluginGrantJson
    {
        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; init; }

        [JsonPropertyName("capabilities")]
        public IReadOnlyList<string>? Capabilities { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }

    private sealed record NuGetPluginGrantJson
    {
        [JsonPropertyName("packageId")]
        public string? PackageId { get; init; }

        [JsonPropertyName("version")]
        public string? ExactVersion { get; init; }

        [JsonPropertyName("capabilities")]
        public IReadOnlyList<string>? Capabilities { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }
}
