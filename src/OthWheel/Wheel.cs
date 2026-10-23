using System;

namespace OthWheel;

// Turns the active setup's raw axes into game values.
//
// Software lock: the game feels right with 540 degrees lock to lock (Steering.LockDegrees). A wheel
// set to more in its driver (e.g. 900) reaches full steering at 540 and the force feedback lock stop
// sits there too; the rest of the rotation is "past the stop".
internal static class Wheel
{
    public static bool Connected { get; private set; }

    // Game ranges: steer -1..1 (negative = left), pedals 0..1.
    public static float Steer, Throttle, Brake, Clutch;
    // Wheel position relative to the steering lock: ±1 = full lock (can go beyond with a software lock).
    // No gain/deadzone; used by force feedback.
    public static float Position;
    // Wheel position over its whole rotation: -1 = left end, +1 = right end (no software lock). For CockpitLink.
    public static float Physical;
    public static int RawSteer;

    public static void Update()
    {
        var p = Devices.Active;
        var steerDev = p?.Steering != null ? Devices.Get(p.Steering.Device) : null;
        Connected = steerDev != null;
        if (!Connected) { Steer = Throttle = Brake = Clutch = 0f; Position = Physical = 0f; return; }

        int axis = Profiles.AxisIndex(p.Steering.Axis);
        RawSteer = axis >= 0 ? steerDev.Axis[axis] : DInput.RawMax / 2;
        float physical = SteerPosition(p.Steering, RawSteer - Settings.SteerCenterOffset.Value);
        if (Settings.SteerInvert.Value) physical = -physical;
        Physical = physical;
        Position = physical * LockScale(p);
        Steer = MapSteer(Position);

        Throttle = Pedal(p.Throttle, Settings.ThrottleDeadzone.Value, Settings.ThrottleFullAt.Value);
        Brake = Pedal(p.Brake, Settings.BrakeDeadzone.Value, Settings.BrakeFullAt.Value);
        Clutch = Pedal(p.Clutch, Settings.ClutchDeadzone.Value, Settings.ClutchFullAt.Value);
    }

    // Driver rotation / game lock, never below 1 (a wheel set to less than 540 just uses its full range).
    public static float LockScale(WheelProfile p)
    {
        float lockDeg = Math.Clamp(Settings.LockDegrees.Value, 90f, 3600f);
        return p.RotationDegrees > lockDeg ? p.RotationDegrees / lockDeg : 1f;
    }

    // Raw axis value -> -1 (left end) .. +1 (right end), separately scaled on each side of center.
    public static float SteerPosition(AxisRef s, int raw)
    {
        float t = raw - s.Center;
        float right = s.Right - s.Center, left = s.Left - s.Center;
        if (t == 0f) return 0f;
        if (right != 0f && Math.Sign(t) == Math.Sign(right)) return Math.Clamp(t / right, 0f, 1.05f);
        if (left != 0f) return -Math.Clamp(t / left, 0f, 1.05f);
        return 0f;
    }

    static float MapSteer(float v)
    {
        v = Math.Clamp(v, -1f, 1f);
        float dz = Math.Clamp(Settings.SteerDeadzone.Value, 0f, 0.5f);
        float mag = Math.Abs(v);
        mag = mag <= dz ? 0f : (mag - dz) / (1f - dz);

        float lin = Math.Max(0.1f, Settings.SteerLinearity.Value);
        if (lin != 1f) mag = MathF.Pow(mag, lin);

        return Math.Clamp(MathF.CopySign(mag, v) * Settings.SteerGain.Value, -1f, 1f);
    }

    public static float PedalRaw(PedalRef r, int raw)
    {
        float span = r.Pressed - r.Released;
        return span == 0f ? 0f : Math.Clamp((raw - r.Released) / span, 0f, 1f);
    }

    static float Pedal(PedalRef r, float deadzone, float fullAt)
    {
        if (r == null) return 0f;
        var d = Devices.Get(r.Device);
        int axis = Profiles.AxisIndex(r.Axis);
        if (d == null || axis < 0) return 0f;
        float v = PedalRaw(r, d.Axis[axis]);
        float lo = Math.Clamp(deadzone, 0f, 0.5f);
        float hi = Math.Clamp(fullAt, lo + 0.05f, 1f);
        return Math.Clamp((v - lo) / (hi - lo), 0f, 1f);
    }
}
