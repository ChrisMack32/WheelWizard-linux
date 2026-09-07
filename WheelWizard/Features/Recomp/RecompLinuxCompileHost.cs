using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>
/// How Linux actually compiles WiiCompiled. The official AppImage already has clang and ninja;
/// the linker still wants Ubuntu's <c>libxml2.so.2</c>, and some hosts have no GCC runtime.
/// Distrobox is an optional compile environment — never something the user has to set up.
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

    public static IReadOnlyList<string> BuildCreateArguments() => ["create", "--name", ContainerName, "--image", ContainerImage, "--yes"];

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
