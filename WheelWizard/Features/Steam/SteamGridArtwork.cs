namespace WheelWizard.Steam;

/// <summary>
/// Curated SteamGridDB CDN assets for Mario Kart: Retro Rewind.
/// The official API needs a key; these public hashes are enough to decorate a non-Steam shortcut.
/// </summary>
public static class SteamGridArtwork
{
    public const string CdnHost = "https://cdn2.steamgriddb.com";

    public static IReadOnlyList<SteamGridAsset> RetroRewind { get; } =
        [
            new(SteamGridAssetKind.Landscape, $"{CdnHost}/grid/8334e946fee869463cf5bd581dd51f14.png"),
            new(SteamGridAssetKind.Portrait, $"{CdnHost}/grid/2ece86640980dbdc87e91902d0c23542.png"),
            new(SteamGridAssetKind.Logo, $"{CdnHost}/logo/8234593c32bcb14766facaf225ed97a3.png"),
            new(SteamGridAssetKind.Icon, $"{CdnHost}/icon/e210da95dec22e376913d473886f4c22.png"),
            new(SteamGridAssetKind.Hero, $"{CdnHost}/hero/b8c236e96fe743f33c1137d9dd6510c8.png"),
        ];

    /// <summary>
    /// Steam Deck / current Steam reads non-Steam artwork by the 32-bit shortcut appid
    /// (<c>4065074380.png</c>). Older tools also wrote the 64-bit grid id; we emit both.
    /// </summary>
    public static string GridFileName(SteamGridAssetKind kind, uint appId) => FileName(kind, appId.ToString());

    public static string LegacyGridFileName(SteamGridAssetKind kind, uint appId) =>
        FileName(kind, SteamShortcutId.GridId(appId).ToString());

    public static IReadOnlyList<string> GridFileNames(SteamGridAssetKind kind, uint appId) =>
        [GridFileName(kind, appId), LegacyGridFileName(kind, appId)];

    public static string AppIconFileName(uint appId) => $"{appId}_icon.png";

    public static string LogoLayoutFileName(uint appId) => $"{appId}.json";

    public static string LogoLayoutJson { get; } =
        """{"nVersion":1,"logoPosition":{"pinnedPosition":"BottomLeft","nWidthPct":50,"nHeightPct":50}}""";

    private static string FileName(SteamGridAssetKind kind, string stem) =>
        kind switch
        {
            SteamGridAssetKind.Landscape => $"{stem}.png",
            SteamGridAssetKind.Portrait => $"{stem}p.png",
            SteamGridAssetKind.Hero => $"{stem}_hero.png",
            SteamGridAssetKind.Logo => $"{stem}_logo.png",
            SteamGridAssetKind.Icon => $"{stem}_icon.png",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}

public enum SteamGridAssetKind
{
    Landscape,
    Portrait,
    Hero,
    Logo,
    Icon,
}

public sealed record SteamGridAsset(SteamGridAssetKind Kind, string Url);
