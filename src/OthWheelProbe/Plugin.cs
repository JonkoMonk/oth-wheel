using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OthWheelProbe;

// Step 2 of the wheel mod: logs only, changes nothing in the game.
[BepInPlugin(Guid, "OTH Wheel Probe", "0.1.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "jonkomonk.oth.wheelprobe";

    internal static ManualLogSource L;
    internal static ConfigEntry<bool> CfgFfbHooks, CfgInputHooks, CfgVehicleHooks, CfgXref, CfgDIWrapperScan, CfgAxisLog, CfgNativeHooks;

    public override void Load()
    {
        L = Log;
        CfgFfbHooks = Config.Bind("Probe", "FfbHooks", true, "Count calls to force feedback functions.");
        CfgInputHooks = Config.Bind("Probe", "InputHooks", true, "Count calls to the game's DirectInput driving input functions.");
        CfgVehicleHooks = Config.Bind("Probe", "VehicleHooks", true, "List components on cars when they spawn.");
        // Both of these crashed the game (native access violation) in probe v1/v2, so they are off by default.
        CfgXref = Config.Bind("Probe", "XrefReport", false, "Log who calls what (static code scan) a few seconds after start. Crashed the game.");
        CfgDIWrapperScan = Config.Bind("Probe", "DIWrapperScan", false, "List devices as VPP's DIWrapper sees them (once, when your car spawns). Crashed the game.");
        CfgAxisLog = Config.Bind("Probe", "AxisLog", true, "Log wheel/pedal axis and button changes.");
        CfgNativeHooks = Config.Bind("Probe", "NativeHooks", true, "Count calls into DirectInputForceFeedback.dll (effects, device state).");

        Hooks.InstallEarly(new Harmony(Guid));
        AddComponent<ProbeBehaviour>();
        L.LogInfo("[Probe] loaded");
    }
}
