using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Captured attribute attached to an indexed symbol, in its pre-resolution form.
/// <see cref="AttributeClassKey"/> is the canonical key of the attribute's class; it
/// resolves to an <c>attribute_symbol_id</c> once the indexer has finished walking
/// every file in the changed set, since the attribute class might live in a file we
/// haven't touched yet at the moment we walk a use site.
/// </summary>
internal readonly record struct PendingAttribute(
    long SymbolId,
    string Name,
    string FullName,
    string? ArgsJson,
    string? AttributeClassKey);

/// <summary>
/// Walks <see cref="ISymbol.GetAttributes"/> and turns each <see cref="AttributeData"/>
/// into a <see cref="PendingAttribute"/> ready for resolution. Constructor and named
/// argument values are unwrapped from Roslyn's <see cref="TypedConstant"/> shape into
/// CLR-native objects (string, primitive, enum value, type display string, arrays) so
/// the JSON serialiser in Core sees the canonical shape it expects.
/// </summary>
internal static class AttributeExtractor
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    public static void AppendAttributes(
        ISymbol symbol,
        long symbolId,
        List<PendingAttribute> sink)
    {
        var attrs = symbol.GetAttributes();
        if (attrs.IsDefaultOrEmpty) return;

        foreach (var attr in attrs)
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null) continue;

            var name = StripAttributeSuffix(attrClass.Name);
            var fullName = attrClass.ToDisplayString(TypeFormat);

            var ctor = new List<object?>(attr.ConstructorArguments.Length);
            foreach (var ca in attr.ConstructorArguments)
            {
                ctor.Add(UnwrapTypedConstant(ca));
            }

            var named = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in attr.NamedArguments)
            {
                named[kv.Key] = UnwrapTypedConstant(kv.Value);
            }

            string? argsJson = null;
            if (ctor.Count > 0 || named.Count > 0)
            {
                argsJson = AttributeArgsJson.Serialize(new AttributeArgs(ctor, named));
            }

            sink.Add(new PendingAttribute(
                symbolId,
                name,
                fullName,
                argsJson,
                SymbolMapping.CanonicalKey(attrClass)));
        }
    }

    /// <summary>
    /// Resolve each pending attribute's <c>attribute_symbol_id</c> against the
    /// (now fully populated) symbol-id map. Attributes whose class lives outside
    /// the indexed graph (BCL types, NuGet types) get <c>null</c>.
    /// </summary>
    public static AttributeRecord Resolve(
        PendingAttribute pending,
        IReadOnlyDictionary<string, long> symbolIdByKey)
    {
        long? attrSymbolId = null;
        if (pending.AttributeClassKey is not null
            && symbolIdByKey.TryGetValue(pending.AttributeClassKey, out var hit))
        {
            attrSymbolId = hit;
        }
        return new AttributeRecord(pending.SymbolId, pending.Name, pending.FullName, pending.ArgsJson, attrSymbolId);
    }

    private static object? UnwrapTypedConstant(TypedConstant tc)
    {
        if (tc.IsNull) return null;
        switch (tc.Kind)
        {
            case TypedConstantKind.Primitive:
                return tc.Value;
            case TypedConstantKind.Enum:
                // Carry the underlying numeric so the JSON serialiser can emit it as a number.
                return tc.Value;
            case TypedConstantKind.Type:
                if (tc.Value is ITypeSymbol ts) return ts.ToDisplayString(TypeFormat);
                return tc.Value?.ToString();
            case TypedConstantKind.Array:
                var items = new List<object?>(tc.Values.Length);
                foreach (var item in tc.Values) items.Add(UnwrapTypedConstant(item));
                return items;
            case TypedConstantKind.Error:
            default:
                return tc.Value?.ToString();
        }
    }

    /// <summary>
    /// C# attribute usage drops the trailing <c>"Attribute"</c> from the type's short name
    /// (<c>[HttpGet]</c> resolves to <c>HttpGetAttribute</c>). We store the user-visible form
    /// so <c>find_by_attribute(name = "HttpGet")</c> works without forcing the agent to know
    /// the trailing suffix. <see cref="AttributeRecord.FullName"/> retains the full type for
    /// disambiguation.
    /// </summary>
    private static string StripAttributeSuffix(string name)
    {
        const string suffix = "Attribute";
        return name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length]
            : name;
    }
}
