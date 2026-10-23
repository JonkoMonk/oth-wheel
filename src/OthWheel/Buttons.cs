using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
#endif

namespace OthWheel;

// Wheel buttons press the game's own keyboard keys, so every action behaves exactly as with
// the keyboard (holds, toggles, co-op). Some buttons press two keys: one for driving, one for
// menus (arrow keys and Enter do nothing while driving).
//
// Which wheel button does what comes from the active wheel setup (built-in preset or the setup wizard);
// the game key(s) per action are in the config file ([ButtonKeys]).
//
// The D-pad moves the car camera. The game has no camera keys, only mouse (right button held +
// move) and the gamepad's right stick, so the camera actions simulate the mouse while driving.
internal static class Buttons
{
    class Action
    {
        public string Name, Description;
        public ConfigEntry<string> Keys;
        public ushort[] Vk = Array.Empty<ushort>();
        public bool Down;
        // Mod-internal actions (no game key): runs once per press, returns the text to show on screen.
        public Func<string> OnPress;
        // Camera actions: mouse direction while held (x right, y down).
        public Vector2 Camera;
        // Zoom actions: +1 = zoom in, -1 = zoom out, while held.
        public int Zoom;
    }

    static readonly List<Action> _actions = new();
    public static ConfigEntry<bool> Enabled, ShowButtonNumbers, ReplaceGameWheelButtons, CameraInvertX, CameraInvertY;
    public static ConfigEntry<float> CameraSpeed, ZoomSpeed;
    public static ConfigEntry<string> DisplayToggleKey;
    static ushort[] _displayToggleVk = Array.Empty<ushort>();
    static bool _displayToggleWasDown;

    // The setup wizard uses the buttons itself while it is open.
    public static bool Suspended;

    // For the on-screen display.
    public static string LastText = "";
    public static float LastTime = -100f;
    // Mod actions (e.g. profile switch) are shown even when ShowButtonNumbers is off.
    public static float AlwaysShowUntil = -100f;

    static bool _rightMouseDown;
    static float _lastUpdate, _mouseX, _mouseY;

    // For the wizard: action name and what it does, in the order it asks for them.
    public static IEnumerable<(string Name, string Description)> ActionList => _actions.Select(a => (a.Name, a.Description));

    public static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Buttons", "Enabled", true, "Wheel buttons press game keys (which button does what is part of the wheel setup, made in the setup wizard).");
        ShowButtonNumbers = cfg.Bind("Buttons", "ShowButtonNumbers", true,
            "Show the pressed wheel button and its action in the top-left corner for 3 seconds. F8 in game switches it (see ShowButtonNumbersKey).");
        DisplayToggleKey = cfg.Bind("Buttons", "ShowButtonNumbersKey", "F8",
            "Keyboard key that switches the on-screen button display (ShowButtonNumbers) on and off. Empty = no key.");
        ReplaceGameWheelButtons = cfg.Bind("Buttons", "ReplaceGameWheelButtons", true,
            "Turn off the game's own (partial) wheel button handling so buttons don't fire twice.");
        CameraSpeed = cfg.Bind("Buttons", "CameraSpeed", 900f, "How fast the camera buttons turn the camera (simulated mouse pixels per second).");
        // Both inverted by default: the plain mouse directions felt backwards on the D-pad in testing.
        CameraInvertX = cfg.Bind("Buttons", "CameraInvertX", true, "Swap camera left/right.");
        CameraInvertY = cfg.Bind("Buttons", "CameraInvertY", true, "Swap camera up/down.");
        ZoomSpeed = cfg.Bind("Buttons", "ZoomSpeed", 0.3f, "How fast the camera zooms while a zoom button is held (share of the full zoom range per second; 0.3 = about 3 s from closest to farthest).");

