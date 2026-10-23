extern alias udi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using VehiclePhysics;
using DIManager = udi::DirectInputManager.DIManager;
using DIInputManager = udi::DIInputManager;
using FFBEffects = udi::DirectInputManager.FFBEffects;
using DeviceInfo = udi::DirectInputManager.DeviceInfo;
using UdiDevice = udi::DirectInputDevice;
using Native = udi::DirectInputManager.Native;
using DICondition = udi::DirectInputManager.DICondition;

namespace OthWheelProbe;

// Harmony postfixes that only count calls and remember the last arguments.
internal static class Hooks
{
    internal class Counter
    {
        public string Name;
        public long Total, LastReported, MaxAbsInt;
        public string LastArgs;
    }

    internal static readonly Dictionary<MethodBase, Counter> Counters = new();
    internal static readonly Dictionary<string, (float value, long count)> InputValues = new();
    internal static readonly object Lock = new();
    internal static readonly Queue<FunselektorFusionVPStandardInput> SpawnedInputs = new();

    static Harmony _h;

    // Patching a method runs its class's static constructor. The UnityDirectInput classes must not
    // be initialized before the game does it itself (doing so hid the R3: "0 DirectInput device(s)"),
    // so those hooks go in later via InstallDirectInput().
    public static void InstallEarly(Harmony h)
    {
        _h = h;
        if (Plugin.CfgFfbHooks.Value)
        {
            Patch(h, typeof(DIWrapper), "Initialize", null, nameof(PostCount));
            Patch(h, typeof(DIWrapper), "SelectDevice", null, nameof(PostCountArgs));
            Patch(h, typeof(DIWrapper), "AcquireExclusive", null, nameof(PostCount));
            Patch(h, typeof(DIWrapper), "UpdateConstantForceEffect", null, nameof(PostCountArgs));
            Patch(h, typeof(VPWheelDeviceInput), "OnEnableVehicle", null, nameof(PostCount));
            Patch(h, typeof(VPWheelDeviceInput), "ProcessForceFeedback", null, nameof(PostCountArgs));
            Patch(h, typeof(VPDeviceInput), "OnEnableVehicle", null, nameof(PostCount));
            Patch(h, typeof(VPDeviceInput), "ProcessForceFeedback", null, nameof(PostCountArgs));
            Patch(h, typeof(VehiclePhysics.InputManagement.DirectInputDevice), "ApplyForceFeedback", null, nameof(PostCount));
            Patch(h, typeof(VehiclePhysics.InputManagement.ForceFeedbackModelV1), "Update", null, nameof(PostCount));
            Patch(h, typeof(VehiclePhysics.InputManagement.ForceFeedbackModelV2), "Update", null, nameof(PostCount));
        }

        if (Plugin.CfgInputHooks.Value)
        {
            Patch(h, typeof(VehicleInput), "GetInputFromDirectInputDevice", null, nameof(PostCount));
            Patch(h, typeof(VehicleInput), "GetInputFromKeyboardOrController", null, nameof(PostCount));
            Patch(h, typeof(VehicleInput), "UpdateDirectInputReliableButtons", null, nameof(PostCount));
            Patch(h, typeof(FunselektorFusionVPStandardInput), "ApplyVehicleInput", null, nameof(PostCount));
        }

        if (Plugin.CfgVehicleHooks.Value)
            Patch(h, typeof(FunselektorFusionVPStandardInput), "OnEnableVehicle", null, nameof(PostVehicleEnabled));
    }

