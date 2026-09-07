using WheelWizard.Features.Patches;
using WheelWizard.Models.Enums;
using WheelWizard.Models.Mods;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;

namespace WheelWizard.Test.Features.Recomp;

/// <summary>
/// A deliberately small smoke suite over the three contracts that break silently when the recomp is
/// updated: the command line we hand the setup executable, the report we read back, and the status
/// that report maps onto. Everything here is string in / value out, so it runs the same everywhere.
/// </summary>
public class RecompTests
{
    [Fact]
    public void SilentInstall_BuildsTheCommandLineTheSetupExecutableExpects()
    {
        var arguments = RecompSetupCommandBuilder.BuildSilentInstallArguments(
            new()
            {
                GameFilePath = @"D:\Games\Mario Kart Wii.rvz",
                InstallFolderPath = @"D:\WheelWizard\Recomp\Install",
                RetroRewindFolderPath = @"D:\WheelWizard\RetroRewind6",
                Portable = true,
            }
        );

        Assert.Equal(
            "--silent --game \"D:\\Games\\Mario Kart Wii.rvz\" --install-dir \"D:\\WheelWizard\\Recomp\\Install\" --portable "
                + "--progress-json --retro-dir \"D:\\WheelWizard\\RetroRewind6\" --download-retro-wfc-payload",
            arguments
        );
    }

    [Fact]
    public void SilentInstall_SkipsThePayloadOnlyWhenAskedTo()
    {
        var arguments = RecompSetupCommandBuilder.BuildSilentInstallArguments(
            new()
            {
                GameFilePath = @"D:\Games\Mario Kart Wii.rvz",
                InstallFolderPath = @"D:\WheelWizard\Recomp\Install",
                RetroRewindFolderPath = @"D:\WheelWizard\RetroRewind6",
                RetroWfcPayloadMode = RecompRetroWfcPayloadMode.Skip,
            }
        );

        Assert.EndsWith("--retro-dir \"D:\\WheelWizard\\RetroRewind6\" --skip-retro-wfc-payload", arguments);
        Assert.DoesNotContain("--download-retro-wfc-payload", arguments);
    }

    [Fact]
    public void LinuxInstall_UsesTheAppImageVerbAndBundledToolchainFlags()
    {
        var arguments = RecompLinuxSetupArgs.BuildInstallArguments(
            "/home/deck/roms/RMCP01.wbfs",
            "/opt/WiiCompiled/play",
            "/opt/WiiCompiled/RetroRewind/RetroRewind6"
        );

        Assert.Equal(
            [
                "--appimage-extract-and-run",
                "install",
                "--game",
                "/home/deck/roms/RMCP01.wbfs",
                "--install-dir",
                "/opt/WiiCompiled/play",
                "--retro-dir",
                "/opt/WiiCompiled/RetroRewind/RetroRewind6",
                "--download-retro-wfc-payload",
                "--progress-json",
            ],
            arguments
        );
    }

    [Fact]
    public void LinuxInstall_SkipsThePayloadOnlyWhenAskedTo()
    {
        var arguments = RecompLinuxSetupArgs.BuildInstallArguments(
            "/home/deck/roms/RMCP01.wbfs",
            "/opt/WiiCompiled/play",
            "/opt/WiiCompiled/RetroRewind/RetroRewind6",
            RecompRetroWfcPayloadMode.Skip
        );

        Assert.Equal("--skip-retro-wfc-payload", arguments[^2]);
        Assert.DoesNotContain("--download-retro-wfc-payload", arguments);
    }

