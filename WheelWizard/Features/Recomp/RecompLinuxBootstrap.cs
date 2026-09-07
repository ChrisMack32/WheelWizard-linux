using WheelWizard.Services;
using WheelWizard.Settings;

namespace WheelWizard.Recomp;

/// <summary>
/// First-run Linux convenience: if a native WiiCompiled install is already on disk, point Wheel Wizard
/// at that game image and turn on the WiiCompiled launcher so Play does not start on Dolphin.
/// </summary>
public static class RecompLinuxBootstrap
{
    public static void Seed(ISettingsManager settings)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var gameImage = RecompLinuxPaths.FindGameImage();
        var currentGame = settings.Get<string>(settings.GAME_LOCATION);
        if (gameImage is not null)
        {
            var needsGamePath =
                string.IsNullOrWhiteSpace(currentGame)
                || currentGame.StartsWith(RecompLinuxPaths.LegacyGamesRootPath, StringComparison.Ordinal);
            if (needsGamePath)
                settings.Set(settings.GAME_LOCATION, gameImage);
        }

        if (File.Exists(PathManager.WheelWizardConfigFilePath) || !RecompLinuxPaths.HasNativeInstall)
            return;

        settings.Set(settings.ENABLE_RECOMP, true);
    }
}
