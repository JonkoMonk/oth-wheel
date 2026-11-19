using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OthWheel;

// H-pattern shifter (PLAN-shifter.md). A shifter has its own small setup, separate from the wheel
// setup and matched by its own USB id, so it works with any wheel (also the built-in R3 setup):
//  - set up in the wizard (F3 > 7), saved in <data folder>\shifters\
//  - built-in presets: Presets\Shifters\*.json (embedded)
// Every gear slot is a button that is held while the lever sits in it. No button = neutral.
public enum RangeGears { Auto, Off }
// What the car does when a gear grinds (clutch to shift): Neutral = the lever left the old gear, so neutral;
// StayInGear = the old gear stays in, as if the shift was refused.
public enum AfterGrind { Neutral, StayInGear }

public sealed class ShifterProfile
{
    public int Format { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Source { get; set; } = "wizard";
    public string ModVersion { get; set; } = Plugin.Version;
    public DeviceRef Device { get; set; } = new();
    // Lever slot ("1".."7", "R") -> button (0-127, or 128+ for hat directions, as in ButtonRef).
    public Dictionary<string, int> Gears { get; set; } = new();
    public string Notes { get; set; } = "";

    [JsonIgnore] public string FilePath;

    public static readonly string[] Slots = { "1", "2", "3", "4", "5", "6", "7", "R" };

    public string Describe()
    {
        var fwd = Gears.Keys.Where(k => k != "R").Select(k => int.TryParse(k, out var n) ? n : 0).Where(n => n > 0).OrderBy(n => n).ToList();
        string s = fwd.Count == 0 ? "no gears" : fwd.Count == 1 ? "gear 1" : $"gears {fwd.First()}-{fwd.Last()}";
        return s + (Gears.ContainsKey("R") ? " + reverse" : "");
    }
}

internal static class Shifter
{
    public static ConfigEntry<bool> Enabled;

    public static ShifterProfile Active { get; private set; }
    static DInput.Device _dev;
    static int _generation = -1;

    // Lever position: -1 = reverse, 1..7 = gear, 0 = neutral (no slot).
    public static int Slot { get; private set; }
    // The gear sent to the game (slot limited to the car's gears); held while in neutral.
    public static int Gear { get; private set; }
    // A setup can match a device without the shifter being there (the Moza HGP preset matches every R3, since the
    // HGP plugs into the base). So the shifter only takes over once a gear has been engaged; until then neutral
    // would hold the clutch down for good and paddles would be ignored.
    static bool _armed;
    static bool Ready => Active != null && _dev != null && Enabled.Value && Settings.Enabled.Value;
    public static bool Engaged => Ready && _armed;
    // Lever in no slot, or a gear that didn't go in (clutch required): CarPhysics puts the gearbox itself in
    // neutral (the clutch stays held as a fallback).
    public static bool InNeutral => Engaged && _inGear == 0;
    // The slot the game gets: Slot, or 0 while the gear grinds.
    static int _inGear;
    public static float LastChange { get; private set; } = -100f;

    public static string Dir => Path.Combine(Profiles.Root, "shifters");

    public static ConfigEntry<RangeGears> RangeAsGears;
    public static ConfigEntry<bool> ClutchRequired;
    public static ConfigEntry<float> ClutchToShift, GrindBuzz, GrindBuzzLength;
    public static ConfigEntry<AfterGrind> GrindResult;

