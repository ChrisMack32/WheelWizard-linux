namespace WheelWizard.Recomp;

/// <summary>
/// Well-known Linux locations for an official or Deck-local WiiCompiled install.
/// Wheel Wizard's Windows portable <c>Recomp\Install</c> layout is not what the Linux
/// setup writes, so Play/Update have to find the native binaries themselves.
/// </summary>
public static class RecompLinuxPaths
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static string SetupFileName =>
        OperatingSystem.IsLinux() ? "WiiCompiled-Setup-x86_64.AppImage" : RecompSetupCommandBuilder.WindowsSetupFileName;

    public static string UserDataFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WiiCompiled");

    /// <summary>
    /// WiiCompiled files that ship beside this Wheel Wizard build
    /// (<c>&lt;WheelWizard&gt;/WiiCompiled</c>).
    /// </summary>
    public static string BundledRootPath
    {
        get
        {
            var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(exeDirectory))
                return Path.Combine(exeDirectory, "WiiCompiled");

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "WheelWizard", "WiiCompiled");
        }
    }

    public static string LegacyGamesRootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "WiiCompiled");

    public static string GamesRootPath =>
        GameRootCandidates().FirstOrDefault(root => Directory.Exists(Path.Combine(root, "play"))) ?? BundledRootPath;

    public static string? GamesRootPathIfPresent() => Directory.Exists(Path.Combine(GamesRootPath, "play")) ? GamesRootPath : null;

    public static bool HasNativeInstall => FindPlayExecutable() is not null;

    public static string? FindPlayExecutable()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        foreach (var candidate in PlayExecutableCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? FindPlayWorkingDirectory()
    {
        var executable = FindPlayExecutable();
        return executable is null ? null : Path.GetDirectoryName(executable);
    }

    public static string? FindSetupHost()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        foreach (var candidate in SetupHostCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? FindUpdateScript()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        foreach (var root in GameRootCandidates())
        {
            var script = Path.Combine(root, "update-all.sh");
            if (File.Exists(script))
                return script;
        }

        return null;
    }

    public static string? FindRetroRewind6()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        foreach (var root in GameRootCandidates())
        {
            var folder = Path.Combine(root, "RetroRewind", "RetroRewind6");
            if (Directory.Exists(Path.Combine(folder, "Binaries")))
                return folder;
        }

        return null;
    }

    /// <summary>
    /// Retro Rewind's <c>rksys.dat</c> lives beside the pack, not in Dolphin's Riivolution folder.
    /// </summary>
    public static string? SaveFolderFromRetroRewind6(string? retroRewind6Folder)
    {
        if (string.IsNullOrWhiteSpace(retroRewind6Folder))
            return null;

        var parent = Path.GetDirectoryName(retroRewind6Folder);
        return string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, "riivolution", "save", "RetroWFC");
    }

    public static string? FindSaveFolder() => SaveFolderFromRetroRewind6(FindRetroRewind6());

    public static string? FindGameImage()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        string[] extensions = [".wbfs", ".iso", ".rvz", ".wia", ".gcz", ".ciso", ".gcm"];
        foreach (var root in GameRootCandidates())
        {
            var romFolder = Path.Combine(root, "rom");
            if (!Directory.Exists(romFolder))
                continue;

            var match = Directory
                .EnumerateFiles(romFolder)
                .FirstOrDefault(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    public static string? FindRetroRewindVersion()
    {
        var folder = FindRetroRewind6();
        if (folder is null)
            return null;

        var versionFile = Path.Combine(folder, "version.txt");
        if (!File.Exists(versionFile))
            return null;

        var version = File.ReadAllText(versionFile).Trim();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    public static string? FindOfficialConfigFile()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        var config = Path.Combine(UserDataFolderPath, "Config.toml");
        return File.Exists(config) ? config : null;
    }

    private static IEnumerable<string> GameRootCandidates()
    {
        yield return BundledRootPath;
        if (!string.Equals(BundledRootPath, LegacyGamesRootPath, StringComparison.Ordinal))
            yield return LegacyGamesRootPath;
    }

    private static IEnumerable<string> PlayExecutableCandidates()
    {
        foreach (var root in GameRootCandidates())
            yield return Path.Combine(root, "play", "RetroRewind");

        yield return Path.Combine(UserDataFolderPath, "Install", "RetroRewind", "RetroRewind");
        yield return Path.Combine(UserDataFolderPath, "Install", "play", "RetroRewind");
    }

    private static IEnumerable<string> SetupHostCandidates()
    {
        foreach (var root in GameRootCandidates())
        {
            yield return Path.Combine(root, "setup", "usr", "bin", "wiicompiled-setup");
            yield return Path.Combine(root, SetupFileName);
        }

        yield return Path.Combine(UserDataFolderPath, SetupFileName);
    }
}
