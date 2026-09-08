using System.Text;

namespace WheelWizard.Recomp;

/// <summary>
/// Points the Linux play binary at the extracted disc and Retro Rewind pack.
/// Official AppRun always extracts <c>DATA</c> under <c>~/.local/share/WiiCompiled</c>, but
/// <c>portable.txt</c> next to the app makes the runtime read a different <c>Config.toml</c>.
/// </summary>
public static class RecompLinuxRuntimeConfig
{
    public const string PortableMarkerFileName = "portable.txt";
    public const int PortableSearchDepth = 4;

    public static string? FindPortableRoot(params string?[] startDirectories) => FindPortableRoot(startDirectories, fileExists: null);

    public static string? FindPortableRoot(IEnumerable<string?> startDirectories, Func<string, bool>? fileExists)
    {
        fileExists ??= File.Exists;
        foreach (var start in startDirectories)
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            string current;
            try
            {
                current = Path.GetFullPath(start);
            }
            catch (Exception)
            {
                continue;
            }

            for (var level = 0; level <= PortableSearchDepth; level++)
            {
                if (fileExists(Path.Combine(current, PortableMarkerFileName)))
                    return current;

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                    break;

                current = parent;
            }
        }

        return null;
    }

    public static string RuntimeUserDataFolderPath
    {
        get
        {
            var portable = FindPortableRoot(RecompLinuxPaths.FindPlayWorkingDirectory(), Path.GetDirectoryName(Environment.ProcessPath));
            return portable is null ? RecompLinuxPaths.UserDataFolderPath : Path.Combine(portable, "UserData");
        }
    }

    public static string RuntimeConfigFilePath => Path.Combine(RuntimeUserDataFolderPath, "Config.toml");

    public static string ExtractedDvdDataFolderPath => Path.Combine(RecompLinuxPaths.UserDataFolderPath, "workspace", "Assets", "DATA");

    public static bool IsDvdDataRoot(string? folder, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return false;

        fileExists ??= File.Exists;
        return fileExists(Path.Combine(folder, "sys", "fst.bin"));
    }

    public static bool HasPulsarPack(string? folder, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return false;

        fileExists ??= File.Exists;
        var binaries = Path.Combine(folder, "Binaries");
        return fileExists(Path.Combine(binaries, "Code.pul"))
            && (fileExists(Path.Combine(binaries, "ConfigCT.pul")) || fileExists(Path.Combine(binaries, "ConfigRT.pul")));
    }

    public static string? NormalizeRetroRewind6Folder(string? folder, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        if (HasPulsarPack(folder, fileExists))
            return folder;

        var child = Path.Combine(folder, "RetroRewind6");
        return HasPulsarPack(child, fileExists) ? child : null;
    }

    public static string QuoteTomlString(string text) => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    public static string UpsertTomlSetting(string existingText, string section, string key, string quotedValue)
    {
        var lines = existingText.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var header = $"[{section}]";
        var sectionIndex = lines.FindIndex(line => line.Trim() == header);
        if (sectionIndex == -1)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add(string.Empty);
            lines.Add(header);
            lines.Add($"{key} = {quotedValue}");
            return JoinToml(lines);
        }

        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                break;

            if (!trimmed.StartsWith($"{key}=") && !trimmed.StartsWith($"{key} ="))
                continue;

            lines[i] = $"{key} = {quotedValue}";
            return JoinToml(lines);
        }

        lines.Insert(sectionIndex + 1, $"{key} = {quotedValue}");
        return JoinToml(lines);
    }

    public static bool ApplyPlayPaths(string? retroRewind6Folder)
    {
        try
        {
            var configPath = RuntimeConfigFilePath;
            var configDirectory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(configDirectory))
                Directory.CreateDirectory(configDirectory);

            var text = File.Exists(configPath) ? File.ReadAllText(configPath) : "# WiiCompiled user configuration\n\n[paths]\n";
            var wrote = false;

            var dvd = ExtractedDvdDataCandidates().FirstOrDefault(folder => IsDvdDataRoot(folder));
            if (dvd is not null && !CurrentDvdRootIsUsable(text, configDirectory))
            {
                text = UpsertTomlSetting(text, "paths", "dvd_root", QuoteTomlString(dvd));
                wrote = true;
            }

            var retro = NormalizeRetroRewind6Folder(retroRewind6Folder) ?? FindRetroRewind6NearPortable();
            if (retro is not null)
            {
                text = UpsertTomlSetting(text, "paths", "retro_rewind_root", QuoteTomlString(Path.GetFullPath(retro)));
                wrote = true;
            }

            if (!wrote)
                return false;

            File.WriteAllText(configPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> ExtractedDvdDataCandidates()
    {
        yield return ExtractedDvdDataFolderPath;
        yield return Path.Combine(RuntimeUserDataFolderPath, "workspace", "Assets", "DATA");

        var portable = FindPortableRoot(RecompLinuxPaths.FindPlayWorkingDirectory(), Path.GetDirectoryName(Environment.ProcessPath));
        if (portable is not null)
            yield return Path.Combine(portable, "DATA");

        var userDataParent = Path.GetDirectoryName(RuntimeUserDataFolderPath);
        if (!string.IsNullOrWhiteSpace(userDataParent))
            yield return Path.Combine(userDataParent, "DATA");
    }

    private static string? FindRetroRewind6NearPortable()
    {
        var found = RecompLinuxPaths.FindRetroRewind6();
        if (found is not null)
            return found;

        var portable = FindPortableRoot(RecompLinuxPaths.FindPlayWorkingDirectory(), Path.GetDirectoryName(Environment.ProcessPath));
        if (portable is null)
            return null;

        return new[]
        {
            Path.Combine(portable, "WiiCompiled", "RetroRewind", "RetroRewind6"),
            Path.Combine(portable, "RetroRewind", "RetroRewind6"),
            Path.Combine(portable, "RetroRewind6"),
        }.FirstOrDefault(folder => HasPulsarPack(folder));
    }

    private static bool CurrentDvdRootIsUsable(string configText, string? configDirectory)
    {
        foreach (var rawLine in configText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || (!line.StartsWith("dvd_root=") && !line.StartsWith("dvd_root =")))
                continue;

            var value = line[(line.IndexOf('=') + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");

            if (IsDvdDataRoot(value))
                return true;

            if (!string.IsNullOrWhiteSpace(configDirectory) && !Path.IsPathRooted(value))
                return IsDvdDataRoot(Path.GetFullPath(Path.Combine(configDirectory, value)));

            return false;
        }

        return false;
    }

    private static string JoinToml(IReadOnlyList<string> lines) => string.Join('\n', lines) + "\n";
}
