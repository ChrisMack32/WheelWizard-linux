using WheelWizard.Services;
using WheelWizard.Settings;

namespace WheelWizard.Recomp;

/// <summary>
/// First-run Linux convenience: this fork is WiiCompiled-first. A brand-new config enables the
/// native launcher, and a dump already sitting in <c>WiiCompiled/rom</c> is pointed at automatically.
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

        if (!File.Exists(PathManager.WheelWizardConfigFilePath))
            settings.Set(settings.ENABLE_RECOMP, true);
    }
}