    // Called once the local car exists, i.e. long after the game set up UnityDirectInput itself.
    public static void InstallDirectInput()
    {
        var h = _h;
        var s = typeof(string);
        if (Plugin.CfgFfbHooks.Value)
        {
            Patch(h, typeof(DIManager), "UpdateConstantForceSimple", new[] { s, typeof(int) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "UpdateDamperSimple", new[] { s, typeof(int) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "UpdateFrictionSimple", new[] { s, typeof(int) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "UpdateInertiaSimple", new[] { s, typeof(int) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "UpdateSpringSimple", new[] { s, typeof(uint), typeof(int), typeof(int), typeof(int), typeof(uint), typeof(uint) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "UpdatePeriodicSimple", new[] { s, typeof(FFBEffects), typeof(int), typeof(uint), typeof(int), typeof(int) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "UpdateConstantForceSimple", new[] { typeof(DeviceInfo), typeof(int) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "EnableFFBEffect", new[] { s, typeof(FFBEffects) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "DestroyFFBEffect", new[] { s, typeof(FFBEffects) }, nameof(PostCountArgs));
            Patch(h, typeof(DIManager), "StopAllFFBEffects", new[] { s }, nameof(PostCountArgs));
            Patch(h, typeof(DIInputManager), "TestDirectionalCollision", null, nameof(PostCount));
            Patch(h, typeof(DIInputManager), "UpdateFFBEffects", null, nameof(PostCount));
            Patch(h, typeof(DIInputManager), "UpdatePeriodicForces", null, nameof(PostCount));
        }
        if (Plugin.CfgInputHooks.Value)
        {
            Patch(h, typeof(DIInputManager), "GetDeviceInputValue", new[] { typeof(UdiDevice), s }, nameof(PostDeviceInputValue));
            Patch(h, typeof(DIInputManager), "GetFFBDeviceAxisValue", new[] { s }, nameof(PostFfbAxisValue));
            Patch(h, typeof(DIInputManager), "GetMappingByName", new[] { s }, nameof(PostCountArgs));
        }
    }

    // The P/Invoke layer into DirectInputForceFeedback.dll: every effect goes through here,
    // even if IL2CPP inlined the DIManager wrappers (which would explain why those hooks never fired).
    public static void InstallNative()
    {
        var h = _h;
        var n = typeof(Native);
        Patch(h, n, "CreateDevice", null, nameof(PostCountArgs));
        Patch(h, n, "DestroyDevice", null, nameof(PostCountArgs));
        Patch(h, n, "CreateFFBEffect", null, nameof(PostCountArgs));
        Patch(h, n, "DestroyFFBEffect", null, nameof(PostCountArgs));
        Patch(h, n, "UpdateFFBEffect", null, nameof(PostUpdateFfbEffect));
        Patch(h, n, "StopAllFFBEffects", null, nameof(PostCountArgs));
        Patch(h, n, "GetDeviceState", null, nameof(PostCount));
    }

    // Logs the effect type and the first condition's magnitude-like values.
    public static void PostUpdateFfbEffect(MethodBase __originalMethod, FFBEffects effectType, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<DICondition> conditions)
    {
        object[] args = { effectType, "n/a" };
        try
        {
            if (conditions != null && conditions.Length > 0)
            {
                var c = conditions[0];
                args[1] = $"len={conditions.Length} off={c.offset} pc={c.positiveCoefficient} nc={c.negativeCoefficient} ps={c.positiveSaturation} ns={c.negativeSaturation} db={c.deadband}";
            }
        }
        catch (Exception e) { args[1] = "err " + e.Message; }
        Hit(__originalMethod, args);
    }

    static void Patch(Harmony h, Type t, string name, Type[] args, string postfix)
    {
        try
        {
            var m = args == null ? AccessTools.Method(t, name) : AccessTools.Method(t, name, args);
            if (m == null) { Plugin.L.LogWarning($"[Probe] not found: {t.FullName}.{name}"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(Hooks), postfix));
            Plugin.L.LogInfo($"[Probe] hooked {Describe(m)}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[Probe] hook failed {t.FullName}.{name}: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static string Describe(MethodBase m) =>
        $"{m.DeclaringType?.Name}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})";

    public static void PostCount(MethodBase __originalMethod) => Hit(__originalMethod, null);

    public static void PostCountArgs(MethodBase __originalMethod, object[] __args) => Hit(__originalMethod, __args);

    public static void PostDeviceInputValue(MethodBase __originalMethod, string inputName, float __result)
    {
        Hit(__originalMethod, null);
        RecordValue("dev:" + inputName, __result);
    }

    public static void PostFfbAxisValue(MethodBase __originalMethod, string axisName, float __result)
    {
        Hit(__originalMethod, null);
        RecordValue("ffbAxis:" + axisName, __result);
    }

    public static void PostVehicleEnabled(FunselektorFusionVPStandardInput __instance)
    {
        lock (Lock) SpawnedInputs.Enqueue(__instance);
    }

    static void RecordValue(string key, float v)
    {
        lock (Lock)
        {
            InputValues.TryGetValue(key ?? "?", out var old);
            InputValues[key ?? "?"] = (v, old.count + 1);
        }
    }

    static void Hit(MethodBase m, object[] args)
    {
        try
        {
            lock (Lock)
            {
                if (!Counters.TryGetValue(m, out var c))
                    Counters[m] = c = new Counter { Name = Describe(m) };
                c.Total++;
                if (args == null) return;
                long maxAbs = 0;
                foreach (var a in args)
                    if (a is int i) maxAbs = Math.Max(maxAbs, Math.Abs((long)i));
                c.MaxAbsInt = Math.Max(c.MaxAbsInt, maxAbs);
                if (maxAbs > 0 || c.LastArgs == null)
                    c.LastArgs = string.Join(", ", args.Select(a => a?.ToString() ?? "null"));
            }
        }
        catch { /* never break the game from a counter */ }
    }
}
