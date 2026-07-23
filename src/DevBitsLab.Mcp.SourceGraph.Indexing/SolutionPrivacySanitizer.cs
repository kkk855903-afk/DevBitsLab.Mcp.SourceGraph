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
    public static Solution Sanitize(Solution solution, PrivacyPathPolicy privacyPolicy)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(privacyPolicy);

        var sanitized = solution;
        foreach (var project in solution.Projects)
        {
            if (privacyPolicy.IsExcluded(project.FilePath))
            {
                sanitized = sanitized.RemoveProject(project.Id);
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (privacyPolicy.IsExcluded(document.FilePath))
                {
                    sanitized = sanitized.RemoveDocument(document.Id);
                }
            }

            foreach (var document in project.AdditionalDocuments)
            {
                if (privacyPolicy.IsExcluded(document.FilePath))
                {
                    sanitized = sanitized.RemoveAdditionalDocument(document.Id);
                }
            }

            foreach (var document in project.AnalyzerConfigDocuments)
            {
                if (privacyPolicy.IsExcluded(document.FilePath))
                {
                    sanitized = sanitized.RemoveAnalyzerConfigDocument(document.Id);
                }
            }
        }

        return sanitized;
    }
}
