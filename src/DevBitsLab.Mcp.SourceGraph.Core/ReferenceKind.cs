namespace DevBitsLab.Mcp.SourceGraph.Core;

public enum ReferenceKind
{
    Definition = 0,
    Reference,
    Call,
    Implements,
    Inherits,
}
