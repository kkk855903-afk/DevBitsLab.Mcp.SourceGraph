using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class SourceContextReaderTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "source-context-reader-" + Guid.NewGuid().ToString("N"));

    public SourceContextReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ReadAsync_returnsBoundedOneBasedWindow()
    {
        var file = Path.Join(_root, "sample.cs");
        await File.WriteAllTextAsync(file, "one\ntwo\nthree\nfour\nfive\n");

        var snippet = await SourceContextReader.ReadAsync(
            _root,
            "sample.cs",
            startLine: 3,
            endLine: 3,
            contextLines: 1,
            CancellationToken.None);

        snippet.Should().NotBeNull();
        snippet!.StartLine.Should().Be(2);
        snippet.EndLine.Should().Be(4);
        snippet.Text.Should().Be("two\nthree\nfour");
    }

    [Fact]
    public async Task ReadAsync_rejectsPathOutsideScope()
    {
        var outside = Path.Join(Path.GetTempPath(), "outside-" + Guid.NewGuid() + ".cs");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            var snippet = await SourceContextReader.ReadAsync(
                _root,
                outside,
                startLine: 1,
                endLine: 1,
                contextLines: 0,
                CancellationToken.None);

            snippet.Should().BeNull();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("locations")]
    [InlineData("evidence")]
    [InlineData("audit")]
    public void EvidenceDetailOptions_acceptsUniformLevels(string detail)
    {
        EvidenceDetailOptions.TryCreate(
                detail,
                contextLines: 2,
                includeSnippet: true,
                out var options,
                out var error)
            .Should().BeTrue(error);
        options.Detail.Should().Be(detail);
        options.ContextLines.Should().Be(2);
        options.IncludeSnippet.Should().BeTrue();
    }

    [Fact]
    public void EvidenceDetailOptions_rejectsUnboundedContext()
    {
        EvidenceDetailOptions.TryCreate(
                "evidence",
                contextLines: 21,
                includeSnippet: true,
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("between 0 and 20");
    }
}
