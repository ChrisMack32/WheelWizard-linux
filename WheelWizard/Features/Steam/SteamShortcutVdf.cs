using System.Text;

namespace WheelWizard.Steam;

/// <summary>
/// Reads and writes Steam's binary <c>shortcuts.vdf</c> (the non-Steam game list).
/// </summary>
public static class SteamShortcutVdf
{
    private const byte TypeObject = 0x00;
    private const byte TypeString = 0x01;
    private const byte TypeInt32 = 0x02;
    private const byte TypeEnd = 0x08;

    public static List<SteamShortcut> Read(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadByte() != TypeObject || ReadCString(reader) != "shortcuts")
            throw new InvalidDataException("The Steam shortcuts file does not start with a shortcuts object.");

        var shortcuts = new List<SteamShortcut>();
        while (true)
        {
            var type = reader.ReadByte();
            if (type == TypeEnd)
                break;
            if (type != TypeObject)
                throw new InvalidDataException("Unexpected entry in the Steam shortcuts file.");

            ReadCString(reader); // index name ("0", "1", ...)
            shortcuts.Add(ReadShortcut(reader));
        }

        // The file normally ends with a second TypeEnd for the implicit root object.
        while (stream.Position < stream.Length && reader.ReadByte() == TypeEnd) { }

        return shortcuts;
    }

    public static byte[] Write(IReadOnlyList<SteamShortcut> shortcuts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(TypeObject);
        WriteCString(writer, "shortcuts");
        for (var index = 0; index < shortcuts.Count; index++)
        {
            writer.Write(TypeObject);
            WriteCString(writer, index.ToString());
            WriteShortcut(writer, shortcuts[index]);
        }

        writer.Write(TypeEnd);
        writer.Write(TypeEnd);
        return stream.ToArray();
    }

    private static SteamShortcut ReadShortcut(BinaryReader reader)
    {
        var shortcut = new SteamShortcut();
        while (true)
        {
            var type = reader.ReadByte();
            if (type == TypeEnd)
                return shortcut;

            var name = ReadCString(reader);
            switch (type)
            {
                case TypeString:
                    SetString(shortcut, name, ReadCString(reader));
                    break;
                case TypeInt32:
                    SetInt(shortcut, name, reader.ReadInt32());
                    break;
                case TypeObject:
                    if (name.Equals("tags", StringComparison.OrdinalIgnoreCase))
                        ReadTags(reader, shortcut);
                    else
                        SkipObject(reader);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Steam shortcut field type {type}.");
            }
        }
    }

    private static void WriteShortcut(BinaryWriter writer, SteamShortcut shortcut)
    {
        WriteInt(writer, "appid", unchecked((int)shortcut.AppId));
        WriteString(writer, "appname", shortcut.AppName);
        WriteString(writer, "exe", shortcut.Exe);
        WriteString(writer, "StartDir", shortcut.StartDir);
        WriteString(writer, "icon", shortcut.Icon);
        WriteString(writer, "ShortcutPath", shortcut.ShortcutPath);
        WriteString(writer, "LaunchOptions", shortcut.LaunchOptions);
        WriteInt(writer, "IsHidden", shortcut.IsHidden);
        WriteInt(writer, "AllowDesktopConfig", shortcut.AllowDesktopConfig);
        WriteInt(writer, "AllowOverlay", shortcut.AllowOverlay);
        WriteInt(writer, "OpenVR", shortcut.OpenVR);
        WriteInt(writer, "Devkit", shortcut.Devkit);
        WriteString(writer, "DevkitGameID", shortcut.DevkitGameID);
        WriteInt(writer, "DevkitOverrideAppID", shortcut.DevkitOverrideAppID);
        WriteInt(writer, "LastPlayTime", shortcut.LastPlayTime);
        WriteString(writer, "FlatpakAppID", shortcut.FlatpakAppID);
        WriteString(writer, "sortas", shortcut.SortAs);
        foreach (var extra in shortcut.ExtraStrings)
            WriteString(writer, extra.Key, extra.Value);
        foreach (var extra in shortcut.ExtraInts)
            WriteInt(writer, extra.Key, extra.Value);
        writer.Write(TypeObject);
        WriteCString(writer, "tags");
        for (var index = 0; index < shortcut.Tags.Count; index++)
            WriteString(writer, index.ToString(), shortcut.Tags[index]);
        writer.Write(TypeEnd);
        writer.Write(TypeEnd);
    }

    private static void WriteString(BinaryWriter writer, string name, string value)
    {
        writer.Write(TypeString);
        WriteCString(writer, name);
        WriteCString(writer, value);
    }

    private static void WriteInt(BinaryWriter writer, string name, int value)
    {
        writer.Write(TypeInt32);
        WriteCString(writer, name);
        writer.Write(value);
    }

    private static void SetString(SteamShortcut shortcut, string name, string value)
    {
        switch (name.ToLowerInvariant())
        {
            case "appname":
                shortcut.AppName = value;
                break;
            case "exe":
                shortcut.Exe = value;
                break;
            case "startdir":
                shortcut.StartDir = value;
                break;
            case "icon":
                shortcut.Icon = value;
                break;
            case "shortcutpath":
                shortcut.ShortcutPath = value;
                break;
            case "launchoptions":
                shortcut.LaunchOptions = value;
                break;
            case "devkitgameid":
                shortcut.DevkitGameID = value;
                break;
            case "flatpakappid":
                shortcut.FlatpakAppID = value;
                break;
            case "sortas":
                shortcut.SortAs = value;
                break;
            default:
                shortcut.ExtraStrings[name] = value;
                break;
        }
    }

    private static void SetInt(SteamShortcut shortcut, string name, int value)
    {
        switch (name.ToLowerInvariant())
        {
            case "appid":
                shortcut.AppId = unchecked((uint)value);
                break;
            case "ishidden":
                shortcut.IsHidden = value;
                break;
            case "allowdesktopconfig":
                shortcut.AllowDesktopConfig = value;
                break;
            case "allowoverlay":
                shortcut.AllowOverlay = value;
                break;
            case "openvr":
                shortcut.OpenVR = value;
                break;
            case "devkit":
                shortcut.Devkit = value;
                break;
            case "devkitoverrideappid":
                shortcut.DevkitOverrideAppID = value;
                break;
            case "lastplaytime":
                shortcut.LastPlayTime = value;
                break;
            default:
                shortcut.ExtraInts[name] = value;
                break;
        }
    }

    private static void ReadTags(BinaryReader reader, SteamShortcut shortcut)
    {
        while (true)
        {
            var type = reader.ReadByte();
            if (type == TypeEnd)
                return;
            var name = ReadCString(reader);
            if (type == TypeString)
                shortcut.Tags.Add(ReadCString(reader));
            else if (type == TypeInt32)
                reader.ReadInt32();
            else if (type == TypeObject)
                SkipObject(reader);
            else
                throw new InvalidDataException($"Unsupported Steam shortcut tag '{name}'.");
        }
    }

    private static void SkipObject(BinaryReader reader)
    {
        while (true)
        {
            var type = reader.ReadByte();
            if (type == TypeEnd)
                return;
            ReadCString(reader);
            switch (type)
            {
                case TypeString:
                    ReadCString(reader);
                    break;
                case TypeInt32:
                    reader.ReadInt32();
                    break;
                case TypeObject:
                    SkipObject(reader);
                    break;
                default:
                    throw new InvalidDataException("Unsupported nested Steam shortcut field.");
            }
        }
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = reader.ReadByte();
            if (value == 0)
                return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(value);
        }
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }
}
