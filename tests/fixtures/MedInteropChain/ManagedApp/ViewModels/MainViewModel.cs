using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MedInteropChain.GrpcService;
using MedInteropChain.GrpcService.Generated;

namespace MedInteropChain.ManagedApp.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AlgorithmService _service;
    private int _patientAge = 42;
    private string _status = "Ready";

    public MainViewModel()
        : this(new AlgorithmService(new AlgorithmApi.AlgorithmApiClient()))
    {
    }

    internal MainViewModel(AlgorithmService service)
    {
        _service = service;
        RunAlgorithmCommand = new AsyncRelayCommand(RunAlgorithmAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int PatientAge
    {
        get => _patientAge;
        set => SetField(ref _patientAge, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public ICommand RunAlgorithmCommand { get; }

    private async Task RunAlgorithmAsync()
    {
        var result = await _service.CalculateAsync(PatientAge);
        Status = $"Result: {result}";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
