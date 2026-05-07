using System.ComponentModel;
using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the auto-register-tool-instructions change: <c>[ToolTrigger]</c> attribute,
/// description-append formatter, plugin <c>trigger:</c> argument, server-instructions suppression
/// helper, and a catalog-shape assertion that every built-in non-diagnostic tool carries a trigger.
/// </summary>
public sealed class ToolTriggerTests
{
    [Fact]
    public void AppendTrigger_returnsDescriptionUnchanged_whenTriggerIsNull()
    {
        ToolDescriptionFormatter.AppendTrigger("Find a thing.", null)
            .Should().Be("Find a thing.");
    }

    [Fact]
    public void AppendTrigger_returnsDescriptionUnchanged_whenTriggerIsWhitespace()
    {
        ToolDescriptionFormatter.AppendTrigger("Find a thing.", "   ")
            .Should().Be("Find a thing.");
    }

    [Fact]
    public void AppendTrigger_appendsUseWhenLine()
    {
        ToolDescriptionFormatter.AppendTrigger("Find a thing.", "\"where is X?\"")
            .Should().Be("Find a thing.\n\nUse when: \"where is X?\"");
    }

    [Fact]
    public void AppendTrigger_handlesEmptyDescription()
    {
        ToolDescriptionFormatter.AppendTrigger(string.Empty, "\"where is X?\"")
            .Should().Be("Use when: \"where is X?\"");
    }

    [Fact]
    public void ApplyTriggersFromAttributes_mutatesDescription_onlyForAnnotatedTools()
    {
        var withTrigger = McpServerTool.Create(
            typeof(ToolTriggerTests).GetMethod(nameof(WithTriggerSample), BindingFlags.Static | BindingFlags.NonPublic)!,
            target: null,
            new McpServerToolCreateOptions());
        var withoutTrigger = McpServerTool.Create(
            typeof(ToolTriggerTests).GetMethod(nameof(WithoutTriggerSample), BindingFlags.Static | BindingFlags.NonPublic)!,
            target: null,
            new McpServerToolCreateOptions());

        var originalWithoutTrigger = withoutTrigger.ProtocolTool.Description;

        ToolDescriptionFormatter.ApplyTriggersFromAttributes(new[] { withTrigger, withoutTrigger });

        withTrigger.ProtocolTool.Description.Should().EndWith("Use when: \"trigger phrase\"");
        withoutTrigger.ProtocolTool.Description.Should().Be(originalWithoutTrigger);
    }

    [Fact]
    public void ApplyTriggersFromAttributes_isIdempotent()
    {
        var tool = McpServerTool.Create(
            typeof(ToolTriggerTests).GetMethod(nameof(WithTriggerSample), BindingFlags.Static | BindingFlags.NonPublic)!,
            target: null,
            new McpServerToolCreateOptions());

        ToolDescriptionFormatter.ApplyTriggersFromAttributes(new[] { tool });
        var firstPass = tool.ProtocolTool.Description;
        ToolDescriptionFormatter.ApplyTriggersFromAttributes(new[] { tool });
        var secondPass = tool.ProtocolTool.Description;

        secondPass.Should().Be(firstPass);
    }

    [Fact]
    public void ToolRegistry_appendsTrigger_whenTriggerOverloadIsCalled()
    {
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", false);
        var registry = new ToolRegistry("mine", new HashSet<string>(StringComparer.Ordinal), record);
        registry.AddTool("find_handlers", "Find handlers.", new Func<string>(() => "ok"),
            trigger: "\"who handles MediatR request X?\"");

        registry.RegisteredTools.Should().HaveCount(1);
        registry.RegisteredTools[0].ProtocolTool.Description
            .Should().Be("Find handlers.\n\nUse when: \"who handles MediatR request X?\"");
    }

    [Fact]
    public void ToolRegistry_omitsTrigger_whenThreeArgOverloadIsCalled()
    {
        // The 3-arg overload is the original SDK 1.0.0 signature, kept on the interface so
        // plugins compiled against 1.0.0 keep linking and running.
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", false);
        var registry = new ToolRegistry("mine", new HashSet<string>(StringComparer.Ordinal), record);
        registry.AddTool("find_handlers", "Find handlers.", new Func<string>(() => "ok"));

        registry.RegisteredTools[0].ProtocolTool.Description.Should().Be("Find handlers.");
    }

    [Fact]
    public void ToolRegistry_triggerOverload_rejectsEmptyTrigger()
    {
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", false);
        var registry = new ToolRegistry("mine", new HashSet<string>(StringComparer.Ordinal), record);

        var act = () => registry.AddTool("find_handlers", "Find handlers.", new Func<string>(() => "ok"), trigger: "  ");
        act.Should().Throw<ArgumentException>().WithMessage("*non-empty*");
    }

    [Fact]
    public void ServerInstructions_template_carriesPreambleAndEpilogueKeywords()
    {
        ServerInstructions.Template.Should().Contain("prefer");
        ServerInstructions.Template.Should().Contain("usage_stats");
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "0", true)]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(false, "1", true)]
    [InlineData(false, "true", true)]
    [InlineData(false, "TRUE", true)]
    [InlineData(false, "0", false)]
    [InlineData(false, "false", false)]
    [InlineData(false, "yes", false)]
    public void ServerInstructions_ShouldSuppress_combinesFlagAndEnv(bool flag, string? env, bool expected)
    {
        ServerInstructions.ShouldSuppress(flag, env).Should().Be(expected);
    }

    [Fact]
    public void Catalog_everyNonDiagnosticToolDeclaresATrigger()
    {
        // Lock in the migration: every built-in tool that previously embedded `Use for ...` prose
        // must now carry [ToolTrigger]. Diagnostic-only tools are explicitly exempt because they're
        // self-check tools, not trigger-driven (see proposal/design).
        var diagnosticOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "GraphStatsAsync",
            "UsageStats",
            "Ping",
        };

        var serverAsm = typeof(ServerInstructions).Assembly;
        var toolMethods = serverAsm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

        toolMethods.Should().NotBeEmpty("the server registers built-in tools");

        var missing = toolMethods
            .Where(m => !diagnosticOnly.Contains(m.Name))
            .Where(m => m.GetCustomAttribute<ToolTriggerAttribute>() is null)
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToList();

        missing.Should().BeEmpty(
            "every non-diagnostic [McpServerTool] must declare [ToolTrigger]; missing: {0}",
            string.Join(", ", missing));
    }

    // Test-only methods used via reflection in the formatter tests above.
    [Description("Sample with trigger.")]
    [ToolTrigger("\"trigger phrase\"")]
    private static string WithTriggerSample() => "x";

    [Description("Sample without trigger.")]
    private static string WithoutTriggerSample() => "x";
}
