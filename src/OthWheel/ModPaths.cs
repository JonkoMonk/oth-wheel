using System.IO;

namespace OthWheel;

// Where the mod keeps its files. Set by the loader entry point before anything else runs.
//   BepInEx:     BepInEx\config\jonkomonk.oth.wheel.cfg  +  BepInEx\config\OthWheel\ (setups, presets)
//   MelonLoader: UserData\OthWheel\jonkomonk.oth.wheel.cfg  +  UserData\OthWheel\ (setups, presets)
internal static class ModPaths
{
    public static string Loader { get; private set; }
    public static string SettingsFile { get; private set; }
    public static string DataDir { get; private set; }   // setups\ and presets\ live here
    public static string LogFile { get; private set; }    // the loader's log, for the support report

    public static void Init(string loader, string settingsDir, string dataDir, string logFile)
    {
        Loader = loader;
        SettingsFile = Path.Combine(settingsDir, Plugin.Guid + ".cfg");
        DataDir = dataDir;
        LogFile = logFile;
    }
}
