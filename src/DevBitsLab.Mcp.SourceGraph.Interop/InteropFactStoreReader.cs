using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Interop;

/// <summary>
/// One decoded interop fact paired with the exact annotation row that owns it.
/// </summary>
public sealed record StoredInteropFact<T>(
    StoredAnnotationRow Row,
    T Fact);

/// <summary>
/// A bounded, fail-closed read of one interop annotation flavor. Any malformed or conflicting
/// row makes <see cref="IsComplete"/> false so callers cannot turn missing facts into a proven
/// negative.
/// </summary>
public sealed record StoredInteropFactSnapshot<T>(
    IReadOnlyList<StoredInteropFact<T>> Facts,
    IReadOnlyList<InteropFactLoadFailure> Failures,
    bool WasTruncated)
{
    public bool IsComplete => Failures.Count == 0 && !WasTruncated;
}

public sealed record InteropFactLoadFailure(
    long? AnnotationId,
    string Flavor,
    string FilePath,
    string Reason);

/// <summary>
/// Reads versioned interop annotations without trusting arbitrary SQLite JSON. Every row passes
/// through <see cref="InteropFactPayloadCodec"/> and is checked against its host canonical key.
/// </summary>
public static class InteropFactStoreReader
{
    public const int DefaultMaximumRows = 50_000;
    public const int MaximumRows = 100_000;
    private const int PageSize = 1000;
    private const int MaximumFailureReasonCharacters = 256;

    public static Task<StoredInteropFactSnapshot<ManagedImport>>
        ReadManagedImportsAsync(
            IGraphStore store,
            int maximumRows = DefaultMaximumRows,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            store,
            InteropAnnotationFlavors.ManagedImport,
            maximumRows,
            (json, ownerFileId) =>
                InteropFactPayloadCodec.DecodeManagedImport(
                    json,
                    ownerFileId),
            fact => fact.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeManagedImport,
            cancellationToken);

    public static Task<StoredInteropFactSnapshot<ManagedCallbackUsageProjection>>
        ReadManagedCallbackUsagesAsync(
            IGraphStore store,
            int maximumRows = DefaultMaximumRows,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            store,
            InteropAnnotationFlavors.ManagedCallbackUsage,
            maximumRows,
            (json, ownerFileId) =>
                InteropFactPayloadCodec.DecodeManagedCallbackUsage(
                    json,
                    ownerFileId),
            fact => fact.Usage.CallerSymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeManagedCallbackUsage,
            cancellationToken,
            ManagedCallbackUsageIdentity);

    public static Task<StoredInteropFactSnapshot<ManagedReturnReleaseProjection>>
        ReadManagedReturnReleasesAsync(
            IGraphStore store,
            int maximumRows = DefaultMaximumRows,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            store,
            InteropAnnotationFlavors.ManagedReturnRelease,
            maximumRows,
            (json, ownerFileId) =>
                InteropFactPayloadCodec.DecodeManagedReturnRelease(
                    json,
                    ownerFileId),
            fact => fact.Release.CallerSymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeManagedReturnRelease,
            cancellationToken,
            ManagedReturnReleaseIdentity);

    public static Task<StoredInteropFactSnapshot<NativeExport>>
        ReadNativeExportsAsync(
            IGraphStore store,
            int maximumRows = DefaultMaximumRows,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            store,
            InteropAnnotationFlavors.NativeExport,
            maximumRows,
            (json, ownerFileId) =>
                InteropFactPayloadCodec.DecodeNativeExport(
                    json,
                    ownerFileId),
            fact => fact.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeNativeExport,
            cancellationToken);

    public static Task<StoredInteropFactSnapshot<AbiRecordLayout>>
        ReadAbiRecordsAsync(
            IGraphStore store,
            int maximumRows = DefaultMaximumRows,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            store,
            InteropAnnotationFlavors.AbiRecord,
            maximumRows,
            (json, ownerFileId) =>
                InteropFactPayloadCodec.DecodeAbiRecord(
                    json,
                    ownerFileId),
            fact => fact.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeAbiRecord,
            cancellationToken);

    public static Task<StoredInteropFactSnapshot<InteropMatchProjection>>
        ReadMatchesAsync(
            IGraphStore store,
            int maximumRows = DefaultMaximumRows,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            store,
            InteropAnnotationFlavors.Match,
            maximumRows,
            (json, _) => InteropFactPayloadCodec.DecodeMatch(json),
            fact => fact.ManagedSymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeMatch,
            cancellationToken);

    public static Task<StoredInteropFactSnapshot<InteropFindingProjection>>
        ReadFindingsAsync(
            IGraphStore store,
            int maximumRows = DefaultMaximumRows,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            store,
            InteropAnnotationFlavors.Finding,
            maximumRows,
            (json, _) => InteropFactPayloadCodec.DecodeFinding(json),
            fact => fact.ManagedSymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeFinding,
            cancellationToken,
            InteropFactPayloadCodec.EncodeFinding);

