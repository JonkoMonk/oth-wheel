using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace OthWheel;

// In-game wheel setup: a window in the middle of the screen, driven by doing things on the wheel
// (it detects axes, pedals and buttons by itself) plus number keys and F9 (skip) / F10 (back).
// The game uses neither digits nor F-keys, so nothing happens in the game while it is open; the mod
// also stops driving the car and pressing game keys while it is open.
//
// Opens with the setup key (cfg General.SetupKey, default F3), and by itself the first time an unknown wheel is connected and the player sits in a car.
// With a known wheel (built-in preset or saved setup), the setup key opens a small menu instead: remap buttons,
// full setup, force feedback test, rotation, share, support report.
internal static class Wizard
{
    enum Page { Closed, Menu, Devices, Steering, Rotation, Throttle, Brake, Clutch, FfbIntro, FfbDirection, FfbStrength, Buttons, Summary, Done, ShifterGear }
    // What happens after the last step of a partial run.
    enum Mode { Full, ButtonsOnly, FfbOnly }

    static Page _page = Page.Closed;
    static Mode _mode;
    public static bool IsOpen => _page != Page.Closed;
    public static bool OwnsForceFeedback { get; private set; }

    static WheelProfile _draft;
    static List<int> _draftConn = new(); // draft device index -> index in Devices.Connected
    static string _title = "", _body = "", _footer = "", _note = "";
    static float _pageStart;
    static bool _autoOpened;
    static float _inCarSince = -1f;

    static readonly int[] RotationChoices = { 270, 360, 540, 720, 900, 1080, 1440 };

    // --- keys -------------------------------------------------------------------------------------

    const ushort VK_F9 = 0x78, VK_F10 = 0x79;
    static readonly bool[] _keyWas = new bool[256];
    static ushort[] _openKey = Array.Empty<ushort>();
    static string _openKeyName = "";

    static bool Pressed(ushort vk)
    {
        bool down = Win32.IsKeyDown(vk);
        bool edge = down && !_keyWas[vk];
        _keyWas[vk] = down;
        return edge;
    }

    // 1-9 on the main row or the numpad; 0 for "0". -1 = none.
    static int DigitPressed()
    {
        int hit = -1;
        for (int i = 0; i <= 9; i++)
            if (Pressed((ushort)(0x30 + i)) | Pressed((ushort)(0x60 + i))) hit = i;
        return hit;
    }

    // --- main loop --------------------------------------------------------------------------------

    public static void Update(float now)
    {
        try { Tick(now); }
        catch (Exception e) { Plugin.ErrorOnce("wizard", e); Close(); }
    }

    static void Tick(float now)
    {
        if (_openKeyName != Settings.SetupKey.Value)
        {
            _openKeyName = Settings.SetupKey.Value;
            _openKey = Win32.ParseKeys(_openKeyName, "SetupKey");
        }
        bool focus = Win32.GameHasFocus();
        bool openKey = focus && _openKey.Length > 0 && Pressed(_openKey[0]);
        Buttons.Suspended = IsOpen || Menu.WaitingForButton;

        if (!IsOpen)
        {
            // With a working setup the key opens the settings menu (its "Setup tools" lead back here).
            if (openKey)
            {
                if (Menu.IsOpen) Menu.Close();
                else if (Devices.Active != null) Menu.Open();
                else Open(now);
            }
            else AutoOpen(now);
            return;
        }
        if (openKey) { Close(); return; }

        int digit = focus ? DigitPressed() : -1;
        bool skip = focus && Pressed(VK_F9);
        bool back = focus && Pressed(VK_F10);

        switch (_page)
        {
            case Page.Menu: MenuPage(digit); break;
            case Page.Devices: DevicesPage(digit); break;
            case Page.Steering: SteeringPage(now, skip, back); break;
            case Page.Rotation: RotationPage(digit, back); break;
            case Page.Throttle: PedalPage(now, "THROTTLE", p => _draft.Throttle = p, skip, back, Page.Rotation, Page.Brake, false); break;
            case Page.Brake: PedalPage(now, "BRAKE", p => _draft.Brake = p, skip, back, Page.Throttle, Page.Clutch, false); break;
            case Page.Clutch: PedalPage(now, "CLUTCH", p => _draft.Clutch = p, skip, back, Page.Brake, Page.FfbIntro, true); break;
            case Page.FfbIntro: FfbIntroPage(now, digit, skip, back); break;
            case Page.FfbDirection: FfbDirectionPage(now, digit, skip); break;
            case Page.FfbStrength: FfbStrengthPage(now, digit, skip, back); break;
            case Page.Buttons: ButtonsPage(now, digit, skip, back); break;
            case Page.Summary: SummaryPage(digit, back); break;
            case Page.Done: DonePage(digit); break;
            case Page.ShifterGear: ShifterGearPage(now, skip, back); break;
        }
    }

