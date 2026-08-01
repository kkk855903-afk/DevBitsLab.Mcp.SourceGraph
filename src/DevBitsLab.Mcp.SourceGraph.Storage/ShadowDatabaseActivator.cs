namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Atomically promotes a fully-built shadow SQLite database and retains the displaced database
/// as a rollback artifact. Callers must checkpoint and close every connection first.
/// </summary>
public static class ShadowDatabaseActivator
{
    public static string Activate(
        string primaryPath,
        string shadowPath,
        string archiveDirectory,
        string archiveDiscriminator) =>
        Activate(primaryPath, shadowPath, archiveDirectory, archiveDiscriminator, null);

    internal static string Activate(
        string primaryPath,
        string shadowPath,
        string archiveDirectory,
        string archiveDiscriminator,
        Action<ShadowActivationStage>? injectFault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDiscriminator);

        var primary = Path.GetFullPath(primaryPath);
        var shadow = Path.GetFullPath(shadowPath);
        if (!File.Exists(shadow))
        {
            throw new FileNotFoundException("Validated shadow database does not exist.", shadow);
        }
        if (!string.Equals(Path.GetDirectoryName(primary), Path.GetDirectoryName(shadow),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The shadow database must be in the primary database directory so promotion stays atomic.");
        }

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(shadow + suffix))
            {
                throw new InvalidOperationException(
                    $"Shadow database still has an uncheckpointed SQLite sidecar: {shadow + suffix}");
            }
        }

        Directory.CreateDirectory(archiveDirectory);
        var scopeName = Path.GetFileNameWithoutExtension(primary);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var archive = Path.Join(
            Path.GetFullPath(archiveDirectory),
            $"{scopeName}-{archiveDiscriminator}-{timestamp}-{nonce}.db");

        if (!File.Exists(primary))
        {
            injectFault?.Invoke(ShadowActivationStage.BeforeAtomicPromotion);
            File.Move(shadow, primary);
            return archive;
        }

        // File.Replace is a single-filesystem atomic replacement on supported platforms. The
        // backup is created by the same operation, so a failed promotion leaves the primary in
        // place instead of exposing a half-built or missing database.
        injectFault?.Invoke(ShadowActivationStage.BeforeAtomicPromotion);
        File.Replace(shadow, primary, archive, ignoreMetadataErrors: true);
        return archive;
    }
}

internal enum ShadowActivationStage
{
    BeforeAtomicPromotion,
}