    public static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Shifter", "Enabled", true,
            "Use the H-pattern shifter set up in the setup wizard (F3 > 7). false = ignore it (paddles / shift buttons only).");
        RangeAsGears = cfg.Bind("Shifter", "RangeAsGears", RangeGears.Auto,
            "Auto = on cars with only 2 forward gears the shifter works like a 4-speed: 1 = 1st low, 2 = 2nd low, 3 = 1st high, 4 = 2nd high, " +
            "R = reverse low (the mod switches the high/low range for you). Off = every slot is that gear, the range stays on its own button.");
        ClutchRequired = cfg.Bind("Shifter", "ClutchRequired", false,
            "H-shifter: a gear only goes in with the clutch pedal pressed (at least ClutchToShift). Without it the gear grinds " +
            "(a short buzz in the wheel) and doesn't go in: the car stays in neutral until you press the clutch (lever still in the gear) " +
            "or move the lever again. Only once a clutch pedal has been pressed this session, and only while the engine runs.");
        ClutchToShift = cfg.Bind("Shifter", "ClutchToShift", 0.5f,
            "With ClutchRequired: how far the clutch pedal must be pressed for a gear to go in (0.1-1; 0.5 = halfway).");
        GrindBuzz = cfg.Bind("Shifter", "GrindBuzz", 0.4f,
            "With ClutchRequired: strength of the buzz in the wheel when a gear grinds (0 = none, 1 = strong).");
        // JonkoMonk's test 2026-11-13: both as settings, neutral and 0.3 s stay the defaults.
        GrindResult = cfg.Bind("Shifter", "AfterGrind", AfterGrind.Neutral,
            "With ClutchRequired, when a gear grinds: Neutral = the car is in neutral (the lever has left the old gear); " +
            "StayInGear = the old gear stays in, as if the shift was refused (also while the lever crosses neutral; a lever left " +
            "in neutral for over half a second without the clutch is neutral). Either way, pressing the clutch with the lever " +
            "still in the new gear puts it in, and moving the lever back to the old gear just keeps it.");
        GrindBuzzLength = cfg.Bind("Shifter", "GrindBuzzLength", 0.3f,
            "With ClutchRequired: how long the grind buzz lasts, seconds (0.1-1; it then fades out over 0.3 s). " +
            "More than 1 = as long as the lever sits in the gear without the clutch.");
    }

    // --- setups -----------------------------------------------------------------------------------

    static List<ShifterProfile> _builtin;

    static List<ShifterProfile> BuiltIn()
    {
        if (_builtin != null) return _builtin;
        _builtin = new List<ShifterProfile>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(Profiles.IsShifterResource))
        {
            try
            {
                using var s = asm.GetManifestResourceStream(res);
                var p = JsonSerializer.Deserialize<ShifterProfile>(s, WheelProfile.Json);
                if (p?.Device == null || p.Gears == null || p.Gears.Count == 0) continue;
                p.Source = "builtin";
                _builtin.Add(p);
            }
            catch (Exception e) { Plugin.Error($"built-in shifter preset {res}: {e.Message}"); }
        }
        return _builtin;
    }

    // The player's own shifter setups (newest first), then built-ins.
    static List<ShifterProfile> All()
    {
        var all = new List<ShifterProfile>();
        if (Directory.Exists(Dir))
            foreach (var f in Directory.GetFiles(Dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<ShifterProfile>(File.ReadAllText(f), WheelProfile.Json);
                    if (p?.Device == null || p.Gears == null || p.Gears.Count == 0) continue;
                    p.Source = "wizard";
                    p.FilePath = f;
                    all.Add(p);
                }
                catch (Exception e) { Plugin.Warn($"skipping shifter file {Path.GetFileName(f)}: {e.Message}"); }
            }
        all.AddRange(BuiltIn());
        return all;
    }

    static DInput.Device Find(DeviceRef want) =>
        Devices.All.FirstOrDefault(d => !string.IsNullOrEmpty(want.UsbId) ? string.Equals(d.Info.UsbId, want.UsbId, StringComparison.OrdinalIgnoreCase) : d.Info.Name == want.Name);

    static void Choose()
    {
        ShifterProfile pick = null; DInput.Device dev = null;
        foreach (var p in All())
        {
            dev = Find(p.Device);
            if (dev != null) { pick = p; break; }
        }
        if (pick?.FilePath != Active?.FilePath || pick?.Name != Active?.Name || dev != _dev)
        {
            Plugin.Info(pick != null ? $"shifter: '{pick.Name}' ({pick.Source}, {pick.Describe()}) on '{dev.Info.Name}', waiting for the first gear" : "shifter: none");
            _armed = false;
        }
        Active = pick; _dev = pick != null ? dev : null;
    }

    public static string Save(ShifterProfile p)
    {
        Directory.CreateDirectory(Dir);
        p.Source = "wizard";
        p.ModVersion = Plugin.Version;
        string path = Path.Combine(Dir, Profiles.SafeFileName(p.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(p, WheelProfile.Json));
        p.FilePath = path;
        _generation = -1;   // re-choose on the next update
        return path;
    }

    // Removes the player's own setup for the active shifter (a built-in one can only be switched off in the settings).
    public static string RemoveActive()
    {
        if (Active == null) return "No shifter is set up.";
        if (Active.FilePath == null) return $"'{Active.Name}' is built into the mod. To ignore it, set [Shifter] Enabled = false in the settings file.";
        try { File.Delete(Active.FilePath); } catch (Exception e) { return "Couldn't remove it: " + e.Message; }
        Plugin.Info($"shifter setup removed: {Active.FilePath}");
        _generation = -1;
        return $"Shifter setup '{Active.Name}' removed.";
    }

    public static IEnumerable<string> Files() => Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.json") : Enumerable.Empty<string>();

    // --- every frame --------------------------------------------------------------------------------

    static bool _hinted;

    public static void Update(float now)
    {
        try
        {
            if (_generation != Devices.Generation) { _generation = Devices.Generation; Choose(); }
            Hint(now);
            int slot = 0;
            if (Ready)
                foreach (var kv in Active.Gears)
                    if (Buttons.HeldFor(_dev, kv.Value, now, Buttons.ReleaseDelay))
                    {
                        slot = kv.Key == "R" ? -1 : int.TryParse(kv.Key, out var n) ? n : 0;
                        break;
                    }
            if (slot != 0 && !_armed)
            {
                _armed = true;
                Plugin.Info($"shifter: first gear engaged, '{Active.Name}' takes over the gears");
            }
            RangeTick(now);
            if (slot != Slot)
            {
                Slot = slot;
                LastChange = now;
                string why = "";
                // AfterGrind StayInGear, clutch not pressed: the car keeps the gear it is in (_inGear) while the
                // lever is on its way. Build 3 sent neutral for the ~0.1 s the lever crossed neutral (he saw it drop
                // to N and come back), and lever 2 -> 1 (grinds, keeps 2) -> back to 2 counted as a new grind and
                // ended in neutral (JonkoMonk 2026-11-13).
                int carGear = _inGear;
                bool stay = GrindResult.Value == AfterGrind.StayInGear && Engaged && !ClutchLetsIn(out why);
                _grindHold = 0;
                _neutralHoldUntil = -1f;
                Grinding = false;
                if (slot == 0)
                {
                    // Lever in neutral: keep the gear for a moment; it goes to neutral if the lever stays there.
                    if (stay && carGear != 0) { _grindHold = carGear; _neutralHoldUntil = now + NeutralGrace; }
                }
                else if (stay && carGear == slot)
                    Plugin.Info($"shifter: lever back in {(slot < 0 ? "reverse" : $"gear {slot}")}, the gear it never left");
                else if (Engaged && !ClutchLetsIn(out why))
                {
                    Grinding = true;
                    if (stay) _grindHold = carGear;   // 0 after sitting in neutral: then it's neutral
                    Grind(now, why);
                }
            }
            else if (Grinding && ClutchLetsIn(out _))
            {
                Grinding = false;
                Plugin.Info($"shifter: clutch in ({Wheel.Clutch * 100f:0}%), the gear goes in");
            }
            else if (Slot == 0 && _grindHold != 0 && (now >= _neutralHoldUntil || ClutchLetsIn(out _)))
                _grindHold = 0;   // stayed in neutral (or the clutch went down): now really neutral
            int inGear = Grinding || Slot == 0 ? _grindHold : Slot;
            if (inGear == _inGear) return;
            _inGear = inGear;
            if (!Engaged) return;
            slot = inGear;
            if (Grinding) return;   // the grind already said so; the car is in neutral (or kept its old gear)
            int max = CarGears();
            if (slot != 0) Gear = GameGear(slot);
            _rangeWant = slot != 0 ? WantLow(slot) : null;
            _rangeTapped = false;
            string range = _rangeWant.HasValue ? (_rangeWant.Value ? " low" : " high") : "";
            Buttons.LastText = slot == 0 ? "Neutral" : slot < 0 ? "Reverse" + range :
                RangeMode ? $"Gear {Gear}{range}" :
                Gear != slot ? $"Gear {slot}  (this car has {max}: gear {Gear})" : $"Gear {slot}";
            Buttons.LastTime = now;
            Plugin.Info($"shifter: lever {(slot == 0 ? "N" : slot < 0 ? "R" : slot.ToString())} -> game gear {(slot == 0 ? $"{Gear} + clutch (neutral)" : Gear.ToString())}{range}");
            RangeTick(now);
        }
        catch (Exception e) { Plugin.ErrorOnce("shifter", e); }
    }

    // --- clutch required -----------------------------------------------------------------------------
    // Without the clutch pressed far enough a gear grinds and doesn't go in: the car stays in neutral (the lever
    // has left the old gear) until the clutch goes down while the lever is still in the slot, or the lever moves.
    // Taking a gear OUT never needs the clutch. Off while the engine is off (nothing spins, gears go in) and
    // until a clutch pedal has been pressed this session (players without one could never shift otherwise).

    // The lever sits in a gear that didn't go in.
    public static bool Grinding { get; private set; }
    static float _grindAt = -100f;
    // AfterGrind StayInGear: the gear the car keeps while the lever grinds or crosses neutral, and how long a
    // lever in neutral keeps it (longer = really neutral).
    static int _grindHold;
    static float _neutralHoldUntil = -1f;
    const float NeutralGrace = 0.6f;
    public static bool BuzzWhileHeld => GrindBuzzLength.Value > 1.05f;

    public static bool ClutchRequiredNow => ClutchRequired.Value && CarPhysics.ClutchSeen && CarPhysics.EngineRunning;

    static bool ClutchLetsIn(out string why)
    {
        why = "";
        if (!ClutchRequiredNow) return true;
        float need = Mathf.Clamp(ClutchToShift.Value, 0.1f, 1f);
        if (Wheel.Clutch >= need) return true;
        why = $"clutch {Wheel.Clutch * 100f:0}%, needs {need * 100f:0}%";
        return false;
    }

    static void Grind(float now, string why)
    {
        _grindAt = now;
        string stays = _grindHold == 0 ? "stays in neutral" : $"stays in {(_grindHold < 0 ? "reverse" : $"gear {_grindHold}")}";
        Plugin.Info($"shifter: lever {(Slot < 0 ? "R" : Slot.ToString())} grinds ({why}): {stays}");
        Buttons.LastText = "Grind! Press the clutch to shift"; Buttons.LastTime = now; Buttons.AlwaysShowUntil = now + 2f;
    }

    // Force feedback buzz for a grind (from Ffb, in steering-force units): a 25 Hz rattle, full for GrindBuzzLength
    // (0.3 s by default), then fading out over 0.3 s; above 1 s: as long as the gear grinds.
    public static float GrindForce(float now)
    {
        float t = now - _grindAt;
        if (t < 0f || !Grinding || GrindBuzz.Value <= 0f) return 0f;
        float full = BuzzWhileHeld ? float.MaxValue : Mathf.Clamp(GrindBuzzLength.Value, 0.1f, 1f);
        if (t > full + 0.3f) return 0f;
        float fade = t < full ? 1f : 1f - (t - full) / 0.3f;
        float square = Mathf.Sin(t * 2f * Mathf.PI * 25f) >= 0f ? 1f : -1f;
        return square * Mathf.Clamp01(GrindBuzz.Value) * 0.6f * fade;
    }

    // --- range as gears -----------------------------------------------------------------------------
    // Cars with only 2 forward gears and a high/low range: the H-pattern becomes a 4-speed, shortest first:
    // lever 1 = 1st low, 2 = 2nd low, 3 = 1st high, 4 = 2nd high (5-7 = 2nd high), R = reverse low.
    // The mod switches the range with the game's own Hi/Lo key, so it works like pressing it yourself (co-op too).

    static bool RangeMode => RangeAsGears.Value == RangeGears.Auto && CarGears() is 1 or 2;

    static bool? WantLow(int slot) => RangeMode ? slot < 0 || slot <= 2 : null;

    static bool? _rangeWant;
    static bool _rangeTapped;
    static float _rangeDeadline;
    static bool _rangeWaitLogged;

    static void RangeTick(float now)
    {
        if (_rangeWant == null || !Engaged) return;
        bool low;
        try { if (_std == null) return; low = _std.GearboxIsLowTransferCase; }
        catch { return; }   // car still spawning
        if (low == _rangeWant.Value) { _rangeWant = null; return; }
        if (!_rangeTapped)
        {
            if (!Buttons.TapAction("HiLo", now))
            {
                // The tap is a key press, which only reaches the game while its window is in front. The shifter is read
                // in the background, so a lever move with another window in front (the 2026-11-13 test log said
                // "no key for HiLo" although HiLo = Tab) now waits and switches once the game is back in front.
                if (Buttons.ActionHasKey("HiLo"))
                {
                    if (!_rangeWaitLogged) { _rangeWaitLogged = true; Plugin.Info("shifter: range switch waits until the game window is in front (it presses the HiLo key)"); }
                    return;
                }
                _rangeWant = null;
                Plugin.Warn("shifter: no key set for HiLo ([ButtonKeys] HiLo in the settings file is empty or unknown), can't switch range");
                Buttons.LastText = "Range: no key set for HiLo in the settings file"; Buttons.LastTime = now; Buttons.AlwaysShowUntil = now + 3f;
                return;
            }
            _rangeWaitLogged = false;
            _rangeTapped = true;
            _rangeDeadline = now + 1f;
            return;
        }
        if (now < _rangeDeadline) return;
        // The game didn't switch (maybe not allowed at this speed). Only tried again on the next lever move.
        Plugin.Info($"shifter: the game didn't switch to {(_rangeWant.Value ? "low" : "high")} range");
        Buttons.LastText = $"Couldn't switch to {(_rangeWant.Value ? "low" : "high")} range now"; Buttons.LastTime = now; Buttons.AlwaysShowUntil = now + 3f;
        _rangeWant = null;
    }

    // Once per session: a device that looks like a shifter but has no shifter setup yet.
    static void Hint(float now)
    {
        if (_hinted || Active != null || InputHooks.LocalVehicleInput == null) return;
        var d = Devices.All.FirstOrDefault(x => LooksLikeShifter(x.Info.Name));
        if (d == null) return;
        _hinted = true;
        Buttons.LastText = $"Shifter found ({d.Info.Name}): press {Settings.SetupKey.Value} > 7 to set it up";
        Buttons.LastTime = now; Buttons.AlwaysShowUntil = now + 8f;
        Plugin.Info("shifter-like device without a setup: " + d.Info.Name);
    }

    static bool LooksLikeShifter(string name)
    {
        name = name?.ToLowerInvariant() ?? "";
        return name.Contains("shift") || name.Contains("hgp") || name.Contains("th8") || name.Contains("gear");
    }

    // --- into the game's input ----------------------------------------------------------------------

    // From InputHooks.ApplyTo. Research (PLAN-shifter.md step 0): with the DirectInputDevice flag the game
    // takes SelectedManualGear as the gear (0 = no change, so neutral = hold the gear + clutch fully pressed).
    public static void Apply(ref NetworkInputData d)
    {
        if (!Engaged) return;
        d.InputFlags |= NetworkInputData.InputFlagsEnum.InputSource_DirectInputDevice;
        // Recomputed every tick, so getting into a car with fewer gears applies at once.
        if (_inGear != 0) Gear = GameGear(_inGear);
        if (Gear == 0) Gear = 1;
        // Neutral sends 0 ("no change"): the game then leaves the gearbox where CarPhysics put it (neutral).
        // Sending the held gear made the car flick back into it for a tick or two after leaving neutral, because
        // those older inputs were still being applied. Without CarPhysics (co-op guest) 0 keeps the last gear,
        // and the held clutch below makes that neutral.
        d.SelectedManualGear = _inGear == 0 ? 0 : Gear;
        if (_inGear == 0) d.Clutch = 1f;
    }

    // Lever slot -> game gear: reverse stays reverse; slots above the car's top gear give the top gear.
    // In range mode the slots repeat the car's gears (1 2 1 2) and the range does the rest.
    static int GameGear(int slot)
    {
        if (slot < 0) return -1;
        int max = CarGears();
        if (RangeMode) return slot > 4 ? max : (slot - 1) % max + 1;
        return max > 0 ? Math.Min(slot, max) : slot;
    }

    // Forward gears of the local car (0 = unknown).
    static VehicleInput _of;
    static FunselektorFusionVPStandardInput _std;
    static int _gears;

    static int CarGears()
    {
        var vi = InputHooks.LocalVehicleInput;
        if (vi == null) return 0;
        if (_of != vi)
        {
            _of = vi;
            _gears = 0;
            _std = null;
            try
            {
                _gears = vi.gameObject.GetComponent<VPVehicleController>()?.gearbox?.forwardGearRatios?.Length ?? 0;
                _std = vi.gameObject.GetComponent<FunselektorFusionVPStandardInput>();
            }
            catch { }
        }
        return _gears;
    }
}
