using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Captured annotation (a .NET attribute) attached to an indexed symbol, in its pre-resolution
/// form. <see cref="AnnotationClassKey"/> is the canonical key of the attribute's class; it
/// resolves to an <c>attribute_symbol_id</c> once the indexer has finished walking every file
/// in the changed set, since the attribute class might live in a file we haven't touched yet at
/// the moment we walk a use site.
/// </summary>
internal readonly record struct PendingAnnotation(
    string SymbolCanonicalKey,
    long SymbolId,
    string Name,
    string FullName,
    string? ArgsJson,
    string? AnnotationClassKey);

/// <summary>
/// Walks <see cref="ISymbol.GetAttributes"/> and turns each <see cref="AttributeData"/>
/// into a <see cref="PendingAnnotation"/> ready for resolution. Constructor and named
/// argument values are unwrapped from Roslyn's <see cref="TypedConstant"/> shape into
/// CLR-native objects (string, primitive, enum value, type display string, arrays) so
/// the JSON serialiser in Core sees the canonical shape it expects.
/// </summary>
internal static class AttributeExtractor
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>Annotation flavor emitted for every C# <c>[Attribute]</c>.</summary>
    public const string CSharpAttributeFlavor = "csharp-attribute";

    public static void AppendAnnotations(
        ISymbol symbol,
        string symbolCanonicalKey,
        long symbolId,
        List<PendingAnnotation> sink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolCanonicalKey);
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

            sink.Add(new PendingAnnotation(
                symbolCanonicalKey,
                symbolId,
                name,
                fullName,
                argsJson,
                SymbolMapping.CanonicalKey(attrClass)));
        }
    }

    /// <summary>
    /// Resolve each pending annotation's <c>attribute_symbol_id</c> against the
    /// (now fully populated) symbol-id map. Annotations whose class lives outside
    /// the indexed graph (BCL types, NuGet types) get <c>null</c>.
    /// </summary>
    public static AnnotationRecord Resolve(
        PendingAnnotation pending,
        IReadOnlyDictionary<string, long> symbolIdByKey)
    {
        long? attrSymbolId = null;
        if (pending.AnnotationClassKey is not null
            && symbolIdByKey.TryGetValue(pending.AnnotationClassKey, out var hit))
        {
            attrSymbolId = hit;
        }
        return new AnnotationRecord(
            pending.SymbolId,
            pending.Name,
            pending.FullName,
            CSharpAttributeFlavor,
            pending.ArgsJson,
            attrSymbolId);
    }

    /// <summary>
    /// Converts a pending attribute to its canonical-key form for atomic declaration reconcile.
    /// The store resolves the optional attribute definition in the same transaction as the host.
    /// </summary>
    public static FileAnnotationFact ToFact(PendingAnnotation pending) =>
        new(
            pending.SymbolCanonicalKey,
            pending.Name,
            pending.FullName,
            CSharpAttributeFlavor,
            pending.ArgsJson,
            pending.AnnotationClassKey);

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
    /// the trailing suffix. <see cref="AnnotationRecord.FullName"/> retains the full type for
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
