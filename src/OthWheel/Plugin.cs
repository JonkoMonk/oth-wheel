using System;
using System.Collections.Generic;
using HarmonyLib;

namespace OthWheel;

// Steering wheels (any brand, set up with the in-game wizard; Moza R3 built in) for over the hill:
// steering, pedals, buttons, physics force feedback.
// Loader-independent core. The BepInEx and MelonLoader entry points (Loader.*.cs) set up paths
// and logging, call Start, then put StatusBehaviour on a GameObject.
public static class Plugin
{
    public const string Guid = "jonkomonk.oth.wheel", Name = "OTH Wheel", Version = "1.0.0", Author = "JonkoMonk";

    static Action<string> _info = Console.WriteLine, _warn = Console.WriteLine, _error = Console.WriteLine;
    static readonly HashSet<string> _reported = new();

    public static void SetLog(Action<string> info, Action<string> warn, Action<string> error)
    {
        _info = info; _warn = warn; _error = error;
    }

    public static void Start(HarmonyLib.Harmony harmony)
    {
        Settings.Bind(new ConfigFile(ModPaths.SettingsFile));
        // Only game classes here, no UnityDirectInput: touching those this early breaks device detection.
        InputHooks.Install(harmony);
        CarPhysics.Install(harmony);
        Buttons.InstallZoomHook(harmony);
        DirectInputLate.Init(harmony);
    }

    public static void Info(string s) => _info(s);
    public static void Warn(string s) => _warn(s);
    public static void Error(string s) => _error(s);

    // Hooks run every tick; report each kind of failure only once instead of flooding the log.
    public static void ErrorOnce(string where, Exception e)
    {
        lock (_reported)
            if (!_reported.Add(where)) return;
        _error($"{where}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
}
