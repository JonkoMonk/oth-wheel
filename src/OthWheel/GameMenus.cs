using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
#endif

namespace OthWheel;

// Which part of the game has the controls right now (driving, a menu, the quick action wheel, photo mode,
// the map, the music player), so wheel buttons can press what that screen listens to: B = back in menus,
// A = take the photo in photo mode, the D-pad picks an item on the quick action wheel, and so on.
//
// The game keeps one active input map (GameInput.CurrentMap). Its getter returns a Nullable<Map>, which
// il2cpp hands back as null or a boxed Map, so it is called raw and the int read from the box.
//
// The quick action wheel (RadialMenu, keyboard Q) is held open by holding Q, like on the keyboard, and its
// item is picked by writing the direction the mouse would give (mouseDirection) and snapping to it.
internal static unsafe class GameMenus
{
    public enum Context { Unknown, Driving, Menu, QuickMenu, PhotoCamera, MapScreen, Music, Other }

    public static Context Now = Context.Unknown;
    static Context _logged = (Context)(-1);
    static IntPtr _getCurrentMap;
    static float _nextMapRead;
    static bool _mapReadWarned;

    public static bool InMenu => Now is Context.Menu or Context.QuickMenu or Context.PhotoCamera or Context.MapScreen or Context.Music
                                 || (Now == Context.Unknown && Cursor.visible);

    // Looked up a few seconds after start (reading the field runs GameInput's static setup, which should
    // happen in the game's own order first).
    static bool _lookedUp;

    static void LookUp()
    {
        _lookedUp = true;
        try
        {
            var f = typeof(GameInput).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(x => x.Name.StartsWith("NativeMethodInfoPtr_get_CurrentMap"));
            if (f != null) _getCurrentMap = (IntPtr)f.GetValue(null);
            if (_getCurrentMap == IntPtr.Zero) Plugin.Warn("not found: GameInput.CurrentMap (game update?), menu buttons fall back to the old keys");
        }
        catch (Exception e) { Plugin.Error($"GameInput.CurrentMap lookup failed: {e.GetType().Name}: {e.Message}"); }
    }

