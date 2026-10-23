using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
#endif
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OthWheel;

public enum FfbProfile { Soft, Normal, Realistic, Heavy, Custom }

// Physics force feedback, computed every frame from VPP's tyre state and sent through our own
// DirectInput client (DInput) to the wheel setup's force feedback device. The game's own DirectInput
// library is NOT used for output: its old DirectInputForceFeedback.dll sends garbage constant-force
// values (dangling pointer bug). The game's wall-jolt values are still read from it and added on top.
//
// Direction and strength per wheel come from the setup (Ffb.Direction is measured by the wizard;
// Strength evens out weak and strong wheels).
//
// Force is computed in "steer space": positive = towards the right end of the wheel.
//
// Feel follows common sim-racing practice: the aligning force builds with grip, peaks just
// before the tyre's grip limit and then drops off (so understeer is felt early), it must not
// "grab" the wheel in big slides, and road/suspension detail stays well below the aligning force.
internal static class Ffb
{
    internal struct Tuning
    {
        public float Strength, SteeringWeight, LightWhenSliding, Bumps, LockStop, LowSpeedCentering, Damper, LowSpeedDamper, Smoothing, Impacts;
    }

    static readonly Tuning Soft = new() { Strength = 0.35f, SteeringWeight = 0.9f, LightWhenSliding = 0.85f, Bumps = 0.10f, LockStop = 0.8f, LowSpeedCentering = 0.12f, Damper = 0.05f, LowSpeedDamper = 0.15f, Smoothing = 0.05f, Impacts = 0.6f };
    // Halfway between Soft and Realistic.
    static readonly Tuning Normal = new() { Strength = 0.43f, SteeringWeight = 0.95f, LightWhenSliding = 0.85f, Bumps = 0.14f, LockStop = 0.9f, LowSpeedCentering = 0.15f, Damper = 0.06f, LowSpeedDamper = 0.185f, Smoothing = 0.0425f, Impacts = 0.8f };
    static readonly Tuning Realistic = new() { Strength = 0.50f, SteeringWeight = 1.0f, LightWhenSliding = 0.85f, Bumps = 0.18f, LockStop = 1.0f, LowSpeedCentering = 0.18f, Damper = 0.07f, LowSpeedDamper = 0.22f, Smoothing = 0.035f, Impacts = 1.0f };
    static readonly Tuning Heavy = new() { Strength = 0.75f, SteeringWeight = 1.15f, LightWhenSliding = 0.80f, Bumps = 0.28f, LockStop = 1.0f, LowSpeedCentering = 0.28f, Damper = 0.10f, LowSpeedDamper = 0.32f, Smoothing = 0.025f, Impacts = 1.2f };

    public static ConfigEntry<bool> Enabled, Invert;
    public static ConfigEntry<FfbProfile> Profile;
    public static ConfigEntry<float> StrengthTrim, Jolt;
    static ConfigEntry<float> Strength, SteeringWeight, LightWhenSliding, Bumps, LockStop, LowSpeedCentering, Damper, LowSpeedDamper, Smoothing, Impacts;
    static ConfigEntry<string> Layout;

    // Diagnostics for the status log.
    public static float Aligning, BumpForce, LockForce, Centering, ImpactForce, Total, GamePulse;
    public static int Output, DamperOut, SatSignVotes, BadValues;
    public static string State = "idle";

    static float _smoothAligning, _smoothBump, _lastTime;

    // VPP's tyre force layout isn't documented here, so it's learned while driving: with grip,
    // the front tyres' lateral force points the way you steer, and the aligning force pulls back.
    static float _corrX, _corrY;
    static int _satSign = -1;
    static bool _useY, _satLearned;