        Add(cfg, "GearUp", "LeftShift", "Shift up");
        Add(cfg, "GearDown", "LeftCtrl", "Shift down");
        Add(cfg, "HiLo", "Tab", "High / low range");
        Add(cfg, "WinchAttach", "O", "Winch: attach / pull");
        Add(cfg, "WinchRelease", "P", "Winch: release");
        Add(cfg, "WinchExtendAndInteract", "L+Enter", "Winch: extend / interact, OK in menus");
        Add(cfg, "Headlights", "H", "Headlights");
        Add(cfg, "ParkingBrake", "Space", "Parking brake");
        Add(cfg, "Map", "M", "Map");
        Add(cfg, "Pause", "Escape", "Pause menu / back");
        Add(cfg, "Horn", "Z", "Horn");
        Add(cfg, "Ignition", "I", "Engine on / off");
        Add(cfg, "ResetCar", "V", "Reset car");
        Add(cfg, "PushCar", "K", "Push car");
        Add(cfg, "CameraLeft", "LeftArrow", "Look left (left in menus)").Camera = new Vector2(-1, 0);
        Add(cfg, "CameraRight", "RightArrow", "Look right (right in menus)").Camera = new Vector2(1, 0);
        Add(cfg, "CameraUp", "UpArrow", "Look up (up in menus)").Camera = new Vector2(0, -1);
        Add(cfg, "CameraDown", "DownArrow", "Look down (down in menus)").Camera = new Vector2(0, 1);
        Add(cfg, "ZoomIn", "", "Camera zoom in (hold)").Zoom = 1;
        Add(cfg, "ZoomOut", "", "Camera zoom out (hold)").Zoom = -1;
        _actions.Add(new Action { Name = "CycleFfbProfile", Description = "Force feedback profile Soft / Normal / Realistic / Heavy", OnPress = Ffb.CycleProfile });
        _actions.Add(new Action { Name = "CockpitView", Description = "Cockpit (first person) view on / off (OTH Cockpit mod)", OnPress = CockpitLink.RequestToggle });

