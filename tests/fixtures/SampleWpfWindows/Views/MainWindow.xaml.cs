using System.Windows;
using SampleWpfWindows.ViewModels;

namespace SampleWpfWindows.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        // The fixture only needs a real WPF event target for semantic indexing.
    }
}