    public static void Install(HarmonyLib.Harmony h)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(RadialMenu), "Update");
            if (m == null) { Plugin.Warn("not found: RadialMenu.Update (game update?), the D-pad can't pick quick menu items"); return; }
            h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(GameMenus), nameof(AfterRadialUpdate)));
            Plugin.Info("hooked RadialMenu.Update (quick action wheel)");
        }
        catch (Exception e) { Plugin.Error($"hook failed RadialMenu.Update: {e.GetType().Name}: {e.Message}"); }
        Patch(h, typeof(MapController), "Update", nameof(AfterMapUpdate), "map zoom");
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(MapCursorSnapper), "LateUpdate");
            if (m == null) Plugin.Warn("not found: MapCursorSnapper.LateUpdate (game update?), the map circle is only centred in Update");
            else
            {
                h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(GameMenus), nameof(BeforeSnapperLateUpdate)));
                Plugin.Info("hooked MapCursorSnapper.LateUpdate (map circle in the middle)");
            }
        }
        catch (Exception e) { Plugin.Error($"hook failed MapCursorSnapper.LateUpdate: {e.GetType().Name}: {e.Message}"); }
        Patch(h, typeof(StateCarWinchPointSelection), "Open", nameof(AfterCarWinchOpen), "winch points on the car");
        Patch(h, typeof(StateCarWinchPointSelection), "Close", nameof(AfterWinchClose), "winch points on the car");
        Patch(h, typeof(StateWorldWinchPointSelection), "Open", nameof(AfterWorldWinchOpen), "winch anchor points");
        Patch(h, typeof(StateWorldWinchPointSelection), "Close", nameof(AfterWinchClose), "winch anchor points");
        Patch(h, typeof(StateMerchantMenu), "Open", nameof(AfterMerchantOpen), "the car menu's tabs");
        Patch(h, typeof(StateMerchantMenu), "Close", nameof(AfterMerchantClose), "the car menu's tabs");
    }

    public static void Bind(ConfigFile cfg)
    {
        MapOpensOnCar = cfg.Bind("Map", "OpenOnCar", true, "Opening the map while driving: it opens centred on your car.");
        MapDpadSpeed = cfg.Bind("Map", "DpadSpeed", 0.35f,
            "How fast the D-pad moves the map at first, as a share of the game's speed (0.35 = 35%); held longer it speeds up to the full speed.");
        PedalsSwitchTabs = cfg.Bind("Buttons", "PedalsSwitchTabs", true,
            "In the car selection / customisation menu: throttle = next tab, brake = previous tab (the game's E / Q).");
    }

    static void Patch(HarmonyLib.Harmony h, Type t, string method, string postfix, string what)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(t, method);
            if (m == null) { Plugin.Warn($"not found: {t.Name}.{method} (game update?), wheel buttons for {what} work like the keyboard"); return; }
            h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(GameMenus), postfix));
            Plugin.Info($"hooked {t.Name}.{method} ({what})");
        }
        catch (Exception e) { Plugin.Error($"hook failed {t.Name}.{method}: {e.GetType().Name}: {e.Message}"); }
    }

    // Every frame, before the buttons.
    public static void Update(float now)
    {
        if (!_lookedUp && now > 8f) LookUp();
        Now = ReadContext();
        if (Now != _logged)
        {
            _logged = Now;
            Plugin.Info($"game controls: {Now}" + (_mapName != null ? $" (input map {_mapName})" : ""));
        }
        UpdateQuickMenu(now);
        UpdateMerchantTabs(now);
        WatchMapPointer(now);
        _prevContext = Now;
    }

    static Context _prevContext = Context.Unknown;

    // --- map: circle, pointer, opening on the car ---------------------------------------------------
    //
    // On the map the selection circle follows the mouse pointer (game circle mode FollowPointer). With the wheel
    // we switch the game to its Centered mode (AfterMapUpdate). Found in the game's code (2026-11-07): Centered
    // mode does NOT move the circle to the middle; it leaves the circle where the pointer last put it and moves
    // the map under it. So the circle sat wherever the mouse was left, and only got to the middle after A
    // (a click with the pointer held in the middle). Now the mod puts the circle in the middle itself.
    // The pointer: A on the map is a left click, so the pointer is put in the middle when the map opens and
    // right before A clicks. It used to be held there every frame and every frame's few pixels of difference
    // were added up, so a small drift counted as "the mouse moved" after one second. Now only a real move
    // (MouseTakeOverPixels away from where the mod put it) hands the map to the mouse (FollowPointer).
    const float MouseTakeOverPixels = 60f;
    static (int X, int Y)? _cursorSetTo;
    static bool _mouseTookOver, _mapCentredOnCar, _mapFromDriving;
    static float _mapOpenedAt = -100f;

    static void WatchMapPointer(float now)
    {
        if (Now != Context.MapScreen)
        {
            if (_mapOpenedAt >= 0f) RestorePanSpeed();
            _cursorSetTo = null; _mouseTookOver = false; _mapOpenedAt = -100f; _mapCentredOnCar = false;
            _mapTouched = false; _mapRecentres = 0; _circleMoves = 0; _lateMovesLogged = 0; _putBackLogged = 0; _dpadAt = -100f;
            return;
        }
        if (_mapOpenedAt < 0f)
        {
            _mapOpenedAt = now;
            // Opened while driving or from a menu (M, the map button, the quick action wheel, the menu after loading
            // in): open it on the car. The first map after loading in comes from a menu and was left off-centre
            // (JonkoMonk, 2026-11-07). Not straight from a loading screen.
            _mapFromDriving = _prevContext is Context.Driving or Context.QuickMenu or Context.Menu;
            Plugin.Info($"map: opened from {_prevContext}, pointer {(Cursor.visible ? "visible" : "hidden")} {Cursor.lockState}");
        }
        if (Mouse.current != null && Mouse.current.delta.ReadValue().sqrMagnitude > 4f) _realMouseAt = now;
        if (Buttons.HeldDpad != Vector2.zero || Buttons.HeldZoom != 0) { _dpadAt = now; if (now - _mapOpenedAt > 0.15f) _mapTouched = true; }
        if (!Win32.GameHasFocus()) return;
        if (_mouseTookOver)
        {
            // The wheel again (D-pad) and the mouse is still: back to the circle in the middle.
            if (Buttons.HeldDpad != Vector2.zero)
            {
                _mouseTookOver = false;
                _cursorSetTo = null;
                Plugin.Info("map: D-pad used, the circle is back in the middle");
            }
            else return;
        }
        var centre = Win32.GameWindowCentre();
        if (centre == null) return;
        var cur = Win32.CursorPos();
        // The game moves the pointer once itself right after the map opens: keep putting it back for a second.
        if (now - _mapOpenedAt < 1f || _cursorSetTo == null)
        {
            if (cur != centre.Value) Win32.MoveCursor(centre.Value.X, centre.Value.Y);
            _cursorSetTo = centre;
            return;
        }
        float dx = cur.X - _cursorSetTo.Value.X, dy = cur.Y - _cursorSetTo.Value.Y;
        if (dx * dx + dy * dy > MouseTakeOverPixels * MouseTakeOverPixels)
        {
            // Only a mouse that really moved takes over: on the first map after loading in the pointer jumped
            // 87 px by itself 8 s after opening (2026-11-07 log), and the circle went with it. Unity's mouse delta
            // wasn't enough to tell (retest: 64 px "move" while only the D-pad was used), so also not within a
            // second of the D-pad or zoom buttons. (Build 3 also refused while the game hides the pointer, but the
            // game hides it on every map for a wheel player, so a real mouse never got the circle: 2026-11-08 test.)
            if (now - _realMouseAt > 0.5f || now - _dpadAt < 1f)
            {
                if (_putBackLogged++ < 3)
                    Plugin.Info($"map: the pointer moved without the mouse ({cur.X},{cur.Y}, {now - _mapOpenedAt:0.0} s after opening; " +
                                $"last mouse move {now - _realMouseAt:0.0} s ago, D-pad {now - _dpadAt:0.0} s ago), put back in the middle");
                Win32.MoveCursor(centre.Value.X, centre.Value.Y);
                _cursorSetTo = centre;
                return;
            }
            _mouseTookOver = true;
            Plugin.Info($"map: mouse moved ({cur.X},{cur.Y} instead of {_cursorSetTo.Value.X},{_cursorSetTo.Value.Y}, " +
                        $"{now - _mapOpenedAt:0.0} s after opening), the circle follows the mouse now");
        }
    }

    static float _realMouseAt = -100f, _dpadAt = -100f;
    static bool _mapTouched;
    static int _putBackLogged;

    // Right before A clicks on the map: pointer to the middle, where the circle is.
    public static void PointerToMapCentre()
    {
        if (Now != Context.MapScreen || _mouseTookOver) return;
        var centre = Win32.GameWindowCentre();
        if (centre == null) return;
        Win32.MoveCursor(centre.Value.X, centre.Value.Y);
        _cursorSetTo = centre;
    }

    static string _mapName;

    static Context ReadContext()
    {
        if (_getCurrentMap == IntPtr.Zero || Time.unscaledTime < _nextMapRead) return Context.Unknown;
        try
        {
            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(_getCurrentMap, IntPtr.Zero, (void**)null, ref exc);
            // Since game 26.10.30 it can throw while the game is still starting: try again a second later.
            if (exc != IntPtr.Zero)
            {
                _nextMapRead = Time.unscaledTime + 1f;
                if (!_mapReadWarned) { _mapReadWarned = true; Plugin.Warn("GameInput.CurrentMap threw, retrying every second (menu buttons use the old keys until it works)"); }
                return Context.Unknown;
            }
            if (boxed == IntPtr.Zero) { _mapName = "none"; return Context.Other; }
            var map = (GameInput.Map)(*(int*)Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(boxed));
            _mapName = map.ToString();
            return map switch
            {
                GameInput.Map.Driving => Context.Driving,
                GameInput.Map.UI or GameInput.Map.UIWithCameraZoomAndOrbit or GameInput.Map.PhotoModeUI or GameInput.Map.FeedbackMenu
                    or GameInput.Map.CutscenePlaying => Context.Menu,
                GameInput.Map.RadialMenuNavigation => Context.QuickMenu,
                GameInput.Map.PhotoModeFreeCamera => Context.PhotoCamera,
                GameInput.Map.MapScreen => Context.MapScreen,
                GameInput.Map.MusicMenu => Context.Music,
                // Newer menus (game 26.10.30: UIWithCameraZoomOrbitAndMouse = winch and inventory from the quick
                // action wheel) are menus too, so B = Escape there. Before, B pressed the winch key in them.
                _ => _mapName.StartsWith("UI") ? Context.Menu : Context.Other,
            };
        }
        catch (Exception e) { _nextMapRead = Time.unscaledTime + 1f; Plugin.ErrorOnce("input map", e); return Context.Unknown; }
    }

    // --- quick action wheel (keyboard Q) ----------------------------------------------------------

    const ushort VK_Q = 0x51;
    static RadialMenu _radial;
    static bool _holdingQ;
    static float _qDownAt, _retapAt = -1f;
    static Vector2 _pick;          // item direction the D-pad chose, re-applied each frame while open
    static bool _havePick;

    static bool RadialOpen()
    {
        try { return _radial != null && _radial.isOpen; }
        catch { _radial = null; return false; }
    }

    public static void AfterRadialUpdate(RadialMenu __instance)
    {
        try
        {
            _radial = __instance;
            if (!__instance.isOpen) { _havePick = false; return; }
            // The game turns mouse movement into mouseDirection; keep ours while no real mouse moves it.
            if (_havePick) __instance.mouseDirection = _pick;
        }
        catch (Exception e) { Plugin.ErrorOnce("quick menu hook", e); }
    }

    // The quick menu button: open the wheel (hold Q, like the keyboard), or close it with the chosen item.
    public static string ToggleQuickMenu()
    {
        float now = Time.unscaledTime;
        if (_holdingQ || RadialOpen())
        {
            ReleaseQ();
            // Q works as a toggle in some setups: if the wheel is still open shortly after, tap Q once more.
            _retapAt = now + 0.25f;
            return null;
        }
        if (Now is not (Context.Driving or Context.Unknown)) return "Quick menu: only while driving";
        Win32.Key(VK_Q, true);
        _holdingQ = true;
        _qDownAt = now;
        _havePick = false;
        Plugin.Info("quick menu: opened (holding Q)");
        return "Quick menu: D-pad = choose, A = OK, B = close";
    }

    static void ReleaseQ()
    {
        if (!_holdingQ) return;
        Win32.Key(VK_Q, false);
        _holdingQ = false;
    }

    static void UpdateQuickMenu(float now)
    {
        if (_retapAt > 0f && now >= _retapAt)
        {
            _retapAt = -1f;
            if (RadialOpen())
            {
                Plugin.Info("quick menu: still open after releasing Q, tapping Q to close it");
                Win32.Key(VK_Q, true); Win32.Key(VK_Q, false);
            }
        }
        if (!_holdingQ) return;
        // Closed by A/B (Enter/Escape) or by the game: let go of Q. Never hold it while the game isn't in front.
        // Without the wheel's hook (game update?) it can't be seen closing, so Q stays held until the next press.
        bool settled = now - _qDownAt > 0.5f;
        bool known = _radial != null || Now != Context.Unknown;
        bool open = RadialOpen() || Now == Context.QuickMenu;
        if (!Win32.GameHasFocus() || (settled && known && !open))
        {
            ReleaseQ();
            Plugin.Info("quick menu: closed");
        }
    }

    public static void ReleaseAll() { ReleaseQ(); _retapAt = -1f; }

    // D-pad on the quick action wheel: left/right = previous/next item around the circle, up/down = the
    // item at the top/bottom. Called on each press (and repeats while held).
    public static void QuickMenuDpad(Vector2 dir)
    {
        if (!RadialOpen()) return;
        try
        {
            var center = (Vector2)_radial.radialLayout.transform.position;
            var items = new List<(ContextUIRadialMenuItem Item, Vector2 Dir, float Angle)>();
            foreach (var it in _radial.radialMenuItemsTransforms)
            {
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                var d = (Vector2)it.transform.position - center;
                if (d.sqrMagnitude < 1f) continue;
                float a = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;   // 0 = up, clockwise
                items.Add((it, d.normalized, a < 0f ? a + 360f : a));
            }
            if (items.Count == 0) return;
            items.Sort((x, y) => x.Angle.CompareTo(y.Angle));

            int target;
            if (dir.x != 0f)
            {
                var cur = _radial.currentlySelectedRadialMenuItem;
                int i = cur == null ? -1 : items.FindIndex(x => x.Item.Pointer == cur.Pointer);
                if (i < 0) target = dir.x > 0f ? 0 : items.Count - 1;
                else target = (i + (dir.x > 0f ? 1 : -1) + items.Count) % items.Count;
            }
            else
            {
                float want = dir.y < 0f ? 0f : 180f;   // camera direction y: -1 = up
                target = 0;
                for (int i = 1; i < items.Count; i++)
                    if (AngleGap(items[i].Angle, want) < AngleGap(items[target].Angle, want)) target = i;
            }

            _pick = items[target].Dir;
            _havePick = true;
            _radial.mouseDirection = _pick;
            _radial.SnapToMenuItems(_pick);
            Plugin.Info($"quick menu: picked {items[target].Item.name} ({items[target].Angle:0} deg, {items.Count} items)");
        }
        catch (Exception e) { Plugin.ErrorOnce("quick menu D-pad", e); }
    }

    static float AngleGap(float a, float b) { float g = Mathf.Abs(a - b) % 360f; return g > 180f ? 360f - g : g; }

    // Clockwise angle from straight up (0..360) of a point around a centre.
    static float Clockwise(Vector2 d) { float a = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg; return a < 0f ? a + 360f : a; }

    // --- map zoom ---------------------------------------------------------------------------------

    // The map zooms by scaling its rect (MapController minZoom..maxZoom). Mouse wheel notches from the zoom
    // buttons were slow and steppy, so while a zoom button is held the scale is changed smoothly here,
    // keeping the middle of the view in place.
    const float MapZoomPerSecond = 1.0f;   // e^1 = 2.7x per second
    static MapController _mapCtl;
    static bool _mapLogged;

    public static bool MapZoomReady => _mapCtl != null && Now == Context.MapScreen;

    public static void AfterMapUpdate(MapController __instance)
    {
        try
        {
            _mapCtl = __instance;
            KeepCircleMode(__instance);
            OpenOnCar(__instance);
            DpadPanSpeed(__instance);
            int dir = Buttons.HeldZoom;
            if (dir == 0 || Now != Context.MapScreen) return;
            var rect = __instance.mapRect;
            float s = rect.localScale.x;
            float lo = Mathf.Min(__instance.minZoom, __instance.maxZoom), hi = Mathf.Max(__instance.minZoom, __instance.maxZoom);
            if (!_mapLogged)
            {
                _mapLogged = true;
                Plugin.Info($"map zoom: scale {s:0.###} (image {__instance.mapImageRect.localScale.x:0.###}), zoom {lo}..{hi}, " +
                            $"speed {__instance.zoomSpeed}, position {rect.anchoredPosition}");
            }
            if (s <= 0f || hi <= lo) return;
            float next = Mathf.Clamp(s * Mathf.Exp(dir * MapZoomPerSecond * Time.unscaledDeltaTime), lo, hi);
            if (Mathf.Approximately(next, s)) return;
            rect.localScale = new Vector3(next, next, rect.localScale.z);
            rect.anchoredPosition *= next / s;
            __instance.ClampMapPosition();
            try { __instance.mapView.UpdatePlayersNameZoom(next); } catch { }
        }
        catch (Exception e) { Plugin.ErrorOnce("map zoom", e); _mapCtl = null; }
    }

    // Its own try: a map without this mode (game update) keeps its zoom. The game sets the mode back when it
    // thinks the controls changed, so this is checked every frame.
    static void KeepCircleMode(MapController map)
    {
        try
        {
            if (Now != Context.MapScreen) return;
            var snapper = map.mapCursorSnapper;
            if (snapper == null) return;
            var wanted = _mouseTookOver ? MapCursorSnapper.CursorMode.FollowPointer : MapCursorSnapper.CursorMode.Centered;
            if (snapper.mode != wanted) { snapper.SetMode(wanted); Plugin.Info($"map: circle mode {wanted}"); }
            if (wanted == MapCursorSnapper.CursorMode.Centered) CircleToMiddle(map, snapper, false);
        }
        catch (Exception e) { Plugin.ErrorOnce("map circle mode", e); }
    }

    // First map after loading in (2026-11-08 test): the circle stayed off-centre although it was moved every frame
    // ("was at" the same spot each frame = something puts it back, most likely an animation, which runs after
    // Update). LateUpdate comes after the animations: move it there too, right before the game's circle code.
    public static void BeforeSnapperLateUpdate(MapCursorSnapper __instance)
    {
        try
        {
            if (Now != Context.MapScreen || _mouseTookOver || __instance.mode != MapCursorSnapper.CursorMode.Centered) return;
            var map = __instance.mapController ?? _mapCtl;
            if (map != null) CircleToMiddle(map, __instance, true);
        }
        catch (Exception e) { Plugin.ErrorOnce("map circle (late)", e); }
    }

    // Centered mode leaves the circle where it was (see WatchMapPointer), so move it to the middle of the
    // screen, with the game's own screen <-> canvas helpers (the ones its circle code uses).

    static void CircleToMiddle(MapController map, MapCursorSnapper snapper, bool late)
    {
        var circle = snapper.cursorRect;
        if (circle == null) return;
        var canvas = map.canvas;
        Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var rect = snapper.GetScreenRect(circle, cam);
        var middle = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        if ((rect.center - middle).sqrMagnitude < 1f) return;
        var from = MapCursorSnapper.ScreenToWorldOnCanvas(circle, rect.center, cam);
        var to = MapCursorSnapper.ScreenToWorldOnCanvas(circle, middle, cam);
        circle.position += to - from;
        float t = Time.unscaledTime - _mapOpenedAt;
        // Still being put back after half a second: log it a few times per opening (what moves it, and where).
        if (t > 0.5f && _lateMovesLogged < 3)
        {
            _lateMovesLogged++;
            Plugin.Info($"map: circle still being moved back ({_circleMoves + 1} moves, in {(late ? "LateUpdate" : "Update")}, was at {rect.center.x:0},{rect.center.y:0}, {t:0.00} s after opening)");
        }
        // Logged for every opening (a few times): if something keeps moving the circle back, it shows here.
        if (_circleMoves++ < 4)
            Plugin.Info($"map: circle moved to the middle (was at {rect.center.x:0},{rect.center.y:0}, screen {Screen.width}x{Screen.height}, " +
                        $"{Time.unscaledTime - _mapOpenedAt:0.00} s after opening, map '{map.name}' {map.GetInstanceID()}, " +
                        $"canvas {(canvas != null ? canvas.renderMode.ToString() : "none")}, pointer {(Cursor.visible ? "visible" : "hidden")} {Cursor.lockState})");
    }
    static int _circleMoves, _lateMovesLogged;

    // Opened while driving: centre the map on the car, once, a moment after opening (after the game's own setup).
    public static ConfigEntry<bool> MapOpensOnCar;
    public static ConfigEntry<float> MapDpadSpeed;

    // The first map after loading in still opened somewhere else (2026-11-07 retest): the game moves it itself
    // after we centred it. So for the first 2 s after opening, as long as you haven't moved or zoomed it with the
    // wheel, the map is put back on the car whenever it isn't where we left it.
    static Vector2 _mapLeftAt;
    static int _mapRecentres;

    static void OpenOnCar(MapController map)
    {
        if (Now != Context.MapScreen || !_mapFromDriving || !MapOpensOnCar.Value) return;
        float t = Time.unscaledTime - _mapOpenedAt;
        if (t < 0.1f) return;
        try
        {
            if (!map.gameObject.activeInHierarchy) return;
            var rect = map.mapRect;
            if (_mapCentredOnCar)
            {
                if (t > 2f || _mapTouched || (rect.anchoredPosition - _mapLeftAt).sqrMagnitude < 4f) return;
                if (_mapRecentres++ >= 20) return;
                if (_mapRecentres <= 3)
                    Plugin.Info($"map: moved after we centred it ({_mapLeftAt} -> {rect.anchoredPosition}, {t:0.00} s after opening), back on the car");
            }
            var car = InputHooks.LocalVehicleInput;
            if (car == null) { _mapCentredOnCar = true; return; }
            var pos = car.transform.position;
            map.CenterMapOnWorldPosition(pos);
            map.ClampMapPosition();
            _mapLeftAt = rect.anchoredPosition;
            if (!_mapCentredOnCar)
                Plugin.Info($"map: opened on the car ({pos.x:0}, {pos.z:0}), map '{map.name}' {map.GetInstanceID()}");
            _mapCentredOnCar = true;
        }
        catch (Exception e) { Plugin.ErrorOnce("map: centre on the car", e); _mapCentredOnCar = true; }
    }

    // D-pad on the map: the game moves the map at full speed from the first moment (W/A/S/D), so the circle
    // overshot small targets like a safe house. While the D-pad is held the game's pan speed is lowered:
    // MapDpadSpeed of it at first, up to the full speed after 1.5 s held. The keyboard keeps the full speed.
    static float _panSpeedOrig = -1f, _dpadHeldFor;
    static MapController _panOf;

    static void DpadPanSpeed(MapController map)
    {
        try
        {
            if (_panOf == null || _panOf.Pointer != map.Pointer) { _panOf = map; _panSpeedOrig = map.panSpeed; }
            bool held = Now == Context.MapScreen && Buttons.HeldDpad != Vector2.zero;
            _dpadHeldFor = held ? _dpadHeldFor + Time.unscaledDeltaTime : 0f;
            float start = Mathf.Clamp(MapDpadSpeed.Value, 0.05f, 1f);
            float want = held ? _panSpeedOrig * Mathf.Lerp(start, 1f, Mathf.Clamp01((_dpadHeldFor - 0.4f) / 1.1f)) : _panSpeedOrig;
            if (!Mathf.Approximately(map.panSpeed, want)) map.panSpeed = want;
        }
        catch (Exception e) { Plugin.ErrorOnce("map D-pad speed", e); }
    }

    static void RestorePanSpeed()
    {
        try { if (_panOf != null && _panSpeedOrig > 0f) _panOf.panSpeed = _panSpeedOrig; }
        catch { _panOf = null; }
        _dpadHeldFor = 0f;
    }

    // --- car selection menu (the garage / merchant): tabs with the pedals ---------------------------
    //
    // The game switches its tabs there with Q / E (gamepad LB / RB). Throttle = next tab (E), brake = previous (Q),
    // on each press. A pedal must be let go once after the menu opened, so a held brake doesn't switch at once.
    public static ConfigEntry<bool> PedalsSwitchTabs;
    const ushort VK_E = 0x45;
    static bool _merchantOpen, _thrArmed, _brkArmed;
    static float _merchantOpenedAt;

    public static void AfterMerchantOpen() { _merchantOpen = true; _thrArmed = _brkArmed = false; _merchantOpenedAt = Time.unscaledTime; Plugin.Info("car menu: open (pedals switch tabs)"); }
    public static void AfterMerchantClose() { _merchantOpen = false; }

    public static bool InCarMenu => _merchantOpen && Now == Context.Menu;

    static void UpdateMerchantTabs(float now)
    {
        if (!InCarMenu || !PedalsSwitchTabs.Value || !Win32.GameHasFocus() || Menu.IsOpen) { _thrArmed = _brkArmed = false; return; }
        float thr = Wheel.Throttle, brk = Wheel.Brake;
        if (thr < 0.2f) _thrArmed = true;
        if (brk < 0.2f) _brkArmed = true;
        if (now - _merchantOpenedAt < 0.3f) return;
        if (_thrArmed && thr > 0.6f) { _thrArmed = false; Buttons.Tap(new[] { VK_E }, now); Plugin.Info("car menu: throttle -> next tab (E)"); }
        if (_brkArmed && brk > 0.6f) { _brkArmed = false; Buttons.Tap(new[] { VK_Q }, now); Plugin.Info("car menu: brake -> previous tab (Q)"); }
    }

    // --- winch point picker -----------------------------------------------------------------------

    // Choosing where the winch goes (on the car, or the anchor in the world): the arrow keys only jump to
    // a neighbour on screen and can't reach every point, so the D-pad steps through all of them in a loop
    // (right / down = next clockwise, left / up = back). A = Enter picks the selected one.
    static ContextUIWinchPointSelection _winchUi;

    public static void AfterCarWinchOpen(StateCarWinchPointSelection __instance) => SetWinchUi(__instance.contextUIWinchPointSelection);
    public static void AfterWorldWinchOpen(StateWorldWinchPointSelection __instance) => SetWinchUi(__instance.contextUIWinchPointSelection);
    public static void AfterWinchClose() { _winchUi = null; }

    static void SetWinchUi(ContextUIWinchPointSelection ui)
    {
        _winchUi = ui;
        Plugin.Info("winch points: picker open (D-pad steps through the points)");
    }

    public static bool WinchPicking
    {
        get
        {
            if (_winchUi == null || Now != Context.Menu) return false;
            try { return _winchUi.gameObject.activeInHierarchy; }
            catch { _winchUi = null; return false; }
        }
    }

    public static void WinchDpad(Vector2 dir)
    {
        try
        {
            var buttons = new List<(FunButton Button, Vector2 Pos)>();
            foreach (var p in _winchUi.WinchPointsButtons)
            {
                if (p == null || !p.gameObject.activeInHierarchy) continue;
                var b = p.button;
                if (b == null || !b.interactable) continue;
                buttons.Add((b, (Vector2)b.transform.position));
            }
            if (buttons.Count == 0) return;
            var centre = Vector2.zero;
            foreach (var b in buttons) centre += b.Pos;
            centre /= buttons.Count;
            buttons.Sort((x, y) => Clockwise(x.Pos - centre).CompareTo(Clockwise(y.Pos - centre)));

            var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            int i = sel == null ? -1 : buttons.FindIndex(x => x.Button.gameObject.Pointer == sel.Pointer);
            int step = dir.x > 0f || dir.y > 0f ? 1 : -1;   // camera direction y: +1 = down
            int next = i < 0 ? (step > 0 ? 0 : buttons.Count - 1) : (i + step + buttons.Count) % buttons.Count;
            buttons[next].Button.Select();
            Plugin.Info($"winch points: selected {next + 1} of {buttons.Count} ({buttons[next].Button.name})");
        }
        catch (Exception e) { Plugin.ErrorOnce("winch point D-pad", e); }
    }
}
