using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
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
        Patch(h, typeof(StateCarWinchPointSelection), "Open", nameof(AfterCarWinchOpen), "winch points on the car");
        Patch(h, typeof(StateCarWinchPointSelection), "Close", nameof(AfterWinchClose), "winch points on the car");
        Patch(h, typeof(StateWorldWinchPointSelection), "Open", nameof(AfterWorldWinchOpen), "winch anchor points");
        Patch(h, typeof(StateWorldWinchPointSelection), "Close", nameof(AfterWinchClose), "winch anchor points");
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
        KeepMapCursorCentred();
    }

    // On the map the selection circle follows the mouse pointer, so with the wheel it sat wherever the mouse
    // was left. While the map is open the pointer is held in the middle of the game window (the D-pad moves
    // the map under it, zoom keeps the middle in place), until the real mouse moves it: then it's left alone
    // until the map is opened again.
    // The game moves the pointer once itself when the map opens (it gave up after one frame on 2026-10-27,
    // "mouse moved" 7 ms after opening), so: no checks for the first second, and only a pointer that is off
    // on two frames in a row counts as the real mouse (a one-off jump is put back).
    static (int X, int Y)? _cursorSetTo;
    static bool _mouseTookOver;
    static float _mapOpenedAt = -100f;
    static int _offFrames;

    static void KeepMapCursorCentred()
    {
        if (Now != Context.MapScreen || !Win32.GameHasFocus())
        {
            _cursorSetTo = null;
            if (Now != Context.MapScreen) { _mouseTookOver = false; _mapOpenedAt = -100f; }
            return;
        }
        float now = Time.unscaledTime;
        if (_mapOpenedAt < 0f) _mapOpenedAt = now;
        if (_mouseTookOver) return;
        var centre = Win32.GameWindowCentre();
        if (centre == null) return;
        var cur = Win32.CursorPos();
        bool off = _cursorSetTo != null && (Math.Abs(cur.X - _cursorSetTo.Value.X) > 2 || Math.Abs(cur.Y - _cursorSetTo.Value.Y) > 2);
        _offFrames = off && now - _mapOpenedAt > 1f ? _offFrames + 1 : 0;
        if (_offFrames >= 2)
        {
            _mouseTookOver = true;
            Plugin.Info($"map: mouse moved ({cur.X},{cur.Y} instead of {_cursorSetTo.Value.X},{_cursorSetTo.Value.Y}), no longer keeping the pointer in the middle");
            return;
        }
        if (cur != centre.Value) Win32.MoveCursor(centre.Value.X, centre.Value.Y);
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
                _ => Context.Other,
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
            return "Quick menu: OK";
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
