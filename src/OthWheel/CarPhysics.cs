using System;
using HarmonyLib;
using UnityEngine;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OthWheel;

public enum ClutchMode { Real, TorqueConverter }
public enum ClutchBite { Sharp, Smooth }

// Changes to the local car's physics (VPP) that the game's input can't express:
//  - Real neutral: the game reads SelectedManualGear = 0 as "no change", so with the H-shifter in neutral the
//    game keeps writing the held gear to the data bus. A postfix on the game's ApplyVehicleInput overwrites the
//    bus gear with 0 (VPP's neutral). Only works where this PC simulates the car (solo, host); elsewhere the
//    shifter's clutch-held neutral still applies.
//  - Real clutch + stalling (only once a clutch pedal was pressed this session). The cars ship with a
//    torque-converter clutch of ~300 Nm (DingDing) that slips and never lets the engine die; the mod switches
//    to a friction-disc clutch that holds far more than the engine makes. VPP's own stall never shows: the
//    game's CarIgnition keeps the starter turning while it thinks the engine is on (tests 2026-10-23: engine
//    hovered at 500-650 rpm with the clutch out and the brakes on). So the mod stops the engine through
//    CarIgnition itself when VPP reports a stall or the clutch drags the engine well below idle.
//    The car's own settings are put back when it's switched off or the car changes.
internal static class CarPhysics
{
    public static ConfigEntry<ClutchMode> Mode;
    public static ConfigEntry<bool> Stalling;
    public static ConfigEntry<ClutchBite> Bite;

    // Set once the clutch pedal has been pressed past halfway this session (an R3 without a clutch pedal
    // still has the clutch axis in its setup; it then never moves).
    public static bool ClutchSeen { get; private set; }
    public static bool RealClutchActive => _applied;

    static VPVehicleController _car;
    static FunselektorFusionVPStandardInput _std;
    static VehicleInput _of;

    // The car's own settings, restored when the real clutch is switched off.
    static bool _origCanStall;
    static Engine.ClutchType _origClutch;
    static float _origClutchTorque, _peakTorque, _origIdleThrottle;
    static bool _applied;

    public static void Bind(ConfigFile cfg)
    {
        Mode = cfg.Bind("Clutch", "Mode", ClutchMode.Real,
            "Real = a clutch you work with the pedal, like a manual car (only once a clutch pedal has been pressed this session, " +
            "so players without one are never affected). TorqueConverter = the game's own clutch: slips like an automatic (a bit like a CVT), fast, never stalls.");
        Stalling = cfg.Bind("Clutch", "Stalling", true,
            "With the real clutch: the engine stalls if you stop in gear or let the clutch out too fast. Restart it with ignition.");
        Bite = cfg.Bind("Clutch", "Bite", ClutchBite.Sharp,
            "With the real clutch: Sharp = grabs right at the top of the pedal travel, like a new clutch. Smooth = takes over gradually as you release the pedal.");
    }

