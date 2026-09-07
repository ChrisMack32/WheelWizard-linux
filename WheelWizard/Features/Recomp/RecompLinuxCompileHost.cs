using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>
/// How Linux actually compiles WiiCompiled. The official AppImage toolchain's linker needs
/// Ubuntu's <c>libxml2.so.2</c> and GCC runtime, which SteamOS does not ship. Distrobox is the
/// same environment <c>update-all.sh</c> already uses.
/// </summary>
public static class RecompLinuxCompileHost
{
    public const string DistroboxExecutable = "/usr/bin/distrobox";
    public const string ContainerName = "wiicompiled";
    public const string ContainerImage = "docker.io/library/ubuntu:24.04";

    public static bool DistroboxIsAvailable => File.Exists(DistroboxExecutable);

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
