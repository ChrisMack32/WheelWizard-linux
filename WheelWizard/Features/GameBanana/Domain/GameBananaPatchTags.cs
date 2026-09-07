namespace WheelWizard.GameBanana.Domain;

/// <summary>
/// GameBanana marks Pulsar-ready MKW mods with a Patch/Patches tag. Those are what WiiCompiled can load.
/// </summary>
public static class GameBananaPatchTags
{
    public static bool UsesPatches(IEnumerable<GameBananaTag>? tags) => tags?.Any(tag => IsPatchesTag(tag.Title)) == true;

    public static bool IsPatchesTag(string? tagTitle)
    {
        var normalizedTitle = tagTitle?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleOnly = normalizedTitle.Split(':', 2)[0].Trim();
        return titleOnly.Equals("patch", StringComparison.OrdinalIgnoreCase)
            || titleOnly.Equals("patches", StringComparison.OrdinalIgnoreCase);
    }
}
