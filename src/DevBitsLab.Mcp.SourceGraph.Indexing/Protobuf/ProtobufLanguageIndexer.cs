using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;

public enum ProtobufSourceFailureKind
{
    SourceRejected,
    SourceTooLarge,
    InvalidEncoding,
    SyntaxError,
    LimitExceeded,
}

/// <summary>
/// A bounded protobuf source failure. The dispatcher treats this like any other per-file indexer
/// failure, so the last successful file projection remains intact.
/// </summary>
public sealed class ProtobufSourceIndexingException : FormatException
{
    public ProtobufSourceIndexingException(
        ProtobufSourceFailureKind kind,
        string message,
        int? line = null,
        int? column = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Line = line;
        Column = column;
    }

    public ProtobufSourceFailureKind Kind { get; }
    public int? Line { get; }
    public int? Column { get; }
}

/// <summary>
/// Pure source protobuf indexer. It recognizes package, nested message, numbered message field,
/// oneof, service, and unary/streaming RPC declarations without invoking protoc or reading
/// imports. Imports therefore make emitted facts explicitly partial. Enums, options, reserved
/// ranges, and extensions are parsed only far enough to preserve declaration boundaries; they
/// are not emitted as contract facts.
/// </summary>
public sealed class ProtobufLanguageIndexer :
    ILanguageIndexer,
    IBoundedSourceLanguageIndexer
{
    public const int MaximumSourceBytes = 1024 * 1024;
    public const int MaximumTokens = 100_000;
    public const int MaximumDeclarations = 10_000;
    public const int MaximumMessageNesting = 32;

    private static readonly UTF8Encoding _strictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public IReadOnlyCollection<string> FileExtensions { get; } = [".proto"];
    public int MaximumSourceSizeBytes => MaximumSourceBytes;

    public Task<IReadOnlyList<IndexEvent>> IndexAsync(
        IndexContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        // Reapply the scope boundary before inspecting source bytes. The normal dispatcher has
        // already made the same check before its disk read; this second gate keeps direct/plugin
        // invocation fail-closed and prevents excluded content from reaching the parser.
        if (!IsAuthorized(ctx))
        {
            return Task.FromResult<IReadOnlyList<IndexEvent>>(
                Array.Empty<IndexEvent>());
        }

        ArgumentNullException.ThrowIfNull(ctx.Contents);
        if (ctx.Contents.Length > MaximumSourceBytes)
        {
            throw Failure(
                ProtobufSourceFailureKind.SourceTooLarge,
                $"Protobuf source exceeds the {MaximumSourceBytes}-byte limit.");
        }

        string source;
        try
        {
            source = _strictUtf8.GetString(ctx.Contents);
        }
        catch (DecoderFallbackException ex)
        {
            throw Failure(
                ProtobufSourceFailureKind.InvalidEncoding,
                "Protobuf source is not valid UTF-8.",
                innerException: ex);
        }

        var document = new Parser(source, ct).Parse();
        ct.ThrowIfCancellationRequested();
        var events = Emit(document);
        events.Add(new IndexEvent.FileScanned(
            ctx.FilePath,
            SHA256.HashData(ctx.Contents)));
        return Task.FromResult<IReadOnlyList<IndexEvent>>(events);
    }

    private static bool IsAuthorized(IndexContext ctx)
    {
        try
        {
            var policy = new ScopePathPolicy(
                ctx.RepoRoot,
                ctx.ExcludePatterns);
            return !policy.IsExcluded(ctx.FilePath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            throw Failure(
                ProtobufSourceFailureKind.SourceRejected,
                "Protobuf source path could not be authorized inside its scope.",
                innerException: ex);
        }
    }

    private static List<IndexEvent> Emit(ParsedDocument document)
    {
        var events = new List<IndexEvent>(
            document.Declarations.Count * 2 + 1);
        var status = document.ImportCount == 0
            ? ProtoContractStatus.Complete
            : ProtoContractStatus.Partial;
        IReadOnlyList<string> incompleteReasons =
            document.ImportCount == 0
                ? Array.Empty<string>()
                : [ProtoContractPayloadCodec.ImportsNotResolvedReason];

        foreach (var declaration in document.Declarations)
        {
            switch (declaration)
            {
                case ParsedMessage message:
                    EmitMessage(
                        events,
                        document,
                        message,
                        status,
                        incompleteReasons);
                    break;

                case ParsedField field:
                    EmitField(
                        events,
                        document,
                        field,
                        status,
                        incompleteReasons);
                    break;

                case ParsedRpc rpc:
                    EmitRpc(
                        events,
                        document,
                        rpc,
                        status,
                        incompleteReasons);
                    break;
            }
        }

        return events;
    }

    private static void EmitMessage(
        List<IndexEvent> events,
        ParsedDocument document,
        ParsedMessage message,
        ProtoContractStatus status,
        IReadOnlyList<string> incompleteReasons)
    {
        var key = ProtoCanonicalKeys.ForMessage(message.FullName);
        events.Add(new IndexEvent.SymbolDeclared(
            key,
            message.Name.Text,
            message.FullName,
            SymbolKinds.Message,
            message.Name.Line,
            message.Name.Column,
            message.End.Line,
            message.End.EndColumn,
            signature: $"message {message.FullName}",
            containerCanonicalKey: message.ParentFullName is null
                ? null
                : ProtoCanonicalKeys.ForMessage(message.ParentFullName),
            accessibility: 6));

        events.Add(CreateAnnotation(
            key,
            "ProtoMessageContract",
            "protobuf.contract.v1.message",
            new ProtoContractFact(
                ProtoContractKind.Message,
                key,
                document.Package,
                message.FullName,
                status,
                incompleteReasons,
                document.ImportCount,
                new ProtoMessageContract(
                    message.ParentFullName,
                    message.NestingDepth),
                null,
                null)));
    }

    private static void EmitField(
        List<IndexEvent> events,
        ParsedDocument document,
        ParsedField field,
        ProtoContractStatus status,
        IReadOnlyList<string> incompleteReasons)
    {
        var key = ProtoCanonicalKeys.ForField(
            field.MessageFullName,
            field.Name.Text);
        var modifiers = FieldModifiers(field);
        events.Add(new IndexEvent.SymbolDeclared(
            key,
            field.Name.Text,
            field.FullName,
            SymbolKinds.ProtoField,
            field.Name.Line,
            field.Name.Column,
            field.End.Line,
            field.End.EndColumn,
            signature: FieldSignature(field),
            containerCanonicalKey:
                ProtoCanonicalKeys.ForMessage(field.MessageFullName),
            modifiers: modifiers,
            accessibility: 6));

        events.Add(CreateAnnotation(
            key,
            "ProtoFieldContract",
            "protobuf.contract.v1.field",
            new ProtoContractFact(
                ProtoContractKind.Field,
                key,
                document.Package,
                field.FullName,
                status,
                incompleteReasons,
                document.ImportCount,
                null,
                new ProtoFieldContract(
                    field.MessageFullName,
                    field.Type,
                    field.Number,
                    field.Cardinality,
                    field.OneofName),
                null)));
    }

    private static void EmitRpc(
        List<IndexEvent> events,
        ParsedDocument document,
        ParsedRpc rpc,
        ProtoContractStatus status,
        IReadOnlyList<string> incompleteReasons)
    {
        var key = ProtoCanonicalKeys.ForRpc(
            rpc.ServiceFullName,
            rpc.Name.Text);
        var modifiers = new List<string>(2);
        if (rpc.ClientStreaming) modifiers.Add("client-streaming");
        if (rpc.ServerStreaming) modifiers.Add("server-streaming");

        events.Add(new IndexEvent.SymbolDeclared(
            key,
            rpc.Name.Text,
            rpc.FullName,
            SymbolKinds.Rpc,
            rpc.Name.Line,
            rpc.Name.Column,
            rpc.End.Line,
            rpc.End.EndColumn,
            signature: RpcSignature(rpc),
            modifiers: modifiers.Count == 0
                ? null
                : string.Join(' ', modifiers),
            accessibility: 6));

        events.Add(CreateAnnotation(
            key,
            "ProtoRpcContract",
            "protobuf.contract.v1.rpc",
            new ProtoContractFact(
                ProtoContractKind.Rpc,
                key,
                document.Package,
                rpc.FullName,
                status,
                incompleteReasons,
                document.ImportCount,
                null,
                null,
                new ProtoRpcContract(
                    rpc.ServiceFullName,
                    rpc.InputType,
                    rpc.OutputType,
                    rpc.ClientStreaming,
                    rpc.ServerStreaming))));
    }

    private static IndexEvent.AnnotationAttached CreateAnnotation(
        string key,
        string name,
        string fullName,
        ProtoContractFact fact) =>
        new(
            key,
            name,
            ProtoContractAnnotations.Flavor,
            fullName,
            ProtoContractPayloadCodec.Encode(fact));

    private static string FieldSignature(ParsedField field)
    {
        var prefix = field.Cardinality switch
        {
            ProtoFieldCardinality.Optional => "optional ",
            ProtoFieldCardinality.Repeated => "repeated ",
            ProtoFieldCardinality.Required => "required ",
            _ => string.Empty,
        };
        return $"{prefix}{field.Type} {field.Name.Text} = {field.Number}";
    }

    private static string? FieldModifiers(ParsedField field)
    {
        var values = new List<string>(2);
        if (field.Cardinality != ProtoFieldCardinality.Singular)
        {
            values.Add(field.Cardinality switch
            {
                ProtoFieldCardinality.Optional => "optional",
                ProtoFieldCardinality.Repeated => "repeated",
                ProtoFieldCardinality.Required => "required",
                _ => throw Failure(
                    ProtobufSourceFailureKind.SyntaxError,
                    "Unknown protobuf field cardinality."),
            });
        }
        if (field.OneofName is not null)
        {
            values.Add("oneof");
        }
        return values.Count == 0 ? null : string.Join(' ', values);
    }

    private static string RpcSignature(ParsedRpc rpc) =>
        $"rpc {rpc.Name.Text}("
        + (rpc.ClientStreaming ? "stream " : string.Empty)
        + rpc.InputType
        + ") returns ("
        + (rpc.ServerStreaming ? "stream " : string.Empty)
        + rpc.OutputType
        + ")";

    private static ProtobufSourceIndexingException Failure(
        ProtobufSourceFailureKind kind,
        string message,
        int? line = null,
        int? column = null,
        Exception? innerException = null) =>
        new(kind, message, line, column, innerException);

    private sealed class Parser
    {
        private const int MaximumImports = 256;
        private readonly Lexer _lexer;
        private readonly CancellationToken _ct;
        private readonly List<ParsedDeclaration> _declarations = [];
        private readonly HashSet<string> _canonicalKeys =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _scopeNames =
            new(StringComparer.Ordinal);
        private Token _current;
        private string _package = string.Empty;
        private bool _packageSeen;
        private bool _syntaxSeen;
        private bool _editionSeen;
        private bool _declarationsStarted;
        private int _importCount;

        public Parser(string source, CancellationToken ct)
        {
            _lexer = new Lexer(source, ct);
            _ct = ct;
            _current = _lexer.Next();
        }

        public ParsedDocument Parse()
        {
            while (_current.Kind != TokenKind.End)
            {
                _ct.ThrowIfCancellationRequested();
                if (Match(";")) continue;

                switch (_current.Text)
                {
                    case "syntax":
                        ParseSyntax();
                        break;

                    case "edition":
                        ParseEdition();
                        break;

                    case "package":
                        ParsePackage();
                        break;

                    case "import":
                        ParseImport();
                        break;

                    case "option":
                        SkipStatement();
                        break;

                    case "message":
                        _declarationsStarted = true;
                        ParseMessage(parentFullName: null, nestingDepth: 0);
                        break;

                    case "service":
                        _declarationsStarted = true;
                        ParseService();
                        break;

                    case "enum":
                        _declarationsStarted = true;
                        SkipNamedBlock(registerScopeName: true);
                        break;

                    case "extend":
                        _declarationsStarted = true;
                        SkipBlockDeclaration();
                        break;

                    default:
                        throw Error(
                            $"Unsupported or unexpected top-level token `{_current.Text}`.");
                }
            }

            return new ParsedDocument(
                _package,
                _importCount,
                _declarations.ToArray());
        }

        private void ParseSyntax()
        {
            var keyword = _current;
            if (_syntaxSeen || _editionSeen || _declarationsStarted)
            {
                throw Error(
                    "syntax must appear once before protobuf declarations.",
                    keyword);
            }
            _syntaxSeen = true;
            Advance();
            Expect("=");
            var value = ExpectKind(TokenKind.String, "syntax string");
            if (value.Text is not ("proto2" or "proto3"))
            {
                throw Error(
                    $"Unsupported protobuf syntax `{value.Text}`.",
                    value);
            }
            Expect(";");
        }

        private void ParseEdition()
        {
            var keyword = _current;
            if (_editionSeen || _syntaxSeen || _declarationsStarted)
            {
                throw Error(
                    "edition must appear once before protobuf declarations.",
                    keyword);
            }
            _editionSeen = true;
            Advance();
            Expect("=");
            ExpectKind(TokenKind.String, "edition string");
            Expect(";");
        }

        private void ParsePackage()
        {
            var keyword = _current;
            if (_packageSeen || _declarationsStarted)
            {
                throw Error(
                    "package must appear at most once before declarations.",
                    keyword);
            }
            _packageSeen = true;
            Advance();
            _package = ParseDottedName(allowLeadingDot: false);
            Expect(";");
        }

        private void ParseImport()
        {
            Advance();
            if (_current.Kind == TokenKind.Identifier
                && _current.Text is "public" or "weak")
            {
                Advance();
            }
            ExpectKind(TokenKind.String, "import path");
            Expect(";");
            _importCount++;
            if (_importCount > MaximumImports)
            {
                throw Limit(
                    $"Protobuf source exceeds the {MaximumImports}-import limit.");
            }
        }

        private void ParseMessage(
            string? parentFullName,
            int nestingDepth)
        {
            if (nestingDepth > MaximumMessageNesting)
            {
                throw Limit(
                    $"Protobuf message nesting exceeds the "
                    + $"{MaximumMessageNesting}-level limit.");
            }

            Expect("message");
            var name = ExpectKind(TokenKind.Identifier, "message name");
            var fullName = Qualify(parentFullName, name.Text);
            RegisterScopeName(fullName, name);
            RegisterCanonicalKey(
                ProtoCanonicalKeys.ForMessage(fullName),
                name);
            RegisterDeclaration();
            var message = new ParsedMessage(
                name,
                name,
                fullName,
                parentFullName,
                nestingDepth);
            _declarations.Add(message);

            Expect("{");
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            var fieldNumbers = new HashSet<int>();
            while (!Match("}"))
            {
                _ct.ThrowIfCancellationRequested();
                if (_current.Kind == TokenKind.End)
                {
                    throw Error("Unterminated message declaration.", name);
                }
                if (Match(";")) continue;

                switch (_current.Text)
                {
                    case "message":
                        ParseMessage(fullName, nestingDepth + 1);
                        break;

                    case "oneof":
                        ParseOneof(fullName, fieldNames, fieldNumbers);
                        break;

                    case "enum":
                        SkipNamedBlock(registerScopeName: false);
                        break;

                    case "option":
                    case "reserved":
                    case "extensions":
                        SkipStatement();
                        break;

                    case "extend":
                        SkipBlockDeclaration();
                        break;

                    default:
                        ParseField(
                            fullName,
                            oneofName: null,
                            fieldNames,
                            fieldNumbers);
                        break;
                }
            }
            message.End = _previous;
            Match(";");
        }

        private void ParseOneof(
            string messageFullName,
            HashSet<string> fieldNames,
            HashSet<int> fieldNumbers)
        {
            Expect("oneof");
            var name = ExpectKind(TokenKind.Identifier, "oneof name");
            Expect("{");
            while (!Match("}"))
            {
                _ct.ThrowIfCancellationRequested();
                if (_current.Kind == TokenKind.End)
                {
                    throw Error("Unterminated oneof declaration.", name);
                }
                if (Match(";")) continue;
                if (_current.Text == "option")
                {
                    SkipStatement();
                    continue;
                }
                ParseField(
                    messageFullName,
                    name.Text,
                    fieldNames,
                    fieldNumbers);
            }
            Match(";");
        }

        private void ParseField(
            string messageFullName,
            string? oneofName,
            HashSet<string> fieldNames,
            HashSet<int> fieldNumbers)
        {
            var cardinality = ProtoFieldCardinality.Singular;
            if (_current.Kind == TokenKind.Identifier
                && _current.Text is "optional" or "repeated" or "required")
            {
                if (oneofName is not null)
                {
                    throw Error(
                        "A oneof field cannot have a cardinality label.");
                }
                cardinality = _current.Text switch
                {
                    "optional" => ProtoFieldCardinality.Optional,
                    "repeated" => ProtoFieldCardinality.Repeated,
                    "required" => ProtoFieldCardinality.Required,
                    _ => ProtoFieldCardinality.Singular,
                };
                Advance();
            }

            var type = ParseFieldType();
            var name = ExpectKind(TokenKind.Identifier, "field name");
            Expect("=");
            var numberToken = ExpectKind(TokenKind.Integer, "field number");
            if (!int.TryParse(
                    numberToken.Text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number)
                || number is < 1 or > 536_870_911
                || number is >= 19_000 and <= 19_999)
            {
                throw Error(
                    $"Invalid protobuf field number `{numberToken.Text}`.",
                    numberToken);
            }

            if (Match("["))
            {
                SkipBalanced("[", "]", _previous);
            }
            var end = Expect(";");

            if (!fieldNames.Add(name.Text))
            {
                throw Error(
                    $"Duplicate protobuf field name `{name.Text}`.",
                    name);
            }
            if (!fieldNumbers.Add(number))
            {
                throw Error(
                    $"Duplicate protobuf field number `{number}`.",
                    numberToken);
            }

            var fullName = messageFullName + "." + name.Text;
            RegisterCanonicalKey(
                ProtoCanonicalKeys.ForField(messageFullName, name.Text),
                name);
            RegisterDeclaration();
            _declarations.Add(new ParsedField(
                name,
                end,
                fullName,
                messageFullName,
                type,
                number,
                cardinality,
                oneofName)
            {
                End = end,
            });
        }

        private string ParseFieldType()
        {
            if (_current.Text != "map")
            {
                return ParseDottedName(allowLeadingDot: true);
            }

            Advance();
            Expect("<");
            var keyType = ParseDottedName(allowLeadingDot: false);
            Expect(",");
            var valueType = ParseDottedName(allowLeadingDot: true);
            Expect(">");
            return $"map<{keyType},{valueType}>";
        }

        private void ParseService()
        {
            Expect("service");
            var name = ExpectKind(TokenKind.Identifier, "service name");
            var serviceFullName = Qualify(parentFullName: null, name.Text);
            RegisterScopeName(serviceFullName, name);
            var rpcNames = new HashSet<string>(StringComparer.Ordinal);
            Expect("{");

            while (!Match("}"))
            {
                _ct.ThrowIfCancellationRequested();
                if (_current.Kind == TokenKind.End)
                {
                    throw Error("Unterminated service declaration.", name);
                }
                if (Match(";")) continue;
                if (_current.Text == "option")
                {
                    SkipStatement();
                    continue;
                }
                if (_current.Text != "rpc")
                {
                    throw Error(
                        $"Unsupported service member `{_current.Text}`.");
                }
                ParseRpc(serviceFullName, rpcNames);
            }
            Match(";");
        }

        private void ParseRpc(
            string serviceFullName,
            HashSet<string> rpcNames)
        {
            Expect("rpc");
            var name = ExpectKind(TokenKind.Identifier, "RPC name");
            if (!rpcNames.Add(name.Text))
            {
                throw Error($"Duplicate RPC name `{name.Text}`.", name);
            }
            Expect("(");
            var clientStreaming = Match("stream");
            var inputType = ParseDottedName(allowLeadingDot: true);
            Expect(")");
            Expect("returns");
            Expect("(");
            var serverStreaming = Match("stream");
            var outputType = ParseDottedName(allowLeadingDot: true);
            Expect(")");

            Token end;
            if (Match(";"))
            {
                end = _previous;
            }
            else if (Match("{"))
            {
                end = SkipBalanced("{", "}", _previous);
                Match(";");
            }
            else
            {
                throw Error("RPC declaration must end with `;` or an option block.");
            }

            var fullName = serviceFullName + "." + name.Text;
            RegisterCanonicalKey(
                ProtoCanonicalKeys.ForRpc(serviceFullName, name.Text),
                name);
            RegisterDeclaration();
            _declarations.Add(new ParsedRpc(
                name,
                end,
                fullName,
                serviceFullName,
                inputType,
                outputType,
                clientStreaming,
                serverStreaming)
            {
                End = end,
            });
        }

        private void SkipNamedBlock(bool registerScopeName)
        {
            Advance();
            var name = ExpectKind(TokenKind.Identifier, "declaration name");
            if (registerScopeName)
            {
                RegisterScopeName(
                    Qualify(parentFullName: null, name.Text),
                    name);
            }
            Expect("{");
            SkipBalanced("{", "}", _previous);
            Match(";");
        }

        private void SkipBlockDeclaration()
        {
            Advance();
            while (_current.Kind != TokenKind.End && _current.Text != "{")
            {
                Advance();
            }
            if (_current.Kind == TokenKind.End)
            {
                throw Error("Unterminated protobuf block declaration.");
            }
            Expect("{");
            SkipBalanced("{", "}", _previous);
            Match(";");
        }

        private void SkipStatement()
        {
            var start = _current;
            var stack = new Stack<string>();
            Advance();
            while (_current.Kind != TokenKind.End)
            {
                if (_current.Text == ";" && stack.Count == 0)
                {
                    Advance();
                    return;
                }
                if (_current.Text is "(" or "[" or "{")
                {
                    stack.Push(_current.Text switch
                    {
                        "(" => ")",
                        "[" => "]",
                        _ => "}",
                    });
                    if (stack.Count > MaximumMessageNesting)
                    {
                        throw Limit(
                            "Protobuf option nesting exceeds the parser limit.",
                            _current);
                    }
                }
                else if (_current.Text is ")" or "]" or "}")
                {
                    if (stack.Count == 0
                        || !string.Equals(
                            stack.Pop(),
                            _current.Text,
                            StringComparison.Ordinal))
                    {
                        throw Error(
                            "Mismatched delimiter in protobuf statement.");
                    }
                }
                Advance();
            }
            throw Error("Unterminated protobuf statement.", start);
        }

        private Token SkipBalanced(
            string open,
            string close,
            Token openingToken)
        {
            var depth = 1;
            while (_current.Kind != TokenKind.End)
            {
                if (_current.Text == open)
                {
                    depth++;
                    if (depth > MaximumMessageNesting)
                    {
                        throw Limit(
                            "Protobuf delimiter nesting exceeds the parser limit.",
                            _current);
                    }
                }
                else if (_current.Text == close)
                {
                    depth--;
                    var closing = _current;
                    Advance();
                    if (depth == 0) return closing;
                }
                Advance();
            }
            throw Error(
                $"Unterminated `{open}` delimiter.",
                openingToken);
        }

        private string ParseDottedName(bool allowLeadingDot)
        {
            var builder = new StringBuilder();
            if (allowLeadingDot && Match("."))
            {
                builder.Append('.');
            }
            var first = ExpectKind(TokenKind.Identifier, "protobuf type/name");
            builder.Append(first.Text);
            while (Match("."))
            {
                var segment = ExpectKind(
                    TokenKind.Identifier,
                    "protobuf name segment");
                builder.Append('.').Append(segment.Text);
            }
            return builder.ToString();
        }

        private string Qualify(string? parentFullName, string name)
        {
            if (parentFullName is not null)
            {
                return parentFullName + "." + name;
            }
            return string.IsNullOrEmpty(_package)
                ? name
                : _package + "." + name;
        }

        private void RegisterScopeName(string fullName, Token token)
        {
            if (!_scopeNames.Add(fullName))
            {
                throw Error(
                    $"Duplicate protobuf declaration `{fullName}`.",
                    token);
            }
        }

        private void RegisterCanonicalKey(string key, Token token)
        {
            if (!_canonicalKeys.Add(key))
            {
                throw Error(
                    $"Duplicate protobuf canonical key `{key}`.",
                    token);
            }
        }

        private void RegisterDeclaration()
        {
            if (_declarations.Count >= MaximumDeclarations)
            {
                throw Limit(
                    $"Protobuf source exceeds the "
                    + $"{MaximumDeclarations}-declaration limit.");
            }
        }

        private bool Match(string text)
        {
            if (!string.Equals(_current.Text, text, StringComparison.Ordinal))
            {
                return false;
            }
            Advance();
            return true;
        }

        private Token Expect(string text)
        {
            if (!string.Equals(_current.Text, text, StringComparison.Ordinal))
            {
                throw Error(
                    $"Expected `{text}`, found `{_current.Text}`.");
            }
            var token = _current;
            Advance();
            return token;
        }

        private Token ExpectKind(TokenKind kind, string description)
        {
            if (_current.Kind != kind)
            {
                throw Error(
                    $"Expected {description}, found `{_current.Text}`.");
            }
            var token = _current;
            Advance();
            return token;
        }

        private Token _previous;

        private void Advance()
        {
            _previous = _current;
            _current = _lexer.Next();
        }

        private static ProtobufSourceIndexingException Error(
            string message,
            Token? token = null)
        {
            var location = token ?? default;
            return Failure(
                ProtobufSourceFailureKind.SyntaxError,
                token is null
                    ? message
                    : $"{message} (line {location.Line}, column {location.Column})",
                token?.Line,
                token?.Column);
        }

        private ProtobufSourceIndexingException Error(string message) =>
            Error(message, _current);

        private static ProtobufSourceIndexingException Limit(
            string message,
            Token? token = null) =>
            Failure(
                ProtobufSourceFailureKind.LimitExceeded,
                token is null
                    ? message
                    : $"{message} (line {token.Value.Line}, "
                      + $"column {token.Value.Column})",
                token?.Line,
                token?.Column);
    }

    private sealed class Lexer
    {
        private const int MaximumIdentifierCharacters = 512;
        private const int MaximumStringCharacters = 16 * 1024;
        private readonly string _source;
        private readonly CancellationToken _ct;
        private int _index;
        private int _line = 1;
        private int _column = 1;
        private int _tokenCount;

        public Lexer(string source, CancellationToken ct)
        {
            _source = source;
            _ct = ct;
        }

        public Token Next()
        {
            _ct.ThrowIfCancellationRequested();
            SkipTrivia();
            if (_index >= _source.Length)
            {
                return new Token(
                    TokenKind.End,
                    "<eof>",
                    _line,
                    _column,
                    _line,
                    _column);
            }

            _tokenCount++;
            if (_tokenCount > MaximumTokens)
            {
                throw Failure(
                    ProtobufSourceFailureKind.LimitExceeded,
                    $"Protobuf source exceeds the {MaximumTokens}-token limit.",
                    _line,
                    _column);
            }

            var line = _line;
            var column = _column;
            var current = _source[_index];
            if (IsIdentifierStart(current))
            {
                var start = _index;
                AdvanceCharacter();
                while (_index < _source.Length
                       && IsIdentifierPart(_source[_index]))
                {
                    AdvanceCharacter();
                }
                var text = _source[start.._index];
                if (text.Length > MaximumIdentifierCharacters)
                {
                    throw Failure(
                        ProtobufSourceFailureKind.LimitExceeded,
                        $"Protobuf identifier exceeds the "
                        + $"{MaximumIdentifierCharacters}-character limit.",
                        line,
                        column);
                }
                return TokenAt(
                    TokenKind.Identifier,
                    text,
                    line,
                    column);
            }

            if (current is >= '0' and <= '9')
            {
                var start = _index;
                AdvanceCharacter();
                while (_index < _source.Length
                       && _source[_index] is >= '0' and <= '9')
                {
                    AdvanceCharacter();
                }
                return TokenAt(
                    TokenKind.Integer,
                    _source[start.._index],
                    line,
                    column);
            }

            if (current is '"' or '\'')
            {
                return ReadString(current, line, column);
            }

            if (char.IsControl(current) && !char.IsWhiteSpace(current))
            {
                throw Failure(
                    ProtobufSourceFailureKind.SyntaxError,
                    "Protobuf source contains an unsupported control character.",
                    line,
                    column);
            }
            if (current > 127)
            {
                throw Failure(
                    ProtobufSourceFailureKind.SyntaxError,
                    "Protobuf identifiers and punctuation must use the ASCII grammar.",
                    line,
                    column);
            }

            AdvanceCharacter();
            return TokenAt(
                TokenKind.Symbol,
                current.ToString(),
                line,
                column);
        }

        private Token ReadString(char quote, int line, int column)
        {
            AdvanceCharacter();
            var value = new StringBuilder();
            while (_index < _source.Length)
            {
                var current = _source[_index];
                if (current == quote)
                {
                    AdvanceCharacter();
                    return TokenAt(
                        TokenKind.String,
                        value.ToString(),
                        line,
                        column);
                }
                if (current is '\r' or '\n')
                {
                    throw Failure(
                        ProtobufSourceFailureKind.SyntaxError,
                        "Unterminated protobuf string literal.",
                        line,
                        column);
                }
                if (current == '\\')
                {
                    AdvanceCharacter();
                    if (_index >= _source.Length)
                    {
                        break;
                    }
                    current = _source[_index];
                    AdvanceCharacter();
                    value.Append(current switch
                    {
                        'a' => '\a',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'v' => '\v',
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '"',
                        _ => current,
                    });
                }
                else
                {
                    value.Append(current);
                    AdvanceCharacter();
                }

                if (value.Length > MaximumStringCharacters)
                {
                    throw Failure(
                        ProtobufSourceFailureKind.LimitExceeded,
                        $"Protobuf string exceeds the "
                        + $"{MaximumStringCharacters}-character limit.",
                        line,
                        column);
                }
            }

            throw Failure(
                ProtobufSourceFailureKind.SyntaxError,
                "Unterminated protobuf string literal.",
                line,
                column);
        }

        private void SkipTrivia()
        {
            while (_index < _source.Length)
            {
                _ct.ThrowIfCancellationRequested();
                if (_index == 0 && _source[_index] == '\uFEFF')
                {
                    AdvanceCharacter();
                    continue;
                }
                if (char.IsWhiteSpace(_source[_index]))
                {
                    AdvanceCharacter();
                    continue;
                }
                if (_source[_index] != '/'
                    || _index + 1 >= _source.Length)
                {
                    return;
                }
                if (_source[_index + 1] == '/')
                {
                    AdvanceCharacter();
                    AdvanceCharacter();
                    while (_index < _source.Length
                           && _source[_index] is not ('\r' or '\n'))
                    {
                        AdvanceCharacter();
                    }
                    continue;
                }
                if (_source[_index + 1] == '*')
                {
                    var line = _line;
                    var column = _column;
                    AdvanceCharacter();
                    AdvanceCharacter();
                    var closed = false;
                    while (_index < _source.Length)
                    {
                        if (_source[_index] == '*'
                            && _index + 1 < _source.Length
                            && _source[_index + 1] == '/')
                        {
                            AdvanceCharacter();
                            AdvanceCharacter();
                            closed = true;
                            break;
                        }
                        AdvanceCharacter();
                    }
                    if (!closed)
                    {
                        throw Failure(
                            ProtobufSourceFailureKind.SyntaxError,
                            "Unterminated protobuf block comment.",
                            line,
                            column);
                    }
                    continue;
                }
                return;
            }
        }

        private void AdvanceCharacter()
        {
            if (_index >= _source.Length) return;
            var current = _source[_index++];
            if (current == '\r')
            {
                if (_index < _source.Length && _source[_index] == '\n')
                {
                    _index++;
                }
                _line++;
                _column = 1;
            }
            else if (current == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
        }

        private Token TokenAt(
            TokenKind kind,
            string text,
            int line,
            int column) =>
            new(
                kind,
                text,
                line,
                column,
                _line,
                _column);

        private static bool IsIdentifierStart(char value) =>
            value == '_'
            || value is >= 'A' and <= 'Z'
            || value is >= 'a' and <= 'z';

        private static bool IsIdentifierPart(char value) =>
            IsIdentifierStart(value)
            || value is >= '0' and <= '9';
    }

    private enum TokenKind
    {
        End,
        Identifier,
        Integer,
        String,
        Symbol,
    }

    private readonly record struct Token(
        TokenKind Kind,
        string Text,
        int Line,
        int Column,
        int EndLine,
        int EndColumn);

    private sealed record ParsedDocument(
        string Package,
        int ImportCount,
        IReadOnlyList<ParsedDeclaration> Declarations);

    private abstract record ParsedDeclaration(
        Token Name,
        string FullName)
    {
        public Token End { get; set; } = Name;
    }

    private sealed record ParsedMessage(
        Token Name,
        Token InitialEnd,
        string FullName,
        string? ParentFullName,
        int NestingDepth)
        : ParsedDeclaration(Name, FullName);

    private sealed record ParsedField(
        Token Name,
        Token FieldEnd,
        string FullName,
        string MessageFullName,
        string Type,
        int Number,
        ProtoFieldCardinality Cardinality,
        string? OneofName)
        : ParsedDeclaration(Name, FullName);

    private sealed record ParsedRpc(
        Token Name,
        Token RpcEnd,
        string FullName,
        string ServiceFullName,
        string InputType,
        string OutputType,
        bool ClientStreaming,
        bool ServerStreaming)
        : ParsedDeclaration(Name, FullName);
}
