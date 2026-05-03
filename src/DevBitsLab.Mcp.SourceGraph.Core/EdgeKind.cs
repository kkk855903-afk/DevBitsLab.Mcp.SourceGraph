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
}
