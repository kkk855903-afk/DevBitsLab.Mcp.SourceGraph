using DevBitsLab.Mcp.SourceGraph.Server;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Wiring-level coverage for the <c>--no-instructions</c> / <c>SOURCEGRAPH_NO_INSTRUCTIONS</c>
/// path. We don't spawn a stdio server; we replay the exact <c>services.Configure&lt;McpServerOptions&gt;</c>
/// call <see cref="Program"/> makes and resolve <see cref="IOptions{T}"/> to confirm the right
/// value lands on <see cref="McpServerOptions.ServerInstructions"/>. This covers the wiring
/// without taking on the cost of a real MCP client roundtrip — the SDK itself is responsible for
/// lifting <c>McpServerOptions.ServerInstructions</c> into the <c>initialize</c> response.
/// </summary>
public sealed class ServerInstructionsWiringTests
{
    private static McpServerOptions Build(bool flag, string? envValue)
    {
        var services = new ServiceCollection();
        // Always register Options<McpServerOptions> so IOptions<T> resolves whether or not
        // Configure runs — mirrors what AddMcpServer() does in the live host.
        services.AddOptions<McpServerOptions>();
        var suppressed = ServerInstructions.ShouldSuppress(flag, envValue);
        if (!suppressed)
        {
            services.Configure<McpServerOptions>(o => o.ServerInstructions = ServerInstructions.Template);
        }
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
    }

    [Fact]
    public void Default_publishesInstructions()
    {
        var opts = Build(flag: false, envValue: null);
        opts.ServerInstructions.Should().NotBeNullOrWhiteSpace();
        opts.ServerInstructions.Should().Contain("prefer");
        opts.ServerInstructions.Should().Contain("usage_stats");
    }

    [Fact]
    public void Flag_suppressesInstructions()
    {
        var opts = Build(flag: true, envValue: null);
        opts.ServerInstructions.Should().BeNullOrEmpty();
    }

    [Fact]
    public void EnvVar_suppressesInstructions_whenFlagAbsent()
    {
        var opts = Build(flag: false, envValue: "1");
        opts.ServerInstructions.Should().BeNullOrEmpty();
    }

    [Fact]
    public void EnvVar_trueSuppresses_caseInsensitive()
    {
        Build(flag: false, envValue: "true").ServerInstructions.Should().BeNullOrEmpty();
        Build(flag: false, envValue: "TRUE").ServerInstructions.Should().BeNullOrEmpty();
    }

    [Fact]
    public void EnvVar_otherValuesDoNotSuppress()
    {
        Build(flag: false, envValue: "0").ServerInstructions.Should().NotBeNullOrEmpty();
        Build(flag: false, envValue: "false").ServerInstructions.Should().NotBeNullOrEmpty();
        Build(flag: false, envValue: "yes").ServerInstructions.Should().NotBeNullOrEmpty();
    }
}
