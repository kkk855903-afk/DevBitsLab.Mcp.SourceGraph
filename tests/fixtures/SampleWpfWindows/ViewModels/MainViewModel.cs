using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SampleWpfWindows.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _queryText = string.Empty;
    private string _status = "Ready";

    public MainViewModel()
    {
        RunCommand = new RelayCommand(Run);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (_queryText == value)
            {
                return;
            }

            _queryText = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public ICommand RunCommand { get; }

    private void Run()
    {
        Status = string.IsNullOrWhiteSpace(QueryText)
            ? "No query"
            : "Complete";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
