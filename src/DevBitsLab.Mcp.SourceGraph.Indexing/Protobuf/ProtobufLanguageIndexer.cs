using System.Security.Cryptography;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;

public enum ProtobufSourceFailureKind
{
    SourceRejected,
    SourceTooLarge,
    InvalidEncoding,
    LimitExceeded,
    ToolUnavailable,
    CompilerFailed,
    InvalidDescriptorSet,
}

/// <summary>
/// A bounded protobuf compiler/projection failure. The dispatcher treats this like any other
/// per-file indexer failure, so the last successful file projection remains intact.
/// </summary>
public sealed class ProtobufSourceIndexingException : FormatException
{
    public ProtobufSourceIndexingException(
        ProtobufSourceFailureKind kind,
        string message,
        int? line = null,
        int? column = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Line = line;
        Column = column;
    }

    public ProtobufSourceFailureKind Kind { get; }
    public int? Line { get; }
    public int? Column { get; }
}

/// <summary>
/// Compiles protobuf source with the pinned official <c>protoc</c> binary, then projects
/// <see cref="Google.Protobuf.Reflection.FileDescriptorProto"/> reflection data into the graph.
/// Imports are resolved only from a privacy-filtered, bounded staging tree; protoc never receives
/// the repository root as an include path.
/// </summary>
public sealed class ProtobufLanguageIndexer :
    ILanguageIndexer,
    IBoundedSourceLanguageIndexer
{
    public const int MaximumSourceBytes = 1024 * 1024;
    public const int MaximumSourceFiles = 4096;
    public const int MaximumStagedBytes = 32 * 1024 * 1024;
    public const int MaximumDescriptorSetBytes = 16 * 1024 * 1024;
    public const int MaximumDeclarations = 10_000;
    public const int MaximumMessageNesting = 32;

    private static readonly UTF8Encoding _strictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IProtobufDescriptorCompiler _compiler;

    public ProtobufLanguageIndexer()
        : this(new ProtocDescriptorCompiler())
    {
    }

    internal ProtobufLanguageIndexer(IProtobufDescriptorCompiler compiler)
    {
        _compiler = compiler
            ?? throw new ArgumentNullException(nameof(compiler));
    }

    public IReadOnlyCollection<string> FileExtensions { get; } = [".proto"];
    public int MaximumSourceSizeBytes => MaximumSourceBytes;

    public async Task<IReadOnlyList<IndexEvent>> IndexAsync(
        IndexContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        // Reapply the scope boundary before decoding bytes or starting an external process. The
        // dispatcher already performs this check; the second gate protects direct/plugin use.
        if (!IsAuthorized(ctx))
        {
            return Array.Empty<IndexEvent>();
        }

        ArgumentNullException.ThrowIfNull(ctx.Contents);
        if (ctx.Contents.Length > MaximumSourceBytes)
        {
            throw Failure(
                ProtobufSourceFailureKind.SourceTooLarge,
                $"Protobuf source exceeds the {MaximumSourceBytes}-byte limit.");
        }

        try
        {
            _strictUtf8.GetCharCount(ctx.Contents);
        }
        catch (DecoderFallbackException ex)
        {
            throw Failure(
                ProtobufSourceFailureKind.InvalidEncoding,
                "Protobuf source is not valid UTF-8.",
                innerException: ex);
        }

        var descriptor = await _compiler
            .CompileAsync(ctx, ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var events = ProtobufDescriptorProjector.Project(descriptor);
        events.Add(new IndexEvent.FileScanned(
            ctx.FilePath,
            SHA256.HashData(ctx.Contents)));
        return events;
    }

    private static bool IsAuthorized(IndexContext ctx)
    {
        try
        {
            var policy = new ScopePathPolicy(
                ctx.RepoRoot,
                ctx.ExcludePatterns);
            return !policy.IsExcluded(ctx.FilePath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            throw Failure(
                ProtobufSourceFailureKind.SourceRejected,
                "Protobuf source path could not be authorized inside its scope.",
                innerException: ex);
        }
    }

    internal static ProtobufSourceIndexingException Failure(
        ProtobufSourceFailureKind kind,
        string message,
        int? line = null,
        int? column = null,
        Exception? innerException = null) =>
        new(kind, message, line, column, innerException);
}
