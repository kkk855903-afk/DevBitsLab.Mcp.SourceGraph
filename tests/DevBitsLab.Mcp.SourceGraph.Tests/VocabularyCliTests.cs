using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// CLI coverage for <c>sourcegraph-mcp vocabulary list</c>: drift detection, exit codes, output
/// format, and the <c>--scope</c> / <c>--strict</c> flags.
///
/// <para>Each test seeds a temp repo root with a synthetic <c>.sourcegraph.json</c> + per-scope
/// SQLite DB. Output is captured via <see cref="StringWriter"/> redirection through the optional
/// <c>TextWriter</c> parameter on <see cref="VocabularyCli.RunListAsync"/>; we don't spawn a
/// subprocess. Exit codes are asserted against the integer return value of the handler.</para>
/// </summary>
public sealed class VocabularyCliTests : IDisposable
{
    private readonly string _tempRoot;

    public VocabularyCliTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "vocabulary-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    [Fact]
    public void Levenshtein_basicDistances()
    {
        VocabularyCli.Levenshtein("calls", "calls").Should().Be(0);
        VocabularyCli.Levenshtein("bind-path", "binds-path").Should().Be(1);
        VocabularyCli.Levenshtein("kitten", "sitting").Should().Be(3);
        VocabularyCli.Levenshtein("", "abc").Should().Be(3);
        VocabularyCli.Levenshtein("abc", "").Should().Be(3);
    }

    [Fact]
    public async Task RunListAsync_noDrift_exitsZero()
    {
        // Two scopes: each emits only kebab-case kinds that are far apart, so no drift candidates.
        WriteScopeConfig(new[] { ("frontend", "frontend.sln"), ("backend", "backend.sln") });
        await SeedScopeDbAsync("frontend",
            edges: new[] { ("calls", 10L), ("inherits", 3L) },
            symbols: new[] { ("class", 4L), ("method", 12L) },
            flavors: new[] { ("csharp-attribute", 2L) });
        await SeedScopeDbAsync("backend",
            edges: new[] { ("calls", 41L), ("uses-type", 7L) },
            symbols: new[] { ("class", 6L) },
            flavors: Array.Empty<(string, long)>());

        var (rc, stdout, _) = await RunAsync(new[] { "vocabulary" });

        rc.Should().Be(0);
        stdout.Should().Contain("Scope: frontend");
        stdout.Should().Contain("Scope: backend");
        // SDK constants get the [sdk] tag; the count from the GROUP BY query is rendered.
        stdout.Should().Contain("calls").And.Contain("[sdk, emitted: 10]");
        stdout.Should().Contain("[sdk, emitted: 41]");
        stdout.Should().Contain("inherits");
        stdout.Should().Contain("Drift candidates:");
        stdout.Should().Contain("(none)");
    }

    [Fact]
    public async Task RunListAsync_withDrift_exitsZeroByDefault()
    {
        WriteScopeConfig(new[] { ("default", "Sample.sln") });
        // bind-path vs binds-path: Levenshtein distance 1, both within the same edge_kinds list.
        await SeedScopeDbAsync("default",
            edges: new[] { ("bind-path", 8L), ("binds-path", 412L), ("calls", 1234L) },
            symbols: new[] { ("class", 1L) },
            flavors: Array.Empty<(string, long)>());

        var (rc, stdout, _) = await RunAsync(new[] { "vocabulary" });

        rc.Should().Be(0, "no --strict flag means drift is reported but the exit code stays clean");
        stdout.Should().Contain("Drift candidates:");
        stdout.Should().Contain("bind-path ~ binds-path");
        // Non-SDK kebab-case kind ends up in the unknown bucket today.
        stdout.Should().Contain("[unknown, emitted: 8]");
        stdout.Should().Contain("[unknown, emitted: 412]");
    }