    private static async Task<StoredInteropFactSnapshot<T>> ReadAsync<T>(
        IGraphStore store,
        string flavor,
        int maximumRows,
        Func<string, long, T> decode,
        Func<T, string> hostCanonicalKey,
        Func<T, string> encode,
        CancellationToken cancellationToken,
        Func<T, string>? identityKey = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maximumRows is < 1 or > MaximumRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows),
                maximumRows,
                $"Interop fact row limit must be between 1 and {MaximumRows}.");
        }

        var failures = new List<InteropFactLoadFailure>();
        var factsByIdentity =
            new Dictionary<string, StoredInteropFact<T>>(StringComparer.Ordinal);
        var canonicalPayloadByIdentity =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var conflictingIdentities = new HashSet<string>(StringComparer.Ordinal);
        var rowsRead = 0;
        long afterId = 0;

        while (rowsRead < maximumRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Min(PageSize, maximumRows - rowsRead);
            var page = await store.ListAnnotationsByFlavorAsync(
                    flavor,
                    afterId,
                    limit,
                    cancellationToken)
                .ConfigureAwait(false);
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowsRead++;
                afterId = row.AnnotationId;
                try
                {
                    if (row.ArgsJson is null)
                    {
                        throw new InteropFactPayloadException(
                            "The interop annotation has no payload.");
                    }

                    var fact = decode(row.ArgsJson, row.FileId);
                    var factKey = hostCanonicalKey(fact);
                    if (!string.Equals(
                            factKey,
                            row.SymbolCanonicalKey,
                            StringComparison.Ordinal))
                    {
                        throw new InteropFactPayloadException(
                            "The payload canonical key does not match its annotation host.");
                    }

                    var factIdentity = identityKey?.Invoke(fact) ?? factKey;
                    var canonicalPayload = encode(fact);
                    if (conflictingIdentities.Contains(factIdentity))
                    {
                        continue;
                    }
                    if (canonicalPayloadByIdentity.TryGetValue(
                            factIdentity,
                            out var previousPayload))
                    {
                        if (string.Equals(
                                previousPayload,
                                canonicalPayload,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        factsByIdentity.Remove(factIdentity);
                        canonicalPayloadByIdentity.Remove(factIdentity);
                        conflictingIdentities.Add(factIdentity);
                        failures.Add(new InteropFactLoadFailure(
                            row.AnnotationId,
                            flavor,
                            row.FilePath,
                            "Conflicting payloads share one interop canonical key."));
                        continue;
                    }

                    factsByIdentity.Add(
                        factIdentity,
                        new StoredInteropFact<T>(row, fact));
                    canonicalPayloadByIdentity.Add(
                        factIdentity,
                        canonicalPayload);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (
                    ex is InteropFactPayloadException
                        or ArgumentException
                        or InvalidOperationException
                        or OverflowException)
                {
                    failures.Add(new InteropFactLoadFailure(
                        row.AnnotationId,
                        flavor,
                        row.FilePath,
                        Truncate(ex.Message)));
                }
            }

            if (page.Count < limit) break;
        }

        var wasTruncated = false;
        if (rowsRead == maximumRows)
        {
            var probe = await store.ListAnnotationsByFlavorAsync(
                    flavor,
                    afterId,
                    limit: 1,
                    cancellationToken)
                .ConfigureAwait(false);
            wasTruncated = probe.Count > 0;
            if (wasTruncated)
            {
                failures.Add(new InteropFactLoadFailure(
                    AnnotationId: null,
                    flavor,
                    FilePath: string.Empty,
                    $"Interop annotation scan exceeded the {maximumRows}-row bound."));
            }
        }

        var facts = factsByIdentity
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToArray();
        return new StoredInteropFactSnapshot<T>(
            facts,
            failures
                .OrderBy(failure => failure.AnnotationId ?? long.MaxValue)
                .ThenBy(failure => failure.FilePath, StringComparer.Ordinal)
                .ToArray(),
            wasTruncated);
    }

    private static string Truncate(string message) =>
        message.Length <= MaximumFailureReasonCharacters
            ? message
            : message[..MaximumFailureReasonCharacters];

    private static string ManagedCallbackUsageIdentity(
        ManagedCallbackUsageProjection projection) =>
        BuildManagedUsageIdentity(
            projection.ManagedImportSymbolCanonicalKey,
            projection.Usage.CallerSymbolCanonicalKey,
            projection.Usage.ParameterPosition.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            projection.Usage.Target,
            projection.Usage.Evidence);

    private static string ManagedReturnReleaseIdentity(
        ManagedReturnReleaseProjection projection) =>
        BuildManagedUsageIdentity(
            projection.ManagedImportSymbolCanonicalKey,
            projection.Release.CallerSymbolCanonicalKey,
            parameterPosition: string.Empty,
            projection.Release.Target,
            projection.Release.Evidence);

    private static string BuildManagedUsageIdentity(
        string importCanonicalKey,
        string callerCanonicalKey,
        string parameterPosition,
        InteropTarget target,
        Evidence evidence)
    {
        var location = evidence.Location;
        var components = new[]
        {
            importCanonicalKey,
            callerCanonicalKey,
            parameterPosition,
            target.RuntimeIdentifier.ToUpperInvariant(),
            ((int)target.Architecture).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)target.CompilerAbi).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            target.PointerSizeBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            target.DefaultPack.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            evidence.ProducingFileId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            location.StartLine.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            location.StartColumn.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            location.EndLine.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            location.EndColumn.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };
        var identity = new System.Text.StringBuilder();
        foreach (var component in components)
        {
            identity.Append(component.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            identity.Append(':');
            identity.Append(component);
        }
        return identity.ToString();
    }
}
