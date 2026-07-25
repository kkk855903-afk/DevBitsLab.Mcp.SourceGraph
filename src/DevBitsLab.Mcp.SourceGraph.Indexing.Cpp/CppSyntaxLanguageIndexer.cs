using System.Security.Cryptography;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;
using TsNode = TreeSitter.Node;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Cpp;

/// <summary>
/// Safe, compiler-independent structural indexer for C and C++ source. It deliberately does not
/// preprocess includes or claim ABI facts: configured libclang indexing remains authoritative for
/// ABI/P/Invoke. This layer makes implementation files searchable even when their toolchain or
/// system headers are unavailable.
/// </summary>
public sealed class CppSyntaxLanguageIndexer : ILanguageIndexer, IBoundedSourceLanguageIndexer
{
    private const int MaximumTraversalNodes = 1_000_000;
    private const int MaximumAncestorHops = 1024;

    private static readonly IReadOnlyCollection<string> _extensions =
    [
        ".c",
        ".cc",
        ".cpp",
        ".cxx",
        ".c++",
        ".h",
        ".hh",
        ".hpp",
        ".hxx",
        ".inl",
        ".ipp",
    ];

    private readonly ILogger _logger;

    public CppSyntaxLanguageIndexer(ILogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    public IReadOnlyCollection<string> FileExtensions => _extensions;

    public int MaximumSourceSizeBytes => 10 * 1024 * 1024;

    public Task<IReadOnlyList<IndexEvent>> IndexAsync(
        IndexContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Contents.Length > MaximumSourceSizeBytes)
        {
            return Task.FromResult<IReadOnlyList<IndexEvent>>([]);
        }

        var source = Encoding.UTF8.GetString(ctx.Contents);
        var grammar = IsCSource(ctx.FilePath) ? "C" : "Cpp";
        using var parser = new Parser(TreeSitterAdapter.GetLanguage(grammar));
        using var tree = parser.Parse(source);
        var events = new List<IndexEvent>();
        if (tree is null)
        {
            _logger.LogDebug(
                "C/C++ parser returned no tree for {Path}",
                ctx.FilePath);
        }
        else
        {
            if (tree.RootNode.HasError)
            {
                // C++ source commonly contains compiler extensions that the portable grammar
                // represents with ERROR nodes while preserving the surrounding declarations.
                // This indexer emits only syntax-level facts with stable extracted names, so
                // walking the recovery tree is preferable to turning one unsupported construct
                // into a false "the whole .cpp file has no symbols" result.
                _logger.LogDebug(
                    "C/C++ parse tree for {Path} contains recovery nodes; indexing stable syntax declarations only",
                    ctx.FilePath);
            }
            IndexTree(tree.RootNode, ctx, events, ct);
        }

        events.Add(new IndexEvent.FileScanned(
            ctx.FilePath,
            SHA256.HashData(ctx.Contents)));
        return Task.FromResult<IReadOnlyList<IndexEvent>>(events);
    }

    private static void IndexTree(
        TsNode root,
        IndexContext ctx,
        List<IndexEvent> events,
        CancellationToken ct)
    {
        var declarations = CollectDeclarations(root, ctx, ct);
        events.AddRange(declarations.Select(item => item.Symbol));

        var byLeafAndArity = declarations
            .Where(item => item.Arity is not null)
            .GroupBy(
                item => new CallableLookup(item.LeafName, item.Arity!.Value),
                CallableLookupComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => group
                    .DistinctBy(item => item.Symbol.CanonicalKey)
                    .ToArray(),
                CallableLookupComparer.Instance);
        var declarationByNode = declarations
            .Where(item => item.NodeType == "function_definition")
            .GroupBy(item => item.Identity)
            .ToDictionary(
                group => group.Key,
                group => group.First());

        var visited = 0;
        Walk(root, node =>
        {
            ct.ThrowIfCancellationRequested();
            if (++visited > MaximumTraversalNodes)
            {
                throw new InvalidOperationException(
                    $"C/C++ syntax traversal exceeded {MaximumTraversalNodes} nodes.");
            }
            if (node.Type == "call_expression")
            {
                AddCallEvents(
                    node,
                    ctx,
                    byLeafAndArity,
                    declarationByNode,
                    events);
            }
        });
    }

