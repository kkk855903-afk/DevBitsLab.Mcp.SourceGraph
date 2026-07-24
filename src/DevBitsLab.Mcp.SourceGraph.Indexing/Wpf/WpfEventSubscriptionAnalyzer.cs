using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Wpf;

internal enum WpfEventAssignmentKind
{
    Subscription,
    Unsubscription,
}

/// <summary>
/// A direct, semantically resolved assignment of a named instance handler to a source-defined
/// static event. More complex delegate construction is deliberately not represented as a fact.
/// </summary>
internal sealed record WpfEventLifetimeFact(
    WpfEventAssignmentKind Kind,
    string EventCanonicalKey,
    string HandlerCanonicalKey,
    string SubscriberTypeCanonicalKey,
    SyntaxTree SyntaxTree,
    TextSpan SourceSpan);

/// <summary>
/// A proven static-event subscription for which the complete compilation contains no exact
/// matching removal. The Roslyn indexer can project this typed finding into its diagnostics
/// table after resolving the producing syntax tree to a persisted file id.
/// </summary>
internal sealed record WpfEventUnsubscriptionFinding(
    string EventCanonicalKey,
    string HandlerCanonicalKey,
    string SubscriberTypeCanonicalKey,
    SyntaxTree SyntaxTree,
    TextSpan SourceSpan)
{
    internal const string RuleId = "WPFEVENT001";

    internal DiagnosticRecord ToDiagnosticRecord(long fileId, long? symbolId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileId, 1);

        var lineSpan = SyntaxTree.GetLineSpan(SourceSpan);
        return new DiagnosticRecord(
            symbolId,
            fileId,
            (int)DiagnosticSeverity.Warning,
            RuleId,
            $"Static event '{EventCanonicalKey}' retains instance handler "
            + $"'{HandlerCanonicalKey}', but no exact '-=' for the same event and handler "
            + "was found in the complete compilation.",
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }
}

/// <summary>
/// One deliberately unsupported or incomplete event-lifetime observation. Unknown observations
/// never become diagnostics; they make the analysis boundary inspectable in tests and callers.
/// </summary>
internal sealed record WpfEventLifetimeUnknown(
    string Reason,
    SyntaxTree? SyntaxTree,
    TextSpan? SourceSpan);

internal sealed record WpfEventSubscriptionAnalysis(
    bool IsComplete,
    IReadOnlyList<WpfEventLifetimeFact> Facts,
    IReadOnlyList<WpfEventUnsubscriptionFinding> Findings,
    IReadOnlyList<WpfEventLifetimeUnknown> Unknowns);

/// <summary>
/// Finds only the narrow static-event lifetime shape that a closed Roslyn compilation can prove.
/// A finding requires a source-defined static event, a direct named handler on the containing
/// instance, a complete semantic input closure, and no exact matching removal anywhere in that
/// compilation. Alias, lambda, external-event, broken-compilation, and unresolved-operation
/// shapes fail closed as unknown.
/// </summary>
internal static class WpfEventSubscriptionAnalyzer
{
    private const string SemanticInputIncomplete =
        "semantic-input-incomplete";
    private const string CompilationContainsErrors =
        "compilation-contains-errors";
    private const string DiagnosticDiscoveryFailed =
        "diagnostic-discovery-failed";
    private const string OperationDiscoveryFailed =
        "operation-discovery-failed";
    private const string EventDeclarationExternal =
        "event-declaration-outside-compilation";
    private const string HandlerNotDirectNamedInstance =
        "handler-not-direct-named-instance";
    private const string SymbolIdentityUnavailable =
        "symbol-identity-unavailable";
    private const string MatchingRemovalAmbiguous =
        "matching-removal-ambiguous";

