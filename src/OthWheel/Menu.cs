using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OthWheel;

// In-game settings menu (setup key, default F3, once a wheel setup is active). A panel on the left; the car
// stays drivable, so force feedback, steering and clutch can be tuned while driving.
// Keys: arrow up/down = choose, left/right = change (right also opens), Backspace = back, Delete = remove a
// button. The wheel's D-pad presses the arrow keys, so it works too (its camera movement is paused meanwhile).
// Every change is saved at once. The step-by-step setup (new wheel, force feedback test, rotation, shifter
// setup, share, report) is under "Setup tools" and opens the setup wizard.
internal static class Menu
{
    sealed class Item
    {
        public string Label, Help = "";
        public Func<string> Value;
        public Action<int> Change;   // -1 / +1 (left / right)
        public Action Open;          // right arrow on a section or command
    }

    enum Page { Main, Ffb, Steering, Clutch, Buttons }

    static bool _open;
    static Page _page;
    static int _sel;
    static readonly Dictionary<Page, int> _lastSel = new();
    static string _note = "";
    static float _noteUntil;

    public static bool IsOpen => _open;

    public static void Open()
    {
        _open = true;
        _page = Page.Main;
        _sel = 0;
        _waitAction = null;
        foreach (var vk in Keys) _keyDown[vk] = Win32.IsKeyDown(vk);   // a key held while opening doesn't count
        Plugin.Info("settings menu opened");
    }

    public static void Close()
    {
        _open = false;
        _waitAction = null;
        Plugin.Info("settings menu closed");
    }

    // --- keys -------------------------------------------------------------------------------------

    const ushort VK_BACK = 0x08, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_DELETE = 0x2E;
    static readonly ushort[] Keys = { VK_BACK, VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN, VK_DELETE };
    static readonly Dictionary<ushort, bool> _keyDown = new();
    static readonly Dictionary<ushort, float> _repeatAt = new();

    // A press, then repeats while held (after 0.4 s, 10 per second).
    static bool Pressed(ushort vk, float now, bool repeat = true)
    {
        bool down = Win32.IsKeyDown(vk);
        _keyDown.TryGetValue(vk, out bool was);
        _keyDown[vk] = down;
        if (down && !was) { _repeatAt[vk] = now + 0.4f; return true; }
        if (down && repeat && _repeatAt.TryGetValue(vk, out float at) && now >= at) { _repeatAt[vk] = now + 0.1f; return true; }
        return false;
    }

    // --- every frame ------------------------------------------------------------------------------

    public static void Update(float now)
    {
        if (!_open) return;
        try { Tick(now); }
        catch (Exception e) { Plugin.ErrorOnce("menu", e); Close(); }
    }

    static void Tick(float now)
    {
        if (Devices.Active == null) { Close(); return; }
        if (!Win32.GameHasFocus()) { foreach (var vk in Keys) _keyDown[vk] = true; return; }
        if (_waitAction != null) { WaitForButton(now); return; }

        var items = Items(_page);
        _sel = Mathf.Clamp(_sel, 0, items.Count - 1);
        if (Pressed(VK_UP, now)) _sel = (_sel + items.Count - 1) % items.Count;
        if (Pressed(VK_DOWN, now)) _sel = (_sel + 1) % items.Count;
        var it = items[_sel];
        bool right = Pressed(VK_RIGHT, now, it.Change != null);
        bool left = Pressed(VK_LEFT, now, it.Change != null);
        if (Pressed(VK_BACK, now, false)) { Back(); return; }
        if (Pressed(VK_DELETE, now, false) && _page == Page.Buttons) ClearButton(it);

        if (it.Change != null)
        {
            if (right) { it.Change(1); Settings.MarkSaved(); }
            if (left) { it.Change(-1); Settings.MarkSaved(); }
        }
        else if (right && it.Open != null) it.Open();
        // Left on a line without a value goes back (the wheel has no Backspace).
        else if (left && _page != Page.Main) Back();
    }

    static void Go(Page p)
    {
        _lastSel[_page] = _sel;
        _page = p;
        _sel = p == Page.Main && _lastSel.TryGetValue(p, out int s) ? s : 1;   // 0 is "Back" in sections
    }

    static void Back()
    {
        if (_page == Page.Main) Close();
        else Go(Page.Main);
    }

    static void Note(string text)
    {
        _note = text;
        _noteUntil = Time.unscaledTime + 4f;
    }

    // --- pages ------------------------------------------------------------------------------------

