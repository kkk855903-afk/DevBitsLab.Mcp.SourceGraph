using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Produces the Roslyn snapshot that is safe for indexing. Project evaluation has already
/// happened by the time this runs, so this is a data-flow boundary, not an MSBuild sandbox.
/// In particular, untrusted project files, analyzers, and source generators still require a
/// separately isolated process when they must not execute in the server process.
/// </summary>
internal static class SolutionPrivacySanitizer
{
    private static readonly System.Reflection.PropertyInfo? _documentStateProperty =
        typeof(Document).GetProperty(
            "DocumentState",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.PropertyInfo?
        _documentStateIsGeneratedProperty =
            _documentStateProperty?.PropertyType.GetProperty(
                "IsGenerated",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);

    public static Solution Sanitize(Solution solution, PrivacyPathPolicy privacyPolicy)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(privacyPolicy);
        return Sanitize(solution, privacyPolicy.IsExcluded);
    }

    public static Solution SanitizeForScope(Solution solution, ScopePathPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        return Sanitize(
            solution,
            pathPolicy.IsExcluded,
            document => IsBuildGeneratedDocument(document)
                ? pathPolicy.IsGeneratedDocumentExcluded(document.FilePath)
                : pathPolicy.IsExcluded(document.FilePath));
    }

    private static Solution Sanitize(
        Solution solution,
        Func<string?, bool> isExcluded,
        Func<Document, bool>? isDocumentExcluded = null)
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
                if ((isDocumentExcluded ?? (candidate => isExcluded(candidate.FilePath)))(
                        document))
                {
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

    /// <summary>
    /// Roslyn marks SDK and build-target documents (for example GlobalUsings.g.cs and WPF
    /// connector sources) as generated. They must remain in the semantic snapshot so the
    /// compilation matches <c>dotnet build</c>, while the indexing pass still filters their
    /// obj/bin paths from ordinary source results.
    /// </summary>
    internal static bool IsBuildGeneratedDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            var state = _documentStateProperty?.GetValue(document);
            return state is not null
                   && _documentStateIsGeneratedProperty?.GetValue(state) is true;
        }
        catch
        {
            // Roslyn does not expose this provenance publicly. A version mismatch must fail
            // closed rather than admitting an arbitrary obj/bin document into compilation.
            return false;
        }
    }
}
