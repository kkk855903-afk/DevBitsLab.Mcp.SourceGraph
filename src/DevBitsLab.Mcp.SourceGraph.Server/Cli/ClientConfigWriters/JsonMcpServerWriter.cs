using System.Text;
using System.Text.Json.Nodes;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Shared base for JSON-based MCP-server config writers. Subclasses provide the per-client
/// schema (top-level key, the per-server entry shape) and the file-path defaults; the merge
/// logic — find the existing <c>sourcegraph</c> entry, deep-equal vs ours, decide which
/// <see cref="WriterAction"/> applies — lives here so the four JSON writers (Claude Code,
/// Copilot, Cursor, Claude Desktop) stay tiny and uniform.
/// </summary>
internal abstract class JsonMcpServerWriter : IClientConfigWriter
{
    public abstract ClientId ClientId { get; }
    public abstract string? DefaultProjectPath(string root);
    public abstract string? DefaultUserPath();

    /// <summary>The top-level key holding the server map; <c>"mcpServers"</c> for Claude Code /
    /// Cursor / Claude Desktop, <c>"servers"</c> for Copilot.</summary>
    protected abstract string TopLevelKey { get; }

    /// <summary>The fixed entry-name under <see cref="TopLevelKey"/>. Always <c>"sourcegraph"</c>
    /// today; carved out as a virtual property so a future client that clashes on the name can
    /// override.</summary>
    protected virtual string ServerEntryName => "sourcegraph";

    /// <summary>Emit the per-server JSON object that goes under <c>topLevel[ServerEntryName]</c>
    /// for this client. Default implementation produces <c>{ command, args }</c>; Copilot
    /// overrides to insert the <c>type: "stdio"</c> field.</summary>
    protected virtual JsonObject BuildServerEntry(WriterContext ctx)
    {
        var (command, args) = WriterCommandLine.Build(ctx);
        var argsArray = new JsonArray();
        foreach (var a in args) argsArray.Add(a);
        return new JsonObject
        {
            ["command"] = command,
            ["args"] = argsArray,
        };
    }

    public WriterPlan Plan(WriterContext ctx)
    {
        var ourEntry = BuildServerEntry(ctx);

        // Comment detection runs against the *existing* file only — a fresh write produces
        // exactly the bytes WriterJson serialises, no comments.
        if (ctx.ExistingContent is { Length: > 0 } bytes)
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (CommentDetector.HasJsonComments(text))
            {
                var freshDoc = NewDocumentWithEntry(ourEntry);
                return new WriterPlan(
                    ctx.TargetPath,
                    WriterAction.SkipHasComments,
                    WriterJson.SerializeToBytes(freshDoc),
                    "config has comments at " + ctx.TargetPath + " — please paste manually");
            }
        }

        // Fresh write when the file is absent or empty: emit a brand-new document.
        if (ctx.ExistingContent is null or { Length: 0 })
        {
            var freshDoc = NewDocumentWithEntry(ourEntry);
            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.Insert,
                WriterJson.SerializeToBytes(freshDoc),
                "would create new config with sourcegraph entry");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(ctx.ExistingContent);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON: we can't safely merge into it, so the only options are skip (default)
            // or overwrite with a fresh document (--force). The caller surfaces the warning either
            // way; Force makes it actionable.
            var freshDoc = NewDocumentWithEntry(ourEntry);
            return new WriterPlan(
                ctx.TargetPath,
                ctx.Force ? WriterAction.ReplaceOurs : WriterAction.SkipExistingDiffers,
                WriterJson.SerializeToBytes(freshDoc),
                ctx.Force
                    ? "existing config is not valid JSON; force-replacing with a fresh document"
                    : "existing config is not valid JSON; refusing to overwrite (use --force)");
        }

        if (root is not JsonObject topObj)
        {
            var freshDoc = NewDocumentWithEntry(ourEntry);
            return new WriterPlan(
                ctx.TargetPath,
                ctx.Force ? WriterAction.ReplaceOurs : WriterAction.SkipExistingDiffers,
                WriterJson.SerializeToBytes(freshDoc),
                "existing config root is not an object");
        }

        // Get-or-create the server map. If the key exists but isn't an object, treat as conflict.
        if (topObj[TopLevelKey] is { } existingMapNode and not JsonObject)
        {
            var freshDoc = NewDocumentWithEntry(ourEntry);
            return new WriterPlan(
                ctx.TargetPath,
                ctx.Force ? WriterAction.ReplaceOurs : WriterAction.SkipExistingDiffers,
                WriterJson.SerializeToBytes(freshDoc),
                $"top-level '{TopLevelKey}' exists but is not an object");
        }

        var serverMap = topObj[TopLevelKey] as JsonObject;
        var existingEntry = serverMap?[ServerEntryName];

        // Compare deeply. JsonNode.DeepEquals (System.Text.Json 9+) handles property-order
        // insensitivity at the object level, exact-equality at the value level.
        if (existingEntry is not null && JsonNode.DeepEquals(existingEntry, ourEntry))
        {
            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.NoOpAlreadyMatches,
                ctx.ExistingContent, // unchanged bytes
                "already wired (no change)");
        }

        // Build the merged document either way (we'll attach to ContentBytes for diagnostics).
        if (serverMap is null)
        {
            serverMap = new JsonObject();
            topObj[TopLevelKey] = serverMap;
        }
        // Detach ourEntry from any owning JsonObject — JsonNode rejects re-parenting in place.
        var detachedEntry = JsonNode.Parse(ourEntry.ToJsonString()) as JsonObject ?? new JsonObject();
        serverMap[ServerEntryName] = detachedEntry;
        var mergedBytes = WriterJson.SerializeToBytes(topObj);

        if (existingEntry is null)
        {
            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.Insert,
                mergedBytes,
                $"would insert sourcegraph into existing config (preserving other servers)");
        }

        // Existing differs.
        return new WriterPlan(
            ctx.TargetPath,
            ctx.Force ? WriterAction.ReplaceOurs : WriterAction.SkipExistingDiffers,
            mergedBytes,
            ctx.Force
                ? "would replace existing differing sourcegraph entry"
                : "existing sourcegraph entry differs (use --force to overwrite)");
    }

    public void Apply(WriterPlan plan)
    {
        if (plan.Action is not (WriterAction.Insert or WriterAction.ReplaceOurs)) return;
        var dir = Path.GetDirectoryName(plan.TargetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(plan.TargetPath, plan.ContentBytes);
    }

    private JsonObject NewDocumentWithEntry(JsonObject ourEntry)
    {
        // Fresh ourEntry detached for the fresh-write path so the same entry can be reused
        // without JsonNode parent-tracking complaints.
        var fresh = JsonNode.Parse(ourEntry.ToJsonString()) as JsonObject ?? new JsonObject();
        return new JsonObject
        {
            [TopLevelKey] = new JsonObject { [ServerEntryName] = fresh },
        };
    }
}

