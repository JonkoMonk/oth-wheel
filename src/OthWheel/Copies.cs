using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OthWheel;

// Where this mod's DLL is, and whether another copy is installed. Players "uninstalled" the mod and it kept
// running: a copy was left where they didn't look (e.g. BepInEx\plugins\OthWheel.dll from the installer on a
// BepInEx game, while the manual README only names Mods\OthWheel.Melon.dll). Two loaded copies also fight
// over the wheel. So the mod says where it is loaded from (log, F3 > Remove the mod, the wizard's menu) and
// warns at the start when another copy would load too.
//   MelonLoader loads Mods\*.dll, plus subfolders of Mods that have a manifest.json (not "Disabled", "~...").
//   BepInEx loads BepInEx\plugins\**\*.dll (every subfolder).
internal static class Copies
{
    public static string GameDir { get; private set; } = "";
    public static string Self { get; private set; } = "";                 // full path of the loaded DLL
    public static List<string> Running { get; private set; } = new();     // other copies a loader on this game loads
    public static List<string> Leftovers { get; private set; } = new();   // copies nothing loads now (other loader not installed)

    public static void Scan(string gameDir, string self)
    {
        try
        {
            GameDir = gameDir ?? "";
            Self = string.IsNullOrEmpty(self) ? "" : Path.GetFullPath(self);
            bool melon = Directory.Exists(Path.Combine(GameDir, "MelonLoader")) && File.Exists(Path.Combine(GameDir, "version.dll"));
            bool bep = Directory.Exists(Path.Combine(GameDir, "BepInEx", "core")) && File.Exists(Path.Combine(GameDir, "winhttp.dll"));

            Running = new List<string>();
            Leftovers = new List<string>();
            var melonFiles = Find(Path.Combine(GameDir, "Mods"), false);
            foreach (var dir in SafeDirs(Path.Combine(GameDir, "Mods")))
            {
                string name = Path.GetFileName(dir);
                bool loads = File.Exists(Path.Combine(dir, "manifest.json")) && !name.StartsWith("~") && !name.StartsWith(".") &&
                             name != "Disabled" && name != "Broken" && name != "Retired";
                (loads ? melonFiles : Leftovers).AddRange(Find(dir, true));
            }
            var bepFiles = Find(Path.Combine(GameDir, "BepInEx", "plugins"), true);
            var pluginsFiles = Find(Path.Combine(GameDir, "Plugins"), true);   // MelonLoader refuses a mod there, but it's a leftover

            (melon ? Running : Leftovers).AddRange(melonFiles);
            (bep ? Running : Leftovers).AddRange(bepFiles);
            Leftovers.AddRange(pluginsFiles);
            Running = Running.Where(p => !Same(p, Self)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Leftovers = Leftovers.Where(p => !Same(p, Self) && !Running.Contains(p, StringComparer.OrdinalIgnoreCase))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            Plugin.Info($"mod file: {Rel(Self)}" +
                        (Running.Count > 0 ? $"; ANOTHER COPY LOADS TOO: {string.Join(", ", Running.Select(Rel))}" : "") +
                        (Leftovers.Count > 0 ? $"; unused copies: {string.Join(", ", Leftovers.Select(Rel))}" : ""));
        }
        catch (Exception e) { Plugin.ErrorOnce("copies", e); }
    }

    // Our DLLs: OthWheel.Melon.dll, OthWheel.dll, renamed copies like "OthWheel.Melon (2).dll" or "OthWheel - Copy.dll".
    static List<string> Find(string dir, bool recursive)
    {
        try
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.dll", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                            .Where(f => Path.GetFileName(f).StartsWith("OthWheel", StringComparison.OrdinalIgnoreCase))
                            .Select(Path.GetFullPath).ToList();
        }
        catch { return new List<string>(); }
    }

    static string[] SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // "Mods\OthWheel.Melon.dll" instead of the whole path (fits the menu, no Windows user name in reports).
    public static string Rel(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(unknown)";
        if (GameDir.Length > 0 && path.StartsWith(GameDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return path.Substring(GameDir.TrimEnd('\\', '/').Length + 1);
        return path;
    }

    // Every file and folder to delete for a full removal, as lines for the menus.
    public static List<string> ToDelete()
    {
        var list = new List<string> { Rel(Self) };
        list.AddRange(Running.Select(Rel));
        list.AddRange(Leftovers.Select(Rel));
        string data = Rel(ModPaths.DataDir);
        if (Directory.Exists(ModPaths.DataDir)) list.Add(data + "\\  (settings and wheel setups)");
        if (File.Exists(ModPaths.SettingsFile) && !ModPaths.SettingsFile.StartsWith(ModPaths.DataDir, StringComparison.OrdinalIgnoreCase))
            list.Add(Rel(ModPaths.SettingsFile));
        return list;
    }

    // Opens Explorer with the loaded DLL selected (the file that keeps the mod running).
    public static string ShowInExplorer()
    {
        try
        {
            if (!File.Exists(Self)) return "Couldn't find the mod's file. See the list above.";
            Process.Start("explorer.exe", $"/select,\"{Self}\"");
            Plugin.Info($"opened Explorer at {Rel(Self)}");
            return $"Explorer opened at {Rel(Self)}. Close the game, then delete it (and the other files listed).";
        }
        catch (Exception e) { return "Couldn't open Explorer: " + e.Message; }
    }

    // Warning for the start of the game: another copy runs at the same time.
    // Every running copy shows it at the same spot, so the text must not depend on which copy draws it.
    public static string Warning => Running.Count == 0 ? null :
        $"OTH Wheel is installed {Running.Count + 1} times, the copies fight over the wheel: " +
        string.Join(",  ", Running.Append(Self).Select(Rel).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) +
        $".  Close the game and delete all but one ({Settings.SetupKey.Value} > Remove the mod shows where they are).";
}
