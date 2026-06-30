public static class CiHealthServiceCollectionExtensions
{
    public static IServiceCollection AddCiHealthServices(this IServiceCollection services)
    {
        services.AddSingleton<CiHealthSnapshotStore>();
        services.AddHostedService<CiHealthProducer>();
        return services;
    }
}
