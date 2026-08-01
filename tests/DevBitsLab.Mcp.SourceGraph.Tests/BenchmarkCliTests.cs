using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using SymbolKinds = DevBitsLab.Mcp.SourceGraph.Sdk.SymbolKinds;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class BenchmarkCliTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "sourcegraph-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        await using var store = new SqliteGraphStore(_dbPath);
        await store.EnsureSchemaAsync();
        var fileId = await store.UpsertFileAsync(
            "/virtual/Calculator.cs", new byte[32], DateTimeOffset.UtcNow);
        var calculatorId = await store.UpsertSymbolAsync("csharp:T:Sample.Calculator", new Symbol(
            0, "Calculator", "Sample.Calculator", SymbolKinds.Class, fileId,
            1, 1, 20, 1, "class Calculator", null, "public", 1));
        var startId = await store.UpsertSymbolAsync("csharp:M:Sample.Calculator.Start", new Symbol(
            0, "Start", "Sample.Calculator.Start", SymbolKinds.Method, fileId,
            3, 5, 6, 5, "void Start()", calculatorId, "public", 1));
        var nativeId = await store.UpsertSymbolAsync("cpp:function:sample_start", new Symbol(
            0, "sample_start", "sample_start", SymbolKinds.Function, fileId,
            10, 1, 14, 1, "void sample_start()", null, null, 1));
        var evidence = new Evidence(
            fileId,
            new SourceLocation("/virtual/Calculator.cs", 4, 5, 4, 15),
            EvidenceConfidence.Exact,
            "benchmark-test");
        await store.BulkInsertEdgesAsync([
            new Edge(calculatorId, startId, "calls") { Evidence = evidence },
            new Edge(startId, nativeId, "native-implementation") { Evidence = evidence },
        ]);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public void Parser_acceptsBenchmarkFlags()
    {
        var cli = CommandLine.Parse([
            "benchmark", "--root", _tempDir, "--scope", "backend",
            "--golden", "tasks.json", "--cold", "--json",
        ]);

        cli.Subcommand.Should().Be("benchmark");
        cli.Cold.Should().BeTrue();
        cli.GoldenPath.Should().EndWith("tasks.json");
        cli.Json.Should().BeTrue();
        cli.ScopeId.Should().Be("backend");
    }

    [Fact]
    public async Task GoldenDefinition_reportsRecallLatencyAndPayloadSize()
    {
        var runner = new BenchmarkRunner(_dbPath, cold: true);
        var results = await runner.RunGoldenAsync([
            new BenchmarkGoldenTask(
                "definition", "definition", Query: "Sample.Calculator",
                ExpectedCanonicalKeys: ["csharp:T:Sample.Calculator"],
                MinResults: 1, MinRecall: 1.0),
        ], CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Status.Should().Be("passed", results[0].Message);
        results[0].Recall.Should().Be(1.0);
        results[0].ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
        results[0].ResponseBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GoldenDefinition_failsWhenExpectedKeyIsMissing()
    {
        var runner = new BenchmarkRunner(_dbPath, cold: false);
        var results = await runner.RunGoldenAsync([
            new BenchmarkGoldenTask(
                "regression", "definition", Query: "Sample.Calculator",
                ExpectedCanonicalKeys: ["csharp:T:Sample.Missing"],
                MinResults: 1, MinRecall: 1.0),
        ], CancellationToken.None);

        results[0].Status.Should().Be("failed");
        results[0].Recall.Should().Be(0.0, results[0].Message);
    }

    [Fact]
    public async Task GoldenRejectsDuplicateTaskNames()
    {
        var runner = new BenchmarkRunner(_dbPath, cold: false);
        var results = await runner.RunGoldenAsync([
            new BenchmarkGoldenTask("same", "definition", Query: "Sample.Calculator"),
            new BenchmarkGoldenTask("same", "definition", Query: "Sample.Calculator"),
        ], CancellationToken.None);

        results.Should().HaveCount(2);
        results[1].Status.Should().Be("failed");
        results[1].Message.Should().Contain("unique");
    }

    [Fact]
    public void GoldenJson_deserializesSnakeCaseIdentityLists()
    {
        const string json = """
            {
              "version": 1,
              "tasks": [{
                "name": "seven-hop",
                "kind": "path",
                "from": "csharp:M:Sample.Start",
                "to": "cpp:function:sample_start",
                "relations": ["calls", "native-implementation"],
                "expected_canonical_keys": ["cpp:function:sample_start"],
                "minHops": 7
              }]
            }
            """;

        var parsed = JsonSerializer.Deserialize<BenchmarkGoldenFile>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        parsed.Should().NotBeNull();
        parsed!.Tasks[0].RelationList.Should().Equal("calls", "native-implementation");
        parsed.Tasks[0].ExpectedCanonicalKeyList.Should().ContainSingle("cpp:function:sample_start");
        parsed.Tasks[0].MinHops.Should().Be(7);
    }

    [Fact]
    public async Task GoldenPath_requiresEvidenceBackedMinimumHopCount()
    {
        var runner = new BenchmarkRunner(_dbPath, cold: true, embeddingsEnabled: false);
        var results = await runner.RunGoldenAsync([
            new BenchmarkGoldenTask(
                "managed-to-native", "path",
                From: "csharp:T:Sample.Calculator",
                To: "cpp:function:sample_start",
                Relations: ["calls", "native-implementation"],
                MinHops: 2,
                MaxDepth: 3),
        ], CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Status.Should().Be("passed", results[0].Message);
        results[0].ResultCount.Should().Be(2);
    }

    [Fact]
    public async Task BuiltInSemanticProbe_checksPipelineHealthWithoutInventingRecallGolden()
    {
        var generatorFactoryCalls = 0;
        var runner = new BenchmarkRunner(
            _dbPath,
            cold: true,
            embeddingsEnabled: false,
            embeddingGeneratorFactory: () =>
            {
                generatorFactoryCalls++;
                throw new InvalidOperationException("Disabled benchmarks must not construct a generator.");
            });

        var results = await runner.RunBuiltInAsync(CancellationToken.None);

        generatorFactoryCalls.Should().Be(0);
        var semantic = results.Single(result => result.Name == "semantic-probe");
        semantic.Status.Should().Be("passed", semantic.Message);
        semantic.ResultCount.Should().BeGreaterThan(0);
        semantic.Recall.Should().BeNull(
            "an arbitrary sampled symbol is not a semantic relevance golden");
    }
}
