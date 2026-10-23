using System;
using System.Collections.Generic;
using System.Linq;

namespace OthWheel;

// All connected game controllers, read every frame through our own DirectInput client, and the
// active wheel setup (profile) mapped onto them.
//
// Input is read non-exclusively, which works even while the game's own DirectInput library holds
// the wheel. Only the force feedback device is switched to exclusive, once the game has let go
// of it (DirectInputLate).
internal static class Devices
{
    // Our DirectInput starts a few seconds after launch, after the game's own library has
    // attached its devices (touching DirectInput during that phase is asking for trouble).
    const float StartDelay = 5f;

    public static List<DInput.DeviceInfo> Connected { get; private set; } = new();
    static readonly Dictionary<Guid, DInput.Device> _open = new();

    public static WheelProfile Active { get; private set; }
    static int[] _map; // profile device index -> index in Connected
    public static string Status = "starting";
    public static int Generation; // bumped whenever devices or the active setup change

    static IntPtr _hwnd;
    static bool _started, _rescanSoon;
    static float _nextScan;

    public static void Update(float now)
    {
        if (!_started)
        {
            if (now < StartDelay) return;
            _started = true;
            if (!DInput.Init()) { Status = "DirectInput failed: " + DInput.LastError; Plugin.Error(Status); return; }
        }
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = Win32.FindGameWindow();
            if (_hwnd == IntPtr.Zero) { Status = "waiting for the game window"; return; }
        }

        if (_rescanSoon || now >= _nextScan)
        {
            _rescanSoon = false;
            Rescan();
            // Unknown or incomplete setup: keep looking for devices being plugged in.
            _nextScan = now + (Active == null ? 3f : 30f);
        }

        foreach (var d in _open.Values)
            if (!d.Read() && !d.ForceFeedbackRunning)
            {
                // Unplugged (or the driver hiccupped): rebuild the list shortly.
                if (!_rescanSoon) Plugin.Warn($"device '{d.Info.Name}' stopped answering ({d.Status}), rescanning");
                _rescanSoon = true;
                _nextScan = Math.Min(_nextScan, now + 2f);
            }
    }

    public static void Rescan()
    {
        var list = DInput.Enumerate();
        var keep = new HashSet<Guid>(list.Select(i => i.Instance));
        foreach (var gone in _open.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            Plugin.Info($"device removed: '{_open[gone].Info.Name}'");
            _open[gone].Dispose();
            _open.Remove(gone);
        }
        foreach (var info in list)
        {
            if (_open.ContainsKey(info.Instance)) continue;
            var d = DInput.Device.Open(info, _hwnd, false);
            if (d == null) { Plugin.Warn($"can't open '{info.Name}' ({info.UsbId}): {DInput.LastError}"); continue; }
            d.Read();
            _open[info.Instance] = d;
            Plugin.Info($"device: '{info.Name}' usb {info.UsbId}, {d.Axes} axes, {d.Buttons} buttons, {d.Povs} hats, force feedback {(d.ForceFeedback ? "yes" : "no")}");
        }
        bool changed = list.Count != Connected.Count || list.Where((i, k) => Connected[k].Instance != i.Instance).Any();
        Connected = list;
        if (changed) Generation++;
        ChooseProfile(false);
    }

    // Picks the first setup whose devices are all connected (own setups first, then presets, then built-ins).
    public static void ChooseProfile(bool force)
    {
        if (!force && Active != null)
        {
            var m = Profiles.Match(Active, Connected);
            if (m != null) { _map = m; return; }
        }
        WheelProfile pick = null;
        int[] map = null;
        foreach (var p in Profiles.All())
        {
            map = Profiles.Match(p, Connected);
            if (map != null) { pick = p; break; }
        }
        SetActive(pick, map);
    }

    public static void SetActive(WheelProfile p, int[] map = null)
    {
        map ??= p != null ? Profiles.Match(p, Connected) : null;
        if (p != null && map == null) p = null;
        bool changed = !ReferenceEquals(p, Active);
        if (changed && Active != null) StopForceFeedback();
        Active = p;
        _map = map;
        Generation++;
        if (p != null)
            Status = $"setup '{p.Name}' ({p.Source})";
        else
            Status = Connected.Count == 0 ? "no game controllers connected" : $"no setup for the connected devices ({Settings.SetupKey.Value} = setup wizard)";
        if (changed) Plugin.Info(p != null ? $"active setup: '{p.Name}' ({p.Source}{(p.FilePath != null ? ", " + p.FilePath : "")})" : Status);
    }

    // Device by index in the active profile's device list.
    public static DInput.Device Get(int profileDevice)
    {
        if (Active == null || _map == null || profileDevice < 0 || profileDevice >= _map.Length) return null;
        return _open.TryGetValue(Connected[_map[profileDevice]].Instance, out var d) ? d : null;
    }

    public static DInput.Device GetConnected(int k) =>
        k >= 0 && k < Connected.Count && _open.TryGetValue(Connected[k].Instance, out var d) ? d : null;

    public static IEnumerable<DInput.Device> All => Connected.Select(i => _open.TryGetValue(i.Instance, out var d) ? d : null).Where(d => d != null);

    public static bool LooksLikeWheel(DInput.Device d) =>
        d != null && (d.ForceFeedback || d.Info.Name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0
                      || d.Info.Name.IndexOf("racing", StringComparison.OrdinalIgnoreCase) >= 0);

    public static bool Held(ButtonRef b)
    {
        if (b == null) return false;
        var d = Get(b.Device);
        return d != null && IsDown(d, b.Button);
    }

    public static bool IsDown(DInput.Device d, int button)
    {
        if (button < ButtonRef.PovBase) return button >= 0 && button < DInput.ButtonCount && d.Button[button];
        int hat = (button - ButtonRef.PovBase) / 4, dir = (button - ButtonRef.PovBase) % 4;
        if (hat >= DInput.PovCount) return false;
        int p = d.Pov[hat];
        if (p < 0) return false;
        // Hat angle in hundredths of a degree; diagonals count for both neighbours.
        int center = dir * 9000;
        int diff = Math.Abs(((p - center) % 36000 + 54000) % 36000 - 18000);
        return diff < 6750;
    }

    // --- force feedback device ---------------------------------------------------------------

    public static DInput.Device FfbDevice => Active != null ? Get(Active.Ffb?.Device ?? 0) : null;

    public static bool StartForceFeedback(out string why)
    {
        var d = FfbDevice;
        if (d == null) { why = "no force feedback device"; return false; }
        if (d.ForceFeedbackRunning) { why = "ok"; return true; }
        bool ok = d.StartForceFeedback();
        why = d.Status;
        if (ok) Plugin.Info($"force feedback: '{d.Info.Name}' opened exclusively");
        return ok;
    }

    public static void StopForceFeedback()
    {
        foreach (var d in _open.Values)
            if (d.ForceFeedbackRunning) d.StopForceFeedback();
    }

    public static void Shutdown()
    {
        foreach (var d in _open.Values) d.Dispose();
        _open.Clear();
    }

    public static string Describe()
    {
        var lines = new List<string>();
        foreach (var info in Connected)
        {
            _open.TryGetValue(info.Instance, out var d);
            lines.Add($"'{info.Name}' usb {info.UsbId}" + (d == null ? " (not open)" :
                $": {d.Axes} axes, {d.Buttons} buttons, {d.Povs} hats, ffb {d.ForceFeedback}, status {d.Status}, " +
                $"axes {string.Join(" ", d.Axis.Select((v, k) => $"{DInput.AxisNames[k]}={v}"))}"));
        }
        return string.Join("\n", lines);
    }
}
