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

    private static async Task<StoredInteropFactSnapshot<T>> ReadAsync<T>(
        IGraphStore store,
        string flavor,
        int maximumRows,
        Func<string, long, T> decode,
        Func<T, string> canonicalKey,
        Func<T, string> encode,
        CancellationToken cancellationToken)
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
        var factsByKey =
            new Dictionary<string, StoredInteropFact<T>>(StringComparer.Ordinal);
        var canonicalPayloadByKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var conflictingKeys = new HashSet<string>(StringComparer.Ordinal);
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
                    var factKey = canonicalKey(fact);
                    if (!string.Equals(
                            factKey,
                            row.SymbolCanonicalKey,
                            StringComparison.Ordinal))
                    {
                        throw new InteropFactPayloadException(
                            "The payload canonical key does not match its annotation host.");
                    }

                    var canonicalPayload = encode(fact);
                    if (conflictingKeys.Contains(factKey))
                    {
                        continue;
                    }
                    if (canonicalPayloadByKey.TryGetValue(
                            factKey,
                            out var previousPayload))
                    {
                        if (string.Equals(
                                previousPayload,
                                canonicalPayload,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        factsByKey.Remove(factKey);
                        canonicalPayloadByKey.Remove(factKey);
                        conflictingKeys.Add(factKey);
                        failures.Add(new InteropFactLoadFailure(
                            row.AnnotationId,
                            flavor,
                            row.FilePath,
                            "Conflicting payloads share one interop canonical key."));
                        continue;
                    }

                    factsByKey.Add(
                        factKey,
                        new StoredInteropFact<T>(row, fact));
                    canonicalPayloadByKey.Add(factKey, canonicalPayload);
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

        var facts = factsByKey
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
}
