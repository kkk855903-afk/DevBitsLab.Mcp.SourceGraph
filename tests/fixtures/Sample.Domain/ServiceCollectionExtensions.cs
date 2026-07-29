namespace Sample.Domain;

public interface IServiceCollection
{
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection UseProgressWaiter(
        this IServiceCollection services) => services;
}

public static class ExtensionConsumers
{
    public static void Configure(IServiceCollection services)
    {
        services.UseProgressWaiter();
    }

    public static void ConfigureAgain(IServiceCollection services)
    {
        services.UseProgressWaiter();
    }
}