    [Fact]
    public async Task RunListAsync_withDriftAndStrict_exitsTwo()
    {
        WriteScopeConfig(new[] { ("default", "Sample.sln") });
        await SeedScopeDbAsync("default",
            edges: new[] { ("bind-path", 8L), ("binds-path", 412L) },
            symbols: Array.Empty<(string, long)>(),
            flavors: Array.Empty<(string, long)>());

        var (rc, stdout, _) = await RunAsync(new[] { "vocabulary", "--strict" });

        rc.Should().Be(2, "--strict turns drift into a non-zero exit");
        stdout.Should().Contain("bind-path ~ binds-path");
    }

    [Fact]
    public async Task RunListAsync_strictWithoutDrift_exitsZero()
    {
        WriteScopeConfig(new[] { ("default", "Sample.sln") });
        await SeedScopeDbAsync("default",
            edges: new[] { ("calls", 5L), ("inherits", 2L) },
            symbols: Array.Empty<(string, long)>(),
            flavors: Array.Empty<(string, long)>());

        var (rc, _, _) = await RunAsync(new[] { "vocabulary", "--strict" });

        rc.Should().Be(0, "--strict only fails on drift; clean vocabulary is fine");
    }

    [Fact]
    public async Task RunListAsync_scopeFlag_filtersOutput()
    {
        WriteScopeConfig(new[] { ("frontend", "frontend.sln"), ("backend", "backend.sln") });
        await SeedScopeDbAsync("frontend",
            edges: new[] { ("renders-component", 99L) },
            symbols: Array.Empty<(string, long)>(),
            flavors: Array.Empty<(string, long)>());
        await SeedScopeDbAsync("backend",
            edges: new[] { ("calls", 1L) },
            symbols: Array.Empty<(string, long)>(),
            flavors: Array.Empty<(string, long)>());

        var (rc, stdout, _) = await RunAsync(new[] { "vocabulary", "--scope", "frontend" });

        rc.Should().Be(0);
        stdout.Should().Contain("Scope: frontend");
        stdout.Should().NotContain("Scope: backend");
        stdout.Should().Contain("renders-component");
        stdout.Should().NotContain("Scope: backend");
    }

    [Fact]
    public async Task RunListAsync_unknownScope_exitsOne()
    {
        WriteScopeConfig(new[] { ("default", "Sample.sln") });

        var (rc, _, stderr) = await RunAsync(new[] { "vocabulary", "--scope", "ghost" });

        rc.Should().Be(1, "an unknown scope id is operator error, not configuration drift");
        stderr.Should().Contain("ghost");
    }

    [Fact]
    public async Task RunListAsync_noDatabaseYet_emptyVocabularyAndNoDrift()
    {
        // Scope is configured but never indexed — the per-scope DB doesn't exist yet. The CLI
        // should report empty vocab cleanly and exit 0. This is the common case for a fresh clone.
        WriteScopeConfig(new[] { ("fresh", "fresh.sln") });

        var (rc, stdout, _) = await RunAsync(new[] { "vocabulary" });

        rc.Should().Be(0);
        stdout.Should().Contain("Scope: fresh");
        stdout.Should().Contain("never indexed");
        stdout.Should().Contain("(none)");
    }

    [Fact]
    public void HelpText_documentsTheSubcommand()
    {
        var help = CommandLine.HelpText;
        help.Should().Contain("vocabulary list");
        help.Should().Contain("--strict");
        help.Should().Contain("--scope");
        // The phrase "Drift candidates" wraps across a soft line break in the help block; check
        // for the unwrapped tokens individually so a future re-flow doesn't break this test.
        help.Should().Contain("Drift");
        help.Should().Contain("candidates");
        help.Should().Contain("Levenshtein");
    }

