using System;
using System.Reflection;
#if MELON
using Il2CppFusion;
#else
using Fusion;
#endif
using HarmonyLib;
using UnityEngine;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
#endif

namespace OthWheel;

// Puts wheel steering and pedals into the game's own networked input (NetworkInputData),
// at the point where the game has just filled it from keyboard/controller. Because this is
// the data Fusion sends to the host, co-op sees the same input as the local car.
//
// Three write points, all idempotent (same result no matter how many run):
//  1. prefix  VehicleInput.UpdateHandbrakeInput(ref data): before the auto-handbrake looks at throttle/brake
//  2. postfix VehicleInput.GetInputFromKeyboardOrController(input): through the input buffer pointer
//  3. postfix VehicleInput.OnInput(runner, input): last word, in case the game re-sets the buffer afterwards
internal static unsafe class InputHooks
{
    // Set when the game produced driving input this tick, so we never drive the car while the
    // game itself is not reading driving controls (menus, on foot, ...).
    static bool _gameInputThisTick;
    static int _badTypeKey = int.MinValue, _goodTypeKey = int.MinValue;

    // For the status log.
    public static VehicleInput LocalVehicleInput;
    // When the game last produced driving input (force feedback and "are we driving" use this).
    public static float LastTickTime = -100f;
    public static float GameSteer, GameThrottle, GameBrake;
    public static float SentSteer, SentThrottle, SentBrake, SentClutch;
    public static long Ticks, HandbrakeHits, KbdHits, OnInputHits;

    public static void Install(HarmonyLib.Harmony h)
    {
        Patch(h, typeof(VehicleInput), "UpdateHandbrakeInput", prefix: nameof(BeforeHandbrake));
        Patch(h, typeof(VehicleInput), "GetInputFromKeyboardOrController", postfix: nameof(AfterKeyboardOrController));
        Patch(h, typeof(VehicleInput), "Fusion_INetworkRunnerCallbacks_OnInput", postfix: nameof(AfterOnInput));
        Patch(h, typeof(VehicleInput), "UpdateDirectInputReliableButtons", prefix: nameof(BeforeGameWheelButtons));
    }

    // The game maps a few wheel buttons itself (e.g. X = Hi/Lo). Our button map replaces that,
    // otherwise those buttons would fire twice.
    public static bool BeforeGameWheelButtons() =>
        !(Buttons.Enabled.Value && Buttons.ReplaceGameWheelButtons.Value && Settings.Enabled.Value);

    static void Patch(HarmonyLib.Harmony h, Type t, string name, string prefix = null, string postfix = null)
    {
        try
        {
            var m = AccessTools.Method(t, name);
            if (m == null) { Plugin.Warn($"not found: {t.Name}.{name} (game update?)"); return; }
            h.Patch(m,
                prefix: prefix != null ? new HarmonyMethod(typeof(InputHooks), prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(InputHooks), postfix) : null);
            Plugin.Info($"hooked {t.Name}.{name}");
        }
        catch (Exception e)
        {
            Plugin.Error($"hook failed {t.Name}.{name}: {e.GetType().Name}: {e.Message}");
        }
    }

    public static void BeforeHandbrake(VehicleInput __instance, ref NetworkInputData data)
    {
        try
        {
            if (!BeginTick(__instance)) return;
            HandbrakeHits++;
            ApplyTo(ref data);
        }
        catch (Exception e) { Plugin.ErrorOnce("handbrake hook", e); }
    }

    public static void AfterKeyboardOrController(VehicleInput __instance, NetworkInput input)
    {
        try
        {
            if (!BeginTick(__instance)) return;
            KbdHits++;
            ApplyToBuffer(input);
        }
        catch (Exception e) { Plugin.ErrorOnce("keyboard/controller hook", e); }
    }

    public static void AfterOnInput(VehicleInput __instance, NetworkInput input)
    {
        try
        {
            if (!_gameInputThisTick) return;
            _gameInputThisTick = false;
            OnInputHits++;
            ApplyToBuffer(input);
        }
        catch (Exception e) { Plugin.ErrorOnce("OnInput hook", e); }
    }

    // The first write point in a frame reads the wheel; later ones in the same frame reuse the values.
    // Keyed on the frame (not on the OnInput hook resetting a flag), so a missing hook can't freeze the wheel.
    static bool BeginTick(VehicleInput vi)
    {
        if (!Settings.Enabled.Value || !vi.HasInputAuthority) return false;
        LocalVehicleInput = vi;
        int frame = Time.frameCount;
        LastTickTime = Time.unscaledTime;
        if (frame != _lastFrame)
        {
            _lastFrame = frame;
            Ticks++;
            _firstWriteThisTick = true;
        }
        _gameInputThisTick = true;
        // While the setup wizard runs, turning the wheel and pressing pedals must not drive the car.
        return Wheel.Connected && !Wizard.IsOpen;
    }

    static int _lastFrame = -1;
    static bool _firstWriteThisTick;

    static void ApplyToBuffer(NetworkInput input)
    {
        if (!Wheel.Connected || Wizard.IsOpen || !input.IsValid) return;
        int key = input.TypeKey;
        if (key == _badTypeKey) return;
        if (key != _goodTypeKey)
        {
            // Only touch the buffer if it really holds NetworkInputData and is big enough.
            string tn = input.Type?.FullName;
            if (tn != "NetworkInputData" || input.WordCount * 4 < sizeof(NetworkInputData))
            {
                _badTypeKey = key;
                Plugin.Warn($"input buffer holds '{tn}' ({input.WordCount} words), expected NetworkInputData ({sizeof(NetworkInputData)} bytes); not writing to it");
                return;
            }
            _goodTypeKey = key;
            Plugin.Info($"input buffer OK: {tn}, {input.WordCount} words");
        }
        var p = (NetworkInputData*)input.Data;
        if (p == null) return;
        ApplyTo(ref *p);
    }

    static void ApplyTo(ref NetworkInputData d)
    {
        if (_firstWriteThisTick)
        {
            _firstWriteThisTick = false;
            GameSteer = d.Steering; GameThrottle = d.Throttle; GameBrake = d.Brake;
        }

        // Steering: the wheel decides, unless it sits inside its deadzone and keyboard/controller is steering.
        if (Wheel.Steer != 0f || Math.Abs(d.Steering) < 0.01f) d.Steering = Wheel.Steer;
        // Pedals: whichever is pressed harder, so keyboard/controller keep working too.
        d.Throttle = Math.Max(d.Throttle, Wheel.Throttle);
        d.Brake = Math.Max(d.Brake, Wheel.Brake);
        d.Clutch = Math.Max(d.Clutch, Wheel.Clutch);

        Shifter.Apply(ref d);
        GearProbe.Apply(ref d);
        SentSteer = d.Steering; SentThrottle = d.Throttle; SentBrake = d.Brake; SentClutch = d.Clutch;
    }
}
