using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Wpf;

/// <summary>
/// Reports UI member access only when both ends of the thread-affinity claim are semantic facts:
/// an inline callback is scheduled by a known BCL background API, and the receiver's static type
/// derives from WPF's <c>DispatcherObject</c>. Indirect callbacks and aliases deliberately remain
/// unknown. This is an indexing-pipeline analyzer rather than a compiler extension because the
/// indexing assembly intentionally depends on Roslyn Workspaces.
/// </summary>
internal static class WpfUiThreadRiskAnalyzer
{
    internal const string DiagnosticId = "WPFTHREAD001";

#pragma warning disable RS2008 // The rule is internal to the indexer, not a shipped compiler analyzer.
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "WPF object accessed from a proven background callback",
        "UI-bound member '{0}' is accessed from background entry '{1}' at line {2}, column {3}",
        "WPF",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A member whose receiver statically derives from DispatcherObject is accessed inside "
            + "a callback directly scheduled by Task.Run, ThreadPool, or an immediately started "
            + "Thread. Marshal the access through Dispatcher.Invoke, BeginInvoke, or InvokeAsync.");
#pragma warning restore RS2008

    public static ImmutableArray<Diagnostic> Analyze(
        Compilation compilation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var symbols = KnownSymbols.TryCreate(compilation);
        if (symbols is null) return [];

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);

            foreach (var syntax in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (model.GetOperation(syntax, cancellationToken) is IInvocationOperation invocation)
                {
                    AnalyzeScheduledInvocation(invocation, symbols, diagnostics.Add);
                }
            }

            foreach (var syntax in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (model.GetOperation(syntax, cancellationToken) is IObjectCreationOperation creation)
                {
                    AnalyzeThreadCreation(creation, symbols, diagnostics.Add);
                }
            }
        }

        return diagnostics.ToImmutable();
    }

    private static void AnalyzeScheduledInvocation(
        IInvocationOperation invocation,
        KnownSymbols symbols,
        Action<Diagnostic> reportDiagnostic)
    {
        var target = invocation.TargetMethod;
        string? entryDescription = null;

        if (target.IsStatic
            && SymbolEqualityComparer.Default.Equals(target.ContainingType, symbols.Task)
            && target.Name == "Run")
        {
            entryDescription = "Task.Run";
        }
        else if (target.IsStatic
                 && SymbolEqualityComparer.Default.Equals(
                     target.ContainingType,
                     symbols.ThreadPool)
                 && (target.Name == "QueueUserWorkItem"
                     || target.Name == "UnsafeQueueUserWorkItem"))
        {
            entryDescription = $"ThreadPool.{target.Name}";
        }

        if (entryDescription is null) return;

        AnalyzeDirectCallbacks(
            invocation.Arguments,
            invocation.Syntax.GetLocation(),
            entryDescription,
            symbols,
            reportDiagnostic);
    }

    private static void AnalyzeThreadCreation(
        IObjectCreationOperation creation,
        KnownSymbols symbols,
        Action<Diagnostic> reportDiagnostic)
    {
        if (!SymbolEqualityComparer.Default.Equals(
                creation.Constructor?.ContainingType,
                symbols.Thread))
        {
            return;
        }

        if (!TryGetImmediateThreadStart(creation, symbols.Thread, out var start)) return;

        AnalyzeDirectCallbacks(
            creation.Arguments,
            start.Syntax.GetLocation(),
            "Thread.Start",
            symbols,
            reportDiagnostic);
    }

    private static void AnalyzeDirectCallbacks(
        ImmutableArray<IArgumentOperation> arguments,
        Location entryLocation,
        string entryDescription,
        KnownSymbols symbols,
        Action<Diagnostic> reportDiagnostic)
    {
        foreach (var argument in arguments)
        {
            var callback = TryGetDirectCallback(argument.Value);
            if (callback is null) continue;

            var walker = new BackgroundCallbackWalker(
                symbols,
                entryLocation,
                entryDescription,
                reportDiagnostic);
            walker.Visit(callback.Body);
        }
    }

    private static bool TryGetImmediateThreadStart(
        IObjectCreationOperation creation,
        INamedTypeSymbol threadType,
        out IInvocationOperation start)
    {
        IOperation current = creation;
        while (current.Parent is IConversionOperation or IParenthesizedOperation)
        {
            current = current.Parent;
        }

        if (current.Parent is IInvocationOperation invocation
            && invocation.Instance is not null
            && invocation.TargetMethod.Name == "Start"
            && !invocation.TargetMethod.IsStatic
            && SymbolEqualityComparer.Default.Equals(
                invocation.TargetMethod.ContainingType,
                threadType))
        {
            start = invocation;
            return true;
        }

        start = null!;
        return false;
    }

    private static IAnonymousFunctionOperation? TryGetDirectCallback(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IAnonymousFunctionOperation callback:
                    return callback;
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IDelegateCreationOperation delegateCreation:
                    operation = delegateCreation.Target;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                default:
                    return null;
            }
        }
    }

    private sealed class BackgroundCallbackWalker(
        KnownSymbols symbols,
        Location entryLocation,
        string entryDescription,
        Action<Diagnostic> reportDiagnostic) : OperationWalker
    {
        public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
        {
            // An arbitrary nested delegate may run synchronously, asynchronously, or never.
            // Its thread context is unknown, so do not inherit the outer callback's context.
        }

        public override void VisitInvocation(IInvocationOperation operation)
        {
            if (IsDirectDispatcherMarshal(operation))
            {
                // Reading Dispatcher is part of the proven marshal expression. Analyze any
                // non-callback argument in the current background context, but the direct
                // callback itself is known to run under Dispatcher and is therefore safe.
                foreach (var argument in operation.Arguments)
                {
                    if (TryGetDirectCallback(argument.Value) is null)
                    {
                        Visit(argument.Value);
                    }
                }

                return;
            }

            ReportIfUiMember(
                operation,
                operation.Instance,
                operation.TargetMethod);
            base.VisitInvocation(operation);
        }

        public override void VisitPropertyReference(IPropertyReferenceOperation operation)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    operation.Property,
                    symbols.DispatcherProperty))
            {
                ReportIfUiMember(
                    operation,
                    operation.Instance,
                    operation.Property);
            }

            base.VisitPropertyReference(operation);
        }

        public override void VisitFieldReference(IFieldReferenceOperation operation)
        {
            ReportIfUiMember(
                operation,
                operation.Instance,
                operation.Field);
            base.VisitFieldReference(operation);
        }

        public override void VisitEventReference(IEventReferenceOperation operation)
        {
            ReportIfUiMember(
                operation,
                operation.Instance,
                operation.Event);
            base.VisitEventReference(operation);
        }

        private bool IsDirectDispatcherMarshal(IInvocationOperation invocation)
        {
            if (invocation.TargetMethod.IsStatic
                || invocation.Instance is null
                || !SymbolEqualityComparer.Default.Equals(
                    invocation.TargetMethod.ContainingType,
                    symbols.Dispatcher)
                || invocation.TargetMethod.Name is not (
                    "Invoke" or "BeginInvoke" or "InvokeAsync"))
            {
                return false;
            }

            return invocation.Arguments.Any(argument =>
                TryGetDirectCallback(argument.Value) is not null);
        }

        private void ReportIfUiMember(
            IOperation operation,
            IOperation? receiver,
            ISymbol member)
        {
            if (receiver?.Type is not INamedTypeSymbol receiverType
                || member.IsStatic
                || !DerivesFrom(receiverType, symbols.DispatcherObject))
            {
                return;
            }

            var entryStart = entryLocation.GetLineSpan().StartLinePosition;
            var diagnostic = Diagnostic.Create(
                Rule,
                operation.Syntax.GetLocation(),
                additionalLocations: [entryLocation],
                properties: null,
                member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                entryDescription,
                entryStart.Line + 1,
                entryStart.Character + 1);
            reportDiagnostic(diagnostic);
        }
    }

    private static bool DerivesFrom(
        INamedTypeSymbol type,
        INamedTypeSymbol expectedBaseType)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, expectedBaseType)) return true;
        }

        return false;
    }

    private sealed record KnownSymbols(
        INamedTypeSymbol DispatcherObject,
        INamedTypeSymbol Dispatcher,
        IPropertySymbol? DispatcherProperty,
        INamedTypeSymbol Task,
        INamedTypeSymbol Thread,
        INamedTypeSymbol ThreadPool)
    {
        public static KnownSymbols? TryCreate(Compilation compilation)
        {
            var dispatcherObject = compilation.GetTypeByMetadataName(
                "System.Windows.Threading.DispatcherObject");
            var dispatcher = compilation.GetTypeByMetadataName(
                "System.Windows.Threading.Dispatcher");
            var task = compilation.GetTypeByMetadataName(
                "System.Threading.Tasks.Task");
            var thread = compilation.GetTypeByMetadataName(
                "System.Threading.Thread");
            var threadPool = compilation.GetTypeByMetadataName(
                "System.Threading.ThreadPool");
            if (dispatcherObject is null
                || dispatcher is null
                || task is null
                || thread is null
                || threadPool is null)
            {
                return null;
            }

            var dispatcherProperty = dispatcherObject
                .GetMembers("Dispatcher")
                .OfType<IPropertySymbol>()
                .SingleOrDefault(property =>
                    SymbolEqualityComparer.Default.Equals(
                        property.Type,
                        dispatcher));
            return new KnownSymbols(
                dispatcherObject,
                dispatcher,
                dispatcherProperty,
                task,
                thread,
                threadPool);
        }
    }
}
