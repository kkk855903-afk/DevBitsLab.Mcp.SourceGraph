using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Interop;

internal sealed record ManagedInteropUsageExtraction(
    IReadOnlyList<ManagedCallbackUsageProjection> CallbackUsages,
    IReadOnlyList<ManagedReturnReleaseProjection> ReturnReleases);

/// <summary>
/// Extracts only small, directly proven managed interop use-flow shapes. More complex aliasing,
/// control flow, or rooting conventions deliberately produce no fact rather than a guessed
/// lifetime/ownership conclusion.
/// </summary>
internal static class ManagedInteropUsageExtractor
{
    internal const string Producer = "roslyn-managed-interop-usage";

    public static ManagedInteropUsageExtraction Extract(
        SyntaxNode root,
        SemanticModel model,
        InteropTarget target,
        long producingFileId,
        string producingFilePath,
        Func<IMethodSymbol, long?> importFileIdResolver,
        Func<long, string?> importFilePathResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(producingFileId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(producingFilePath);
        ArgumentNullException.ThrowIfNull(importFileIdResolver);
        ArgumentNullException.ThrowIfNull(importFilePathResolver);

        var marshalType = model.Compilation.GetTypeByMetadataName(
            "System.Runtime.InteropServices.Marshal");
        var importCache = new Dictionary<IMethodSymbol, ManagedImport?>(
            SymbolEqualityComparer.Default);
        var callbackUsages = new List<ManagedCallbackUsageProjection>();
        var returnReleases = new List<ManagedReturnReleaseProjection>();

        ManagedImport? ResolveImport(IMethodSymbol method)
        {
            if (importCache.TryGetValue(method, out var cached))
            {
                return cached;
            }

            ManagedImport? resolved = null;
            foreach (var candidate in ImportCandidates(method))
            {
                var ownerFileId = importFileIdResolver(candidate);
                if (ownerFileId is not > 0) continue;
                resolved = ManagedInteropExtractor.TryExtract(
                    candidate,
                    target,
                    ownerFileId.Value,
                    importFilePathResolver(ownerFileId.Value));
                if (resolved is not null) break;
            }
            importCache[method] = resolved;
            return resolved;
        }

        foreach (var syntax in root
                     .DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.GetOperation(syntax, cancellationToken) is not
                IInvocationOperation invocation)
            {
                continue;
            }

            var caller = FindCaller(
                model,
                root.SyntaxTree,
                syntax.SpanStart,
                cancellationToken);
            if (caller is null) continue;
            var callerKey = SymbolMapping.CanonicalKey(caller);
            if (callerKey is null) continue;

            var import = ResolveImport(invocation.TargetMethod);
            if (import is not null)
            {
                AppendUnrootedCallbackUsages(
                    invocation,
                    import,
                    callerKey,
                    producingFileId,
                    producingFilePath,
                    callbackUsages);
            }

            if (marshalType is null
                || !TryGetReleaseFamily(
                    invocation.TargetMethod,
                    marshalType,
                    out var releaseFamily)
                || invocation.Arguments.Length != 1)
            {
                continue;
            }

            var returnedValue = ResolveReleasedValue(
                invocation.Arguments[0].Value,
                syntax,
                model,
                cancellationToken);
            if (returnedValue is null) continue;
            var releasedImport = ResolveImport(returnedValue.TargetMethod);
            if (releasedImport is null) continue;

            returnReleases.Add(new ManagedReturnReleaseProjection(
                releasedImport.SymbolCanonicalKey,
                new ManagedReturnRelease(
                    callerKey,
                    releaseFamily,
                    target,
                    CreateEvidence(
                        producingFileId,
                        producingFilePath,
                        syntax,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["managed_import"] =
                                releasedImport.SymbolCanonicalKey,
                            ["release_family"] =
                                AllocatorFamilyToken(releaseFamily),
                        }))));
        }

