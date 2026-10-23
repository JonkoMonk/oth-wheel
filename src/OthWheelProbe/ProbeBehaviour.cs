extern alias udi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.XrefScans;
using UnityEngine;
using VehiclePhysics;
using DIManager = udi::DirectInputManager.DIManager;
using DIInputManager = udi::DIInputManager;
using DeviceInfo = udi::DirectInputManager.DeviceInfo;
using WinMM = OthWheel.WinMM;

namespace OthWheelProbe;

// Runs on Unity's main thread: turns the hook counters into log lines once per second
// and does the one-off scans (code xrefs, scene objects, car components, devices).
public class ProbeBehaviour : MonoBehaviour
{
    public ProbeBehaviour(IntPtr ptr) : base(ptr) { }

    static readonly Regex Interesting = new("Input|DirectInput|FFB|ForceFeedback|Feedback|Wheel|Rumble|Haptic|Steer", RegexOptions.IgnoreCase);

    float _nextSecond, _nextAxis, _nextError, _udiAt = float.MaxValue;
    bool _startupDone, _diWrapperScanned, _sceneScannedInCar, _mappingsListed, _udiReady;
    readonly List<(FunselektorFusionVPStandardInput input, float at)> _pending = new();
    VehicleBase _localVehicle;
    string _lastDim, _lastMapValues, _lastLocalInput;
    udi::DirectInputManager.FlatJoyState2 _lastState;
    bool _haveState;

    static void Log(string s) => Plugin.L.LogInfo($"[Probe] t={Time.unscaledTime:0.00} " + s);

    int _winmmId = -2;
    WinMM.JOYINFOEX _lastJoy;
    bool _haveJoy;
    float _nextHeartbeat;
    string _lastFfbFields;
    int _secondCount;

