namespace DevBitsLab.Mcp.SourceGraph.Core;

public enum EdgeKind
{
    Calls = 0,
    Inherits = 1,
    Implements = 2,
    UsesType = 3,
    OverridesMember = 4,
    ImplementsMember = 5,
    Instantiates = 6,
    Throws = 7,
    /// <summary>
    /// Test method exercises a piece of production code. Source = the test method
    /// (carries <c>symbols.test_framework</c>), destination = the first non-trivial
    /// production-code symbol the test calls. Heuristic: see <c>integrate-tests-and-history</c>.
    /// </summary>
    Tests = 8,
}
