namespace WheelWizard.Recomp;

/// <summary>
/// SteamOS ships <c>libxml2.so.16</c>. The WiiCompiled AppImage linker still needs
/// <c>libxml2.so.2</c>, so first-time install downloads the matching Ubuntu libraries into cache
/// instead of asking the user to install Distrobox and try again.
/// </summary>
public static class RecompLinuxLinkerLibraries
{
    public const string LibXml2DebUrl = "http://archive.ubuntu.com/ubuntu/pool/main/libx/libxml2/libxml2_2.9.13+dfsg-1ubuntu0.12_amd64.deb";
    public const string Icu70DebUrl = "http://archive.ubuntu.com/ubuntu/pool/main/i/icu/libicu70_70.1-2ubuntu1_amd64.deb";

    public static readonly string[] HostLibXml2Paths =
    [
        "/usr/lib/x86_64-linux-gnu/libxml2.so.2",
        "/lib/x86_64-linux-gnu/libxml2.so.2",
        "/usr/lib64/libxml2.so.2",
        "/usr/lib/libxml2.so.2",
    ];

    public static string LibraryFolder(string cacheFolder) => Path.Combine(cacheFolder, "linker-libs");

    public static bool HasCachedLibXml2(string libraryFolder) => File.Exists(Path.Combine(libraryFolder, "libxml2.so.2"));

    public static bool HostHasLibXml2(Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return HostLibXml2Paths.Any(fileExists);
    }

    public static bool ShouldCopyLibraryFile(string fileName)
    {
        return fileName.StartsWith("libxml2.so.2", StringComparison.Ordinal)
            || fileName.StartsWith("libicuuc.so.70", StringComparison.Ordinal)
            || fileName.StartsWith("libicudata.so.70", StringComparison.Ordinal);
    }

    public static Dictionary<string, string> WithLibraryPath(IReadOnlyDictionary<string, string> basis, string libraryFolder)
    {
        var environment = new Dictionary<string, string>(basis);
        var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(existing) ? libraryFolder : $"{libraryFolder}:{existing}";
        return environment;
    }
}
