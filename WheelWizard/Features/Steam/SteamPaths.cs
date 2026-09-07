using System.IO.Abstractions;

namespace WheelWizard.Steam;

public static class SteamPaths
{
    public static IReadOnlyList<string> FindRoots(IFileSystem fileSystem)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var candidate in RootCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate) || !fileSystem.Directory.Exists(candidate))
                continue;

            var normalized = Normalize(fileSystem, candidate);
            if (!fileSystem.Directory.Exists(fileSystem.Path.Combine(normalized, "userdata")))
                continue;

            if (!seen.Add(normalized))
                continue;

            roots.Add(normalized);
        }

        return roots;
    }

    public static IReadOnlyList<string> FindUserConfigFolders(IFileSystem fileSystem, IEnumerable<string> steamRoots)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in steamRoots)
        {
            var userdata = fileSystem.Path.Combine(root, "userdata");
            if (!fileSystem.Directory.Exists(userdata))
                continue;

            foreach (var userDirectory in fileSystem.Directory.EnumerateDirectories(userdata))
            {
                var name = fileSystem.Path.GetFileName(userDirectory.TrimEnd(fileSystem.Path.DirectorySeparatorChar));
                if (!uint.TryParse(name, out var userId) || userId == 0)
                    continue;

                var config = fileSystem.Path.Combine(userDirectory, "config");
                if (!fileSystem.Directory.Exists(config) || !seen.Add(Normalize(fileSystem, config)))
                    continue;

                folders.Add(config);
            }
        }

        return folders;
    }

    public static bool LooksLikeNativePlayBinary(string exePath)
    {
        var normalized = exePath.Replace('\\', '/');
        return normalized.EndsWith("/play/RetroRewind", StringComparison.Ordinal);
    }

    public static SteamShortcut? FindExisting(IEnumerable<SteamShortcut> shortcuts, string exePath)
    {
        foreach (var shortcut in shortcuts)
        {
            if (string.Equals(shortcut.UnquotedExe, exePath, StringComparison.Ordinal))
                return shortcut;
            if (LooksLikeNativePlayBinary(shortcut.UnquotedExe) && LooksLikeNativePlayBinary(exePath))
                return shortcut;
        }

        return null;
    }

    private static IEnumerable<string> RootCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamDir = Environment.GetEnvironmentVariable("STEAM_DIR");
        if (!string.IsNullOrWhiteSpace(steamDir))
            yield return steamDir;

        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    }

    private static string Normalize(IFileSystem fileSystem, string path)
    {
        try
        {
            return fileSystem.Path.GetFullPath(path).TrimEnd(fileSystem.Path.DirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path;
        }
    }
}
