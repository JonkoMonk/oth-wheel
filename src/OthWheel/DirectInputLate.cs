extern alias udi;

using System;
using UnityEngine;
#if MELON
using DIInputManager = udi::Il2Cpp.DIInputManager;
using DIManager = udi::Il2CppDirectInputManager.DIManager;
using RuntimeFFBConfigUI = udi::Il2Cpp.RuntimeFFBConfigUI;
using RuntimeInputMappingUI = udi::Il2Cpp.RuntimeInputMappingUI;
#else
using DIInputManager = udi::DIInputManager;
using DIManager = udi::DirectInputManager.DIManager;
using RuntimeFFBConfigUI = udi::RuntimeFFBConfigUI;
using RuntimeInputMappingUI = udi::RuntimeInputMappingUI;
#endif

namespace OthWheel;

// Everything that touches UnityDirectInput (the game's DirectInput library). Calling those classes
// before the game has initialized them breaks wheel detection, so this only starts 3 s after the
// local car exists.
//
// Its job now: make the game's library let go of the R3 (it holds it exclusively, and its old
// native DLL sends broken constant forces), so our DInput client can open it exclusively for force
// feedback; and read the game's crash-jolt
// values, which the game still writes into DIInputManager's fields.
internal static class DirectInputLate
{
    static float _carSeenAt = -1f, _nextRelease;
    public static bool ReleasedFromGame { get; private set; }

    public static void Update(float now)
    {
        if (InputHooks.LocalVehicleInput == null) return;
        if (_carSeenAt < 0f) _carSeenAt = now;
        if (now - _carSeenAt < 3f) return;
        HideGamePanels();
        // Re-check every few seconds while our own connection is down: the game may re-attach the wheel.
        if (Ffb.Ready || now < _nextRelease) return;
        _nextRelease = now + 3f;
        ReleaseFromGame();
    }

    static HarmonyLib.Harmony _h;
    static bool _effectsMuted;

    public static void Init(HarmonyLib.Harmony h) => _h = h;

    // Once the wheel is ours, the game's effect updates can only fail (and log an error per crash),
    // so they are skipped. The game still writes its jolt values into the fields, which we read.
    public static bool SkipOriginal() => false;

    static void MuteGameEffects()
    {
        if (_effectsMuted || _h == null) return;
        _effectsMuted = true;
        foreach (var name in new[] { "UpdateFFBEffects", "UpdatePeriodicForces" })
        {
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(DIInputManager), name);
                _h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(DirectInputLate), nameof(SkipOriginal)));
                Plugin.Info($"muted the game's DIInputManager.{name} (wheel now driven by the mod)");
            }
            catch (Exception e) { Plugin.Error($"could not mute DIInputManager.{name}: {e.GetType().Name}: {e.Message}"); }
        }
    }

    // The library's developer panels (raw FFB effect sliders, input mapping) open with a key the game
    // binds (F3 for the FFB panel, same as our setup key). They drive the game's own wheel code, which
    // the mod switches off, so they only confuse players: never toggle or draw them.
    static bool _panelsHidden;

    static void HideGamePanels()
    {
        if (_panelsHidden || _h == null) return;
        _panelsHidden = true;
        foreach (var (type, name) in new[] { (typeof(RuntimeFFBConfigUI), "InputAction_performed"), (typeof(RuntimeFFBConfigUI), "OnGUI"),
                                             (typeof(RuntimeInputMappingUI), "InputAction_performed"), (typeof(RuntimeInputMappingUI), "OnGUI") })
        {
            try
            {
                var m = HarmonyLib.AccessTools.Method(type, name);
                _h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(DirectInputLate), nameof(SkipOriginal)));
            }
            catch (Exception e) { Plugin.Error($"could not hide {type.Name}.{name}: {e.GetType().Name}: {e.Message}"); }
        }
        Plugin.Info("hid the game's DirectInput developer panels (FFB config, input mapping)");
    }

    // For the setup wizard's force feedback test: release right away instead of 3 s after the car appears.
    // Only once the car exists, i.e. once the game has fully set up its DirectInput library.
    public static bool TryReleaseNow()
    {
        if (ReleasedFromGame) return true;
        if (InputHooks.LocalVehicleInput == null) return false;
        ReleaseFromGame();
        return ReleasedFromGame;
    }

    static void ReleaseFromGame()
    {
        MuteGameEffects();
        try
        {
            var active = DIManager.GetActiveDevices();
            if (active != null)
                foreach (var guid in active)
                {
                    if (string.IsNullOrEmpty(guid) || !DIManager.FFBCapable(guid)) continue;
                    DIManager.StopAllFFBEffects(guid);
                    bool ok = DIManager.Destroy(guid);
                    Plugin.Info($"released wheel {guid} from the game's DirectInput library: {ok}");
                }
        }
        catch (Exception e) { Plugin.ErrorOnce("release wheel from game", e); }
        ReleasedFromGame = true;
    }

    // The game's crash jolt: it writes a fading ConstantForceMagnitude into DIInputManager on impact.
    // Only values that keep changing count; a value that sits still is a leftover, not a jolt.
    static int _lastValue;
    static float _lastChange = -100f;

    public static float ReadGameJolt(float now)
    {
        if (!ReleasedFromGame) return 0f;
        try
        {
            var m = DIInputManager.Instance;
            if (m == null) return 0f;
            int v = m.ConstantForceEnabled ? m.ConstantForceMagnitude : 0;
            if (v != _lastValue) { _lastValue = v; _lastChange = now; }
            return now - _lastChange < 0.15f ? v : 0f;
        }
        catch (Exception e) { Plugin.ErrorOnce("read game jolt", e); return 0f; }
    }
}
