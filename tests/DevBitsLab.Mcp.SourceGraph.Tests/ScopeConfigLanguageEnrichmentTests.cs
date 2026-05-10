using System.IO;
using System.Linq;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Round-trip + negative coverage for the <c>language</c> and <c>enrichment</c> fields added to
/// <c>.sourcegraph.json</c> by <c>add-tree-sitter-language-indexer-host</c>. The loader validates
/// shape (kebab-case language, non-empty enrichment.lsp.command) and surfaces both fields on the
/// in-memory <see cref="Scope"/> record via the new init-only properties.
/// </summary>
public sealed class ScopeConfigLanguageEnrichmentTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "sg-scope-cfg-tests-" + Guid.NewGuid().ToString("N"));

    public ScopeConfigLanguageEnrichmentTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string json)
    {
        File.WriteAllText(Path.Join(_tempDir, ScopeConfigLoader.FileName), json);
        return _tempDir;
    }

    [Fact]
    public void Load_accepts_kebab_case_language_and_lsp_enrichment()
    {
        var root = WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/web/**/*.ts"],
                  "language": "typescript",
                  "enrichment": {
                    "lsp": { "command": "typescript-language-server", "args": ["--stdio"] }
                  }
                }
              ]
            }
            """);

        var config = ScopeConfigLoader.Load(root);

        var scope = config.Scopes.Should().ContainSingle().Subject;
        scope.Language.Should().Be("typescript");
        scope.Enrichment.Should().NotBeNull();
        scope.Enrichment!.Lsp.Should().NotBeNull();
        scope.Enrichment.Lsp!.Command.Should().Be("typescript-language-server");
        scope.Enrichment.Lsp.Args.Should().BeEquivalentTo(new[] { "--stdio" });
    }

    [Fact]
    public void Load_omits_both_fields_when_absent()
    {
        var root = WriteConfig("""
            {
              "scopes": [
                { "name": "backend", "solutions": ["backend.slnx"] }
              ]
            }
            """);

        var config = ScopeConfigLoader.Load(root);
        var scope = config.Scopes.Single();

        scope.Language.Should().BeNull();
        scope.Enrichment.Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_round_trips_language_and_enrichment()
    {
        var original = new ScopeConfig(
            new[]
            {
                new Scope(
                    Id: "frontend",
                    Name: "frontend",
                    Root: _tempDir,
                    ProjectSet: new ScopeProjectSet.Paths(new[] { "src/web/**/*.ts" }, Array.Empty<string>()),
                    Isolated: false,
                    LastIndexedAt: DateTimeOffset.MinValue)
                {
                    Language = "typescript",
                    Enrichment = new ScopeEnrichmentConfig(new LspEnrichmentConfig("tsserver", new[] { "--stdio" })),
                },
            },
            DefaultScope: "frontend",
            Plugins: Array.Empty<PluginRef>());

        ScopeConfigLoader.Save(_tempDir, original);
        var roundTripped = ScopeConfigLoader.Load(_tempDir);
        var scope = roundTripped.Scopes.Single();

        scope.Language.Should().Be("typescript");
        scope.Enrichment!.Lsp!.Command.Should().Be("tsserver");
        scope.Enrichment.Lsp.Args.Should().BeEquivalentTo(new[] { "--stdio" });
    }

    [Fact]
    public void Save_omits_language_and_enrichment_when_unset()
    {
        var config = ScopeConfigLoader.Synthesise(_tempDir, new[] { "backend.slnx" });
        ScopeConfigLoader.Save(_tempDir, config);
        var json = File.ReadAllText(Path.Join(_tempDir, ScopeConfigLoader.FileName));

        json.Should().NotContain("\"language\"");
        json.Should().NotContain("\"enrichment\"");
    }

    [Theory]
    [InlineData("TypeScript")]   // uppercase
    [InlineData("type_script")]  // underscore
    [InlineData("type--script")] // double-hyphen
    [InlineData("typescript-")]  // trailing hyphen
    [InlineData("")]             // empty
    public void Load_rejects_non_kebab_case_language(string badLanguage)
    {
        var root = WriteConfig($$"""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/**/*.ts"],
                  "language": "{{badLanguage}}"
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(root);
        act.Should().Throw<ScopeConfigException>().WithMessage("*language*");
    }

    [Fact]
    public void Load_rejects_enrichment_without_lsp()
    {
        var root = WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/**/*.ts"],
                  "enrichment": {}
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(root);
        act.Should().Throw<ScopeConfigException>().WithMessage("*enrichment*");
    }

    [Fact]
    public void Load_rejects_lsp_without_command()
    {
        var root = WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/**/*.ts"],
                  "enrichment": { "lsp": { "args": ["--stdio"] } }
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(root);
        act.Should().Throw<ScopeConfigException>().WithMessage("*command*");
    }

    [Fact]
    public void Load_rejects_enrichment_with_unknown_keys()
    {
        // Future enrichment kinds (embeddings, static-analysis, …) are reserved-but-rejected
        // until their indexers ship. Without `[JsonExtensionData]` on `ScopeEnrichmentJson`,
        // the deserializer would silently drop unknown keys and the operator would never know.
        var root = WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/**/*.ts"],
                  "enrichment": {
                    "lsp": { "command": "tsserver" },
                    "embeddings": { "model": "jina-v2" }
                  }
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(root);
        act.Should().Throw<ScopeConfigException>()
            .WithMessage("*embeddings*");
    }

    [Theory]
    [InlineData("   ")]      // whitespace-only
    [InlineData("\t\t")]     // tabs
    [InlineData("\n")]       // newline-only
    public void Load_rejects_lsp_with_whitespace_command(string badCommand)
    {
        // IsNullOrWhiteSpace catches commands that would parse cleanly but launch with a
        // useless FileNotFoundException at process-spawn time — operator gets the failure at
        // load time instead.
        var root = WriteConfig($$"""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/**/*.ts"],
                  "enrichment": { "lsp": { "command": "{{badCommand}}" } }
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(root);
        act.Should().Throw<ScopeConfigException>().WithMessage("*command*");
    }

    [Fact]
    public void Load_trims_whitespace_around_lsp_command()
    {
        var root = WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/**/*.ts"],
                  "enrichment": { "lsp": { "command": "  tsserver  ", "args": ["--stdio"] } }
                }
              ]
            }
            """);

        var config = ScopeConfigLoader.Load(root);
        config.Scopes.Single().Enrichment!.Lsp!.Command.Should().Be("tsserver");
    }

    [Fact]
    public void Load_rejects_lsp_with_empty_command()
    {
        var root = WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/**/*.ts"],
                  "enrichment": { "lsp": { "command": "" } }
                }
              ]
            }
            """);

        var act = () => ScopeConfigLoader.Load(root);
        act.Should().Throw<ScopeConfigException>().WithMessage("*command*");
    }
}
