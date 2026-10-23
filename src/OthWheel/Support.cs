using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;

namespace OthWheel;

// Sharing a wheel setup (so it can become a built-in preset) and the support report.
// Nothing is ever sent automatically: files go to the desktop, and the player sends them
// to the mod's author themselves (e.g. through the mod's Nexus / GitHub page).
internal static class Support
{
    static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    static void ShowInExplorer(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }

    // Saves the setup to the desktop and shows it in Explorer. Returns a message for the screen.
    public static string Share(WheelProfile p)
    {
        if (p == null) return "There's no setup to share yet.";
        string file;
        try
        {
            var copy = p.Clone();
            copy.Source = "shared";
            copy.ModVersion = Plugin.Version;
            file = Path.Combine(Desktop, $"OTH wheel setup - {Profiles.SafeFileName(p.Name)}.json");
            File.WriteAllText(file, Profiles.ToJson(copy));
        }
        catch (Exception e) { return "Couldn't save the setup file: " + e.Message; }
        Plugin.Info($"shared setup saved to {file}");
        ShowInExplorer(file);
        return $"Saved on your desktop: {Path.GetFileName(file)}\n" +
               "Send this file to the mod's author (for example on the mod's page) to get your wheel added. Thanks!";
    }

    // One zip on the desktop with everything needed to help someone remotely.
    public static string Report()
    {
        string zip = Path.Combine(Desktop, $"OTH wheel mod report {DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip");
        try
        {
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var info = new StringBuilder();
                info.AppendLine($"OTH wheel mod {Plugin.Version}, game {Application.version}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                info.AppendLine($"Devices status: {Devices.Status}");
                info.AppendLine(Devices.Describe());
                info.AppendLine();
                info.AppendLine($"Force feedback: {Ffb.State}, profile {Ffb.Profile.Value}, output {Ffb.Output}, tyres {Ffb.LearnStatus}, bad values {Ffb.BadValues}");
                info.AppendLine($"Wheel: connected {Wheel.Connected}, raw steer {Wheel.RawSteer}, position {Wheel.Position:0.000}, steer {Wheel.Steer:0.000}, throttle {Wheel.Throttle:0.00}, brake {Wheel.Brake:0.00}, clutch {Wheel.Clutch:0.00}");
                info.AppendLine();
                info.AppendLine("Active setup:");
                info.AppendLine(Devices.Active != null ? Profiles.ToJson(Devices.Active) : "(none)");
                AddText(z, "info.txt", info.ToString());
                AddFile(z, Path.GetFileName(ModPaths.LogFile), ModPaths.LogFile);
                AddFile(z, "Player.log", Path.Combine(Application.persistentDataPath, "Player.log"));
                AddFile(z, Path.GetFileName(ModPaths.SettingsFile), ModPaths.SettingsFile);
                if (Directory.Exists(Profiles.SetupsDir))
                    foreach (var f in Directory.GetFiles(Profiles.SetupsDir, "*.json")) AddFile(z, "setups/" + Path.GetFileName(f), f);
                foreach (var f in Shifter.Files()) AddFile(z, "shifters/" + Path.GetFileName(f), f);
                if (Directory.Exists(Profiles.PresetsDir))
                    foreach (var f in Directory.GetFiles(Profiles.PresetsDir, "*.json")) AddFile(z, "presets/" + Path.GetFileName(f), f);
            }
        }
        catch (Exception e) { return "Couldn't save the report: " + e.Message; }
        Plugin.Info($"support report saved to {zip}");
        ShowInExplorer(zip);
        return $"Report saved on your desktop: {Path.GetFileName(zip)}";
    }

    static void AddText(ZipArchive z, string name, string text)
    {
        using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    // Logs are still open for writing by the game / the mod loader, hence the sharing flags.
    static void AddFile(ZipArchive z, string name, string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = z.CreateEntry(name).Open();
            src.CopyTo(dst);
        }
        catch (Exception e) { AddText(z, name + ".error.txt", e.Message); }
    }
}
