using System;
using UnityEngine;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OthWheel;

// Research for shifter support (PLAN-shifter.md, step 0). Debug only.
// - Logs every gear-related change: what the game sends (NetworkInputData.SelectedManualGear), the
//   networked value, the data bus (input gear / gearbox gear, mode, shifting) and the car's gearbox
//   setup (type, number of forward/reverse ratios).
// - With [Debug] GearTestKeys = true: number keys put the car straight into a gear, as an H-shifter
//   would (1-8, 0 = neutral, 9 = reverse -1, minus = back to normal shifting).
internal static class GearProbe
{
    public static ConfigEntry<bool> TestKeys;

    static int? _forced;
    static int _game, _sent;
    static string _last, _lastCar;
    static float _nextCheck;
    static VehicleInput _of;
    static VPVehicleController _car;
    static FunselektorFusionVPStandardInput _std;

    // VK codes: '0'..'9' = 0x30..0x39, minus = VK_OEM_MINUS.
    const ushort VkMinus = 0xBD;
    static readonly bool[] _wasDown = new bool[11];

    public static void Bind(ConfigFile cfg)
    {
        TestKeys = cfg.Bind("Debug", "GearTestKeys", false,
            "Research for shifter support: number keys 1-8 put the car straight into that gear, 0 = neutral, 9 = reverse (-1), " +
            "minus = back to normal shifting. Gear changes are always written to the log.");
    }

    // From InputHooks.ApplyTo, for every input write.
    public static void Apply(ref NetworkInputData d)
    {
        _game = d.SelectedManualGear;
        _gameFlags = (int)d.InputFlags;
        if (_forced.HasValue)
        {
            d.SelectedManualGear = _forced.Value;
            // Test 2: the game ignored SelectedManualGear with its normal flags. Its own DirectInput code
            // supports H-shifters, so the field is probably only used for input marked as coming from a
            // DirectInput device.
            d.InputFlags |= NetworkInputData.InputFlagsEnum.InputSource_DirectInputDevice;
            if (_neutral) d.Clutch = 1f;
        }
        else _neutral = false;
        _sent = d.SelectedManualGear;
        _sentFlags = (int)d.InputFlags;
    }

    static int _gameFlags, _sentFlags;
    static bool _neutral;

    static int CurrentGear()
    {
        try { if (_car != null) { int g = _car.data.Get(Channel.Input, InputData.ManualGear); if (g != 0) return g; } } catch { }
        return 1;
    }

    public static void Update(float now)
    {
        try
        {
            if (TestKeys.Value && !Wizard.IsOpen && Win32.GameHasFocus()) ReadKeys(now);
            else _forced = null;
            if (now < _nextCheck) return;
            _nextCheck = now + 0.05f;
            LogChanges();
        }
        catch (Exception e) { Plugin.ErrorOnce("gear probe", e); }
    }

    static void ReadKeys(float now)
    {
        for (int i = 0; i <= 10; i++)
        {
            ushort vk = i < 10 ? (ushort)(0x30 + i) : VkMinus;
            bool down = Win32.IsKeyDown(vk);
            if (down && !_wasDown[i])
            {
                // Test 3: 0 isn't neutral for the game (it means "no change"), so neutral = keep the gear,
                // clutch fully pressed. Needs a gear to hold: the current one if nothing is forced yet.
                _neutral = i == 0;
                if (i == 0) _forced ??= CurrentGear();
                else _forced = i == 10 ? null : i == 9 ? -1 : i;
                string text = !_forced.HasValue ? "Gear test: off (normal shifting)"
                    : _neutral ? $"Gear test: neutral (clutch fully pressed, holding gear {_forced.Value})" : $"Gear test: forcing gear {_forced.Value}";
                Buttons.LastText = text; Buttons.LastTime = now; Buttons.AlwaysShowUntil = now + 3f;
                Plugin.Info(text);
            }
            _wasDown[i] = down;
        }
    }

    static void LogChanges()
    {
        var vi = InputHooks.LocalVehicleInput;
        if (vi == null) { _of = null; _car = null; _std = null; return; }
        if (_of != vi)
        {
            _of = vi;
            _car = vi.gameObject.GetComponent<VPVehicleController>();
            _std = vi.gameObject.GetComponent<FunselektorFusionVPStandardInput>();
        }
        if (_car == null) return;

        string carInfo = CarInfo();
        if (carInfo != _lastCar) { _lastCar = carInfo; Plugin.Info("gear probe car: " + carInfo); }

        var d = _car.data;
        int inManual = d.Get(Channel.Input, InputData.ManualGear), inAuto = d.Get(Channel.Input, InputData.AutomaticGear), inShift = d.Get(Channel.Input, InputData.GearShift);
        int boxGear = d.Get(Channel.Vehicle, VehicleData.GearboxGear), boxMode = d.Get(Channel.Vehicle, VehicleData.GearboxMode), shifting = d.Get(Channel.Vehicle, VehicleData.GearboxShifting);
        string net = "?", low = "?";
        try { if (_std != null) { net = _std.SelectedManualGear.ToString(); low = _std.GearboxIsLowTransferCase.ToString(); } } catch { }
        int clutch = d.Get(Channel.Input, InputData.Clutch);
        string state = $"game-sends={_game} flags=0x{_gameFlags:X} we-send={_sent} flags=0x{_sentFlags:X}{(_forced.HasValue ? " (forced)" : "")}{(_neutral ? " NEUTRAL" : "")} networked={net} bus-clutch={clutch} low-range={low} | " +
                       $"bus input: manual={inManual} auto={inAuto} shift={inShift} | gearbox: gear={boxGear} mode={boxMode} shifting={shifting}";
        if (state == _last) return;
        _last = state;
        float rpm = d.Get(Channel.Vehicle, VehicleData.EngineRpm) / 1000f;
        var rb = _car.cachedRigidbody;
        float kmh = rb != null ? rb.velocity.magnitude * 3.6f : 0f;
        Plugin.Info($"gear probe: {state} | rpm={rpm:0} km/h={kmh:0}");
    }

    static string CarInfo()
    {
        try
        {
            var g = _car.gearbox;
            if (g == null) return $"{_car.name}: no gearbox settings";
            int fwd = g.forwardGearRatios?.Length ?? -1, rev = g.reverseGearRatios?.Length ?? -1;
            string ratios = "";
            if (g.forwardGearRatios != null) for (int i = 0; i < fwd; i++) ratios += (i > 0 ? " " : "") + g.forwardGearRatios[i].ToString("0.00");
            return $"{_car.name}: gearbox type={g.type} forward gears={fwd} ({ratios}) reverse gears={rev}";
        }
        catch (Exception e) { return $"{_car.name}: gearbox unreadable ({e.GetType().Name})"; }
    }
}
