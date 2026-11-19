using System.Collections.Generic;
using UnityEngine;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
#endif

namespace OthWheel;

// Update notice, part 2: the message itself (IMGUI, mouse), shown only on the game's menu screens.
// Drawn by hand from boxes and labels; clicks are read from Event.current (no GUI.Button / GUI.Toggle: this
// IL2CPP build strips methods the game doesn't use itself, and a stripped call throws).
// The same file is in OTH Wheel and OTH Cockpit (only the namespace differs): change both together.
// The message stays until Close: the Nexus buttons only open a page. The game's own menu buttons under the message
// don't get its clicks (an invisible catcher on top of the game's menus, see Catch): a click that also pressed the
// game's button there made the game leave the menu, and the message vanished with it (owner's test, 19 Nov 2026).
internal static class UpdatePopup
{
    static bool _tick;
    static string _note = "";
    static float _noteUntil, _menuSince = -1f;
    static GUIStyle _title, _text, _small, _button, _box;
    static GameObject _catcher;
    static RectTransform _catcherRect;
    static Rect _catcherAt;
    static bool _catcherOn;
    static int _drawnFrame = -100;

    // The game's menu screens (main menu, pause menu): its "UI" controls are on and the driving and camera ones
    // are off, so the mouse works and nobody is driving. True after 1.5 s of that (not during a quick flicker).
    // hint: the caller's own reading of the same thing (the wheel mod knows the game's input map by name).
    public static bool OnMenuScreen(float now, bool hint = false)
    {
        // In the background (the web browser in front after a Nexus button) the answer stays as it was.
        if (!Application.isFocused && _menuSince >= 0f) return now - _menuSince >= 1.5f;
        bool menu = hint;
        if (!menu)
            try
            {
                var gi = GameInput.instance;
                if (gi != null)
                {
                    var a = gi.playerInputActions;
                    menu = a.UI.enabled && !a.Driving.enabled && !a.CameraOrbitAndZoom.enabled;
                }
            }
            catch { }
        if (!menu) { _menuSince = -1f; return false; }
        if (_menuSince < 0f) _menuSince = now;
        return now - _menuSince >= 1.5f;
    }

    // Draws the message for these updates. Returns true once the player closed it; dontShow = the box was ticked.
    public static bool Draw(IList<UpdateCheck.Found> list, string footer, out bool dontShow)
    {
        dontShow = false;
        if (_title == null)
        {
            _title = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            _title.normal.textColor = Color.yellow;
            _text = new GUIStyle(GUI.skin.label) { fontSize = 19, wordWrap = true };
            _text.normal.textColor = Color.white;
            _small = new GUIStyle(_text) { fontSize = 15 };
            _small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 18 };
            _box = new GUIStyle(GUI.skin.box) { fontSize = 18, fontStyle = FontStyle.Bold };
        }
        float now = Time.unscaledTime;
        float w = Mathf.Min(820f, Screen.width - 40f), pad = 20f, inner = w - 2 * pad;
        float h = pad + 40f;
        foreach (var f in list) h += 30f + (string.IsNullOrEmpty(f.WhatsNew) ? 0f : 52f) + 48f;
        h += 30f + 44f + 40f + 50f + pad;
        var r = new Rect((Screen.width - w) / 2f, 50f, w, h);
        Catch(r);

        var e = Event.current;
        bool click = e != null && e.type == EventType.MouseDown && e.button == 0;
        Vector2 m = click ? e.mousePosition : Vector2.zero;
        bool Hit(Rect q) => click && m.x >= q.x && m.x <= q.x + q.width && m.y >= q.y && m.y <= q.y + q.height;