    static List<Item> Items(Page p)
    {
        var list = new List<Item>();
        if (p != Page.Main) list.Add(new Item { Label = "< Back", Open = () => Go(Page.Main), Help = "Back to the main menu (or Backspace)." });
        switch (p)
        {
            case Page.Main:
                list.Add(Section("Force feedback", Page.Ffb, "Profile, strength and every detail of the force feedback."));
                list.Add(Section("Steering & pedals", Page.Steering, "Steering lock, sensitivity, deadzones."));
                list.Add(Section("Clutch & shifter", Page.Clutch, "Real clutch, stalling, bite, H-shifter."));
                list.Add(Section("Buttons", Page.Buttons, "Which wheel button does what."));
                list.Add(new Item
                {
                    Label = "Setup tools", Value = () => ">",
                    Help = "Step by step: set up a new wheel, force feedback test, wheel rotation, H-shifter setup, share your setup, support report. (Driving pauses while it's open.)",
                    Open = () => { Close(); Wizard.OpenTools(Time.unscaledTime); },
                });
                list.Add(new Item { Label = "Close", Open = Close, Help = $"Or press {Settings.SetupKey.Value}." });
                break;

            case Page.Ffb:
                list.Add(Bool("Force feedback", Ffb.Enabled, "Physics force feedback on the wheel."));
                list.Add(Choice("Profile", Ffb.Profile, "Soft, Normal, Realistic, Heavy, or Custom (your own values below). Changing a value below switches to Custom."));
                list.Add(Num("Overall strength", Ffb.StrengthTrim, 0f, 2f, 0.05f, Pct, "Multiplies every profile (100% = as designed)."));
                foreach (var d in Ffb.Details)
                {
                    var det = d;
                    list.Add(new Item
                    {
                        Label = det.Label, Help = det.Help,
                        Value = () => Ffb.GetDetail(det).ToString(det.Step < 0.01f ? "0.000" : "0.00"),
                        Change = dir => Ffb.SetDetail(det, Step(Ffb.GetDetail(det), dir, 0f, det.Max, det.Step)),
                    });
                }
                break;

            case Page.Steering:
                list.Add(Num("Full lock at", Settings.LockDegrees, 90f, 1440f, 10f, v => $"{v:0}°",
                    "Wheel rotation (lock to lock) that gives full steering lock in the car. Your wheel's own rotation is set in Setup tools."));
                list.Add(Num("Steering sensitivity", Settings.SteerGain, 0.5f, 3f, 0.05f, v => $"{v:0.00}", "Higher = more steering for the same wheel turn."));
                list.Add(Num("Steering linearity", Settings.SteerLinearity, 0.5f, 3f, 0.1f, v => $"{v:0.0}", "1.0 = linear. Higher = calmer around center, faster near full lock."));
                list.Add(Num("Steering deadzone", Settings.SteerDeadzone, 0f, 0.2f, 0.005f, Pct, "Small zone around center that counts as straight."));
                list.Add(Bool("Invert steering", Settings.SteerInvert, "Swap left and right."));
                list.Add(Num("Throttle deadzone", Settings.ThrottleDeadzone, 0f, 0.3f, 0.01f, Pct, "Pedal travel ignored at the top."));
                list.Add(Num("Throttle full at", Settings.ThrottleFullAt, 0.5f, 1f, 0.01f, Pct, "Pedal position that already counts as 100%."));
                list.Add(Num("Brake deadzone", Settings.BrakeDeadzone, 0f, 0.3f, 0.01f, Pct, "Pedal travel ignored at the top."));
                list.Add(Num("Brake full at", Settings.BrakeFullAt, 0.5f, 1f, 0.01f, Pct, "Pedal position that already counts as 100% (lower = less force needed for full braking)."));
                break;

            case Page.Clutch:
                list.Add(new Item
                {
                    Label = "Clutch", Help = "Real = you work the clutch, the engine can stall. Torque converter = the game's own clutch: slips like an automatic (a bit like a CVT), fast, never stalls.",
                    Value = () => CarPhysics.Mode.Value == ClutchMode.Real ? "Real" : "Torque converter",
                    Change = _ => CarPhysics.Mode.Value = CarPhysics.Mode.Value == ClutchMode.Real ? ClutchMode.TorqueConverter : ClutchMode.Real,
                });
                list.Add(Choice("Clutch bite", CarPhysics.Bite, "Real clutch only. Sharp = grabs right at the top, like a new clutch. Smooth = takes over gradually."));
                list.Add(Bool("Stalling", CarPhysics.Stalling, "Real clutch only. Stop in gear or drop the clutch and the engine dies; restart with ignition."));
                list.Add(Num("Clutch deadzone", Settings.ClutchDeadzone, 0f, 0.3f, 0.01f, Pct, "Pedal travel ignored at the top."));
                list.Add(Num("Clutch full at", Settings.ClutchFullAt, 0.5f, 1f, 0.01f, Pct, "Pedal position that already counts as fully pressed."));
                list.Add(Bool("H-shifter", Shifter.Enabled, "Use the H-pattern shifter (set it up in Setup tools). Off = paddles / shift buttons only."));
                list.Add(Choice("Range as gears", Shifter.RangeAsGears,
                    "Auto: cars with 2 gears become a 4-speed on the shifter: 1 = 1st low, 2 = 2nd low, 3 = 1st high, 4 = 2nd high, R = reverse low. Off: range stays on its own button."));
                list.Add(new Item
                {
                    Label = "Status",
                    Value = () => (Shifter.Active != null ? $"shifter: {Shifter.Active.Name}" : "no shifter") + ", " +
                                  (Devices.Active?.Clutch == null ? "no clutch pedal" : !CarPhysics.ClutchSeen ? "clutch not pressed yet" :
                                   CarPhysics.RealClutchActive ? "real clutch on" : "torque converter"),
                    Help = "The real clutch starts after the clutch pedal has been pressed once this session, so players without a clutch pedal are never affected.",
                });
                break;

            case Page.Buttons:
                foreach (var (name, desc) in Buttons.ActionList)
                {
                    string n = name;
                    list.Add(new Item
                    {
                        Label = desc,
                        Value = () => Devices.Active?.Buttons.TryGetValue(n, out var b) == true ? Devices.Active.Label(b) : "-",
                        Open = () => StartWait(n, desc),
                        Help = "Right arrow, then press the wheel button for this. Delete = no button.",
                    });
                }
                break;
        }
        return list;
    }