    public static void Bind(ConfigFile cfg)
    {
        const string s = "ForceFeedback";
        Enabled = cfg.Bind(s, "Enabled", true, "Physics force feedback on the wheel.");
        Profile = cfg.Bind(s, "Profile", FfbProfile.Realistic,
            "Soft = light and calm; Normal = between Soft and Realistic; Realistic = balanced; Heavy = strong with lots of road detail; " +
            "Custom = use the Custom* values below. Can also be cycled in game with a wheel button (ButtonMap: CycleFfbProfile).");
        StrengthTrim = cfg.Bind(s, "StrengthTrim", 1.0f, "Multiplies the strength of any profile (0.8 = 20% weaker).");
        Invert = cfg.Bind(s, "Invert", false,
            "Flip all forces on top of the direction measured by the setup wizard. Only needed if the end stop pushes the wheel FURTHER into full lock.");
        Jolt = cfg.Bind(s, "Jolt", 1.0f, "Strength of the game's own crash jolt (0 = off).");
        Layout = cfg.Bind(s, "TyreForceLayout", "auto",
            "Learned automatically in the first corners and saved here (x-normal, x-reversed, y-normal, y-reversed). Set back to auto to re-learn.");

        const string c = "ForceFeedbackCustom";
        Strength = cfg.Bind(c, "Strength", Realistic.Strength, "Overall strength, 0..1 (1.0 = the wheel's full force). Only used when Profile = Custom, like everything in this section.");
        SteeringWeight = cfg.Bind(c, "SteeringWeight", Realistic.SteeringWeight, "How strongly the front tyres pull the wheel back to straight (self-aligning force).");
        LightWhenSliding = cfg.Bind(c, "LightWhenSliding", Realistic.LightWhenSliding, "How much the wheel goes light when the front tyres slide (0 = not at all, 1 = no force left).");
        Bumps = cfg.Bind(c, "Bumps", Realistic.Bumps, "Kicks from the front suspension over rocks, ruts and bumps.");
        LockStop = cfg.Bind(c, "LockStop", Realistic.LockStop, "Firm push back near full steering lock.");
        LowSpeedCentering = cfg.Bind(c, "LowSpeedCentering", Realistic.LowSpeedCentering, "Centering spring when standing still or crawling (fades out with speed).");
        Damper = cfg.Bind(c, "Damper", Realistic.Damper, "Damping at speed (calms the wheel). Pit House adds its own damper on top.");
        LowSpeedDamper = cfg.Bind(c, "LowSpeedDamper", Realistic.LowSpeedDamper, "Extra heaviness when standing still or crawling.");
        Smoothing = cfg.Bind(c, "Smoothing", Realistic.Smoothing, "Seconds of smoothing on the steering weight. 0 = raw.");
        Impacts = cfg.Bind(c, "Impacts", Realistic.Impacts, "Crash kick from the car's sudden deceleration, used when the game sends no jolt (0 = off).");
        ApplyLayout();
    }

    static Tuning Current() => Profile.Value switch
    {
        FfbProfile.Soft => Soft,
        FfbProfile.Normal => Normal,
        FfbProfile.Heavy => Heavy,
        FfbProfile.Custom => new Tuning
        {
            Strength = Strength.Value, SteeringWeight = SteeringWeight.Value, LightWhenSliding = LightWhenSliding.Value, Bumps = Bumps.Value,
            LockStop = LockStop.Value, LowSpeedCentering = LowSpeedCentering.Value, Damper = Damper.Value, LowSpeedDamper = LowSpeedDamper.Value,
            Smoothing = Smoothing.Value, Impacts = Impacts.Value,
        },
        _ => Realistic,
    };

    // Wheel button action: Soft â†’ Normal â†’ Realistic â†’ Heavy â†’ Soft (Custom joins the cycle once selected in the file).
    public static string CycleProfile()
    {
        Profile.Value = Profile.Value switch
        {
            FfbProfile.Soft => FfbProfile.Normal,
            FfbProfile.Normal => FfbProfile.Realistic,
            FfbProfile.Realistic => FfbProfile.Heavy,
            _ => FfbProfile.Soft,
        };
        Profile.ConfigFile.Save();
        Plugin.Info($"FFB profile: {Profile.Value}");
        return $"Force feedback: {Profile.Value}";
    }

