using System;
using System.Collections.Generic;
using System.Text;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Helpers for constructing C# canonical keys (the <c>csharp:&lt;doc-comment-id&gt;</c> form) without
/// reimplementing Roslyn's documentation-comment-id format. The keys produced here are byte-identical
/// to <c>"csharp:" + ISymbol.GetDocumentationCommentId()</c> for the symbol shapes covered, so cross-
/// language plugins can construct C# canonical keys for join purposes (XAML <c>x:Class</c> →
/// <c>csharp:T:...</c>, JS-side IPC handler → <c>csharp:M:...</c>) and the host's
/// <c>symbols.canonical_key</c> column is the join key.
///
/// <para>Format reference (per ECMA-334):</para>
/// <list type="bullet">
///   <item><description>Type: <c>T:Namespace.Type</c>; open generics use <c>Type`Arity</c>.</description></item>
///   <item><description>Nested type: <c>T:Outer.Inner</c> (always <c>.</c>, never <c>+</c>).</description></item>
///   <item><description>Method: <c>M:Type.Method(Param1,Param2)</c>; empty parens when no params; no parens at all when the parameter list is unspecified.</description></item>
///   <item><description>Field: <c>F:Type.FieldName</c>.</description></item>
///   <item><description>Property: <c>P:Type.PropertyName</c>.</description></item>
/// </list>
///
/// <para>Inputs accept either <c>+</c> or <c>.</c> as the nested-type separator and tolerate a leading
/// <c>global::</c> qualifier; both are normalised away. Generic type names are converted to backtick-
/// arity form (<c>List&lt;T&gt;</c> → <c>List`1</c>, <c>Dictionary&lt;K,V&gt;</c> → <c>Dictionary`2</c>).
/// </para>
///
/// <para><b>Known v1 simplification — closed generics:</b> the helpers emit the open-generic form for
/// closed generics too. A caller passing <c>List&lt;int&gt;</c> receives <c>csharp:T:...List`1</c>
/// rather than the curly-brace closed-generic notation Roslyn uses in <c>cref</c> attributes. For
/// type definitions this matches what <c>ISymbol.GetDocumentationCommentId()</c> emits anyway (Roslyn
/// returns the open-generic doc-comment-id for the type definition); for parameter type references
/// inside method signatures, callers needing exact closed-generic doc-comment-id strings should defer
/// to Roslyn directly until a future SDK release lifts this simplification.</para>
/// </summary>
public static class CanonicalKeys
{
    private const string GlobalPrefix = "global::";