    void Update()
    {
        float now = Time.unscaledTime;
        Safe("startup", () =>
        {
            if (_startupDone || now < 8f) return;
            _startupDone = true;
            if (Plugin.CfgXref.Value) Guarded("xref", XrefReport);
            Guarded("scene-startup", () => SceneScan("startup"));
        });

        Safe("spawn queue", () =>
        {
            lock (Hooks.Lock)
                while (Hooks.SpawnedInputs.Count > 0)
                    _pending.Add((Hooks.SpawnedInputs.Dequeue(), now + 2f));
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now < _pending[i].at) continue;
                var inp = _pending[i].input;
                _pending.RemoveAt(i);
                bool local = false;
                if (inp != null) Guarded("car-report", () => local = VehicleReport(inp));
                if (!local) continue;
                if (!_sceneScannedInCar) { _sceneScannedInCar = true; Guarded("scene-car", () => SceneScan("in car")); }
                if (Plugin.CfgDIWrapperScan.Value && !_diWrapperScanned) { _diWrapperScanned = true; Guarded("diwrapper", DIWrapperScan); }
                if (_udiAt == float.MaxValue) _udiAt = now + 3f;
            }
        });

        // Only touch UnityDirectInput classes once the game has long since initialized them.
        if (!_udiReady && now >= _udiAt)
        {
            _udiReady = true;
            Guarded("directinput-hooks", Hooks.InstallDirectInput);
            // Native hooks could also crash later, not just while installing: keep the marker for 30 s.
            if (Plugin.CfgNativeHooks.Value && !GuardTripped("native-hooks"))
            {
                SetGuard("native-hooks");
                Safe("native hooks", Hooks.InstallNative);
                _nativeGuardClearAt = now + 30f;
            }
        }
        if (_nativeGuardClearAt > 0 && now >= _nativeGuardClearAt)
        {
            _nativeGuardClearAt = 0;
            ClearGuard("native-hooks");
            Log("native hooks survived 30 s, guard cleared");
        }

        if (now >= _nextSecond)
        {
            _nextSecond = now + 1f;
            Safe("counters", CounterReport);
            if (_udiReady) Safe("DIInputManager", DIInputManagerReport);
            Safe("local input", LocalInputReport);
        }

        // Every frame: catch short force feedback pulses (e.g. the wall jolt) that a 1 s sample would miss.
        if (_udiReady) Safe("ffb fields", FfbFieldWatch);

        if (Plugin.CfgAxisLog.Value && now >= _nextAxis)
        {
            _nextAxis = now + 0.1f;
            bool heartbeat = now >= _nextHeartbeat;
            if (heartbeat) _nextHeartbeat = now + 5f;
            Safe("winmm axes", () => WinmmReport(heartbeat));
            if (_udiReady) Safe("axes", () => AxisReport(heartbeat));
        }
    }

    void FfbFieldWatch()
    {
        var m = DIInputManager.Instance;
        if (m == null) return;
        var s = $"const={m.ConstantForceEnabled}/{m.ConstantForceMagnitude} damper={m.DamperForceEnabled}/{m.DamperMagnitude} " +
                $"friction={m.FrictionForceEnabled}/{m.FrictionMagnitude} spring={m.SpringForceEnabled}/{m.SpringCoefficient} " +
                $"sine={m.SineForceEnabled}/{m.SineMagnitude} collision F{m.TestFrontalCollision} R{m.TestRearCollision} L{m.TestLeftCollision} Rt{m.TestRightCollision}";
        if (s != _lastFfbFields) { Log("FFB fields: " + s); _lastFfbFields = s; }
    }

    void WinmmReport(bool heartbeat)
    {
        if (_winmmId == -2)
        {
            _winmmId = WinMM.Find(WinMM.MozaVid, out var caps);
            Log(_winmmId < 0 ? "winmm: no Moza joystick found"
                : $"winmm: Moza is joystick #{_winmmId} '{caps.szPname}' axes={caps.wNumAxes} buttons={caps.wNumButtons} caps=0x{caps.wCaps:X} " +
                  $"X{caps.wXmin}-{caps.wXmax} Y{caps.wYmin}-{caps.wYmax} Z{caps.wZmin}-{caps.wZmax} R{caps.wRmin}-{caps.wRmax} U{caps.wUmin}-{caps.wUmax} V{caps.wVmin}-{caps.wVmax}");
        }
        if (_winmmId < 0) return;
        if (!WinMM.Read(_winmmId, out var j)) { if (heartbeat) Log("winmm: read failed"); return; }
        static bool D(uint a, uint b) => Math.Abs((long)a - b) > 300;
        bool changed = !_haveJoy || D(j.dwXpos, _lastJoy.dwXpos) || D(j.dwYpos, _lastJoy.dwYpos) || D(j.dwZpos, _lastJoy.dwZpos) ||
                       D(j.dwRpos, _lastJoy.dwRpos) || D(j.dwUpos, _lastJoy.dwUpos) || D(j.dwVpos, _lastJoy.dwVpos) ||
                       j.dwButtons != _lastJoy.dwButtons || j.dwPOV != _lastJoy.dwPOV;
        if (!changed && !heartbeat) return;
        _lastJoy = j; _haveJoy = true;
        Log($"winmm{(changed ? "" : " (heartbeat)")}: X={j.dwXpos} Y={j.dwYpos} Z={j.dwZpos} R={j.dwRpos} U={j.dwUpos} V={j.dwVpos} buttons=0x{j.dwButtons:X} pov={j.dwPOV}");
    }

    // A native crash can't be caught, so leave a marker file while a risky one-off step runs.
    // If the game dies mid-step, the marker survives and the step is skipped on the next launch.
    void Guarded(string name, Action a)
    {
        if (GuardTripped(name)) return;
        SetGuard(name);
        Log($"step '{name}' start");
        Safe(name, a);
        ClearGuard(name);
        Log($"step '{name}' done");
    }

    float _nativeGuardClearAt;
    static string GuardPath(string name) => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "OthWheelProbe-guards", name);

    static bool GuardTripped(string name)
    {
        if (!System.IO.File.Exists(GuardPath(name))) return false;
        Log($"SKIPPING '{name}': it crashed the game last time (delete {GuardPath(name)} to retry)");
        return true;
    }

    static void SetGuard(string name)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GuardPath(name)));
        System.IO.File.WriteAllText(GuardPath(name), DateTime.Now.ToString("s"));
    }

    static void ClearGuard(string name) => System.IO.File.Delete(GuardPath(name));

    void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            if (Time.unscaledTime < _nextError) return;
            _nextError = Time.unscaledTime + 5f;
            Plugin.L.LogError($"[Probe] {what}: {e.GetType().Name}: {e.Message}");
        }
    }

    // ---------- once per second ----------

    void CounterReport()
    {
        // Rare calls are reported every second; per-frame ones only every 10 s to keep the log readable.
        bool full = ++_secondCount % 10 == 0;
        var lines = new List<string>();
        lock (Hooks.Lock)
        {
            foreach (var c in Hooks.Counters.Values)
            {
                if (c.Total == c.LastReported) continue;
                if (!full && c.Total - c.LastReported > 20) continue;
                lines.Add($"calls {c.Name}: +{c.Total - c.LastReported} (total {c.Total}) maxInt={c.MaxAbsInt} last=[{c.LastArgs}]");
                c.LastReported = c.Total;
                c.MaxAbsInt = 0;
            }
            if (full && Hooks.InputValues.Count > 0)
            {
                lines.Add("read values: " + string.Join("  ", Hooks.InputValues.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.value:0.000}(x{k.Value.count})")));
                Hooks.InputValues.Clear();
            }
        }
        foreach (var l in lines) Log(l);
    }

    void DIInputManagerReport()
    {
        var m = DIInputManager.Instance;
        if (m == null) { if (_lastDim != "none") Log("DIInputManager.Instance = null"); _lastDim = "none"; return; }

        var s = $"DIInputManager: init={m.isInitialized} useDInputManager={m.useDInputManager} ffbDevice='{m.FFBDeviceName}' #{m.fFBDeviceNumber} " +
                $"const={m.ConstantForceEnabled}/{m.ConstantForceMagnitude} damper={m.DamperForceEnabled}/{m.DamperMagnitude} " +
                $"friction={m.FrictionForceEnabled}/{m.FrictionMagnitude} inertia={m.InertiaForceEnabled}/{m.InertiaMagnitude} " +
                $"spring={m.SpringForceEnabled}/off{m.SpringOffset}/coef{m.SpringCoefficient}/sat{m.SpringSaturation} " +
                $"sine={m.SineForceEnabled}/{m.SineMagnitude} square={m.SquareForceEnabled}/{m.SquareMagnitude} " +
                $"testCollision={m.TestCollisionMagnitude} F{m.TestFrontalCollision} R{m.TestRearCollision} L{m.TestLeftCollision} Rt{m.TestRightCollision}";
        if (s != _lastDim) { Log(s); _lastDim = s; }

        var maps = m.inputMappings;
        if (maps == null) return;
        if (!_mappingsListed && maps.Length > 0)
        {
            _mappingsListed = true;
            Log($"DIInputManager.inputMappings ({maps.Length}):");
            foreach (var mp in maps)
                if (mp != null) Log($"   mapping '{mp.mappingName}' device='{mp.deviceName}' input='{mp.inputName}' inverted={mp.inverted}");
        }
        var sb = new StringBuilder();
        foreach (var mp in maps)
            if (mp != null) sb.Append($"{mp.mappingName}={mp.currentValue:0.00} ");
        var vals = sb.ToString();
        if (vals != _lastMapValues) { Log("mapping values: " + vals); _lastMapValues = vals; }
    }

    void LocalInputReport()
    {
        if (_localVehicle == null) return;
        var d = _localVehicle.data;
        if (d == null) return;
        int ch = Channel.Input;
        var s = $"car input bus: steer={d.Get(ch, InputData.Steer)} throttle={d.Get(ch, InputData.Throttle)} brake={d.Get(ch, InputData.Brake)} " +
                $"handbrake={d.Get(ch, InputData.Handbrake)} clutch={d.Get(ch, InputData.Clutch)}";
        if (s != _lastLocalInput) { Log(s); _lastLocalInput = s; }
    }

    void AxisReport(bool heartbeat)
    {
        var m = DIInputManager.Instance;
        var dev = m?.ffbDevice;
        var adi = dev?.attachedDevice;
        if (adi == null) return;
        string guid = adi.deviceInfo.guidInstance;
        if (string.IsNullOrEmpty(guid)) return;
        var st = DIManager.GetDeviceState(guid);
        bool changed = !_haveState || Changed(_lastState, st);
        if (!changed && !heartbeat) return;
        _lastState = st; _haveState = true;
        Log($"R3 via library{(changed ? "" : " (heartbeat)")}: X={st.lX} Y={st.lY} Z={st.lZ} U={st.lU} V={st.lV} Rx={st.lRx} Ry={st.lRy} Rz={st.lRz} " +
            $"btnA={st.buttonsA:X} btnB={st.buttonsB:X} pov={st.rgdwPOV}");
    }

    static bool Changed(udi::DirectInputManager.FlatJoyState2 a, udi::DirectInputManager.FlatJoyState2 b)
    {
        static bool D(ushort x, ushort y) => Math.Abs(x - y) > 300;
        return D(a.lX, b.lX) || D(a.lY, b.lY) || D(a.lZ, b.lZ) || D(a.lU, b.lU) || D(a.lV, b.lV) ||
               D(a.lRx, b.lRx) || D(a.lRy, b.lRy) || D(a.lRz, b.lRz) ||
               a.buttonsA != b.buttonsA || a.buttonsB != b.buttonsB || a.rgdwPOV != b.rgdwPOV;
    }

    // ---------- one-off scans ----------

    // Returns true if this is the local player's car.
    bool VehicleReport(FunselektorFusionVPStandardInput inp)
    {
        string auth = "?";
        bool local = false;
        try { local = inp.HasInputAuthority; auth = $"inputAuth={local} stateAuth={inp.HasStateAuthority}"; } catch (Exception e) { auth = "auth? " + e.Message; }

        VehicleBase veh = null;
        try { veh = inp.vehicle; } catch { }
        var root = veh != null ? veh.gameObject : inp.gameObject;
        Log($"=== car enabled: '{root.name}' {auth} vehicleType={(veh != null ? veh.GetIl2CppType().FullName : "null")}");

        var comps = root.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
        var byType = new Dictionary<string, int>();
        int listed = 0;
        foreach (var c in comps)
        {
            if (c == null) continue;
            var tn = c.GetIl2CppType().FullName;
            byType[tn] = byType.TryGetValue(tn, out var n) ? n + 1 : 1;
            if ((local || Interesting.IsMatch(tn)) && listed < 400)
            {
                listed++;
                var b = c.TryCast<Behaviour>();
                var en = b != null ? $" enabled={b.enabled}" : "";
                Log($"   {Path(c.transform, root.transform)} : {tn}{en} active={c.gameObject.activeInHierarchy}");
            }

            var w = c.TryCast<VPWheelDeviceInput>();
            if (w != null)
                Log($"   >> VPWheelDeviceInput enabled={w.enabled} deviceIndex={w.deviceIndex} range={w.configuredWheelRange} force={w.forceIntensity} weight={w.weightIntensity} " +
                    $"damper={w.damperEffect} friction={w.frictionEffect} rumble={w.enableRumble} steerAidsOff={w.disableSteeringAids} ready={w.m_deviceReady}");
            var d = c.TryCast<VPDeviceInput>();
            if (d != null)
                Log($"   >> VPDeviceInput enabled={d.enabled} selectedDevice={d.selectedDevice} model={d.deviceModel} range={d.configuredWheelRange} force={d.forceIntensity} " +
                    $"fallbackToStandard={d.fallbackToStandardInput} ready={d.m_deviceReady}");
        }
        Log($"   component counts: " + string.Join(", ", byType.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}")));

        if (local) _localVehicle = veh;
        return local;
    }

    static string Path(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (var x = t; x != null; x = x.parent)
        {
            parts.Add(x.name);
            if (x == root) break;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    void SceneScan(string tag)
    {
        var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MonoBehaviour>(), true);
        var counts = new Dictionary<string, (int total, int on)>();
        foreach (var o in all)
        {
            var mb = o?.TryCast<MonoBehaviour>();
            if (mb == null) continue;
            var tn = mb.GetIl2CppType().FullName;
            if (!Interesting.IsMatch(tn)) continue;
            bool on = mb.enabled && mb.gameObject.activeInHierarchy;
            counts.TryGetValue(tn, out var v);
            counts[tn] = (v.total + 1, v.on + (on ? 1 : 0));
            if (tn is "RuntimeInputMappingUI" or "RuntimeFFBConfigUI" or "DIInputValueMonitor" or "DirectInputLogger" or "DIInputManager"
                || tn.Contains("VPWheelDeviceInput") || tn.Contains("VPDeviceInput"))
                Log($"[scene:{tag}] {tn} on '{Path(mb.transform, null)}' enabled={mb.enabled} active={mb.gameObject.activeInHierarchy}");
        }
        Log($"[scene:{tag}] input-related behaviours (type: total/active): " +
            string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value.total}/{k.Value.on}")));
    }

    void DIWrapperScan()
    {
        Log("DIWrapper scan (VPP's own DirectInput list). Note: this calls DIWrapper.Initialize once.");
        bool ok = DIWrapper.Initialize();
        int n = ok ? DIWrapper.GetNumDevices() : -1;
        Log($"   DIWrapper.Initialize={ok} devices={n} lastResult=0x{DIWrapper.GetLastResult():X}");
        for (int i = 0; i < n; i++)
        {
            try
            {
                bool got = DIWrapper.GetDeviceInfo(i, out var di);
                Log($"   [{i}] ok={got} product='{di.productName}' instance='{di.instanceName}' type={DIWrapper.DeviceTypeToString(di.dwDevType)}");
            }
            catch (Exception e) { Log($"   [{i}] error {e.GetType().Name}: {e.Message}"); }
        }
    }

    // Static scan of the game's compiled code: who calls these methods, and what do they call.
    void XrefReport()
    {
        var vi = typeof(VehicleInput);
        var dim = typeof(DIInputManager);
        var dm = typeof(DIManager);
        // Not AccessTools.TypeByName: it walks every type of every interop assembly, which is unsafe here.
        var cheats = vi.Assembly.GetType("CheatsController");

        var outgoing = new List<MethodBase>
        {
            AccessTools.Method(vi, "GetInputFromDirectInputDevice"),
            AccessTools.Method(vi, "UpdateDirectInputReliableButtons"),
            AccessTools.Method(vi, "Fusion_INetworkRunnerCallbacks_OnInput"),
            AccessTools.Method(vi, "GetInputFlags"),
            AccessTools.Method(typeof(global::DirectInputManager), "Start"),
            AccessTools.Method(typeof(global::DirectInputManager), "EnableDInputManager"),
            AccessTools.Method(dim, "UpdateFFBEffects"),
            AccessTools.Method(dim, "TestDirectionalCollision"),
            cheats != null ? AccessTools.Method(cheats, "EnableDirectInputSupport") : null,
        };
        var incoming = new List<MethodBase>
        {
            AccessTools.PropertyGetter(dim, "Instance"),
            AccessTools.Method(dim, "TestDirectionalCollision"),
            AccessTools.Method(dim, "GetDeviceInputValue"),
            AccessTools.Method(dim, "GetFFBDeviceAxisValue"),
            AccessTools.Method(dim, "GetMappingByName"),
            AccessTools.Method(dm, "UpdateConstantForceSimple", new[] { typeof(string), typeof(int) }),
            AccessTools.Method(dm, "UpdateConstantForceSimple", new[] { typeof(DeviceInfo), typeof(int) }),
            AccessTools.Method(vi, "GetInputFromDirectInputDevice"),
            AccessTools.Method(vi, "UpdateDirectInputReliableButtons"),
            AccessTools.PropertyGetter(typeof(NetworkInputData), "IsUsingDirectInputDevice"),
            AccessTools.Method(typeof(DIWrapper), "Initialize"),
            AccessTools.Method(typeof(DIWrapper), "UpdateConstantForceEffect"),
        };

        foreach (var m in outgoing.Where(x => x != null))
        {
            Log($"[xref] scanning {Hooks.Describe(m)}");
            var items = new List<string>();
            try
            {
                // Method calls only. Reading "global" xrefs as objects dereferences raw pointers and can crash.
                foreach (var x in XrefScanner.XrefScan(m).Take(300))
                {
                    if (x.Type != XrefType.Method) continue;
                    var r = x.TryResolve();
                    var s = r != null ? $"{r.DeclaringType?.FullName}.{r.Name}" : null;
                    if (s != null && (items.Count == 0 || items[^1] != s)) items.Add(s);
                }
            }
            catch (Exception e) { items.Add("ERR " + e.Message); }
            Log($"[xref] {Hooks.Describe(m)} uses: " + string.Join(" | ", items));
        }

        foreach (var m in incoming.Where(x => x != null))
        {
            Log($"[xref] users of {Hooks.Describe(m)}");
            var items = new List<string>();
            try
            {
                foreach (var x in XrefScanner.UsedBy(m).Take(100))
                {
                    var r = x.TryResolve();
                    if (r != null) items.Add($"{r.DeclaringType?.FullName}.{r.Name}");
                }
            }
            catch (Exception e) { items.Add("ERR " + e.Message); }
            Log($"[xref] {Hooks.Describe(m)} used by: " + (items.Count == 0 ? "(none found)" : string.Join(" | ", items.Distinct())));
        }
    }
}
