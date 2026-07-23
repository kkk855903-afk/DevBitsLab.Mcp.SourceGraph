using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Resolves the single configured scope that owns a one-shot <c>index</c> solution.
/// Ambiguous or mismatched configurations fail closed instead of silently indexing with an
/// unrelated scope's exclusions.
/// </summary>
internal static class OneShotScopeResolver
{
    public static OneShotScopeSelection Resolve(
        ScopeConfig config,
        string solutionPath,
        string? requestedScopeId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);

        var fullSolutionPath = Path.GetFullPath(solutionPath);
        Scope scope;
        if (requestedScopeId is not null)
        {
            if (string.IsNullOrWhiteSpace(requestedScopeId))
            {
                throw new ArgumentException(
                    "The --scope value must be a non-empty configured scope id.",
                    nameof(requestedScopeId));
            }

            scope = config.Scopes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, requestedScopeId, StringComparison.Ordinal))
                ?? throw new ArgumentException(
                    $"Scope `{requestedScopeId}` was not found. Choose an id declared in .sourcegraph.json.",
                    nameof(requestedScopeId));

            if (!IncludesSolution(scope, fullSolutionPath))
            {
                throw new ArgumentException(
                    $"Scope `{scope.Id}` does not include the requested solution. " +
                    "Choose a scope whose `solutions` list contains it.",
                    nameof(requestedScopeId));
            }
        }
        else
        {
            var matches = config.Scopes
                .Where(candidate => IncludesSolution(candidate, fullSolutionPath))
                .ToList();
            if (matches.Count == 0)
            {
                throw new ArgumentException(
                    "No configured scope includes the requested solution. " +
                    "Add it to a scope's `solutions` list or pass --scope <id> after correcting the scope.",
                    nameof(solutionPath));
            }
            if (matches.Count > 1)
            {
                var ids = string.Join(", ", matches.Select(match => match.Id).OrderBy(id => id, StringComparer.Ordinal));
                throw new ArgumentException(
                    $"The requested solution matches multiple scopes ({ids}). Pass --scope <id> explicitly.",
                    nameof(solutionPath));
            }
            scope = matches[0];
        }

        var pathPolicy = new ScopePathPolicy(scope.Root, scope.ProjectSet.Exclude);
        if (pathPolicy.IsExcluded(fullSolutionPath))
        {
            throw new ArgumentException(
                $"Scope `{scope.Id}` excludes the requested solution through its privacy or exclude policy.",
                nameof(solutionPath));
        }

        return new OneShotScopeSelection(scope, pathPolicy);
    }

    private static bool IncludesSolution(Scope scope, string fullSolutionPath)
    {
        if (scope.ProjectSet is not ScopeProjectSet.Solutions solutions) return false;

        foreach (var configuredPath in solutions.Items)
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(
                    Path.IsPathRooted(configuredPath)
                        ? configuredPath
                        : Path.Join(scope.Root, configuredPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException(
                    $"Scope `{scope.Id}` contains an invalid solution path.",
                    nameof(scope),
                    ex);
            }

            if (string.Equals(candidate, fullSolutionPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

internal sealed record OneShotScopeSelection(Scope Scope, ScopePathPolicy PathPolicy);
