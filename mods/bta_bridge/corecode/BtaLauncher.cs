using System;
using System.Diagnostics;
using System.IO;
using DealOrForce.Simulation;

namespace DealOrForce.BtaBridge;

/// <summary>Optional package-owned launcher. Called only by an explicit frontend action.</summary>
public static class BtaLauncher
{
    public const string InstanceName = "Deal or Force x BTA Flat World";
    public static string Launch(bool bridgeReady, Func<ProcessStartInfo, bool> start = null)
    {
        if (!bridgeReady) return Loc.Get("BTA bridge is not configured. Run Tools/BtaBridge/setup.ps1 first.");
        var launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "PrismLauncher", "prismlauncher.exe");
        var instance = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrismLauncher", "instances", InstanceName);
        return LaunchPrepared(launcher, instance, start);
    }

    // The process boundary is injectable so validation never starts a real game.
    public static string LaunchPrepared(string launcher, string instance, Func<ProcessStartInfo, bool> start = null)
    {
        if (!File.Exists(launcher) || !Directory.Exists(instance))
            return Loc.Get("Prepare the BTA Flat World instance with Tools/BtaBridge/flat/create-instance.ps1.");
        var info = new ProcessStartInfo { FileName = launcher, UseShellExecute = false };
        info.ArgumentList.Add("--launch"); info.ArgumentList.Add(InstanceName);
        try
        {
            var started = start is not null ? start(info) : Start(info);
            return Loc.Get(started ? "Prism launch requested. Keep this local campaign open." : "Prism could not be started.");
        }
        catch { return Loc.Get("Prism could not be started."); }
    }

    private static bool Start(ProcessStartInfo info)
    {
        // Prism is the interactive tool explicitly selected by the player, not a hidden helper.
        using var process = Process.Start(info);
        return process is not null;
    }
}
