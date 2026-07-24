namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// One file-owned derived projection participating in a multi-file atomic replacement.
/// Annotation flavors are scoped to the producing file, while edge evidence is scoped to the
/// exact producing-file/producer pair.
/// </summary>
public sealed record FileDerivedProjectionReplacement(
    string ProducingFilePath,
    string Producer,
    IReadOnlyCollection<string> AnnotationFlavors,
    IReadOnlyList<FileAnnotationFact> Annotations,
    IReadOnlyList<ProducerEdgeEvidenceFact> Edges);
