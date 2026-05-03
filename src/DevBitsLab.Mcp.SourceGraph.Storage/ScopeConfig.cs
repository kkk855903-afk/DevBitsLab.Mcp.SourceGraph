using System.Text.Json;
using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// In-memory shape of <c>.sourcegraph.json</c>. Loaded by <see cref="ScopeConfigLoader"/>; consumed
/// by the indexer wiring and the scope CLI subcommands. Non-defaulted fields come straight from
/// the JSON; <see cref="DefaultScope"/> is optional and falls back to "single registered scope" at
/// resolution time.
/// </summary>
public sealed record ScopeConfig(
    IReadOnlyList<Scope> Scopes,
    string? DefaultScope);

/// <summary>
/// Reads, validates, and synthesises <c>.sourcegraph.json</c>. The single-solution path stays
/// zero-config: when the file is absent, the loader returns a synthesised <c>default</c> scope so
/// existing users keep working unchanged.
/// </summary>
public static class ScopeConfigLoader
{
    public const string FileName = ".sourcegraph.json";

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the config for <paramref name="repoRoot"/>. When <c>.sourcegraph.json</c> is absent,
    /// synthesises a single <c>default</c> scope rooted at <paramref name="repoRoot"/>; the
    /// solutions list is populated from <paramref name="discoveredSolutions"/> (typically the
    /// CLI's <c>--solution</c> argument or the auto-discovered .slnx siblings).
    ///
    /// Throws <see cref="ScopeConfigException"/> with a precise message when the file exists but
    /// is malformed; the server treats that as a fatal startup error (exit code 2).
    /// </summary>
    public static ScopeConfig Load(string repoRoot, IReadOnlyList<string>? discoveredSolutions = null)
    {
        var path = Path.Combine(repoRoot, FileName);
        if (!File.Exists(path))
        {
            return Synthesise(repoRoot, discoveredSolutions ?? Array.Empty<string>());
        }
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new ScopeConfigException($"Failed to read {FileName}: {ex.Message}", ex);
        }

        ScopeConfigJson dto;
        try
        {
            dto = JsonSerializer.Deserialize<ScopeConfigJson>(raw, _readOptions)
                ?? throw new ScopeConfigException($"{FileName} is empty or null");
        }
        catch (JsonException ex)
        {
            throw new ScopeConfigException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }

        if (dto.Scopes is null || dto.Scopes.Count == 0)
        {
            throw new ScopeConfigException($"{FileName} has no `scopes` array.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new List<Scope>(dto.Scopes.Count);
        for (var i = 0; i < dto.Scopes.Count; i++)
        {
            var entry = dto.Scopes[i];
            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new ScopeConfigException($"{FileName} scope #{i} is missing the required `name` field.");
            }
            if (!ScopeIdValidator.IsValid(entry.Name))
            {
                throw new ScopeConfigException(
                    $"{FileName} scope `{entry.Name}` has an invalid id. Must match ^[a-z0-9][a-z0-9-]{{0,63}}$ (kebab-case slug, 1-64 chars).");
            }
            if (!seen.Add(entry.Name))
            {
                throw new ScopeConfigException($"{FileName} declares scope `{entry.Name}` more than once.");
            }
            var hasSolutions = entry.Solutions is { Count: > 0 };
            var hasProjects = entry.Projects is { Count: > 0 };
            var hasPaths = entry.Paths is { Count: > 0 } || entry.Include is { Count: > 0 };
            if (!hasSolutions && !hasProjects && !hasPaths)
            {
                throw new ScopeConfigException(
                    $"{FileName} scope `{entry.Name}` must declare exactly one of `solutions`, `projects`, or `paths`/`include`.");
            }
            var declared = (hasSolutions ? 1 : 0) + (hasProjects ? 1 : 0) + (hasPaths ? 1 : 0);
            if (declared > 1)
            {
                throw new ScopeConfigException(
                    $"{FileName} scope `{entry.Name}` declares more than one project-set kind. Pick exactly one of `solutions`, `projects`, or `paths`/`include`.");
            }
            ScopeProjectSet projectSet = hasSolutions
                ? new ScopeProjectSet.Solutions(entry.Solutions!, entry.Exclude ?? Array.Empty<string>())
                : hasProjects
                    ? new ScopeProjectSet.Projects(entry.Projects!, entry.Exclude ?? Array.Empty<string>())
                    : new ScopeProjectSet.Paths(entry.Paths ?? entry.Include ?? Array.Empty<string>(), entry.Exclude ?? Array.Empty<string>());
            scopes.Add(new Scope(
                Id: entry.Name,
                Name: entry.Name,
                Root: repoRoot,
                ProjectSet: projectSet,
                Isolated: entry.Isolated ?? false,
                LastIndexedAt: DateTimeOffset.MinValue));
        }

        if (!string.IsNullOrEmpty(dto.DefaultScope) && !seen.Contains(dto.DefaultScope!))
        {
            throw new ScopeConfigException(
                $"{FileName}: `default_scope` is `{dto.DefaultScope}` but no scope by that name is declared.");
        }

        return new ScopeConfig(scopes, dto.DefaultScope);
    }

