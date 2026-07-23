namespace DevBitsLab.Mcp.SourceGraph.Core;

public enum ReferenceKind
{
    Definition = 0,
    Reference = 1,
    Call = 2,
    Implements = 3,
    Inherits = 4,
    Read = 5,
    Write = 6,
}
