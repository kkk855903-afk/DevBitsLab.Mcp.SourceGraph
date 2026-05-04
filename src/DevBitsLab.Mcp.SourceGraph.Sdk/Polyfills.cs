// netstandard2.0 polyfill so this assembly can use C# 9+ records (`init` setters require
// IsExternalInit). Defining the type as internal keeps it out of the public surface — when
// a third-party plugin built against this SDK is loaded into the net10 host, the host's runtime
// already provides `IsExternalInit`, so this internal copy doesn't escape the SDK assembly.

#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}

#endif
