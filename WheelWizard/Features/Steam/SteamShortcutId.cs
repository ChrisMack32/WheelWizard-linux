using System.Text;

namespace WheelWizard.Steam;

public static class SteamShortcutId
{
    /// <summary>
    /// Steam stores this 32-bit value as the shortcut <c>appid</c>. It is a CRC-32 of the
    /// unquoted executable path plus the display name, with the high bit set.
    /// </summary>
    public static uint AppId(string exePath, string appName)
    {
        var payload = Encoding.UTF8.GetBytes(exePath + appName);
        return Crc32(payload) | 0x80000000;
    }

    /// <summary>
    /// Filename stem used under <c>userdata/.../config/grid</c> for capsule, hero, logo, and icon art.
    /// </summary>
    public static ulong GridId(uint appId) => ((ulong)appId << 32) | 0x02000000;

    public static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = (uint)-(crc & 1);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        return ~crc;
    }
}
