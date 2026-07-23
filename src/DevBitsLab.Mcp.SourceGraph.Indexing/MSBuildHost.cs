using Microsoft.Build.Locator;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

public static class MSBuildHost
{
    private static readonly object _gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (_gate)
        {
            if (_registered) return;
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
            _registered = true;
        }
    }
}
