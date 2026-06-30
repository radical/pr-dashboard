public static class CiHealthServiceCollectionExtensions
{
    public static IServiceCollection AddCiHealthServices(this IServiceCollection services)
    {
        services.AddSingleton<CiHealthSnapshotStore>();
        services.AddSingleton<CopilotCli>();
        services.AddSingleton<CiTriageRunner>();
        services.AddSingleton<BotShepherdRunner>();
        services.AddSingleton<CiHealthProducer>();
        services.AddHostedService(sp => sp.GetRequiredService<CiHealthProducer>());
        return services;
    }
}
