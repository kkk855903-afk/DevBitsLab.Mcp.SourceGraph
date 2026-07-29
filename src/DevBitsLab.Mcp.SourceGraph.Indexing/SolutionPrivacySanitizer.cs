using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Produces the Roslyn snapshot that is safe for indexing. Project evaluation has already
/// happened by the time this runs, so this is a data-flow boundary, not an MSBuild sandbox.
/// In particular, untrusted project files, analyzers, and source generators still require a
/// separately isolated process when they must not execute in the server process.
/// </summary>
internal static class SolutionPrivacySanitizer
{
    public static Solution Sanitize(Solution solution, PrivacyPathPolicy privacyPolicy)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(privacyPolicy);
        return Sanitize(solution, privacyPolicy.IsExcluded);
    }

    public static Solution SanitizeForScope(Solution solution, ScopePathPolicy pathPolicy)
        => SanitizeForScope(solution, pathPolicy, isBuildGenerated: null);

    internal static Solution SanitizeForScope(
        Solution solution,
        ScopePathPolicy pathPolicy,
        Func<Document, bool>? isBuildGenerated)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        return Sanitize(solution, pathPolicy.IsExcluded, isBuildGenerated);
    }

    private static Solution Sanitize(
        Solution solution,
        Func<string?, bool> isExcluded,
        Func<Document, bool>? isBuildGenerated = null)
    {
        var sanitized = solution;
        foreach (var project in solution.Projects)
        {
            if (isExcluded(project.FilePath))
            {
                sanitized = sanitized.RemoveProject(project.Id);
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (isExcluded(document.FilePath))
                {
                    if (isBuildGenerated?.Invoke(document) == true
                        && (TryExtractGlobalUsings(document)
                            || IsWpfMarkupGeneratedSource(document)))
                    {
                        // Keep the original Roslyn DocumentId so source-generator ownership
                        // remains stable across reloads. Regular-document discovery excludes
                        // its obj/ path, so it contributes only to compilation and is never
                        // persisted as user source.
                        continue;
                    }
                    sanitized = sanitized.RemoveDocument(document.Id);
                }
            }

            foreach (var document in project.AdditionalDocuments)
            {
                if (isExcluded(document.FilePath))
                {
                    sanitized = sanitized.RemoveAdditionalDocument(document.Id);
                }
            }

            foreach (var document in project.AnalyzerConfigDocuments)
            {
                if (isExcluded(document.FilePath))
                {
                    sanitized = sanitized.RemoveAnalyzerConfigDocument(document.Id);
                }
            }

        }

        return sanitized;
    }

    private static bool TryExtractGlobalUsings(Document document)
    {
        if (!document.Name.EndsWith(
                ".GlobalUsings.g.cs",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = document.GetTextAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        if (root.ContainsDiagnostics || root.Members.Count != 0)
        {
            return false;
        }

        return root.Usings.Count > 0
            && root.Usings.All(directive =>
                !directive.GlobalKeyword.IsKind(SyntaxKind.None));
    }

    private static bool IsWpfMarkupGeneratedSource(Document document)
    {
        var name = document.Name;
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    }
}
