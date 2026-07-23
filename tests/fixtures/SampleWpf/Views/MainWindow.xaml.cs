using System;

namespace SampleWpf.Views;

/// <summary>
/// Codebehind for <c>Views/MainWindow.xaml</c>. The partial class declares the <see cref="OnSave"/>
/// handler the XAML's <c>Click="OnSave"</c> event resolves against.
/// </summary>
public partial class MainWindow
{
    public void OnSave(object? sender, EventArgs e)
    {
        // Body deliberately empty — the indexer only needs the symbol to exist for the
        // cross-language `handles-event` edge to land on a real C# method.
    }
}
