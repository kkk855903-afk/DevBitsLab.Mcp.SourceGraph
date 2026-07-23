using System.Diagnostics;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

internal static class PhysicalPathTestSupport
{
    public static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return TryCreateWindowsJunction(linkPath, targetPath);
        }
        catch (UnauthorizedAccessException)
        {
            return TryCreateWindowsJunction(linkPath, targetPath);
        }
        catch (PlatformNotSupportedException)
        {
            return TryCreateWindowsJunction(linkPath, targetPath);
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return TryCreateWindowsJunction(linkPath, targetPath);
        }
    }

    private static bool TryCreateWindowsJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }
}
