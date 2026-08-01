using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

internal sealed record ProcessLifetimeOptions(
    TimeSpan IdleTimeout,
    TimeSpan ShutdownGracePeriod,
    TimeSpan IdleCheckInterval);

/// <summary>
/// Tracks user-visible MCP work separately from protocol keep-alives. The timestamp is monotonic,
/// so wall-clock changes cannot accidentally extend or shorten a server session.
/// </summary>
internal sealed class ProcessActivityTracker
{
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private long _lastActivity;
    private int _activeRequests;

    public ProcessActivityTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivity = _timeProvider.GetTimestamp();
    }

    public IDisposable BeginActivity()
    {
        lock (_sync)
        {
            _activeRequests++;
            _lastActivity = _timeProvider.GetTimestamp();
        }
        return new ActivityLease(this);
    }

    public bool IsIdleFor(TimeSpan timeout)
    {
        lock (_sync)
        {
            return _activeRequests == 0
                && _timeProvider.GetElapsedTime(_lastActivity) >= timeout;
        }
    }

    private void EndActivity()
    {
        lock (_sync)
        {
            _activeRequests--;
            _lastActivity = _timeProvider.GetTimestamp();
        }
    }

    private sealed class ActivityLease(ProcessActivityTracker owner) : IDisposable
    {
        private ProcessActivityTracker? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndActivity();
    }
}

internal interface IProcessTerminator
{
    void Exit(int exitCode);
}

internal sealed class EnvironmentProcessTerminator : IProcessTerminator
{
    public void Exit(int exitCode) => Environment.Exit(exitCode);
}

/// <summary>
/// Binds the stdio server to its launcher and to meaningful MCP activity without changing the
/// server's one-process-per-session architecture. Stdin EOF remains owned by the MCP SDK.
/// </summary>
internal sealed class ProcessLifetimeManager : BackgroundService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ProcessActivityTracker _activityTracker;
    private readonly ProcessLifetimeOptions _options;
    private readonly IProcessTerminator _terminator;
    private readonly ILogger<ProcessLifetimeManager> _logger;
    private readonly Process? _parentProcess;
    private readonly Lock _shutdownSync = new();
    private bool _shutdownRequested;
    private CancellationTokenRegistration _stoppingRegistration;

    public ProcessLifetimeManager(
        IHostApplicationLifetime applicationLifetime,
        ProcessActivityTracker activityTracker,
        ProcessLifetimeOptions options,
        IProcessTerminator terminator,
        ILogger<ProcessLifetimeManager> logger)
        : this(
            applicationLifetime,
            activityTracker,
            options,
            terminator,
            logger,
            ParentProcessResolver.TryOpenParentProcess())
    {
    }

    internal ProcessLifetimeManager(
        IHostApplicationLifetime applicationLifetime,
        ProcessActivityTracker activityTracker,
        ProcessLifetimeOptions options,
        IProcessTerminator terminator,
        ILogger<ProcessLifetimeManager> logger,
        Process? parentProcess)
    {
        _applicationLifetime = applicationLifetime;
        _activityTracker = activityTracker;
        _options = options;
        _terminator = terminator;
        _logger = logger;
        _parentProcess = parentProcess;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration = _applicationLifetime.ApplicationStopping.Register(
            OnApplicationStopping);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var monitors = new List<Task>(2);
        if (_parentProcess is not null)
        {
            monitors.Add(MonitorParentAsync(_parentProcess, stoppingToken));
        }
        if (_options.IdleTimeout > TimeSpan.Zero)
        {
            monitors.Add(MonitorIdleAsync(stoppingToken));
        }

        if (monitors.Count == 0)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal host shutdown.
            }
            return;
        }

        try
        {
            await Task.WhenAll(monitors).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public override void Dispose()
    {
        _stoppingRegistration.Dispose();
        _parentProcess?.Dispose();
        base.Dispose();
    }

    private async Task MonitorParentAsync(Process parent, CancellationToken stoppingToken)
    {
        try
        {
            await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            RequestShutdown("parent process exited");
        }
        catch (InvalidOperationException)
        {
            // The process cannot be observed on this platform. Idle and stdin lifecycle remain.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access to the parent handle was revoked. Idle and stdin lifecycle remain.
        }
    }

    private async Task MonitorIdleAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.IdleCheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (_activityTracker.IsIdleFor(_options.IdleTimeout))
            {
                RequestShutdown($"no tool call or resource read for {_options.IdleTimeout.TotalMinutes:g} minute(s)");
                return;
            }
        }
    }

    private void RequestShutdown(string reason)
    {
        lock (_shutdownSync)
        {
            if (_shutdownRequested) return;
            _shutdownRequested = true;
        }

        _logger.LogInformation("Stopping sourcegraph-mcp: {Reason}", reason);
        _applicationLifetime.StopApplication();
    }

    private void OnApplicationStopping()
    {
        lock (_shutdownSync)
        {
            // Stdin EOF is handled by the MCP SDK and reaches us through ApplicationStopping.
            // Record that external shutdown before parent/idle monitors can race in and call the
            // host's stop entry point a second time.
            _shutdownRequested = true;
        }

        _ = EnforceShutdownDeadlineAsync();
    }

    private async Task EnforceShutdownDeadlineAsync()
    {
        try
        {
            await Task.Delay(_options.ShutdownGracePeriod).ConfigureAwait(false);
            if (!_applicationLifetime.ApplicationStopped.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "sourcegraph-mcp did not stop within {Seconds:g}s; forcing process exit",
                    _options.ShutdownGracePeriod.TotalSeconds);
                _terminator.Exit(0);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Shutdown deadline enforcement failed");
        }
    }
}

internal static class ParentProcessResolver
{
    public static Process? TryOpenParentProcess()
    {
        try
        {
            var parentId = TryGetParentProcessId();
            return parentId is > 0 && parentId != Environment.ProcessId
                ? Process.GetProcessById(parentId.Value)
                : null;
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static int? TryGetParentProcessId()
    {
        if (OperatingSystem.IsWindows()) return TryGetWindowsParentProcessId();
        if (OperatingSystem.IsLinux()) return TryGetLinuxParentProcessId();
        if (OperatingSystem.IsMacOS()) return GetParentProcessIdUnix();
        return null;
    }

    private static int? TryGetWindowsParentProcessId()
    {
        var info = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0,
            ref info,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        return status == 0 ? checked((int)info.InheritedFromUniqueProcessId) : null;
    }

    private static int? TryGetLinuxParentProcessId()
    {
        var stat = File.ReadAllText("/proc/self/stat");
        var commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 4 >= stat.Length) return null;

        // After the closing ')' come the state character and then the parent PID.
        var remainder = stat.AsSpan(commandEnd + 2);
        var firstSpace = remainder.IndexOf(' ');
        if (firstSpace < 0) return null;
        remainder = remainder[(firstSpace + 1)..];
        var secondSpace = remainder.IndexOf(' ');
        var parentText = secondSpace < 0 ? remainder : remainder[..secondSpace];
        return int.TryParse(parentText, out var parentId) ? parentId : null;
    }

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessIdUnix();

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }
}
