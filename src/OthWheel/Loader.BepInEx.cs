using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OthWheel;

// BepInEx 6 (IL2CPP) entry point. Builds OthWheel.dll for BepInEx\plugins\.
[BepInPlugin(Plugin.Guid, Plugin.Name, Plugin.Version)]
public class BepInExEntry : BasePlugin
{
    public override void Load()
    {
        Plugin.SetLog(s => Log.LogInfo(s), s => Log.LogWarning(s), s => Log.LogError(s));
        ModPaths.Init("BepInEx", Paths.ConfigPath, Path.Combine(Paths.ConfigPath, "OthWheel"), Path.Combine(Paths.BepInExRootPath, "LogOutput.log"));
        Plugin.Start(new Harmony(Plugin.Guid));
        AddComponent<StatusBehaviour>();
        Plugin.Info($"loaded under BepInEx, enabled={Settings.Enabled.Value}");
    }
}