    public static void Install(HarmonyLib.Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(FunselektorFusionVPStandardInput), "ApplyVehicleInput");
            if (m == null) { Plugin.Warn("not found: FunselektorFusionVPStandardInput.ApplyVehicleInput (game update?)"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(CarPhysics), nameof(AfterApplyVehicleInput)));
            Plugin.Info("hooked FunselektorFusionVPStandardInput.ApplyVehicleInput");
        }
        catch (Exception e) { Plugin.Error($"hook failed FunselektorFusionVPStandardInput.ApplyVehicleInput: {e.GetType().Name}: {e.Message}"); }
        foreach (var (name, prefix) in new[] { ("StartEngine", nameof(BeforeStartEngine)), ("ToggleEngine", nameof(BeforeToggleEngine)) })
        {
            try
            {
                var m = AccessTools.Method(typeof(CarIgnition), name);
                if (m == null) { Plugin.Warn($"not found: CarIgnition.{name} (game update?), stalled engines may restart by themselves"); continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(CarPhysics), prefix));
                Plugin.Info($"hooked CarIgnition.{name}");
            }
            catch (Exception e) { Plugin.Error($"hook failed CarIgnition.{name}: {e.GetType().Name}: {e.Message}"); }
        }
    }

    // After a stall the engine stays off until the player turns the key. Test 4 (2026-10-23): the engine was
    // stopped, but the game started it again within 0.1 s (throttle was pressed: it seems to auto-start).
    // Test 5: the player's ignition ALSO goes through StartEngine (not ToggleEngine), so a start is let through
    // only within 1 s of the ignition key going down. The wheel's ignition button presses that same key.
    static bool _stallHold;
    static bool _blockLogged;
    static float _ignitionAt = -100f;

    static bool IgnitionRecently => Buttons.ActionKeyDown("Ignition") || Time.unscaledTime - _ignitionAt < 1f;

    static bool IsLocal(CarIgnition ign)
    {
        try { return _std?.carIgnition != null && ign.Pointer == _std.carIgnition.Pointer; } catch { return false; }
    }

    public static bool BeforeStartEngine(CarIgnition __instance)
    {
        if (!_stallHold || !IsLocal(__instance)) return true;
        if (IgnitionRecently)
        {
            _stallHold = false;
            Plugin.Info("ignition pressed after a stall: engine starts");
            return true;
        }
        if (!_blockLogged) { _blockLogged = true; Plugin.Info("stalled: blocked an automatic engine start (waiting for ignition)"); }
        return false;
    }

    public static bool BeforeToggleEngine(CarIgnition __instance)
    {
        if (_stallHold && IsLocal(__instance))
        {
            _stallHold = false;
            Plugin.Info("ignition turned after a stall: engine starts");
        }
        return true;
    }

    // Every physics step, right after the game has written the input to the car's data bus.
    public static void AfterApplyVehicleInput(FunselektorFusionVPStandardInput __instance)
    {
        try
        {
            if (_std == null || _car == null || __instance.Pointer != _std.Pointer) return;
            if (Shifter.InNeutral) _car.data.Set(Channel.Input, InputData.ManualGear, 0);
            // With the real clutch the pedal alone decides. Test 6 (2026-10-23): in high range the game pressed the
            // clutch by itself while braking to a stop (bus clutch flicking 0/100% with the pedal up), so the car
            // couldn't stall there.
            if (_applied) _car.data.Set(Channel.Input, InputData.Clutch, Mathf.RoundToInt(ClutchCurve(Mathf.Clamp01(InputHooks.SentClutch)) * 10000f));
            // (Not done: keeping the gear while the engine starts. The gearbox itself drops to neutral during a start;
            // forcing the gear made it flick in and out twice, test 8.)
        }
        catch (Exception e) { Plugin.ErrorOnce("neutral", e); }
    }

    // Pedal (1 = pressed) -> clutch input. Both bites use the same strong clutch (so it never slips once out and can
    // drag the engine down uphill); Smooth spreads the take-up over the pedal travel instead of using a weaker clutch.
    // VPP passes engagement × capacity: with 3× the engine's torque, engagement^3 bites at about half travel.
    static float ClutchCurve(float pedal)
    {
        if (Bite.Value != ClutchBite.Smooth) return pedal;
        float engaged = 1f - pedal;
        return 1f - engaged * engaged * engaged;
    }

    // --- every frame --------------------------------------------------------------------------------

    static int _tracedGear = int.MinValue;

    public static void Update(float now)
    {
        try
        {
            if (Wheel.Clutch > 0.5f && !ClutchSeen)
            {
                ClutchSeen = true;
                Plugin.Info($"clutch pedal used: clutch mode {Mode.Value}, stalling {(Stalling.Value ? "on" : "off")}");
            }
            if (Buttons.ActionKeyDown("Ignition")) _ignitionAt = now;
            FollowCar();
            if (_car == null) return;
            ApplyClutch();
            // On its own: a failure here (e.g. the car respawning) must not switch off the rest.
            try { WatchStall(now); } catch (Exception e) { Plugin.ErrorOnce("stall watch", e); }
            LogState(now);

            // For 0.5 s after each lever move: every gearbox gear the car passes through (catches flickers).
            if (now - Shifter.LastChange < 0.5f)
            {
                int box = _car.data.Get(Channel.Vehicle, VehicleData.GearboxGear);
                if (box != _tracedGear)
                {
                    _tracedGear = box;
                    Plugin.Info($"gear trace +{(now - Shifter.LastChange) * 1000f:0} ms: lever {Shifter.Slot}, bus gear {_car.data.Get(Channel.Input, InputData.ManualGear)}, gearbox gear {box}");
                }
            }
            else _tracedGear = int.MinValue;
        }
        catch (Exception e) { Plugin.ErrorOnce("car physics", e); }
    }

    static void FollowCar()
    {
        var vi = InputHooks.LocalVehicleInput;
        if (vi == _of) return;
        Restore();
        _of = vi;
        _car = vi?.gameObject.GetComponent<VPVehicleController>();
        _std = vi?.gameObject.GetComponent<FunselektorFusionVPStandardInput>();
        _applied = false;
        _runningSince = -1f;
        _stallHold = false;
        if (_car == null) return;
        var e = _car.engine;
        var c = _car.clutch;
        _origCanStall = e.canStall;
        _origClutch = c.type;
        _origClutchTorque = c.maxTorqueTransfer;
        _origIdleThrottle = e.maxIdleThrottle;
        string specs = "";
        _peakTorque = 0f;
        try
        {
            var s = new Engine.EngineSpecs();
            _car.m_engine?.GetEngineSpecifications(ref s);
            _peakTorque = s.maxTorqueAtPeak;
            specs = $" | specs: stallRpm={s.stallRpm:0} torque idle={s.maxTorqueAtIdle:0} peak={s.maxTorqueAtPeak:0} Nm";
        }
        catch (Exception x) { specs = " | specs unreadable: " + x.GetType().Name; }
        Plugin.Info($"car physics {_car.name}: engine canStall={_origCanStall} stallBias={e.stallBias:0.00} idle={e.idleRpm:0} idleControl={e.idleControl} maxIdleThrottle={_origIdleThrottle:0.00} | " +
                    $"clutch type={_origClutch} maxTorque={_origClutchTorque:0} unlockRpm={c.unlockRpm:0} lockRpm={c.lockRpm:0}{specs}");
    }

    static void ApplyClutch()
    {
        if (!(Mode.Value == ClutchMode.Real && Settings.Enabled.Value && ClutchSeen)) { Restore(); return; }
        // Holds 3× the engine's peak torque (at least 1000 Nm): never slips once out, so a load (uphill, brakes) can
        // drag the engine down. Sharp bites at the very top of the pedal; Smooth gets its take-up from ClutchCurve.
        // (Test 8: the old Smooth clutch of 1.4× peak slipped uphill instead of stalling.)
        float peak = _peakTorque > 1f ? _peakTorque : 150f;
        float torque = Math.Max(_origClutchTorque, Math.Max(1000f, peak * 3f));
        var e = _car.engine; var c = _car.clutch;
        // The engine's idle control may open the throttle up to maxIdleThrottle (0.8 on the DingDing) to hold idle:
        // far more than a real idle valve, so the engine fought off every stall uphill. Capped while stalling is on.
        float idleThrottle = Stalling.Value ? Math.Min(_origIdleThrottle, 0.25f) : _origIdleThrottle;
        if (_applied && c.type == Engine.ClutchType.FrictionDisc && e.canStall == Stalling.Value && c.maxTorqueTransfer == torque && e.maxIdleThrottle == idleThrottle) return;
        e.canStall = Stalling.Value;
        c.type = Engine.ClutchType.FrictionDisc;
        c.maxTorqueTransfer = torque;
        e.maxIdleThrottle = idleThrottle;
        _applied = true;
        Plugin.Info($"real clutch on {_car.name}: bite {Bite.Value} (holds {torque:0} Nm), idle throttle {_origIdleThrottle:0.00} -> {idleThrottle:0.00}, stalling {(Stalling.Value ? "on" : "off")}");
    }

    static void Restore()
    {
        if (!_applied) return;
        _applied = false;
        try
        {
            if (_car == null || _car.WasCollected) return;
            _car.engine.canStall = _origCanStall;
            _car.clutch.type = _origClutch;
            _car.clutch.maxTorqueTransfer = _origClutchTorque;
            _car.engine.maxIdleThrottle = _origIdleThrottle;
            Plugin.Info($"clutch back to the game's own on {_car.name}");
        }
        catch { }   // car already gone
    }

    // --- stalling -----------------------------------------------------------------------------------

    static float _runningSince = -1f, _lugSince = -1f;

    // VPP's engine rpm can't be used to spot a stall: test 3 (2026-10-23) showed it held at 600-650 rpm with
    // the car braked to a stop in gear, clutch out. The transmission rpm (wheel side of the clutch) is used
    // instead; car speed alone wrongly stalled a flipped car (test 5).
    static bool _transTrusted;
    static float _lastDragged, _dragRate;

    // The car's ratio for a gearbox gear (reverse = negative gears), 1 if unknown.
    static float GearRatio(int gear)
    {
        try
        {
            var g = _car.gearbox;
            var r = gear > 0 ? g.forwardGearRatios : g.reverseGearRatios;
            int i = Math.Abs(gear) - 1;
            if (r != null && i >= 0 && i < r.Length) { float v = Math.Abs(r[i]); if (v > 0.01f && float.IsFinite(v)) return v; }
        }
        catch { }
        return 1f;
    }

    static float _stallTraceUntil;
    static string _stallTraceLast = "";

    static void WatchStall(float now)
    {
        if (!_applied || !Stalling.Value) { _lugSince = -1f; _stallHold = false; return; }
        var d = _car.data;
        // For 2 s after a stall: does the engine really stay off?
        if (now < _stallTraceUntil)
        {
            string s = $"working={d.Get(Channel.Vehicle, VehicleData.EngineWorking)} starting={d.Get(Channel.Vehicle, VehicleData.EngineStarting)} rpm={d.Get(Channel.Vehicle, VehicleData.EngineRpm) / 1000 / 100 * 100} key={d.Get(Channel.Input, InputData.Key)}";
            if (s != _stallTraceLast) { _stallTraceLast = s; Plugin.Info($"after stall +{(now - _stallTraceUntil + 2f) * 1000f:0} ms: {s}"); }
        }
        if (_stallHold) return;
        bool running = d.Get(Channel.Vehicle, VehicleData.EngineWorking) != 0 && d.Get(Channel.Vehicle, VehicleData.EngineStarting) == 0;
        if (!running) { _runningSince = -1f; _lugSince = -1f; _lastDragged = 0f; _dragRate = 0f; return; }
        if (_runningSince < 0f) _runningSince = now;
        // Give a freshly started engine a moment to settle.
        if (now - _runningSince < 1.5f) return;

        float rpm = d.Get(Channel.Vehicle, VehicleData.EngineRpm) / 1000f;
        float idle = _car.engine.idleRpm;
        int gear = d.Get(Channel.Vehicle, VehicleData.GearboxGear);
        float clutch = Mathf.Clamp01(InputHooks.SentClutch);   // the pedal (the bus can hold the game's own clutch help)
        float throttle = d.Get(Channel.Input, InputData.Throttle) / 10000f;
        // Gearbox output rpm, driven by the wheels (measured: engine 4300 / transmission 1750 in 2nd = ratio 2.5,
        // the range is after it). × the gear's ratio = what the engine is dragged to with the clutch out.
        // Wheels spinning freely (car on its roof, wheelspin in mud) keep it up, so those don't stall.
        float trans = Math.Abs(d.Get(Channel.Vehicle, VehicleData.TransmissionRpm) / 1000f);
        float dragged = trans * GearRatio(gear);
        var rb = _car.cachedRigidbody;
        float speed = rb != null ? rb.velocity.magnitude : 0f;

        // Only trusted once it has shown real values while driving in gear with the clutch out.
        if (!_transTrusted && gear != 0 && clutch < 0.05f && rpm > idle * 1.2f && dragged > idle)
        {
            _transTrusted = true;
            Plugin.Info($"transmission rpm works: engine {rpm:0}, transmission {trans:0} x ratio {GearRatio(gear):0.00} = {dragged:0}, {speed * 3.6f:0} km/h");
        }

        // How fast the dragged rpm climbs (smoothed): a car that's pulling away gets the engine back up by itself.
        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        _dragRate = Mathf.Lerp(_dragRate, (dragged - _lastDragged) / dt, Mathf.Clamp01(dt / 0.15f));
        _lastDragged = dragged;

        bool vppStalled = d.Get(Channel.Vehicle, VehicleData.EngineStalled) != 0;
        // Dragged down: in gear, clutch (mostly) out, engine dragged far below idle and not coming back up within
        // half a second (low 1st on flat ground crawls away; uphill, brake held or 2nd high doesn't).
        // Before the transmission rpm is trusted: practically standing still without throttle.
        float gap = idle * 0.5f - dragged;
        bool recovering = _dragRate > 0f && gap / _dragRate < 0.5f;
        bool lugging = gear != 0 && clutch < 0.3f && (_transTrusted ? gap > 0f && !recovering : speed < 0.5f && throttle < 0.2f);
        if (lugging) { if (_lugSince < 0f) _lugSince = now; }
        else _lugSince = -1f;
        if (!vppStalled && !(lugging && now - _lugSince > (_transTrusted ? 0.3f : 0.6f))) return;

        try { _std.carIgnition?.StopEngine(); }
        catch (Exception e) { Plugin.ErrorOnce("stop engine", e); return; }
        _stallHold = true; _blockLogged = false;
        _stallTraceUntil = now + 2f; _stallTraceLast = "";
        _runningSince = -1f; _lugSince = -1f;
        Plugin.Info($"engine stalled ({(vppStalled ? "physics" : _transTrusted ? $"gear {gear}, engine dragged to {dragged:0} rpm (rising {_dragRate:0}/s), idle {idle:0}, {speed * 3.6f:0.0} km/h" : $"standing still in gear {gear}, clutch out")}): engine stopped");
        Buttons.LastText = "Engine stalled: press ignition to restart"; Buttons.LastTime = now; Buttons.AlwaysShowUntil = now + 3f;
    }

    static string _last;
    static float _nextLog;

    // Clutch/engine state while it changes (at most 5 lines a second).
    static void LogState(float now)
    {
        if (!_applied || now < _nextLog) return;
        var d = _car.data;
        var e = _car.m_engine;
        bool stalled = d.Get(Channel.Vehicle, VehicleData.EngineStalled) != 0;
        int rpm = d.Get(Channel.Vehicle, VehicleData.EngineRpm) / 1000;
        float lockRatio = e != null ? e.sensorClutchLock : -1f;
        string state = $"stalled={stalled}/{(e != null && e.sensorStalled)} gear={d.Get(Channel.Vehicle, VehicleData.GearboxGear)} " +
                       $"clutchIn={d.Get(Channel.Input, InputData.Clutch) / 10000f:0.0} lock={lockRatio:0.0} rpm={rpm / 50 * 50} trans={Math.Abs(d.Get(Channel.Vehicle, VehicleData.TransmissionRpm) / 1000) / 50 * 50}";
        if (state == _last) return;
        _last = state;
        _nextLog = now + 0.2f;
        Plugin.Info($"clutch: {state}");
    }
}
