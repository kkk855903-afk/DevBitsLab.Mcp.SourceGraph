using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;

internal interface IProtobufDescriptorCompiler
{
    Task<FileDescriptorProto> CompileAsync(
        IndexContext context,
        CancellationToken cancellationToken);
}

internal sealed class ProtocDescriptorCompiler : IProtobufDescriptorCompiler
{
    internal const string ProtocPathEnvironmentVariable =
        "MEDINTEROPLENS_PROTOC_PATH";
    internal const string ProtocIncludeEnvironmentVariable =
        "MEDINTEROPLENS_PROTOC_INCLUDE";
    internal const string GrpcToolsVersion = "2.82.0";

    private const int MaximumCompilerOutputBytes = 64 * 1024;
    private static readonly TimeSpan _compilerTimeout =
        TimeSpan.FromSeconds(20);
    private static readonly StringComparer _pathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public async Task<FileDescriptorProto> CompileAsync(
        IndexContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var tool = ProtocToolLocator.Locate();
        var tempRoot = Directory.CreateTempSubdirectory(
            "medinterop-proto-").FullName;
        try
        {
            var sourceRoot = Path.Join(tempRoot, "source");
            var toolRoot = Path.Join(tempRoot, "tool");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(toolRoot);

            var targetRelativePath = GetTargetRelativePath(context);
            await StageSourcesAsync(
                    context,
                    sourceRoot,
                    targetRelativePath,
                    cancellationToken)
                .ConfigureAwait(false);

            var stagedTool = await StageToolAsync(
                    tool,
                    toolRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            var outputPath = Path.Join(tempRoot, "descriptor.pb");

            var output = await RunCompilerAsync(
                    stagedTool.CompilerPath,
                    sourceRoot,
                    stagedTool.IncludePath,
                    targetRelativePath,
                    outputPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (output.ExitCode != 0)
            {
                var diagnostic = ParseCompilerDiagnostic(output);
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.CompilerFailed,
                    BuildCompilerFailure(output),
                    diagnostic.Line,
                    diagnostic.Column);
            }

            return await ReadDescriptorAsync(
                    outputPath,
                    targetRelativePath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemporaryDirectory(tempRoot);
        }
    }

    private static string GetTargetRelativePath(IndexContext context)
    {
        string root;
        string target;
        try
        {
            root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(context.RepoRoot));
            target = Path.GetFullPath(context.FilePath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.SourceRejected,
                "The protobuf source path is invalid.",
                innerException: ex);
        }

        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.SourceRejected,
                "The protobuf source is outside its scope root.");
        }
        return NormalizeProtoPath(relative);
    }

    private static async Task StageSourcesAsync(
        IndexContext context,
        string sourceRoot,
        string targetRelativePath,
        CancellationToken cancellationToken)
    {
        var policy = new ScopePathPolicy(
            context.RepoRoot,
            context.ExcludePatterns);
        var targetPath = Path.GetFullPath(context.FilePath);
        var stagedFiles = 0;
        long stagedBytes = 0;

        await StageBytesAsync(
                sourceRoot,
                targetRelativePath,
                context.Contents,
                cancellationToken)
            .ConfigureAwait(false);
        stagedFiles++;
        stagedBytes += context.Contents.Length;

        foreach (var candidate in EnumerateProtoFiles(
                     context.RepoRoot,
                     policy,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pathComparer.Equals(
                    Path.GetFullPath(candidate.LogicalPath),
                    targetPath))
            {
                continue;
            }
            if (stagedFiles >= ProtobufLanguageIndexer.MaximumSourceFiles)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.LimitExceeded,
                    "The privacy-filtered protobuf source set exceeds the "
                    + $"{ProtobufLanguageIndexer.MaximumSourceFiles}-file limit.");
            }

            var bytes = await ReadBoundedSourceAsync(
                    candidate.PhysicalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stagedBytes
                > ProtobufLanguageIndexer.MaximumStagedBytes - bytes.Length)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.LimitExceeded,
                    "The privacy-filtered protobuf source set exceeds the "
                    + $"{ProtobufLanguageIndexer.MaximumStagedBytes}-byte limit.");
            }

            var relative = NormalizeProtoPath(
                Path.GetRelativePath(
                    Path.GetFullPath(context.RepoRoot),
                    Path.GetFullPath(candidate.LogicalPath)));
            await StageBytesAsync(
                    sourceRoot,
                    relative,
                    bytes,
                    cancellationToken)
                .ConfigureAwait(false);
            stagedFiles++;
            stagedBytes += bytes.Length;
        }
    }

    private static IEnumerable<SourceCandidate> EnumerateProtoFiles(
        string root,
        ScopePathPolicy policy,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var pending = new Stack<string>();
        var visitedPhysicalDirectories = new HashSet<string>(_pathComparer);
        pending.Push(normalizedRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (policy.IsExcludedForDiscovery(
                    directory,
                    out var physicalDirectory)
                || physicalDirectory is null
                || !visitedPhysicalDirectories.Add(physicalDirectory))
            {
                continue;
            }

            IReadOnlyList<string> directories;
            IReadOnlyList<string> files;
            try
            {
                directories = Directory
                    .EnumerateDirectories(directory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                files = Directory
                    .EnumerateFiles(directory, "*.proto")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException
                                       or System.Security.SecurityException)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.SourceRejected,
                    "A protobuf source directory could not be enumerated safely.",
                    innerException: ex);
            }

            for (var index = directories.Count - 1; index >= 0; index--)
            {
                var child = directories[index];
                if (!policy.IsExcluded(child)) pending.Push(child);
            }

            foreach (var file in files)
            {
                if (policy.IsExcludedForDiscovery(
                        file,
                        out var physicalFile)
                    || physicalFile is null)
                {
                    continue;
                }
                yield return new SourceCandidate(file, physicalFile);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedSourceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 81_920,
                    Options = System.IO.FileOptions.Asynchronous
                        | System.IO.FileOptions.SequentialScan,
                });
            if (stream.CanSeek
                && stream.Length > ProtobufLanguageIndexer.MaximumSourceBytes)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.LimitExceeded,
                    "An imported protobuf source exceeds the "
                    + $"{ProtobufLanguageIndexer.MaximumSourceBytes}-byte limit.");
            }

            using var output = new MemoryStream(
                stream.CanSeek && stream.Length > 0
                    ? checked((int)stream.Length)
                    : 0);
            var buffer = new byte[81_920];
            var total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                if (read
                    > ProtobufLanguageIndexer.MaximumSourceBytes - total)
                {
                    throw ProtobufLanguageIndexer.Failure(
                        ProtobufSourceFailureKind.LimitExceeded,
                        "An imported protobuf source exceeds the "
                        + $"{ProtobufLanguageIndexer.MaximumSourceBytes}-byte limit.");
                }
                await output
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                total += read;
            }
            return output.ToArray();
        }
        catch (ProtobufSourceIndexingException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.SourceRejected,
                "An imported protobuf source could not be read safely.",
                innerException: ex);
        }
    }

    private static async Task StageBytesAsync(
        string sourceRoot,
        string relativePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(
            Path.Join(
                sourceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedSourceRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(sourceRoot));
        if (!destination.StartsWith(
                normalizedSourceRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.SourceRejected,
                "A protobuf staging path escaped its temporary source root.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(
                destination,
                bytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<StagedTool> StageToolAsync(
        ProtocTool tool,
        string toolRoot,
        CancellationToken cancellationToken)
    {
        var fileName = OperatingSystem.IsWindows()
            ? "protoc.exe"
            : "protoc";
        var compilerPath = Path.Join(toolRoot, fileName);
        await using (var source = new FileStream(
                         tool.CompilerPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        await using (var destination = new FileStream(
                         compilerPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            await source
                .CopyToAsync(destination, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(compilerPath);
            File.SetUnixFileMode(
                compilerPath,
                mode
                | UnixFileMode.UserExecute
                | UnixFileMode.UserRead
                | UnixFileMode.UserWrite);
        }

        var includePath = Path.Join(toolRoot, "include");
        CopyIncludeTree(tool.IncludePath, includePath);
        return new StagedTool(compilerPath, includePath);
    }

    private static void CopyIncludeTree(string sourceRoot, string destinationRoot)
    {
        var protobufSource = Path.Join(
            sourceRoot,
            "google",
            "protobuf");
        if (!Directory.Exists(protobufSource))
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.ToolUnavailable,
                "The protoc well-known-type include directory is unavailable.");
        }

        foreach (var source in Directory
                     .EnumerateFiles(
                         protobufSource,
                         "*.proto",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(
                sourceRoot,
                source);
            var destination = Path.Join(
                destinationRoot,
                relative);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);
            File.Copy(
                source,
                destination,
                overwrite: false);
        }
    }

    private static async Task<CompilerOutput> RunCompilerAsync(
        string compilerPath,
        string sourceRoot,
        string includePath,
        string targetRelativePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = compilerPath,
                WorkingDirectory = sourceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(
            "--descriptor_set_out=" + outputPath);
        process.StartInfo.ArgumentList.Add("--include_imports");
        process.StartInfo.ArgumentList.Add("--include_source_info");
        process.StartInfo.ArgumentList.Add("--proto_path=" + sourceRoot);
        process.StartInfo.ArgumentList.Add("--proto_path=" + includePath);
        process.StartInfo.ArgumentList.Add(targetRelativePath);

        try
        {
            if (!process.Start())
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.ToolUnavailable,
                    "The bundled protoc process could not be started.");
            }
        }
        catch (ProtobufSourceIndexingException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.ToolUnavailable,
                "The bundled protoc process could not be started.",
                innerException: ex);
        }

        var stdoutTask = DrainBoundedAsync(
            process.StandardOutput.BaseStream,
            cancellationToken);
        var stderrTask = DrainBoundedAsync(
            process.StandardError.BaseStream,
            cancellationToken);
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_compilerTimeout);
        try
        {
            await process
                .WaitForExitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.CompilerFailed,
                $"protoc exceeded the {_compilerTimeout.TotalSeconds:0}-second limit.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CompilerOutput(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> DrainBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var captured = new MemoryStream(MaximumCompilerOutputBytes);
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            var remaining = MaximumCompilerOutputBytes
                - checked((int)captured.Length);
            if (remaining > 0)
            {
                await captured
                    .WriteAsync(
                        buffer.AsMemory(0, Math.Min(read, remaining)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        return Encoding.UTF8.GetString(captured.ToArray());
    }

    private static async Task<FileDescriptorProto> ReadDescriptorAsync(
        string outputPath,
        string targetRelativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(outputPath);
            if (!info.Exists
                || info.Length <= 0
                || info.Length
                    > ProtobufLanguageIndexer.MaximumDescriptorSetBytes)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.InvalidDescriptorSet,
                    "protoc produced no descriptor set or exceeded the "
                    + $"{ProtobufLanguageIndexer.MaximumDescriptorSetBytes}-byte limit.");
            }

            var bytes = await File
                .ReadAllBytesAsync(outputPath, cancellationToken)
                .ConfigureAwait(false);
            var descriptorSet = FileDescriptorSet.Parser.ParseFrom(bytes);
            var normalizedTarget = NormalizeProtoPath(targetRelativePath);
            var candidates = descriptorSet.File
                .Where(file => string.Equals(
                    NormalizeProtoPath(file.Name),
                    normalizedTarget,
                    StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length != 1)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.InvalidDescriptorSet,
                    "protoc did not produce exactly one descriptor for the target source.");
            }
            return candidates[0];
        }
        catch (ProtobufSourceIndexingException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidProtocolBufferException
                                   or UnauthorizedAccessException)
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.InvalidDescriptorSet,
                "The protoc descriptor set could not be read safely.",
                innerException: ex);
        }
    }

    private static string BuildCompilerFailure(CompilerOutput output)
    {
        var detail = string.IsNullOrWhiteSpace(output.StandardError)
            ? output.StandardOutput
            : output.StandardError;
        detail = detail
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (detail.Length > 1024) detail = detail[..1024];
        return string.IsNullOrWhiteSpace(detail)
            ? $"protoc exited with code {output.ExitCode}."
            : $"protoc exited with code {output.ExitCode}: {detail}";
    }

    private static CompilerDiagnostic ParseCompilerDiagnostic(
        CompilerOutput output)
    {
        var text = string.IsNullOrWhiteSpace(output.StandardError)
            ? output.StandardOutput
            : output.StandardError;
        foreach (var line in text.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            // protoc diagnostics end their source prefix with :line:column:. Parse from the
            // right so a Windows drive-letter colon cannot be confused with a separator.
            var parts = line.Split(':');
            for (var index = parts.Length - 2; index >= 2; index--)
            {
                if (int.TryParse(parts[index], out var column)
                    && int.TryParse(parts[index - 1], out var sourceLine)
                    && sourceLine > 0
                    && column > 0)
                {
                    return new CompilerDiagnostic(sourceLine, column);
                }
            }
        }
        return new CompilerDiagnostic(null, null);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or NotSupportedException
                                   or System.ComponentModel.Win32Exception)
        {
            // Best effort after timeout/cancellation.
        }
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            if (fullPath.StartsWith(
                    tempRoot + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                && Path.GetFileName(fullPath)
                    .StartsWith(
                        "medinterop-proto-",
                        StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException)
        {
            // Temporary cleanup is best effort and never changes the indexing result.
        }
    }

    private static string NormalizeProtoPath(string path) =>
        path.Replace('\\', '/');

    private sealed record SourceCandidate(
        string LogicalPath,
        string PhysicalPath);

    private sealed record StagedTool(
        string CompilerPath,
        string IncludePath);

    private sealed record CompilerOutput(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record CompilerDiagnostic(
        int? Line,
        int? Column);
}

internal sealed record ProtocTool(
    string CompilerPath,
    string IncludePath);

internal static class ProtocToolLocator
{
    public static ProtocTool Locate()
    {
        var explicitPath = Environment.GetEnvironmentVariable(
            ProtocDescriptorCompiler.ProtocPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var include = Environment.GetEnvironmentVariable(
                ProtocDescriptorCompiler.ProtocIncludeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(include))
            {
                include = FindIncludePath(
                    Path.GetDirectoryName(Path.GetFullPath(explicitPath))!);
            }
            return Validate(explicitPath, include);
        }

        var appBase = AppContext.BaseDirectory;
        var bundledCompiler = Path.Join(
            appBase,
            "protoc",
            OperatingSystem.IsWindows() ? "protoc.exe" : "protoc");
        var bundledInclude = Path.Join(appBase, "protoc", "include");
        if (File.Exists(bundledCompiler)
            && Directory.Exists(bundledInclude))
        {
            return Validate(bundledCompiler, bundledInclude);
        }

        var packageRoot = ResolveNuGetPackageRoot();
        if (packageRoot is not null)
        {
            var platform = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 when OperatingSystem.IsLinux() =>
                    "linux_arm64",
                _ when OperatingSystem.IsLinux() => "linux_x64",
                _ when OperatingSystem.IsMacOS() => "macosx_x64",
                _ when OperatingSystem.IsWindows() => "windows_x64",
                _ => string.Empty,
            };
            if (platform.Length > 0)
            {
                var compiler = Path.Join(
                    packageRoot,
                    "tools",
                    platform,
                    OperatingSystem.IsWindows()
                        ? "protoc.exe"
                        : "protoc");
                var include = Path.Join(
                    packageRoot,
                    "build",
                    "native",
                    "include");
                if (File.Exists(compiler)
                    && Directory.Exists(include))
                {
                    return Validate(compiler, include);
                }
            }
        }

        throw ProtobufLanguageIndexer.Failure(
            ProtobufSourceFailureKind.ToolUnavailable,
            "The pinned protoc compiler payload is unavailable.");
    }

    private static string? ResolveNuGetPackageRoot()
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
        {
            var profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) return null;
            packages = Path.Join(profile, ".nuget", "packages");
        }
        return Path.Join(
            Path.GetFullPath(packages),
            "grpc.tools",
            ProtocDescriptorCompiler.GrpcToolsVersion);
    }

    private static string FindIncludePath(string compilerDirectory)
    {
        var candidates = new[]
        {
            Path.Join(compilerDirectory, "include"),
            Path.GetFullPath(
                Path.Join(
                    compilerDirectory,
                    "..",
                    "..",
                    "..",
                    "build",
                    "native",
                    "include")),
        };
        return candidates.FirstOrDefault(Directory.Exists)
            ?? string.Empty;
    }

    private static ProtocTool Validate(
        string compilerPath,
        string? includePath)
    {
        if (!Path.IsPathFullyQualified(compilerPath)
            || !File.Exists(compilerPath)
            || string.IsNullOrWhiteSpace(includePath)
            || !Path.IsPathFullyQualified(includePath)
            || !Directory.Exists(includePath))
        {
            throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.ToolUnavailable,
                "The configured protoc compiler or include path is unavailable.");
        }
        return new ProtocTool(
            Path.GetFullPath(compilerPath),
            Path.GetFullPath(includePath));
    }
}
