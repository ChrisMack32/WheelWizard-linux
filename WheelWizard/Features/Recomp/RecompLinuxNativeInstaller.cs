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

        var compileReady = await EnsureCompileHostAsync(progress, cancellationToken);
        if (compileReady.IsFailure)
            return compileReady.Error;

        var installArguments = RecompLinuxCompileHost.BuildAppRunInstallArguments(
            environment.GameFilePath,
            playFolder,
            retroDirResult.Value,
            payloadMode
        );
        var appRun = RecompLinuxCompileHost.AppRunPath(root);
        string fileName;
        IReadOnlyList<string> arguments;
        IReadOnlyDictionary<string, string>? extraEnvironment = null;
        if (RecompLinuxCompileHost.DistroboxIsAvailable)
        {
            fileName = RecompLinuxCompileHost.DistroboxExecutable;
            arguments = RecompLinuxCompileHost.BuildDistroboxEnterArguments(appRun, installArguments);
        }
        else
        {
            fileName = setupResult.Value;
            arguments = RecompLinuxSetupArgs.BuildInstallArguments(environment.GameFilePath, playFolder, retroDirResult.Value, payloadMode);
            extraEnvironment = RecompLinuxSetupArgs.AppImageEnvironment;
        }

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

        if (RecompLinuxPaths.FindPlayExecutable() is null)
            return Fail("The WiiCompiled installer finished, but the Retro Rewind play binary was not created.");

        PublishSetupHost(root, setupResult.Value);
        Report(progress, t("progress.recomp_finished"), 100);
        return Ok();
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

    private async Task<OperationResult> EnsureCompileHostAsync(
        IProgress<RecompInstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        if (!RecompLinuxCompileHost.DistroboxIsAvailable)
            return Ok();

        var listOutput = new System.Text.StringBuilder();
        var listResult = await processRunner.RunAsync(
            RecompLinuxCompileHost.DistroboxExecutable,
            ["list"],
            workingDirectory: null,
            line => listOutput.AppendLine(line),
            extraEnvironment: null,
            cancellationToken
        );
        if (listResult.IsFailure)
            return listResult.Error;

        if (!RecompLinuxCompileHost.ListOutputHasContainer(listOutput.ToString()))
        {
            Report(progress, "Creating the WiiCompiled compile environment", 30);
            var createResult = await processRunner.RunAsync(
                RecompLinuxCompileHost.DistroboxExecutable,
                RecompLinuxCompileHost.BuildCreateArguments(),
                workingDirectory: null,
                onStandardOutputLine: null,
                extraEnvironment: null,
                cancellationToken
            );
            if (createResult.IsFailure)
                return createResult.Error;
            if (createResult.Value != 0)
                return Fail("Could not create the Distrobox environment used to compile WiiCompiled.");
        }

        Report(progress, "Checking the compile environment", 32);
        var packages = await processRunner.RunAsync(
            RecompLinuxCompileHost.DistroboxExecutable,
            RecompLinuxCompileHost.BuildEnsureCompilerArguments(),
            workingDirectory: null,
            onStandardOutputLine: null,
            extraEnvironment: null,
            cancellationToken
        );
        if (packages.IsFailure)
            return packages.Error;
        if (packages.Value != 0)
            return Fail("Could not install the compiler tools inside Distrobox.");

        return Ok();
    }

    private static string ExplainCompileFailure(string message)
    {
        if (message.Contains("libxml2.so.2", StringComparison.OrdinalIgnoreCase))
            return "SteamOS cannot compile WiiCompiled on the host (the AppImage linker needs libxml2.so.2). Restart Wheel Wizard and try again so it can compile inside Distrobox.";

        if (
            message.Contains("local-build.sh", StringComparison.OrdinalIgnoreCase)
            && message.Contains("diagnostics", StringComparison.OrdinalIgnoreCase)
        )
            return "The WiiCompiled compile failed. SteamOS needs Distrobox for this step; Wheel Wizard will use it automatically on the next try.";

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
