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
    static bool _started;

    // --- when to scan ------------------------------------------------------------------------
    // A scan (DirectInput EnumDevices + opening new devices) runs on the main thread and can take tens to
    // hundreds of ms on some PCs (Windows asks every HID device: headsets, RGB, hubs...). That's a frame
    // drop, so it must not run on a short timer (it was every 3 s while no setup matched). Instead a cheap
    // check of Windows' device list (every 1 s, ~0.01 ms) says when something was plugged in or out; only
    // then we scan. Measured on the owner's PC with no controller on: one EnumDevices = 165-180 ms.
    const float MinScanGap = 2f;          // never two scans closer than this
    const float ChangeSettle = 1.5f;      // wait after the last device-list change (Windows adds devices in steps)
    const float FallbackNoCheck = 10f;    // only without that check (shouldn't happen), no setup yet
    const float FallbackNoCheckSetup = 30f;
    static float _nextScan, _scanSoonAt = -1f, _lastScan = -100f, _nextListCheck, _lastChangeLog = -100f;
    static long _fingerprint;
    static bool _listCheckWorks;
    static int _followUps; // looks left after a plug-in that matched no setup yet
    static int _scans;

    // Devices that wouldn't open: tried again later (10 s, 30 s, 90 s, then 5 min), not on every scan.
    static readonly Dictionary<Guid, (int tries, float retryAt)> _openFails = new();
    // Open devices whose reads fail: tried once a second instead of every frame, reopened at the next scan.
    static readonly Dictionary<Guid, float> _failing = new();
    // Open devices that read fine at least once (only those trigger a rescan when they stop answering).
    static readonly HashSet<Guid> _readOk = new();

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

        if (now >= _nextListCheck) CheckDeviceList(now);

        if (now - _lastScan >= MinScanGap)
        {
            bool listChanged = _scanSoonAt >= 0f && now >= _scanSoonAt;
            if (listChanged || now >= _nextScan)
            {
                string why = listChanged ? "device list changed" : _scans == 0 ? "start" : _followUps > 0 ? "follow-up" : "timer";
                if (listChanged) _followUps = 2;
                else if (_followUps > 0) _followUps--;
                _scanSoonAt = -1f;
                Rescan(now, why);
                // No timer while the device-list check works: a scan is a frame drop.
                _nextScan = _listCheckWorks ? float.MaxValue : now + (Active == null ? FallbackNoCheck : FallbackNoCheckSetup);
                // Something was plugged in but no setup matches yet: its DirectInput side may lag, look again
                // after 4 s and 12 s, then stop.
                if (Active == null && _followUps > 0) _nextScan = now + (_followUps == 2 ? 4f : 8f);
                else _followUps = 0;
            }
        }

        foreach (var kv in _open)
        {
            var d = kv.Value;
            bool failing = _failing.TryGetValue(kv.Key, out float retry);
            if (failing && now < retry) continue;
            if (d.Read() || d.ForceFeedbackRunning)
            {
                if (failing) { _failing.Remove(kv.Key); Plugin.Info($"device '{d.Info.Name}' answers again"); }
                _readOk.Add(kv.Key);
                continue;
            }
            if (!failing)
            {
                // Unplugged (or the driver hiccupped): rebuild the list soon, once (it used to rescan every frame).
                // A device that never read fine doesn't trigger scans (that would loop every 2 s).
                bool wasOk = _readOk.Remove(kv.Key);
                Plugin.Warn($"device '{d.Info.Name}' {(wasOk ? "stopped answering" : "can't be read")} ({d.Status}){(wasOk ? ", rescanning" : "")}");
                if (wasOk && _scanSoonAt < 0f) _scanSoonAt = now + 0.5f;
            }
            _failing[kv.Key] = now + 1f;
        }
    }

    // Windows' input device list (cheap). A change = something plugged in or out: scan once it settles.
    static void CheckDeviceList(float now)
    {
        _nextListCheck = now + 1f;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long fp = Win32.InputDeviceFingerprint();
        if (fp == 0)
        {
            if (_listCheckWorks) Plugin.Warn($"device list check stopped working; scanning every {FallbackNoCheck:0}-{FallbackNoCheckSetup:0} s instead");
            if (_listCheckWorks || _nextScan > now + FallbackNoCheckSetup) _nextScan = now + FallbackNoCheck;
            _listCheckWorks = false;
            return;
        }
        if (!_listCheckWorks)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Plugin.Info($"device list check works ({ms:0.00} ms); game controllers are scanned only when something is plugged in or out");
            _listCheckWorks = true;
        }
        if (_fingerprint != 0 && fp != _fingerprint)
        {
            if (now - _lastChangeLog >= 10f) { _lastChangeLog = now; Plugin.Info("Windows' device list changed (plugged in or out), scanning game controllers shortly"); }
            _scanSoonAt = now + ChangeSettle; // each further change pushes the scan out (a device flapping in and out)
            _openFails.Clear();               // replugged: worth trying those again
        }
        _fingerprint = fp;
    }

    static void Rescan(float now, string why)
    {
        _lastScan = now;
        _scans++;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = DInput.Enumerate();
        double listMs = sw.Elapsed.TotalMilliseconds;

        var keep = new HashSet<Guid>(list.Select(i => i.Instance));
        foreach (var gone in _open.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            Plugin.Info($"device removed: '{_open[gone].Info.Name}'");
            _open[gone].Dispose();
            _open.Remove(gone);
            _failing.Remove(gone);
            _readOk.Remove(gone);
        }
        // Still listed but its reads keep failing: close it and open it again below. (Scans are rare now, and a
        // device that never read fine doesn't trigger them, so this can't loop.)
        foreach (var bad in _failing.Keys.Where(k => _open.ContainsKey(k) && !_open[k].ForceFeedbackRunning).ToList())
        {
            Plugin.Info($"reopening '{_open[bad].Info.Name}' (reads failed)");
            _open[bad].Dispose();
            _open.Remove(bad);
            _failing.Remove(bad);
        }

        double openStart = sw.Elapsed.TotalMilliseconds;
        var slow = new List<string>();
        foreach (var info in list)
        {
            if (_open.ContainsKey(info.Instance)) continue;
            _openFails.TryGetValue(info.Instance, out var fail);
            if (fail.tries > 0 && now < fail.retryAt) continue;
            double t = sw.Elapsed.TotalMilliseconds;
            var d = DInput.Device.Open(info, _hwnd, false);
            d?.Read();
            double took = sw.Elapsed.TotalMilliseconds - t;
            if (took > 20) slow.Add($"'{info.Name}' {took:0} ms");
            if (d == null)
            {
                int tries = fail.tries + 1;
                float wait = tries switch { 1 => 10f, 2 => 30f, 3 => 90f, _ => 300f };
                _openFails[info.Instance] = (tries, now + wait);
                Plugin.Warn($"can't open '{info.Name}' ({info.UsbId}): {DInput.LastError}; next try at a later scan, not before {wait:0} s (try {tries})");
                continue;
            }
            _openFails.Remove(info.Instance);
            _open[info.Instance] = d;
            Plugin.Info($"device: '{info.Name}' usb {info.UsbId}, {d.Axes} axes, {d.Buttons} buttons, {d.Povs} hats, force feedback {(d.ForceFeedback ? "yes" : "no")}");
        }
        double openMs = sw.Elapsed.TotalMilliseconds - openStart;

        bool changed = list.Count != Connected.Count || list.Where((i, k) => Connected[k].Instance != i.Instance).Any();
        Connected = list;
        if (changed) Generation++;
        double t1 = sw.Elapsed.TotalMilliseconds;
        ChooseProfile(false);
        double setupsMs = sw.Elapsed.TotalMilliseconds - t1;
        double total = sw.Elapsed.TotalMilliseconds;

        // The first scans are always logged (to compare PCs); later ones only when slow enough to feel.
        if (_scans <= 3 || total > 20)
            Plugin.Info($"device scan #{_scans} ({why}): {total:0.0} ms (list {listMs:0.0} ms for {list.Count} controllers, opening {openMs:0.0} ms, setups {setupsMs:0.0} ms)"
                        + (slow.Count > 0 ? "; slow: " + string.Join(", ", slow) : "")
                        + (Active == null ? "; no setup matches" : ""));
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
