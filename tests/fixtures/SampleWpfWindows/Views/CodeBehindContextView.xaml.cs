using System.Windows;
using SampleWpfWindows.ViewModels;

namespace SampleWpfWindows.Views;

public partial class CodeBehindContextView : Window
{
    public CodeBehindContextView()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
