using System;
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

// Main-thread housekeeping: reloads the config when the file changes and writes a status line
// to the mod loader's log so a test drive can be checked afterwards.
public class StatusBehaviour : MonoBehaviour
{
    public StatusBehaviour(IntPtr ptr) : base(ptr) { }

    float _nextReload, _nextLog, _nextHeartbeat;
    // "Wheel mod active" message the first time the player is in a car (startup is a loading screen, easy to miss),
    // so a new user can see the mod is installed, found the wheel and has force feedback.
    // Shown 4 s after the car appears: the mod takes the wheel over for force feedback 3 s after the car.
    const float BannerDelay = 4f, BannerFor = 10f;
    float _bannerFrom = -1f;
    string _banner;
    long _lastTicks, _lastHb, _lastKbd, _lastOnInput;
    string _lastLine;

    void Update()
    {
        float now = Time.unscaledTime;
        try
        {
            if (now >= _nextReload)
            {
                _nextReload = now + 2f;
                if (Settings.ReloadIfChanged()) Plugin.Info($"config reloaded, enabled={Settings.Enabled.Value} gain={Settings.SteerGain.Value} ffb={Ffb.Profile.Value} x{Ffb.StrengthTrim.Value}");
            }

            // All devices are read every frame here (buttons work in menus too), not only in the
            // driving input hooks.
            Devices.Update(now);
            Wheel.Update();
            CockpitLink.Update();
            Wizard.Update(now);
            Menu.Update(now);
            Buttons.Update(now);
            Shifter.Update(now);
            CarPhysics.Update(now);
            Buttons.CheckDisplayToggleKey(now);
            DirectInputLate.Update(now);
            Ffb.Update(now);
            GearProbe.Update(now);

            float interval = Settings.LogInterval.Value;
            if (interval <= 0f || now < _nextLog) return;
            _nextLog = now + interval;
            LogStatus(now);
        }
        catch (Exception e) { Plugin.ErrorOnce("status", e); }
    }

    void LogStatus(float now)
    {
        long t = InputHooks.Ticks, hb = InputHooks.HandbrakeHits, kb = InputHooks.KbdHits, oi = InputHooks.OnInputHits;
        string bus = CarBus();
        var ffbDev = Devices.FfbDevice;
        // Rounded, so tiny sensor noise doesn't produce a new line every second.
        string line = $"wheel {(Wheel.Connected ? "on" : "OFF")} raw steer={Wheel.RawSteer / 100 * 100} pos={Wheel.Position:0.00} | " +
                      $"wheel steer={Wheel.Steer:0.00} thr={Wheel.Throttle:0.00} brk={Wheel.Brake:0.00} clu={Wheel.Clutch:0.00} | " +
                      $"game steer={InputHooks.GameSteer:0.00} thr={InputHooks.GameThrottle:0.00} brk={InputHooks.GameBrake:0.00} | " +
                      $"sent steer={InputHooks.SentSteer:0.00} thr={InputHooks.SentThrottle:0.00} brk={InputHooks.SentBrake:0.00} clu={InputHooks.SentClutch:0.00} | {bus} | " +
                      $"ffb {Ffb.State} out={Ffb.Output / 100 * 100} damper={Ffb.DamperOut / 100 * 100} align={Ffb.Aligning:0.00} bump={Ffb.BumpForce:0.00} " +
                      $"lock={Ffb.LockForce:0.00} center={Ffb.Centering:0.00} impact={Ffb.ImpactForce:0.00} gameJolt={Ffb.GamePulse:0} " +
                      $"profile={Ffb.Profile.Value} tyres={Ffb.LearnStatus} bad={Ffb.BadValues} dev='{ffbDev?.Status ?? "none"}' | {Devices.Status}";
        bool heartbeat = now >= _nextHeartbeat;
        if (line == _lastLine && !heartbeat) return;
        if (heartbeat) _nextHeartbeat = now + 10f;
        _lastLine = line;
        Plugin.Info($"t={now:0.0} {line} | ticks +{t - _lastTicks} hooks hb+{hb - _lastHb} kbd+{kb - _lastKbd} onInput+{oi - _lastOnInput}");
        _lastTicks = t; _lastHb = hb; _lastKbd = kb; _lastOnInput = oi;
    }

    // Leave the wheel slack and free for other programs when the game closes.
    void OnApplicationQuit()
    {
        Buttons.ReleaseAll();
        Devices.Shutdown();
    }

    GUIStyle _style;

    // "Wheel button N → Action" in the top-left corner, to find out which number a button has.
    void OnGUI()
    {
        try
        {
            float now = Time.unscaledTime;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold };
                _style.normal.textColor = Color.yellow;
            }
            if (_bannerFrom < 0f && InputHooks.LocalVehicleInput != null) _bannerFrom = now + BannerDelay;
            if (_bannerFrom >= 0f && now >= _bannerFrom && now < _bannerFrom + BannerFor)
            {
                string key = Settings.SetupKey.Value;
                _banner ??= Devices.Active == null
                    ? Devices.Connected.Count == 0
                        ? $"Wheel mod {Plugin.Version}: NO wheel found. Plug it in and start its driver software."
                        : $"Wheel mod {Plugin.Version}: this wheel isn't set up yet. Press {key} for the setup wizard."
                    : Devices.Active.Ffb?.Direction == 0
                        ? $"Wheel mod {Plugin.Version} active ({Devices.Active.Name}). Force feedback not tested yet: {key} > force feedback test."
                        : Ffb.Ready
                            ? $"Wheel mod {Plugin.Version} active: {Devices.Active.Name}, force feedback on"
                            : $"Wheel mod {Plugin.Version} active: {Devices.Active.Name}. Force feedback NOT connected yet: click into the game window.";
                GUI.Label(new Rect(30, 80, 1800, 50), _banner, _style);
            }
            Wizard.OnGUI();
            Menu.OnGUI();
            bool show = Buttons.ShowButtonNumbers.Value || now < Buttons.AlwaysShowUntil;
            if (Wizard.IsOpen || !show || now - Buttons.LastTime > 3f) return;
            GUI.Label(new Rect(30, 30, 1400, 50), Buttons.LastText, _style);
        }
        catch (Exception e) { Plugin.ErrorOnce("overlay", e); }
    }

    VehicleBase _vehicle;
    VehicleInput _vehicleOf;

    // What actually arrived in the car physics (VPP input channel, -10000..10000 / 0..10000).
    string CarBus()
    {
        var vi = InputHooks.LocalVehicleInput;
        if (vi == null) return "car: none";
        if (_vehicleOf != vi) { _vehicleOf = vi; _vehicle = vi.gameObject.GetComponent<VPVehicleController>(); }
        var d = _vehicle != null ? _vehicle.data : null;
        if (d == null) return "car: no data";
        int ch = Channel.Input;
        return $"car steer={d.Get(ch, InputData.Steer)} thr={d.Get(ch, InputData.Throttle)} brk={d.Get(ch, InputData.Brake)} hb={d.Get(ch, InputData.Handbrake)}";
    }
}
