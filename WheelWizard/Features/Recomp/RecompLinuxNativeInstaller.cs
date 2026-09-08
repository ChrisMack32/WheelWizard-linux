using System.IO.Abstractions;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WheelWizard.GitHub;
using WheelWizard.Recomp.Domain;
using WheelWizard.Services;

namespace WheelWizard.Recomp;

public interface IRecompLinuxNativeInstaller
{
    Task<OperationResult> InstallOrUpdateAsync(
        RecompRetroWfcPayloadMode payloadMode,
        IProgress<RecompInstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// First-time and update path for Linux: download Retro Rewind and the official WiiCompiled
/// AppImage, then run the AppImage with its bundled toolchain. Users never have to fetch or
/// compile anything themselves.
/// </summary>
public sealed class RecompLinuxNativeInstaller(
    IRecompEnvironment environment,
    IRecompProcessRunner processRunner,
    IRecompSetupDownloader downloader,
    IRecompLinuxUpdateChecker linuxUpdateChecker,
    IGitHubSingletonService gitHubService,
    IHttpClientFactory httpClientFactory,
    IFileSystem fileSystem,
    ILogger<RecompLinuxNativeInstaller> logger
) : IRecompLinuxNativeInstaller
{
    public const string RetroRewindFullZipUrl = Endpoints.RRUrl + "RetroRewind/zip/RetroRewind.zip";

    private static readonly Regex BracketPercent = new(@"^\[\s*(\d+)%\]\s*(.*)$", RegexOptions.Compiled);

    public async Task<OperationResult> InstallOrUpdateAsync(
        RecompRetroWfcPayloadMode payloadMode,
        IProgress<RecompInstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(environment.GameFilePath) || !fileSystem.File.Exists(environment.GameFilePath))
            return Fail("Select your Mario Kart Wii game file in Settings → Location first.");

        var root = RecompLinuxPaths.GamesRootPathIfPresent() ?? RecompLinuxPaths.BundledRootPath;
        var playFolder = fileSystem.Path.Combine(root, "play");
        var retroParent = fileSystem.Path.Combine(root, "RetroRewind");
        fileSystem.Directory.CreateDirectory(root);

        if (RecompLinuxPaths.FindPlayExecutable() is not null)
        {
            var outdated = await linuxUpdateChecker.IsOutOfDateAsync(installedWiiCompiledVersion: null, cancellationToken);
            if (outdated != true)
            {
                Report(progress, t("progress.recomp_finished"), 100);
                return Ok();
            }
        }

        Report(progress, "Preparing Retro Rewind", 5);
        var retroDirResult = await EnsureRetroRewindAsync(retroParent, progress, cancellationToken);
        if (retroDirResult.IsFailure)
            return retroDirResult.Error;

        Report(progress, t("progress.recomp_checking_release"), 20);
        var setupResult = await EnsureExtractedSetupAsync(root, progress, cancellationToken);
        if (setupResult.IsFailure)
            return setupResult.Error;

        await TryInstallDistroboxAsync(progress, cancellationToken);

        var immutableHost = RecompLinuxCompileHost.IsImmutableLinux();
        var extraVolumes = RecompLinuxCompileHost.HostBindRoots(
            environment.GameFilePath,
            playFolder,
            RecompLinuxCompileHost.AppRunPath(root),
            retroDirResult.Value
        );
        var installArguments = RecompLinuxCompileHost.BuildAppRunInstallArguments(
            environment.GameFilePath,
            playFolder,
            retroDirResult.Value,
            payloadMode
        );
        var appRun = RecompLinuxCompileHost.AppRunPath(root);

        if (immutableHost)
        {
            if (!RecompLinuxCompileHost.CanUseDistrobox())
                return Fail(t("message_error.recomp_immutable_distrobox_required"));

            var distroResult = await RunDistroboxCompileAsync(appRun, installArguments, extraVolumes, progress, cancellationToken);
            if (distroResult.IsFailure)
                return distroResult.Error;

            PublishSetupHost(root, setupResult.Value);
            Report(progress, t("progress.recomp_finished"), 100);
            return Ok();
        }

        var hostResult = await RunHostCompileAsync(
            appRun,
            environment.GameFilePath,
            playFolder,
            retroDirResult.Value,
            payloadMode,
            progress,
            cancellationToken
        );
        if (hostResult.IsSuccess && RecompLinuxPaths.FindPlayExecutable() is not null)
        {
            PublishSetupHost(root, setupResult.Value);
            Report(progress, t("progress.recomp_finished"), 100);
            return Ok();
        }

        if (RecompLinuxCompileHost.IsDiscImageFailure(hostResult.Error?.Message))
            return hostResult.Error ?? Fail(t("message_error.recomp_disc_unreadable"));

        logger.LogWarning("Host compile failed; trying Distrobox if available. {Error}", hostResult.Error);

        if (RecompLinuxCompileHost.CanUseDistrobox())
        {
            var distroResult = await RunDistroboxCompileAsync(appRun, installArguments, extraVolumes, progress, cancellationToken);
            if (distroResult.IsSuccess)
            {
                PublishSetupHost(root, setupResult.Value);
                Report(progress, t("progress.recomp_finished"), 100);
                return Ok();
            }

            if (RecompLinuxCompileHost.IsDiscImageFailure(distroResult.Error?.Message))
                return distroResult.Error ?? Fail(t("message_error.recomp_disc_unreadable"));

            return distroResult.Error ?? hostResult.Error ?? Fail(t("message_error.recomp_host_compile_failed"));
        }

        return hostResult.Error ?? Fail(t("message_error.recomp_host_compile_failed"));
    }

    private async Task<OperationResult<string>> EnsureRetroRewindAsync(
        string retroParent,
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var existing = environment.RetroRewindFolderPath;
        if (
            !string.IsNullOrWhiteSpace(existing)
            && HasRetroRewindPack(existing)
            && !await RetroRewindNeedsRefreshAsync(existing, cancellationToken)
        )
            return Ok(existing);

        var destination = fileSystem.Path.Combine(retroParent, "RetroRewind6");
        if (HasRetroRewindPack(destination) && !await RetroRewindNeedsRefreshAsync(destination, cancellationToken))
            return Ok(destination);

        fileSystem.Directory.CreateDirectory(retroParent);
        var zipPath = fileSystem.Path.Combine(environment.CacheFolderPath, "RetroRewind.zip");
        Report(progress, "Downloading Retro Rewind", 8);
        var downloadProgress = new DelegateProgress<int>(percent => Report(progress, "Downloading Retro Rewind", 8 + (percent * 10 / 100)));
        var downloadResult = await downloader.DownloadAsync(RetroRewindFullZipUrl, zipPath, downloadProgress, cancellationToken);
        if (downloadResult.IsFailure)
        {
            var fallback = FindPackUnder(retroParent);
            if (fallback is null && !string.IsNullOrWhiteSpace(existing) && HasRetroRewindPack(existing))
                fallback = existing;

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                logger.LogWarning("Could not download a newer Retro Rewind pack; using {Folder}", fallback);
                return Ok(fallback);
            }

            return downloadResult.Error;
        }

        Report(progress, "Extracting Retro Rewind", 18);
        try
        {
            if (fileSystem.Directory.Exists(destination))
                fileSystem.Directory.Delete(destination, recursive: true);

            ZipFile.ExtractToDirectory(zipPath, retroParent, overwriteFiles: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to extract Retro Rewind");
            return Fail("Could not extract the Retro Rewind pack.");
        }

        var extracted = FindPackUnder(retroParent);
        if (extracted is null)
            return Fail("The Retro Rewind pack is missing Code.pul after download.");

        return Ok(extracted);
    }

    private string? FindPackUnder(string parent)
    {
        if (!fileSystem.Directory.Exists(parent))
            return null;

        var preferred = fileSystem.Path.Combine(parent, "RetroRewind6");
        if (HasRetroRewindPack(preferred))
            return preferred;
        if (HasRetroRewindPack(parent))
            return parent;

        foreach (var directory in fileSystem.Directory.EnumerateDirectories(parent, "*", SearchOption.AllDirectories))
        {
            if (HasRetroRewindPack(directory))
                return directory;
        }

        return null;
    }

    private async Task<bool> RetroRewindNeedsRefreshAsync(string folder, CancellationToken cancellationToken)
    {
        var versionFile = fileSystem.Path.Combine(folder, "version.txt");
        if (!fileSystem.File.Exists(versionFile))
            return false;

        var installed = fileSystem.File.ReadAllText(versionFile).Trim();
        if (string.IsNullOrWhiteSpace(installed))
            return false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var client = httpClientFactory.CreateClient();
            var body = await client.GetStringAsync(RecompLinuxUpdateChecker.RetroRewindVersionUrl, timeout.Token);
            var latest = RecompLinuxUpdateChecker.ReadLatestVersionToken(body);
            return latest is not null && !string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not check whether Retro Rewind needs a refresh");
            return false;
        }
    }

