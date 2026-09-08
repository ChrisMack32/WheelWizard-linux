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
            var dvd = ExtractedDvdDataCandidates().FirstOrDefault(folder => IsDvdDataRoot(folder));
            if (dvd is null)
                return false;

            var configPath = RuntimeConfigFilePath;
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var text = File.Exists(configPath) ? File.ReadAllText(configPath) : "# WiiCompiled user configuration\n\n[paths]\n";
            if (!CurrentDvdRootIsUsable(text))
                text = UpsertTomlSetting(text, "paths", "dvd_root", QuoteTomlString(dvd));

            if (!string.IsNullOrWhiteSpace(retroRewind6Folder) && Directory.Exists(Path.Combine(retroRewind6Folder, "Binaries")))
                text = UpsertTomlSetting(text, "paths", "retro_rewind_root", QuoteTomlString(retroRewind6Folder));

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
    }

    private static bool CurrentDvdRootIsUsable(string configText)
    {
        foreach (var rawLine in configText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || (!line.StartsWith("dvd_root=") && !line.StartsWith("dvd_root =")))
                continue;

            var value = line[(line.IndexOf('=') + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");

            return IsDvdDataRoot(value);
        }

        return false;
    }

    private static string JoinToml(IReadOnlyList<string> lines) => string.Join('\n', lines) + "\n";
}
