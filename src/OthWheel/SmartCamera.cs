using System;
using UnityEngine;
using UnityEngine.InputSystem;
#if MELON
using Il2Cpp;   // MelonLoader puts the game's namespace-less classes here
using Il2CppCinemachine;
using Il2CppVehiclePhysics;
#else
using Cinemachine;
using VehiclePhysics;
#endif

namespace OthWheel;

// Smart third-person camera for wilderness driving (plan: PLAN-camera.md). With a wheel in your hands
// you can't keep turning the camera, so it chooses a useful view by itself from the terrain around the car:
//  - off-road three-quarter view: a bit to the side and above the car at crawling speed, closer to behind
//    when going fast; it stays on one side until the ground leans the other way;
//  - steep slopes: further round to the side; downhill also higher and a little further back;
//  - on the side of a mountain: round to the downhill side looking back up at the car (or the uphill side,
//    [SmartCamera] SideSlope), and higher;
//  - downhill / a crest ahead: higher, so you look over the car at the ground ahead (uphill it stays low);
//  - reversing: it does NOT swing round; it rises a little so you see the ground behind the car;
//  - something between the car and the camera (trees, rocks): it zooms in until the view is clear.
// Your own camera moves (mouse, right stick, camera buttons) always win; it eases back after ReturnAfter s.
// The camera buttons (D-pad) turn the game camera directly, speeding up while held.
// It sets the game's chase camera (CinemachineFreeLook) heading, height and zoom right before the camera
// computes its position each frame, so the game's own terrain/obstacle avoidance still works on top.
internal static class SmartCamera
{
    public enum Side { Downhill, Uphill, Off }

    public static ConfigEntry<bool> Enabled, Log;
    public static ConfigEntry<float> Strength, Speed, ReturnAfter, DpadSpeed, DpadMaxSpeed, SideAngle, Height, MaxHeight;
    public static ConfigEntry<Side> SideSlope;
    public static ConfigEntry<bool> Reverse, ZoomInWhenBlocked, SideReturn, ReverseKeepsYourTurn, ReverseRemembers;
    public static ConfigEntry<float> Distance;

    public static void Bind(ConfigFile cfg)
    {
        const string s = "SmartCamera";
        Enabled = cfg.Bind(s, "Enabled", true,
            "Third-person camera that picks a good view by itself from the terrain (slopes, mountain sides, reversing). " +
            "false = the game's own camera (the camera buttons then work as before).");
        Strength = cfg.Bind(s, "Strength", 1.0f, "How far it moves the camera for slopes and reversing, 0..1.5 (0 = only stays behind you).");
        Speed = cfg.Bind(s, "Speed", 1.0f, "How quickly it moves to a new view (1 = about 1.5 s, 2 = twice as fast).");
        ReturnAfter = cfg.Bind(s, "ReturnAfter", 2.5f, "Seconds after you last moved the camera yourself before it takes over again.");
        SideAngle = cfg.Bind(s, "SideAngle", 25f,
            "Off-road view: how far round to the side of the car the camera sits at crawling speed, degrees (0 = straight behind). " +
            "Slopes add to it; at higher speed it moves closer to behind.");
        Height = cfg.Bind(s, "Height", 0.15f,
            "Camera height on the flat: 0 = the lowest the game allows, 1 = the highest (straight down from above). Slopes add to it. " +
            "Raising or lowering the camera with the camera buttons changes it until the game restarts.");
        MaxHeight = cfg.Bind(s, "MaxHeight", 0.8f, "The camera never goes higher than this (1 = straight down from above).");
        SideSlope = cfg.Bind(s, "SideSlope", Side.Downhill,
            "Driving along the side of a mountain: Downhill = the camera goes round to the downhill side, looking back up at the car; " +
            "Uphill = it goes to the uphill side, looking over the car down the slope; Off = stays behind.");
        Reverse = cfg.Bind(s, "Reverse", true, "Reversing: the camera rises a little so you see the ground behind the car (it never swings round).");
        ReverseKeepsYourTurn = cfg.Bind(s, "ReverseKeepsYourTurn", true,
            "Reversing: when you turn the camera yourself, it stays that way until you drive forward again (to look forward or back).");
        ReverseRemembers = cfg.Bind(s, "ReverseRemembers", true,
            "With ReverseKeepsYourTurn: the next time you reverse, the camera goes back to the view you chose last time you reversed. " +
            "false = every reverse starts with the normal view.");
        ZoomInWhenBlocked = cfg.Bind(s, "ZoomInWhenBlocked", true,
            "When trees, rocks or buildings come between the car and the camera, it zooms in until you can see the car again.");
        SideReturn = cfg.Bind(s, "SideReturn", true,
            "After a mountainside moved the camera to the other side: back to your side (the camera-side button) once the ground " +
            "is flat again for 6 s. false = it stays where the slope put it.");
        Distance = cfg.Bind(s, "Distance", 1.0f,
            "Camera distance: 0.5 = the closest the game allows, 1 = the game's normal, 2 = the farthest. It comes a little closer " +
            "when crawling and among trees, and goes further back when fast or going downhill. Zooming yourself changes it until the game restarts.");
        DpadSpeed = cfg.Bind(s, "DpadSpeed", 60f, "Camera buttons: turning speed when you start holding one, degrees per second.");
        DpadMaxSpeed = cfg.Bind(s, "DpadMaxSpeed", 240f, "Camera buttons: top turning speed after holding for a second.");
        Log = cfg.Bind(s, "Log", true, "Write what the smart camera sees and does to the log once a second (for tuning).");
    }