        Reparse();
    }

    static Action Add(ConfigFile cfg, string name, string keys, string desc)
    {
        var a = new Action { Name = name, Description = desc };
        // Zoom has no game key (it moves the camera's zoom directly).
        if (keys.Length > 0)
            a.Keys =cfg.Bind("ButtonKeys", name, keys, $"Game key(s) pressed for '{desc}', joined with '+'. Change only if you rebound keys in the game.");
        _actions.Add(a);
        return a;
    }

    // After a config reload: re-read key names.
    public static void Reparse()
    {
        ReleaseAll();
        foreach (var a in _actions) if (a.Keys != null) a.Vk = Win32.ParseKeys(a.Keys.Value, a.Name);
        _displayToggleVk = Win32.ParseKeys(DisplayToggleKey.Value, DisplayToggleKey.Definition.Key);
    }

    // Keyboard hotkey (F8): button display on/off. Called every frame.
    public static void CheckDisplayToggleKey(float now)
    {
        bool down = _displayToggleVk.Length > 0 && Win32.GameHasFocus() && Win32.IsKeyDown(_displayToggleVk[0]);
        if (down && !_displayToggleWasDown)
        {
            ShowButtonNumbers.Value = !ShowButtonNumbers.Value;
            ShowButtonNumbers.ConfigFile.Save();
            string key = DisplayToggleKey.Value;
            LastText = ShowButtonNumbers.Value ? $"Button display on ({key} to hide)" : $"Button display off ({key} to show)";
            LastTime = now; AlwaysShowUntil = now + 3f;
            Plugin.Info(LastText);
        }
        _displayToggleWasDown = down;
    }

    // A held button's signal drops out for a frame now and then (seen on the R3: repeated "presses"
    // of a held button). A button only counts as released after it has been off for this long.
    // ZoomHold: dropouts on the R3's S2/HOME are < 70 ms; 0.5 s made zoom overrun after release.
    internal const float ReleaseDelay = 0.08f;
    const float ZoomHold = 0.12f;
    const int VirtualButtons = ButtonRef.PovBase + DInput.PovCount * 4;
    // Per device (instance): when each button/hat direction was last seen pressed, and the
    // debounced state of the previous frame (for the on-screen press display).
    static readonly Dictionary<Guid, float[]> _lastOn = new();
    static readonly Dictionary<Guid, bool[]> _prevHeld = new();

    static void TrackRaw(float now)
    {
        foreach (var d in Devices.All)
        {
            if (!_lastOn.TryGetValue(d.Info.Instance, out var on)) _lastOn[d.Info.Instance] = on = new float[VirtualButtons];
            for (int b = 0; b < VirtualButtons; b++)
                if (Devices.IsDown(d, b)) on[b] = now;
        }
    }

    internal static bool HeldFor(DInput.Device d, int button, float now, float delay) =>
        d != null && _lastOn.TryGetValue(d.Info.Instance, out var on) && button >= 0 && button < VirtualButtons
        && on[button] > 0f && now - on[button] < delay;

    // Called every frame after the devices were read.
    public static void Update(float now)
    {
        float dt = Mathf.Clamp(now - _lastUpdate, 0f, 0.1f);
        _lastUpdate = now;
        CockpitLink.LookX = CockpitLink.LookY = 0f;
        TapRelease(now);
        TrackRaw(now);
        var profile = Devices.Active;
        if (Suspended || !Enabled.Value || !Settings.Enabled.Value || !Win32.GameHasFocus())
        {
            // Never leave a key stuck down, e.g. when alt-tabbing while holding the horn.
            ReleaseAll();
            _prevHeld.Clear();
            _zoomDir = 0;
            return;
        }

        ShowPresses(profile, now);
        if (profile == null) { ReleaseAll(); _zoomDir = 0; return; }

        var camera = Vector2.zero;
        int zoom = 0;
        foreach (var act in _actions)
        {
            profile.Buttons.TryGetValue(act.Name, out var bind);
            var dev = bind != null ? Devices.Get(bind.Device) : null;
            if (act.Zoom != 0)
            {
                bool zoomHeld = HeldFor(dev, bind?.Button ?? -1, now, ZoomHold);
                if (zoomHeld) zoom += act.Zoom;
                if (zoomHeld && !act.Down && Driving(now))
                {
                    if (act.Zoom > 0) _zoomInPressAt = now; else _zoomOutPressAt = now;
                }
                act.Down = zoomHeld;
                continue;
            }
            bool held = HeldFor(dev, bind?.Button ?? -1, now, ReleaseDelay);
            if (held) camera += act.Camera;
            if (held == act.Down) continue;
            act.Down = held;
            if (act.OnPress != null)
            {
                if (held) { LastText = act.OnPress(); LastTime = now; AlwaysShowUntil = now + 3f; }
                continue;
            }
            foreach (var vk in act.Vk) Win32.Key(vk, held);
        }

        // The cockpit mod looks around with these directly (camera actions: x right, y down).
        // Not while the settings menu is open: the D-pad steers the menu then.
        if (Menu.IsOpen) camera = Vector2.zero;
        CockpitLink.LookX = Mathf.Clamp(camera.x, -1f, 1f);
        CockpitLink.LookY = Mathf.Clamp(-camera.y, -1f, 1f);
        MoveCamera(camera, now, dt);
        _zoomDir = Math.Sign(zoom);
    }

    // "BOX → ResetCar" in the top-left on every press (when ShowButtonNumbers is on), and in the log.
    static void ShowPresses(WheelProfile profile, float now)
    {
        foreach (var d in Devices.All)
        {
            if (!_prevHeld.TryGetValue(d.Info.Instance, out var prev))
            {
                _prevHeld[d.Info.Instance] = prev = new bool[VirtualButtons];
                for (int b = 0; b < VirtualButtons; b++) prev[b] = HeldFor(d, b, now, ReleaseDelay);
                continue;
            }
            int pi = ProfileIndex(profile, d);
            for (int b = 0; b < VirtualButtons; b++)
            {
                bool held = HeldFor(d, b, now, ReleaseDelay);
                if (held && !prev[b])
                {
                    var names = new List<string>();
                    string label;
                    if (pi >= 0)
                    {
                        var r = new ButtonRef { Device = pi, Button = b };
                        foreach (var kv in profile.Buttons) if (r.Same(kv.Value)) names.Add(kv.Key);
                        label = profile.Label(r);
                    }
                    else label = $"{d.Info.Name} {ButtonRef.Describe(b)}";
                    LastTime = now;
                    LastText = $"Wheel {label}" + (names.Count > 0 ? $"  →  {string.Join(", ", names)}" : "  (not assigned)");
                    Plugin.Info(LastText + $" [{d.Info.Name} {ButtonRef.Describe(b)}]");
                }
                prev[b] = held;
            }
        }
    }

    static int ProfileIndex(WheelProfile p, DInput.Device d)
    {
        if (p == null) return -1;
        for (int i = 0; i < p.Devices.Count; i++)
            if (ReferenceEquals(Devices.Get(i), d)) return i;
        return -1;
    }

    static bool Driving(float now) => now - InputHooks.LastTickTime < 0.5f && !Cursor.visible;

    // Zoom: repeated simulated mouse-wheel notches only gave one step every few seconds in game,
    // so holding moves the game camera's own zoom target directly (ExtraCameraControls.targetZoomMultiplier,
    // see AfterCameraUpdate), from the moment of the press until release.
    // Measured: range 0.5..2 (default 1), one mouse notch "in" moves the target down by ~0.03.
    const int ZoomInSign = -1;
    static int _zoomDir;
    static bool _zoomLogged;

    public static void InstallZoomHook(HarmonyLib.Harmony h)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(ExtraCameraControls), "Update");
            if (m == null) { Plugin.Warn("not found: ExtraCameraControls.Update (game update?), zoom buttons won't work"); return; }
            h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(Buttons), nameof(AfterCameraUpdate)));
            Plugin.Info("hooked ExtraCameraControls.Update");
        }
        catch (Exception e) { Plugin.Error($"hook failed ExtraCameraControls.Update: {e.GetType().Name}: {e.Message}"); }
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(InputAction), nameof(InputAction.WasPressedThisFrame));
            h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(Buttons), nameof(AfterWasPressedThisFrame)));
            Plugin.Info("hooked InputAction.WasPressedThisFrame (zoom button presses for camera mods)");
        }
        catch (Exception e) { Plugin.Error($"hook failed InputAction.WasPressedThisFrame: {e.GetType().Name}: {e.Message}"); }
    }

    // Camera mods (Rudimentary FPP / BonnetCam: zoom in past the closest view for the bonnet camera and
    // first person) watch the game's CameraZoomIn/Out actions for a press, which only the mouse wheel and
    // gamepad give. A zoom button press counts as one such press: for one frame, WasPressedThisFrame on
    // those two actions answers true (every caller in that frame sees it).
    const float PressWindow = 0.25f;
    static float _zoomInPressAt = -100f, _zoomOutPressAt = -100f;
    static int _zoomInFrame = -1, _zoomOutFrame = -1;
    static IntPtr _zoomInAction, _zoomOutAction;

    public static void AfterWasPressedThisFrame(InputAction __instance, ref bool __result)
    {
        if (__result) return;
        try
        {
            IntPtr p = __instance.Pointer;
            if (p == IntPtr.Zero) return;
            if (p == _zoomInAction) __result = Report(ref _zoomInPressAt, ref _zoomInFrame);
            else if (p == _zoomOutAction) __result = Report(ref _zoomOutPressAt, ref _zoomOutFrame);
        }
        catch (Exception e) { Plugin.ErrorOnce("zoom press hook", e); }
    }

    static bool Report(ref float pressAt, ref int frame)
    {
        int f = Time.frameCount;
        if (frame == f) return true;
        if (Time.unscaledTime - pressAt > PressWindow) return false;
        pressAt = -100f;
        frame = f;
        return true;
    }

    static void FindZoomActions()
    {
        var actions = GameInput.instance != null ? GameInput.instance.playerInputActions : null;
        if (actions == null) { _zoomInAction = _zoomOutAction = IntPtr.Zero; return; }
        var cam = actions.CameraOrbitAndZoom;
        _zoomInAction = cam.CameraZoomIn?.Pointer ?? IntPtr.Zero;
        _zoomOutAction = cam.CameraZoomOut?.Pointer ?? IntPtr.Zero;
    }

    public static void AfterCameraUpdate(ExtraCameraControls __instance)
    {
        try
        {
            FindZoomActions();
            float now = Time.unscaledTime, t = __instance.targetZoomMultiplier;
            float lo = Mathf.Min(__instance.minZoom, __instance.maxZoom), hi = Mathf.Max(__instance.minZoom, __instance.maxZoom);
            if (!_zoomLogged)
            {
                _zoomLogged = true;
                Plugin.Info($"camera zoom: min={__instance.minZoom} max={__instance.maxZoom} speed={__instance.zoomSpeed} target={t}");
            }

            if (_zoomDir == 0 || !Driving(now)) return;
            float range = hi > lo && hi - lo < 1e6f ? hi - lo : 1f;
            float next = t + _zoomDir * ZoomInSign * ZoomSpeed.Value * range * Time.unscaledDeltaTime;
            __instance.targetZoomMultiplier = hi > lo ? Mathf.Clamp(next, lo, hi) : next;
        }
        catch (Exception e) { Plugin.ErrorOnce("zoom hook", e); }
    }

    // Only while driving with the mouse cursor hidden; in menus the camera buttons just send their arrow keys.
    // In the cockpit view the cockpit mod reads the buttons itself (CockpitLink.LookX/Y), so no simulated mouse.
    static void MoveCamera(Vector2 dir, float now, float dt)
    {
        bool driving = Driving(now);
        // While the settings menu is open the D-pad only steers the menu (through its arrow keys).
        if (!driving || dir == Vector2.zero || CockpitLink.CockpitActive || Menu.IsOpen)
        {
            if (_rightMouseDown) { Win32.MouseRight(false); _rightMouseDown = false; }
            _mouseX = _mouseY = 0f;
            return;
        }
        if (!_rightMouseDown) { Win32.MouseRight(true); _rightMouseDown = true; }
        if (CameraInvertX.Value) dir.x = -dir.x;
        if (CameraInvertY.Value) dir.y = -dir.y;
        _mouseX += dir.x * CameraSpeed.Value * dt;
        _mouseY += dir.y * CameraSpeed.Value * dt;
        int dx = (int)_mouseX, dy = (int)_mouseY;
        _mouseX -= dx; _mouseY -= dy;
        Win32.MouseMove(dx, dy);
    }

    // Is an action's game key held right now (from the keyboard or from its wheel button, which presses that key)?
    public static bool ActionKeyDown(string name)
    {
        var a = _actions.FirstOrDefault(x => x.Name == name);
        return a != null && a.Vk.Length > 0 && Win32.GameHasFocus() && a.Vk.Any(vk => Win32.IsKeyDown(vk));
    }

    // A short press of an action's game key(s), as if its wheel button was tapped (the shifter switches the
    // range this way). Released again after 0.1 s in TapRelease.
    static ushort[] _tapVk = Array.Empty<ushort>();
    static float _tapUntil;

    public static bool TapAction(string name, float now)
    {
        var a = _actions.FirstOrDefault(x => x.Name == name);
        if (a == null || a.Vk.Length == 0 || !Win32.GameHasFocus()) return false;
        TapRelease(float.MaxValue);
        _tapVk = a.Vk;
        _tapUntil = now + 0.1f;
        foreach (var vk in _tapVk) Win32.Key(vk, true);
        return true;
    }

    static void TapRelease(float now)
    {
        if (_tapVk.Length == 0 || now < _tapUntil) return;
        foreach (var vk in _tapVk) Win32.Key(vk, false);
        _tapVk = Array.Empty<ushort>();
    }

    public static void ReleaseAll()
    {
        TapRelease(float.MaxValue);
        foreach (var act in _actions)
        {
            if (!act.Down) continue;
            act.Down = false;
            foreach (var vk in act.Vk) Win32.Key(vk, false);
        }
        if (_rightMouseDown) { Win32.MouseRight(false); _rightMouseDown = false; }
    }
}