    private bool HasRetroRewindPack(string folder) => fileSystem.File.Exists(fileSystem.Path.Combine(folder, "Binaries", "Code.pul"));

    private async Task<OperationResult<string>> EnsureExtractedSetupAsync(
        string root,
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var appImageResult = await EnsureAppImageAsync(progress, cancellationToken);
        if (appImageResult.IsFailure)
            return appImageResult.Error;

        var appRun = RecompLinuxCompileHost.AppRunPath(root);
        if (
            RecompLinuxCompileHost.HasExtractedSetup(root)
            && fileSystem.File.Exists(appRun)
            && fileSystem.File.GetLastWriteTimeUtc(appRun) >= fileSystem.File.GetLastWriteTimeUtc(appImageResult.Value)
        )
            return Ok(appImageResult.Value);

        Report(progress, "Extracting the WiiCompiled toolchain", 28);
        var extractFolder = fileSystem.Path.Combine(environment.CacheFolderPath, "appimage-extract");
        fileSystem.Directory.CreateDirectory(extractFolder);
        var extractResult = await processRunner.RunAsync(
            appImageResult.Value,
            ["--appimage-extract"],
            extractFolder,
            onStandardOutputLine: null,
            RecompLinuxSetupArgs.AppImageEnvironment,
            cancellationToken
        );
        if (extractResult.IsFailure)
            return extractResult.Error;
        if (extractResult.Value != 0)
            return Fail("Could not extract the WiiCompiled AppImage.");

        var squash = fileSystem.Path.Combine(extractFolder, "squashfs-root");
        if (!fileSystem.Directory.Exists(squash))
            return Fail("The WiiCompiled AppImage did not extract a setup folder.");

        var destination = fileSystem.Path.Combine(root, "setup");
        var rsync = await processRunner.RunAsync(
            "/usr/bin/rsync",
            ["-a", "--delete", squash.TrimEnd('/') + "/", destination.TrimEnd('/') + "/"],
            workingDirectory: null,
            onStandardOutputLine: null,
            extraEnvironment: null,
            cancellationToken
        );
        if (rsync.IsFailure || rsync.Value != 0)
            return Fail("Could not install the extracted WiiCompiled toolchain.");

        if (!RecompLinuxCompileHost.HasExtractedSetup(root))
            return Fail("The extracted WiiCompiled toolchain is missing AppRun.");

        return Ok(appImageResult.Value);
    }

