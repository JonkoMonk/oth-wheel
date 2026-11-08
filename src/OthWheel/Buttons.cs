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
        // The keys this press sent (they depend on the screen, see KeysNow), released with the button.
        public ushort[] PressedVk = Array.Empty<ushort>();
        public float PressedAt, RepeatAt;
        public bool HoldFired;
    }

    static readonly List<Action> _actions = new();
    static Action _ok, _back, _map;
    public static ConfigEntry<bool> Enabled, ShowButtonNumbers, ReplaceGameWheelButtons, CameraInvertX, CameraInvertY, QuickMenuOnMapHold;
    // Keys for the OK / back buttons (A / B by default) outside driving, per screen ([MenuKeys]).
    static ConfigEntry<string> _menuOkKeys, _menuBackKeys, _photoOkKeys, _mapOkKeys, _musicOkKeys, _prevTabKeys, _nextTabKeys;
    static ushort[] _menuOk = Array.Empty<ushort>(), _menuBack = Array.Empty<ushort>(), _photoOk = Array.Empty<ushort>(),
                    _mapOk = Array.Empty<ushort>(), _musicOk = Array.Empty<ushort>(), _prevTab = Array.Empty<ushort>(), _nextTab = Array.Empty<ushort>();
    public static ConfigEntry<float> PhotoLookSpeed, PhotoZoomSpeed;
    // The map screen moves with W/A/S/D (the game binds no arrow keys there).
    static readonly ushort[] MapUp = { 0x57 }, MapDown = { 0x53 }, MapLeft = { 0x41 }, MapRight = { 0x44 };
    static readonly ushort[] VkBackspace = { 0x08 };
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
        ShowButtonNumbers = cfg.Bind("Buttons", "ShowButtonNumbers", false,
            "Show the pressed wheel button and its action in the top-left corner for 3 seconds (handy to check your buttons work). F8 in game switches it (see ShowButtonNumbersKey).");
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
        Add(cfg, "WinchRelease", "P", "Winch: release, back in menus");
        Add(cfg, "WinchExtendAndInteract", "L+Enter", "Winch: extend / interact, OK in menus");
        Add(cfg, "Headlights", "H", "Headlights");
        Add(cfg, "ParkingBrake", "Space", "Parking brake");
        Add(cfg, "Map", "M", "Map (hold: quick action wheel)");
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
        _actions.Add(new Action { Name = "CameraSide", Description = "Smart camera: left / right side of the car", OnPress = SmartCamera.ToggleSide });
        _actions.Add(new Action { Name = "QuickMenu", Description = "Quick action wheel (game key Q): D-pad chooses, A = OK, B = close", OnPress = GameMenus.ToggleQuickMenu });
        _actions.Add(new Action { Name = "WheelMenu", Description = "Wheel mod menu (F3) open / close: D-pad chooses, B = back", OnPress = ToggleWheelMenu });
        _actions.Add(new Action { Name = "PreviousTab", Description = "Menus: previous tab (game key Q, e.g. in the car menu)", OnPress = () => SwitchTab(false) });
        _actions.Add(new Action { Name = "NextTab", Description = "Menus: next tab (game key E, e.g. in the car menu)", OnPress = () => SwitchTab(true) });
        _ok = _actions.First(a => a.Name == "WinchExtendAndInteract");
        _back = _actions.First(a => a.Name == "WinchRelease");
        _map = _actions.First(a => a.Name == "Map");

        QuickMenuOnMapHold = cfg.Bind("Buttons", "QuickMenuOnMapHold", true,
            "Holding the Map button for half a second opens the quick action wheel (game key Q); a short press still opens the map. " +
            "Only while no wheel button is set for QuickMenu.");
        _menuOkKeys = cfg.Bind("MenuKeys", "OK", "Enter", "Key(s) the OK button (WinchExtendAndInteract, A) presses in menus and on the quick action wheel.");
        _menuBackKeys = cfg.Bind("MenuKeys", "Back", "Escape", "Key(s) the back button (WinchRelease, B) presses in menus, photo mode, the map and the music player.");
        _photoOkKeys = cfg.Bind("MenuKeys", "PhotoModeOK", "LeftMouse", "Key(s) the OK button presses in photo mode (take the photo).");
        _mapOkKeys = cfg.Bind("MenuKeys", "MapOK", "LeftMouse", "Key(s) the OK button presses on the map screen.");
        _musicOkKeys = cfg.Bind("MenuKeys", "MusicOK", "E", "Key(s) the OK button presses in the music player (play / pause).");
        _prevTabKeys = cfg.Bind("MenuKeys", "PreviousTab", "Q", "Key(s) the 'previous tab' button presses in menus.");
        _nextTabKeys = cfg.Bind("MenuKeys", "NextTab", "E", "Key(s) the 'next tab' button presses in menus.");
        PhotoLookSpeed = cfg.Bind("Buttons", "PhotoLookSpeed", 2.0f,
            "Photo mode: how fast the D-pad looks around at first (x CameraSpeed); held for a second it gets 3.5x faster.");
        PhotoZoomSpeed = cfg.Bind("Buttons", "PhotoZoomSpeed", 1.0f,
            "Photo mode: how fast the zoom buttons zoom (1 = 10 steps a second at first, speeding up to 50 while held; 2 = twice that).");

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
        _menuOk = Win32.ParseKeys(_menuOkKeys.Value, "MenuKeys.OK");
        _menuBack = Win32.ParseKeys(_menuBackKeys.Value, "MenuKeys.Back");
        _photoOk = Win32.ParseKeys(_photoOkKeys.Value, "MenuKeys.PhotoModeOK");
        _mapOk = Win32.ParseKeys(_mapOkKeys.Value, "MenuKeys.MapOK");
        _musicOk = Win32.ParseKeys(_musicOkKeys.Value, "MenuKeys.MusicOK");
        _prevTab = Win32.ParseKeys(_prevTabKeys.Value, "MenuKeys.PreviousTab");
        _nextTab = Win32.ParseKeys(_nextTabKeys.Value, "MenuKeys.NextTab");
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
            HeldDpad = Vector2.zero; HeldZoom = 0;
            return;
        }

        ShowPresses(profile, now);
        if (profile == null) { ReleaseAll(); _zoomDir = 0; HeldDpad = Vector2.zero; HeldZoom = 0; return; }

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
            if (act == _map && MapHoldOpensQuickMenu(profile)) { MapOrQuickMenu(act, held, now); continue; }
            // On the quick action wheel and the winch point picker the D-pad steps through the items
            // (a press, then repeats while held).
            bool quick = GameMenus.Now == GameMenus.Context.QuickMenu, winch = !quick && GameMenus.WinchPicking;
            if (act.Camera != Vector2.zero && held && (quick || winch))
            {
                if (!act.Down) act.RepeatAt = now + 0.4f;
                if (!act.Down || now >= act.RepeatAt)
                {
                    if (act.Down) act.RepeatAt = now + 0.25f;
                    if (quick) GameMenus.QuickMenuDpad(act.Camera);
                    else GameMenus.WinchDpad(act.Camera);
                }
            }
            if (held == act.Down) continue;
            act.Down = held;
            if (act.OnPress != null)
            {
                if (held) Show(act.OnPress(), now);
                continue;
            }
            if (held)
            {
                act.PressedVk = KeysNow(act);
                if (act == _ok) GameMenus.PointerToMapCentre();   // A clicks on the map: where the circle is
                foreach (var vk in act.PressedVk) Win32.Key(vk, true);
            }
            else
            {
                foreach (var vk in act.PressedVk) Win32.Key(vk, false);
                act.PressedVk = Array.Empty<ushort>();
            }
        }

        // The cockpit mod looks around with these directly (camera actions: x right, y down).
        // Not while the settings menu is open: the D-pad steers the menu then.
        if (Menu.IsOpen) camera = Vector2.zero;
        HeldDpad = camera;
        HeldZoom = Math.Sign(zoom);
        ScrollInMenus(HeldZoom, now);
        var look = CarCamera ? camera : Vector2.zero;
        CockpitLink.LookX = Mathf.Clamp(look.x, -1f, 1f);
        CockpitLink.LookY = Mathf.Clamp(-look.y, -1f, 1f);
        MoveCamera(camera, now, dt);
        _zoomDir = Math.Sign(zoom);
    }

    static void Show(string text, float now)
    {
        if (string.IsNullOrEmpty(text)) return;
        LastText = text; LastTime = now; AlwaysShowUntil = now + 3f;
    }

    // The car camera (D-pad look, zoom, cockpit look) only while the game is in driving controls.
    static bool CarCamera => GameMenus.Now is GameMenus.Context.Driving or GameMenus.Context.Unknown;

    // What a button presses depends on the screen: B (back) is Escape in menus but releases the winch while
    // driving; A (OK) is Enter in menus, the left mouse button in photo mode and on the map, E in the music
    // player; the D-pad is W/A/S/D on the map and only picks items on the quick action wheel.
    static ushort[] KeysNow(Action a)
    {
        var c = GameMenus.Now;
        if (c == GameMenus.Context.Unknown && Cursor.visible) c = GameMenus.Context.Menu;   // no input map: guess
        if (a == _back && Menu.IsOpen) return VkBackspace;   // the mod's own settings menu
        if (a == _ok || a == _back)
        {
            bool ok = a == _ok;
            switch (c)
            {
                case GameMenus.Context.Menu:
                case GameMenus.Context.QuickMenu: return ok ? _menuOk : _menuBack;
                case GameMenus.Context.PhotoCamera: return ok ? _photoOk : _menuBack;
                case GameMenus.Context.MapScreen: return ok ? _mapOk : _menuBack;
                case GameMenus.Context.Music: return ok ? _musicOk : _menuBack;
            }
            return a.Vk;
        }
        if (a.Camera != Vector2.zero)
        {
            if (c is GameMenus.Context.QuickMenu or GameMenus.Context.PhotoCamera || GameMenus.WinchPicking) return Array.Empty<ushort>();
            if (c == GameMenus.Context.MapScreen)
                return a.Camera.y < 0f ? MapUp : a.Camera.y > 0f ? MapDown : a.Camera.x < 0f ? MapLeft : MapRight;
        }
        return a.Vk;
    }

    // Map button: a short press opens the map (on release), held for half a second it opens the quick action wheel.
    const float MapHoldTime = 0.5f;

    static bool MapHoldOpensQuickMenu(WheelProfile profile) =>
        QuickMenuOnMapHold.Value && !profile.Buttons.ContainsKey("QuickMenu");

    static void MapOrQuickMenu(Action act, bool held, float now)
    {
        if (held && !act.Down) { act.Down = true; act.PressedAt = now; act.HoldFired = false; return; }
        if (held && act.Down && !act.HoldFired && now - act.PressedAt >= MapHoldTime)
        {
            act.HoldFired = true;
            Show(GameMenus.ToggleQuickMenu(), now);
            return;
        }
        if (!held && act.Down)
        {
            act.Down = false;
            if (!act.HoldFired) TapAction(act.Name, now);
        }
    }

    // Zoom buttons held this frame (+1 in, -1 out); the map zooms with it smoothly (GameMenus.AfterMapUpdate).
    public static int HeldZoom;
    // Camera buttons (D-pad) held this frame (x right, y down, not inverted); the map slows its pan with it.
    public static Vector2 HeldDpad;

    // Zoom buttons in photo mode (and on the map if its smooth zoom isn't available): mouse wheel notches
    // while held (those screens zoom with the wheel).
    static float _nextNotch;

    // Photo mode zoom was really slow (one notch every 0.1 s): it now speeds up while held, from 10 notches a second
    // to 50 after a second (x PhotoZoomSpeed); the first 0.3 s stay slow for small steps.
    static float _zoomHeldFor;

    static void ScrollInMenus(int dir, float now)
    {
        if (dir == 0 || GameMenus.Now is not (GameMenus.Context.MapScreen or GameMenus.Context.PhotoCamera)) { _zoomHeldFor = 0f; return; }
        if (GameMenus.MapZoomReady) return;
        _zoomHeldFor += Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
        if (now < _nextNotch) return;
        float perSecond = 10f;
        if (GameMenus.Now == GameMenus.Context.PhotoCamera)
            perSecond = Mathf.Lerp(10f, 50f, Mathf.Clamp01((_zoomHeldFor - 0.3f) / 0.7f)) * Mathf.Clamp(PhotoZoomSpeed.Value, 0.25f, 4f);
        _nextNotch = now + 1f / perSecond;
        Win32.MouseWheel(dir);
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
            // With a setup active, only its own devices: a gamepad (e.g. Xbox pad next to a G920) isn't part of
            // the wheel setup, its buttons never fire wheel actions, so don't show them as wheel buttons either.
            if (profile != null && pi < 0) { for (int b = 0; b < VirtualButtons; b++) prev[b] = HeldFor(d, b, now, ReleaseDelay); continue; }
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
            SmartCamera.SetControls(__instance);
            float now = Time.unscaledTime, t = __instance.targetZoomMultiplier;
            float lo = Mathf.Min(__instance.minZoom, __instance.maxZoom), hi = Mathf.Max(__instance.minZoom, __instance.maxZoom);
            if (!_zoomLogged)
            {
                _zoomLogged = true;
                Plugin.Info($"camera zoom: min={__instance.minZoom} max={__instance.maxZoom} speed={__instance.zoomSpeed} target={t}");
            }

            if (_zoomDir == 0 || !Driving(now) || !CarCamera) return;
            float range = hi > lo && hi - lo < 1e6f ? hi - lo : 1f;
            float next = t + _zoomDir * ZoomInSign * ZoomSpeed.Value * range * Time.unscaledDeltaTime;
            __instance.targetZoomMultiplier = hi > lo ? Mathf.Clamp(next, lo, hi) : next;
        }
        catch (Exception e) { Plugin.ErrorOnce("zoom hook", e); }
    }

    // Photo mode look: starts at PhotoLookSpeed x CameraSpeed, 3.5x that after 1 s held (1.2x ramping to 4x over
    // 1.5 s was still "a bit too slow", 2026-11-07).
    const float PhotoLookMaxFactor = 3.5f, PhotoLookRampTime = 1.0f;
    static float _photoLookFor;

    // Only while driving with the mouse cursor hidden; in menus the camera buttons just send their arrow keys.
    // In the cockpit view the cockpit mod reads the buttons itself (CockpitLink.LookX/Y), so no simulated mouse.
    static void MoveCamera(Vector2 dir, float now, float dt)
    {
        // Photo mode's free camera looks around with plain mouse movement (no right button), speeding up
        // while the D-pad is held.
        if (GameMenus.Now != GameMenus.Context.PhotoCamera || dir == Vector2.zero) _photoLookFor = 0f;
        if (GameMenus.Now == GameMenus.Context.PhotoCamera && dir != Vector2.zero)
        {
            if (_rightMouseDown) { Win32.MouseRight(false); _rightMouseDown = false; }
            SmartCamera.Dpad = Vector2.zero;
            _photoLookFor += dt;
            float speed = Mathf.Clamp(PhotoLookSpeed.Value, 0.2f, 10f) * CameraSpeed.Value * Mathf.Lerp(1f, PhotoLookMaxFactor, Mathf.Clamp01(_photoLookFor / PhotoLookRampTime));
            _mouseX += dir.x * speed * dt;
            _mouseY += dir.y * speed * dt;
            int px = (int)_mouseX, py = (int)_mouseY;
            _mouseX -= px; _mouseY -= py;
            Win32.MouseMove(px, py);
            return;
        }
        bool driving = Driving(now) && CarCamera;
        // With the smart camera on, the camera buttons turn the game camera directly (SmartCamera.Dpad).
        var smart = driving && !CockpitLink.CockpitActive && !Menu.IsOpen ? dir : Vector2.zero;
        if (CameraInvertX.Value) smart.x = -smart.x;
        if (CameraInvertY.Value) smart.y = -smart.y;
        SmartCamera.Dpad = SmartCamera.HandlesDpad ? smart : Vector2.zero;
        // While the settings menu is open the D-pad only steers the menu (through its arrow keys).
        if (!driving || dir == Vector2.zero || CockpitLink.CockpitActive || Menu.IsOpen || SmartCamera.HandlesDpad)
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
        return a != null && Tap(a.Vk, now);
    }

    public static bool Tap(ushort[] keys, float now)
    {
        if (keys == null || keys.Length == 0 || !Win32.GameHasFocus()) return false;
        TapRelease(float.MaxValue);
        _tapVk = keys;
        _tapUntil = now + 0.1f;
        foreach (var vk in _tapVk) Win32.Key(vk, true);
        return true;
    }

    // "WheelMenu" button: the F3 settings menu, for wheels without a keyboard in reach (it needs a wheel setup).
    static string ToggleWheelMenu()
    {
        if (Menu.IsOpen) { Menu.Close(); return null; }
        if (Devices.Active == null) return "Wheel menu: set up the wheel first (F3)";
        Menu.Open();
        return "Wheel menu: D-pad chooses, B = back";
    }

    // "Previous tab" / "Next tab" buttons: the game's tab keys (Q / E), only in menus (while driving Q is the
    // quick action wheel).
    static string SwitchTab(bool next)
    {
        var c = GameMenus.Now;
        bool menu = c == GameMenus.Context.Menu || (c == GameMenus.Context.Unknown && Cursor.visible);
        if (!menu || Menu.IsOpen) return "Tabs: only in menus";
        Tap(next ? _nextTab : _prevTab, Time.unscaledTime);
        return null;
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
        GameMenus.ReleaseAll();
        foreach (var act in _actions)
        {
            if (!act.Down) continue;
            act.Down = false;
            foreach (var vk in act.PressedVk) Win32.Key(vk, false);
            act.PressedVk = Array.Empty<ushort>();
        }
        if (_rightMouseDown) { Win32.MouseRight(false); _rightMouseDown = false; }
    }
}
