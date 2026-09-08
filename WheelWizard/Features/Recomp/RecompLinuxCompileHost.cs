using System.Runtime.InteropServices;
using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>
/// How Linux actually compiles WiiCompiled. The official AppImage already has clang and ninja;
/// the linker still wants Ubuntu's <c>libxml2.so.2</c>, and immutable hosts have no GCC runtime.
/// Distrobox is the compile environment on SteamOS and other read-only distros.
/// </summary>
public static class RecompLinuxCompileHost
{
    public const string DistroboxExecutable = "/usr/bin/distrobox";
    public const string ContainerName = "wiicompiled";
    public const string ContainerImage = "docker.io/library/ubuntu:24.04";

    public static bool DistroboxIsAvailable => FindDistroboxExecutable() is not null;

    public static IReadOnlyList<string> DistroboxSearchPaths()
    {
        var paths = new List<string> { DistroboxExecutable, "/usr/local/bin/distrobox" };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            paths.Add(Path.Combine(home, ".local", "bin", "distrobox"));

        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
            return paths;

        foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            paths.Add(Path.Combine(directory, "distrobox"));

        return paths;
    }

    public static string? FindDistroboxExecutable(Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return DistroboxSearchPaths().FirstOrDefault(fileExists);
    }

    public static string UserInstallBinDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Path.Combine(".local", "bin") : Path.Combine(home, ".local", "bin");
    }

    public const string DistroboxReleaseArchiveUrl = "https://github.com/89luca89/distrobox/archive/refs/tags/1.8.2.5.tar.gz";
    public const string PodmanLauncherVersion = "v0.0.5";
    public const string SteamOsReadonlyExecutable = "/usr/bin/steamos-readonly";
    public const string OstreeBootedPath = "/run/ostree-booted";
    public const string OsReleasePath = "/etc/os-release";

    public static bool IsDistroboxScript(string fileName)
    {
        return fileName.Equals("distrobox", StringComparison.Ordinal)
            || (fileName.StartsWith("distrobox-", StringComparison.Ordinal) && !fileName.Contains('.', StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> ContainerRuntimeSearchPaths()
    {
        var names = new[] { "podman", "docker" };
        var paths = new List<string>();
        foreach (var name in names)
        {
            paths.Add($"/usr/bin/{name}");
            paths.Add($"/usr/local/bin/{name}");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var name in names)
                paths.Add(Path.Combine(home, ".local", "bin", name));
        }

        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in names)
                    paths.Add(Path.Combine(directory, name));
            }
        }

        return paths;
    }

    public static string? FindContainerRuntime(Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return ContainerRuntimeSearchPaths().FirstOrDefault(fileExists);
    }

    public static bool CanUseDistrobox(Func<string, bool>? fileExists = null) =>
        FindDistroboxExecutable(fileExists) is not null && FindContainerRuntime(fileExists) is not null;

    public static IReadOnlyList<string> ArchiveToolSearchPaths(string name)
    {
        var paths = new List<string> { $"/usr/bin/{name}", $"/usr/local/bin/{name}", $"/bin/{name}" };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            paths.Add(Path.Combine(home, ".local", "bin", name));

        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
            return paths;

        foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            paths.Add(Path.Combine(directory, name));

        return paths;
    }

    public static string? FindBsdtarExecutable(Func<string, bool>? fileExists = null) => FindToolExecutable("bsdtar", fileExists);

    public static string? FindTarExecutable(Func<string, bool>? fileExists = null) => FindToolExecutable("tar", fileExists);

    public static string? FindArExecutable(Func<string, bool>? fileExists = null) => FindToolExecutable("ar", fileExists);

    public static string? FindTarExtractor(Func<string, bool>? fileExists = null) =>
        FindBsdtarExecutable(fileExists) ?? FindTarExecutable(fileExists);

    public static bool IsDiscImageFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("nodtool", StringComparison.OrdinalIgnoreCase)
            || message.Contains("disc format", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not read this disc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStaleReleaseCacheFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("only supports Release builds", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CMakeCacheIsRelease(string cacheText)
    {
        foreach (var rawLine in cacheText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("CMAKE_BUILD_TYPE", StringComparison.Ordinal) && line.EndsWith("=Release", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? FindToolExecutable(string name, Func<string, bool>? fileExists)
    {
        fileExists ??= File.Exists;
        return ArchiveToolSearchPaths(name).FirstOrDefault(fileExists);
    }

    public static Dictionary<string, string> UserToolEnvironment()
    {
        var bin = UserInstallBinDirectory();
        var path = Environment.GetEnvironmentVariable("PATH");
        return new Dictionary<string, string>
        {
            ["PATH"] = string.IsNullOrWhiteSpace(path) ? bin : $"{bin}{Path.PathSeparator}{path}",
            ["CMAKE_BUILD_TYPE"] = "Release",
        };
    }

    public static Dictionary<string, string> ParseOsRelease(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
                continue;

            var separator = line.IndexOf('=');
            var key = line[..separator];
            var value = line[(separator + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        return values;
    }

    public static bool OsReleaseIsImmutable(IReadOnlyDictionary<string, string> osRelease)
    {
        if (osRelease.TryGetValue("ID", out var id) && id.Equals("steamos", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!osRelease.TryGetValue("VARIANT_ID", out var variant))
            return false;

        return variant.Contains("steamdeck", StringComparison.OrdinalIgnoreCase)
            || variant.Contains("silverblue", StringComparison.OrdinalIgnoreCase)
            || variant.Contains("kinoite", StringComparison.OrdinalIgnoreCase)
            || variant.Contains("sericea", StringComparison.OrdinalIgnoreCase)
            || variant.Contains("onyx", StringComparison.OrdinalIgnoreCase)
            || variant.Contains("iot", StringComparison.OrdinalIgnoreCase)
            || variant.Contains("coreos", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsImmutableLinux(Func<string, bool>? fileExists = null, IReadOnlyDictionary<string, string>? osRelease = null)
    {
        fileExists ??= File.Exists;
        if (fileExists(SteamOsReadonlyExecutable) || fileExists(OstreeBootedPath))
            return true;

        return OsReleaseIsImmutable(osRelease ?? ReadOsReleaseKeys(fileExists));
    }

    public static bool IsSteamOs(Func<string, bool>? fileExists = null, IReadOnlyDictionary<string, string>? osRelease = null)
    {
        fileExists ??= File.Exists;
        if (fileExists(SteamOsReadonlyExecutable))
            return true;

        osRelease ??= ReadOsReleaseKeys(fileExists);
        return osRelease.TryGetValue("ID", out var id) && id.Equals("steamos", StringComparison.OrdinalIgnoreCase);
    }

    public static string? PodmanLauncherDownloadUrl(Architecture? architecture = null)
    {
        architecture ??= RuntimeInformation.ProcessArchitecture;
        return architecture switch
        {
            Architecture.X64 =>
                $"https://github.com/89luca89/podman-launcher/releases/download/{PodmanLauncherVersion}/podman-launcher-amd64",
            Architecture.Arm64 =>
                $"https://github.com/89luca89/podman-launcher/releases/download/{PodmanLauncherVersion}/podman-launcher-arm64",
            _ => null,
        };
    }

    public static IReadOnlyList<string> TerminalSearchPaths()
    {
        var names = new[] { "konsole", "xdg-terminal-exec", "gnome-terminal", "kgx", "xfce4-terminal", "xterm" };
        var paths = new List<string>();
        foreach (var name in names)
        {
            paths.Add($"/usr/bin/{name}");
            paths.Add($"/usr/local/bin/{name}");
        }

        return paths;
    }

    public static string? FindTerminalExecutable(Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return TerminalSearchPaths().FirstOrDefault(fileExists);
    }

    public static IReadOnlyList<string> BuildTerminalRunArguments(string terminalPath, string scriptPath)
    {
        var name = Path.GetFileName(terminalPath);
        return name switch
        {
            "gnome-terminal" or "kgx" => ["--", "/bin/bash", scriptPath],
            "xdg-terminal-exec" => ["/bin/bash", scriptPath],
            _ => ["-e", "/bin/bash", scriptPath],
        };
    }

    public static string SteamOsDistroboxInstallScript() =>
        """
            set -euo pipefail
            echo
            echo "Wheel Wizard needs Distrobox and Podman to compile WiiCompiled."
            echo "SteamOS is read-only, so this unlocks it briefly, installs those tools, then locks it again."
            echo "Enter your password if asked."
            echo

            restore_readonly() {
              sudo steamos-readonly enable || true
            }
            trap restore_readonly EXIT

            sudo steamos-readonly disable
            if ! sudo pacman -S --noconfirm --needed distrobox podman; then
              echo "Refreshing SteamOS package keys..."
              sudo pacman-key --init
              sudo pacman-key --populate
              sudo pacman -Sy --noconfirm --needed distrobox podman
            fi

            echo
            echo "Distrobox is installed. You can close this window."
            """;

    private static Dictionary<string, string> ReadOsReleaseKeys(Func<string, bool> fileExists)
    {
        if (!fileExists(OsReleasePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return ParseOsRelease(File.ReadAllText(OsReleasePath));
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string AppRunPath(string root) => Path.Combine(root, "setup", "AppRun");

    public static bool HasExtractedSetup(string root) => File.Exists(AppRunPath(root));

    public static IReadOnlyList<string> BuildAppRunInstallArguments(
        string gameFilePath,
        string playFolderPath,
        string retroRewindFolderPath,
        RecompRetroWfcPayloadMode payloadMode = RecompRetroWfcPayloadMode.Download
    )
    {
        var payload = payloadMode == RecompRetroWfcPayloadMode.Skip ? "--skip-retro-wfc-payload" : "--download-retro-wfc-payload";
        return
        [
            "install",
            "--game",
            gameFilePath,
            "--install-dir",
            playFolderPath,
            "--retro-dir",
            retroRewindFolderPath,
            payload,
            "--progress-json",
        ];
    }

    public static IReadOnlyList<string> BuildDistroboxEnterArguments(string executable, IReadOnlyList<string> executableArguments)
    {
        var arguments = new List<string> { "enter", ContainerName, "--", executable };
        arguments.AddRange(executableArguments);
        return arguments;
    }

    /// <summary>
    /// Distrobox always mounts <c>$HOME</c>. Game files and the install tree often live on
    /// <c>/mnt</c> or <c>/run/media</c>, so those roots have to be added at create time.
    /// </summary>
    public static IReadOnlyList<string> HostBindRoots(params string?[] paths)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                continue;
            }

            if (
                !string.IsNullOrWhiteSpace(home)
                && (
                    full.Equals(home, StringComparison.Ordinal)
                    || full.StartsWith(home.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                )
            )
            {
                continue;
            }

            var parts = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var depth = parts[0] is "mnt" or "run" or "media" ? Math.Min(2, parts.Length) : 1;
            roots.Add("/" + string.Join("/", parts.Take(depth)));
        }

        return roots.OrderBy(root => root, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> BuildCreateArguments(IEnumerable<string>? extraVolumes = null)
    {
        var arguments = new List<string> { "create", "--name", ContainerName, "--image", ContainerImage, "--yes" };
        if (extraVolumes is null)
            return arguments;

        foreach (var volume in extraVolumes.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(volume))
                continue;

            arguments.Add("--volume");
            arguments.Add($"{volume}:{volume}:rw");
        }

        return arguments;
    }

    public static IReadOnlyList<string> BuildEnsureCompilerArguments() =>
        [
            "enter",
            ContainerName,
            "--",
            "bash",
            "-lc",
            "if command -v gcc >/dev/null && ldconfig -p | grep -q libxml2.so.2; then exit 0; fi; sudo DEBIAN_FRONTEND=noninteractive apt-get update && sudo DEBIAN_FRONTEND=noninteractive apt-get install -y gcc libxml2",
        ];

    public static bool ListOutputHasContainer(string listOutput)
    {
        foreach (var line in listOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains(ContainerName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
