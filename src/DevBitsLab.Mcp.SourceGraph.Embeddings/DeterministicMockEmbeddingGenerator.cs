using System.Security.Cryptography;
using System.Text;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Reproducible offline embedding generator for tests and the <c>--no-embeddings</c> path.
/// Builds a vector from a deterministic hash of the input text so the same string always
/// produces the same vector and similar inputs produce vectors close to each other in cosine
/// space. Used in tests to exercise the full pipeline without downloading the real model.
/// </summary>
public sealed class DeterministicMockEmbeddingGenerator : ICodeEmbeddingGenerator
{
    private readonly EmbeddingModelInfo _model;

    public DeterministicMockEmbeddingGenerator(EmbeddingModelInfo? model = null)
    {
        _model = model ?? new EmbeddingModelInfo("test/mock-deterministic", DefaultEmbeddingModel.Dimension);
    }

    public EmbeddingModelInfo Model => _model;

    public bool IsAvailable => true;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var output = new float[inputs.Count][];
        for (var i = 0; i < inputs.Count; i++)
        {
            output[i] = Embed(inputs[i] ?? string.Empty, _model.Dimension);
        }
        return Task.FromResult<IReadOnlyList<float[]>>(output);
    }

    /// <summary>
    /// Deterministic hash-bag-of-words embedding: each whitespace-separated token contributes
    /// a fixed unit vector (derived from SHA-256 of the token) into the output, then we
    /// L2-normalise. Words common to two inputs push their vectors together; words unique to
    /// one input rotate them apart. Stable across runs and processes.
    /// </summary>
    public static float[] Embed(string text, int dim)
    {
        var v = new float[dim];
        if (string.IsNullOrWhiteSpace(text)) return v;

        // Split on simple whitespace; lowercase to make the encoding case-insensitive so
        // "Calculator" and "calculator" map close together.
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.ToLowerInvariant().Trim();
            if (token.Length == 0) continue;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            // Use 4 successive bytes as a uint, mod dim, to pick a slot, and the next byte as
            // the sign. This gives reasonable spread.
            for (var off = 0; off + 4 < hash.Length; off += 5)
            {
                var slot = (int)(BitConverter.ToUInt32(hash, off) % (uint)dim);
                var sign = (hash[off + 4] & 1) == 0 ? +1f : -1f;
                v[slot] += sign;
            }
        }

        // L2-normalise so cosine = dot product in tests.
        double sum = 0;
        for (var i = 0; i < dim; i++) sum += v[i] * v[i];
        if (sum > 0)
        {
            var inv = (float)(1.0 / Math.Sqrt(sum));
            for (var i = 0; i < dim; i++) v[i] *= inv;
        }
        return v;
    }

    public void Dispose() { /* no-op */ }
}
