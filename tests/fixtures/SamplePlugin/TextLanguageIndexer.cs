using System.Text.RegularExpressions;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace SamplePlugin;

/// <summary>
/// Reference <see cref="ILanguageIndexer"/> for <c>.txt</c> files. Walks the file's first
/// identifier-looking token (letters, digits, underscores; must start with a letter) and emits
/// a single <see cref="IndexEvent.SymbolDeclared"/> + a <see cref="IndexEvent.FileScanned"/>.
/// Trivially demonstrates the contract round-trips: the test fixture creates a sentinel .txt
/// file, runs the dispatcher, and asserts the symbol shows up in the per-scope graph DB.
///
/// <para>NOTE on canonical-key scheme: the SDK validator (open-language-contract) restricts
/// emissions to the reserved-and-enforced scheme set (<c>csharp</c>, <c>xaml</c>) at v1.
/// Until a sample / test scheme is reserved, this fixture borrows the <c>csharp:</c> scheme
/// for its keys even though the file's extension is <c>.txt</c>. The host doesn't bind
/// scheme to extension — only the scheme is validated — so this works at the contract level.
/// A future SDK release that reserves a <c>sample:</c> or <c>plugin:</c> scheme should swap
/// these literals.</para>
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
            // The canonical key needs to be globally stable. We borrow the `csharp:` scheme
            // (see class summary) and form a key from a path-derived identifier slug + name.
            // The validator forbids backslashes in keys; replace any so cross-platform paths
            // round-trip. The fixture indexer doesn't need a full Roslyn DocumentationCommentId.
            var slug = ctx.FilePath.Replace('\\', '/');
            events.Add(new IndexEvent.SymbolDeclared(
                canonicalKey: $"csharp:T:Sample.Text.{name}@{slug}",
                name: name,
                fqn: $"Sample.Text.{name}",
                kind: SymbolKinds.Other,
                startLine: 1,
                startColumn: 1,
                endLine: 1,
                endColumn: name.Length + 1));
        }
        // SHA256.HashData is a net5+ API; netstandard2.0 doesn't have it, so use the instance form.
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            events.Add(new IndexEvent.FileScanned(ctx.FilePath, sha.ComputeHash(ctx.Contents)));
        }
        return Task.FromResult<IReadOnlyList<IndexEvent>>(events);
    }
}