    // Opens by itself once per session: an unknown wheel is connected and the player has been in a car for 3 s.
    static void AutoOpen(float now)
    {
        if (_autoOpened || Devices.Active != null) return;
        if (InputHooks.LocalVehicleInput == null) { _inCarSince = -1f; return; }
        if (_inCarSince < 0f) _inCarSince = now;
        if (now - _inCarSince < 3f || !Devices.All.Any(Devices.LooksLikeWheel)) return;
        _autoOpened = true;
        Plugin.Info("unknown wheel connected, opening the setup wizard");
        Open(now);
        _note = "New wheel found! Let's set it up (takes about 2 minutes).";
    }

    static void Open(float now)
    {
        _autoOpened = true;
        _note = "";
        Buttons.ReleaseAll();
        if (Devices.Active != null) Go(Page.Menu, now);
        else StartFull(now);
    }

    // From the settings menu's "Setup tools".
    public static void OpenTools(float now) => Open(now);

    public static void Close()
    {
        StopForces();
        _page = Page.Closed;
        Buttons.Suspended = false;
        Plugin.Info("setup wizard closed");
    }

    static void Go(Page p, float now)
    {
        if (p != Page.FfbDirection && p != Page.FfbStrength) StopForces();
        _page = p;
        _pageStart = now;
        _step = 0;
        _stepTime = 0f;
        Snapshot();
    }

    // --- draft handling ---------------------------------------------------------------------------

    static void StartFull(float now)
    {
        _mode = Mode.Full;
        _draft = new WheelProfile();
        _draftConn = new List<int>();
        Go(Page.Devices, now);
    }

    static bool StartFromActive(Mode mode, float now)
    {
        var a = Devices.Active;
        if (a == null) return false;
        _mode = mode;
        _draft = a.Clone();
        _draft.FilePath = a.Source == "wizard" ? a.FilePath : null;
        var map = Profiles.Match(_draft, Devices.Connected);
        if (map == null) return false;
        _draftConn = map.ToList();
        return true;
    }

    // Draft device index for a connected device, adding it to the draft if new.
    static int DraftDevice(int conn)
    {
        int i = _draftConn.IndexOf(conn);
        if (i >= 0) return i;
        var info = Devices.Connected[conn];
        _draft.Devices.Add(new DeviceRef { Name = info.Name, UsbId = info.UsbId });
        _draftConn.Add(conn);
        if (string.IsNullOrEmpty(_draft.Name)) _draft.Name = info.Name;
        return _draftConn.Count - 1;
    }

    static DInput.Device DraftDev(int draftIndex) =>
        draftIndex >= 0 && draftIndex < _draftConn.Count ? Devices.GetConnected(_draftConn[draftIndex]) : null;

    static float DraftSteerPosition()
    {
        var s = _draft.Steering;
        var d = s != null ? DraftDev(s.Device) : null;
        int axis = s != null ? Profiles.AxisIndex(s.Axis) : -1;
        return d != null && axis >= 0 ? Wheel.SteerPosition(s, d.Axis[axis]) : 0f;
    }

    // --- snapshots for detection ------------------------------------------------------------------

    static int[][] _axisStart, _axisMin, _axisMax;
    static float[][] _tMin, _tMax;
    static bool[][] _btnStart;
    static int _step;
    static float _stepTime;

    static void Snapshot()
    {
        int n = Devices.Connected.Count;
        _axisStart = new int[n][]; _axisMin = new int[n][]; _axisMax = new int[n][];
        _tMin = new float[n][]; _tMax = new float[n][]; _btnStart = new bool[n][];
        for (int k = 0; k < n; k++)
        {
            var d = Devices.GetConnected(k);
            _axisStart[k] = d != null ? (int[])d.Axis.Clone() : new int[DInput.AxisCount];
            _axisMin[k] = (int[])_axisStart[k].Clone();
            _axisMax[k] = (int[])_axisStart[k].Clone();
            _tMin[k] = new float[DInput.AxisCount];
            _tMax[k] = new float[DInput.AxisCount];
            _btnStart[k] = new bool[ButtonRef.PovBase + DInput.PovCount * 4];
            if (d != null)
                for (int b = 0; b < _btnStart[k].Length; b++) _btnStart[k][b] = Devices.IsDown(d, b);
        }
    }

    static bool SnapshotValid => _axisStart != null && _axisStart.Length == Devices.Connected.Count;

    static void TrackAxes(float now)
    {
        if (!SnapshotValid) { Snapshot(); return; }
        for (int k = 0; k < _axisStart.Length; k++)
        {
            var d = Devices.GetConnected(k);
            if (d == null) continue;
            for (int a = 0; a < DInput.AxisCount; a++)
            {
                int v = d.Axis[a];
                if (v < _axisMin[k][a]) { _axisMin[k][a] = v; _tMin[k][a] = now; }
                if (v > _axisMax[k][a]) { _axisMax[k][a] = v; _tMax[k][a] = now; }
            }
        }
    }

