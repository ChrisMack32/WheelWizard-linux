namespace WheelWizard.Steam;

public sealed class SteamShortcut
{
    public uint AppId { get; set; }
    public string AppName { get; set; } = "";
    public string Exe { get; set; } = "";
    public string StartDir { get; set; } = "";
    public string Icon { get; set; } = "";
    public string ShortcutPath { get; set; } = "";
    public string LaunchOptions { get; set; } = "";
    public int IsHidden { get; set; }
    public int AllowDesktopConfig { get; set; } = 1;
    public int AllowOverlay { get; set; } = 1;
    public int OpenVR { get; set; }
    public int Devkit { get; set; }
    public string DevkitGameID { get; set; } = "";
    public int DevkitOverrideAppID { get; set; }
    public int LastPlayTime { get; set; }
    public string FlatpakAppID { get; set; } = "";
    public string SortAs { get; set; } = "";
    public List<string> Tags { get; } = [];
    public Dictionary<string, string> ExtraStrings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ExtraInts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string UnquotedExe
    {
        get
        {
            var value = Exe.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                return value[1..^1];
            return value;
        }
    }
}