    static Item Section(string label, Page p, string help) => new() { Label = label, Value = () => ">", Open = () => Go(p), Help = help };

    static string Pct(float v) => $"{v * 100f:0.#}%";

    static float Step(float v, int dir, float min, float max, float step) =>
        Mathf.Clamp(Mathf.Round(v / step + dir) * step, min, max);

    static Item Num(string label, ConfigEntry<float> e, float min, float max, float step, Func<float, string> fmt, string help) => new()
    {
        Label = label, Help = help, Value = () => fmt(e.Value),
        Change = dir => e.Value = Step(e.Value, dir, min, max, step),
    };

    static Item Bool(string label, ConfigEntry<bool> e, string help) => new()
    {
        Label = label, Help = help, Value = () => e.Value ? "On" : "Off",
        Change = _ => e.Value = !e.Value,
    };

    static Item Choice<T>(string label, ConfigEntry<T> e, string help) where T : struct, Enum => new()
    {
        Label = label, Help = help, Value = () => e.Value.ToString(),
        Change = dir =>
        {
            var all = (T[])Enum.GetValues(typeof(T));
            int i = Array.IndexOf(all, e.Value);
            e.Value = all[(i + dir + all.Length) % all.Length];
        },
    };

    // --- button assignment ------------------------------------------------------------------------

    static string _waitAction, _waitDesc;
    static float _waitStart;
    static Dictionary<Guid, bool[]> _waitHeld;

    // While true, wheel buttons do nothing in the game (the next press is being assigned).
    public static bool WaitingForButton => _open && _waitAction != null;

    static void StartWait(string action, string desc)
    {
        _waitAction = action; _waitDesc = desc;
        _waitStart = Time.unscaledTime;
        _waitHeld = new Dictionary<Guid, bool[]>();
        Buttons.ReleaseAll();
    }

    static void WaitForButton(float now)
    {
        if (Pressed(VK_BACK, now, false) || now - _waitStart > 10f) { _waitAction = null; Note("Nothing changed."); return; }
        foreach (var d in Devices.All)
        {
            int n = ButtonRef.PovBase + DInput.PovCount * 4;
            if (!_waitHeld.TryGetValue(d.Info.Instance, out var held))
            {
                // Buttons already held when waiting started (e.g. the D-pad press that chose this line) don't count.
                _waitHeld[d.Info.Instance] = held = new bool[n];
                for (int b = 0; b < n; b++) held[b] = Devices.IsDown(d, b);
                continue;
            }
            for (int b = 0; b < n; b++)
            {
                bool down = Devices.IsDown(d, b);
                if (!down) { held[b] = false; continue; }
                if (held[b]) continue;
                Assign(d, b);
                return;
            }
        }
    }

    static void Assign(DInput.Device d, int button)
    {
        var active = Devices.Active;
        int dev = -1;
        for (int i = 0; i < active.Devices.Count; i++)
            if (ReferenceEquals(Devices.Get(i), d)) dev = i;
        string action = _waitAction, desc = _waitDesc;
        _waitAction = null;
        if (dev < 0) { Note($"'{d.Info.Name}' isn't part of this wheel setup. Add it with Setup tools > full setup."); return; }

        var p = Editable(active);
        var r = new ButtonRef { Device = dev, Button = button };
        string moved = "";
        foreach (var kv in p.Buttons.Where(kv => kv.Key != action && r.Same(kv.Value)).ToList())
        {
            p.Buttons.Remove(kv.Key);
            moved = kv.Key;
        }
        p.Buttons[action] = r;
        SaveSetup(p, $"{desc}: {p.Label(r)}" + (moved.Length > 0 ? $"  (taken away from '{moved}')" : ""));
    }

