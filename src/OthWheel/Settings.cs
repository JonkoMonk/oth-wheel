using System;
using System.IO;

namespace OthWheel;

// Everything the player can tune lives in jonkomonk.oth.wheel.cfg (folder: see ModPaths).
// The file is re-read automatically while the game runs, so edits apply within ~2 seconds.
internal static class Settings
{
    static ConfigFile _cfg;
    static DateTime _lastWrite;

    public static ConfigEntry<bool> Enabled;

    public static ConfigEntry<bool> SteerInvert;
    public static ConfigEntry<float> SteerGain, SteerDeadzone, SteerLinearity;
    public static ConfigEntry<int> SteerCenterOffset;

    public static ConfigEntry<float> LockDegrees;
    public static ConfigEntry<float> ThrottleDeadzone, BrakeDeadzone, ThrottleFullAt, BrakeFullAt, ClutchDeadzone, ClutchFullAt;
    public static ConfigEntry<string> SetupKey;

    public static ConfigEntry<float> LogInterval;

    public static void Bind(ConfigFile cfg)
    {
        _cfg = cfg;

        Enabled = cfg.Bind("General", "Enabled", true,
            "Master switch. false = the wheel does nothing and the game behaves as without the mod.");

        SteerGain = cfg.Bind("Steering", "Gain", 1.0f,
            "1.0 = turning the wheel to LockDegrees gives full steering lock in the car. " +
            "Higher = more steering for the same wheel turn.");
        SteerInvert = cfg.Bind("Steering", "Invert", false, "Swap left and right.");
        SteerDeadzone = cfg.Bind("Steering", "Deadzone", 0.0f,
            "Small zone around center that counts as straight (0.01 = 1% of half the wheel range). " +
            "Inside it, keyboard/controller steering still works.");
        SteerLinearity = cfg.Bind("Steering", "Linearity", 1.0f,
            "1.0 = linear. Above 1 = calmer around center, faster near full lock.");
        SteerCenterOffset = cfg.Bind("Steering", "CenterOffset", 0,
            "Raw correction if the car pulls to one side with the wheel centered (raw units, full range is 0..65535).");

        LockDegrees = cfg.Bind("Steering", "LockDegrees", 540f,
            "Wheel rotation (lock to lock, degrees) that gives full steering lock in the car. If your wheel's driver is set " +
            "to more (e.g. 900), full lock and the force feedback end stop are reached at this angle (software lock).");
        // Which device/axis is steering and which are the pedals lives in the wheel setup (setup wizard), not here.
        ThrottleDeadzone = cfg.Bind("Pedals", "ThrottleDeadzone", 0.03f, "Pedal travel ignored at the top (0.03 = first 3%).");
        BrakeDeadzone = cfg.Bind("Pedals", "BrakeDeadzone", 0.03f, "Pedal travel ignored at the top (0.03 = first 3%).");
        ThrottleFullAt = cfg.Bind("Pedals", "ThrottleFullAt", 0.97f, "Pedal position that already counts as 100%.");
        BrakeFullAt = cfg.Bind("Pedals", "BrakeFullAt", 0.97f, "Pedal position that already counts as 100%.");
        ClutchDeadzone = cfg.Bind("Pedals", "ClutchDeadzone", 0.03f, "Pedal travel ignored at the top (0.03 = first 3%).");
        ClutchFullAt = cfg.Bind("Pedals", "ClutchFullAt", 0.97f, "Pedal position that already counts as 100% (fully pressed).");

        SetupKey = cfg.Bind("General", "SetupKey", "F3", "Keyboard key that opens the wheel setup wizard / menu. " +
            "(Not F7: the 'First Person and Drone' mod uses F6 and F7.)");
        // One-time move of the old default F7 to F3 for existing installs (F7 = drone in First Person and Drone).
        var keyDefaults = cfg.Bind("Debug", "KeyDefaultsVersion", 0, "Internal, don't change.");
        if (keyDefaults.Value < 1)
        {
            if (string.Equals(SetupKey.Value?.Trim(), "F7", StringComparison.OrdinalIgnoreCase)) SetupKey.Value = "F3";
            keyDefaults.Value = 1;
            cfg.Save();
        }

        LogInterval = cfg.Bind("Debug", "LogIntervalSeconds", 1.0f, "How often wheel values are written to the mod loader's log (0 = never).");

        Ffb.Bind(cfg);
        Buttons.Bind(cfg);
        Shifter.Bind(cfg);
        CarPhysics.Bind(cfg);
        GearProbe.Bind(cfg);

        _lastWrite = File.Exists(cfg.ConfigFilePath) ? File.GetLastWriteTimeUtc(cfg.ConfigFilePath) : DateTime.MinValue;
    }

    // After the mod itself saved the file (settings menu): not a change from outside, so no reload.
    public static void MarkSaved()
    {
        if (File.Exists(_cfg.ConfigFilePath)) _lastWrite = File.GetLastWriteTimeUtc(_cfg.ConfigFilePath);
    }

    // Returns true if the file changed on disk and was reloaded.
    public static bool ReloadIfChanged()
    {
        if (!File.Exists(_cfg.ConfigFilePath)) return false;
        var t = File.GetLastWriteTimeUtc(_cfg.ConfigFilePath);
        if (t == _lastWrite) return false;
        _lastWrite = t;
        _cfg.Reload();
        Buttons.Reparse();
        Ffb.ApplyLayout();
        return true;
    }
}