/// <summary>
/// Builds the <c>command</c> + <c>args</c> array shared by every JSON-based writer. Picks a
/// shape based on <see cref="WriterContext.InstallMode"/> and threads through
/// <see cref="WriterContext.NoEmbeddings"/>/<see cref="WriterContext.NoHistory"/>.
/// </summary>
internal static class WriterCommandLine
{
    public static (string Command, string[] Args) Build(WriterContext ctx)
    {
        switch (ctx.InstallMode)
        {
            case InstallMode.Global:
                return ("sourcegraph-mcp", BuildServeArgs(ctx));

            case InstallMode.LocalTool:
                {
                    var args = new List<string> { "sourcegraph-mcp" };
                    args.AddRange(BuildServeArgs(ctx));
                    return ("dotnet", args.ToArray());
                }

            case InstallMode.InRepo:
                {
                    var serverProject = ctx.ServerProjectPath
                        ?? "${workspaceFolder}/src/DevBitsLab.Mcp.SourceGraph.Server";
                    var args = new List<string>
                    {
                        "run", "--project", serverProject,
                        "--no-build", "--no-launch-profile", "--verbosity", "quiet", "--",
                    };
                    args.AddRange(BuildServeArgs(ctx));
                    return ("dotnet", args.ToArray());
                }

            default:
                return ("sourcegraph-mcp", BuildServeArgs(ctx));
        }
    }

    /// <summary>
    /// Builds the <c>serve …</c> tail of the args array. Two scope shapes:
    /// <list type="bullet">
    /// <item>Single-solution (<see cref="WriterContext.SolutionPath"/> non-null) → <c>--solution &lt;path&gt;</c>.
    /// Used both for resolved single-solution detect AND for the "no solutions found" placeholder
    /// (<c>${workspaceFolder}/MySolution.slnx</c>) the user fills in later.</item>
    /// <item>Multi-scope root mode (<see cref="WriterContext.SolutionPath"/> null) → <c>--root ${workspaceFolder}</c>.
    /// Lets the server discover <c>.sourcegraph.json</c> instead of registering an implicit single
    /// scope. Falling back to <c>--solution &lt;placeholder&gt;</c> here would silently override the
    /// user's multi-scope config.</item>
    /// </list>
    /// </summary>
    private static string[] BuildServeArgs(WriterContext ctx)
    {
        var args = new List<string> { "serve" };
        if (string.IsNullOrEmpty(ctx.SolutionPath))
        {
            args.Add("--root");
            args.Add("${workspaceFolder}");
        }
        else
        {
            args.Add("--solution");
            args.Add(ctx.SolutionPath);
        }
        if (ctx.NoEmbeddings) args.Add("--no-embeddings");
        if (ctx.NoHistory) args.Add("--no-history");
        return args.ToArray();
    }
}