    // --- for the settings menu ----------------------------------------------------------------------

    internal sealed class Detail
    {
        public string Label, Help;
        public float Max, Step;
        internal Func<Tuning, float> Get;
        internal ConfigEntry<float> Custom;
    }

    static Detail[] _details;

    // Every tuning value of the profiles, in menu order.
    public static Detail[] Details => _details ??= new[]
    {
        new Detail { Label = "Steering weight", Help = "How strongly the tyres pull the wheel back to straight.", Max = 2f, Step = 0.05f, Get = t => t.SteeringWeight, Custom = SteeringWeight },
        new Detail { Label = "Light when sliding", Help = "How much the wheel goes light when the front tyres slide (0 = not at all).", Max = 1f, Step = 0.05f, Get = t => t.LightWhenSliding, Custom = LightWhenSliding },
        new Detail { Label = "Bumps", Help = "Kicks from rocks, ruts and bumps.", Max = 1f, Step = 0.02f, Get = t => t.Bumps, Custom = Bumps },
        new Detail { Label = "Crash kick", Help = "Kick and shake when you hit something.", Max = 2f, Step = 0.1f, Get = t => t.Impacts, Custom = Impacts },
        new Detail { Label = "Lock stop", Help = "Firm push back near full steering lock.", Max = 1.5f, Step = 0.05f, Get = t => t.LockStop, Custom = LockStop },
        new Detail { Label = "Centering when slow", Help = "Centering spring when standing still or crawling.", Max = 1f, Step = 0.02f, Get = t => t.LowSpeedCentering, Custom = LowSpeedCentering },
        new Detail { Label = "Damper", Help = "Calms the wheel at speed (the wheel's driver adds its own on top).", Max = 0.5f, Step = 0.01f, Get = t => t.Damper, Custom = Damper },
        new Detail { Label = "Damper when slow", Help = "Extra heaviness when standing still or crawling.", Max = 1f, Step = 0.02f, Get = t => t.LowSpeedDamper, Custom = LowSpeedDamper },
        new Detail { Label = "Smoothing", Help = "Seconds of smoothing on the steering weight (0 = raw, more = softer).", Max = 0.2f, Step = 0.005f, Get = t => t.Smoothing, Custom = Smoothing },
    };

    public static float GetDetail(Detail d) => d.Get(Current());

    // Changing any detail switches to Custom, starting from the profile that was active.
    public static void SetDetail(Detail d, float value)
    {
        if (Profile.Value != FfbProfile.Custom)
        {
            var t = Current();
            Strength.Value = t.Strength;
            foreach (var x in Details) x.Custom.Value = x.Get(t);
            Profile.Value = FfbProfile.Custom;
        }
        d.Custom.Value = value;
    }

    public static void ApplyLayout()
    {
        string v = (Layout.Value ?? "auto").Trim().ToLowerInvariant();
        _satLearned = v is "x-normal" or "x-reversed" or "y-normal" or "y-reversed";
        if (!_satLearned) { _corrX = _corrY = 0f; SatSignVotes = 0; return; }
        _useY = v.StartsWith("y");
        _satSign = v.EndsWith("normal") ? -1 : 1;
    }

    static void SaveLayout()
    {
        Layout.Value = $"{(_useY ? "y" : "x")}-{(_satSign < 0 ? "normal" : "reversed")}";
        Layout.ConfigFile.Save();
    }

    public static string LearnStatus => _satLearned ? Layout.Value : $"learning {Mathf.Max(Mathf.Abs(_corrX), Mathf.Abs(_corrY)) / LearnThreshold * 100f:0}%";
    const float LearnThreshold = 0.2f;

    // Every frame, from StatusBehaviour.
    public static void Update(float now)
    {
        try { Tick(now); }
        catch (Exception e) { Plugin.ErrorOnce("ffb", e); Send(0, 0); }
    }

    static float _nextOpen;

