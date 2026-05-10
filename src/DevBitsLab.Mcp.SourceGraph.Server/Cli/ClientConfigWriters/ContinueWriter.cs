using System.Text;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Writer for Continue's <c>.continue/mcp/sourcegraph.yaml</c> (project) or
/// <c>~/.continue/mcp/sourcegraph.yaml</c> (user). Continue uses a one-server-per-file YAML
/// layout — the filesystem path is the merge boundary, so no in-file merge logic is needed
/// (unlike the JSON writers, which all share a single <c>mcpServers</c>/<c>servers</c> map).
///
/// We hand-emit the YAML rather than take a YamlDotNet dependency: the schema is fixed and
/// trivially small (three top-level keys, one nested array), and the file content is fully
/// determined by <see cref="WriterContext"/> so byte-equality is a sound match predicate.
/// </summary>
internal sealed class ContinueWriter : IClientConfigWriter
{
    public ClientId ClientId => ClientId.Continue;

    public string? DefaultProjectPath(string root) =>
        Path.Join(root, ".continue", "mcp", "sourcegraph.yaml");

    public string? DefaultUserPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(home) ? null : Path.Join(home, ".continue", "mcp", "sourcegraph.yaml");
    }

    public WriterPlan Plan(WriterContext ctx)
    {
        var ours = BuildYaml(ctx);
        var ourBytes = Encoding.UTF8.GetBytes(ours);

        if (ctx.ExistingContent is null or { Length: 0 })
        {
            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.Insert,
                ourBytes,
                "would create new sourcegraph.yaml");
        }

        // Continue YAML: a single-server file. The merge boundary is the filename, so we don't
        // need to preserve unrelated content. Either the existing file's bytes match ours
        // (NoOp), or they differ (replace under --force, skip otherwise).
        if (ctx.ExistingContent.AsSpan().SequenceEqual(ourBytes))
        {
            return new WriterPlan(
                ctx.TargetPath,
                WriterAction.NoOpAlreadyMatches,
                ctx.ExistingContent,
                "already wired (no change)");
        }

        return new WriterPlan(
            ctx.TargetPath,
            ctx.Force ? WriterAction.ReplaceOurs : WriterAction.SkipExistingDiffers,
            ourBytes,
            ctx.Force
                ? "would replace existing sourcegraph.yaml"
                : "existing sourcegraph.yaml differs (use --force to overwrite)");
    }

    public void Apply(WriterPlan plan)
    {
        if (plan.Action is not (WriterAction.Insert or WriterAction.ReplaceOurs)) return;
        var dir = Path.GetDirectoryName(plan.TargetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(plan.TargetPath, plan.ContentBytes);
    }

    private static string BuildYaml(WriterContext ctx)
    {
        var (command, args) = WriterCommandLine.Build(ctx);
        var sb = new StringBuilder();
        sb.Append("name: sourcegraph\n");
        sb.Append("command: ").Append(EscapeScalar(command)).Append('\n');
        sb.Append("args:\n");
        foreach (var arg in args)
        {
            sb.Append("  - ").Append(EscapeScalar(arg)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quote a YAML scalar when it contains characters with semantic meaning (colons, leading
    /// dashes, special tokens). Uses double-quoted style so backslash escapes are conventional.
    /// </summary>
    private static string EscapeScalar(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        // Plain scalars may contain ${...} placeholders, slashes, dots — but not colons,
        // hash marks, or quote characters. Be conservative and quote anything that looks
        // dangerous, OR that begins with a YAML indicator (-, ?, ' ', etc.). Both branches
        // share the same escape pass so a value like `-foo"bar` round-trips correctly when
        // its trigger is the leading dash rather than the embedded quote.
        if (NeedsQuoting(value))
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return value;
    }

    private static bool NeedsQuoting(string value)
    {
        if (value.Any(c => c is ':' or '#' or '"' or '\'' or '[' or ']' or '{' or '}' or ',' or '&' or '*'
            or '!' or '|' or '>' or '%' or '@' or '`' or '\\' or '\n' or '\t'))
        {
            return true;
        }
        // Plain-scalar guards: a leading indicator character forces quoting even when the
        // rest of the string is plain.
        return value[0] is '-' or '?' or ' ';
    }
}
