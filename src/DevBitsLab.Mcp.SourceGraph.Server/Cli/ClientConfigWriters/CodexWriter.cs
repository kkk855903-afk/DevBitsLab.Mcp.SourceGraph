using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Writer for Codex's project-scoped <c>.codex/config.toml</c>. Codex uses a TOML table named
/// <c>[mcp_servers.sourcegraph]</c>.
///
/// Existing TOML is parsed strictly and edited through lossless syntax-tree source spans. Only
/// the values owned by this writer (<c>command</c>, <c>args</c>, and <c>cwd</c>) are replaced;
/// unrelated settings, comments, extra sourcegraph options, and other servers remain untouched.
/// </summary>
internal sealed class CodexWriter : IClientConfigWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public ClientId ClientId => ClientId.Codex;

    public string? DefaultProjectPath(string root) =>
        Path.Join(root, ".codex", "config.toml");

    // Project config is the safe, portable onboarding target. Editing the shared user-level
    // ~/.codex/config.toml is intentionally outside this writer's scope.
    public string? DefaultUserPath() => null;

    public WriterPlan Plan(WriterContext ctx)
    {
        var (command, rawArgs) = WriterCommandLine.Build(ctx);
        var cwd = Path.GetFullPath(ctx.Root);
        var args = MakeAbsoluteArguments(rawArgs, cwd).ToList();
        if (!args.Contains("--root", StringComparer.Ordinal))
        {
            args.Add("--root");
            args.Add(cwd);
        }
        args.Add("--codex-compat");
        var ownedArgs = args.ToArray();

        if (!IsWellFormedUnicode(command)
            || ownedArgs.Any(arg => !IsWellFormedUnicode(arg)))
        {
            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.SkipExistingDiffers,
                ctx.ExistingContent ?? Array.Empty<byte>(),
                "generated Codex command contains an invalid Unicode scalar; refusing to write it");
        }

        if (ctx.ExistingContent is null or { Length: 0 })
        {
            var fresh = BuildTable(command, ownedArgs, cwd, "\n");
            if (!CandidateIsValid(fresh, ctx.TargetPath, command, ownedArgs, cwd))
            {
                return UnsafePlan(
                    ctx,
                    fresh,
                    "generated Codex config did not pass strict TOML validation; refusing to create the file");
            }

            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.Insert,
                StrictUtf8.GetBytes(fresh),
                "would create Codex config with sourcegraph MCP server");
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(ctx.ExistingContent);
        }
        catch (DecoderFallbackException)
        {
            return UnsafePlan(
                ctx,
                BuildTable(command, args, cwd, "\n"),
                "existing Codex config is not valid UTF-8; refusing to modify it");
        }

        // Tomlyn accepts TOML text without a BOM. Keep the BOM separately so source spans stay
        // aligned with the parsed string and the exact encoding marker can be restored afterward.
        var hasBom = decoded.StartsWith('\uFEFF');
        var existing = hasBom ? decoded[1..] : decoded;
        var newline = DetectNewline(existing);
        var standalone = BuildTable(command, ownedArgs, cwd, newline);

        if (!TryParse(existing, ctx.TargetPath, out var document)
            || !TryReadModel(existing, out var model))
        {
            return UnsafePlan(
                ctx,
                standalone,
                "existing Codex config is not valid TOML; refusing to modify it");
        }

        var hasSemanticTarget = TryGetSourcegraphTable(model, out var targetModel);
        var targetSyntaxNodes = document.Tables
            .OfType<TableSyntaxBase>()
            .Where(table => IsExactTargetPath(KeyParts(table.Name)))
            .ToArray();

        if (!hasSemanticTarget)
        {
            if ((model.TryGetValue("mcp_servers", out var serversValue)
                    && serversValue is not TomlTable)
                || HasInlineMcpServersDefinition(document))
            {
                return UnsafePlan(
                    ctx,
                    standalone,
                    "existing Codex config has an incompatible mcp_servers structure; refusing to append");
            }

            var candidate = AppendTable(existing, standalone, newline);
            if (!CandidateIsValid(candidate, ctx.TargetPath, command, ownedArgs, cwd))
            {
                return UnsafePlan(
                    ctx,
                    standalone,
                    "existing Codex config has an incompatible mcp_servers structure; refusing to append");
            }

            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.Insert,
                Encode(candidate, hasBom),
                "would append sourcegraph to existing Codex config (preserving all other TOML)");
        }

        // A semantic sourcegraph value without one ordinary table header means it was expressed
        // as an inline/dotted/implicit table or table array. Those shapes are valid TOML but do
        // not provide a single safe body in which to patch our owned fields.
        if (targetModel is null
            || targetSyntaxNodes.Length != 1
            || targetSyntaxNodes[0] is not TableSyntax targetSyntax)
        {
            return UnsafePlan(
                ctx,
                standalone,
                "existing Codex sourcegraph entry uses an unsupported inline, dotted, implicit, or array-table shape; edit it manually");
        }

        var patches = new List<TextPatch>();
        var missingLines = new List<string>();
        var differs = false;

        if (!PlanOwnedField(
                targetModel,
                targetSyntax,
                "command",
                command,
                BasicString(command),
                patches,
                missingLines,
                ref differs)
            || !PlanOwnedField(
                targetModel,
                targetSyntax,
                "args",
                ownedArgs,
                ArrayValue(ownedArgs),
                patches,
                missingLines,
                ref differs)
            || !PlanOwnedField(
                targetModel,
                targetSyntax,
                "cwd",
                cwd,
                BasicString(cwd),
                patches,
                missingLines,
                ref differs))
        {
            return UnsafePlan(
                ctx,
                standalone,
                "existing Codex sourcegraph fields use a structure that cannot be patched safely");
        }

        if (!differs)
        {
            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.NoOpAlreadyMatches,
                ctx.ExistingContent,
                "already wired (no change)");
        }

        if (missingLines.Count > 0)
        {
            var insertionOffset = InsertionOffset(targetSyntax, existing);
            var needsLeadingNewline = insertionOffset == 0
                || existing[insertionOffset - 1] is not ('\r' or '\n');
            var insertion = (needsLeadingNewline ? newline : string.Empty)
                + string.Join(newline, missingLines)
                + newline;
            patches.Add(new TextPatch(insertionOffset, 0, insertion));
        }

        var updated = ApplyPatches(existing, patches);
        if (!CandidateIsValid(updated, ctx.TargetPath, command, ownedArgs, cwd))
        {
            return UnsafePlan(
                ctx,
                standalone,
                "generated Codex config did not pass strict TOML validation; refusing to modify the file");
        }

        return new WriterPlan(
            ctx.TargetPath,
            ctx.Force ? WriterAction.ReplaceOurs : WriterAction.SkipExistingDiffers,
            Encode(updated, hasBom),
            ctx.Force
                ? "would update only command/args/cwd in the existing Codex sourcegraph table"
                : "existing Codex sourcegraph command/args/cwd differ (use --force to update only those fields)");
    }

    public void Apply(WriterPlan plan)
    {
        if (plan.Action is not (WriterAction.Insert or WriterAction.ReplaceOurs)) return;
        var dir = Path.GetDirectoryName(plan.TargetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(plan.TargetPath, plan.ContentBytes);
    }

    /// <summary>
    /// Presence-only detector used by onboarding diagnostics. It deliberately does not claim the
    /// entry matches today's generated command line; the writer performs that stronger check.
    /// </summary>
    internal static bool ContainsSourcegraphEntry(string text)
    {
        if (text.StartsWith('\uFEFF')) text = text[1..];
        return TryParse(text, "config.toml", out _)
            && TryReadModel(text, out var model)
            && TryGetSourcegraphTable(model, out _);
    }

    internal static bool ContainsSourcegraphEntry(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return ContainsSourcegraphEntry(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool PlanOwnedField(
        TomlTable targetModel,
        TableSyntax targetSyntax,
        string key,
        object expected,
        string replacementValue,
        ICollection<TextPatch> patches,
        ICollection<string> missingLines,
        ref bool differs)
    {
        var hasValue = targetModel.TryGetValue(key, out var actual);
        if (hasValue && SemanticEquals(actual, expected)) return true;

        differs = true;
        var syntax = targetSyntax.Items
            .OfType<KeyValueSyntax>()
            .Where(item => item.Value is not null && IsSingleKey(KeyParts(item.Key), key))
            .ToArray();
        if (syntax.Length > 1) return false;

        if (syntax.Length == 1)
        {
            // Replacing an entire TOML value would also replace trivia nested inside it. Arrays
            // can legally carry comments between elements, so decline the update rather than
            // silently discarding user-authored text.
            var valueSyntax = syntax[0].Value!;
            var span = valueSyntax.Span;
            if (valueSyntax.Descendants(includeTokensCommentsAndWhitespaces: true)
                .Any(node =>
                    node is SyntaxTrivia { Kind: TokenKind.Comment } trivia
                    && trivia.Span.Offset >= span.Offset
                    && trivia.Span.Offset < span.Offset + span.Length))
            {
                return false;
            }

            if (span.Offset < 0 || span.Length < 0) return false;
            patches.Add(new TextPatch(span.Offset, span.Length, replacementValue));
            return true;
        }

        // A semantic value with no direct key/value node is dotted or otherwise implicit.
        if (hasValue) return false;
        missingLines.Add($"{key} = {replacementValue}");
        return true;
    }

    private static bool SemanticEquals(object? actual, object expected)
    {
        if (expected is string expectedString)
        {
            return actual is string actualString
                && string.Equals(actualString, expectedString, StringComparison.Ordinal);
        }

        if (expected is string[] expectedArray)
        {
            if (actual is not TomlArray actualArray || actualArray.Count != expectedArray.Length)
            {
                return false;
            }
            for (var i = 0; i < expectedArray.Length; i++)
            {
                if (actualArray[i] is not string item
                    || !string.Equals(item, expectedArray[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        return false;
    }

    private static bool CandidateIsValid(
        string candidate,
        string sourcePath,
        string command,
        string[] args,
        string cwd)
    {
        return TryParse(candidate, sourcePath, out _)
            && TryReadModel(candidate, out var model)
            && TryGetSourcegraphTable(model, out var target)
            && target is not null
            && target.TryGetValue("command", out var actualCommand)
            && SemanticEquals(actualCommand, command)
            && target.TryGetValue("args", out var actualArgs)
            && SemanticEquals(actualArgs, args)
            && target.TryGetValue("cwd", out var actualCwd)
            && SemanticEquals(actualCwd, cwd);
    }

    private static bool TryParse(string text, string sourcePath, out DocumentSyntax document)
    {
        try
        {
            document = SyntaxParser.ParseStrict(text, sourcePath, false);
            return !document.HasErrors;
        }
        catch (TomlException)
        {
            document = new DocumentSyntax();
            return false;
        }
    }

    private static bool TryReadModel(string text, out TomlTable model)
    {
        try
        {
            return TomlSerializer.TryDeserialize(text, out model!)
                && model is not null;
        }
        catch (TomlException)
        {
            model = new TomlTable();
            return false;
        }
    }

    private static bool TryGetSourcegraphTable(TomlTable model, out TomlTable? sourcegraph)
    {
        sourcegraph = null;
        if (!model.TryGetValue("mcp_servers", out var serversValue)
            || serversValue is not TomlTable servers
            || !servers.TryGetValue("sourcegraph", out var sourcegraphValue)
            || sourcegraphValue is not TomlTable sourcegraphTable)
        {
            return false;
        }
        sourcegraph = sourcegraphTable;
        return true;
    }

    private static string[] KeyParts(KeySyntax? key)
    {
        if (key?.Key is null) return Array.Empty<string>();
        var parts = new List<string> { KeyValue(key.Key) };
        if (key.DotKeys is not null)
        {
            parts.AddRange(key.DotKeys
                .OfType<DottedKeyItemSyntax>()
                .Select(item => KeyValue(item.Key)));
        }
        return parts.ToArray();
    }

    private static string KeyValue(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? string.Empty,
        StringValueSyntax quoted => quoted.Value ?? string.Empty,
        null => string.Empty,
        _ => key.ToString() ?? string.Empty,
    };

    private static bool IsExactTargetPath(IReadOnlyList<string> parts) =>
        parts.Count == 2
        && string.Equals(parts[0], "mcp_servers", StringComparison.Ordinal)
        && string.Equals(parts[1], "sourcegraph", StringComparison.Ordinal);

    private static bool IsSingleKey(IReadOnlyList<string> parts, string key) =>
        parts.Count == 1 && string.Equals(parts[0], key, StringComparison.Ordinal);

    private static bool HasInlineMcpServersDefinition(DocumentSyntax document) =>
        document.KeyValues
            .OfType<KeyValueSyntax>()
            .Any(item =>
                item.Value is InlineTableSyntax
                && IsSingleKey(KeyParts(item.Key), "mcp_servers"));

    private static int InsertionOffset(TableSyntax table, string source)
    {
        var items = table.Items?.OfType<KeyValueSyntax>().ToArray()
            ?? Array.Empty<KeyValueSyntax>();
        int anchorOffset;
        if (items.Length > 0)
        {
            var last = items[^1].Span;
            anchorOffset = last.Offset + last.Length;
        }
        else if (table.CloseBracket is { } closeBracket)
        {
            var close = closeBracket.Span;
            anchorOffset = close.Offset + close.Length;
        }
        else
        {
            anchorOffset = table.Span.Offset + table.Span.Length;
        }

        anchorOffset = Math.Clamp(anchorOffset, 0, source.Length);
        if (anchorOffset > 0 && source[anchorOffset - 1] is '\r' or '\n')
        {
            return anchorOffset;
        }

        // Keep an inline comment attached to the table header or final key/value line. Insert
        // after that complete physical line, but before any following table.
        while (anchorOffset < source.Length
            && source[anchorOffset] is not ('\r' or '\n'))
        {
            anchorOffset++;
        }
        if (anchorOffset < source.Length && source[anchorOffset] == '\r')
        {
            anchorOffset++;
            if (anchorOffset < source.Length && source[anchorOffset] == '\n')
            {
                anchorOffset++;
            }
        }
        else if (anchorOffset < source.Length && source[anchorOffset] == '\n')
        {
            anchorOffset++;
        }
        return anchorOffset;
    }

    private static string ApplyPatches(string text, IEnumerable<TextPatch> patches)
    {
        var sb = new StringBuilder(text);
        foreach (var patch in patches.OrderByDescending(patch => patch.Offset))
        {
            if (patch.Offset < 0
                || patch.Length < 0
                || patch.Offset + patch.Length > sb.Length)
            {
                throw new InvalidOperationException("TOML syntax span is outside the source text.");
            }
            sb.Remove(patch.Offset, patch.Length);
            sb.Insert(patch.Offset, patch.Replacement);
        }
        return sb.ToString();
    }

    private static WriterPlan UnsafePlan(WriterContext ctx, string snippet, string description) =>
        new(
            ctx.TargetPath,
            WriterAction.SkipExistingDiffers,
            StrictUtf8.GetBytes(snippet),
            description);

    private static byte[] Encode(string text, bool withBom) =>
        StrictUtf8.GetBytes(withBom ? "\uFEFF" + text : text);

    private static string[] MakeAbsoluteArguments(IReadOnlyList<string> values, string root)
    {
        var result = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var isPathValue = i > 0
                && values[i - 1] is "--solution" or "--root" or "--project";
            result[i] = isPathValue
                ? MakeAbsolutePathArgument(values[i], root)
                : values[i];
        }
        return result;
    }

    private static string MakeAbsolutePathArgument(string value, string root)
    {
        const string token = "${workspaceFolder}";
        if (string.Equals(value, token, StringComparison.Ordinal)) return root;
        if (value.StartsWith(token + "/", StringComparison.Ordinal)
            || value.StartsWith(token + "\\", StringComparison.Ordinal))
        {
            return Path.GetFullPath(Path.Join(root, value[(token.Length + 1)..]));
        }

        return Path.GetFullPath(
            Path.IsPathFullyQualified(value) ? value : Path.Join(root, value));
    }

    private static string BuildTable(
        string command,
        IReadOnlyList<string> args,
        string cwd,
        string newline)
    {
        var sb = new StringBuilder();
        sb.Append("[mcp_servers.sourcegraph]").Append(newline);
        sb.Append("command = ").Append(BasicString(command)).Append(newline);
        sb.Append("args = ").Append(ArrayValue(args)).Append(newline);
        sb.Append("cwd = ").Append(BasicString(cwd)).Append(newline);
        return sb.ToString();
    }

    private static string ArrayValue(IReadOnlyList<string> values)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(BasicString(values[i]));
        }
        return sb.Append(']').ToString();
    }

    private static string BasicString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < ' ' || c == '\u007f')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static bool IsWellFormedUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static string DetectNewline(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
        : text.Contains('\n', StringComparison.Ordinal) ? "\n"
        : text.Contains('\r', StringComparison.Ordinal) ? "\r"
        : "\n";

    private static string AppendTable(string existing, string table, string newline)
    {
        if (existing.Length == 0) return table;
        if (!EndsWithLineBreak(existing)) return existing + newline + newline + table;
        if (EndsWithBlankLine(existing)) return existing + table;
        return existing + newline + table;
    }

    private static bool EndsWithLineBreak(string value) =>
        value.EndsWith('\n') || value.EndsWith('\r');

    private static bool EndsWithBlankLine(string value) =>
        value.EndsWith("\n\n", StringComparison.Ordinal)
        || value.EndsWith("\r\n\r\n", StringComparison.Ordinal)
        || value.EndsWith("\r\r", StringComparison.Ordinal);

    private sealed record TextPatch(int Offset, int Length, string Replacement);
}
