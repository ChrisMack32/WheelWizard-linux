using Microsoft.Extensions.Logging;
using WheelWizard.GitHub;

namespace WheelWizard.Recomp;

public interface IRecompLinuxUpdateChecker
{
    /// <summary>
    /// <see langword="true"/> when Retro Rewind or WiiCompiled has a newer official release,
    /// or when the play binary was compiled against a different <c>Code.pul</c>,
    /// <see langword="false"/> when both look current, and <see langword="null"/> when the check
    /// could not be completed (offline, missing files).
    /// </summary>
    Task<bool?> IsOutOfDateAsync(string? installedWiiCompiledVersion, CancellationToken cancellationToken = default);

    void InvalidateCache();
}

public sealed class RecompLinuxUpdateChecker(
    IHttpClientFactory httpClientFactory,
    IGitHubSingletonService gitHubService,
    ILogger<RecompLinuxUpdateChecker> logger
) : IRecompLinuxUpdateChecker
{
    public const string RetroRewindVersionUrl = "https://update.rwfc.net/RetroRewind/RetroRewindVersion.txt";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromSeconds(60);

    private readonly object _cacheLock = new();
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;
    private bool? _cachedResult;

    public async Task<bool?> IsOutOfDateAsync(string? installedWiiCompiledVersion, CancellationToken cancellationToken = default)
    {
        if (RecompLinuxLocalBuild.NeedsRecompile(RecompLinuxPaths.FindPlayWorkingDirectory(), RecompLinuxPaths.FindRetroRewind6()))
            return true;

        lock (_cacheLock)
        {
            if (DateTimeOffset.UtcNow - _cachedAtUtc < ResultLifetime)
                return _cachedResult;
        }

        var retroRewindOutOfDate = await RetroRewindIsOutOfDateAsync(cancellationToken);
        var wiiCompiledOutOfDate = await WiiCompiledIsOutOfDateAsync(installedWiiCompiledVersion, cancellationToken);

        bool? result =
            retroRewindOutOfDate == true || wiiCompiledOutOfDate == true ? true
            : retroRewindOutOfDate is null && wiiCompiledOutOfDate is null ? null
            : false;

        lock (_cacheLock)
        {
            _cachedAtUtc = DateTimeOffset.UtcNow;
            _cachedResult = result;
        }

        return result;
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedAtUtc = DateTimeOffset.MinValue;
            _cachedResult = null;
        }
    }

    public static string? ReadLatestVersionToken(string versionList)
    {
        string? latest = null;
        using var reader = new StringReader(versionList);
        while (reader.ReadLine() is { } line)
        {
            var token = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is not null && token.Length > 0 && char.IsDigit(token[0]))
                latest = token;
        }

        return latest;
    }

    private async Task<bool?> RetroRewindIsOutOfDateAsync(CancellationToken cancellationToken)
    {
        var installed = RecompLinuxPaths.FindRetroRewindVersion();
        if (installed is null)
            return null;

        try
        {
            using var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(RetroRewindVersionUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var latest = ReadLatestVersionToken(body);
            if (latest is null)
                return null;

            return !string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not check the Retro Rewind version list");
            return null;
        }
    }

    private async Task<bool?> WiiCompiledIsOutOfDateAsync(string? installedVersion, CancellationToken cancellationToken)
    {
        if (!RecompVersion.TryParse(installedVersion, out var installed))
            return null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var releases = await gitHubService.GetReleasesAsync(
                RecompReleaseResolver.RepositoryOwner,
                RecompReleaseResolver.RepositoryName,
                count: 20
            );
            if (releases.IsFailure)
                return null;

            var latest = RecompReleaseResolver.FindLatest(releases.Value);
            if (latest is null)
                return null;

            return latest.Version.ComparePrecedenceTo(installed) > 0;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not check WiiCompiled GitHub releases");
            return null;
        }
    }
}
