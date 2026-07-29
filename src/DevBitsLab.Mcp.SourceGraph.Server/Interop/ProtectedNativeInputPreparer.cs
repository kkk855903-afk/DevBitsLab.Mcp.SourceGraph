using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

internal sealed record ProtectedNativeInputPreparation(
    ClangNativeExtractionRequest Request,
    string? FailureCode,
    string? FailureMessage)
{
    public bool IsSuccess => FailureCode is null;
}

/// <summary>
/// Bridges endpoint-protection products that expose plaintext to approved shell readers but
/// protected physical bytes to a native parser. It runs only after the NativeParsing trust gate,
/// reads validated repository files through a fixed PowerShell program, keeps plaintext in
/// memory, and never writes a shadow tree or changes the repository.
/// </summary>
internal static class ProtectedNativeInputPreparer
{
    private const int MaximumVisitedFiles = 20_000;
    private const int MaximumProtectedFiles = 256;
    private const int MaximumLogicalBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> _nativeExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".cc", ".cpp", ".cxx",
            ".h", ".hh", ".hpp", ".hxx",
            ".inc", ".inl",
        };

    private const string ReaderScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $result = [System.Collections.Generic.List[object]]::new()
        $total = 0
        foreach ($path in $request) {
          $bytes = [IO.File]::ReadAllBytes([string]$path)
          $total += $bytes.Length
          if ($total -gt 4194304) { throw 'logical input size limit exceeded' }
          $result.Add([pscustomobject]@{
            path = [string]$path
            contents = [Convert]::ToBase64String($bytes)
          })
        }
        [Console]::Out.Write((ConvertTo-Json -Compress -InputObject $result.ToArray()))
        """;

    public static async Task<ProtectedNativeInputPreparation> PrepareAsync(
        string repositoryRoot,
        ClangNativeExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            return Success(request);
        }

        string root;
        ScopePathPolicy policy;
        try
        {
            root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(repositoryRoot));
            policy = new ScopePathPolicy(root, request.ExcludePatterns);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(
                request,
                "protected-input-root-invalid",
                $"The protected-input root is invalid ({ex.GetType().Name}).");
        }

        var protectedPaths = new List<string>();
        var pending = new Stack<string>();
        var visitedFiles = 0;
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (File.GetAttributes(child).HasFlag(
                            FileAttributes.ReparsePoint)
                        || policy.IsExcludedForDiscovery(child, out _))
                    {
                        continue;
                    }
                    pending.Push(child);
                }
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    if (++visitedFiles > MaximumVisitedFiles)
                    {
                        return Failure(
                            request,
                            "protected-input-scan-limit",
                            $"Protected-input discovery exceeds the {MaximumVisitedFiles}-file limit.");
                    }
                    if (!_nativeExtensions.Contains(Path.GetExtension(path))
                        || policy.IsExcludedForDiscovery(path, out var approved)
                        || approved is null)
                    {
                        continue;
                    }
                    var bytes = File.ReadAllBytes(approved);
                    if (!LooksPhysicalProtected(bytes))
                    {
                        continue;
                    }
                    protectedPaths.Add(approved);
                    if (protectedPaths.Count > MaximumProtectedFiles)
                    {
                        return Failure(
                            request,
                            "protected-input-count-limit",
                            $"Protected native inputs exceed the {MaximumProtectedFiles}-file limit.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Failure(
                request,
                "protected-input-scan-failed",
                $"Protected-input discovery failed ({ex.GetType().Name}).");
        }

        if (protectedPaths.Count == 0)
        {
            return Success(request);
        }
        protectedPaths.Sort(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var powershell = string.IsNullOrWhiteSpace(systemRoot)
            ? string.Empty
            : Path.Join(
                systemRoot,
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
        if (!Path.IsPathFullyQualified(powershell)
            || !File.Exists(powershell))
        {
            return Failure(
                request,
                "protected-input-reader-unavailable",
                "The fixed Windows PowerShell logical reader is unavailable.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(ReaderScript);

        try
        {
            if (!process.Start())
            {
                return Failure(
                    request,
                    "protected-input-reader-start-failed",
                    "The fixed logical reader did not start.");
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(
                cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(
                cancellationToken);
            await process.StandardInput.WriteAsync(
                JsonSerializer.Serialize(protectedPaths));
            await process.StandardInput.DisposeAsync();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return Failure(
                    request,
                    "protected-input-reader-failed",
                    "The fixed logical reader failed"
                    + (string.IsNullOrWhiteSpace(error)
                        ? "."
                        : $" ({Bound(error)})."));
            }

            var payloads = JsonSerializer.Deserialize<LogicalInputPayload[]>(
                    output,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    })
                ?? [];
            if (payloads.Length != protectedPaths.Count)
            {
                return Failure(
                    request,
                    "protected-input-reader-invalid",
                    "The fixed logical reader returned an incomplete result.");
            }
            var logicalInputs = new List<ClangInMemoryInput>(payloads.Length);
            var expected = new HashSet<string>(
                protectedPaths,
                StringComparer.OrdinalIgnoreCase);
            var totalBytes = 0;
            foreach (var payload in payloads)
            {
                if (string.IsNullOrWhiteSpace(payload.Path)
                    || string.IsNullOrWhiteSpace(payload.Contents)
                    || !expected.Remove(Path.GetFullPath(payload.Path)))
                {
                    return Failure(
                        request,
                        "protected-input-reader-invalid",
                        "The fixed logical reader returned an unexpected path.");
                }
                var contents = Convert.FromBase64String(payload.Contents);
                totalBytes = checked(totalBytes + contents.Length);
                if (totalBytes > MaximumLogicalBytes
                    || StartsWithHsKey(contents)
                    || (contents.Contains((byte)0)
                        && !HasUtf16Bom(contents)))
                {
                    return Failure(
                        request,
                        "protected-input-reader-invalid",
                        $"Logical native input `{payload.Path}` remains protected, has invalid NUL bytes, or exceeds the size limit.");
                }
                logicalInputs.Add(new ClangInMemoryInput(
                    Path.GetFullPath(payload.Path),
                    contents));
            }
            return Success(request with { InMemoryInputs = logicalInputs });
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(
                request,
                "protected-input-reader-timeout",
                "The fixed logical reader exceeded its 30-second timeout.");
        }
        catch (Exception ex) when (
            ex is IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or JsonException
                or FormatException
                or OverflowException
                or System.ComponentModel.Win32Exception)
        {
            TryTerminate(process);
            return Failure(
                request,
                "protected-input-reader-failed",
                $"The fixed logical reader failed ({ex.GetType().Name}).");
        }
    }

    private static bool LooksPhysicalProtected(ReadOnlySpan<byte> bytes) =>
        bytes.Contains((byte)0)
        || StartsWithHsKey(bytes);

    private static bool StartsWithHsKey(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 5
            && bytes[0] == (byte)'H'
            && bytes[1] == (byte)'S'
            && bytes[2] == (byte)'K'
            && bytes[3] == (byte)'e'
            && bytes[4] == (byte)'y';

    private static bool HasUtf16Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 2
        && ((bytes[0] == 0xff && bytes[1] == 0xfe)
            || (bytes[0] == 0xfe && bytes[1] == 0xff));

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Bound(string value)
    {
        var normalized = value.Trim().ReplaceLineEndings(" ");
        return normalized.Length <= 256
            ? normalized
            : normalized[..256];
    }

    private static ProtectedNativeInputPreparation Success(
        ClangNativeExtractionRequest request) =>
        new(request, null, null);

    private static ProtectedNativeInputPreparation Failure(
        ClangNativeExtractionRequest request,
        string code,
        string message) =>
        new(request, code, message);

    private sealed record LogicalInputPayload(
        string Path,
        string Contents);
}