    public static WpfEventSubscriptionAnalysis Analyze(
        Compilation compilation,
        bool semanticInputComplete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        if (!semanticInputComplete)
        {
            return Incomplete(SemanticInputIncomplete);
        }

        try
        {
            if (compilation.GetDiagnostics(cancellationToken).Any(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error))
            {
                return Incomplete(CompilationContainsErrors);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Incomplete(DiagnosticDiscoveryFailed);
        }

        var facts = new List<WpfEventLifetimeFact>();
        var assignments = new List<ResolvedAssignment>();
        var unknowns = new List<WpfEventLifetimeUnknown>();
        var ambiguousRemovalEvents = new HashSet<string>(
            StringComparer.Ordinal);

        try
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = tree.GetRoot(cancellationToken);
                var model = compilation.GetSemanticModel(tree);
                foreach (var syntax in root.DescendantNodes()
                             .OfType<AssignmentExpressionSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!syntax.IsKind(
                            Microsoft.CodeAnalysis.CSharp.SyntaxKind
                                .AddAssignmentExpression)
                        && !syntax.IsKind(
                            Microsoft.CodeAnalysis.CSharp.SyntaxKind
                                .SubtractAssignmentExpression))
                    {
                        continue;
                    }

                    if (model.GetOperation(syntax, cancellationToken) is not
                        IEventAssignmentOperation operation)
                    {
                        continue;
                    }

                    AnalyzeAssignment(
                        compilation,
                        operation,
                        syntax,
                        facts,
                        assignments,
                        unknowns,
                        ambiguousRemovalEvents);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new WpfEventSubscriptionAnalysis(
                false,
                [],
                [],
                [
                    new WpfEventLifetimeUnknown(
                        OperationDiscoveryFailed,
                        null,
                        null),
                ]);
        }

        var removalKeys = assignments
            .Where(assignment =>
                assignment.Fact.Kind
                    == WpfEventAssignmentKind.Unsubscription)
            .Select(assignment => assignment.Identity)
            .ToHashSet();
        var findings = new List<WpfEventUnsubscriptionFinding>();

        foreach (var subscription in assignments.Where(assignment =>
                     assignment.Fact.Kind
                         == WpfEventAssignmentKind.Subscription))
        {
            if (removalKeys.Contains(subscription.Identity))
            {
                continue;
            }
            if (ambiguousRemovalEvents.Contains(
                    subscription.Fact.EventCanonicalKey))
            {
                unknowns.Add(new WpfEventLifetimeUnknown(
                    MatchingRemovalAmbiguous,
                    subscription.Fact.SyntaxTree,
                    subscription.Fact.SourceSpan));
                continue;
            }

            findings.Add(new WpfEventUnsubscriptionFinding(
                subscription.Fact.EventCanonicalKey,
                subscription.Fact.HandlerCanonicalKey,
                subscription.Fact.SubscriberTypeCanonicalKey,
                subscription.Fact.SyntaxTree,
                subscription.Fact.SourceSpan));
        }

        return new WpfEventSubscriptionAnalysis(
            true,
            facts
                .OrderBy(fact => fact.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(fact => fact.SourceSpan.Start)
                .ThenBy(fact => fact.Kind)
                .ToArray(),
            findings
                .OrderBy(
                    finding => finding.SyntaxTree.FilePath,
                    StringComparer.Ordinal)
                .ThenBy(finding => finding.SourceSpan.Start)
                .ToArray(),
            unknowns
                .OrderBy(
                    unknown => unknown.SyntaxTree?.FilePath,
                    StringComparer.Ordinal)
                .ThenBy(unknown => unknown.SourceSpan?.Start ?? -1)
                .ThenBy(unknown => unknown.Reason, StringComparer.Ordinal)
                .ToArray());
    }

    private static void AnalyzeAssignment(
        Compilation compilation,
        IEventAssignmentOperation operation,
        AssignmentExpressionSyntax syntax,
        ICollection<WpfEventLifetimeFact> facts,
        ICollection<ResolvedAssignment> assignments,
        ICollection<WpfEventLifetimeUnknown> unknowns,
        ISet<string> ambiguousRemovalEvents)
    {
        if (operation.EventReference is not
            IEventReferenceOperation eventReference)
        {
            unknowns.Add(new WpfEventLifetimeUnknown(
                SymbolIdentityUnavailable,
                syntax.SyntaxTree,
                syntax.OperatorToken.Span));
            return;
        }

        var eventSymbol = eventReference.Event;
        if (!eventSymbol.IsStatic)
        {
            // Instance events are not inherently longer-lived than their subscribers and are
            // outside this rule's deliberately narrow proof.
            return;
        }

        var location = syntax.OperatorToken.Span;
        if (!IsDeclaredInCompilation(eventSymbol, compilation))
        {
            unknowns.Add(new WpfEventLifetimeUnknown(
                EventDeclarationExternal,
                syntax.SyntaxTree,
                location));
            return;
        }

        var eventKey = SymbolMapping.CanonicalKey(eventSymbol);
        if (eventKey is null)
        {
            unknowns.Add(new WpfEventLifetimeUnknown(
                SymbolIdentityUnavailable,
                syntax.SyntaxTree,
                location));
            return;
        }

        if (!TryResolveNamedInstanceHandler(
                operation.HandlerValue,
                compilation,
                out var handler,
                out var subscriberType))
        {
            unknowns.Add(new WpfEventLifetimeUnknown(
                HandlerNotDirectNamedInstance,
                syntax.SyntaxTree,
                location));
            if (!operation.Adds)
            {
                // An alias or other unsupported removal could denote any handler on this event.
                // Suppress otherwise-positive findings rather than treating it as proof of
                // absence.
                ambiguousRemovalEvents.Add(eventKey);
            }
            return;
        }

        var handlerKey = SymbolMapping.CanonicalKey(handler);
        var subscriberTypeKey = SymbolMapping.CanonicalKey(subscriberType);
        if (handlerKey is null || subscriberTypeKey is null)
        {
            unknowns.Add(new WpfEventLifetimeUnknown(
                SymbolIdentityUnavailable,
                syntax.SyntaxTree,
                location));
            if (!operation.Adds)
            {
                ambiguousRemovalEvents.Add(eventKey);
            }
            return;
        }

        var fact = new WpfEventLifetimeFact(
            operation.Adds
                ? WpfEventAssignmentKind.Subscription
                : WpfEventAssignmentKind.Unsubscription,
            eventKey,
            handlerKey,
            subscriberTypeKey,
            syntax.SyntaxTree,
            location);
        facts.Add(fact);
        assignments.Add(new ResolvedAssignment(
            fact,
            new AssignmentIdentity(eventKey, handlerKey)));
    }

    private static bool TryResolveNamedInstanceHandler(
        IOperation handlerValue,
        Compilation compilation,
        out IMethodSymbol handler,
        out INamedTypeSymbol subscriberType)
    {
        var current = Unwrap(handlerValue);
        if (current is IDelegateCreationOperation delegateCreation)
        {
            current = Unwrap(delegateCreation.Target);
        }

        if (current is not IMethodReferenceOperation
            {
                Method:
                {
                    IsStatic: false,
                    MethodKind: MethodKind.Ordinary,
                    ContainingType: { } containingType,
                } method,
                Instance: IInstanceReferenceOperation
                {
                    ReferenceKind:
                        InstanceReferenceKind.ContainingTypeInstance,
                },
            }
            || !IsDeclaredInCompilation(method, compilation)
            || !IsDeclaredInCompilation(containingType, compilation))
        {
            handler = null!;
            subscriberType = null!;
            return false;
        }

        handler = method;
        subscriberType = containingType;
        return true;
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

    private static bool IsDeclaredInCompilation(
        ISymbol symbol,
        Compilation compilation) =>
        symbol.Locations.Any(location =>
            location.IsInSource
            && location.SourceTree is { } sourceTree
            && compilation.ContainsSyntaxTree(sourceTree));

    private static WpfEventSubscriptionAnalysis Incomplete(string reason) =>
        new(
            false,
            [],
            [],
            [new WpfEventLifetimeUnknown(reason, null, null)]);

    private sealed record AssignmentIdentity(
        string EventCanonicalKey,
        string HandlerCanonicalKey);

    private sealed record ResolvedAssignment(
        WpfEventLifetimeFact Fact,
        AssignmentIdentity Identity);
}
