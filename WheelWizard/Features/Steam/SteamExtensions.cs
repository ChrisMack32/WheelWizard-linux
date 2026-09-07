namespace WheelWizard.Steam;

public static class SteamExtensions
{
    public static IServiceCollection AddSteamLibrary(this IServiceCollection services)
    {
        services
            .AddHttpClient(SteamLibraryService.HttpClientName)
            .ConfigureHttpClient((serviceProvider, client) => client.ConfigureWheelWizardClient(serviceProvider));

        services.AddSingleton<ISteamLibraryService, SteamLibraryService>();
        return services;
    }
}