    // The axis that moved furthest from where it was when the page opened (excluding used ones).
    static (int conn, int axis, int extreme, float travel) BiggestMove(Func<int, int, bool> exclude)
    {
        (int, int, int, float) best = (-1, -1, 0, 0f);
        for (int k = 0; k < _axisStart.Length; k++)
            for (int a = 0; a < DInput.AxisCount; a++)
            {
                if (exclude(k, a)) continue;
                int s = _axisStart[k][a];
                int up = _axisMax[k][a] - s, down = s - _axisMin[k][a];
                float travel = Math.Max(up, down) / (float)DInput.RawMax;
                if (travel > best.Item4) best = (k, a, up >= down ? _axisMax[k][a] : _axisMin[k][a], travel);
            }
        return best;
    }

    // --- pages ------------------------------------------------------------------------------------

    static void MenuPage(int digit)
    {
        var a = Devices.Active;
        if (a == null) { StartFull(Time.unscaledTime); return; }
        _title = "Wheel setup";
        var sb = new StringBuilder();
        sb.AppendLine($"Active setup: {a.Name}  ({(a.Source == "builtin" ? "built into the mod" : a.Source == "preset" ? "preset file" : "your own setup")})");
        sb.AppendLine($"Rotation: {a.RotationDegrees}°   Force feedback: {(a.Ffb?.Direction != 0 ? $"tested, strength {a.Ffb.Strength:0.00}" : "not tested yet")}");
        var sh = Shifter.Active;
        sb.AppendLine(sh != null ? $"Shifter: {sh.Name} ({sh.Describe()}){(Shifter.Enabled.Value ? "" : ", switched off in the settings")}" : "Shifter: none");
        sb.AppendLine();
        sb.AppendLine("1   Change button assignments");
        sb.AppendLine("2   Full setup from scratch (steering, pedals, force feedback, buttons)");
        sb.AppendLine("3   Force feedback test (direction and strength)");
        sb.AppendLine("4   Steering rotation set in the wheel's driver software");
        sb.AppendLine("5   Share this setup (saves a file to send to the mod's author, so your wheel can be added)");
        sb.AppendLine("6   Save a support report on the desktop");
        sb.AppendLine(sh != null ? "7   Set up the H-pattern shifter again" : "7   Set up an H-pattern shifter");
        if (sh?.FilePath != null) sb.AppendLine("8   Remove the shifter setup");
        _body = sb.ToString();
        _footer = $"{_openKeyName} = close";

        float now = Time.unscaledTime;
        switch (digit)
        {
            case 1:
                if (StartFromActive(Mode.ButtonsOnly, now)) { _buttonIndex = 0; Go(Page.Buttons, now); }
                break;
            case 2: StartFull(now); break;
            case 3:
                if (StartFromActive(Mode.FfbOnly, now)) Go(Page.FfbIntro, now);
                break;
            case 4:
                if (StartFromActive(Mode.FfbOnly, now)) { _rotationOnly = true; Go(Page.Rotation, now); }
                break;
            case 5:
                _note = Support.Share(a);
                break;
            case 6:
                _note = Support.Report();
                break;
            case 7:
                StartShifter(now);
                break;
            case 8:
                if (sh?.FilePath != null) _note = Shifter.RemoveActive();
                break;
        }
    }

    // --- H-pattern shifter (own setup, separate from the wheel setup) ----------------------------

    static ShifterProfile _shifterDraft;
    static int _shifterConn = -1, _gearIndex;

    static void StartShifter(float now)
    {
        _shifterDraft = new ShifterProfile();
        _shifterConn = -1;
        _gearIndex = 0;
        _note = "";
        Go(Page.ShifterGear, now);
    }

    static string SlotName(string s) => s == "R" ? "REVERSE" : s switch { "1" => "1st", "2" => "2nd", "3" => "3rd", _ => s + "th" } + " gear";

