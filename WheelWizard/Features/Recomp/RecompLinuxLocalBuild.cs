using System.Security.Cryptography;
using System.Text.Json;

namespace WheelWizard.Recomp;

/// <summary>
/// Official WiiCompiled records the <c>Code.pul</c> it compiled against in
/// <c>play/local-build.json</c>. Asset-only Retro Rewind updates are visible on the next
/// launch; a new <c>Code.pul</c> has to be compiled again. Linux previously compared only
/// <c>version.txt</c>, so an extract that never finished compiling still looked "latest"
/// while the in-game online prompt kept asking for an update.
/// </summary>
public static class RecompLinuxLocalBuild
{
    public const string FileName = "local-build.json";

    public static bool NeedsRecompile(
        string? playFolder,
        string? retroRewind6Folder,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readAllText = null,
        Func<string, byte[]>? readAllBytes = null
    )
    {
        if (string.IsNullOrWhiteSpace(playFolder) || string.IsNullOrWhiteSpace(retroRewind6Folder))
            return false;

        fileExists ??= File.Exists;
        var codePulPath = Path.Combine(retroRewind6Folder, "Binaries", "Code.pul");
        if (!fileExists(codePulPath))
            return false;

        var localBuildPath = Path.Combine(playFolder, FileName);
        if (!fileExists(localBuildPath))
            return true;

        readAllText ??= File.ReadAllText;
        readAllBytes ??= File.ReadAllBytes;
        try
        {
            var recorded = ReadCodePulSha256(readAllText(localBuildPath));
            if (string.IsNullOrWhiteSpace(recorded))
                return true;

            return !string.Equals(recorded, Sha256Hex(readAllBytes(codePulPath)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static string? ReadCodePulSha256(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("CodePulSha256", StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
