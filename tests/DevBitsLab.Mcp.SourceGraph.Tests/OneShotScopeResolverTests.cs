using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class OneShotScopeResolverTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "sourcegraph-one-shot-scope-" + Guid.NewGuid().ToString("N"));

    public OneShotScopeResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void MissingConfig_synthesizesBackwardCompatibleDefaultScope()
    {
        var solution = Path.Join(_root, "App.sln");
        var config = ScopeConfigLoader.Load(_root, [solution]);

        var selection = OneShotScopeResolver.Resolve(config, solution, requestedScopeId: null);

        selection.Scope.Id.Should().Be("default");
        selection.Scope.ProjectSet.Exclude.Should().BeEmpty();
        selection.PathPolicy.IsExcluded(Path.Join(_root, "src", "Allowed.cs")).Should().BeFalse();
    }

    [Fact]
    public void UniqueSolutionMatch_selectsItsExactExcludeSet()
    {
        var solution = Path.Join(_root, "src", "Backend.sln");
        var config = Config(
            ScopeFor("frontend", "src/Frontend.sln", ["**/frontend-generated/**"]),
            ScopeFor("backend", "src/Backend.sln", ["**/backend-generated/**"]));

        var selection = OneShotScopeResolver.Resolve(config, solution, requestedScopeId: null);

        selection.Scope.Id.Should().Be("backend");
        selection.PathPolicy.IsExcluded(
            Path.Join(_root, "src", "backend-generated", "Hidden.cs")).Should().BeTrue();
        selection.PathPolicy.IsExcluded(
            Path.Join(_root, "src", "frontend-generated", "Visible.cs")).Should().BeFalse();
    }

    [Fact]
    public void AmbiguousSolutionMatch_requiresExplicitScope()
    {
        var solution = Path.Join(_root, "Shared.sln");
        var config = Config(
            ScopeFor("alpha", "Shared.sln", ["**/alpha/**"]),
            ScopeFor("beta", "Shared.sln", ["**/beta/**"]));

        var act = () => OneShotScopeResolver.Resolve(config, solution, requestedScopeId: null);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*matches multiple scopes*--scope <id>*");
    }

    [Fact]
    public void ExplicitScope_resolvesAnOtherwiseAmbiguousSolution()
    {
        var solution = Path.Join(_root, "Shared.sln");
        var config = Config(
            ScopeFor("alpha", "Shared.sln", ["**/alpha/**"]),
            ScopeFor("beta", "Shared.sln", ["**/beta/**"]));

        var selection = OneShotScopeResolver.Resolve(config, solution, requestedScopeId: "beta");

        selection.Scope.Id.Should().Be("beta");
        selection.PathPolicy.IsExcluded(Path.Join(_root, "beta", "Hidden.cs")).Should().BeTrue();
        selection.PathPolicy.IsExcluded(Path.Join(_root, "alpha", "Visible.cs")).Should().BeFalse();
    }

    [Fact]
    public void ExplicitScope_mustExistAndContainTheRequestedSolution()
    {
        var solution = Path.Join(_root, "Backend.sln");
        var config = Config(
            ScopeFor("backend", "Backend.sln", []),
            ScopeFor("frontend", "Frontend.sln", []));

        var unknown = () => OneShotScopeResolver.Resolve(config, solution, "ghost");
        var mismatched = () => OneShotScopeResolver.Resolve(config, solution, "frontend");

        unknown.Should().Throw<ArgumentException>().WithMessage("*was not found*");
        mismatched.Should().Throw<ArgumentException>().WithMessage("*does not include*");
    }

    [Fact]
    public void ExplicitScope_cannotBeBlank()
    {
        var solution = Path.Join(_root, "Backend.sln");
        var config = Config(ScopeFor("backend", "Backend.sln", []));

        var act = () => OneShotScopeResolver.Resolve(config, solution, " ");

        act.Should().Throw<ArgumentException>().WithMessage("*--scope value must be a non-empty*");
    }

    [Fact]
    public void NoConfiguredSolutionMatch_failsClosed()
    {
        var solution = Path.Join(_root, "Backend.sln");
        var config = Config(ScopeFor("frontend", "Frontend.sln", []));

        var act = () => OneShotScopeResolver.Resolve(config, solution, requestedScopeId: null);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*No configured scope includes the requested solution*");
    }

    [Fact]
    public void SelectedScope_cannotExcludeItsOwnSolution()
    {
        var solution = Path.Join(_root, "Backend.sln");
        var config = Config(ScopeFor("backend", "Backend.sln", ["**/*.sln"]));

        var act = () => OneShotScopeResolver.Resolve(config, solution, requestedScopeId: null);

        act.Should().Throw<ArgumentException>().WithMessage("*excludes the requested solution*");
    }

    [Fact]
    public void IndexCommand_acceptsAndDocumentsExplicitScope()
    {
        var cli = CommandLine.Parse(["index", "App.sln", "--scope", "backend"]);

        cli.ScopeId.Should().Be("backend");
        CommandLine.HelpText.Should().Contain("index <solution-path> [--scope <id>]");
        CommandLine.HelpText.Should().Match("*multiple*configured scopes*");
    }

    private ScopeConfig Config(params Scope[] scopes) =>
        new(scopes, DefaultScope: null, Plugins: Array.Empty<PluginRef>());

    private Scope ScopeFor(
        string id,
        string solution,
        IReadOnlyList<string> exclude) =>
        new(
            Id: id,
            Name: id,
            Root: _root,
            ProjectSet: new ScopeProjectSet.Solutions([solution], exclude),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.MinValue);
}