    private static IReadOnlyList<Declaration> CollectDeclarations(
        TsNode root,
        IndexContext ctx,
        CancellationToken ct)
    {
        var declarations = new List<Declaration>();
        var visited = 0;
        Walk(root, node =>
        {
            ct.ThrowIfCancellationRequested();
            if (++visited > MaximumTraversalNodes)
            {
                throw new InvalidOperationException(
                    $"C/C++ syntax traversal exceeded {MaximumTraversalNodes} nodes.");
            }
            var declaration = TryCreateDeclaration(node, ctx);
            if (declaration is not null)
            {
                declarations.Add(declaration);
            }
        });
        return declarations
            .GroupBy(item => item.Symbol.CanonicalKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.NodeType == "function_definition")
                .First())
            .ToArray();
    }

    private static Declaration? TryCreateDeclaration(
        TsNode node,
        IndexContext ctx)
    {
        if (node.Type is "function_definition" or "declaration" or "field_declaration")
        {
            return TryCreateFunctionDeclaration(node, ctx);
        }

        var kind = node.Type switch
        {
            "class_specifier" => SymbolKinds.Class,
            "struct_specifier" => SymbolKinds.Struct,
            "union_specifier" => SymbolKinds.Union,
            "enum_specifier" => SymbolKinds.Enum,
            "alias_declaration" or "type_definition" => SymbolKinds.TypeAlias,
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        var nameNode = NamedField(node, "name")
            ?? FindFirstDescendant(
                node,
                "type_identifier",
                "identifier",
                "field_identifier");
        var name = nameNode?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var fqn = BuildQualifiedName(node, name);
        var repoRelativePath = RepoRelativePath(ctx);
        var scheme = IsCSource(ctx.FilePath) ? "c" : "cpp";
        var canonicalKey = kind == SymbolKinds.TypeAlias
            ? NativeCanonicalKeys.ForTypeAlias(
                scheme,
                repoRelativePath,
                $"syntax::{fqn}")
            : NativeCanonicalKeys.ForType(
                scheme,
                repoRelativePath,
                $"syntax::{fqn}");
        var (line, column) = TreeSitterAdapter.ToOneBased(node.StartPosition);
        var (endLine, endColumn) = TreeSitterAdapter.ToOneBased(node.EndPosition);
        var symbol = new IndexEvent.SymbolDeclared(
            canonicalKey,
            name,
            fqn,
            kind,
            line,
            column,
            endLine,
            endColumn,
            signature: TypeSignature(node.Type, name),
            modifiers: "syntax-only");
        return new Declaration(
            NodeIdentity.For(node),
            node.Type,
            name,
            Arity: null,
            symbol);
    }

    private static Declaration? TryCreateFunctionDeclaration(
        TsNode node,
        IndexContext ctx)
    {
        if (node.Type == "declaration" && IsInsideCompoundStatement(node))
        {
            return null;
        }

        var functionDeclarator = node.Type == "function_definition"
            ? FindFunctionDeclarator(NamedField(node, "declarator") ?? node)
            : FindFunctionDeclarator(node);
        if (functionDeclarator is null)
        {
            return null;
        }

        var declarator = NamedField(functionDeclarator, "declarator")
            ?? functionDeclarator;
        var nameNode = FindFunctionNameNode(declarator);
        var rawName = nameNode?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var leafName = FunctionLeafName(rawName);
        if (string.IsNullOrWhiteSpace(leafName))
        {
            return null;
        }

        var qualifiedName = rawName.Contains("::", StringComparison.Ordinal)
            ? NormalizeQualifiedName(rawName)
            : BuildQualifiedName(node, leafName);
        var parameterList = NamedField(functionDeclarator, "parameters")
            ?? FindFirstDescendant(functionDeclarator, "parameter_list");
        var arity = CountParameters(parameterList);
        var parameters = parameterList is null
            ? "()"
            : CollapseWhitespace(parameterList.Text);
        var repoRelativePath = RepoRelativePath(ctx);
        var scheme = IsCSource(ctx.FilePath) ? "c" : "cpp";
        var canonicalKey = NativeCanonicalKeys.ForFunction(
            scheme,
            repoRelativePath,
            $"syntax::{qualifiedName}{parameters}");

        var enclosingType = FindEnclosingTypeName(node);
        var isDestructor = leafName.StartsWith('~');
        var kind = !isDestructor && enclosingType is not null
            && string.Equals(
                leafName,
                enclosingType,
                StringComparison.Ordinal)
                ? SymbolKinds.Constructor
                : enclosingType is null && !rawName.Contains("::", StringComparison.Ordinal)
                    ? SymbolKinds.Function
                    : SymbolKinds.Method;
        var modifiers = FindFirstDescendant(node, "delete_method_clause") is null
            ? "syntax-only"
            : "syntax-only deleted";
        var (line, column) = TreeSitterAdapter.ToOneBased(node.StartPosition);
        var (endLine, endColumn) = TreeSitterAdapter.ToOneBased(node.EndPosition);
        var symbol = new IndexEvent.SymbolDeclared(
            canonicalKey,
            leafName,
            qualifiedName,
            kind,
            line,
            column,
            endLine,
            endColumn,
            signature: CollapseWhitespace(functionDeclarator.Text),
            modifiers: modifiers);
        return new Declaration(
            NodeIdentity.For(node),
            node.Type,
            leafName,
            arity,
            symbol);
    }

    private static void AddCallEvents(
        TsNode call,
        IndexContext ctx,
        IReadOnlyDictionary<CallableLookup, Declaration[]> byLeafAndArity,
        IReadOnlyDictionary<NodeIdentity, Declaration> declarationByNode,
        List<IndexEvent> events)
    {
        var function = NamedField(call, "function")
            ?? call.NamedChildren.FirstOrDefault(child =>
                child.Type != "argument_list");
        var arguments = NamedField(call, "arguments")
            ?? FindFirstDescendant(call, "argument_list");
        if (function is null || arguments is null)
        {
            return;
        }

        var leafName = FunctionLeafName(function.Text);
        var arity = arguments.NamedChildren.Count(child =>
            child.Type is not ("comment" or "preproc_arg"));
        if (!byLeafAndArity.TryGetValue(
                new CallableLookup(leafName, arity),
                out var candidates)
            || candidates.Length != 1)
        {
            return;
        }

        var source = FindEnclosingFunction(call, declarationByNode);
        if (source is null)
        {
            return;
        }

        var target = candidates[0];
        var nameNode = FindFunctionNameNode(function) ?? function;
        var (line, column) =
            TreeSitterAdapter.ToOneBased(nameNode.StartPosition);
        events.Add(new IndexEvent.ReferenceFound(
            target.Symbol.CanonicalKey,
            line,
            column,
            "call"));
        var (endLine, endColumn) =
            TreeSitterAdapter.ToOneBased(nameNode.EndPosition);
        events.Add(new IndexEvent.EdgeEmitted(
            source.Symbol.CanonicalKey,
            target.Symbol.CanonicalKey,
            EdgeKinds.Calls)
        {
            Evidence = new EdgeEvidence(
                new SourceLocation(
                    ctx.FilePath,
                    line,
                    column,
                    endLine,
                    endColumn),
                EvidenceConfidence.Inferred,
                "tree-sitter-cpp"),
        });
    }

    private static Declaration? FindEnclosingFunction(
        TsNode node,
        IReadOnlyDictionary<NodeIdentity, Declaration> declarationByNode)
    {
        var current = node.Parent;
        for (var hops = 0;
             hops < MaximumAncestorHops && current is not null;
             hops++, current = current.Parent)
        {
            if (current.Type is "lambda_expression")
            {
                return null;
            }
            if (current.Type == "function_definition"
                && declarationByNode.TryGetValue(
                    NodeIdentity.For(current),
                    out var declaration))
            {
                return declaration;
            }
        }
        return null;
    }

    private static string BuildQualifiedName(TsNode node, string leafName)
    {
        var containers = new List<string>();
        var current = node.Parent;
        for (var hops = 0;
             hops < MaximumAncestorHops && current is not null;
             hops++, current = current.Parent)
        {
            if (current.Type is not (
                    "namespace_definition"
                    or "class_specifier"
                    or "struct_specifier"
                    or "union_specifier"))
            {
                continue;
            }
            var name = NamedField(current, "name")
                ?? FindDirectNamedChild(
                    current,
                    "namespace_identifier",
                    "type_identifier");
            if (!string.IsNullOrWhiteSpace(name?.Text))
            {
                containers.Add(name.Text.Trim());
            }
        }
        containers.Reverse();
        containers.Add(leafName);
        return string.Join("::", containers);
    }

    private static string? FindEnclosingTypeName(TsNode node)
    {
        var current = node.Parent;
        for (var hops = 0;
             hops < MaximumAncestorHops && current is not null;
             hops++, current = current.Parent)
        {
            if (current.Type is not (
                    "class_specifier"
                    or "struct_specifier"
                    or "union_specifier"))
            {
                continue;
            }
            return (NamedField(current, "name")
                    ?? FindDirectNamedChild(current, "type_identifier"))
                ?.Text
                ?.Trim();
        }
        return null;
    }

    private static TsNode? FindFunctionDeclarator(TsNode node) =>
        node.Type == "function_declarator"
            ? node
            : FindFirstDescendant(node, "function_declarator");

    private static TsNode? FindFunctionNameNode(TsNode node)
    {
        if (node.Type is (
                "identifier"
                or "field_identifier"
                or "operator_name"
                or "destructor_name"))
        {
            return node;
        }
        if (node.Type is "qualified_identifier")
        {
            return NamedField(node, "name")
                ?? node.NamedChildren.LastOrDefault();
        }
        var declarator = NamedField(node, "declarator");
        if (declarator is not null)
        {
            return FindFunctionNameNode(declarator);
        }
        return node.NamedChildren
            .Select(FindFunctionNameNode)
            .FirstOrDefault(item => item is not null);
    }

    private static TsNode? FindFirstDescendant(
        TsNode node,
        params string[] types)
    {
        var accepted = types.ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<TsNode>();
        foreach (var child in node.NamedChildren.Reverse())
        {
            pending.Push(child);
        }
        var visited = 0;
        while (pending.Count > 0 && visited++ < MaximumTraversalNodes)
        {
            var current = pending.Pop();
            if (accepted.Contains(current.Type))
            {
                return current;
            }
            foreach (var child in current.NamedChildren.Reverse())
            {
                pending.Push(child);
            }
        }
        return null;
    }

    private static TsNode? FindDirectNamedChild(
        TsNode node,
        params string[] types)
    {
        var accepted = types.ToHashSet(StringComparer.Ordinal);
        return node.NamedChildren.FirstOrDefault(child =>
            accepted.Contains(child.Type));
    }

    private static TsNode? NamedField(TsNode node, string field)
    {
        try
        {
            var value = node[field];
            return string.IsNullOrEmpty(value.Type) ? null : value;
        }
        catch (KeyNotFoundException)
        {
            // The binding throws instead of returning an empty node when a grammar field is
            // optional (anonymous namespaces are the common C++ example).
            return null;
        }
    }

    private static int CountParameters(TsNode? parameterList)
    {
        if (parameterList is null)
        {
            return 0;
        }
        var parameters = parameterList.NamedChildren
            .Where(child => child.Type is (
                "parameter_declaration"
                or "optional_parameter_declaration"
                or "variadic_parameter"))
            .ToArray();
        return parameters.Length == 1
            && string.Equals(
                CollapseWhitespace(parameters[0].Text),
                "void",
                StringComparison.Ordinal)
                    ? 0
                    : parameters.Length;
    }

    private static string FunctionLeafName(string text)
    {
        var value = CollapseWhitespace(text);
        var separators = new[] { "->", ".", "::" };
        foreach (var separator in separators)
        {
            var index = value.LastIndexOf(separator, StringComparison.Ordinal);
            if (index >= 0)
            {
                value = value[(index + separator.Length)..];
            }
        }
        var paren = value.IndexOf('(');
        if (paren >= 0)
        {
            value = value[..paren];
        }
        var template = value.IndexOf('<');
        if (template > 0)
        {
            value = value[..template];
        }
        return value.Trim().TrimStart('*', '&');
    }

    private static string NormalizeQualifiedName(string value) =>
        CollapseWhitespace(value)
            .Replace(" :: ", "::", StringComparison.Ordinal)
            .Replace(":: ", "::", StringComparison.Ordinal)
            .Replace(" ::", "::", StringComparison.Ordinal);

    private static bool IsInsideCompoundStatement(TsNode node)
    {
        var current = node.Parent;
        for (var hops = 0;
             hops < MaximumAncestorHops && current is not null;
             hops++, current = current.Parent)
        {
            if (current.Type == "compound_statement")
            {
                return true;
            }
            if (current.Type is "translation_unit" or "namespace_definition")
            {
                return false;
            }
        }
        return false;
    }

    private static string TypeSignature(string nodeType, string name) =>
        nodeType switch
        {
            "class_specifier" => $"class {name}",
            "struct_specifier" => $"struct {name}",
            "union_specifier" => $"union {name}",
            "enum_specifier" => $"enum {name}",
            "alias_declaration" => $"using {name}",
            "type_definition" => $"typedef {name}",
            _ => name,
        };

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string RepoRelativePath(IndexContext ctx) =>
        Path.GetRelativePath(ctx.RepoRoot, ctx.FilePath)
            .Replace('\\', '/');

    private static bool IsCSource(string path) =>
        string.Equals(
            Path.GetExtension(path),
            ".c",
            StringComparison.OrdinalIgnoreCase);

    private static void Walk(TsNode root, Action<TsNode> visit)
    {
        var pending = new Stack<TsNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            visit(current);
            foreach (var child in current.NamedChildren.Reverse())
            {
                pending.Push(child);
            }
        }
    }

    private sealed record Declaration(
        NodeIdentity Identity,
        string NodeType,
        string LeafName,
        int? Arity,
        IndexEvent.SymbolDeclared Symbol);

    private readonly record struct NodeIdentity(
        string Type,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn)
    {
        public static NodeIdentity For(TsNode node) => new(
            node.Type,
            node.StartPosition.Row,
            node.StartPosition.Column,
            node.EndPosition.Row,
            node.EndPosition.Column);
    }

    private readonly record struct CallableLookup(string Name, int Arity);

    private sealed class CallableLookupComparer
        : IEqualityComparer<CallableLookup>
    {
        public static CallableLookupComparer Instance { get; } = new();

        public bool Equals(CallableLookup x, CallableLookup y) =>
            x.Arity == y.Arity
            && string.Equals(x.Name, y.Name, StringComparison.Ordinal);

        public int GetHashCode(CallableLookup obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Name),
                obj.Arity);
    }
}
