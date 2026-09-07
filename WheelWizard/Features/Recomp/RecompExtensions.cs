using System.Net.Http.Headers;
using WheelWizard.Shared;

namespace WheelWizard.Recomp;

public static class RecompExtensions
{
    /// <summary>
    /// Registers the Mario Kart Wii recomp frontend.
    /// Windows uses the official setup-host contract. Linux downloads Retro Rewind and the official
    /// WiiCompiled AppImage, then runs that AppImage with its bundled toolchain.
    /// </summary>
    public static IServiceCollection AddRecomp(this IServiceCollection services)
    {
        if (!RecompLinuxPaths.IsSupported)
            return services;

        services
            .AddHttpClient(RecompSetupDownloader.HttpClientName)
            .ConfigureHttpClient(
                (serviceProvider, client) =>
                {
                    client.ConfigureWheelWizardClient(serviceProvider);

                    // GitHub release assets are served as an octet-stream redirect.
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                }
            );

        services.AddSingleton<IRecompDolphinDataService, RecompDolphinDataService>();
        services.AddSingleton<IRecompEnvironment, RecompEnvironment>();
        services.AddSingleton<IRecompProcessRunner, RecompProcessRunner>();
        services.AddSingleton<IRecompSetupDownloader, RecompSetupDownloader>();
        services.AddSingleton<IRecompRetroWfcPayloadProbe, RecompRetroWfcPayloadProbe>();
        services.AddSingleton<IRecompLinuxUpdateChecker, RecompLinuxUpdateChecker>();
        services.AddSingleton<IRecompLinuxNativeInstaller, RecompLinuxNativeInstaller>();
        services.AddSingleton<IRecompInstallService, RecompInstallService>();
        services.AddTransient<RecompLauncher>();

        return services;
    }
}