    private async Task<OperationResult> RunHostCompileAsync(
        string appRun,
        string gameFilePath,
        string playFolder,
        string retroRewindFolder,
        RecompRetroWfcPayloadMode payloadMode,
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var linkerLibs = await EnsureLinkerLibrariesAsync(progress, cancellationToken);
        if (linkerLibs.IsFailure)
            return linkerLibs.Error;

        var extraEnvironment = string.IsNullOrWhiteSpace(linkerLibs.Value)
            ? RecompLinuxSetupArgs.AppImageEnvironment
            : RecompLinuxLinkerLibraries.WithLibraryPath(RecompLinuxSetupArgs.AppImageEnvironment, linkerLibs.Value);

        if (RecompLinuxPaths.FindPlayExecutable() is null)
            ClearNativeBuildCache();

        var installArguments = RecompLinuxCompileHost.BuildAppRunInstallArguments(gameFilePath, playFolder, retroRewindFolder, payloadMode);
        var result = await RunCompiledSetupAsync(appRun, installArguments, extraEnvironment, progress, cancellationToken);
        if (result.IsFailure && RecompLinuxCompileHost.IsStaleReleaseCacheFailure(result.Error?.Message))
        {
            logger.LogWarning("WiiCompiled's CMake cache was not a Release build; clearing it and compiling again.");
            ClearNativeBuildCache();
            result = await RunCompiledSetupAsync(appRun, installArguments, extraEnvironment, progress, cancellationToken);
        }

        return result;
    }

