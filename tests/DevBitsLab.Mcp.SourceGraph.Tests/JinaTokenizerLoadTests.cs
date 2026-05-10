using System;
using System.IO;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using FluentAssertions;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Regression guard for the FastBertTokenizer → Microsoft.ML.Tokenizers migration. Loads the real
/// <c>tokenizer.json</c> for <c>jinaai/jina-embeddings-v2-base-code</c> (the documented default
/// model) and asserts it loads cleanly via <see cref="JinaCodeEmbeddingGenerator.TryLoadTokenizer"/>
/// and produces the expected token-id sequence for a known input.
///
/// <para>
/// The fixture is **not committed** (the upstream Jina v2 release ships the tokenizer.json
/// under a non-permissive license that would risk redistribution) and is **never fetched at
/// test time**: doing synchronous HTTP IO from an <c>IClassFixture</c> constructor is a
/// well-known sync-over-async hang trap, and a slow / unreachable HuggingFace hangs the entire
/// test runner instead of failing fast. Instead the fixture probes two pre-existing locations:
/// </para>
/// <list type="number">
///   <item>The live cache populated by a real <c>serve</c> run
///         (<c>~/.cache/devbitslab.sourcegraph/models/jinaai_jina-embeddings-v2-base-code/tokenizer.json</c>).</item>
///   <item>The per-project gitignored cache at
///         <c>tests/fixtures/.cache/jina-v2-base-code-tokenizer.json</c> — populate this once
///         on a CI bootstrap step or a dev's first run if the live cache isn't expected to
///         exist (e.g. a fresh container).</item>
/// </list>
/// <para>
/// If neither path is populated, <see cref="SkipReason"/> is set and the tests register as
/// **skipped** (via <see cref="Xunit.SkippableFact"/>) — visible signal in the test runner
/// rather than a silent pass.
/// </para>
///
/// <para>
/// The expected token ids for <c>"Hello world"</c> were captured during the migration spike
/// against the same tokenizer.json file: <c>[0, 10564, 7509, 2]</c> (= <c>&lt;s&gt;</c> +
/// "Hello" + " world" + <c>&lt;/s&gt;</c>). If this assertion ever fails, either the upstream
/// tokenizer file changed or the loading dispatch in
/// <see cref="JinaCodeEmbeddingGenerator.TryLoadTokenizer"/> drifted.
/// </para>
/// </summary>
public sealed class JinaTokenizerLoadTests : IClassFixture<JinaTokenizerLoadTests.TokenizerFixture>
{
    private readonly TokenizerFixture _fixture;

    public JinaTokenizerLoadTests(TokenizerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void TryLoadTokenizer_succeedsAgainstRealJinaTokenizerJson()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(_fixture.Path, out var tokenizer, out var padId, out var error);

        ok.Should().BeTrue($"loading the real Jina tokenizer.json should succeed; got error: {error}");
        tokenizer.Should().NotBeNull();
        padId.Should().Be(1, "Jina v2's <pad> token sits at id 1 in tokenizer.json");
    }

    [SkippableFact]
    public void Tokenizer_encodesHelloWorld_toExpectedIds()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(_fixture.Path, out var tokenizer, out _, out _);
        ok.Should().BeTrue();

        var ids = tokenizer!.EncodeToIds(
            "Hello world",
            maxTokenCount: 16,
            out _, out _,
            considerPreTokenization: true,
            considerNormalization: true);

        // Captured during the migration spike (Microsoft.ML.Tokenizers 2.0 against
        // jinaai/jina-embeddings-v2-base-code's committed-as-of-2026-05-10 tokenizer.json).
        // Sequence is: <s>=0, "Hello", " world", </s>=2.
        ids.Should().Equal(new[] { 0, 10564, 7509, 2 });
    }

    [Fact]
    public void TryLoadTokenizer_returnsFalseWithReason_whenFileMalformed()
    {
        // Construct a tokenizer.json with a missing model.type to verify the graceful-fail path.
        var tmp = Path.Join(Path.GetTempPath(), $"bad-tokenizer-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, "{ \"model\": {} }");
        try
        {
            var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(tmp, out var tokenizer, out _, out var error);
            ok.Should().BeFalse();
            tokenizer.Should().BeNull();
            error.Should().NotBeNull();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TryLoadTokenizer_returnsFalse_whenModelTypeUnsupported()
    {
        var tmp = Path.Join(Path.GetTempPath(), $"unigram-tokenizer-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, "{ \"model\": { \"type\": \"Unigram\" }, \"added_tokens\": [] }");
        try
        {
            var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(tmp, out var tokenizer, out _, out var error);
            ok.Should().BeFalse();
            tokenizer.Should().BeNull();
            error.Should().Contain("Unigram");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>
    /// xUnit class fixture: resolves a path to the Jina v2 tokenizer.json by probing two
    /// pre-existing locations only. **Never reaches the network** — synchronous HTTP IO from
    /// an <c>IClassFixture</c> constructor hangs the test runner when the remote is slow or
    /// unreachable. If neither path is populated, <see cref="SkipReason"/> is set and tests
    /// register as skipped via <see cref="Xunit.SkippableFact"/>.
    /// </summary>
    public sealed class TokenizerFixture : IDisposable
    {
        public string Path { get; }
        public string? SkipReason { get; }

        public TokenizerFixture()
        {
            // 1. Live cache populated by a real `serve` run. Use ModelStore's own resolver so
            // the test honours XDG_CACHE_HOME / LOCALAPPDATA / ~/.cache fallback chain — the
            // hardcoded `~/.cache/...` path was a portability bug on Windows and on machines
            // with XDG_CACHE_HOME set.
            var liveCache = new ModelStore().FilePath(DefaultEmbeddingModel.ModelId, "tokenizer.json");
            if (File.Exists(liveCache))
            {
                Path = liveCache;
                return;
            }

            // 2. Per-project gitignored fixture cache. Populate manually for CI:
            //    curl -L -o tests/fixtures/.cache/jina-v2-base-code-tokenizer.json \
            //      https://huggingface.co/jinaai/jina-embeddings-v2-base-code/resolve/main/tokenizer.json
            var fixtureCache = System.IO.Path.Join(LocateRepoRoot(), "tests", "fixtures", ".cache", "jina-v2-base-code-tokenizer.json");
            if (File.Exists(fixtureCache))
            {
                Path = fixtureCache;
                return;
            }

            Path = string.Empty;
            SkipReason =
                $"Jina v2 tokenizer.json fixture not found at {liveCache} or {fixtureCache}. " +
                "Populate the live cache by running `sourcegraph-mcp serve` once, or pre-populate " +
                "the fixture cache from huggingface.co (see test class summary).";
        }

        public void Dispose() { }

        private static string LocateRepoRoot()
        {
            // Walk up from the test assembly location until we find Directory.Build.props.
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 12; i++)
            {
                if (File.Exists(System.IO.Path.Join(dir, "Directory.Build.props"))) return dir;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent is null || parent == dir) break;
                dir = parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
