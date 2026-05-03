namespace Sample.Domain;

/// <summary>
/// Marker attribute used by the index-attributes change to demonstrate the
/// attribute_symbol_id linkage path: when a user-defined attribute is itself
/// indexed, the corresponding <c>attributes</c> row carries the integer id of
/// this class via <c>attribute_symbol_id</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property)]
public sealed class LegacyAttribute : Attribute
{
    public LegacyAttribute(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }

    public int Severity { get; set; }
}
