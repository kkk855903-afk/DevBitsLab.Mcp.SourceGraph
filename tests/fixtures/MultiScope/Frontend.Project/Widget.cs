namespace MultiScope.Frontend;

/// <summary>UI widget that renders something.</summary>
public sealed class Widget
{
    public string Render(string label) => $"<button>{label}</button>";
}
