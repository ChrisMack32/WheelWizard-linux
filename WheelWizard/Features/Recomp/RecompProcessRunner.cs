using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WheelWizard.Recomp;

/// <summary>
/// Runs the recomp setup executable. Split out from <see cref="RecompInstallService"/> so the install
/// orchestration can be unit tested without spawning processes.
/// </summary>
public interface IRecompProcessRunner
{
    /// <summary>
    /// Runs a process to completion, forwarding every stdout line to <paramref name="onStandardOutputLine"/>.
    /// Stderr is captured for diagnostics only, since the contract keeps it out of the NDJSON stream.
    /// </summary>
    Task<OperationResult<int>> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        Action<string>? onStandardOutputLine,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Same as the string overload, but each argument is passed separately so Linux paths do not
    /// go through Windows quoting. Extra environment variables are merged into the child process.
    /// </summary>
    Task<OperationResult<int>> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string>? onStandardOutputLine,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default
    );
}

/// <inheritdoc />
public sealed class RecompProcessRunner(ILogger<RecompProcessRunner> logger) : IRecompProcessRunner
{
    private const string CancellationEventEnvironmentVariable = "MKWCOMPILED_CANCEL_EVENT";
    private static readonly TimeSpan CancellationGracePeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForcedExitGracePeriod = TimeSpan.FromSeconds(5);

    public Task<OperationResult<int>> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        Action<string>? onStandardOutputLine,
        CancellationToken cancellationToken = default
    ) => RunCoreAsync(CreateStartInfo(fileName, arguments, workingDirectory), fileName, onStandardOutputLine, cancellationToken);

    public Task<OperationResult<int>> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string>? onStandardOutputLine,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default
    ) =>
        RunCoreAsync(
            CreateStartInfo(fileName, arguments, workingDirectory, extraEnvironment),
            fileName,
            onStandardOutputLine,
            cancellationToken
        );

    private async Task<OperationResult<int>> RunCoreAsync(
        ProcessStartInfo startInfo,
        string fileName,
        Action<string>? onStandardOutputLine,
        CancellationToken cancellationToken
    )
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cancellationEventName = $@"Local\MKWCompiled.WheelWizard.Cancel.{Guid.NewGuid():N}";
            // Named wait handles only exist on Windows; elsewhere the constructor throws.
            using var cancellationEvent =
                OperatingSystem.IsWindows() && cancellationToken.CanBeCanceled
                    ? new EventWaitHandle(initialState: false, EventResetMode.ManualReset, cancellationEventName)
                    : null;
            EnsureUnixExecutable(fileName);
            if (cancellationEvent is not null)
                startInfo.Environment[CancellationEventEnvironmentVariable] = cancellationEventName;

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                    onStandardOutputLine?.Invoke(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    return;

                logger.LogDebug("Recomp setup stderr: {Line}", eventArgs.Data);
                onStandardOutputLine?.Invoke(eventArgs.Data);
            };

            if (!process.Start())
                return Fail($"Failed to start '{fileName}'.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
                // WaitForExitAsync observes the process handle. The synchronous wait additionally guarantees
                // that both asynchronous redirected-output readers have delivered their final lines.
                process.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                var exited = cancellationEvent is not null
                    ? await CancelAndWaitForExitAsync(process, cancellationEvent)
                    : await KillAndWaitForExitAsync(process);
                if (!exited)
                    throw;

                // Cooperative cancellation is a request, not proof that the backend abandoned its
                // transaction. Once it exits, the drained terminal output and actual exit code say
                // whether cancellation won before commit (failure) or commit won the race (success).
                return Ok(process.ExitCode);
            }

            return Ok(process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to run '{FileName}'", fileName);
            return Fail(exception);
        }
    }

    private static void EnsureUnixExecutable(string fileName)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        if (fileName is "/bin/bash" or "/usr/bin/env" || fileName.Contains('/', StringComparison.Ordinal) is false)
            return;

        try
        {
            var mode = File.GetUnixFileMode(fileName);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
                File.SetUnixFileMode(fileName, mode | UnixFileMode.UserExecute);
        }
        catch (Exception)
        {
            // Launch still proceeds; a missing execute bit surfaces as a start failure.
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments, string? workingDirectory)
    {
        var startInfo = CreateBaseStartInfo(fileName, workingDirectory);
        startInfo.Arguments = arguments;
        return startInfo;
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment
    )
    {
        var startInfo = CreateBaseStartInfo(fileName, workingDirectory);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (extraEnvironment is null)
            return startInfo;

        foreach (var (key, value) in extraEnvironment)
            startInfo.Environment[key] = value;
        return startInfo;
    }

    private static ProcessStartInfo CreateBaseStartInfo(string fileName, string? workingDirectory) =>
        new()
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : workingDirectory,
            UseShellExecute = false,
            // The recomp setup is CLI-only. Wheel Wizard supplies the UI, including during launch,
            // so the helper process must never flash a console window behind the game.
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

    private async Task<bool> CancelAndWaitForExitAsync(Process process, EventWaitHandle cancellationEvent)
    {
        try
        {
            cancellationEvent.Set();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to signal cooperative cancellation to the recomp setup process");
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(CancellationGracePeriod);
            process.WaitForExit();
            return true;
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Recomp setup did not exit within {GracePeriodSeconds} seconds after cooperative cancellation; stopping it",
                CancellationGracePeriod.TotalSeconds
            );
        }

        return await KillAndWaitForExitAsync(process);
    }

    private async Task<bool> KillAndWaitForExitAsync(Process process)
    {
        if (!TryKill(process) && !process.HasExited)
            return false;

        try
        {
            await process.WaitForExitAsync().WaitAsync(ForcedExitGracePeriod);
            process.WaitForExit();
            return true;
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Recomp setup did not exit after its process tree was stopped");
            if (!process.HasExited)
                return false;

            process.WaitForExit();
            return true;
        }
    }

    private bool TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to stop the recomp setup process after cancellation");
            return false;
        }
    }
}