    [Fact]
    public void LinuxCompileHost_RunsAppRunInsideDistrobox()
    {
        var install = RecompLinuxCompileHost.BuildAppRunInstallArguments(
            "/home/deck/roms/RMCP01.wbfs",
            "/opt/WiiCompiled/play",
            "/opt/WiiCompiled/RetroRewind/RetroRewind6"
        );
        var arguments = RecompLinuxCompileHost.BuildDistroboxEnterArguments("/opt/WiiCompiled/setup/AppRun", install);

        Assert.Equal("enter", arguments[0]);
        Assert.Equal("wiicompiled", arguments[1]);
        Assert.Equal("--", arguments[2]);
        Assert.Equal("/opt/WiiCompiled/setup/AppRun", arguments[3]);
        Assert.Equal("install", arguments[4]);
        Assert.DoesNotContain("--appimage-extract-and-run", arguments);
        Assert.True(RecompLinuxCompileHost.ListOutputHasContainer("9f60ade6010f | wiicompiled          | Up 27 hours"));
        Assert.False(RecompLinuxCompileHost.ListOutputHasContainer("abc | other | Up"));
        Assert.Equal(
            "/home/deck/.local/bin/distrobox",
            RecompLinuxCompileHost.FindDistroboxExecutable(path => path.EndsWith(".local/bin/distrobox"))
        );
        Assert.Null(RecompLinuxCompileHost.FindDistroboxExecutable(_ => false));
        Assert.True(RecompLinuxCompileHost.IsDistroboxScript("distrobox"));
        Assert.True(RecompLinuxCompileHost.IsDistroboxScript("distrobox-enter"));
        Assert.False(RecompLinuxCompileHost.IsDistroboxScript("distrobox.1"));
        Assert.Equal("/usr/bin/podman", RecompLinuxCompileHost.FindContainerRuntime(path => path == "/usr/bin/podman"));
        Assert.True(RecompLinuxCompileHost.CanUseDistrobox(path => path is "/usr/bin/distrobox" or "/usr/bin/podman"));
        Assert.False(RecompLinuxCompileHost.CanUseDistrobox(path => path == "/usr/bin/distrobox"));
    }

    [Fact]
    public void LinuxLinkerLibraries_SelectTheUbuntuLibsTheAppImageLinkerNeeds()
    {
        Assert.True(RecompLinuxLinkerLibraries.ShouldCopyLibraryFile("libxml2.so.2"));
        Assert.True(RecompLinuxLinkerLibraries.ShouldCopyLibraryFile("libicuuc.so.70"));
        Assert.True(RecompLinuxLinkerLibraries.ShouldCopyLibraryFile("libicudata.so.70.1"));
        Assert.False(RecompLinuxLinkerLibraries.ShouldCopyLibraryFile("libxml2.so.16"));
        Assert.True(RecompLinuxLinkerLibraries.HostHasLibXml2(path => path == "/usr/lib/x86_64-linux-gnu/libxml2.so.2"));
        Assert.False(RecompLinuxLinkerLibraries.HostHasLibXml2(_ => false));

        var environment = RecompLinuxLinkerLibraries.WithLibraryPath(
            new Dictionary<string, string> { ["APPIMAGE_EXTRACT_AND_RUN"] = "1" },
            "/tmp/linker-libs"
        );
        Assert.Equal("1", environment["APPIMAGE_EXTRACT_AND_RUN"]);
        Assert.StartsWith("/tmp/linker-libs", environment["LD_LIBRARY_PATH"]);
    }

    [Fact]
    public void RepairProducts_PassesExactlyOnePayloadOption()
    {
        Assert.Equal(
            "--repair-products --install-dir \"D:\\Recomp\" --retro-dir \"D:\\RetroRewind6\" --download-retro-wfc-payload --progress-json",
            RecompSetupCommandBuilder.BuildRepairProductsArguments(@"D:\Recomp", @"D:\RetroRewind6")
        );
        Assert.Equal(
            "--repair-products --install-dir \"D:\\Recomp\" --retro-dir \"D:\\RetroRewind6\" --skip-retro-wfc-payload --progress-json",
            RecompSetupCommandBuilder.BuildRepairProductsArguments(@"D:\Recomp", @"D:\RetroRewind6", RecompRetroWfcPayloadMode.Skip)
        );
    }