    static void ClearButton(Item it)
    {
        var entry = Buttons.ActionList.FirstOrDefault(a => a.Description == it.Label);
        if (entry.Name == null || Devices.Active?.Buttons.ContainsKey(entry.Name) != true) return;
        var p = Editable(Devices.Active);
        p.Buttons.Remove(entry.Name);
        SaveSetup(p, $"{entry.Description}: no button");
    }

    // A copy of the active setup to change. A built-in setup becomes the player's own one when saved.
    static WheelProfile Editable(WheelProfile active)
    {
        var p = active.Clone();
        p.FilePath = active.Source == "wizard" ? active.FilePath : null;
        return p;
    }

    static void SaveSetup(WheelProfile p, string what)
    {
        try
        {
            string path = Profiles.Save(p);
            Devices.SetActive(p);
            Plugin.Info($"menu: {what} (saved {path})");
            Note(what);
        }
        catch (Exception e) { Note("Saving failed: " + e.Message); Plugin.Error("menu save: " + e); }
    }

    // --- drawing ----------------------------------------------------------------------------------

    static GUIStyle _title, _row, _rowSel, _value, _valueSel, _help, _foot, _noteStyle;

    public static void OnGUI()
    {
        if (!_open) return;
        if (_title == null)
        {
            _title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            _title.normal.textColor = new Color(1f, 0.85f, 0.2f);
            _row = new GUIStyle(GUI.skin.label) { fontSize = 19 };
            _row.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            _rowSel = new GUIStyle(_row) { fontStyle = FontStyle.Bold };
            _rowSel.normal.textColor = new Color(1f, 0.85f, 0.2f);
            _value = new GUIStyle(_row) { alignment = TextAnchor.UpperRight };
            _valueSel = new GUIStyle(_rowSel) { alignment = TextAnchor.UpperRight };
            _help = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, fontStyle = FontStyle.Italic };
            _help.normal.textColor = new Color(0.75f, 0.85f, 1f);
            _foot = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            _foot.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            _noteStyle = new GUIStyle(_help) { fontStyle = FontStyle.Bold };
            _noteStyle.normal.textColor = new Color(0.6f, 1f, 0.6f);
        }

        var items = Items(_page);
        const float rowH = 29f, w = 600f;
        int maxRows = Mathf.Max(6, Mathf.FloorToInt((Screen.height - 380f) / rowH));
        int first = Mathf.Clamp(_sel - maxRows / 2, 0, Mathf.Max(0, items.Count - maxRows));
        int shown = Mathf.Min(maxRows, items.Count - first);
        float h = 60f + shown * rowH + 150f;
        var r = new Rect(30f, 90f, Mathf.Min(w, Screen.width - 60f), h);
        for (int i = 0; i < 3; i++) GUI.Box(r, "");

        float x = r.x + 20f, y = r.y + 14f, iw = r.width - 40f;
        string title = _page switch
        {
            Page.Ffb => "Force feedback", Page.Steering => "Steering & pedals", Page.Clutch => "Clutch & shifter",
            Page.Buttons => "Buttons", _ => $"Wheel mod {Plugin.Version}",
        };
        GUI.Label(new Rect(x, y, iw, 34f), title, _title);
        y += 44f;
        for (int i = first; i < first + shown; i++)
        {
            bool sel = i == _sel;
            var it = items[i];
            GUI.Label(new Rect(x, y, iw * 0.62f, rowH), (sel ? "> " : "   ") + it.Label, sel ? _rowSel : _row);
            string v = it.Value?.Invoke() ?? "";
            if (sel && it.Change != null) v = $"<  {v}  >";
            GUI.Label(new Rect(x + iw * 0.5f, y, iw * 0.5f, rowH), v, sel ? _valueSel : _value);
            y += rowH;
        }
        if (first + shown < items.Count) GUI.Label(new Rect(x, y - 6f, iw, 20f), "   ...", _foot);
        y += 10f;

        if (_waitAction != null)
            GUI.Label(new Rect(x, y, iw, 60f), $"Press the wheel button for: {_waitDesc}\n(Backspace = cancel)", _noteStyle);
        else if (Time.unscaledTime < _noteUntil)
            GUI.Label(new Rect(x, y, iw, 60f), _note, _noteStyle);
        else
            GUI.Label(new Rect(x, y, iw, 60f), items[Mathf.Clamp(_sel, 0, items.Count - 1)].Help, _help);
        GUI.Label(new Rect(x, r.yMax - 52f, iw, 48f),
            $"↑↓ choose   ←→ change   → open   Backspace back   {Settings.SetupKey.Value} close\nThe wheel's D-pad works too. Changes are saved at once.", _foot);
    }
}
