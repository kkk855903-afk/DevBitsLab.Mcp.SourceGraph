using System;
using System.Windows.Input;

namespace SampleWpf.ViewModels;

/// <summary>
/// Bound by <c>Views/MainWindow.xaml</c>; exposes <see cref="User"/> for the binding path.
/// </summary>
public class MainViewModel
{
    public User User { get; set; } = new();

    public ICommand SaveCommand { get; } = new RelayCommand();

    private sealed class RelayCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}

public class User
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
