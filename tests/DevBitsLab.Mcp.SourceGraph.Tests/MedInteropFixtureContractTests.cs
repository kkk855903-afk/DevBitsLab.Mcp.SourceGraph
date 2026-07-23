using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class MedInteropFixtureContractTests
{
    private static readonly string FixtureRoot = FindFixtureRoot();

    [Fact]
    public void PositiveGraphContract_coversEveryCrossLayerBridge()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(FixtureRoot, "Expected", "graph-contract.json")));
        var edges = document.RootElement.GetProperty("required_edges")
            .EnumerateArray()
            .ToList();

        edges.Select(edge => edge.GetProperty("relation").GetString())
            .Should().Contain(new[]
            {
                "binds-path",
                "calls",
                "grpc-calls",
                "implements-rpc",
                "pinvoke-maps-to",
            });
        edges.Should().OnlyContain(edge =>
            !string.IsNullOrWhiteSpace(edge.GetProperty("from").GetString())
            && !string.IsNullOrWhiteSpace(edge.GetProperty("to").GetString()));
    }

    [Fact]
    public void FindingContract_hasOneIsolatedCaseForEveryInitialInteropRule()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(FixtureRoot, "Expected", "interop-findings.json")));
        var findings = document.RootElement.GetProperty("findings")
            .EnumerateArray()
            .ToList();

        findings.Select(finding => finding.GetProperty("rule").GetString())
            .Should().BeEquivalentTo(
                "Interop001",
                "Interop002",
                "Interop003",
                "Interop004",
                "Interop005",
                "Interop006");
        findings.Select(finding => finding.GetProperty("native_symbol").GetString())
            .Should().OnlyHaveUniqueItems();
        foreach (var finding in findings)
        {
            File.Exists(Path.Join(
                FixtureRoot,
                finding.GetProperty("managed_file").GetString()!)).Should().BeTrue();
            File.Exists(Path.Join(
                FixtureRoot,
                finding.GetProperty("native_file").GetString()!)).Should().BeTrue();
        }
    }

    [Fact]
    public void Fixture_containsNoMedicalImagesOrPatientDataDirectories()
    {
        var files = Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToList();

        files.Should().NotContain(path =>
            path.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        files.Should().NotContain(path =>
            path.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment.Equals("PatientData", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Logs", StringComparison.OrdinalIgnoreCase)));
    }

    private static string FindFixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(
                directory.FullName,
                "tests",
                "fixtures",
                "MedInteropChain");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate tests/fixtures/MedInteropChain.");
    }
}
