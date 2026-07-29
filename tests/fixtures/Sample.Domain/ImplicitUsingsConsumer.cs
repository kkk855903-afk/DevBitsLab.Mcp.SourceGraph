namespace Sample.Domain;

public sealed class ImplicitUsingsConsumer : IDisposable
{
    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(1);

    public Action? Callback { get; init; }

    public void Dispose()
    {
    }
}
