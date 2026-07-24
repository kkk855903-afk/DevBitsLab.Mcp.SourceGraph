using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MedInteropChain.ManagedApp.ViewModels;

public sealed class AsyncRelayCommand(Func<Task> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await execute();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