    private void ClearNativeBuildCache()
    {
        var folder = RecompLinuxPaths.NativeBuildFolderPath;
        if (!fileSystem.Directory.Exists(folder))
            return;

        try
        {
            fileSystem.Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not clear the unfinished WiiCompiled CMake cache at {Folder}", folder);
        }
    }

    private async Task<OperationResult> RunDistroboxCompileAsync(
        string appRun,
        IReadOnlyList<string> installArguments,
        IReadOnlyList<string> extraVolumes,
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var compileReady = await EnsureCompileHostAsync(extraVolumes, progress, cancellationToken);
        if (compileReady.IsFailure)
            return compileReady.Error;

        var distrobox = RecompLinuxCompileHost.FindDistroboxExecutable();
        if (distrobox is null)
            return Fail(t("message_error.recomp_immutable_distrobox_required"));

        var distroResult = await RunCompiledSetupAsync(
            distrobox,
            RecompLinuxCompileHost.BuildDistroboxEnterArguments(appRun, installArguments),
            RecompLinuxCompileHost.UserToolEnvironment(),
            progress,
            cancellationToken
        );
        if (distroResult.IsSuccess && RecompLinuxPaths.FindPlayExecutable() is not null)
            return Ok();

        if (distroResult.IsFailure)
            return distroResult.Error;

        return Fail("The WiiCompiled installer finished, but the Retro Rewind play binary was not created.");
    }

    private async Task<OperationResult> RunCompiledSetupAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation("Running the Linux WiiCompiled installer: {Setup} {Arguments}", fileName, string.Join(' ', arguments));
        Report(progress, t("progress.recomp_running_setup"), 35);
        var resultHolder = new ResultHolder();
        var runResult = await processRunner.RunAsync(
            fileName,
            arguments,
            fileSystem.Path.GetDirectoryName(fileName),
            line => HandleOutput(line, progress, resultHolder),
            extraEnvironment,
            cancellationToken
        );
        if (runResult.IsFailure)
            return runResult.Error;

        if (resultHolder.Error is not null)
            return Fail(ExplainCompileFailure(resultHolder.Error));

        if (runResult.Value != 0)
        {
            return Fail(
                ExplainCompileFailure(
                    string.IsNullOrWhiteSpace(resultHolder.LastMessage)
                        ? $"The WiiCompiled installer exited with code {runResult.Value}."
                        : resultHolder.LastMessage
                )
            );
        }

