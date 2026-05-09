namespace SampleAvalonia.ViewModels;

public class MainViewModel
{
    public User User { get; set; } = new();
}

public class User
{
    public string Name { get; set; } = string.Empty;
}