        // Three boxes on top of each other: the skin's box is see-through.
        for (int i = 0; i < 3; i++) GUI.Box(r, "");
        float x = r.x + pad, y = r.y + pad;
        GUI.Label(new Rect(x, y, inner, 36f), list.Count > 1 ? "Updates are out" : "An update is out", _title);
        y += 40f;
        bool closed = false;
        foreach (var f in list)
        {
            GUI.Label(new Rect(x, y, inner, 28f), $"{f.Mod} {f.Latest} (you have {f.Installed})", _text);
            y += 30f;
            if (!string.IsNullOrEmpty(f.WhatsNew))
            {
                GUI.Label(new Rect(x + 16f, y, inner - 16f, 50f), f.WhatsNew, _small);
                y += 52f;
            }
            var b = new Rect(x, y, 330f, 38f);
            GUI.Box(b, $"Open the {f.Mod} page on Nexus", _button);
            if (Hit(b)) { _note = UpdateCheck.OpenPage(f.Url); _noteUntil = now + 5f; }
            y += 48f;
        }
        GUI.Label(new Rect(x, y, inner, 28f), now < _noteUntil ? _note : "", _small);
        y += 30f;
        var cb = new Rect(x, y + 4f, 28f, 28f);
        var cbText = new Rect(x + 40f, y, inner - 40f, 36f);
        GUI.Box(cb, _tick ? "X" : "", _box);
        GUI.Label(cbText, "Don't show this again", _text);
        if (Hit(cb) || Hit(new Rect(cbText.x, cbText.y, 260f, cbText.height))) _tick = !_tick;
        y += 44f;
        GUI.Label(new Rect(x, y, inner, 40f), footer, _small);
        y += 40f;
        var close = new Rect(r.x + w - pad - 160f, y, 160f, 40f);
        GUI.Box(close, "Close", _button);
        if (Hit(close)) { closed = true; dontShow = _tick; _tick = false; }

        if (click && Hit(r)) e.Use();
        return closed;
    }

    // An invisible, see-through panel over the message, on a game-UI layer above all of the game's menus: the
    // game's mouse clicks land on it instead of on a menu button under the message. Our own clicks (read above)
    // are not affected.
    static void Catch(Rect r)
    {
        _drawnFrame = Time.frameCount;
        try
        {
            if (_catcher == null)
            {
                _catcher = new GameObject("OTH update message click catcher");
                UnityEngine.Object.DontDestroyOnLoad(_catcher);
                var canvas = _catcher.AddComponent<UnityEngine.Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32767;   // above every game menu
                _catcher.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                var pane = new GameObject("Pane");
                pane.transform.SetParent(_catcher.transform, false);
                var img = pane.AddComponent<UnityEngine.UI.Image>();
                img.color = new Color(0f, 0f, 0f, 0f);
                img.raycastTarget = true;
                _catcherRect = pane.GetComponent<RectTransform>();
                _catcherRect.anchorMin = _catcherRect.anchorMax = _catcherRect.pivot = Vector2.zero;
                _catcherAt = default;
                _catcherOn = true;
                Plugin.Info("update notice: game clicks under the message are caught");
            }
            if (!_catcherOn) { _catcher.SetActive(true); _catcherOn = true; }
            if (r != _catcherAt)
            {
                _catcherAt = r;
                // The message counts from the top of the screen, the game's menus from the bottom.
                _catcherRect.anchoredPosition = new Vector2(r.x, Screen.height - r.y - r.height);
                _catcherRect.sizeDelta = new Vector2(r.width, r.height);
            }
        }
        catch (System.Exception ex) { Plugin.ErrorOnce("update notice click catcher", ex); }
    }

    // Called on every OnGUI: the catcher goes once the message is gone (closed, or hidden off the menu screens),
    // after the mouse button is let go (so the release of the Close click doesn't reach the game either).
    public static void Idle()
    {
        if (!_catcherOn) return;
        int gone = Time.frameCount - _drawnFrame;
        if (gone <= 1) return;
        if (gone < 60 && MouseHeld()) return;
        try { if (_catcher != null) _catcher.SetActive(false); }
        catch (System.Exception ex) { Plugin.ErrorOnce("update notice click catcher off", ex); }
        _catcherOn = false;
    }

    static bool MouseHeld()
    {
        try
        {
            var m = UnityEngine.InputSystem.Mouse.current;
            return m != null && m.leftButton.isPressed;
        }
        catch { return false; }
    }
}
