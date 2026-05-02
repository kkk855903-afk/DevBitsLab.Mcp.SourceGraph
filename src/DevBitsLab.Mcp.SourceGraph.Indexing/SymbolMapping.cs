using Microsoft.CodeAnalysis;
using CoreSymbolKind = DevBitsLab.Mcp.SourceGraph.Core.SymbolKind;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

internal static class SymbolMapping
{
    private static readonly SymbolDisplayFormat FqnFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeModifiers |
            SymbolDisplayMemberOptions.IncludeAccessibility,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string Fqn(ISymbol symbol) => symbol.ToDisplayString(FqnFormat);
    public static string Signature(ISymbol symbol) => symbol.ToDisplayString(SignatureFormat);

    public static string? CanonicalKey(ISymbol symbol) =>
        symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.OriginalDefinition.ToDisplayString(FqnFormat);

    public static CoreSymbolKind ToCoreKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => CoreSymbolKind.Namespace,
        ITypeSymbol t => t.TypeKind switch
        {
            TypeKind.Class => CoreSymbolKind.Class,
            TypeKind.Struct => CoreSymbolKind.Struct,
            TypeKind.Interface => CoreSymbolKind.Interface,
            TypeKind.Enum => CoreSymbolKind.Enum,
            TypeKind.Delegate => CoreSymbolKind.Delegate,
            TypeKind.TypeParameter => CoreSymbolKind.TypeParameter,
            _ => CoreSymbolKind.Class,
        },
        IMethodSymbol m => m.MethodKind == MethodKind.Constructor ? CoreSymbolKind.Constructor : CoreSymbolKind.Method,
        IPropertySymbol => CoreSymbolKind.Property,
        IFieldSymbol f when f.ContainingType?.TypeKind == TypeKind.Enum => CoreSymbolKind.EnumMember,
        IFieldSymbol => CoreSymbolKind.Field,
        IEventSymbol => CoreSymbolKind.Event,
        IParameterSymbol => CoreSymbolKind.Parameter,
        ILocalSymbol => CoreSymbolKind.Local,
        _ => CoreSymbolKind.Unknown,
    };

    public static bool IsIndexable(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol ns => !ns.IsGlobalNamespace,
        ITypeSymbol t => t.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate,
        IMethodSymbol m => m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion,
        IPropertySymbol => true,
        IFieldSymbol => true,
        IEventSymbol => true,
        _ => false,
    };
}
