using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using WheelWizard.Recomp;

namespace WheelWizard.Steam;

public interface ISteamLibraryService
{
    bool IsSteamAvailable { get; }

    Task<OperationResult<SteamAddGameResult>> AddRetroRewindAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default
    );
}

public sealed record SteamAddGameResult(bool AlreadyPresent, int UserCount, string AppName, string ExePath);

public sealed class SteamLibraryService(IHttpClientFactory httpClientFactory, IFileSystem fileSystem, ILogger<SteamLibraryService> logger)
    : ISteamLibraryService
{
    public const string HttpClientName = "SteamGridDb";
    public const string AppName = "Retro Rewind";

    public bool IsSteamAvailable => SteamPaths.FindRoots(fileSystem).Count > 0;

    public async Task<OperationResult<SteamAddGameResult>> AddRetroRewindAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var exePath = RecompLinuxPaths.FindPlayExecutable();
        if (string.IsNullOrWhiteSpace(exePath) || !fileSystem.File.Exists(exePath))
            return Fail("The Retro Rewind play binary was not found. Install WiiCompiled first.");

        var steamRoots = SteamPaths.FindRoots(fileSystem);
        if (steamRoots.Count == 0)
            return Fail("Steam was not found. Retro Rewind can only be added on a machine with Steam installed.");

        var userConfigs = SteamPaths.FindUserConfigFolders(fileSystem, steamRoots);
        if (userConfigs.Count == 0)
            return Fail("No Steam user data was found.");

        var startDir = fileSystem.Path.GetDirectoryName(exePath) ?? fileSystem.Path.GetFullPath(".");
        var appId = SteamShortcutId.AppId(exePath, AppName);
        var alreadyPresent = false;

        progress?.Report(t("progress.steam_downloading_artwork"));
        var artwork = await DownloadArtworkAsync(cancellationToken);

        foreach (var configFolder in userConfigs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(t("progress.steam_writing_shortcut"));

            var shortcutsPath = fileSystem.Path.Combine(configFolder, "shortcuts.vdf");
            var shortcuts = ReadShortcuts(shortcutsPath);
            var existing = SteamPaths.FindExisting(shortcuts, exePath);
            if (existing is not null)
            {
                alreadyPresent = true;
                appId = existing.AppId != 0 ? existing.AppId : appId;
                UpdateShortcut(existing, exePath, startDir, appId);
            }
            else
            {
                shortcuts.Add(CreateShortcut(exePath, startDir, appId));
            }

            WriteShortcutsAtomically(shortcutsPath, shortcuts);

            var gridFolder = fileSystem.Path.Combine(configFolder, "grid");
            fileSystem.Directory.CreateDirectory(gridFolder);
            CopyArtwork(gridFolder, appId, artwork);

            var iconPath = fileSystem.Path.Combine(gridFolder, SteamGridArtwork.AppIconFileName(appId));
            if (fileSystem.File.Exists(iconPath))
            {
                var shortcut = SteamPaths.FindExisting(shortcuts, exePath);
                if (shortcut is not null)
                {
                    shortcut.Icon = iconPath;
                    WriteShortcutsAtomically(shortcutsPath, shortcuts);
                }
            }
        }

        logger.LogInformation(
            "Added Retro Rewind to Steam for {UserCount} user(s). AlreadyPresent={AlreadyPresent} AppId={AppId}",
            userConfigs.Count,
            alreadyPresent,
            appId
        );

        return Ok(new SteamAddGameResult(alreadyPresent, userConfigs.Count, AppName, exePath));
    }

    private List<SteamShortcut> ReadShortcuts(string shortcutsPath)
    {
        if (!fileSystem.File.Exists(shortcutsPath))
            return [];

        var bytes = fileSystem.File.ReadAllBytes(shortcutsPath);
        return bytes.Length == 0 ? [] : SteamShortcutVdf.Read(bytes);
    }

    private void WriteShortcutsAtomically(string shortcutsPath, IReadOnlyList<SteamShortcut> shortcuts)
    {
        var directory = fileSystem.Path.GetDirectoryName(shortcutsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            fileSystem.Directory.CreateDirectory(directory);

        if (fileSystem.File.Exists(shortcutsPath))
            fileSystem.File.Copy(shortcutsPath, shortcutsPath + ".wheelwizard.bak", overwrite: true);

        var payload = SteamShortcutVdf.Write(shortcuts);
        var tempPath = shortcutsPath + ".tmp";
        fileSystem.File.WriteAllBytes(tempPath, payload);
        fileSystem.File.Move(tempPath, shortcutsPath, overwrite: true);
    }

    private static SteamShortcut CreateShortcut(string exePath, string startDir, uint appId)
    {
        var shortcut = new SteamShortcut
        {
            AppId = appId,
            AppName = AppName,
            Exe = Quote(exePath),
            StartDir = Quote(startDir),
            AllowDesktopConfig = 1,
            AllowOverlay = 1,
            SortAs = AppName,
        };
        shortcut.Tags.Add("Wheel Wizard");
        return shortcut;
    }

    private static void UpdateShortcut(SteamShortcut shortcut, string exePath, string startDir, uint appId)
    {
        shortcut.AppId = appId;
        shortcut.AppName = AppName;
        shortcut.Exe = Quote(exePath);
        shortcut.StartDir = Quote(startDir);
        shortcut.SortAs = AppName;
        if (!shortcut.Tags.Exists(tag => tag.Equals("Wheel Wizard", StringComparison.OrdinalIgnoreCase)))
            shortcut.Tags.Add("Wheel Wizard");
    }

    private static string Quote(string path) => path.StartsWith('"') ? path : $"\"{path}\"";

    private async Task<Dictionary<SteamGridAssetKind, byte[]>> DownloadArtworkAsync(CancellationToken cancellationToken)
    {
        var files = new Dictionary<SteamGridAssetKind, byte[]>();
        var client = httpClientFactory.CreateClient(HttpClientName);
        foreach (var asset in SteamGridArtwork.RetroRewind)
        {
            try
            {
                using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                files[asset.Kind] = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not download SteamGridDB artwork from {Url}", asset.Url);
            }
        }

        return files;
    }

    private void CopyArtwork(string gridFolder, uint appId, IReadOnlyDictionary<SteamGridAssetKind, byte[]> artwork)
    {
        foreach (var (kind, bytes) in artwork)
        {
            if (bytes.Length == 0)
                continue;

            foreach (var name in SteamGridArtwork.GridFileNames(kind, appId))
                fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(gridFolder, name), bytes);
        }

        fileSystem.File.WriteAllText(
            fileSystem.Path.Combine(gridFolder, SteamGridArtwork.LogoLayoutFileName(appId)),
            SteamGridArtwork.LogoLayoutJson
        );
    }
}
