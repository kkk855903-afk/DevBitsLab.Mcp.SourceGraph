using System.Text.RegularExpressions;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace SamplePlugin;

/// <summary>
/// Reference <see cref="ILanguageIndexer"/> for <c>.txt</c> files. Walks the file's first
/// identifier-looking token (letters, digits, underscores; must start with a letter) and emits
/// a single <see cref="IndexEvent.SymbolDeclared"/> + a <see cref="IndexEvent.FileScanned"/>.
/// Trivially demonstrates the contract round-trips: the test fixture creates a sentinel .txt
/// file, runs the dispatcher, and asserts the symbol shows up in the per-scope graph DB.
/// </summary>
public sealed class TextLanguageIndexer : LanguageIndexerBase
{
    private static readonly IReadOnlyCollection<string> _exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" };
    private static readonly Regex _identifier = new(@"\b([A-Za-z][A-Za-z0-9_]*)\b", RegexOptions.Compiled);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> FileExtensions => _exts;

    /// <inheritdoc />
    public override Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
    {
        var events = new List<IndexEvent>();
        var text = ctx.GetText();
        var match = _identifier.Match(text);
        if (match.Success)
        {
            var name = match.Groups[1].Value;
            // The canonical key needs to be globally stable. Path + first-identifier is good enough
            // for the fixture; a real plugin would derive a more robust key.
            events.Add(new IndexEvent.SymbolDeclared(
                CanonicalKey: $"txt:{ctx.FilePath}#{name}",
                Name: name,
                Fqn: name,
                Kind: PluginSymbolKind.Other,
                StartLine: 1,
                StartColumn: 1,
                EndLine: 1,
                EndColumn: name.Length + 1));
        }
        // SHA256.HashData is a net5+ API; netstandard2.0 doesn't have it, so use the instance form.
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            events.Add(new IndexEvent.FileScanned(ctx.FilePath, sha.ComputeHash(ctx.Contents)));
        }
        return Task.FromResult<IReadOnlyList<IndexEvent>>(events);
    }
}
