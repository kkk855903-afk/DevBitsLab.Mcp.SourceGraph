namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Minimal-surface adapter around <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput, TEmbedding}"/>
/// for our internal pipeline. We use this thin wrapper rather than depending on the
/// MS-Extensions-AI <c>Embedding&lt;float&gt;</c> shape directly so we can plug in cheap
/// deterministic mocks in tests without dragging in the full SDK.
///
/// <para>
/// Implementations are expected to be safe to call from a single background worker.
/// Concurrent calls from many threads are not required.
/// </para>
/// </summary>
public interface ICodeEmbeddingGenerator : IDisposable
{
    /// <summary>The model identity that this instance produces vectors for.</summary>
    EmbeddingModelInfo Model { get; }

    /// <summary>True when the generator is ready to embed (model file present, runtime loaded).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Embed a batch of inputs and return one float[Dim] vector per input, in the same order.
    /// The batch size SHOULD be in the 16-32 range to amortise ONNX <c>Run</c> startup cost.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);

    /// <summary>
    /// Embed an interactive semantic-search query. Implementations may prioritize this work
    /// ahead of queued background indexing batches. The default preserves compatibility with
    /// generators that do not need a scheduler.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedQueryAsync(
        IReadOnlyList<string> inputs,
        CancellationToken ct = default) =>
        EmbedAsync(inputs, ct);

    /// <summary>Snapshot of inference-gate wait time for diagnostics and benchmarks.</summary>
    EmbeddingInferenceStatistics InferenceStatistics =>
        new(0, 0, 0, 0);
}

/// <summary>
/// Always-unavailable embedding generator. Registered when <c>--no-embeddings</c> is set so
/// the DI graph stays uniform and the <c>semantic_search</c> tool can branch on
/// <see cref="ICodeEmbeddingGenerator.IsAvailable"/> rather than asking whether the
/// generator is registered at all.
/// </summary>
public sealed class DisabledEmbeddingGenerator : ICodeEmbeddingGenerator
{
    public DisabledEmbeddingGenerator(EmbeddingModelInfo model) { Model = model; }
    public EmbeddingModelInfo Model { get; }
    public bool IsAvailable => false;
    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>());
    public void Dispose() { }
}
