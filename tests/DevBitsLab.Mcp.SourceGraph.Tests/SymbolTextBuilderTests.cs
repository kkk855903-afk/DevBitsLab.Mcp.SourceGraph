using DevBitsLab.Mcp.SourceGraph.Embeddings;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit coverage for <see cref="SymbolTextBuilder"/> — the pure helper that builds the
/// synthesised text per symbol and decides what to skip. Exercised separately from the
/// indexer because the rest of the embedding pipeline depends on these decisions being
/// stable.
/// </summary>
public sealed class SymbolTextBuilderTests
{
    [Fact]
    public void Build_orderedSections_kindFqnSummarySignatureBody()
    {
        var text = SymbolTextBuilder.Build(
            kind: "method",
            fqn: "Sample.Domain.Calculator.Add(int a, int b)",
            xmlSummary: "Adds two integers and returns their sum.",
            signature: "public int Add(int a, int b)",
            bodyExcerpt: "public int Add(int a, int b) => a + b;");

        text.Should().StartWith("method Sample.Domain.Calculator.Add(int a, int b)");
        text.Should().Contain("Adds two integers");
        text.Should().Contain("public int Add(int a, int b)");
        text.Should().EndWith("=> a + b;");
    }

    [Fact]
    public void HashOf_isStable_andIsSensitiveToContent()
    {
        var a = SymbolTextBuilder.HashOf("class Foo { void Bar() {} }");
        var aRepeat = SymbolTextBuilder.HashOf("class Foo { void Bar() {} }");
        var b = SymbolTextBuilder.HashOf("class Foo { void Baz() {} }");

        a.Should().Equal(aRepeat);
        a.Should().NotEqual(b);
        a.Length.Should().Be(32, "SHA-256 produces 32 bytes");
    }

    [Fact]
    public void ShouldSkip_generatedFile_returnsTrue()
    {
        SymbolTextBuilder.ShouldSkip("Foo.g.cs", "summary", "sig", "body").Should().BeTrue();
        SymbolTextBuilder.ShouldSkip("Form1.Designer.cs", null, "sig", "body").Should().BeTrue();
    }

    [Fact]
    public void ShouldSkip_emptyDocAndBody_returnsTrue()
    {
        // Trivial accessor: signature only, no doc, no body. Not worth a 768-d vector.
        SymbolTextBuilder.ShouldSkip("App.cs", null, "public int X { get; set; }", null).Should().BeTrue();
    }

    [Fact]
    public void ShouldSkip_realMethod_returnsFalse()
    {
        SymbolTextBuilder.ShouldSkip(
            "App.cs",
            xmlSummary: "Adds two integers.",
            signature: "public int Add(int a, int b)",
            bodyExcerpt: "public int Add(int a, int b) => a + b;").Should().BeFalse();
    }

    [Fact]
    public void IsGeneratedFile_caseInsensitive()
    {
        SymbolTextBuilder.IsGeneratedFile("Foo.G.CS").Should().BeTrue();
        SymbolTextBuilder.IsGeneratedFile("Foo.designer.cs").Should().BeTrue();
        SymbolTextBuilder.IsGeneratedFile("Foo.cs").Should().BeFalse();
    }

    [Fact]
    public void DeterministicMockEmbeddingGenerator_sameInputSameVector()
    {
        var gen = new DeterministicMockEmbeddingGenerator();
        var a = DeterministicMockEmbeddingGenerator.Embed("calculator add multiply", 768);
        var b = DeterministicMockEmbeddingGenerator.Embed("calculator add multiply", 768);
        a.Should().Equal(b);
    }

    [Fact]
    public void DeterministicMockEmbeddingGenerator_similarTextsCloseInCosine()
    {
        // "calculator add" should be closer in cosine to "calculator add multiply" than to "logger".
        var v1 = DeterministicMockEmbeddingGenerator.Embed("calculator add", 768);
        var v2 = DeterministicMockEmbeddingGenerator.Embed("calculator add multiply", 768);
        var v3 = DeterministicMockEmbeddingGenerator.Embed("logger", 768);
        var d12 = Cosine(v1, v2);
        var d13 = Cosine(v1, v3);
        d12.Should().BeGreaterThan(d13, "shared tokens push v1 and v2 closer than v1 and v3");
    }

    private static double Cosine(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s; // both are unit-norm
    }
}
