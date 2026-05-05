using System.Buffers;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// ONNX-Runtime-backed embedding generator for <c>jinaai/jina-embeddings-v2-base-code</c>
/// (or another BERT-family encoder of the same shape). Holds an <see cref="InferenceSession"/>
/// open for the lifetime of the host process and applies WordPiece tokenisation via
/// <see cref="FastBertTokenizer.BertTokenizer"/>.
///
/// <para>
/// Pipeline per call: tokenise -&gt; pad to a per-batch max length -&gt; ONNX <c>Run</c> with
/// <c>input_ids</c> and <c>attention_mask</c> -&gt; mean-pool the last_hidden_state masked
/// by attention -&gt; L2-normalise. The output is one float[768] per input.
/// </para>
///
/// <para>
/// Construction performs no IO so it's cheap to instantiate. <see cref="EmbedAsync"/> lazily
/// loads the model the first time it's called; if loading fails (file missing, native runtime
/// missing) we flip <see cref="IsAvailable"/> false and the next caller (the hosted service)
/// shuts down the channel cleanly.
/// </para>
/// </summary>
public sealed class JinaCodeEmbeddingGenerator : ICodeEmbeddingGenerator
{
    private readonly string _modelOnnxPath;
    private readonly string _tokenizerJsonPath;
    private readonly ILogger _logger;
    private readonly EmbeddingModelInfo _model;
    private readonly int _maxTokens;

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private bool _initialised;
    private bool _available;
    private readonly object _initLock = new();
    // Serialises EmbedAsync calls. The interface contract only guarantees single-worker access,
    // but the host now wires one drain per scope (all sharing this singleton generator), so we
    // serialise inside the implementation rather than push the concern out to every caller.
    private readonly SemaphoreSlim _embedGate = new(initialCount: 1, maxCount: 1);

    public JinaCodeEmbeddingGenerator(
        string modelOnnxPath,
        string tokenizerJsonPath,
        EmbeddingModelInfo model,
        int maxTokens = 8192,
        ILogger? logger = null)
    {
        _modelOnnxPath = modelOnnxPath;
        _tokenizerJsonPath = tokenizerJsonPath;
        _model = model;
        _maxTokens = maxTokens;
        _logger = logger ?? NullLogger.Instance;
    }

    public EmbeddingModelInfo Model => _model;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialised();
            return _available;
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return Array.Empty<float[]>();
        EnsureInitialised();
        if (!_available || _session is null || _tokenizer is null)
        {
            throw new InvalidOperationException("Embedding generator is not available; check IsAvailable before calling.");
        }

        // Run the synchronous ONNX session call on a worker so we don't block the indexer's
        // task pump. ORT's Run() is reentrant-safe per session, but we serialise here because
        // the host now shares this singleton across one drain task per scope — the interface
        // only guarantees safety from a single background worker.
        await _embedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => EncodeBatch(inputs, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _embedGate.Release();
        }
    }

    private float[][] EncodeBatch(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var batch = inputs.Count;
        var maxTokens = Math.Min(_maxTokens, 512); // 512 is a safer default for v2-base-code's typical pretraining window
        var totalTokens = batch * maxTokens;

        var inputIds = ArrayPool<long>.Shared.Rent(totalTokens);
        var attentionMask = ArrayPool<long>.Shared.Rent(totalTokens);
        try
        {
            // Tokenise the whole batch in one call (FastBertTokenizer parallelises internally).
            var inputArray = new string[batch];
            for (var i = 0; i < batch; i++) inputArray[i] = inputs[i] ?? string.Empty;
            _tokenizer!.Encode(
                inputs: new ReadOnlyMemory<string>(inputArray),
                inputIds: new Memory<long>(inputIds, 0, totalTokens),
                attentionMask: new Memory<long>(attentionMask, 0, totalTokens),
                maximumTokens: maxTokens);

            ct.ThrowIfCancellationRequested();

            // Build the input tensors. Both shapes are (batch, maxTokens). DenseTensor's
            // wrap-existing-buffer ctor takes Memory<T> + ReadOnlySpan<int> for dims.
            var dims = new int[] { batch, maxTokens };
            var idsTensor = new DenseTensor<long>(new Memory<long>(inputIds, 0, totalTokens),
                (ReadOnlySpan<int>)dims);
            var maskTensor = new DenseTensor<long>(new Memory<long>(attentionMask, 0, totalTokens),
                (ReadOnlySpan<int>)dims);

            var inputsList = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
            };

            using var results = _session!.Run(inputsList);

            // Output is "last_hidden_state": (batch, seq, hidden). Mean-pool over the
            // attention-masked tokens, then L2-normalise.
            var hidden = results.First(r => r.Name is "last_hidden_state" or "logits" or "embeddings")
                                .AsTensor<float>();

            var dim = _model.Dimension;
            if (hidden.Dimensions.Length != 3 || hidden.Dimensions[2] != dim)
            {
                throw new InvalidOperationException(
                    $"Unexpected ONNX output shape [{string.Join(",", hidden.Dimensions.ToArray())}] — expected (batch, seq, {dim}).");
            }

            var output = new float[batch][];
            for (var b = 0; b < batch; b++)
            {
                var pooled = new float[dim];
                long maskSum = 0;
                for (var t = 0; t < maxTokens; t++)
                {
                    var m = attentionMask[b * maxTokens + t];
                    if (m == 0) continue;
                    maskSum++;
                    for (var d = 0; d < dim; d++)
                    {
                        pooled[d] += hidden[b, t, d];
                    }
                }
                if (maskSum > 0)
                {
                    var inv = 1f / maskSum;
                    for (var d = 0; d < dim; d++) pooled[d] *= inv;
                }
                Normalise(pooled);
                output[b] = pooled;
            }

            return output;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(inputIds);
            ArrayPool<long>.Shared.Return(attentionMask);
        }
    }

    private static void Normalise(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        if (sum <= 0) return;
        var inv = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
    }

    private void EnsureInitialised()
    {
        if (_initialised) return;
        lock (_initLock)
        {
            if (_initialised) return;
            try
            {
                if (!File.Exists(_modelOnnxPath))
                {
                    _logger.LogWarning("Embedding model {Path} not found; semantic search disabled", _modelOnnxPath);
                    _available = false;
                }
                else if (!File.Exists(_tokenizerJsonPath))
                {
                    _logger.LogWarning("Tokenizer config {Path} not found; semantic search disabled", _tokenizerJsonPath);
                    _available = false;
                }
                else
                {
                    var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    _session = new InferenceSession(_modelOnnxPath, options);
                    _tokenizer = new BertTokenizer();
                    _tokenizer.LoadTokenizerJsonAsync(_tokenizerJsonPath).GetAwaiter().GetResult();
                    _available = true;
                    _logger.LogInformation("Loaded embedding model {Model} (dim={Dim})", _model.ModelId, _model.Dimension);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Embedding model load failed; semantic search disabled");
                _available = false;
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
            }
            _initialised = true;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _tokenizer = null;
        _embedGate.Dispose();
    }
}