    [Fact]
    public void InstallState_ReadsThePayloadModeTheSetupHostWrites()
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<RecompInstallState>(
            """{"SchemaVersion":1,"SetupVersion":"0.3.0","InstallDir":"D:\\Recomp","RetroWfcPayloadMode":"skipped"}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        Assert.NotNull(state);
        Assert.True(state.IsRetroWfcPayloadSkipped);
    }

    [Fact]
    public void PayloadPolicy_LegacyStateWithoutModeKeepsDownloadingWhenTheServiceIsDown()
    {
        // Written by a host that predates the mode field: Retro Rewind is installed, so the host owns a
        // verified payload copy and must be asked to download, never to skip or to bother the user.
        var legacy = new RecompInstallState
        {
            SchemaVersion = 1,
            SetupVersion = "0.2.25",
            InstallDir = @"D:\Recomp",
            RetroRewindInstalled = true,
        };

        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(legacy, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(legacy, true, serviceReachable: false));
    }

    [Fact]
    public void PayloadPolicy_OnlyAFreshRetroRewindBuildAsksTheUser()
    {
        var downloaded = new RecompInstallState { RetroWfcPayloadMode = "downloaded", RetroRewindInstalled = true };
        var skipped = new RecompInstallState { RetroWfcPayloadMode = "skipped", RetroRewindInstalled = true };
        var baseOnly = new RecompInstallState { RetroWfcPayloadMode = "", RetroRewindInstalled = false };

        // Nothing to decide without a Retro Rewind source, and never a probe for a payload-bearing install.
        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(null, hasRetroRewindSource: false));
        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(downloaded, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(downloaded, true, serviceReachable: false));

        // A skipped install stays offline while the service is down and upgrades when it is back.
        Assert.True(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(skipped, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Skip, RecompRetroWfcPayloadPolicy.Decide(skipped, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(skipped, true, serviceReachable: true));

        // A fresh install and a base-only install both need a brand new Retro Rewind build.
        Assert.Equal(RecompRetroWfcPayloadDecision.AskUser, RecompRetroWfcPayloadPolicy.Decide(null, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.AskUser, RecompRetroWfcPayloadPolicy.Decide(baseOnly, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(null, true, serviceReachable: true));
    }

    [Fact]
    public void ProductsLine_IsReadBackAsTheProductStateItReports()
    {
        var parsed = RecompSetupOutputParser.Parse(
            """
            {"type":"products","setupVersion":"0.3.0","installDir":"D:\\WiiCompiled","rebuildRequired":false,"base":{"status":"current","detail":"ok"},"retroRewind":{"status":"code-pul-changed","detail":"Code.pul changed"}}
            """
        );

        var products = Assert.IsType<RecompProductsEvent>(parsed);
        Assert.Equal("0.3.0", products.SetupVersion);
        Assert.True(products.Base.IsCurrent);
        Assert.Equal(RecompProductState.CodePulChanged, products.RetroRewind.State);
        Assert.True(products.ActionRequired);
    }

    [Fact]
    public void RetroRewindVersionList_UsesTheLastNumericLine()
    {
        var latest = RecompLinuxUpdateChecker.ReadLatestVersionToken(
            """
            6.12.6
            6.12.7 extra
            notes
            """
        );

        Assert.Equal("6.12.7", latest);
    }

    [Fact]
    public void PulsarPatchMods_WorkWithWiiCompiled_LooseDolphinModsDoNot()
    {
        var pulsar = new Mod { Title = "Pulsar Pack", HasIncompatibleFiles = false };
        var dolphinOnly = new Mod { Title = "Loose SZS", HasIncompatibleFiles = true };

        Assert.True(ModLauncherCompatibility.WorksWithWiiCompiled(pulsar));
        Assert.False(ModLauncherCompatibility.WorksWithWiiCompiled(dolphinOnly));
    }

    [Fact]
    public void SetupFileName_MatchesTheCurrentPlatformAsset()
    {
        if (OperatingSystem.IsLinux())
            Assert.Equal("WiiCompiled-Setup-x86_64.AppImage", RecompSetupCommandBuilder.SetupFileName);
        else
            Assert.Equal("WiiCompiled-Setup.exe", RecompSetupCommandBuilder.SetupFileName);
    }

    [Fact]
    public void Status_IsOnlyReadyWhenTheInstallWasActuallyVerified()
    {
        var current = new RecompProductStatus(RecompProductState.Current, "ok");
        var verified = new RecompProductsEvent("0.3.0", @"D:\WiiCompiled", RebuildRequired: false, current, current);

        Assert.Equal(WheelWizardStatus.Ready, RecompStatusResolver.Resolve(true, "0.3.0", "v0.3.0", verified));
        Assert.Equal(WheelWizardStatus.OutOfDate, RecompStatusResolver.Resolve(true, "0.3.0", "v0.4.0", verified));
        Assert.Equal(WheelWizardStatus.NotInstalled, RecompStatusResolver.Resolve(true, null, "v0.3.0", verified));
        Assert.Equal(WheelWizardStatus.ConfigNotFinished, RecompStatusResolver.Resolve(false, "0.3.0", "v0.3.0", verified));

        // A check that never answered must never read as ready.
        Assert.Equal(WheelWizardStatus.OutOfDate, RecompStatusResolver.Resolve(true, "0.3.0", "v0.3.0", products: null));
    }
}
