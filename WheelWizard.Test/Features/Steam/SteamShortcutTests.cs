using System.Text;
using WheelWizard.Steam;

namespace WheelWizard.Test.Features.Steam;

public class SteamShortcutTests
{
    [Fact]
    public void AppId_MatchesSteamDeckEsDeShortcut()
    {
        var appId = SteamShortcutId.AppId("/home/deck/Emulation/tools/launchers/es-de/es-de.sh", "EmulationStationDE");

        Assert.Equal(0xb7f16262u, appId);
        Assert.Equal(13254483351407951872ul, SteamShortcutId.GridId(appId));
    }

    [Fact]
    public void Crc32_MatchesZlibIeee()
    {
        var payload = Encoding.UTF8.GetBytes("abc");
        Assert.Equal(0x352441c2u, SteamShortcutId.Crc32(payload));
    }

    [Fact]
    public void Vdf_RoundTripsAShortcutAndKeepsUnknownFields()
    {
        var original = new SteamShortcut
        {
            AppId = SteamShortcutId.AppId("/games/play/RetroRewind", "Retro Rewind"),
            AppName = "Retro Rewind",
            Exe = "\"/games/play/RetroRewind\"",
            StartDir = "\"/games/play\"",
            Icon = "/tmp/icon.png",
            LaunchOptions = "",
            AllowDesktopConfig = 1,
            AllowOverlay = 1,
            SortAs = "Retro Rewind",
        };
        original.Tags.Add("Wheel Wizard");
        original.ExtraStrings["CustomNote"] = "keep me";
        original.ExtraInts["CustomFlag"] = 7;

        var parsed = SteamShortcutVdf.Read(SteamShortcutVdf.Write([original]));

        var shortcut = Assert.Single(parsed);
        Assert.Equal(original.AppId, shortcut.AppId);
        Assert.Equal(original.AppName, shortcut.AppName);
        Assert.Equal("/games/play/RetroRewind", shortcut.UnquotedExe);
        Assert.Equal(original.StartDir, shortcut.StartDir);
        Assert.Equal("keep me", shortcut.ExtraStrings["CustomNote"]);
        Assert.Equal(7, shortcut.ExtraInts["CustomFlag"]);
        Assert.Equal(["Wheel Wizard"], shortcut.Tags);
    }

    [Fact]
    public void FindExisting_MatchesTheNativePlayBinaryEvenIfTheFolderMoved()
    {
        var shortcuts = new List<SteamShortcut>
        {
            new() { AppName = "Retro Rewind", Exe = "\"/home/deck/Games/WiiCompiled/play/RetroRewind\"" },
        };

        var found = SteamPaths.FindExisting(shortcuts, "/home/deck/Games/WheelWizard/WiiCompiled/play/RetroRewind");

        Assert.NotNull(found);
        Assert.Equal("/home/deck/Games/WiiCompiled/play/RetroRewind", found.UnquotedExe);
    }

    [Fact]
    public void GridFileNames_UseTheSteamLibraryConvention()
    {
        const uint appId = 0xb7f16262u;
        var gridId = SteamShortcutId.GridId(appId);

        Assert.Equal($"{appId}.png", SteamGridArtwork.GridFileName(SteamGridAssetKind.Landscape, appId));
        Assert.Equal($"{appId}p.png", SteamGridArtwork.GridFileName(SteamGridAssetKind.Portrait, appId));
        Assert.Equal($"{appId}_hero.png", SteamGridArtwork.GridFileName(SteamGridAssetKind.Hero, appId));
        Assert.Equal($"{appId}_logo.png", SteamGridArtwork.GridFileName(SteamGridAssetKind.Logo, appId));
        Assert.Equal($"{appId}_icon.png", SteamGridArtwork.GridFileName(SteamGridAssetKind.Icon, appId));
        Assert.Equal($"{gridId}.png", SteamGridArtwork.LegacyGridFileName(SteamGridAssetKind.Landscape, appId));
        Assert.Equal($"{appId}_icon.png", SteamGridArtwork.AppIconFileName(appId));
        Assert.Equal(new[] { $"{appId}.png", $"{gridId}.png" }, SteamGridArtwork.GridFileNames(SteamGridAssetKind.Landscape, appId));
    }

    [Fact]
    public void Vdf_RoundTripsAnExistingSteamLibraryIfPresent()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".local", "share", "Steam", "userdata", "271834140", "config", "shortcuts.vdf");
        if (!File.Exists(path))
            return;

        var parsed = SteamShortcutVdf.Read(File.ReadAllBytes(path));
        Assert.NotEmpty(parsed);

        var again = SteamShortcutVdf.Read(SteamShortcutVdf.Write(parsed));
        Assert.Equal(
            parsed.Select(shortcut =>
                (
                    shortcut.AppId,
                    shortcut.AppName,
                    shortcut.UnquotedExe,
                    shortcut.StartDir,
                    shortcut.Icon,
                    shortcut.LaunchOptions,
                    shortcut.LastPlayTime,
                    string.Join('|', shortcut.Tags)
                )
            ),
            again.Select(shortcut =>
                (
                    shortcut.AppId,
                    shortcut.AppName,
                    shortcut.UnquotedExe,
                    shortcut.StartDir,
                    shortcut.Icon,
                    shortcut.LaunchOptions,
                    shortcut.LastPlayTime,
                    string.Join('|', shortcut.Tags)
                )
            )
        );
    }
}
