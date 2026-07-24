namespace DevBitsLab.Mcp.SourceGraph.Core.Security;

/// <summary>
/// Executable capabilities that require an explicit grant in the user-owned trust store.
/// Grants are intentionally independent: authorizing one capability never implies another.
/// </summary>
public enum ExecutionCapability
{
    MsBuildEvaluation,
    ProjectSourceGenerators,
    NativeParsing,
    PluginLanguageIndexer,
    PluginAnalyzer,
    PluginTool,
}

/// <summary>
/// Stable, machine-readable reasons returned by execution-trust evaluation.
/// </summary>
public enum ExecutionTrustReason
{
    Allowed,
    InvalidRequest,
    CapabilityNotApplicable,
    TrustStoreInsideRepository,
    TrustBoundaryResolutionFailed,
    TrustStoreContainsReparsePoint,
    TrustFileMissing,
    TrustFileReadFailed,
    TrustFileTooLarge,
    TrustFileMalformed,
    TrustSchemaUnsupported,
    RepositoryNotTrusted,
    PathPluginBundleMissing,
    PathPluginEntryMissing,
    PathPluginEntryOutsideBundle,
    PathPluginBundleContainsReparsePoint,
    PathPluginBundleContainsNonRegularFile,
    PathPluginBundleHasUnsupportedFileIdentity,
    PathPluginFingerprintReadFailed,
    PathPluginNotTrusted,
    PathPluginSnapshotRequired,
    NuGetPluginNotTrusted,
}

/// <summary>
/// Result of one trust decision. <see cref="ReasonCode"/> is stable for logs and structured
/// output; <see cref="SubjectFingerprint"/> is populated for path-plugin decisions so a future
/// explicit trust command can show the exact immutable bundle identity it evaluated.
/// </summary>
public sealed record ExecutionTrustDecision(
    bool IsAllowed,
    ExecutionTrustReason Reason,
    string? SubjectFingerprint = null)
{
    public string ReasonCode => ExecutionTrustReasonCodes.For(Reason);
}

/// <summary>
/// Read-only gate used by executable project and plugin pathways.
/// </summary>
public interface IExecutionTrustPolicy
{
    ExecutionTrustDecision EvaluateRepositoryCapability(
        string repositoryRoot,
        ExecutionCapability capability);

    ExecutionTrustDecision EvaluatePathPluginCapability(
        string repositoryRoot,
        string entryAssemblyPath,
        ExecutionCapability capability,
        string? bundleRoot = null);

    ExecutionTrustDecision EvaluateNuGetPluginCapability(
        string repositoryRoot,
        string packageId,
        string exactVersion,
        ExecutionCapability capability);
}

/// <summary>
/// Stable wire codes for <see cref="ExecutionTrustReason"/>.
/// </summary>
public static class ExecutionTrustReasonCodes
{
    public static string For(ExecutionTrustReason reason) =>
        reason switch
        {
            ExecutionTrustReason.Allowed => "allowed",
            ExecutionTrustReason.InvalidRequest => "invalid-request",
            ExecutionTrustReason.CapabilityNotApplicable => "capability-not-applicable",
            ExecutionTrustReason.TrustStoreInsideRepository => "trust-store-inside-repository",
            ExecutionTrustReason.TrustBoundaryResolutionFailed =>
                "trust-boundary-resolution-failed",
            ExecutionTrustReason.TrustStoreContainsReparsePoint =>
                "trust-store-contains-reparse-point",
            ExecutionTrustReason.TrustFileMissing => "trust-file-missing",
            ExecutionTrustReason.TrustFileReadFailed => "trust-file-read-failed",
            ExecutionTrustReason.TrustFileTooLarge => "trust-file-too-large",
            ExecutionTrustReason.TrustFileMalformed => "trust-file-malformed",
            ExecutionTrustReason.TrustSchemaUnsupported => "trust-schema-unsupported",
            ExecutionTrustReason.RepositoryNotTrusted => "repository-not-trusted",
            ExecutionTrustReason.PathPluginBundleMissing => "path-plugin-bundle-missing",
            ExecutionTrustReason.PathPluginEntryMissing => "path-plugin-entry-missing",
            ExecutionTrustReason.PathPluginEntryOutsideBundle =>
                "path-plugin-entry-outside-bundle",
            ExecutionTrustReason.PathPluginBundleContainsReparsePoint =>
                "path-plugin-bundle-contains-reparse-point",
            ExecutionTrustReason.PathPluginBundleContainsNonRegularFile =>
                "path-plugin-bundle-contains-non-regular-file",
            ExecutionTrustReason.PathPluginBundleHasUnsupportedFileIdentity =>
                "path-plugin-bundle-has-unsupported-file-identity",
            ExecutionTrustReason.PathPluginFingerprintReadFailed =>
                "path-plugin-fingerprint-read-failed",
            ExecutionTrustReason.PathPluginNotTrusted => "path-plugin-not-trusted",
            ExecutionTrustReason.PathPluginSnapshotRequired =>
                "path-plugin-snapshot-required",
            ExecutionTrustReason.NuGetPluginNotTrusted => "nuget-plugin-not-trusted",
            _ => "unknown",
        };
}
