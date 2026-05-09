namespace SampleWpf.ViewModels;

/// <summary>
/// Bound by <c>Views/MainWindow.xaml</c>; exposes <see cref="User"/> for the binding path.
/// </summary>
public class MainViewModel
{
    public User User { get; set; } = new();
}

public class User
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