    // Camera buttons this frame (x right, y down; Buttons already applied CameraInvertX/Y).
    public static Vector2 Dpad;
    // While true the camera buttons turn the camera here instead of simulating the mouse.
    public static bool HandlesDpad => Enabled.Value;

    static ExtraCameraControls _controls;
    public static void SetControls(ExtraCameraControls c) => _controls = c;

    public static void Install(HarmonyLib.Harmony h)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(CinemachineFreeLook), "InternalUpdateCameraState");
            if (m == null) { Plugin.Warn("not found: CinemachineFreeLook.InternalUpdateCameraState, smart camera off"); return; }
            h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(SmartCamera), nameof(BeforeCameraUpdate)));
            Plugin.Info("hooked CinemachineFreeLook.InternalUpdateCameraState (smart camera)");
        }
        catch (Exception e) { Plugin.Error($"hook failed CinemachineFreeLook.InternalUpdateCameraState: {e.GetType().Name}: {e.Message}"); }
    }

    static VPVehicleController _car;
    static VehicleInput _carOf;
    static float _idle = 99f, _engage, _dpadHeld, _appliedZoom, _nextLog, _lastFrameTime;
    static float _lift, _zoom;   // smoothed targets
    static int _lastFrame = -1;
    static bool _loggedSetup;
    static string _seen = "";

    public static void BeforeCameraUpdate(CinemachineFreeLook __instance, float deltaTime)
    {
        if (!Enabled.Value) { ResetState(); return; }
        try
        {
            if (_controls == null || __instance.Pointer != _controls.freeLookCamera?.Pointer) return;
            int frame = Time.frameCount;
            if (frame == _lastFrame) return;   // once per frame
            _lastFrame = frame;
            float now = Time.unscaledTime;
            float dt = Mathf.Clamp(now - _lastFrameTime, 0f, 0.1f);
            _lastFrameTime = now;

            // The menu's Camera page keeps it running, so its settings can be tried out while looking.
            bool driving = now - InputHooks.LastTickTime < 0.5f && !Cursor.visible && !CockpitLink.CockpitActive &&
                           (!Menu.IsOpen || Menu.OnCameraPage);
            var car = Car();
            var rb = car != null ? car.cachedRigidbody : null;
            var main = Camera.main;
            if (!driving || rb == null || main == null) { Unwind(__instance, dt); _idle = 99f; return; }
            if (!_loggedSetup) LogSetup(__instance);

            var x = __instance.m_XAxis;
            var y = __instance.m_YAxis;

            // Your own camera moves pause the heading control.
            bool own = PlayerMovedCamera();
            if (Dpad != Vector2.zero)
            {
                _dpadHeld += dt;
                float spd = Mathf.Lerp(DpadSpeed.Value, DpadMaxSpeed.Value, Mathf.Clamp01(_dpadHeld));
                x.Value = Wrap(x.Value - Dpad.x * spd * dt, x);
                y.Value = Mathf.Clamp(y.Value + Dpad.y * spd / 300f * dt, Mathf.Min(y.m_MinValue, y.m_MaxValue), Mathf.Max(y.m_MinValue, y.m_MaxValue));
                own = true;
            }
            else _dpadHeld = 0f;
            _idle = own ? 0f : _idle + dt;

            // ---- what the terrain looks like ----
            Vector3 pos = rb.position, vel = rb.velocity;
            Vector3 carFwd = Flat(car.transform.forward);
            float fwdSpeed = Vector3.Dot(vel, carFwd);
            bool reversing = fwdSpeed < -0.8f;
            Vector3 travel = !reversing && Flat(vel).magnitude > 1.5f ? Flat(vel).normalized : carFwd;
            Vector3 right = Vector3.Cross(Vector3.up, travel);
            float h0 = Ground(pos);
            float slope = (Ground(pos + travel * 8f) - Ground(pos - travel * 8f)) / 16f;          // + = uphill
            float near = Ground(pos + travel * 6f), far = Ground(pos + travel * 16f);
            float crest = Mathf.Max(0f, (h0 + (near - h0) / 6f * 16f - far) / 16f);              // ground ahead falls away
            float side = (Ground(pos + right * 6f) - Ground(pos - right * 6f)) / 12f;            // + = right side higher
            float drop = Mathf.Max(h0 - Ground(pos + right * 12f), h0 - Ground(pos - right * 12f));

            // ---- where the camera should be ----
            // The chase camera is bound to the car (LockToTargetWithWorldUp): its heading axis is relative to
            // the car, 0 = straight behind, + = round towards the car's left. So we aim it directly.
            float str = Mathf.Clamp(Strength.Value, 0f, 1.5f);
            float kmh = Flat(vel).magnitude * 3.6f;
            float steep = Mathf.Clamp01((Mathf.Abs(slope) - 0.05f) / 0.25f);      // 0 flat .. 1 at 30%
            float sideSteep = Mathf.Clamp01((Mathf.Abs(side) - 0.06f) / 0.30f);
            // Which side: the downhill (or uphill) side when the ground leans; on the flat, stay where it is.
            // It only changes sides when the ground clearly leans the other way for 1.5 s (straight down a
            // hill the side slope flickers around zero, and the camera swapped sides back and forth).
            // On rolling ground it still swapped every few seconds: slope side, your side, slope side
            // (2026-10-27). So: a clearer lean for longer, a longer flat stretch before going back, and at least
            // 10 s on a side before any automatic swap.
            _sinceSwap += dt;
            if (SideSlope.Value != Side.Off && Mathf.Abs(side) > 0.15f)
            {
                bool rightLower = side < 0f;
                bool toRight = SideSlope.Value == Side.Downhill ? rightLower : !rightLower;
                float want = toRight ? -1f : 1f;
                _otherSideFor = want != _sideSign ? _otherSideFor + dt : 0f;
                if (_otherSideFor > 3f && _sinceSwap > 10f) { _sideSign = want; _otherSideFor = 0f; _sinceSwap = 0f; }
                _flatFor = 0f;
            }
            else _otherSideFor = Mathf.Max(0f, _otherSideFor - dt);
            // Flat again for 6 s: back to your side (the camera-side button).
            if (Mathf.Abs(side) < 0.06f) _flatFor += dt; else if (Mathf.Abs(side) > 0.09f) _flatFor = 0f;
            if (SideReturn.Value && _flatFor > 6f && _sinceSwap > 10f && _sideSign != _mySide) { _sideSign = _mySide; _sinceSwap = 0f; }
            // Off-road three-quarter view at crawling speed, closer to straight behind when going fast.
            float slowness = 1f - Mathf.Clamp01((kmh - 15f) / 35f);
            float angle = SideAngle.Value * (0.35f + 0.65f * slowness) + 25f * steep
                        + (SideSlope.Value != Side.Off ? 30f * sideSteep : 0f);
            float wantX = reversing ? 0.5f * _sideSign * angle : _sideSign * Mathf.Clamp(angle * str, 0f, 60f);
            // Reversing: a turn of your own is kept until you drive forward again (stopping in between keeps it).
            if (fwdSpeed > 0.8f && _revHold && !ReverseGear(car))
            {
                _revHold = false;
                if (ReverseRemembers.Value) _revLastX = x.Value;
                Plugin.Info("smart cam: driving forward, your reversing view is let go");
            }
            // In reverse = rolling backwards, or reverse gear in (the turn is often made before the car moves:
            // 2026-11-07 he turned to the front with R in at standstill, and 2.5 s later it swung back).
            bool inReverse = reversing || (ReverseGear(car) && fwdSpeed < 0.5f);
            if (inReverse && own && ReverseKeepsYourTurn.Value && !_revHold) { _revHold = true; Plugin.Info("smart cam: reversing, keeping your camera turn"); }
            if (!ReverseKeepsYourTurn.Value) _revHold = false;
            if (inReverse && !_revHold && ReverseKeepsYourTurn.Value && ReverseRemembers.Value && !float.IsNaN(_revLastX)) wantX = _revLastX;
            // Downhill: higher, to look over the car at the ground ahead. Uphill: hardly higher (from above you
            // only saw the car roof, a top-down view); you look up the hill at the crest instead, which raises it.
            float down = slope < 0f ? steep : 0f, up = slope > 0f ? steep : 0f;
            float wantLift = str * (0.35f * down + 0.05f * up + 0.25f * Mathf.Clamp01(crest / 0.2f) + 0.10f * sideSteep);
            float wantZoom = str * (0.15f * down + 0.08f * up);
            // Reversing: only a little higher and further back (the slope/crest terms look ahead of the car's
            // nose, the wrong way). Lift 0.30 + zoom 0.35 ended up nearly top-down and too far to see where you
            // were going (JonkoMonk, 2026-10-25).
            if (reversing)
            {
                wantLift = Reverse.Value ? str * (0.12f + 0.10f * sideSteep) : 0f;
                wantZoom = Reverse.Value ? str * 0.08f : 0f;
            }
            // Distance by situation: a little closer when crawling or among trees, further back when fast.
            Trees.Refresh(pos);
            int treesAround = Trees.CountNear(pos, 12f);
            if (!reversing)
                wantZoom += str * (0.12f * Mathf.Clamp01((kmh - 30f) / 40f) - 0.05f * Mathf.Clamp01((10f - kmh) / 6f)
                                   - 0.10f * Mathf.Clamp01((treesAround - 2) / 4f));
            wantLift = Mathf.Clamp(wantLift, 0f, 0.7f);
            wantZoom = Mathf.Clamp(wantZoom, -0.2f, 0.5f);

            // Standing still: leave the camera where it is.
            bool moving = Flat(vel).magnitude > 1.0f;
            float k = 1f - Mathf.Exp(-dt * Mathf.Max(0.1f, Speed.Value) / 1.2f);
            if (moving)
            {
                // Up quickly, down slowly: after a climb the camera stayed too low on the flat at the top.
                float kDown = 1f - Mathf.Exp(-dt * Mathf.Max(0.1f, Speed.Value) / 4f);
                _lift += (wantLift - _lift) * (wantLift > _lift ? k : kDown);
                _zoom += (wantZoom - _zoom) * (wantZoom > _zoom ? k : kDown);
            }

            // Heading: only after you've left the camera alone for ReturnAfter s, easing in over a second.
            // Also standing still right after the side button, and on the menu's Camera page (to try settings).
            float err = Mathf.DeltaAngle(x.Value, wantX);
            bool turnNow = now < _turnUntil || Menu.OnCameraPage;
            bool holdYours = _revHold && !turnNow;
            if (_idle > ReturnAfter.Value && (moving || turnNow) && !holdYours)
            {
                _engage = Mathf.Min(1f, _engage + dt);
                x.Value = Wrap(x.Value + err * k * _engage, x);
            }
            else _engage = 0f;

            // Height: our own (Height + the slope extra), not added to the game's: the game starts its camera at
            // mid height and the extra on top of that went straight to the top-down view. When you raise or
            // lower the camera yourself, that becomes the new flat height.
            float yLo = Mathf.Min(y.m_MinValue, y.m_MaxValue), yHi = Mathf.Max(y.m_MinValue, y.m_MaxValue);
            if (float.IsNaN(_baseY) || _heightOf != Height.Value) { _baseY = Height.Value; _heightOf = Height.Value; }
            if (own) _ownY = true;
            else
            {
                if (_ownY) { _ownY = false; _baseY = Mathf.Clamp(y.Value - _lift, 0f, 1f); }
                float wantY = Mathf.Clamp(_baseY + _lift, yLo, Mathf.Min(yHi, MaxHeight.Value));
                y.Value += (wantY - y.Value) * k;
            }
            // Something between the car and the camera (trees, rocks, buildings): zoom in until the view is
            // clear, and back out a second after it's clear again.
            string what = "";
            bool blocked = false;
            if (ZoomInWhenBlocked.Value && !_blockCheckBroken)
            {
                // Its own try: if it fails, the rest of the camera (heading, height, D-pad) keeps working.
                try { blocked = BlockedView(main.transform.position, car.transform, rb, out what); }
                catch (Exception e) { _blockCheckBroken = true; Plugin.ErrorOnce("smart camera view check (switched off)", e); }
            }
            if (blocked)
            {
                _clearFor = 0f;
                if (what != _blockedBy) { _blockedBy = what; _seen += $" (view blocked by {what})"; }
            }
            else { _clearFor += dt; if (_clearFor > 1f) _blockedBy = ""; }
            float wantIn = blocked || (_clearFor < 1f && _in > 0.01f) ? 1f : 0f;
            _in += (wantIn - _in) * (1f - Mathf.Exp(-dt * (wantIn > _in ? 4f : 1.2f)));
            float total = _zoom - 0.45f * _in;
            // The Distance setting is the base the smart moves add to (set when it changes; your own zooming
            // moves the base until the game restarts, like Height).
            if (_distanceOf != Distance.Value && _controls != null)
            {
                _distanceOf = Distance.Value;
                _zoomBase = Mathf.Clamp(Distance.Value, Mathf.Min(_controls.minZoom, _controls.maxZoom), Mathf.Max(_controls.minZoom, _controls.maxZoom));
            }
            SetZoom(total);
            _appliedZoom = total;

            __instance.m_XAxis = x;
            __instance.m_YAxis = y;

            if (Log.Value && now >= _nextLog)
            {
                _nextLog = now + 1f;
                Plugin.Info($"smart cam: {kmh:0} km/h{(reversing ? " REVERSE" : "")} slope {slope * 100:0}% side {side * 100:0}% " +
                            $"crest {crest * 100:0}% drop {drop:0.0} m trees {treesAround}/{Trees.Near} | want x {wantX:0} lift {wantLift:0.00} zoom {wantZoom:0.00} " +
                            $"| now x {x.Value:0} y {y.Value:0.00} lift {_lift:0.00} zoom {_zoom:0.00} in {_in:0.00} idle {(_idle > 50f ? 0f : _idle):0.0}s{_seen}");
                _seen = "";
            }
        }
        catch (Exception e) { Plugin.ErrorOnce("smart camera", e); }
    }

    // Out of the car / in menus: give back the zoom we added, gently (the height is ours anyway).
    static void Unwind(CinemachineFreeLook fl, float dt)
    {
        if (Mathf.Abs(_appliedZoom) < 1e-3f) return;
        if (CockpitLink.CockpitActive || Menu.IsOpen) return;   // keep it for when you come back
        float zoom = _appliedZoom * Mathf.Exp(-dt * 2f);
        SetZoom(zoom);
        _appliedZoom = _zoom = zoom;
    }

    static float _sideSign = 1f;   // which side of the car the camera sits on: + = left, - = right
    static float _otherSideFor, _sinceSwap = 99f, _baseY = float.NaN, _heightOf = float.NaN;
    static bool _ownY;
    static float _in, _clearFor = 99f, _fullDist;   // zoomed in because something blocks the view (0..1)
    static string _blockedBy = "";
    static bool _blockCheckBroken;
    static float _mySide = 1f, _flatFor, _turnUntil, _distanceOf = float.NaN;   // your side (+ = left), from the button
    static bool _revHold;                  // reversing with a camera turn of your own: keep it
    static float _revLastX = float.NaN;     // the heading you chose last time you reversed (ReverseRemembers)

    // Wheel button "CameraSide": the camera goes to the other side of the car, and that becomes your side.
    public static string ToggleSide()
    {
        if (!Enabled.Value) return "Smart camera is off";
        _mySide = -_sideSign;
        _sideSign = _mySide;
        _otherSideFor = 0f;
        _idle = 99f;                                  // take over at once, even right after your own moves
        _turnUntil = Time.unscaledTime + 3f;          // and turn there even when standing still
        string s = _mySide > 0f ? "left" : "right";
        Plugin.Info($"smart cam: your side is now {s}");
        return $"Camera: {s} side";
    }

    public static string SideName => _mySide > 0f ? "Left" : "Right";

    static void ResetState()
    {
        _appliedZoom = _lift = _zoom = _in = 0f;
        _engage = 0f;
        _zoomBase = _zoomWritten = float.NaN;
    }

    // Is anything (not the car, not the ground) between the car's roof and the camera? Checks the line out to
    // where the camera is when not zoomed in, so it doesn't zoom back out into the tree it just left.
    // Three lines (middle and 25 cm to each side). Only Physics.Raycast: this game's build has
    // SphereCastAll stripped ("Method unstripping failed"); Raycast is the one Ground() already uses.
    static bool BlockedView(Vector3 cam, Transform car, Rigidbody rb, out string what)
    {
        what = "";
        var focus = rb.position + Vector3.up * 1.2f;
        var dir = cam - focus;
        float dist = dir.magnitude;
        if (dist < 1f) return false;
        if (_in < 0.05f) _fullDist = dist;
        float len = Mathf.Max(dist, _fullDist) - 0.4f;
        var d = dir / dist;
        var side = Vector3.Cross(Vector3.up, d).normalized * 0.25f;
        foreach (var off in new[] { Vector3.zero, side, -side })
        {
            var from = focus + off;
            float left = len;
            for (int i = 0; i < 6 && left > 0.05f; i++)   // step past the car's own colliders
            {
                if (!Physics.Raycast(from, d, out var h, left, ~0, QueryTriggerInteraction.Ignore)) break;
                var c = h.collider;
                bool own = c == null || c.attachedRigidbody == rb || c.transform.IsChildOf(car) ||
                           c.name.StartsWith("PhotoDetection");   // an animal's photo-mode box, not something solid
                bool ground = c != null && c.TryCast<TerrainCollider>() != null;   // the game's camera keeps above it itself
                if (!own && !ground)
                {
                    what = $"{c.name} ({LayerMask.LayerToName(c.gameObject.layer)})";
                    return true;
                }
                float step = h.distance + 0.05f;
                from += d * step;
                left -= step;
            }
        }
        // Treetops: no colliders, so from the terrain's tree list.
        if (Trees.Blocks(focus, focus + d * len)) { what = "a treetop"; return true; }
        return false;
    }

    // The game's zoom target = your distance (the base) + our extra, written whole every frame. It used to be
    // added in steps: at the game's zoom limit the clamped part of a step was lost going in and added in full
    // coming out, so every tree pushed the camera further back, towards the game's normal distance
    // (2026-10-27). A change we didn't write is your own zooming: it moves the base.
    static void SetZoom(float total)
    {
        if (_controls == null) return;
        float lo = Mathf.Min(_controls.minZoom, _controls.maxZoom), hi = Mathf.Max(_controls.minZoom, _controls.maxZoom);
        float range = hi > lo && hi - lo < 1e6f ? hi - lo : 1f;
        float cur = _controls.targetZoomMultiplier;
        if (float.IsNaN(_zoomBase)) _zoomBase = cur;
        else if (!float.IsNaN(_zoomWritten) && Mathf.Abs(cur - _zoomWritten) > 1e-4f) _zoomBase = Mathf.Clamp(_zoomBase + cur - _zoomWritten, lo, hi);
        _zoomWritten = Mathf.Clamp(_zoomBase + total * range, lo, hi);
        _controls.targetZoomMultiplier = _zoomWritten;
    }
    static float _zoomBase = float.NaN, _zoomWritten = float.NaN;

    static bool PlayerMovedCamera()
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            var d = mouse.delta.ReadValue();
            if (mouse.rightButton.isPressed && d.sqrMagnitude > 1f) { _seen = " (mouse)"; return true; }
        }
        var pad = Gamepad.current;
        if (pad != null && pad.rightStick.ReadValue().magnitude > 0.2f) { _seen = " (right stick)"; return true; }
        return false;
    }

    static float Wrap(float v, AxisState a)
    {
        if (!a.m_Wrap) return Mathf.Clamp(v, Mathf.Min(a.m_MinValue, a.m_MaxValue), Mathf.Max(a.m_MinValue, a.m_MaxValue));
        float lo = a.m_MinValue, span = a.m_MaxValue - a.m_MinValue;
        if (span <= 0f) return v;
        return lo + Mathf.Repeat(v - lo, span);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // Height of the ground (the game's terrain) at a point; falls back to a ray down.
    static float Ground(Vector3 p)
    {
        try
        {
            var terrains = Terrain.activeTerrains;
            if (terrains != null)
                foreach (var t in terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    var o = t.transform.position;
                    var size = t.terrainData.size;
                    if (p.x >= o.x && p.x <= o.x + size.x && p.z >= o.z && p.z <= o.z + size.z)
                        return o.y + t.SampleHeight(p);
                }
        }
        catch (Exception e) { Plugin.ErrorOnce("smart camera terrain", e); }
        return Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : p.y;
    }

    static bool _gearBroken;

    static bool ReverseGear(VPVehicleController car)
    {
        if (_gearBroken) return false;
        try { return car.data.Get(Channel.Vehicle, VehicleData.GearboxGear) < 0; }
        catch (Exception e) { _gearBroken = true; Plugin.ErrorOnce("smart camera reverse gear", e); return false; }
    }

    static VPVehicleController Car()
    {
        var vi = InputHooks.LocalVehicleInput;
        if (vi == null) { _car = null; _carOf = null; return null; }
        if (_carOf != vi) { _carOf = vi; _car = vi.gameObject.GetComponent<VPVehicleController>(); }
        return _car;
    }

    static void LogSetup(CinemachineFreeLook fl)
    {
        _loggedSetup = true;
        var x = fl.m_XAxis;
        var y = fl.m_YAxis;
        int terrains = 0;
        try { terrains = Terrain.activeTerrains?.Length ?? 0; } catch { }
        Plugin.Info($"smart cam: chase camera '{fl.name}' binding {fl.m_BindingMode}, x axis {x.m_MinValue}..{x.m_MaxValue} wrap {x.m_Wrap} " +
                    $"value {x.Value:0}, y axis {y.m_MinValue}..{y.m_MaxValue} value {y.Value:0.00}, {terrains} terrain(s), side slope {SideSlope.Value}");
    }
}