        return Ok();
    }

    private async Task TryInstallDistroboxAsync(IProgress<RecompInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (RecompLinuxCompileHost.CanUseDistrobox())
            return;

        Report(progress, t("progress.recomp_preparing_compile"), 29);
        if (RecompLinuxCompileHost.FindDistroboxExecutable() is null)
            await InstallDistroboxScriptsAsync(cancellationToken);

        if (RecompLinuxCompileHost.FindContainerRuntime() is null)
            await InstallPodmanLauncherAsync(cancellationToken);

        if (RecompLinuxCompileHost.CanUseDistrobox())
            return;

        if (RecompLinuxCompileHost.IsSteamOs())
            await InstallSteamOsDistroboxInTerminalAsync(progress, cancellationToken);
    }

    private async Task InstallDistroboxScriptsAsync(CancellationToken cancellationToken)
    {
        var binDirectory = RecompLinuxCompileHost.UserInstallBinDirectory();
        fileSystem.Directory.CreateDirectory(binDirectory);
        var workFolder = fileSystem.Path.Combine(environment.CacheFolderPath, "distrobox-install");
        if (fileSystem.Directory.Exists(workFolder))
            fileSystem.Directory.Delete(workFolder, recursive: true);
        fileSystem.Directory.CreateDirectory(workFolder);

        var archivePath = fileSystem.Path.Combine(workFolder, "distrobox.tar.gz");
        var downloadResult = await downloader.DownloadAsync(
            RecompLinuxCompileHost.DistroboxReleaseArchiveUrl,
            archivePath,
            progress: null,
            cancellationToken
        );
        if (downloadResult.IsFailure)
        {
            logger.LogWarning("Could not download Distrobox.");
            return;
        }

        var extractFolder = fileSystem.Path.Combine(workFolder, "src");
        fileSystem.Directory.CreateDirectory(extractFolder);
        var tar = RecompLinuxCompileHost.FindTarExtractor();
        if (tar is null)
        {
            logger.LogWarning("Could not extract Distrobox because tar/bsdtar is missing.");
            return;
        }

        var extract = await processRunner.RunAsync(
            tar,
            ["-C", extractFolder, "-xf", archivePath],
            workingDirectory: extractFolder,
            onStandardOutputLine: null,
            extraEnvironment: null,
            cancellationToken
        );
        if (extract.IsFailure || extract.Value != 0)
        {
            logger.LogWarning("Could not extract Distrobox.");
            return;
        }

        foreach (var file in fileSystem.Directory.EnumerateFiles(extractFolder, "*", SearchOption.AllDirectories))
        {
            var name = fileSystem.Path.GetFileName(file);
            if (!RecompLinuxCompileHost.IsDistroboxScript(name))
                continue;

            var destination = fileSystem.Path.Combine(binDirectory, name);
            fileSystem.File.Copy(file, destination, overwrite: true);
            TryMarkUnixExecutable(destination);
        }

        if (RecompLinuxCompileHost.FindDistroboxExecutable() is null)
            logger.LogWarning("Distrobox scripts were copied, but the distrobox command is still missing.");
    }

    private async Task InstallPodmanLauncherAsync(CancellationToken cancellationToken)
    {
        var url = RecompLinuxCompileHost.PodmanLauncherDownloadUrl();
        if (url is null)
        {
            logger.LogWarning("No Podman launcher is available for this CPU.");
            return;
        }

        var binDirectory = RecompLinuxCompileHost.UserInstallBinDirectory();
        fileSystem.Directory.CreateDirectory(binDirectory);
        var destination = fileSystem.Path.Combine(binDirectory, "podman");
        var downloadPath = fileSystem.Path.Combine(environment.CacheFolderPath, "podman-launcher");
        var downloadResult = await downloader.DownloadAsync(url, downloadPath, progress: null, cancellationToken);
        if (downloadResult.IsFailure)
        {
            logger.LogWarning("Could not download Podman.");
            return;
        }

        fileSystem.File.Copy(downloadPath, destination, overwrite: true);
        TryMarkUnixExecutable(destination);
    }

    private async Task InstallSteamOsDistroboxInTerminalAsync(
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var terminal = RecompLinuxCompileHost.FindTerminalExecutable();
        if (terminal is null)
        {
            logger.LogWarning("No terminal is available to install Distrobox on SteamOS.");
            return;
        }

        Report(progress, t("progress.recomp_opening_distrobox_terminal"), 30);
        var scriptPath = fileSystem.Path.Combine(environment.CacheFolderPath, "install-steamos-distrobox.sh");
        fileSystem.File.WriteAllText(scriptPath, RecompLinuxCompileHost.SteamOsDistroboxInstallScript());
        TryMarkUnixExecutable(scriptPath);

        var run = await processRunner.RunVisibleAsync(
            terminal,
            RecompLinuxCompileHost.BuildTerminalRunArguments(terminal, scriptPath),
            workingDirectory: null,
            cancellationToken
        );
        if (run.IsFailure || run.Value != 0)
            logger.LogWarning("The SteamOS Distrobox terminal installer did not finish successfully.");
    }

    private static void TryMarkUnixExecutable(string filePath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            var mode = File.GetUnixFileMode(filePath);
            File.SetUnixFileMode(filePath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // Install still proceeds; Distrobox start failure is reported later.
        }
    }

    private async Task<OperationResult> EnsureCompileHostAsync(
        IReadOnlyList<string> extraVolumes,
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var distrobox = RecompLinuxCompileHost.CanUseDistrobox() ? RecompLinuxCompileHost.FindDistroboxExecutable() : null;
        if (distrobox is null)
            return Ok();

        var toolEnvironment = RecompLinuxCompileHost.UserToolEnvironment();
        var listOutput = new System.Text.StringBuilder();
        var listResult = await processRunner.RunAsync(
            distrobox,
            ["list"],
            workingDirectory: null,
            line => listOutput.AppendLine(line),
            toolEnvironment,
            cancellationToken
        );
        if (listResult.IsFailure)
            return listResult.Error;

        if (!RecompLinuxCompileHost.ListOutputHasContainer(listOutput.ToString()))
        {
            Report(progress, "Creating the WiiCompiled compile environment", 30);
            var createResult = await processRunner.RunAsync(
                distrobox,
                RecompLinuxCompileHost.BuildCreateArguments(extraVolumes),
                workingDirectory: null,
                onStandardOutputLine: null,
                toolEnvironment,
                cancellationToken
            );
            if (createResult.IsFailure)
                return createResult.Error;
            if (createResult.Value != 0)
                return Fail("Could not create the Distrobox environment used to compile WiiCompiled.");
        }

        Report(progress, "Checking the compile environment", 32);
        var packages = await processRunner.RunAsync(
            distrobox,
            RecompLinuxCompileHost.BuildEnsureCompilerArguments(),
            workingDirectory: null,
            onStandardOutputLine: null,
            toolEnvironment,
            cancellationToken
        );
        if (packages.IsFailure)
            return packages.Error;
        if (packages.Value != 0)
            return Fail("Could not install the compiler tools inside Distrobox.");

        return Ok();
    }

    private async Task<OperationResult<string>> EnsureLinkerLibrariesAsync(
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        if (RecompLinuxLinkerLibraries.HostHasLibXml2())
            return Ok(string.Empty);

        var libraryFolder = RecompLinuxLinkerLibraries.LibraryFolder(environment.CacheFolderPath);
        if (RecompLinuxLinkerLibraries.HasCachedLibXml2(libraryFolder))
            return Ok(libraryFolder);

        Report(progress, "Downloading SteamOS linker libraries", 33);
        fileSystem.Directory.CreateDirectory(libraryFolder);
        var workFolder = fileSystem.Path.Combine(environment.CacheFolderPath, "linker-libs-extract");
        if (fileSystem.Directory.Exists(workFolder))
            fileSystem.Directory.Delete(workFolder, recursive: true);
        fileSystem.Directory.CreateDirectory(workFolder);

        var packages = new (string Url, string FileName)[]
        {
            (RecompLinuxLinkerLibraries.LibXml2DebUrl, "libxml2.deb"),
            (RecompLinuxLinkerLibraries.Icu70DebUrl, "libicu70.deb"),
        };

        foreach (var (url, fileName) in packages)
        {
            var debPath = fileSystem.Path.Combine(workFolder, fileName);
            var downloadResult = await downloader.DownloadAsync(url, debPath, progress: null, cancellationToken);
            if (downloadResult.IsFailure)
                return Fail(t("message_error.recomp_host_compile_failed"));

            if (!await ExtractDebLibrariesAsync(debPath, libraryFolder, cancellationToken))
                return Fail(t("message_error.recomp_missing_archive_tool"));
        }

        if (!RecompLinuxLinkerLibraries.HasCachedLibXml2(libraryFolder))
            return Fail(t("message_error.recomp_host_compile_failed"));

        return Ok(libraryFolder);
    }

    private async Task<bool> ExtractDebLibrariesAsync(string debPath, string libraryFolder, CancellationToken cancellationToken)
    {
        var extractFolder = fileSystem.Path.Combine(
            fileSystem.Path.GetDirectoryName(debPath)!,
            fileSystem.Path.GetFileNameWithoutExtension(debPath)
        );
        fileSystem.Directory.CreateDirectory(extractFolder);

        if (!await ExtractArchiveAsync(debPath, extractFolder, cancellationToken))
            return false;

        var dataArchive = fileSystem.Directory.EnumerateFiles(extractFolder, "data.tar*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (dataArchive is null)
            return false;

        if (!await ExtractArchiveAsync(dataArchive, extractFolder, cancellationToken))
            return false;

        foreach (var file in fileSystem.Directory.EnumerateFiles(extractFolder, "*", SearchOption.AllDirectories))
        {
            var name = fileSystem.Path.GetFileName(file);
            if (!RecompLinuxLinkerLibraries.ShouldCopyLibraryFile(name))
                continue;

            fileSystem.File.Copy(file, fileSystem.Path.Combine(libraryFolder, name), overwrite: true);
        }

        return true;
    }

    private async Task<bool> ExtractArchiveAsync(string archivePath, string destinationFolder, CancellationToken cancellationToken)
    {
        var bsdtar = RecompLinuxCompileHost.FindBsdtarExecutable();
        if (bsdtar is not null)
        {
            var extract = await processRunner.RunAsync(
                bsdtar,
                ["-C", destinationFolder, "-xf", archivePath],
                workingDirectory: destinationFolder,
                onStandardOutputLine: null,
                extraEnvironment: null,
                cancellationToken
            );
            if (extract.IsSuccess && extract.Value == 0)
                return true;
        }

        if (archivePath.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
        {
            var ar = RecompLinuxCompileHost.FindArExecutable();
            if (ar is null)
                return false;

            var unpackAr = await processRunner.RunAsync(
                ar,
                ["-x", archivePath],
                workingDirectory: destinationFolder,
                onStandardOutputLine: null,
                extraEnvironment: null,
                cancellationToken
            );
            return unpackAr.IsSuccess && unpackAr.Value == 0;
        }

        var tar = RecompLinuxCompileHost.FindTarExecutable();
        if (tar is null)
            return false;

        var unpackTar = await processRunner.RunAsync(
            tar,
            ["-C", destinationFolder, "-xf", archivePath],
            workingDirectory: destinationFolder,
            onStandardOutputLine: null,
            extraEnvironment: null,
            cancellationToken
        );
        return unpackTar.IsSuccess && unpackTar.Value == 0;
    }

    private static string ExplainCompileFailure(string message)
    {
        if (RecompLinuxCompileHost.IsDiscImageFailure(message))
            return t("message_error.recomp_disc_unreadable");

        if (RecompLinuxCompileHost.IsStaleReleaseCacheFailure(message))
            return message;

        if (
            message.Contains("libxml2.so.2", StringComparison.OrdinalIgnoreCase)
            || (
                message.Contains("local-build.sh", StringComparison.OrdinalIgnoreCase)
                && message.Contains("diagnostics", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return t("message_error.recomp_host_compile_failed");
        }

        return message;
    }

    private async Task<OperationResult<string>> EnsureAppImageAsync(
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var releasesResult = await gitHubService.GetReleasesAsync(
            RecompReleaseResolver.RepositoryOwner,
            RecompReleaseResolver.RepositoryName,
            count: 100
        );
        if (releasesResult.IsFailure)
            return Fail("Could not look up the latest WiiCompiled release.");

        var release = RecompReleaseResolver.FindLatest(releasesResult.Value);
        if (release is null)
            return Fail("No Linux WiiCompiled setup AppImage is available to download.");

        var cachedPath = fileSystem.Path.Combine(environment.CacheFolderPath, $"WiiCompiled-Setup-{Sanitize(release.TagName)}.AppImage");
        if (IsUsableFile(cachedPath) && await AppImageReportsVersionAsync(cachedPath, release.TagName, cancellationToken))
            return Ok(cachedPath);

        Report(progress, t("progress.recomp_downloading_setup"), 22);
        var downloadProgress = new DelegateProgress<int>(percent =>
            Report(progress, t("progress.recomp_downloading_setup"), 22 + (percent * 12 / 100))
        );
        var downloadResult = await downloader.DownloadAsync(release.SetupDownloadUrl, cachedPath, downloadProgress, cancellationToken);
        if (downloadResult.IsFailure)
            return downloadResult.Error;

        if (!IsUsableFile(cachedPath))
            return Fail("The downloaded WiiCompiled setup is missing or empty.");

        return Ok(cachedPath);
    }

    private async Task<bool> AppImageReportsVersionAsync(string appImagePath, string expectedTag, CancellationToken cancellationToken)
    {
        if (!RecompVersion.TryParse(expectedTag, out var expected))
            return false;

        string? versionText = null;
        var runResult = await processRunner.RunAsync(
            appImagePath,
            RecompLinuxSetupArgs.BuildVersionArguments(),
            workingDirectory: null,
            line =>
            {
                if (RecompVersion.TryParse(line, out var version))
                    versionText = version.ToString();
            },
            RecompLinuxSetupArgs.AppImageEnvironment,
            cancellationToken
        );

        return runResult.IsSuccess
            && runResult.Value == 0
            && RecompVersion.TryParse(versionText, out var actual)
            && actual.ComparePrecedenceTo(expected) == 0;
    }

    private bool IsUsableFile(string filePath)
    {
        if (!fileSystem.File.Exists(filePath))
            return false;

        using var stream = fileSystem.File.OpenRead(filePath);
        return stream.Length > 0;
    }

    private void PublishSetupHost(string root, string setupPath)
    {
        var published = fileSystem.Path.Combine(root, RecompLinuxPaths.SetupFileName);
        if (string.Equals(setupPath, published, StringComparison.Ordinal))
            return;

        try
        {
            fileSystem.File.Copy(setupPath, published, overwrite: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not copy the WiiCompiled AppImage next to the install");
        }
    }

    private static string Sanitize(string tagName) =>
        new(tagName.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

    private static void HandleOutput(string line, IProgress<RecompInstallProgress>? progress, ResultHolder resultHolder)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var parsed = RecompSetupOutputParser.Parse(line);
        switch (parsed)
        {
            case RecompSetupProgressEvent progressEvent:
                var message = string.IsNullOrWhiteSpace(progressEvent.Message) ? progressEvent.Stage : progressEvent.Message;
                resultHolder.LastMessage = message;
                Report(progress, message, 35 + (progressEvent.Percent * 65 / 100));
                return;
            case RecompSetupResultEvent { Success: false } failed:
                resultHolder.Error = string.IsNullOrWhiteSpace(failed.Error)
                    ? "The WiiCompiled installer reported a failure."
                    : failed.Error;
                return;
        }

        if (
            line.Contains("libxml2.so.2", StringComparison.OrdinalIgnoreCase)
            || line.Contains("local-build.sh: error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error: local-build.sh", StringComparison.OrdinalIgnoreCase)
            || RecompLinuxCompileHost.IsDiscImageFailure(line)
            || RecompLinuxCompileHost.IsStaleReleaseCacheFailure(line)
        )
        {
            resultHolder.LastMessage = line.Trim();
            resultHolder.Error ??= line.Trim();
        }

        var match = BracketPercent.Match(line.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var percent))
            return;

        var text = match.Groups[2].Value;
        resultHolder.LastMessage = text;
        Report(progress, text, 35 + (percent * 65 / 100));
    }

    private static void Report(IProgress<RecompInstallProgress>? progress, string message, int percent) =>
        progress?.Report(new(message, Math.Clamp(percent, 0, 100)));

    private sealed class ResultHolder
    {
        public string? Error { get; set; }
        public string? LastMessage { get; set; }
    }

    private sealed class DelegateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
