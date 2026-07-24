using System.Text.RegularExpressions;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class CommandLineHelpLanguageTests
{
    [Fact]
    public void Help_defaultsToEnglish()
    {
        var cli = CommandLine.Parse(["--help"]);

        cli.ShowHelp.Should().BeTrue();
        cli.HelpLanguage.Should().Be(CliHelpLanguage.English);
        cli.SelectedHelpText.Should().Contain("Usage:");
        cli.SelectedHelpText.Should().NotContain("用法:");
    }

    [Theory]
    [InlineData("--help --lang zh")]
    [InlineData("--lang zh --help")]
    [InlineData("serve --help --lang zh")]
    [InlineData("serve --lang zh --help")]
    [InlineData("-h --lang zh")]
    [InlineData("--lang zh -h")]
    [InlineData("serve -h --lang zh")]
    [InlineData("serve --lang zh -h")]
    public void ChineseHelp_acceptsEitherFlagOrder(string commandLine)
    {
        var cli = CommandLine.Parse(commandLine.Split(' '));

        cli.ShowHelp.Should().BeTrue();
        cli.HelpLanguage.Should().Be(CliHelpLanguage.Chinese);
        cli.SelectedHelpText.Should().Contain("用法:");
        cli.SelectedHelpText.Should().NotContain("Usage:");
    }

    [Fact]
    public void EnglishHelp_canBeSelectedExplicitly()
    {
        var cli = CommandLine.Parse(["--lang", "en", "--help"]);

        cli.HelpLanguage.Should().Be(CliHelpLanguage.English);
        cli.SelectedHelpText.Should().Contain("Usage:");
    }

    [Fact]
    public void MissingLanguageValue_isRejected()
    {
        var act = () => CommandLine.Parse(["--help", "--lang"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("--lang requires a value: en or zh.");
    }

    [Fact]
    public void UnsupportedLanguage_isRejected()
    {
        var act = () => CommandLine.Parse(["--help", "--lang", "fr"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Unsupported help language 'fr'. Expected en or zh.");
    }

    [Fact]
    public void LanguageWithoutHelp_isRejected()
    {
        var act = () => CommandLine.Parse(["serve", "--lang", "zh"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("--lang can only be used together with --help.");
    }

    [Fact]
    public void InvalidArgumentBeforeHelp_isStillRejected()
    {
        var act = () => CommandLine.Parse(["serve", "--bogus", "--help"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Unrecognised argument: --bogus");
    }

    [Fact]
    public void InvalidArgumentAfterHelp_isStillIgnored()
    {
        var cli = CommandLine.Parse(["serve", "--help", "--bogus"]);

        cli.ShowHelp.Should().BeTrue();
        cli.HelpLanguage.Should().Be(CliHelpLanguage.English);
    }

    [Theory]
    [InlineData("--help --model --lang")]
    [InlineData("--help --model --lang zh")]
    [InlineData("serve --help --model --lang")]
    [InlineData("serve --help --model --lang zh")]
    public void LanguageLookingOptionValueAfterHelp_isStillIgnored(string commandLine)
    {
        var cli = CommandLine.Parse(commandLine.Split(' '));

        cli.ShowHelp.Should().BeTrue();
        cli.HelpLanguage.Should().Be(CliHelpLanguage.English);
    }

    [Theory]
    [InlineData("--lang --help")]
    [InlineData("serve --lang --help")]
    [InlineData("--help --lang --bogus")]
    public void OptionTokenCannotBeUsedAsLanguageValue(string commandLine)
    {
        var act = () => CommandLine.Parse(commandLine.Split(' '));

        act.Should().Throw<ArgumentException>()
            .WithMessage("--lang requires a value: en or zh.");
    }

    [Theory]
    [InlineData("--help --lang en --lang zh")]
    [InlineData("--lang en --lang zh --help")]
    [InlineData("serve --lang en --lang zh --help")]
    [InlineData("serve --lang en --help --lang zh")]
    public void DuplicateLanguageSelection_isRejected(string commandLine)
    {
        var act = () => CommandLine.Parse(commandLine.Split(' '));

        act.Should().Throw<ArgumentException>()
            .WithMessage("--lang may only be specified once.");
    }

    [Fact]
    public void BothLanguages_documentTheSameCommandAndFlagSurface()
    {
        CommandSyntax(CommandLine.ChineseHelpText)
            .Should().Equal(CommandSyntax(CommandLine.HelpText));
        FlagTokens(CommandLine.ChineseHelpText)
            .Should().Equal(FlagTokens(CommandLine.HelpText));
    }

    private static string[] CommandSyntax(string helpText) =>
        helpText.Split('\n')
            .Select(static line => line.Trim())
            .Where(static line =>
                line.StartsWith("sourcegraph-mcp ", StringComparison.Ordinal)
                && !line.Contains(" — ", StringComparison.Ordinal))
            .ToArray();

    private static string[] FlagTokens(string helpText) =>
        Regex.Matches(helpText, @"--[a-z][a-z0-9-]*")
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
