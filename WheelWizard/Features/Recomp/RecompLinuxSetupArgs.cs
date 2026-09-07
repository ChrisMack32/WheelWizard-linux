using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>
/// Linux WiiCompiled-Setup AppImage arguments. The Linux host is a verb-first CLI
/// (<c>install</c>, not <c>--silent</c>) and ships its own clang toolchain inside the AppImage.
/// </summary>
public static class RecompLinuxSetupArgs
{
    public const string AppImageExtractAndRun = "--appimage-extract-and-run";
    public const string AppImageExtractAndRunVariable = "APPIMAGE_EXTRACT_AND_RUN";

    public static IReadOnlyDictionary<string, string> AppImageEnvironment { get; } =
        new Dictionary<string, string> { [AppImageExtractAndRunVariable] = "1" };

    public static IReadOnlyList<string> BuildInstallArguments(
        string gameFilePath,
        string playFolderPath,
        string retroRewindFolderPath,
        RecompRetroWfcPayloadMode payloadMode = RecompRetroWfcPayloadMode.Download
    )
    {
        var payload = payloadMode == RecompRetroWfcPayloadMode.Skip ? "--skip-retro-wfc-payload" : "--download-retro-wfc-payload";

        return
        [
            AppImageExtractAndRun,
            "install",
            "--game",
            gameFilePath,
            "--install-dir",
            playFolderPath,
            "--retro-dir",
            retroRewindFolderPath,
            payload,
            "--progress-json",
        ];
    }

    public static IReadOnlyList<string> BuildVersionArguments() => [AppImageExtractAndRun, "--version"];

    public static bool IsAppImage(string filePath) => filePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase);
}
