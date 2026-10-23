using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OthWheel;

// One wheel setup: which device/axis is steering, pedals, which buttons do what, force feedback
// direction and strength. Saved as JSON:
//  - built-in presets ship inside the mod (Presets\*.json, embedded), matched by USB id
//  - extra presets can be dropped into <data folder>\presets\ (e.g. one a player shared)
//  - setups made with the wizard are saved in <data folder>\setups\
//    (data folder: BepInEx\config\OthWheel\ or UserData\OthWheel\, see ModPaths)
// Device references are by USB id (vendor:product) + name, so a setup survives a different USB port
// and can be shared with other players.
public sealed class WheelProfile
{
    public int Format { get; set; } = 1;
    public string Name { get; set; } = "";
    // "builtin", "preset" (file in presets\) or "wizard"; not saved for built-ins' copies.
    public string Source { get; set; } = "wizard";
    public string ModVersion { get; set; } = Plugin.Version;
    public List<DeviceRef> Devices { get; set; } = new();
    public AxisRef Steering { get; set; }
    // Rotation set in the wheel's driver software (lock to lock, degrees).
    public int RotationDegrees { get; set; } = 540;
    public PedalRef Throttle { get; set; }
    public PedalRef Brake { get; set; }
    public PedalRef Clutch { get; set; }
    public FfbRef Ffb { get; set; } = new();
    // Action name -> button. Missing = not assigned.
    public Dictionary<string, ButtonRef> Buttons { get; set; } = new();
    // Printed names on the wheel, "device:button" -> "BOX". Optional, for display and the wizard.
    public Dictionary<string, string> Labels { get; set; } = new();
    public string Notes { get; set; } = "";

    [JsonIgnore] public string FilePath;

    public string Label(ButtonRef b)
    {
        if (b == null) return "";
        if (Labels != null && Labels.TryGetValue($"{b.Device}:{b.Button}", out var l)) return l;
        return ButtonRef.Describe(b.Button) + (Devices.Count > 1 && b.Device < Devices.Count ? $" ({Devices[b.Device].Name})" : "");
    }

    public WheelProfile Clone() => JsonSerializer.Deserialize<WheelProfile>(JsonSerializer.Serialize(this, Json), Json);

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class DeviceRef
{
    public string Name { get; set; } = "";
    // "346E:0005" (vendor:product, hex)
    public string UsbId { get; set; } = "";
}

public sealed class AxisRef
{
    public int Device { get; set; }
    public string Axis { get; set; } = "X";
    // Raw values (0..65535) at full left, straight ahead, full right.
    public int Left { get; set; }
    public int Center { get; set; } = 32767;
    public int Right { get; set; } = 65535;
}

public sealed class PedalRef
{
    public int Device { get; set; }
    public string Axis { get; set; } = "Z";
    // Raw values (0..65535) when released and when fully pressed.
    public int Released { get; set; }
    public int Pressed { get; set; } = 65535;
}

public sealed class FfbRef
{
    public int Device { get; set; }
    // DirectInput force = steer-space force (positive = towards the right) * Direction.
    // 0 = not tested yet: force feedback stays off until the wizard's test has run.
    public int Direction { get; set; }
    // Multiplies the profile strength, so wheels of different power feel alike (1 = Moza R3).
    public float Strength { get; set; } = 1f;
}

public sealed class ButtonRef
{
    public int Device { get; set; }
    // 0-127 = button; 128 + hat*4 + (0 up, 1 right, 2 down, 3 left) = POV hat direction.
    public int Button { get; set; }

    public const int PovBase = 128;
    static readonly string[] PovDirs = { "up", "right", "down", "left" };

    public static string Describe(int button) =>
        button < PovBase ? $"button {button}" : $"hat {(button - PovBase) / 4 + 1} {PovDirs[(button - PovBase) % 4]}";

    public bool Same(ButtonRef o) => o != null && o.Device == Device && o.Button == Button;
}

internal static class Profiles
{
    public static string Root => ModPaths.DataDir;
    public static string SetupsDir => Path.Combine(Root, "setups");
    public static string PresetsDir => Path.Combine(Root, "presets");

    static List<WheelProfile> _builtin;

    // Built-in shifter presets (PresetsShifters) are embedded too, but aren't wheel setups.
    public static bool IsShifterResource(string name) =>
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.IndexOf(".Shifters.", StringComparison.OrdinalIgnoreCase) >= 0;

    public static int AxisIndex(string name)
    {
        int i = Array.FindIndex(DInput.AxisNames, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i;
    }

    public static List<WheelProfile> BuiltIn()
    {
        if (_builtin != null) return _builtin;
        _builtin = new List<WheelProfile>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !IsShifterResource(n)))
        {
            try
            {
                using var s = asm.GetManifestResourceStream(res);
                var p = JsonSerializer.Deserialize<WheelProfile>(s, WheelProfile.Json);
                if (p == null || p.Devices == null || p.Devices.Count == 0) continue;
                p.Source = "builtin";
                p.FilePath = null;
                _builtin.Add(p);
            }
            catch (Exception e) { Plugin.Error($"built-in preset {res}: {e.Message}"); }
        }
        return _builtin;
    }

    static List<WheelProfile> LoadDir(string dir, string source)
    {
        var list = new List<WheelProfile>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var p = JsonSerializer.Deserialize<WheelProfile>(File.ReadAllText(f), WheelProfile.Json);
                if (p == null || p.Devices == null || p.Devices.Count == 0) continue;
                p.Source = source;
                p.FilePath = f;
                list.Add(p);
            }
            catch (Exception e) { Plugin.Warn($"skipping setup file {Path.GetFileName(f)}: {e.Message}"); }
        }
        return list;
    }

    // In priority order: the player's own setups (newest first), dropped-in presets, built-ins.
    public static List<WheelProfile> All()
    {
        var all = LoadDir(SetupsDir, "wizard");
        all.AddRange(LoadDir(PresetsDir, "preset"));
        all.AddRange(BuiltIn());
        return all;
    }

    // Maps the profile's devices to connected ones (by USB id, then name; identical devices in order).
    // Null if a device is missing.
    public static int[] Match(WheelProfile p, IReadOnlyList<DInput.DeviceInfo> connected)
    {
        var used = new HashSet<int>();
        var map = new int[p.Devices.Count];
        for (int i = 0; i < p.Devices.Count; i++)
        {
            var want = p.Devices[i];
            int found = -1;
            for (int k = 0; k < connected.Count && found < 0; k++)
                if (!used.Contains(k) && string.Equals(connected[k].UsbId, want.UsbId, StringComparison.OrdinalIgnoreCase)) found = k;
            if (found < 0 && string.IsNullOrEmpty(want.UsbId))
                for (int k = 0; k < connected.Count && found < 0; k++)
                    if (!used.Contains(k) && connected[k].Name == want.Name) found = k;
            if (found < 0) return null;
            used.Add(found);
            map[i] = found;
        }
        return map;
    }

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "wheel").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "wheel" : s;
    }

    public static string Save(WheelProfile p)
    {
        Directory.CreateDirectory(SetupsDir);
        p.Source = "wizard";
        p.ModVersion = Plugin.Version;
        string path = p.FilePath != null && p.FilePath.StartsWith(SetupsDir, StringComparison.OrdinalIgnoreCase)
            ? p.FilePath
            : Path.Combine(SetupsDir, SafeFileName(p.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(p, WheelProfile.Json));
        p.FilePath = path;
        return path;
    }

    public static string ToJson(WheelProfile p) => JsonSerializer.Serialize(p, WheelProfile.Json);
}