    /// <summary>
    /// Synthesise a single-scope config when <c>.sourcegraph.json</c> is absent. This is the
    /// zero-config back-compat path: existing users with one .slnx and no JSON keep working.
    /// </summary>
    public static ScopeConfig Synthesise(string repoRoot, IReadOnlyList<string> discoveredSolutions)
    {
        var solutions = discoveredSolutions.Count > 0
            ? discoveredSolutions
            : Array.Empty<string>();
        var scope = new Scope(
            Id: "default",
            Name: "default",
            Root: repoRoot,
            ProjectSet: new ScopeProjectSet.Solutions(solutions, Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.MinValue);
        return new ScopeConfig(new[] { scope }, "default");
    }

    /// <summary>Discovers .slnx and .sln files at the repo root for the <c>init-scopes</c> scaffolder.</summary>
    public static IReadOnlyList<string> DiscoverSolutionSiblings(string repoRoot)
    {
        var slnx = Directory.Exists(repoRoot)
            ? Directory.EnumerateFiles(repoRoot, "*.slnx").ToArray()
            : Array.Empty<string>();
        if (slnx.Length > 0) return slnx;
        return Directory.Exists(repoRoot)
            ? Directory.EnumerateFiles(repoRoot, "*.sln").ToArray()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Serialise a <see cref="ScopeConfig"/> to JSON. Used by the <c>init-scopes</c> CLI subcommand
    /// to scaffold <c>.sourcegraph.json</c> from discovered .slnx siblings.
    /// </summary>
    public static string Serialise(ScopeConfig config)
    {
        var dto = new ScopeConfigJson
        {
            Scopes = config.Scopes.Select(s => new ScopeEntryJson
            {
                Name = s.Id,
                Solutions = s.ProjectSet is ScopeProjectSet.Solutions sol ? sol.Items : null,
                Projects = s.ProjectSet is ScopeProjectSet.Projects p ? p.Items : null,
                Paths = s.ProjectSet is ScopeProjectSet.Paths g ? g.Globs : null,
                Exclude = s.ProjectSet.Exclude.Count > 0 ? s.ProjectSet.Exclude : null,
                Isolated = s.Isolated ? true : null,
            }).ToList(),
            DefaultScope = config.DefaultScope,
        };
        return JsonSerializer.Serialize(dto, _writeOptions);
    }

    /// <summary>
    /// Persist a <see cref="ScopeConfig"/> to <c>&lt;repoRoot&gt;/.sourcegraph.json</c>. Used by the
    /// <c>init-scopes</c> and <c>scopes add/remove</c> CLI subcommands.
    /// </summary>
    public static void Save(string repoRoot, ScopeConfig config)
    {
        var path = Path.Combine(repoRoot, FileName);
        File.WriteAllText(path, Serialise(config));
    }
}

/// <summary>
/// Raised by <see cref="ScopeConfigLoader.Load"/> on schema violations. The CLI catches this and
/// exits with code 2 + a precise message naming the offending key.
/// </summary>
public sealed class ScopeConfigException : Exception
{
    public ScopeConfigException(string message) : base(message) { }
    public ScopeConfigException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// JSON-shape DTOs. Kept private to the loader so the rest of the codebase consumes
/// <see cref="ScopeConfig"/> + <see cref="Scope"/> directly. Property names use snake_case via
/// <see cref="JsonPropertyNameAttribute"/> to match the documented schema.
/// </summary>
internal sealed record ScopeConfigJson
{
    [JsonPropertyName("scopes")] public IReadOnlyList<ScopeEntryJson>? Scopes { get; init; }
    [JsonPropertyName("default_scope")] public string? DefaultScope { get; init; }
}

internal sealed record ScopeEntryJson
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("solutions")] public IReadOnlyList<string>? Solutions { get; init; }
    [JsonPropertyName("projects")] public IReadOnlyList<string>? Projects { get; init; }
    [JsonPropertyName("paths")] public IReadOnlyList<string>? Paths { get; init; }
    /// <summary>Alias for <see cref="Paths"/>; the design.md uses both names.</summary>
    [JsonPropertyName("include")] public IReadOnlyList<string>? Include { get; init; }
    [JsonPropertyName("exclude")] public IReadOnlyList<string>? Exclude { get; init; }
    [JsonPropertyName("isolated")] public bool? Isolated { get; init; }
}
