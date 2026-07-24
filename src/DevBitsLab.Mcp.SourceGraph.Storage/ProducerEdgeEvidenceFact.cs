namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// One canonical-key edge occurrence rebuilt by a named producer. Unlike
/// <see cref="FileEdgeFact"/>, evidence is mandatory because this replacement API is intended
/// for derived cross-file relationships whose incremental ownership must remain explicit.
/// </summary>
public sealed record ProducerEdgeEvidenceFact(
    string SourceCanonicalKey,
    string TargetCanonicalKey,
    string Kind,
    IReadOnlyDictionary<string, string>? Metadata,
    FileEvidenceFact Evidence);
