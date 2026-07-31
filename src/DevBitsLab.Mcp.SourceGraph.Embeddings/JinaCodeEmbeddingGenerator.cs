using System.Buffers;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// ONNX-Runtime-backed embedding generator for any encoder paired with a HuggingFace-format
/// <c>tokenizer.json</c>. The class covers both BERT (WordPiece) and RoBERTa-style (BPE) tokenizers
/// — dispatched at load time from the <c>model.type</c> field of <c>tokenizer.json</c> — so models
/// like <c>jinaai/jina-embeddings-v2-base-code</c> (BPE/RoBERTa) and <c>BAAI/bge-base-en-v1.5</c>
/// (WordPiece/BERT) load through the same code path.
///
/// <para>
/// Pipeline per call: tokenise -&gt; pad to a per-batch max length -&gt; ONNX <c>Run</c> with
/// <c>input_ids</c> and <c>attention_mask</c> -&gt; mean-pool the last_hidden_state masked
/// by attention -&gt; L2-normalise. The output is one float[Dimension] per input.
/// </para>
///
/// <para>
/// Construction performs no IO so it's cheap to instantiate. <see cref="EmbedAsync"/> lazily
/// loads the model the first time it's called; if loading fails (file missing, native runtime
/// missing, unsupported tokenizer.model.type) we flip <see cref="IsAvailable"/> false and the
/// next caller (the hosted service) shuts down the channel cleanly.
/// </para>
/// </summary>
public sealed class JinaCodeEmbeddingGenerator : ICodeEmbeddingGenerator, IManagedEmbeddingRuntime
{
    private readonly string _modelOnnxPath;
    private readonly string _tokenizerJsonPath;
    private readonly ILogger _logger;
    private readonly EmbeddingModelInfo _model;
    private readonly int _maxTokens;
    private readonly TimeSpan _idleTimeout;

    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private long _padId;
    private bool _initialised;
    private bool _available;
    private bool _disposed;
    private DateTimeOffset? _loadedAt;
    private DateTimeOffset? _lastUsedAt;
    private readonly Timer _idleTimer;
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
        TimeSpan? idleTimeout = null,
        ILogger? logger = null)
    {
        _modelOnnxPath = modelOnnxPath;
        _tokenizerJsonPath = tokenizerJsonPath;
        _model = model;
        _maxTokens = maxTokens;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        if (_idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be positive.");
        }
        _logger = logger ?? NullLogger.Instance;
        _idleTimer = new Timer(
            static state => ((JinaCodeEmbeddingGenerator)state!).TryUnloadIfIdle(DateTimeOffset.UtcNow),
            this,
            _idleTimeout,
            _idleTimeout);
    }

    public EmbeddingModelInfo Model => _model;

    public bool IsAvailable
    {
        get
        {
            lock (_initLock)
            {
                if (_disposed) return false;
                if (_initialised) return _available;
                // Availability probing stays cheap and does not map the ONNX model into memory.
                // EmbedAsync performs the real tokenizer/session load on first use.
                return File.Exists(_modelOnnxPath) && File.Exists(_tokenizerJsonPath);
            }
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return Array.Empty<float[]>();
        // Run the synchronous ONNX session call on a worker so we don't block the indexer's
        // task pump. ORT's Run() is reentrant-safe per session, but we serialise here because
        // the host now shares this singleton across one drain task per scope — the interface
        // only guarantees safety from a single background worker.
        await _embedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureInitialised();
            if (!_available || _session is null || _tokenizer is null)
            {
                throw new InvalidOperationException("Embedding generator is not available; check IsAvailable before calling.");
            }
            _lastUsedAt = DateTimeOffset.UtcNow;
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
            // Per-string encode. Microsoft.ML.Tokenizers doesn't expose a batch encode that fills
            // a caller-provided buffer; per-string is fine for our typical batch (32 × ≤512 tokens)
            // since the ONNX run that follows dwarfs tokenisation cost.
            for (var b = 0; b < batch; b++)
            {
                ct.ThrowIfCancellationRequested();
                var text = inputs[b] ?? string.Empty;
                var ids = _tokenizer!.EncodeToIds(
                    text,
                    maxTokenCount: maxTokens,
                    out string? _,
                    out int _,
                    considerPreTokenization: true,
                    considerNormalization: true);
                var realLen = Math.Min(ids.Count, maxTokens);
                var rowOffset = b * maxTokens;
                for (var t = 0; t < realLen; t++)
                {
                    inputIds[rowOffset + t] = ids[t];
                    attentionMask[rowOffset + t] = 1;
                }
                for (var t = realLen; t < maxTokens; t++)
                {
                    inputIds[rowOffset + t] = _padId;
                    attentionMask[rowOffset + t] = 0;
                }
            }

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
                else if (TryLoadTokenizer(_tokenizerJsonPath, out var tokenizer, out var padId, out var loadError))
                {
                    var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    _session = new InferenceSession(_modelOnnxPath, options);
                    _tokenizer = tokenizer;
                    _padId = padId;
                    _available = true;
                    _loadedAt = DateTimeOffset.UtcNow;
                    _lastUsedAt = _loadedAt;
                    _logger.LogInformation("Loaded embedding model {Model} (dim={Dim})", _model.ModelId, _model.Dimension);
                }
                else
                {
                    _logger.LogWarning("Tokenizer load failed for {Path}: {Reason}; semantic search disabled", _tokenizerJsonPath, loadError);
                    _available = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Embedding model load failed; semantic search disabled");
                _available = false;
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
                _loadedAt = null;
            }
            _initialised = true;
        }
    }

    /// <summary>
    /// Parse <paramref name="tokenizerJsonPath"/> and construct the matching concrete tokenizer.
    /// Dispatches on <c>model.type</c>: <c>BPE</c> → <see cref="BpeTokenizer"/>; <c>WordPiece</c> →
    /// <see cref="WordPieceTokenizer"/>; anything else returns false with a descriptive reason.
    ///
    /// <para>
    /// Internal so unit tests can drive it directly; the live path goes through
    /// <see cref="EnsureInitialised"/>. The broad <c>catch (Exception)</c> below is intentional
    /// — this method's contract is "never throw, surface every failure shape (malformed JSON,
    /// missing properties, IO errors, ML.Tokenizers internals) as a structured `error` string"
    /// — and is dismissed in the GitHub code-scanning UI rather than via a code-side
    /// suppression because CodeQL queries (`cs/catch-of-all-exceptions`) don't honour Roslyn
    /// analyzer's <c>[SuppressMessage(CA1031)]</c> attributes.
    /// </para>
    /// </summary>
    internal static bool TryLoadTokenizer(string tokenizerJsonPath, out Tokenizer? tokenizer, out long padId, out string? error)
    {
        tokenizer = null;
        padId = 0;
        error = null;
        try
        {
            using var stream = File.OpenRead(tokenizerJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var modelEl = root.GetProperty("model");
            var modelType = modelEl.GetProperty("type").GetString();

            if (string.Equals(modelType, "BPE", StringComparison.Ordinal))
            {
                tokenizer = LoadBpe(root, modelEl, out padId);
                return true;
            }
            if (string.Equals(modelType, "WordPiece", StringComparison.Ordinal))
            {
                tokenizer = LoadWordPiece(root, modelEl, out padId);
                return true;
            }
            error = $"unsupported tokenizer.model.type: '{modelType}' (expected 'BPE' or 'WordPiece')";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static BpeTokenizer LoadBpe(JsonElement root, JsonElement modelEl, out long padId)
    {
        // Vocabulary: dict<string, int> serialised under model.vocab as a JSON object.
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in modelEl.GetProperty("vocab").EnumerateObject())
        {
            vocab[kv.Name] = kv.Value.GetInt32();
        }

        // Merges: list of "tokenA tokenB" strings (each line one BPE merge pair).
        var merges = new List<string>(modelEl.TryGetProperty("merges", out var mergesEl) ? mergesEl.GetArrayLength() : 0);
        if (mergesEl.ValueKind == JsonValueKind.Array)
        {
            merges.AddRange(mergesEl.EnumerateArray()
                .Select(m => m.GetString())
                .Where(s => s is not null)!);
        }

        // Special tokens from added_tokens (filter by `special: true`). Includes the pad token,
        // cls/sep, unk, mask. We honour the `special` flag rather than hard-coding a set of names
        // so non-standard names work.
        var specialTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("added_tokens", out var added) && added.ValueKind == JsonValueKind.Array)
        {
            // Single Where with the full predicate so the body is unconditional. Avoids CodeQL's
            // `cs/missed-where-opportunity` re-flagging when there's a body-level `if`.
            foreach (var t in added.EnumerateArray()
                .Where(t => t.TryGetProperty("special", out var sp) && sp.GetBoolean()
                    && t.TryGetProperty("content", out var ce) && ce.GetString() is not null
                    && t.TryGetProperty("id", out _)))
            {
                specialTokens[t.GetProperty("content").GetString()!] = t.GetProperty("id").GetInt32();
            }
        }

        // post_processor → cls/sep tokens (RoBERTa) or compatible. We feed these to BpeOptions's
        // BeginningOfSentenceToken / EndOfSentenceToken so the tokenizer auto-wraps every encode.
        string? clsToken = null;
        string? sepToken = null;
        if (root.TryGetProperty("post_processor", out var pp) && pp.ValueKind == JsonValueKind.Object)
        {
            if (pp.TryGetProperty("cls", out var clsEl) && clsEl.ValueKind == JsonValueKind.Array && clsEl.GetArrayLength() > 0)
            {
                clsToken = clsEl[0].GetString();
            }
            if (pp.TryGetProperty("sep", out var sepEl) && sepEl.ValueKind == JsonValueKind.Array && sepEl.GetArrayLength() > 0)
            {
                sepToken = sepEl[0].GetString();
            }
        }

        var unkToken = modelEl.TryGetProperty("unk_token", out var ut) ? ut.GetString() : null;

        // Pad id: either from a `<pad>` special token or from the vocab map. Falls back to 0
        // (the model still receives a plausible token for padded slots; attention_mask=0 keeps
        // those positions out of the pooled mean regardless).
        padId = 0;
        if (specialTokens.TryGetValue("<pad>", out var sp1)) padId = sp1;
        else if (specialTokens.TryGetValue("[PAD]", out var sp2)) padId = sp2;
        else if (vocab.TryGetValue("<pad>", out var v1)) padId = v1;
        else if (vocab.TryGetValue("[PAD]", out var v2)) padId = v2;

        var options = new BpeOptions(vocab)
        {
            Merges = merges,
            SpecialTokens = specialTokens,
            UnknownToken = unkToken,
            // ByteLevel BPE is the RoBERTa convention (Ġ-prefixed tokens for spaces). The Jina
            // v2 vocab uses Ġ throughout, so we enable it. A BERT-style BPE without Ġ would still
            // load correctly because EncodeToIds works on the raw vocab; the byte-level transform
            // is a no-op when the input already maps directly.
            ByteLevel = true,
            BeginningOfSentenceToken = clsToken,
            EndOfSentenceToken = sepToken,
        };
        return BpeTokenizer.Create(options);
    }

    private static WordPieceTokenizer LoadWordPiece(JsonElement root, JsonElement modelEl, out long padId)
    {
        // We extract the pad id ourselves so the EncodeBatch padding loop matches the tokenizer's
        // own pad id rather than always writing 0. The tokenizer itself is constructed from a
        // temp newline-delimited vocab file (Microsoft.ML.Tokenizers' WordPieceTokenizer.Create
        // expects standalone vocab.txt, not tokenizer.json's nested vocab object).
        padId = 0;
        if (root.TryGetProperty("added_tokens", out var added) && added.ValueKind == JsonValueKind.Array)
        {
            // Single Where with the full predicate so the body is unconditional. The outer `if`
            // gate above + this Where together cover all the implicit filtering in one place.
            var padToken = added.EnumerateArray()
                .Where(t => t.TryGetProperty("content", out var ce) && ce.GetString() is "[PAD]" or "<pad>"
                    && t.TryGetProperty("id", out _))
                .FirstOrDefault();
            if (padToken.ValueKind == JsonValueKind.Object)
            {
                padId = padToken.GetProperty("id").GetInt32();
            }
        }
        if (padId == 0 && modelEl.TryGetProperty("vocab", out var vocabEl) && vocabEl.ValueKind == JsonValueKind.Object)
        {
            if (vocabEl.TryGetProperty("[PAD]", out var p1)) padId = p1.GetInt32();
            else if (vocabEl.TryGetProperty("<pad>", out var p2)) padId = p2.GetInt32();
        }

        // The WordPieceTokenizer factory accepts a vocab path; it expects the standalone vocab.txt
        // file rather than tokenizer.json's nested vocab. Build a temp file with the vocab.
        var vocabPath = ExtractWordPieceVocabToTempFile(modelEl);
        try
        {
            return WordPieceTokenizer.Create(vocabPath, new WordPieceOptions());
        }
        finally
        {
            // Best-effort cleanup of the temp vocab file. Catch every non-cancellation
            // exception (IOException, UnauthorizedAccessException, anything else): the temp file
            // has already done its job feeding `WordPieceTokenizer.Create`, so a failed Delete
            // shouldn't take down the tokenizer load.
            try { File.Delete(vocabPath); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort cleanup */ }
        }
    }

    private static string ExtractWordPieceVocabToTempFile(JsonElement modelEl)
    {
        // tokenizer.json under model.vocab is a dict<string, int>; WordPieceTokenizer expects a
        // newline-delimited vocab file ordered by id. Reconstruct that ordering and write it.
        var entries = new List<(string Token, int Id)>();
        foreach (var kv in modelEl.GetProperty("vocab").EnumerateObject())
        {
            entries.Add((kv.Name, kv.Value.GetInt32()));
        }
        entries.Sort((a, b) => a.Id.CompareTo(b.Id));
        var tmp = Path.Join(Path.GetTempPath(), $"sourcegraph-mcp-vocab-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(tmp, entries.Select(e => e.Token));
        return tmp;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _embedGate.Wait();
        try
        {
            lock (_initLock)
            {
                _disposed = true;
                UnloadCore();
                _available = false;
                _initialised = true;
            }
        }
        finally
        {
            _embedGate.Release();
        }
        _embedGate.Dispose();
    }

    public EmbeddingRuntimeSnapshot GetRuntimeSnapshot()
    {
        lock (_initLock)
        {
            var fileBytes = File.Exists(_modelOnnxPath)
                ? new FileInfo(_modelOnnxPath).Length
                : 0;
            return new EmbeddingRuntimeSnapshot(
                Loaded: _session is not null,
                Available: !_disposed && (_initialised
                    ? _available
                    : fileBytes > 0 && File.Exists(_tokenizerJsonPath)),
                LoadedAt: _loadedAt,
                LastUsedAt: _lastUsedAt,
                IdleTimeout: _idleTimeout,
                ModelFileBytes: fileBytes,
                // ORT does not expose allocator residency per session. The ONNX file size is a
                // stable, explicitly-labelled estimate rather than a misleading process delta.
                ModelResidentEstimateBytes: _session is null ? 0 : fileBytes);
        }
    }

    public bool TryUnloadIfIdle(DateTimeOffset now)
    {
        try
        {
            if (!_embedGate.Wait(0)) return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        try
        {
            lock (_initLock)
            {
                if (_session is null || _lastUsedAt is null
                    || now - _lastUsedAt.Value < _idleTimeout)
                {
                    return false;
                }

                UnloadCore();
                // Allow the next EmbedAsync call to recreate the session. File availability is
                // re-evaluated then, so a cache removal while idle also fails closed.
                _initialised = false;
                _available = false;
                _logger.LogInformation(
                    "Unloaded embedding model {Model} after {Idle} of inactivity",
                    _model.ModelId,
                    _idleTimeout);
                return true;
            }
        }
        finally
        {
            _embedGate.Release();
        }
    }

    private void UnloadCore()
    {
        _session?.Dispose();
        _session = null;
        _tokenizer = null;
        _loadedAt = null;
    }
}