    static void ShifterGearPage(float now, bool skip, bool back)
    {
        var slots = ShifterProfile.Slots;
        string slot = slots[_gearIndex];
        _title = "Shifter setup";
        var sb = new StringBuilder();
        sb.AppendLine($"Put the shifter lever in {SlotName(slot)} and leave it there.");
        sb.AppendLine();
        if (_shifterConn >= 0) sb.AppendLine($"Shifter: {Devices.Connected[_shifterConn].Name}");
        foreach (var s in slots.Where(s => _shifterDraft.Gears.ContainsKey(s)))
            sb.AppendLine($"  {SlotName(s)}: {ButtonRef.Describe(_shifterDraft.Gears[s])}");
        sb.AppendLine();
        sb.AppendLine("Every slot of the lever becomes that gear in the game. In a car with fewer gears, the higher slots");
        sb.AppendLine("give its top gear. Lever in the middle (no gear) = neutral: the mod presses the clutch for you.");
        _body = sb.ToString();
        _footer = slot == "1" ? $"F10 = back to the menu     {_openKeyName} = cancel"
                : slot == "R" ? "F9 = no reverse (finish)     F10 = back"
                : "F9 = no more gears (go to reverse)     F10 = back";

        if (back)
        {
            if (_gearIndex == 0) { Go(Page.Menu, now); return; }
            _gearIndex--;
            _shifterDraft.Gears.Remove(slots[_gearIndex]);
            if (_shifterDraft.Gears.Count == 0) _shifterConn = -1;
            Go(Page.ShifterGear, now);
            return;
        }
        if (skip && slot != "1")
        {
            if (slot == "R") { FinishShifter(now); return; }
            _gearIndex = slots.Length - 1;
            Go(Page.ShifterGear, now);
            return;
        }

        if (!SnapshotValid) { Snapshot(); return; }
        // Like the buttons page: only a button that wasn't already held when the page opened.
        for (int k = 0; k < _btnStart.Length; k++)
        {
            if (_shifterConn >= 0 && k != _shifterConn) continue;   // all gears on the same device
            var d = Devices.GetConnected(k);
            if (d == null) continue;
            for (int b = 0; b < _btnStart[k].Length; b++)
            {
                bool down = Devices.IsDown(d, b);
                if (!down) { _btnStart[k][b] = false; continue; }
                if (_btnStart[k][b]) continue;
                var taken = _shifterDraft.Gears.FirstOrDefault(kv => kv.Value == b).Key;
                if (taken != null) { _note = $"That's {SlotName(taken)} already. Put the lever in {SlotName(slot)}."; _btnStart[k][b] = true; continue; }
                if (_shifterConn < 0)
                {
                    _shifterConn = k;
                    _shifterDraft.Device = new DeviceRef { Name = d.Info.Name, UsbId = d.Info.UsbId };
                    _shifterDraft.Name = d.Info.Name;
                }
                _shifterDraft.Gears[slot] = b;
                _note = $"{SlotName(slot)}: {ButtonRef.Describe(b)}";
                Plugin.Info($"wizard: shifter {slot} = {d.Info.Name} {ButtonRef.Describe(b)}");
                if (slot == "R") { FinishShifter(now); return; }
                _gearIndex++;
                Go(Page.ShifterGear, now);
                return;
            }
        }
    }

    static void FinishShifter(float now)
    {
        if (_shifterDraft.Gears.Count == 0) { _note = "No gears were set, nothing saved."; Go(Page.Menu, now); return; }
        try
        {
            string path = Shifter.Save(_shifterDraft);
            Plugin.Info($"wizard: saved shifter '{_shifterDraft.Name}' ({_shifterDraft.Describe()}) to {path}");
            _note = $"Shifter saved ({_shifterDraft.Describe()}). Close this menu, put the lever in a gear and drive!";
        }
        catch (Exception e) { _note = "Saving failed: " + e.Message; Plugin.Error("wizard shifter save: " + e); }
        Go(Page.Menu, now);
    }

    static bool _rotationOnly;

    static void DevicesPage(int digit)
    {
        _title = "Wheel setup - step 1 of 6: devices";
        var sb = new StringBuilder();
        if (Devices.Connected.Count == 0)
            sb.AppendLine("No wheel or pedals found. Plug them in and wait a few seconds.\n(Also check that the wheel's driver software is installed.)");
        else
        {
            sb.AppendLine("Found:");
            foreach (var d in Devices.All)
                sb.AppendLine($"  -  {d.Info.Name}   (USB {d.Info.UsbId}{(d.ForceFeedback ? ", force feedback" : "")})");
            sb.AppendLine();
            sb.AppendLine("Before you start: set the rotation and force feedback in your wheel's driver software");
            sb.AppendLine("(Moza Pit House, Logitech G HUB, Thrustmaster / Fanatec control panel). 540° feels best here.");
            sb.AppendLine("Pedals that are set to 'combined' mode in the driver should be switched to separate.");
        }
        _body = sb.ToString();
        _footer = Devices.Connected.Count > 0 ? $"1 = start     {_openKeyName} = cancel" : $"{_openKeyName} = cancel";
        if (digit == 1 && Devices.Connected.Count > 0) Go(Page.Steering, Time.unscaledTime);
    }

