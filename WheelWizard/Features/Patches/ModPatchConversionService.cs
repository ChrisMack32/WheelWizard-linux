using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using WheelWizard.Helpers;
using WheelWizard.Models.Mods;
using WheelWizard.Services;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Features.Patches;

public static class ModPatchCompatibilityText
{
    public static string IncompatibleTitle => t("patch.incompatible_mod.title");
    public static string IncompatibleMessage => t("patch.incompatible_mod.message");
}

/// <summary>
/// WiiCompiled only loads Pulsar patch archives. Loose Dolphin-style SZS/BRSAR trees stay
/// Dolphin-only until the user converts them to patches.
/// </summary>
public static class ModLauncherCompatibility
{
    public static bool WorksWithWiiCompiled(Mod mod) => !mod.HasIncompatibleFiles;

    public static string Label(Mod mod) => WorksWithWiiCompiled(mod) ? t("patch.launcher.both") : t("patch.launcher.dolphin_only");

    public static string Tip(Mod mod) => WorksWithWiiCompiled(mod) ? t("patch.launcher.both_tip") : t("patch.launcher.dolphin_only_tip");

    public static string PageNote(bool recompMode) => recompMode ? t("patch.launcher.note_recomp") : t("patch.launcher.note_dolphin");

    public static string BrowserLabel(bool usesPatches) => usesPatches ? t("patch.launcher.both") : t("patch.launcher.dolphin_only");

    public static string BrowserTip(bool usesPatches) => usesPatches ? t("patch.launcher.both_tip") : t("patch.launcher.dolphin_only_tip");

    public static string BrowserNote(bool usesPatches, bool recompMode) =>
        usesPatches ? t("patch.launcher.browser_note_patches")
        : recompMode ? t("patch.launcher.browser_note_dolphin_recomp")
        : t("patch.launcher.browser_note_dolphin");
}

public interface IModPatchConversionService
{
    bool HasIncompatibleSzsFiles(Mod mod);

    IReadOnlyList<string> GetConvertibleArchiveFiles(Mod mod);

    void RefreshCompatibility(Mod mod);

    Task<OperationResult<ModPatchConversionResult>> ConvertToPatchesAsync(Mod mod, CancellationToken cancellationToken);
}