    static void Send(int magnitude, int damper)
    {
        var d = Devices.FfbDevice;
        if (d == null || !d.ForceFeedbackRunning) return;
        if (!d.SetForces(magnitude, damper))
            Plugin.Warn($"force feedback update failed ({d.Status}), reopening the wheel");
    }

    public static bool Ready => Devices.FfbDevice?.ForceFeedbackRunning == true;

    static void Tick(float now)
    {
        float dt = Mathf.Clamp(now - _lastTime, 0.001f, 0.1f);
        _lastTime = now;

        // The setup wizard drives the wheel itself while it runs its tests.
        if (Wizard.OwnsForceFeedback) { State = "setup wizard"; return; }

        var vehicle = LocalVehicle();
        var setup = Devices.Active;
        bool driving = Enabled.Value && Settings.Enabled.Value && vehicle != null && Wheel.Connected && setup != null
                       && now - InputHooks.LastTickTime < 0.5f && !Wizard.IsOpen;
        if (!driving)
        {
            State = vehicle == null ? "no car" : !Wheel.Connected ? "no wheel" : Wizard.IsOpen ? "setup wizard open" : "not driving";
            Output = 0; DamperOut = 0;
            Send(0, 0);
            return;
        }
        if (setup.Ffb == null || setup.Ffb.Direction == 0)
        {
            State = $"direction not tested yet ({Settings.SetupKey.Value} > force feedback test)";
            Output = 0; DamperOut = 0;
            return;
        }

        // Take the wheel over from the game's DirectInput library, then open it exclusively.
        if (!Ready)
        {
            if (!DirectInputLate.ReleasedFromGame) { State = "waiting to take over the wheel"; return; }
            if (now < _nextOpen) return;
            _nextOpen = now + 2f;
            if (!Devices.StartForceFeedback(out string why)) { State = "opening: " + why; return; }
        }
        State = "driving";
        var tune = Current();

        // Speed from the rigidbody: VehicleBase.speed is a Fusion networked property that throws
        // until the car has finished spawning.
        var rb = vehicle.cachedRigidbody;
        if (rb == null) { State = "car spawning"; Send(0, 0); return; }
        float speed = Finite(rb.velocity.magnitude);
        float fade = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f, 4f, speed));
        float pos = Wheel.Position;

        ReadFrontTyres(vehicle, out float utilX, out float utilY, out float bump, out float slip);
        Learn(speed, pos, slip, utilX, utilY, dt);
        float lateralUtil = _useY ? utilY : utilX;

        // Self-aligning: builds with grip, drops off from just before the grip peak (slip 0.6) to well
        // past it (1.2). Half strength until the tyre force layout has been learned.
        float trail = 1f - tune.LightWhenSliding * Mathf.Clamp01((slip - 0.6f) / 0.6f);
        float aligningTarget = _satSign * lateralUtil * trail * tune.SteeringWeight * fade * (_satLearned ? 1f : 0.5f);
        _smoothAligning = Smooth(_smoothAligning, aligningTarget, tune.Smoothing, dt);
        Aligning = _smoothAligning;

        // Bumps: short kicks only. Lightly smoothed (15 ms) against sensor jitter and soft-limited,
        // so rough ground gives texture instead of yanking the wheel.
        _smoothBump = Smooth(_smoothBump, bump * tune.Bumps, 0.015f, dt);
        BumpForce = SoftLimit(_smoothBump, 0.35f);

        Centering = -pos * tune.LowSpeedCentering * (1f - fade);
        float over = Mathf.Abs(pos) - 0.95f;
        LockForce = over > 0f ? -Mathf.Sign(pos) * Mathf.Clamp01(over / 0.05f) * tune.LockStop : 0f;

        float strength = Mathf.Clamp01(tune.Strength * Mathf.Max(0f, StrengthTrim.Value) * Mathf.Clamp(setup.Ffb.Strength, 0.1f, 5f));

        // The game's crash jolt, already in DirectInput units (sign as the game chose it),
        // scaled with the profile so it is gentler on Soft and stronger on Heavy.
        GamePulse = Finite(DirectInputLate.ReadGameJolt(now) * Jolt.Value * strength / Realistic.Strength);
        // Our own crash jolt from sudden deceleration, used when the game sends none.
        ImpactForce = GamePulse != 0f ? 0f : ImpactJolt(vehicle, rb, now, dt, tune.Impacts);

        Total = Mathf.Clamp(Finite(Aligning + BumpForce + Centering + LockForce + ImpactForce), -1f, 1f);
        // Direction measured for this wheel (R3: Direction = -1).
        float sign = setup.Ffb.Direction * (Invert.Value ? -1f : 1f);
        Output = Mathf.Clamp(Mathf.RoundToInt(Finite(Total * strength * 10000f * sign + GamePulse)), -10000, 10000);
        DamperOut = Mathf.Clamp(Mathf.RoundToInt(Finite((tune.Damper + tune.LowSpeedDamper * (1f - fade)) * strength * 10000f)), 0, 10000);
        Send(Output, DamperOut);
    }

    // VPP can report NaN/Infinity (seen when the car was stuck on a rock with the winch). A NaN that
    // reaches the output becomes "maximum force" and, inside a smoothing filter, stays there forever.
    static float Finite(float v)
    {
        if (float.IsFinite(v)) return v;
        BadValues++;
        return 0f;
    }

    static float Smooth(float current, float target, float seconds, float dt)
    {
        if (!float.IsFinite(current)) current = 0f;
        float k = seconds <= 0f ? 1f : 1f - Mathf.Exp(-dt / seconds);
        return current + (target - current) * k;
    }

    // Linear for small values, flattening towards Â±limit.
    static float SoftLimit(float v, float limit) => limit * (float)Math.Tanh(v / limit);

    // Recent velocities (car-local, horizontal), one per physics step: the velocity only changes when one ran.
    static readonly Queue<(float t, Vector2 v)> _velHistory = new();
    static float _impactTime = -100f, _impactStrength, _impactDir, _impactDv;
    static bool _impactLogged = true;

    // A crash = the car's horizontal velocity changing by more than braking can do within 0.1 s
    // (hard braking â‰ˆ 1 m/s per 0.1 s). Measured over a window, so it works at any frame rate.
    // Feel: a firm kick (80 ms) in the direction of the sideways part of the hit (head-on: further into
    // the current steering, as the front wheels get shoved), then a short fading shake.
    static float ImpactJolt(VehicleBase v, Rigidbody rb, float now, float dt, float gain)
    {
        var lv3 = v.transform.InverseTransformDirection(rb.velocity);
        var lv = new Vector2(lv3.x, lv3.z);
        if (!float.IsFinite(lv.x) || !float.IsFinite(lv.y)) { _velHistory.Clear(); return 0f; }
        if (_velHistory.Count == 0 || _velHistory.Last().v != lv) _velHistory.Enqueue((now, lv));
        while (_velHistory.Count > 1 && now - _velHistory.Peek().t > 0.1f) _velHistory.Dequeue();

        var change = lv - _velHistory.Peek().v;
        float dv = change.magnitude;
        // Above 30 m/s in 0.1 s is a teleport (reset, respawn), not a crash.
        if (dv > 1.5f && dv < 30f)
        {
            float strength = Mathf.Clamp01((dv - 1.5f) / 4f) * gain;
            if (now - _impactTime > 0.4f)
            {
                _impactTime = now;
                _impactStrength = strength;
                _impactDv = dv;
                _impactLogged = false;
                _impactDir = Mathf.Abs(change.x) > 0.3f * dv ? Mathf.Sign(change.x) : Wheel.Position >= 0f ? 1f : -1f;
            }
            // The hit is still building up: grow to its full size.
            else if (now - _impactTime < 0.1f && strength > _impactStrength) { _impactStrength = strength; _impactDv = dv; }
        }

        float t = now - _impactTime;
        if (!_impactLogged && t >= 0.1f)
        {
            _impactLogged = true;
            Plugin.Info($"crash: speed change {_impactDv:0.0} m/s in 0.1 s -> kick {_impactStrength:0.00} {(_impactDir > 0 ? "right" : "left")}");
        }
        if (t < 0.08f) return _impactStrength * _impactDir;
        if (t < 0.33f) return _impactStrength * 0.5f * Mathf.Sin((t - 0.08f) * 2f * Mathf.PI * 20f) * (1f - (t - 0.08f) / 0.25f);
        return 0f;
    }

    // Learn which tireForce component is lateral and its sign, only in clean, gripping corners.
    static void Learn(float speed, float pos, float slip, float utilX, float utilY, float dt)
    {
        if (_satLearned || speed <= 5f || Mathf.Abs(pos) <= 0.05f || slip >= 0.7f) return;
        _corrX += utilX * pos * dt;
        _corrY += utilY * pos * dt;
        SatSignVotes++;
        if (Mathf.Max(Mathf.Abs(_corrX), Mathf.Abs(_corrY)) <= LearnThreshold) return;
        _satLearned = true;
        _useY = Mathf.Abs(_corrY) > Mathf.Abs(_corrX);
        float corr = _useY ? _corrY : _corrX;
        _satSign = corr > 0 ? -1 : 1;
        Plugin.Info($"FFB: learned tyre force layout: lateral = tireForce.{(_useY ? "y" : "x")}, sign {(_satSign < 0 ? "normal" : "reversed")} " +
                    $"(corrX={_corrX:0.00} corrY={_corrY:0.00}, {SatSignVotes} samples)");
        SaveLayout();
    }

    // Averages the steerable, grounded wheels (non-finite values from VPP are skipped):
    //  utilX/utilY: tyre force components / vertical load (grip in use, roughly -1..1); one of them is lateral
    //  bump: left-minus-right suspension compression speed (m/s)
    //  slip: VPP normalized combined slip (1 = at the grip peak)
    static void ReadFrontTyres(VehicleBase v, out float utilX, out float utilY, out float bump, out float slip)
    {
        utilX = 0f; utilY = 0f; bump = 0f; slip = 0f;
        var ws = v.wheelState;
        if (ws == null) return;
        int n = 0;
        float left = 0f, right = 0f; int nl = 0, nr = 0;
        for (int i = 0; i < ws.Length; i++)
        {
            var w = ws[i];
            if (w == null || !w.steerable) continue;
            var s = w.suspensionState;
            if (w.grounded)
            {
                var f = w.tireForce;
                float load = s.verticalTireLoad;
                if (float.IsFinite(f.x) && float.IsFinite(f.y) && float.IsFinite(load))
                {
                    load = Mathf.Max(load, 100f);
                    utilX += Mathf.Clamp(f.x / load, -1.2f, 1.2f);
                    utilY += Mathf.Clamp(f.y / load, -1.2f, 1.2f);
                    n++;
                }
                else BadValues++;
                float ns = w.normalizedSlip;
                if (float.IsFinite(ns)) slip = Mathf.Max(slip, ns);
            }
            float cs = float.IsFinite(s.contactSpeed) ? Mathf.Clamp(s.contactSpeed, -5f, 5f) : 0f;
            // Wheels come in left/right pairs per axle (VPP order: left first).
            if (i % 2 == 0) { left += cs; nl++; } else { right += cs; nr++; }
        }
        if (n > 0) { utilX /= n; utilY /= n; }
        if (nl > 0 && nr > 0) bump = left / nl - right / nr;
    }

    static VehicleBase _vehicle;
    static VehicleInput _vehicleOf;

    static VehicleBase LocalVehicle()
    {
        var vi = InputHooks.LocalVehicleInput;
        if (vi == null) { _vehicle = null; _vehicleOf = null; return null; }
        if (_vehicleOf != vi) { _vehicleOf = vi; _vehicle = vi.gameObject.GetComponent<VPVehicleController>(); }
        return _vehicle;
    }
}