    /// <summary>
    /// Returns the canonical key <c>csharp:T:&lt;doc-comment-id-suffix&gt;</c> for a type whose
    /// fully-qualified name is <paramref name="fullyQualifiedName"/>. Strips a leading
    /// <c>global::</c> qualifier, replaces <c>+</c> nested-type separators with <c>.</c>, and
    /// converts generic argument lists (<c>&lt;T&gt;</c>, <c>&lt;K,V&gt;</c>) to backtick-arity.
    /// </summary>
    /// <param name="fullyQualifiedName">
    /// The C#-style fully-qualified name of the type. Examples: <c>System.String</c>,
    /// <c>System.Collections.Generic.List&lt;T&gt;</c>, <c>MyApp.Outer+Inner</c>,
    /// <c>global::MyApp.Foo</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="fullyQualifiedName"/> is null, empty, or whitespace.
    /// </exception>
    public static string ForType(string fullyQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(fullyQualifiedName))
        {
            throw new ArgumentException("Type fully-qualified name must be non-empty.", nameof(fullyQualifiedName));
        }
        return "csharp:T:" + NormalizeTypeKeySuffix(fullyQualifiedName);
    }

    /// <summary>
    /// Returns the canonical key <c>csharp:M:&lt;type-key-suffix&gt;.&lt;method-name&gt;(&lt;params&gt;)</c>
    /// for a method on a type. The parameter list controls the trailing parentheses:
    /// <list type="bullet">
    ///   <item><description><paramref name="parameterTypeFullyQualifiedNames"/> is <c>null</c>: emit
    ///     <c>csharp:M:Type.Method</c> with NO parens (matches every overload).</description></item>
    ///   <item><description>Empty list: emit <c>csharp:M:Type.Method()</c> (the parameterless overload).</description></item>
    ///   <item><description>Non-empty list: emit <c>csharp:M:Type.Method(P1,P2)</c> — comma-separated, no spaces.</description></item>
    /// </list>
    /// </summary>
    /// <param name="typeFullyQualifiedName">Fully-qualified name of the declaring type. Same normalisation as <see cref="ForType"/>.</param>
    /// <param name="methodName">The method's simple name (e.g. <c>Add</c>, <c>.ctor</c>).</param>
    /// <param name="parameterTypeFullyQualifiedNames">
    /// Parameter type fully-qualified names in declaration order, or <c>null</c> to omit the parameter
    /// list entirely. Each name is normalised the same way as in <see cref="ForType"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when either <paramref name="typeFullyQualifiedName"/> or <paramref name="methodName"/>
    /// is null, empty, or whitespace.
    /// </exception>
    public static string ForMethod(
        string typeFullyQualifiedName,
        string methodName,
        IReadOnlyList<string>? parameterTypeFullyQualifiedNames = null)
    {
        if (string.IsNullOrWhiteSpace(typeFullyQualifiedName))
        {
            throw new ArgumentException("Type fully-qualified name must be non-empty.", nameof(typeFullyQualifiedName));
        }
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentException("Method name must be non-empty.", nameof(methodName));
        }

        var typeSuffix = NormalizeTypeKeySuffix(typeFullyQualifiedName);
        if (parameterTypeFullyQualifiedNames is null)
        {
            // No parens at all — matches every overload by name. Roslyn's doc-comment-id format
            // uses the same convention when the parameter list is unspecified.
            return "csharp:M:" + typeSuffix + "." + methodName;
        }

        var sb = new StringBuilder(64);
        sb.Append("csharp:M:").Append(typeSuffix).Append('.').Append(methodName).Append('(');
        for (var i = 0; i < parameterTypeFullyQualifiedNames.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var p = parameterTypeFullyQualifiedNames[i];
            if (string.IsNullOrWhiteSpace(p))
            {
                throw new ArgumentException(
                    $"Parameter type at index {i} must be non-empty.",
                    nameof(parameterTypeFullyQualifiedNames));
            }
            sb.Append(NormalizeTypeKeySuffix(p));
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Returns the canonical key <c>csharp:F:&lt;type-key-suffix&gt;.&lt;field-name&gt;</c> for a
    /// field on a type.
    /// </summary>
    /// <param name="typeFullyQualifiedName">Fully-qualified name of the declaring type.</param>
    /// <param name="fieldName">The field's simple name.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when either <paramref name="typeFullyQualifiedName"/> or <paramref name="fieldName"/>
    /// is null, empty, or whitespace.
    /// </exception>
    public static string ForField(string typeFullyQualifiedName, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(typeFullyQualifiedName))
        {
            throw new ArgumentException("Type fully-qualified name must be non-empty.", nameof(typeFullyQualifiedName));
        }
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("Field name must be non-empty.", nameof(fieldName));
        }
        return "csharp:F:" + NormalizeTypeKeySuffix(typeFullyQualifiedName) + "." + fieldName;
    }

    /// <summary>
    /// Returns the canonical key <c>csharp:P:&lt;type-key-suffix&gt;.&lt;property-name&gt;</c> for a
    /// property on a type.
    /// </summary>
    /// <param name="typeFullyQualifiedName">Fully-qualified name of the declaring type.</param>
    /// <param name="propertyName">The property's simple name.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when either <paramref name="typeFullyQualifiedName"/> or <paramref name="propertyName"/>
    /// is null, empty, or whitespace.
    /// </exception>
    public static string ForProperty(string typeFullyQualifiedName, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(typeFullyQualifiedName))
        {
            throw new ArgumentException("Type fully-qualified name must be non-empty.", nameof(typeFullyQualifiedName));
        }
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException("Property name must be non-empty.", nameof(propertyName));
        }
        return "csharp:P:" + NormalizeTypeKeySuffix(typeFullyQualifiedName) + "." + propertyName;
    }

    /// <summary>
    /// Strips <c>global::</c>, replaces <c>+</c> with <c>.</c>, and rewrites every
    /// <c>&lt;...&gt;</c> generic-argument list as backtick-arity (<c>`N</c> where <c>N</c> is the
    /// number of top-level type arguments). Mirrors what Roslyn emits in
    /// <c>ISymbol.GetDocumentationCommentId()</c> for type definitions.
    /// </summary>
    private static string NormalizeTypeKeySuffix(string fullyQualifiedName)
    {
        var trimmed = fullyQualifiedName.Trim();
        if (trimmed.StartsWith(GlobalPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(GlobalPrefix.Length);
        }

        // Walk the string once, copying chars into a builder and collapsing each `<...>` block into
        // `\`N` where N counts the top-level commas inside the block plus one. Nested generics
        // (e.g. `Dictionary<int, List<string>>`) flatten into `Dictionary\`2` because we only count
        // commas at depth 0 of the outer block; inner blocks are consumed wholesale.
        var sb = new StringBuilder(trimmed.Length);
        var i = 0;
        while (i < trimmed.Length)
        {
            var c = trimmed[i];
            if (c == '+')
            {
                // Nested-type separator → doc-comment-id always uses '.'.
                sb.Append('.');
                i++;
            }
            else if (c == '<')
            {
                var arity = 1;
                var depth = 1;
                i++;
                while (i < trimmed.Length && depth > 0)
                {
                    var ch = trimmed[i];
                    if (ch == '<') depth++;
                    else if (ch == '>') depth--;
                    else if (ch == ',' && depth == 1) arity++;
                    i++;
                }
                sb.Append('`').Append(arity);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }
}