    static void SteeringPage(float now, bool skip, bool back)
    {
        _title = "Step 2 of 6: steering";
        if (back) { Go(Page.Devices, now); return; }
        TrackAxes(now);
        var best = BiggestMove((k, a) => false);
        var sb = new StringBuilder();
        sb.AppendLine("Turn the wheel all the way to the LEFT,");
        sb.AppendLine("then all the way to the RIGHT,");
        sb.AppendLine("then back to the middle, and let go.");
        sb.AppendLine();
        if (best.conn >= 0 && best.travel > 0.5f)
        {
            int k = best.conn, a = best.axis;
            var d = Devices.GetConnected(k);
            int min = _axisMin[k][a], max = _axisMax[k][a], v = d.Axis[a];
            sb.AppendLine($"Steering found: {d.Info.Name}, axis {DInput.AxisNames[a]}");
            int mid = (min + max) / 2;
            bool centered = Math.Abs(v - mid) < (max - min) * 0.15f;
            bool bothEnds = max - min > DInput.RawMax * 0.5f && _tMin[k][a] > 0f && _tMax[k][a] > 0f;
            if (bothEnds && centered)
            {
                if (_stepTime == 0f) _stepTime = now;
                if (now - _stepTime > 0.7f)
                {
                    // The end reached first was the left one. Center = halfway between the end stops
                    // (a wheel that is let go rarely stops exactly in the middle).
                    bool leftIsMin = _tMin[k][a] <= _tMax[k][a];
                    _draft.Steering = new AxisRef
                    {
                        Device = DraftDevice(k), Axis = DInput.AxisNames[a],
                        Left = leftIsMin ? min : max, Right = leftIsMin ? max : min, Center = mid,
                    };
                    _draft.Ffb ??= new FfbRef();
                    _draft.Ffb.Device = _draft.Steering.Device;
                    Plugin.Info($"wizard: steering = {d.Info.Name} {DInput.AxisNames[a]}, left {_draft.Steering.Left} center {mid} (resting at {v}) right {_draft.Steering.Right}");
                    Go(Page.Rotation, now);
                    return;
                }
            }
            else _stepTime = 0f;
            sb.AppendLine(bothEnds ? "Now back to the middle and let go..." : "Keep going: all the way to both ends.");
        }
        _body = sb.ToString();
        _footer = "F10 = back     F9 = start this step again";
        if (skip) Go(Page.Steering, now);
    }

    static void RotationPage(int digit, bool back)
    {
        float now = Time.unscaledTime;
        _title = _rotationOnly ? "Steering rotation" : "Step 3 of 6: rotation";
        var sb = new StringBuilder();
        sb.AppendLine("Which rotation (lock to lock) is set in your wheel's driver software?");
        sb.AppendLine();
        for (int i = 0; i < RotationChoices.Length; i++)
            sb.AppendLine($"{i + 1}   {RotationChoices[i]}°{(RotationChoices[i] == _draft.RotationDegrees ? "   (current)" : "")}");
        sb.AppendLine();
        sb.AppendLine($"Tip: {Settings.LockDegrees.Value:0}° feels best in this game. With more, the mod still gives full steering at");
        sb.AppendLine($"{Settings.LockDegrees.Value:0}° and you feel the end stop there.");
        _body = sb.ToString();
        _footer = _rotationOnly ? $"{_openKeyName} = cancel" : "F10 = back";
        if (back && !_rotationOnly) { Go(Page.Steering, now); return; }
        if (digit >= 1 && digit <= RotationChoices.Length)
        {
            _draft.RotationDegrees = RotationChoices[digit - 1];
            if (_rotationOnly) { _rotationOnly = false; Go(Page.Summary, now); }
            else Go(Page.Throttle, now);
        }
    }

    static void PedalPage(float now, string pedal, Action<PedalRef> set, bool skip, bool back, Page prev, Page next, bool optional)
    {
        _title = $"Step 4 of 6: {pedal.ToLowerInvariant()} pedal";
        if (back) { Go(prev, now); return; }
        if (skip)
        {
            if (optional) { set(null); Go(next, now); }
            else Go(_page, now); // restart the step
            return;
        }
        TrackAxes(now);
        // Axes already in use don't count.
        var used = new List<(int, int)>();
        void Use(int dev, string axis) { if (dev >= 0 && dev < _draftConn.Count) used.Add((_draftConn[dev], Profiles.AxisIndex(axis))); }
        if (_draft.Steering != null) Use(_draft.Steering.Device, _draft.Steering.Axis);
        if (_page != Page.Throttle && _draft.Throttle != null) Use(_draft.Throttle.Device, _draft.Throttle.Axis);
        if (_page == Page.Clutch && _draft.Brake != null) Use(_draft.Brake.Device, _draft.Brake.Axis);
        var best = BiggestMove((k, a) => used.Contains((k, a)));

        var sb = new StringBuilder();
        sb.AppendLine($"Press the {pedal} all the way down, then let it go.");
        sb.AppendLine();
        if (best.conn >= 0 && best.travel > 0.4f)
        {
            int k = best.conn, a = best.axis;
            var d = Devices.GetConnected(k);
            int rest = _axisStart[k][a], v = d.Axis[a];
            sb.AppendLine($"Found: {d.Info.Name}, axis {DInput.AxisNames[a]}");
            bool released = Math.Abs(v - rest) < DInput.RawMax * 0.1f;
            if (released)
            {
                if (_stepTime == 0f) _stepTime = now;
                if (now - _stepTime > 0.4f)
                {
                    var p = new PedalRef { Device = DraftDevice(k), Axis = DInput.AxisNames[a], Released = rest, Pressed = best.extreme };
                    set(p);
                    Plugin.Info($"wizard: {pedal} = {d.Info.Name} {p.Axis}, released {rest} pressed {p.Pressed}");
                    // A pedal resting in the middle of its axis: "combined pedals" mode (throttle and brake share one axis).
                    if (rest > DInput.RawMax * 0.3f && rest < DInput.RawMax * 0.7f)
                        _note = "This pedal rests in the middle of its axis: your pedals are probably in 'combined' mode. " +
                                "Switch to separate pedals in the wheel's driver software, then run the setup again.";
                    Go(next, now);
                    return;
                }
            }
            else { _stepTime = 0f; sb.AppendLine("Now let it go..."); }
        }
        _body = sb.ToString();
        _footer = optional ? "F9 = I have no clutch pedal     F10 = back" : "F10 = back     F9 = start this step again";
    }

