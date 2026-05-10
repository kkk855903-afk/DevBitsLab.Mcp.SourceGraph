using System.Text;
using System.Text.Json;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Plan-then-apply writer contract for one MCP client's config file. Plan is deterministic and
/// side-effect-free so it can be unit-tested and previewed in <c>--print-only</c> mode; Apply
/// does the actual disk write. Each implementation owns the schema for its target client and
/// emits exactly the shape that client documents — schemas are not normalised across writers.
/// </summary>
internal interface IClientConfigWriter
{
    /// <summary>Stable identity for this writer; matches the picker slug and the
    /// <c>--client &lt;id&gt;</c> flag.</summary>
    ClientId ClientId { get; }

    /// <summary>Project-scoped target path under <paramref name="root"/>, or null when this client
    /// has no project-scoped equivalent (Claude Desktop).</summary>
    string? DefaultProjectPath(string root);

    /// <summary>User-scoped target path under the home directory, or null when this client has
    /// no user-scoped equivalent. Several writers expose both paths and pick one based on
    /// <see cref="WriterContext.UseUserScope"/>.</summary>
    string? DefaultUserPath();

    /// <summary>
    /// Returns a deterministic plan describing what the writer would do given the current target
    /// file's contents (passed in via <see cref="WriterContext.ExistingContent"/>; null means the
    /// file doesn't exist). The plan does not touch disk.
    /// </summary>
    WriterPlan Plan(WriterContext ctx);

    /// <summary>Apply the plan: writes <see cref="WriterPlan.ContentBytes"/> to
    /// <see cref="WriterPlan.TargetPath"/>, creating parent directories as needed. No-op when the
    /// action is <see cref="WriterAction.NoOpAlreadyMatches"/>, <see cref="WriterAction.SkipExistingDiffers"/>,
    /// or <see cref="WriterAction.SkipHasComments"/>.</summary>
    void Apply(WriterPlan plan);
}

/// <summary>
/// Inputs to <see cref="IClientConfigWriter.Plan"/>. The caller resolves all paths and reads the
/// existing target file (if any) before constructing the context, keeping the writer pure.
/// </summary>
internal sealed record WriterContext(
    string Root,
    string TargetPath,
    bool UseUserScope,
    InstallMode InstallMode,
    string? SolutionPath,
    string? ServerProjectPath,
    bool NoEmbeddings,
    bool NoHistory,
    bool Force,
    byte[]? ExistingContent);

/// <summary>How the resulting <c>command</c> + <c>args</c> arrays should invoke the server.</summary>
internal enum InstallMode
{
    /// <summary>The user has <c>sourcegraph-mcp</c> on PATH (global tool install). Default.</summary>
    Global,
    /// <summary>The user pinned the tool via <c>.config/dotnet-tools.json</c>; we shell through
    /// <c>dotnet sourcegraph-mcp ...</c>.</summary>
    LocalTool,
    /// <summary>The user is wiring this repo's own .mcp.json to launch the in-repo server source
    /// via <c>dotnet run --project ... --no-build -- serve ...</c>.</summary>
    InRepo,
}

/// <summary>
/// Outcome a writer's <see cref="IClientConfigWriter.Plan"/> chose to perform. The <see cref="WriterAction"/>
/// distinguishes the four merge outcomes plus the comment-aware degraded path.
/// </summary>
internal sealed record WriterPlan(
    string TargetPath,
    WriterAction Action,
    byte[] ContentBytes,
    string Description);

internal enum WriterAction
{
    /// <summary>Target file is absent OR exists without our entry; we'd write fresh content.</summary>
    Insert,
    /// <summary>Target has our entry but it differs and either <c>--force</c> is set or the user
    /// confirmed the overwrite. The plan emits the new content.</summary>
    ReplaceOurs,
    /// <summary>Target has our entry and it matches byte-for-byte; nothing to do.</summary>
    NoOpAlreadyMatches,
    /// <summary>Target has our entry that differs and <c>--force</c> is not set; the plan leaves
    /// the file alone. <see cref="WriterPlan.ContentBytes"/> still carries the would-be content
    /// for diagnostics. Triggers a CI-failure exit code (<c>2</c>) — this is a real conflict the
    /// caller likely needs to resolve.</summary>
    SkipExistingDiffers,
    /// <summary>Target file contains JS-style line comments outside string literals; round-tripping
    /// would silently strip them. The plan leaves the file alone and the caller emits the snippet
    /// to stdout for the user to paste manually. Informational, not a CI-failure.</summary>
    SkipHasComments,
    /// <summary>The selected client/scope combination has no writer support in v1 (e.g.
    /// <c>--user-copilot</c>: Copilot's user-scope config lives in VS Code's <c>settings.json</c>
    /// under <c>chat.mcp.servers</c>, which has no dedicated writer). The closing report names
    /// the combo and the workaround (typically: re-run without the flag, or use
    /// <c>--print-only</c> and paste). Informational, not a CI-failure.</summary>
    SkipUnsupported,
}

/// <summary>
/// Shared JSON serialiser settings for all JSON-based writers: 2-space indent, snake-case-friendly
/// (we don't apply any naming policy ourselves; the writers construct the property names verbatim),
/// preserve insertion order. Encoder set to UnsafeRelaxedJsonEscaping so `&lt;` / `&gt;` / `&amp;`
/// in args (rare, but possible in a custom path) round-trip without HTML-escape encoding.
/// </summary>
internal static class WriterJson
{
    public static readonly JsonSerializerOptions Indented2 = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] SerializeToBytes(System.Text.Json.Nodes.JsonNode node)
    {
        var json = node.ToJsonString(Indented2);
        // System.Text.Json emits LF on all platforms, which is what we want for portability —
        // the existing .mcp.json shipped at the repo root uses LF. Append a trailing newline for
        // POSIX-friendliness.
        return Encoding.UTF8.GetBytes(json + "\n");
    }
}