    // -- Helpers ---------------------------------------------------------------------------

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args)
    {
        // Anchor the CLI at the temp repo root by injecting --root. Avoids leaking CWD into the
        // test (CWD is process-scoped and other tests change it).
        var fullArgs = new List<string>(args);
        fullArgs.Add("--root");
        fullArgs.Add(_tempRoot);
        var cli = CommandLine.Parse(fullArgs.ToArray());
        // StringWriter has no real I/O to flush at dispose, so synchronous `using` is enough
        // (and survives an older runtime where TextWriter doesn't implement IAsyncDisposable).
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await VocabularyCli.RunSubcommandAsync(cli, stdout, stderr);
        return (rc, stdout.ToString(), stderr.ToString());
    }

    private void WriteScopeConfig(IReadOnlyList<(string Id, string Solution)> scopes)
    {
        // Hand-write minimal JSON so the test stays decoupled from the loader's serialiser
        // shape. Each scope gets a "solutions" entry; the file path is irrelevant to the CLI
        // since we never invoke the indexer here.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"scopes\": [");
        for (var i = 0; i < scopes.Count; i++)
        {
            var (id, sln) = scopes[i];
            sb.Append($"    {{ \"name\": \"{id}\", \"solutions\": [\"{sln}\"] }}");
            if (i < scopes.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        File.WriteAllText(Path.Join(_tempRoot, ScopeConfigLoader.FileName), sb.ToString());
    }

    /// <summary>
    /// Stand up an empty graph DB at the scope's expected on-disk location, then bypass the
    /// store and write rows directly via <see cref="SqliteConnection"/>. We need precise control
    /// of the kinds and counts; going through <see cref="SqliteGraphStore.UpsertSymbolAsync"/>
    /// would require seeding a file row per symbol and constraining the kind to the
    /// <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.SymbolKinds"/> constants, which defeats the
    /// drift-pair test.
    /// </summary>
    private async Task SeedScopeDbAsync(
        string scopeId,
        IReadOnlyList<(string Kind, long Count)> edges,
        IReadOnlyList<(string Kind, long Count)> symbols,
        IReadOnlyList<(string Flavor, long Count)> flavors)
    {
        var dbPath = ScopeLayout.ScopeDbPath(_tempRoot, scopeId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.EnsureSchemaAsync();
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString;
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // We need a placeholder file row so any FK-dependent inserts stay viable; the schema
        // doesn't FK-enforce on edges/symbols/annotations, but staying conservative here means
        // re-using the helper for richer scenarios later.
        await connection.ExecuteAsync(
            "INSERT INTO files (id, path, content_sha256, last_indexed_at) VALUES (1, '/virtual/seed.cs', X'00', 0);");

        var symbolId = 1L;
        foreach (var (kind, count) in symbols)
        {
            for (var i = 0; i < count; i++)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO symbols
                        (id, canonical_key, name, fqn, kind_name, file_id,
                         start_line, start_col, end_line, end_col, accessibility)
                    VALUES (@id, @key, @name, @fqn, @kind, 1, 0, 0, 0, 0, 0);
                    """,
                    new
                    {
                        id = symbolId,
                        key = $"csharp:T:{scopeId}.Sym{symbolId}",
                        name = $"Sym{symbolId}",
                        fqn = $"{scopeId}.Sym{symbolId}",
                        kind = kind,
                    });
                symbolId++;
            }
        }

        var nextSrc = symbolId;
        foreach (var (kind, count) in edges)
        {
            for (var i = 0; i < count; i++)
            {
                // Each edge needs a unique (src, dst, kind_name) tuple to avoid PK collision.
                await connection.ExecuteAsync(
                    "INSERT INTO edges (src, dst, kind_name) VALUES (@src, @dst, @kind);",
                    new { src = nextSrc, dst = nextSrc + 1, kind = kind });
                nextSrc += 2;
            }
        }

        var annotationId = 1L;
        foreach (var (flavor, count) in flavors)
        {
            for (var i = 0; i < count; i++)
            {
                // Capture the current id BEFORE incrementing so name/full_name match the inserted
                // id. The previous form mixed `id = annotationId++` (post-increment, returns the
                // pre-increment value) with `name = $"Attr{annotationId}"` (the post-increment
                // value), producing an off-by-one between id and name.
                var thisId = annotationId++;
                await connection.ExecuteAsync(
                    """
                    INSERT INTO annotations
                        (id, symbol_id, name, full_name, flavor)
                    VALUES (@id, 1, @name, @fn, @flavor);
                    """,
                    new
                    {
                        id = thisId,
                        name = $"Attr{thisId}",
                        fn = $"X.Attr{thisId}Attribute",
                        flavor = flavor,
                    });
            }
        }
    }
}
