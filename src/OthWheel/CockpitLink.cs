namespace OthWheel;

// For other mods: what the real wheel, pedals and camera buttons do right now. The OTH Cockpit mod
// reads (and writes the "cockpit" fields of) these plain static fields by reflection, so neither mod
// needs a reference to the other. Keep the names and types stable; bump Version on a breaking change.
public static class CockpitLink
{
    public const int Version = 2;

    // Written by this mod, read by the cockpit.
    public static bool Active;              // a wheel is connected, set up, and the mod is enabled
    public static float WheelDegrees;       // real wheel angle from centre, degrees, + = right
    public static float RotationDegrees;    // lock to lock of the real wheel (its setup)
    public static float Throttle, Brake, Clutch;   // 0..1, 1 = fully pressed
    public static float LookX, LookY;       // camera buttons held: -1..1, + = look right / look up (not inverted)
    public static int CockpitToggles;       // counts presses of the "cockpit view" button

    // Written by the cockpit mod.
    public static bool CockpitInstalled;    // the cockpit mod is running
    public static bool CockpitActive;       // cockpit view is on: the camera buttons look around in it
                                            // instead of moving the chase camera

    // Called every frame after Wheel.Update.
    internal static void Update()
    {
        var p = Devices.Active;
        Active = Wheel.Connected && p != null && Settings.Enabled.Value;
        RotationDegrees = p?.RotationDegrees ?? 0;
        WheelDegrees = Active ? Wheel.Physical * RotationDegrees / 2f : 0f;
        Throttle = Active ? Wheel.Throttle : 0f;
        Brake = Active ? Wheel.Brake : 0f;
        Clutch = Active ? Wheel.Clutch : 0f;
    }

    // The "CockpitView" wheel button.
    internal static string RequestToggle()
    {
        CockpitToggles++;
        return CockpitInstalled ? "Cockpit view on / off" : "Cockpit view: the OTH Cockpit mod isn't installed";
    }
}
