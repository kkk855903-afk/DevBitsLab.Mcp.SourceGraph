using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SampleWpfRisksWindows;

public static class AppLifetime
{
    public static event EventHandler? Changed;

    public static void Raise() =>
        Changed?.Invoke(null, EventArgs.Empty);
}

public sealed class Subscriber
{
    public void Attach() =>
        AppLifetime.Changed += OnChanged;

    private void OnChanged(object? sender, EventArgs args)
    {
    }
}

public sealed class View : DispatcherObject
{
    public string Text { get; set; } = string.Empty;
}

public static class Worker
{
    public static void Run(View view) =>
        Task.Run(() => view.Text = "unsafe");
}
