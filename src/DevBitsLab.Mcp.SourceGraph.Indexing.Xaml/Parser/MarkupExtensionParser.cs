using System;
using System.Collections.Generic;
using System.Text;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

/// <summary>
/// Tokenises XAML markup-extension syntax (<c>{Binding Path=Foo, Mode=TwoWay, Converter={StaticResource b2v}}</c>)
/// into a <see cref="MarkupExtensionValue"/> tree. Handles nested extensions, escaped braces, and
/// quoted strings; refuses any input that doesn't open with <c>{</c> and close with <c>}</c>. Does
/// NOT depend on <c>System.Xaml</c> or any vendor-specific markup compiler — every dialect we
/// support uses the same overall syntactic shape, with profile-specific differences (e.g.
/// <c>x:Bind</c>) deferred to the strategies in <c>Dialects/</c>.
/// </summary>
public static class MarkupExtensionParser
{
    /// <summary>
    /// Parse <paramref name="text"/> as a markup-extension expression. Returns the parsed tree.
    /// Throws <see cref="FormatException"/> when the input doesn't conform to the syntax.
    /// </summary>
    public static MarkupExtensionValue Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
        {
            throw new FormatException($"Markup extension '{text}' must be enclosed in braces.");
        }
        var pos = 0;
        var value = ParseExtension(trimmed, ref pos);
        SkipWhitespace(trimmed, ref pos);
        if (pos != trimmed.Length)
        {
            throw new FormatException($"Trailing content after markup extension at offset {pos}: '{trimmed.Substring(pos)}'.");
        }
        return value;
    }

    /// <summary>
    /// Try-form of <see cref="Parse"/>. Returns <c>false</c> on a malformed input rather than
    /// throwing — used by the indexer's hot path so a single bad attribute doesn't abort indexing.
    /// </summary>
    public static bool TryParse(string text, out MarkupExtensionValue value)
    {
        try
        {
            value = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            value = MarkupExtensionValue.ForLiteral(text);
            return false;
        }
    }

    private static MarkupExtensionValue ParseExtension(string text, ref int pos)
    {
        Expect(text, ref pos, '{');
        SkipWhitespace(text, ref pos);
        var name = ReadIdentifier(text, ref pos);
        if (string.IsNullOrEmpty(name))
        {
            throw new FormatException($"Expected extension name at offset {pos}.");
        }
        SkipWhitespace(text, ref pos);

        var positional = new List<MarkupExtensionValue>();
        var named = new Dictionary<string, MarkupExtensionValue>(StringComparer.Ordinal);

        // Empty body — `{StaticResource}` is malformed, but `{Binding}` (no args) is valid.
        if (Peek(text, pos) == '}')
        {
            pos++;
            return MarkupExtensionValue.ForExtension(name, positional, named);
        }

        while (true)
        {
            SkipWhitespace(text, ref pos);
            // Snapshot the rewind point so we can fall back to "this argument was positional"
            // when no '=' follows the identifier.
            var savedPos = pos;
            var ident = TryReadIdentifierForNamedArg(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (ident is not null && Peek(text, pos) == '=')
            {
                pos++;
                SkipWhitespace(text, ref pos);
                var value = ParseValue(text, ref pos);
                named[ident] = value;
            }
            else
            {
                // Not a named arg — rewind and parse the whole thing as a positional value.
                pos = savedPos;
                var value = ParseValue(text, ref pos);
                positional.Add(value);
            }

            SkipWhitespace(text, ref pos);
            var next = Peek(text, pos);
            if (next == ',')
            {
                pos++;
                continue;
            }
            if (next == '}')
            {
                pos++;
                return MarkupExtensionValue.ForExtension(name, positional, named);
            }
            throw new FormatException($"Expected ',' or '}}' at offset {pos}, got '{next}'.");
        }
    }

    private static MarkupExtensionValue ParseValue(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        var c = Peek(text, pos);
        if (c == '{')
        {
            return ParseExtension(text, ref pos);
        }
        if (c == '\'' || c == '"')
        {
            return MarkupExtensionValue.ForLiteral(ReadQuoted(text, ref pos, c));
        }
        return MarkupExtensionValue.ForLiteral(ReadBareValue(text, ref pos));
    }

    /// <summary>
    /// Read a quoted string. Consumes both the opening and closing quote chars. Any backslash
    /// is treated as an escape: the next character is appended verbatim to the value (so
    /// <c>'it\'s ok'</c> yields "it's ok", and <c>'a\\b'</c> yields "a\\b" — only the trailing
    /// backslash is consumed as an escape token, not the leading one). This is broader than a
    /// strict quote-only escape but simpler to reason about and matches what
    /// <see cref="ReadBareValue"/> does for unquoted braces; the markup-extension grammar in
    /// practice only escapes quotes and braces so the difference is invisible to real input.
    /// </summary>
    private static string ReadQuoted(string text, ref int pos, char quote)
    {
        pos++; // consume opening quote
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '\\' && pos + 1 < text.Length)
            {
                sb.Append(text[pos + 1]);
                pos += 2;
                continue;
            }
            if (c == quote)
            {
                pos++;
                return sb.ToString();
            }
            sb.Append(c);
            pos++;
        }
        throw new FormatException($"Unterminated quoted string starting near offset {pos}.");
    }

    /// <summary>
    /// Read an unquoted value up to the next ',', '}', or end-of-input. Backslash-escaped braces
    /// (<c>\{</c>, <c>\}</c>) are unescaped into the value rather than terminating the read.
    /// Trailing whitespace is trimmed.
    /// </summary>
    private static string ReadBareValue(string text, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '\\' && pos + 1 < text.Length)
            {
                sb.Append(text[pos + 1]);
                pos += 2;
                continue;
            }
            if (c == ',' || c == '}')
            {
                break;
            }
            sb.Append(c);
            pos++;
        }
        // Trim trailing whitespace; bare values are typically `Binding Path=Foo` style and the
        // tokenizer expects the value not to drag spaces along.
        var s = sb.ToString();
        var end = s.Length;
        while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
        return s.Substring(0, end);
    }

    private static string ReadIdentifier(string text, ref int pos)
    {
        var start = pos;
        while (pos < text.Length)
        {
            var c = text[pos];
            // XAML extension identifiers permit ASCII letters, digits, '.', ':', '_', '-' so a
            // qualified extension like `x:Bind` parses cleanly without needing a separate prefix
            // token.
            if (c == '.' || c == ':' || c == '_' || c == '-' || char.IsLetterOrDigit(c))
            {
                pos++;
                continue;
            }
            break;
        }
        return text.Substring(start, pos - start);
    }

    /// <summary>
    /// Speculative identifier read for a named argument: same shape as <see cref="ReadIdentifier"/>
    /// but stops at characters that wouldn't make a valid named-arg key. Returns <c>null</c> when
    /// no identifier was readable, so the caller can rewind and reparse the whole token as a
    /// positional value.
    /// </summary>
    private static string? TryReadIdentifierForNamedArg(string text, ref int pos)
    {
        var start = pos;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '.' || c == '_' || char.IsLetterOrDigit(c))
            {
                pos++;
                continue;
            }
            break;
        }
        if (pos == start) return null;
        return text.Substring(start, pos - start);
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
    }

    private static char Peek(string text, int pos) => pos < text.Length ? text[pos] : '\0';

    private static void Expect(string text, ref int pos, char ch)
    {
        if (pos >= text.Length || text[pos] != ch)
        {
            throw new FormatException($"Expected '{ch}' at offset {pos}.");
        }
        pos++;
    }
}