public sealed class ModPatchConversionService(ISzsPatchConverter szsPatchConverter, ILogger<ModPatchConversionService> logger)
    : IModPatchConversionService
{
    public bool HasIncompatibleSzsFiles(Mod mod) => GetConvertibleArchiveFiles(mod).Any();

    public IReadOnlyList<string> GetConvertibleArchiveFiles(Mod mod)
    {
        var modDirectory = PathManager.GetModDirectoryPath(mod.Title);
        if (!Directory.Exists(modDirectory))
            return [];

        return Directory.EnumerateFiles(modDirectory, "*", SearchOption.AllDirectories).Where(IsConvertibleArchiveFile).ToArray();
    }

    public void RefreshCompatibility(Mod mod)
    {
        mod.HasIncompatibleFiles = HasIncompatibleSzsFiles(mod);
    }

    public async Task<OperationResult<ModPatchConversionResult>> ConvertToPatchesAsync(Mod mod, CancellationToken cancellationToken)
    {
        var sourceDirectory = PathManager.GetModDirectoryPath(mod.Title);
        if (!Directory.Exists(sourceDirectory))
            return Fail(t("message_error.no_mod_folder.extra"));

        var sourceFiles = GetConvertibleArchiveFiles(mod);
        if (sourceFiles.Count == 0)
        {
            RefreshCompatibility(mod);
            return new ModPatchConversionResult();
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "WheelWizardPatchConversion", $"{mod.Title}-{Guid.NewGuid():N}");
        var tempModDirectory = Path.Combine(tempRoot, mod.Title);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressWindow = new ProgressWindow(t("progress.converting_mod_to_patches"))
            .SetGoal(t("progress.converting_files_count", sourceFiles.Count)!)
            .SetCancellationTokenSource(cts);
        progressWindow.Show();

        try
        {
            var result = await Task.Run(
                () =>
                {
                    CopyDirectory(sourceDirectory, tempModDirectory, cts.Token);
                    var tempFiles = GetConvertibleArchiveFilesInDirectory(tempModDirectory);
                    var warnings = new List<string>();
                    var skipped = new List<string>();
                    var convertedCount = 0;
                    var writtenPatchCount = 0;

                    for (var index = 0; index < tempFiles.Count; index++)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var file = tempFiles[index];
                        var fileName = Path.GetFileName(file);

                        Dispatcher.UIThread.Post(() =>
                        {
                            progressWindow.UpdateProgress((int)(index / (double)Math.Max(tempFiles.Count, 1) * 80));
                            progressWindow.SetExtraText(t("progress.converting_file", fileName)!);
                        });

                        if (LooseBrsarPatchFileName.TryGetNormalizedFileName(fileName, out var normalizedPatchFileName))
                        {
                            writtenPatchCount += WriteLoosePatchFile(file, normalizedPatchFileName, File.ReadAllBytes(file));
                            convertedCount++;
                            continue;
                        }

                        var conversionResult = ConvertArchiveFile(file);
                        if (conversionResult.IsFailure)
                        {
                            skipped.Add($"{fileName}: {conversionResult.Error.Message}");
                            continue;
                        }

                        var conversion = conversionResult.Value;
                        if (conversion.Baseline == null)
                        {
                            skipped.Add(t("warning.file_not_in_built_in_baseline", fileName)!);
                            continue;
                        }

                        warnings.AddRange(conversion.Analysis.Warnings.Select(warning => $"{fileName}: {warning}"));
                        skipped.AddRange(conversion.Analysis.Skipped.Select(item => $"{fileName}: {item}"));

                        if (conversion.Analysis.Skipped.Count > 0)
                            continue;

                        if (conversion.Analysis.Entries.Count == 0)
                        {
                            skipped.Add($"{fileName}: {t("warning.no_szs_differences")}");
                            continue;
                        }

                        if (ShouldWriteWholeFileOverride(conversion))
                        {
                            var archiveTag = conversion.Analysis.ArchiveTag;
                            if (string.IsNullOrWhiteSpace(archiveTag))
                            {
                                skipped.Add(t("warning.file_not_in_built_in_baseline", fileName)!);
                                continue;
                            }

                            warnings.Add($"{fileName}: {t("warning.converted_large_archive_as_whole_file")}");
                            writtenPatchCount += WriteLoosePatchFile(file, $"{archiveTag}.szs", conversion.SourceBytes);
                            convertedCount++;
                            continue;
                        }

                        foreach (var entry in conversion.Analysis.Entries)
                            writtenPatchCount += WriteLoosePatchFile(file, Path.GetFileName(entry.ExportPath), entry.Bytes);

                        convertedCount++;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        progressWindow.UpdateProgress(85);
                        progressWindow.SetExtraText(t("progress.applying_converted_mod"));
                    });

                    ReplaceDirectory(sourceDirectory, tempModDirectory, cts.Token);
                    return new ModPatchConversionResult
                    {
                        ConvertedFileCount = convertedCount,
                        WrittenPatchCount = writtenPatchCount,
                        Warnings = warnings,
                        Skipped = skipped,
                    };
                },
                cts.Token
            );

            RefreshCompatibility(mod);
            Dispatcher.UIThread.Post(() => progressWindow.UpdateProgress(100));
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Patch conversion cancelled for mod {ModTitle}.", mod.Title);
            return Fail("Conversion cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Patch conversion failed for mod {ModTitle}.", mod.Title);
            return ex;
        }
        finally
        {
            progressWindow.Close();
            _ = FileHelper.DeleteDirectoryIfExists(tempRoot);
        }
    }

    private OperationResult<ArchiveConversion> ConvertArchiveFile(string file)
    {
        try
        {
            var fileBytes = File.ReadAllBytes(file);
            var baseline = SelectBaseline(Path.GetFileName(file), fileBytes);
            if (baseline == null)
                return new ArchiveConversion(null, new PatchConversionAnalysis(), fileBytes);

            var analysisResult = AnalyzeArchive(baseline, Path.GetFileName(file), fileBytes);
            if (analysisResult.IsFailure)
                return analysisResult.Error;

            return new ArchiveConversion(baseline, analysisResult.Value, fileBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to analyze archive {ArchivePath}.", file);
            return new OperationError { Message = ex.Message, Exception = ex };
        }
    }

    private BaselineEntry? SelectBaseline(string fileName, byte[] moddedBytes)
    {
        var kind = IsBrsarFileName(fileName) ? "brsar" : "szs";
        var candidates = GameBaselineStore.Instance.FindCandidates(fileName, kind);
        if (candidates.Count == 0)
            return null;

        return candidates
            .Select(candidate => new { Candidate = candidate, Entry = GameBaselineStore.Instance.GetEntry(candidate.Id) })
            .Where(item => item.Entry != null)
            .Select(item => new
            {
                item.Candidate,
                Entry = item.Entry!,
                Difference = EstimateDifference(item.Entry!, moddedBytes),
            })
            .OrderBy(item => item.Difference)
            .ThenBy(item => item.Candidate.Region ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?.Entry;
    }

    private OperationResult<PatchConversionAnalysis> AnalyzeArchive(BaselineEntry baseline, string fileName, byte[] fileBytes) =>
        string.Equals(baseline.Kind, "brsar", StringComparison.OrdinalIgnoreCase)
            ? BrsarPatchConverter.AnalyzeAgainstBaseline(baseline, fileName, fileBytes)
            : szsPatchConverter.AnalyzeAgainstBaseline(baseline, fileName, fileBytes);

    private int EstimateDifference(BaselineEntry baseline, byte[] fileBytes) =>
        string.Equals(baseline.Kind, "brsar", StringComparison.OrdinalIgnoreCase)
            ? BrsarPatchConverter.EstimateDifference(baseline, fileBytes)
            : szsPatchConverter.EstimateDifference(baseline, fileBytes);

    private static IReadOnlyList<string> GetConvertibleArchiveFilesInDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(IsConvertibleArchiveFile).ToArray();

    private static bool IsConvertibleArchiveFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (LooseBrsarPatchFileName.TryGetNormalizedFileName(fileName, out _))
            return true;

        if (IsBrsarFileName(fileName))
            return true;

        return Path.GetExtension(filePath).Equals(".szs", StringComparison.OrdinalIgnoreCase)
            && !IsModdingArchiveFile(fileName)
            && !KartSzsAllowList.IsAllowedFullCharacterOrKart(fileName);
    }

    private static bool IsBrsarFileName(string fileName) => fileName.Equals("revo_kart.brsar", StringComparison.OrdinalIgnoreCase);

    private const int WholeFileOverrideByteThreshold = 256 * 1024;

    private static bool ShouldWriteWholeFileOverride(ArchiveConversion conversion)
    {
        if (!string.Equals(conversion.Analysis.Mode, "tagged-archive", StringComparison.OrdinalIgnoreCase))
            return false;

        var replacementBytes = conversion.Analysis.Entries.Where(entry => entry.Bytes.Length > 0).Sum(entry => (long)entry.Bytes.Length);
        return replacementBytes >= WholeFileOverrideByteThreshold;
    }

    private static int WriteLoosePatchFile(string sourceFile, string patchFileName, byte[] bytes)
    {
        var destination = Path.Combine(Path.GetDirectoryName(sourceFile)!, patchFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!Path.GetFullPath(destination).Equals(Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase))
            File.Delete(sourceFile);

        File.WriteAllBytes(destination, bytes);
        return 1;
    }

    private static bool IsModdingArchiveFile(string fileName)
    {
        if (!fileName.EndsWith(".szs", StringComparison.OrdinalIgnoreCase))
            return false;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var tagSeparator = nameWithoutExtension.LastIndexOf('.');
        return tagSeparator > 0 && tagSeparator + 1 < nameWithoutExtension.Length;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static void ReplaceDirectory(string sourceDirectory, string convertedDirectory, CancellationToken cancellationToken)
    {
        var backupDirectory = $"{sourceDirectory}.patch-conversion-backup-{Guid.NewGuid():N}";

        Directory.Move(sourceDirectory, backupDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectory(convertedDirectory, sourceDirectory, cancellationToken);
            _ = FileHelper.DeleteDirectoryIfExists(backupDirectory);
        }
        catch
        {
            if (Directory.Exists(sourceDirectory))
                Directory.Delete(sourceDirectory, true);
            Directory.Move(backupDirectory, sourceDirectory);
            throw;
        }
    }

    private sealed record ArchiveConversion(BaselineEntry? Baseline, PatchConversionAnalysis Analysis, byte[] SourceBytes);
}
