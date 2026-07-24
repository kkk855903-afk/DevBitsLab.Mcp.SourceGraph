namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// One complete protobuf contract observation eligible to establish a persisted baseline.
/// Baselines are keyed by exact protobuf canonical key and are insert-only: the first complete
/// successful observation wins, so later field-number or streaming changes remain comparable to
/// real prior source evidence.
/// </summary>
public sealed record GrpcContractBaselineFact(
    string SymbolCanonicalKey,
    string ContractJson,
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>
/// Persisted first-successful protobuf contract observation. <see cref="ObservedAtUnixMs"/> is
/// storage-assigned in the same transaction that inserts the complete candidate set.
/// </summary>
public sealed record GrpcContractBaselineRow(
    string SymbolCanonicalKey,
    string ContractJson,
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    long ObservedAtUnixMs);