        return new ManagedInteropUsageExtraction(
            callbackUsages
                .OrderBy(
                    usage => usage.ManagedImportSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(
                    usage => usage.Usage.CallerSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(usage => usage.Usage.ParameterPosition)
                .ThenBy(usage => usage.Usage.Evidence.Location.StartLine)
                .ThenBy(usage => usage.Usage.Evidence.Location.StartColumn)
                .ToArray(),
            returnReleases
                .OrderBy(
                    release => release.ManagedImportSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(
                    release => release.Release.CallerSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(release => release.Release.Evidence.Location.StartLine)
                .ThenBy(release => release.Release.Evidence.Location.StartColumn)
                .ToArray());
    }

    private static IEnumerable<IMethodSymbol> ImportCandidates(
        IMethodSymbol method)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var candidate in new[]
                 {
                     method,
                     method.PartialDefinitionPart,
                     method.PartialImplementationPart,
                     method.OriginalDefinition,
                 })
        {
            if (candidate is not null && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static ISymbol? FindCaller(
        SemanticModel model,
        SyntaxTree currentTree,
        int position,
        CancellationToken cancellationToken)
    {
        for (var symbol = model.GetEnclosingSymbol(
                 position,
                 cancellationToken);
             symbol is not null;
             symbol = symbol.ContainingSymbol)
        {
            if (symbol is not IMethodSymbol method)
            {
                continue;
            }
            if (method.MethodKind is MethodKind.AnonymousFunction
                or MethodKind.LocalFunction)
            {
                // The nested executable is not represented as a persisted declaration. Walking
                // outward would turn a merely declared (and possibly never invoked) lambda or
                // local function into a proven call by its containing member.
                return null;
            }

            ISymbol caller = SymbolMapping.IsIndexable(method)
                ? method
                : method.AssociatedSymbol is IPropertySymbol or IEventSymbol
                    ? method.AssociatedSymbol
                    : method;
            if (!SymbolMapping.IsIndexable(caller)
                || !caller.DeclaringSyntaxReferences.Any(reference =>
                    ReferenceEquals(reference.SyntaxTree, currentTree)))
            {
                continue;
            }
            return caller;
        }
        return null;
    }

    private static void AppendUnrootedCallbackUsages(
        IInvocationOperation invocation,
        ManagedImport import,
        string callerKey,
        long producingFileId,
        string producingFilePath,
        ICollection<ManagedCallbackUsageProjection> destination)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is null
                || import.Parameters.FirstOrDefault(parameter =>
                    parameter.Position == argument.Parameter.Ordinal) is not
                    { Type.Category: AbiTypeCategory.FunctionPointer } parameter
                || Unwrap(argument.Value) is not
                    IDelegateCreationOperation delegateCreation)
            {
                continue;
            }

            var target = Unwrap(delegateCreation.Target);
            if (target is not (IAnonymousFunctionOperation
                or IMethodReferenceOperation))
            {
                continue;
            }

            destination.Add(new ManagedCallbackUsageProjection(
                import.SymbolCanonicalKey,
                new ManagedCallbackUsage(
                    parameter.Position,
                    callerKey,
                    CallbackGcRooting.Unrooted,
                    import.Target,
                    CreateEvidence(
                        producingFileId,
                        producingFilePath,
                        delegateCreation.Syntax,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["managed_import"] = import.SymbolCanonicalKey,
                            ["parameter_position"] =
                                parameter.Position.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture),
                            ["rooting"] = "unrooted",
                        }))));
        }
    }

    private static bool TryGetReleaseFamily(
        IMethodSymbol method,
        INamedTypeSymbol marshalType,
        out InteropAllocatorFamily family)
    {
        family = InteropAllocatorFamily.Unknown;
        if (!method.IsStatic
            || !method.ReturnsVoid
            || method.Parameters.Length != 1
            || method.Parameters[0].Type.SpecialType
                != SpecialType.System_IntPtr
            || !SymbolEqualityComparer.Default.Equals(
                method.ContainingType,
                marshalType))
        {
            return false;
        }

        family = method.Name switch
        {
            "FreeCoTaskMem" => InteropAllocatorFamily.CoTaskMem,
            "FreeHGlobal" => InteropAllocatorFamily.HGlobal,
            _ => InteropAllocatorFamily.Unknown,
        };
        return family != InteropAllocatorFamily.Unknown;
    }

    private static IInvocationOperation? ResolveReleasedValue(
        IOperation value,
        InvocationExpressionSyntax releaseSyntax,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var unwrapped = Unwrap(value);
        if (unwrapped is IInvocationOperation direct)
        {
            return direct;
        }
        if (unwrapped is not ILocalReferenceOperation local
            || releaseSyntax.FirstAncestorOrSelf<ExpressionStatementSyntax>()
                is not { Parent: BlockSyntax block } releaseStatement)
        {
            return null;
        }

        var statementIndex = block.Statements.IndexOf(releaseStatement);
        if (statementIndex <= 0
            || block.Statements[statementIndex - 1] is not
                LocalDeclarationStatementSyntax
                {
                    Declaration.Variables.Count: 1,
                } declaration)
        {
            return null;
        }

        var variable = declaration.Declaration.Variables[0];
        if (variable.Initializer?.Value is null
            || model.GetDeclaredSymbol(variable, cancellationToken) is not
                ILocalSymbol declared
            || !SymbolEqualityComparer.Default.Equals(declared, local.Local)
            || model.GetOperation(
                    variable.Initializer.Value,
                    cancellationToken)
                is not { } initializer)
        {
            return null;
        }
        return Unwrap(initializer) as IInvocationOperation;
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                default:
                    return operation;
            }
        }
    }

    private static Evidence CreateEvidence(
        long producingFileId,
        string producingFilePath,
        SyntaxNode syntax,
        IReadOnlyDictionary<string, string> metadata)
    {
        var span = syntax.GetLocation().GetLineSpan();
        return new Evidence(
            producingFileId,
            new SourceLocation(
                producingFilePath,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                span.EndLinePosition.Line + 1,
                span.EndLinePosition.Character + 1),
            EvidenceConfidence.Semantic,
            Producer,
            metadata);
    }

    private static string AllocatorFamilyToken(
        InteropAllocatorFamily family) => family switch
    {
        InteropAllocatorFamily.CoTaskMem => "co_task_mem",
        InteropAllocatorFamily.HGlobal => "hglobal",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
