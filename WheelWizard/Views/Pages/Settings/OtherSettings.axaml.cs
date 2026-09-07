using Avalonia.Interactivity;
using WheelWizard.CustomDistributions;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Shared.DependencyInjection;
using WheelWizard.Shared.MessageTranslations;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Views.Pages.Settings;

public partial class OtherSettings : UserControlBase
{
    private readonly bool _settingsAreDisabled;

    [Inject]
    private ICustomDistributionSingletonService CustomDistributionSingletonService { get; set; } = null!;

    [Inject]
    private ISettingsManager SettingsService { get; set; } = null!;

    [Inject]
    private IRecompInstallService? RecompInstallService { get; set; }

    public OtherSettings()
    {
        InitializeComponent();
        var recompMode = SettingsService.IsRecompModeActive();
        _settingsAreDisabled = !recompMode && !SettingsService.DolphinPathsSetupCorrectly();
        DisabledWarningText.IsVisible = _settingsAreDisabled;

        // Recomp can be enabled with only a game image configured. Disable the Dolphin-only
        // controls individually so the recomp switch never becomes trapped behind Dolphin setup.
        LaunchRrOnStartup.IsEnabled = !_settingsAreDisabled;
        DolphinReinstallButton.IsEnabled = !_settingsAreDisabled;
        if (recompMode)
        {
            DolphinReinstallButton.Text = t("action.check_updates");
            DolphinReinstallButton.Variant = Views.Components.Button.ButtonsVariantType.Default;
        }
        OpenGameFolderButton.IsEnabled = !_settingsAreDisabled && RetroRewindFolderExists();
        OpenSaveFolderButton.IsEnabled = !_settingsAreDisabled && SaveFolderExists();
        if (!_settingsAreDisabled)
            LoadSettings();
        ForceLoadSettings();
        RefreshRetroRewindVersion();

        // Attach event handlers after loading settings to avoid unwanted triggers
        LaunchRrOnStartup.IsCheckedChanged += ClickLaunchRrOnStartup;
        EnableRecomp.IsCheckedChanged += ClickEnableRecomp;
    }

    private void LoadSettings()
    {
        // Only loads when the settings are not disabled (aka when the paths are set up correctly)
        LaunchRrOnStartup.IsChecked = SettingsService.Get<bool>(SettingsService.LAUNCH_RR_ON_STARTUP);
        OpenGameFolderButton.IsEnabled = RetroRewindFolderExists();
        OpenSaveFolderButton.IsEnabled = SaveFolderExists();
    }

    private void ForceLoadSettings()
    {
        // Always loads

        var recompSupported = RecompLinuxPaths.IsSupported;
        RecompSectionLabel.IsVisible = recompSupported;
        RecompBorder.IsVisible = recompSupported;
        if (recompSupported)
            EnableRecomp.IsChecked = SettingsService.Get<bool>(SettingsService.ENABLE_RECOMP);
    }

    private void RefreshRetroRewindVersion()
    {
        var version =
            RecompLinuxPaths.FindRetroRewindVersion()
            ?? CustomDistributionSingletonService.RetroRewind.GetCurrentVersion()?.ToString()
            ?? t("state.unknown");
        RetroRewindVersionText.Text = SettingsService.IsRecompModeActive()
            ? $"{t("helper_text.installed_version", version)} {t("helper_text.recomp_manual_update")}"
            : t("helper_text.installed_version", version);
    }

    private static string? RetroRewindFolderPath() =>
        RecompLinuxPaths.FindRetroRewind6()
        ?? (Directory.Exists(PathManager.RiivolutionWhWzFolderPath) ? PathManager.RiivolutionWhWzFolderPath : null);

    private static string? SaveFolderPath()
    {
        var recompNand = PathManager.RecompPrivateNandFolderPath;
        if (Directory.Exists(recompNand))
            return recompNand;
        return Directory.Exists(PathManager.SaveFolderPath) ? PathManager.SaveFolderPath : null;
    }

    private static bool RetroRewindFolderExists() => RetroRewindFolderPath() is not null;

    private static bool SaveFolderExists() => SaveFolderPath() is not null;

    private void ClickLaunchRrOnStartup(object? sender, RoutedEventArgs e)
    {
        SettingsService.Set(SettingsService.LAUNCH_RR_ON_STARTUP, LaunchRrOnStartup.IsChecked == true);
    }

    private void ClickEnableRecomp(object? sender, RoutedEventArgs e)
    {
        SettingsService.Set(SettingsService.ENABLE_RECOMP, EnableRecomp.IsChecked == true);
    }

    private async void Reinstall_RetroRewind(object sender, RoutedEventArgs e)
    {
        if (SettingsService.IsRecompModeActive() && RecompInstallService is not null)
        {
            await UpdateNativeInstallAsync();
            RefreshRetroRewindVersion();
            return;
        }

        var progressWindow = new ProgressWindow();
        progressWindow.Show();
        await CustomDistributionSingletonService.RetroRewind.ReinstallAsync(progressWindow);
        progressWindow.Close();
        RefreshRetroRewindVersion();
    }

    private async Task UpdateNativeInstallAsync()
    {
        var goal = t("progress.updating_recomp_and_rr");
        var progressWindow = new ProgressWindow(goal).SetGoal(goal).SetExtraText(t("progress.this_may_take_a_while"));
        var progress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(update.Percent);
        });

        progressWindow.Show();
        try
        {
            var result = await RecompInstallService!.InstallAsync(progress);
            if (result.IsFailure)
                MessageTranslationHelper.ShowMessage(result.Error);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private void OpenSaveFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var folder = SaveFolderPath();
        if (folder is not null)
            FilePickerHelper.OpenFolderInFileManager(folder);
    }

    private void GameFileFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = RetroRewindFolderPath();
        if (folder is not null)
            FilePickerHelper.OpenFolderInFileManager(folder);
    }
}
