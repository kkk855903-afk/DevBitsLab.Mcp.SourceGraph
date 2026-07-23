using System.Text;
using DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using TsNode = TreeSitter.Node;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Synthetic-subclass coverage for <see cref="TreeSitterLanguageIndexer{TGrammarConfig}"/>.
/// Exercises the parse + walk + emit boilerplate against the bundled JavaScript grammar
/// without committing to any real per-language plugin (TS lives in a separate change). The
/// stub subclass below mirrors the shape a future production indexer would take but stays
/// entirely inside the test assembly, so refactors of the abstract base surface as direct
/// test failures rather than bleeding through to in-tree consumers.
/// </summary>
public sealed class TreeSitterLanguageIndexerTests
{
    [Fact]
    public async Task Walks_javascript_source_and_emits_declaration_plus_filescanned()
    {
        var indexer = new StubJsIndexer();
        var bytes = Encoding.UTF8.GetBytes("function greet(name) { return name; }");
        var ctx = new IndexContext(
            filePath: "/repo/src/foo.js",
            contents: bytes,
            scopeId: "test",
            repoRoot: "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        var declaration = events.OfType<IndexEvent.SymbolDeclared>().Should().ContainSingle().Subject;
        declaration.Kind.Should().Be("function");
        declaration.Name.Should().Be("greet");

        var fs = events.OfType<IndexEvent.FileScanned>().Should().ContainSingle("the base must emit exactly one FileScanned per indexed file").Subject;
        fs.FilePath.Should().Be("/repo/src/foo.js");
        fs.ContentSha256.Should().HaveCount(32);
    }

    [Fact]
    public async Task Files_above_size_cap_return_no_events()
    {
        var indexer = new StubJsIndexer(maxFileSizeBytes: 8); // any non-trivial input exceeds this
        var bytes = Encoding.UTF8.GetBytes("function greet(name) { return name; }");
        var ctx = new IndexContext(
            filePath: "/repo/src/big.js",
            contents: bytes,
            scopeId: "test",
            repoRoot: "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        events.Should().BeEmpty("oversized files are skipped before the parser runs and produce no events at all");
    }

    [Fact]
    public async Task Cancellation_is_honored_at_node_walk_boundary()
    {
        var indexer = new StubJsIndexer();
        var bytes = Encoding.UTF8.GetBytes("function a() {} function b() {} function c() {}");
        var ctx = new IndexContext("/repo/src/multi.js", bytes, "test", "/repo");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await indexer.Invoking(i => i.IndexAsync(ctx, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Malformed_source_with_error_nodes_emits_only_filescanned()
    {
        var indexer = new StubJsIndexer();
        // Tree-sitter recovers from `function ;{` partially — the resulting tree's root
        // reports HasError = true. The base treats the whole tree as malformed and skips
        // emitting symbols (matching XAML's posture for malformed input). FileScanned still
        // fires so the watcher's SHA cache stays accurate.
        var bytes = Encoding.UTF8.GetBytes("function ;{ this is not js");
        var ctx = new IndexContext("/repo/src/broken.js", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        events.Should().ContainSingle(e => e is IndexEvent.FileScanned);
        events.OfType<IndexEvent.SymbolDeclared>().Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_source_yields_only_filescanned()
    {
        var indexer = new StubJsIndexer();
        var bytes = Encoding.UTF8.GetBytes(string.Empty);
        var ctx = new IndexContext("/repo/src/empty.js", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        events.Should().ContainSingle(e => e is IndexEvent.FileScanned);
        events.OfType<IndexEvent.SymbolDeclared>().Should().BeEmpty();
    }

    private sealed class StubJsIndexer : TreeSitterLanguageIndexer<StubJsConfig>
    {
        public StubJsIndexer(int maxFileSizeBytes = 10 * 1024 * 1024)
            : base(new StubJsConfig(maxFileSizeBytes)) { }

        protected override INodeKindMapper Mapper { get; } = new StubMapper();

        protected override IndexEvent.SymbolDeclared? OnDeclarationNode(TsNode node, NodeMapping mapping, IndexContext ctx)
        {
            // Pull the function name out of the AST: `function_declaration` has a named
            // `identifier` child whose text is the name.
            var nameNode = node.NamedChildren.FirstOrDefault(c => c.Type == "identifier");
            var name = nameNode?.Text ?? "(anon)";
            var (line, col) = TreeSitterAdapter.ToOneBased(node.StartPosition);
            var (endLine, endCol) = TreeSitterAdapter.ToOneBased(node.EndPosition);
            // The test stub uses `xaml:` as a placeholder scheme — its lexical-path body is
            // the most permissive of the accepted schemes (no kind-prefix structure to
            // satisfy). The scheme choice is irrelevant to what this test asserts (the
            // abstract-base pipeline's correctness), so we pick one that doesn't impose
            // language-specific rules on the stub's synthetic identifiers.
            return new IndexEvent.SymbolDeclared(
                canonicalKey: $"xaml:test:{ctx.FilePath}::{name}",
                name: name,
                fqn: name,
                kind: mapping.Kind,
                startLine: line,
                startColumn: col,
                endLine: endLine,
                endColumn: endCol);
        }

        protected override IndexEvent.ReferenceFound? OnReferenceNode(TsNode node, string referenceKind, IndexContext ctx) =>
            null; // not exercised in this test
    }

    private sealed class StubJsConfig : ITreeSitterGrammarConfig
    {
        public StubJsConfig(int maxFileSizeBytes)
        {
            Options = new LanguageIndexerOptions(maxFileSizeBytes: maxFileSizeBytes);
        }

        public string GrammarName => "JavaScript";
        public LanguageIndexerOptions Options { get; }
        public IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".js" };
    }

    private sealed class StubMapper : INodeKindMapper
    {
        public bool TryMapDeclaration(string nodeType, out NodeMapping mapping)
        {
            if (nodeType == "function_declaration")
            {
                mapping = new NodeMapping("function");
                return true;
            }
            mapping = default;
            return false;
        }

        public bool TryMapReference(string nodeType, out string referenceKind)
        {
            referenceKind = string.Empty;
            return false;
        }

        public bool TryMapEdge(string nodeType, out string edgeKindName)
        {
            edgeKindName = string.Empty;
            return false;
        }
    }
}