    // --- force feedback ---------------------------------------------------------------------------

    static DInput.Device FfbDev => _draft?.Ffb != null ? DraftDev(_draft.Ffb.Device) : null;

    static void FfbIntroPage(float now, int digit, bool skip, bool back)
    {
        _title = _mode == Mode.FfbOnly ? "Force feedback test" : "Step 5 of 6: force feedback";
        if (back && _mode == Mode.Full) { Go(Page.Clutch, now); return; }
        var dev = FfbDev;
        if (dev == null || !dev.ForceFeedback)
        {
            _body = "This wheel has no force feedback (or it isn't available through DirectInput).\nThat's fine: steering, pedals and buttons work without it.";
            _footer = "1 = continue";
            if (digit == 1 || skip) { if (_draft.Ffb != null) _draft.Ffb.Direction = 0; AfterFfb(now); }
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine("The mod gives the wheel a gentle push to find out which way your wheel's force goes.");
        sb.AppendLine();
        sb.AppendLine("Let go of the wheel completely, then press 1.");
        if (_ffbProblem.Length > 0) { sb.AppendLine(); sb.AppendLine(_ffbProblem); }
        _body = sb.ToString();
        _footer = $"1 = start test     F9 = skip (force feedback stays off until tested){(_mode == Mode.Full ? "     F10 = back" : "")}";
        if (skip) { AfterFfb(now); return; }
        if (digit != 1) return;

        // The game's DirectInput library holds the wheel exclusively; it can only be released once the
        // game has fully set it up, i.e. once you're in a car.
        if (!dev.ForceFeedbackRunning && !DirectInputLate.TryReleaseNow())
        {
            _ffbProblem = "Get into your car first (in the menus the game still holds the wheel), then press 1 again.";
            return;
        }
        if (!dev.ForceFeedbackRunning)
        {
            Devices.StopForceFeedback();
            if (!dev.StartForceFeedback())
            {
                _ffbProblem = $"Couldn't take over the wheel's force feedback ({dev.Status}). Click into the game window and press 1 again.";
                return;
            }
        }
        _ffbProblem = "";
        OwnsForceFeedback = true;
        _startPos = DraftSteerPosition();
        Go(Page.FfbDirection, now);
    }

    static string _ffbProblem = "";
    static float _startPos;
    static int _foundDirection;

    static void FfbDirectionPage(float now, int digit, bool skip)
    {
        _title = _mode == Mode.FfbOnly ? "Force feedback test" : "Step 5 of 6: force feedback";
        var dev = FfbDev;
        if (dev == null || skip) { StopForces(); AfterFfb(now); return; }
        float t = now - _pageStart;
        float pos = DraftSteerPosition();
        switch (_step)
        {
            case 0: // push: ramp up until the wheel moves
            {
                _body = "Hands off the wheel...\n\nTesting direction.";
                _footer = "F9 = skip";
                float magnitude = Mathf.Clamp(t / 1.6f, 0f, 1f) * 5000f;
                dev.SetForces(Mathf.RoundToInt(magnitude), 0);
                float moved = pos - _startPos;
                if (Mathf.Abs(moved) > 0.06f)
                {
                    // A positive DirectInput force moved the wheel this way.
                    _foundDirection = moved > 0 ? 1 : -1;
                    _draft.Ffb.Direction = _foundDirection;
                    Plugin.Info($"wizard: force feedback direction {_foundDirection} (DI +{magnitude:0} moved the wheel {(moved > 0 ? "right" : "left")} by {moved:0.00})");
                    _step = 1; _stepTime = now;
                }
                else if (t > 2.2f)
                {
                    dev.SetForces(0, 0);
                    _step = 3;
                }
                break;
            }
            case 1: // bring the wheel back to the middle
            {
                _body = "Direction found!\n\nCentering the wheel...";
                _footer = "";
                float force = Mathf.Clamp(-pos * 0.6f, -0.3f, 0.3f);
                dev.SetForces(Mathf.RoundToInt(force * _foundDirection * 10000f), 2500);
                if (now - _stepTime > 1.5f) { dev.SetForces(0, 0); _step = 2; _stepTime = now; }
                break;
            }
            case 2:
                if (now - _stepTime > 0.3f) Go(Page.FfbStrength, now);
                break;
            case 3: // nothing moved
                _body = "The wheel didn't move.\n\nIs force feedback switched on (and not at 0%) in the wheel's driver software?\nIs the wheel free to turn?";
                _footer = "1 = try again     F9 = skip";
                if (digit == 1) { _startPos = pos; _pageStart = now; _step = 0; }
                break;
        }
    }

    static void FfbStrengthPage(float now, int digit, bool skip, bool back)
    {
        _title = _mode == Mode.FfbOnly ? "Force feedback strength" : "Step 5 of 6: force feedback strength";
        var dev = FfbDev;
        if (dev == null) { AfterFfb(now); return; }
        if (skip) { StopForces(); AfterFfb(now); return; }
        float t = now - _pageStart;
        if (_step == 1)
        {
            // Test: like a corner at medium grip, left then right, at the Realistic profile's strength.
            float s = 0.5f * Mathf.Clamp(_draft.Ffb.Strength, 0.1f, 5f);
            float f = t < 0.6f ? 0.45f : t < 1.2f ? -0.45f : 0f;
            dev.SetForces(Mathf.RoundToInt(Mathf.Clamp(f * s, -1f, 1f) * _draft.Ffb.Direction * 10000f), 1000);
            _body = "Feel it...";
            _footer = "";
            if (t > 1.3f) { dev.SetForces(0, 0); _step = 2; }
            return;
        }
        var sb = new StringBuilder();
        if (_step == 0)
        {
            sb.AppendLine("Now hold the wheel lightly, like when driving, and press 1.");
            sb.AppendLine("It will pull left, then right, like in a corner.");
            _footer = "1 = play     F9 = skip";
            if (digit == 1) { _pageStart = now; _step = 1; }
        }
        else
        {
            sb.AppendLine($"How did that feel?   (strength now: {_draft.Ffb.Strength:0.00})");
            sb.AppendLine();
            sb.AppendLine("1   too weak");
            sb.AppendLine("2   good");
            sb.AppendLine("3   too strong");
            sb.AppendLine("4   play again");
            _footer = "F9 = skip";
            if (digit == 1) { _draft.Ffb.Strength = Mathf.Min(_draft.Ffb.Strength * 1.3f, 5f); _pageStart = now; _step = 1; }
            else if (digit == 3) { _draft.Ffb.Strength = Mathf.Max(_draft.Ffb.Strength / 1.3f, 0.15f); _pageStart = now; _step = 1; }
            else if (digit == 4) { _pageStart = now; _step = 1; }
            else if (digit == 2) { StopForces(); AfterFfb(now); }
        }
        _body = sb.ToString();
    }

    static void AfterFfb(float now)
    {
        StopForces();
        if (_mode == Mode.FfbOnly) Go(Page.Summary, now);
        else { _buttonIndex = 0; Go(Page.Buttons, now); }
    }

    static void StopForces()
    {
        if (!OwnsForceFeedback) return;
        OwnsForceFeedback = false;
        FfbDev?.SetForces(0, 0);
        Devices.FfbDevice?.SetForces(0, 0);
    }

    // --- buttons ----------------------------------------------------------------------------------

    static int _buttonIndex;

    static void ButtonsPage(float now, int digit, bool skip, bool back)
    {
        var actions = Buttons.ActionList.ToList();
        if (_buttonIndex >= actions.Count) { Go(Page.Summary, now); return; }
        var (name, desc) = actions[_buttonIndex];
        _title = _mode == Mode.ButtonsOnly ? $"Buttons ({_buttonIndex + 1} of {actions.Count})" : $"Step 6 of 6: buttons ({_buttonIndex + 1} of {actions.Count})";

        _draft.Buttons.TryGetValue(name, out var current);
        var sb = new StringBuilder();
        sb.AppendLine($"Press the wheel button for:");
        sb.AppendLine();
        sb.AppendLine($"     {desc}");
        sb.AppendLine();
        sb.AppendLine($"Now: {(current != null ? _draft.Label(current) : "nothing")}");
        _body = sb.ToString();
        _footer = $"F9 = keep it ({(current != null ? "next" : "leave empty")})     0 = no button     F10 = back";

        if (back)
        {
            if (_buttonIndex > 0) { _buttonIndex--; Go(Page.Buttons, now); }
            else if (_mode == Mode.Full) Go(Page.FfbIntro, now);
            return;
        }
        if (skip) { _buttonIndex++; Go(Page.Buttons, now); return; }
        if (digit == 0) { _draft.Buttons.Remove(name); _buttonIndex++; Go(Page.Buttons, now); return; }

        if (!SnapshotValid) { Snapshot(); return; }
        // Wait until everything held at the start is released (the previous press).
        for (int k = 0; k < _btnStart.Length; k++)
        {
            var d = Devices.GetConnected(k);
            if (d == null) continue;
            for (int b = 0; b < _btnStart[k].Length; b++)
            {
                bool down = Devices.IsDown(d, b);
                if (!down) { _btnStart[k][b] = false; continue; }
                if (_btnStart[k][b]) continue;
                var r = new ButtonRef { Device = DraftDevice(k), Button = b };
                string moved = "";
                foreach (var kv in _draft.Buttons.Where(kv => kv.Key != name && r.Same(kv.Value)).ToList())
                {
                    _draft.Buttons.Remove(kv.Key);
                    moved = kv.Key;
                }
                _draft.Buttons[name] = r;
                _note = $"{desc}: {_draft.Label(r)}" + (moved.Length > 0 ? $"  (taken away from '{moved}')" : "");
                Plugin.Info($"wizard: {name} = {d.Info.Name} {ButtonRef.Describe(b)}");
                _buttonIndex++;
                Go(Page.Buttons, now);
                return;
            }
        }
    }

    // --- summary and save -------------------------------------------------------------------------

    static void SummaryPage(int digit, bool back)
    {
        float now = Time.unscaledTime;
        _title = "Check and save";
        var sb = new StringBuilder();
        string Dev(int i) => i >= 0 && i < _draft.Devices.Count ? _draft.Devices[i].Name : "?";
        sb.AppendLine($"Setup name: {_draft.Name}");
        if (_draft.Steering != null) sb.AppendLine($"Steering: {Dev(_draft.Steering.Device)}, axis {_draft.Steering.Axis}, {_draft.RotationDegrees}°");
        else sb.AppendLine("Steering: NOT SET");
        sb.AppendLine($"Throttle: {(_draft.Throttle != null ? $"{Dev(_draft.Throttle.Device)}, axis {_draft.Throttle.Axis}" : "NOT SET")}");
        sb.AppendLine($"Brake: {(_draft.Brake != null ? $"{Dev(_draft.Brake.Device)}, axis {_draft.Brake.Axis}" : "NOT SET")}");
        sb.AppendLine($"Clutch: {(_draft.Clutch != null ? $"{Dev(_draft.Clutch.Device)}, axis {_draft.Clutch.Axis}" : "none")}");
        sb.AppendLine($"Force feedback: {(_draft.Ffb?.Direction != 0 ? $"tested, strength {_draft.Ffb.Strength:0.00}" : "off (not tested)")}");
        sb.AppendLine($"Buttons: {_draft.Buttons.Count} assigned");
        _body = sb.ToString();
        _footer = $"1 = save     2 = save and share     F10 = back     {_openKeyName} = cancel (nothing saved)";

        if (back)
        {
            if (_mode == Mode.FfbOnly) Go(Page.FfbIntro, now);
            else { _buttonIndex = Math.Max(0, Buttons.ActionList.Count() - 1); Go(Page.Buttons, now); }
            return;
        }
        if (digit != 1 && digit != 2) return;
        if (_draft.Steering == null) { _note = "Steering isn't set up yet: choose 'Full setup' to do that."; return; }
        try
        {
            string path = Profiles.Save(_draft);
            Devices.SetActive(_draft);
            Plugin.Info($"wizard: saved setup '{_draft.Name}' to {path}");
            _note = "Saved! The new setup is active now.";
            if (digit == 2) _note += "\n" + Support.Share(_draft);
        }
        catch (Exception e) { _note = "Saving failed: " + e.Message; Plugin.Error("wizard save: " + e); return; }
        Go(Page.Done, now);
    }

    static void DonePage(int digit)
    {
        _title = "Done";
        _body = "Your setup is saved and active.\n\n" +
                $"Press {_openKeyName} any time to change buttons, test force feedback, or share this setup.\n" +
                $"Have an H-pattern shifter? {_openKeyName} > 7 sets it up.";
        _footer = $"{_openKeyName} = close     5 = share this setup";
        if (digit == 5) _note = Support.Share(Devices.Active);
    }

    // --- drawing ----------------------------------------------------------------------------------

    static GUIStyle _titleStyle, _bodyStyle, _footStyle, _noteStyle;

    public static void OnGUI()
    {
        if (!IsOpen) return;
        if (_titleStyle == null)
        {
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, wordWrap = true };
            _titleStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
            _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true };
            _bodyStyle.normal.textColor = Color.white;
            _noteStyle = new GUIStyle(_bodyStyle) { fontStyle = FontStyle.Italic };
            _noteStyle.normal.textColor = new Color(0.6f, 1f, 0.6f);
            _footStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
            _footStyle.normal.textColor = new Color(0.75f, 0.85f, 1f);
        }
        float w = Mathf.Min(1100f, Screen.width - 80f), h = Mathf.Min(680f, Screen.height - 80f);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        // The default box is see-through; three layers make it dark enough to read over the game.
        for (int i = 0; i < 3; i++) GUI.Box(r, "");
        float x = r.x + 30f, y = r.y + 24f, iw = w - 60f;
        GUI.Label(new Rect(x, y, iw, 44f), _title, _titleStyle);
        y += 56f;
        GUI.Label(new Rect(x, y, iw, h - 200f), _body, _bodyStyle);
        if (_note.Length > 0) GUI.Label(new Rect(x, r.yMax - 130f, iw, 70f), _note, _noteStyle);
        GUI.Label(new Rect(x, r.yMax - 52f, iw, 40f), _footer, _footStyle);
    }
}
