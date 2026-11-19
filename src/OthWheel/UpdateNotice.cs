using System.Collections.Generic;

namespace OthWheel;

// Update notice in the wheel mod: one message for both mods (the cockpit hands its result over through
// CockpitLink), shown once per game start on a menu screen; F3 then keeps one line per update (Menu).
internal static class UpdateNotice
{
    public const string NexusPage = "https://www.nexusmods.com/overthehill/mods/83";
    const string Footer = "Nothing is downloaded by itself: install it like the last one (Install.bat updates and keeps your settings). " +
                          "F3 keeps a line about it. No more checks: [Updates] Check = false in the settings file.";

    static readonly HashSet<string> _shown = new();   // "OTH Wheel 1.2.1": already in a message this game start
    static List<UpdateCheck.Found> _showing;
    static float _wheelDoneAt = -1f;
    static UpdateCheck.Found _cockpit;

    public static void Start() => UpdateCheck.Start(Plugin.Name, Plugin.Version, "JonkoMonk/oth-wheel", NexusPage);

    public static void Update() => UpdateCheck.Tick();

    // A newer cockpit, as the cockpit mod reported it (null = none, or no cockpit / an older one).
    static UpdateCheck.Found Cockpit()
    {
        string latest = CockpitLink.CockpitUpdateLatest;
        if (latest == null) return _cockpit = null;
        if (_cockpit == null || _cockpit.Latest != latest)
            _cockpit = new UpdateCheck.Found
            {
                Mod = "OTH Cockpit", Latest = latest, Installed = CockpitLink.CockpitUpdateInstalled,
                WhatsNew = CockpitLink.CockpitUpdateWhatsNew, Url = CockpitLink.CockpitUpdateUrl,
            };
        return _cockpit;
    }

    // For the F3 menu: every newer version found (shown there even after "Don't show this again").
    public static List<UpdateCheck.Found> All()
    {
        var list = new List<UpdateCheck.Found>();
        if (UpdateCheck.Newer != null) list.Add(UpdateCheck.Newer);
        var c = Cockpit();
        if (c != null) list.Add(c);
        return list;
    }

    static string Key(UpdateCheck.Found f) => f.Mod + " " + f.Latest;

    public static void OnGUI(float now)
    {
        UpdatePopup.Idle();
        if (!UpdateCheck.Done) return;
        if (_wheelDoneAt < 0f) _wheelDoneAt = now;
        bool canShow = !Menu.IsOpen && !Wizard.IsOpen && UpdatePopup.OnMenuScreen(now, GameMenus.MapName == "UI");
        if (_showing == null)
        {
            // With the cockpit installed, wait (up to 30 s) for its check too: one message for both.
            if (CockpitLink.CockpitInstalled && !CockpitLink.CockpitUpdateChecked && now - _wheelDoneAt < 30f) return;
            if (!canShow) return;
            var list = new List<UpdateCheck.Found>();
            if (UpdateCheck.WantsPopup && !_shown.Contains(Key(UpdateCheck.Newer))) list.Add(UpdateCheck.Newer);
            var c = Cockpit();
            if (c != null && CockpitLink.CockpitUpdatePopup && !_shown.Contains(Key(c))) list.Add(c);
            if (list.Count == 0) return;
            foreach (var f in list) _shown.Add(Key(f));
            _showing = list;
            Plugin.Info($"update notice: shown ({string.Join(", ", list.ConvertAll(Key))})");
        }
        // Stays until closed; hidden while not on a menu screen (it comes back there).
        if (!canShow) return;
        if (!UpdatePopup.Draw(_showing, Footer, out bool dontShow)) return;
        if (dontShow)
            foreach (var f in _showing)
            {
                if (f == UpdateCheck.Newer) { UpdateCheck.DontShowFor.Value = f.Latest; Settings.MarkSaved(); }
                else CockpitLink.CockpitUpdateDontShow = f.Latest;   // the cockpit saves it in its own settings file
            }
        Plugin.Info($"update notice: closed{(dontShow ? ", 'Don't show this again' ticked" : "")}");
        _showing = null;
    }
}
