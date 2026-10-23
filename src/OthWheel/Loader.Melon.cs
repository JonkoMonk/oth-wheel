using System.IO;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(OthWheel.MelonEntry), OthWheel.Plugin.Name, OthWheel.Plugin.Version, OthWheel.Plugin.Author)]
[assembly: MelonGame("Funselektor", "over the hill")]

namespace OthWheel;

// MelonLoader 0.7 entry point. Builds OthWheel.Melon.dll for Mods\.
public class MelonEntry : MelonMod
{
    public override void OnInitializeMelon()
    {
        var log = LoggerInstance;
        Plugin.SetLog(s => log.Msg(s), s => log.Warning(s), s => log.Error(s));
        var dataDir = Path.Combine(MelonEnvironment.UserDataDirectory, "OthWheel");
        ModPaths.Init("MelonLoader", dataDir, dataDir, Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Latest.log"));
        // Our hooks are installed by hand (no [HarmonyPatch] attributes), on MelonLoader's instance for this mod.
        Plugin.Start(HarmonyInstance);
    }

    // Unity objects only once the engine is fully up.
    public override void OnLateInitializeMelon()
    {
        // Same as BepInEx's AddComponent: our MonoBehaviour on a hidden object that survives scene loads.
        ClassInjector.RegisterTypeInIl2Cpp<StatusBehaviour>();
        var go = new GameObject("OthWheel");
        Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<StatusBehaviour>();
        Plugin.Info($"loaded under MelonLoader, enabled={Settings.Enabled.Value}");
    }
}
