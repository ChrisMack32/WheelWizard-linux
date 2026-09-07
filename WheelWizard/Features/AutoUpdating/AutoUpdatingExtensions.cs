using WheelWizard.AutoUpdating.Platforms;

namespace WheelWizard.AutoUpdating;

public static class AutoUpdatingExtensions
{
    public static IServiceCollection AddAutoUpdating(this IServiceCollection services)
    {
        services.AddSingleton<IAutoUpdaterSingletonService, AutoUpdaterSingletonService>();

        var implementationType = typeof(FallbackUpdatePlatform);
        if (OperatingSystem.IsWindows())
            implementationType = typeof(WindowsUpdatePlatform);
        else if (OperatingSystem.IsLinux())
            implementationType = typeof(LinuxUpdatePlatform);

        services.AddSingleton(typeof(IUpdatePlatform), implementationType);

        return services;
    }
}
