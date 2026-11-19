using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace OthWheel;

// Update notice, part 1: once per game start, on a background thread, ask GitHub which versions of this mod
// exist. Nothing personal is sent (only a plain User-Agent), nothing is downloaded or replaced: the player gets
// a message (UpdatePopup) and decides. A failure (offline, rate limit, GitHub down) is one log line, nothing else.
// The same file is in OTH Wheel and OTH Cockpit (only the namespace differs): change both together.
internal static class UpdateCheck
{
    public static ConfigEntry<bool> Check;
    public static ConfigEntry<string> PretendVersion, DontShowFor;

    public sealed class Found
    {
        public string Mod, Installed, Latest, WhatsNew, Url;
    }

    static volatile Found _found;
    static volatile bool _done;
    static volatile string _log;
    static string _mod, _installed, _repo, _url;

    public static Found Newer => _found;     // set once the check found a newer version
    public static bool Done => _done;          // the check has finished (found or not, failed, or switched off)
    // A newer version the player hasn't ticked "Don't show this again" for.
    public static bool WantsPopup => _found != null && Compare(_found.Latest, DontShowFor.Value) != 0;

    public static void Bind(ConfigFile cfg)
    {
        Check = cfg.Bind("Updates", "Check", true,
            "Once per game start, ask GitHub whether a newer version of this mod is out, and if so say it once in the " +
            "game's menu (with a 'Don't show this again' box). Nothing is downloaded. false = never ask.");
        DontShowFor = cfg.Bind("Updates", "DontShowFor", "",
            "Internal: the version you ticked 'Don't show this again' for (a still newer one is shown once more).");
        PretendVersion = cfg.Bind("Updates", "PretendVersion", "",
            "For testing the update notice: act as if this older version were installed (e.g. 1.0.0). Empty = the real version.");
    }

    // modName "OTH Wheel", repo "JonkoMonk/oth-wheel", url = the mod's Nexus page.
    public static void Start(string modName, string installed, string repo, string url)
    {
        _mod = modName; _repo = repo; _url = url;
        string pretend = PretendVersion.Value?.Trim();
        _installed = string.IsNullOrEmpty(pretend) ? installed : pretend;
        if (!Check.Value) { _done = true; Plugin.Info("update check: off ([Updates] Check = false)"); return; }
        if (_installed != installed) Plugin.Info($"update check: pretending {_installed} is installed ([Updates] PretendVersion)");
        var t = new Thread(Run) { IsBackground = true, Name = "OTH update check" };
        t.Start();
    }

    // Main thread, every frame: the background thread only leaves a line here (no logging from other threads).
    public static void Tick()
    {
        var l = _log;
        if (l == null) return;
        _log = null;
        Plugin.Info(l);
    }

    static void Run()
    {
        try
        {
            var (tag, body) = Pick(Fetch(_repo));
            if (tag == null) { _log = $"update check: no releases of {_repo} found"; return; }
            if (Compare(tag, _installed) > 0)
            {
                _found = new Found { Mod = _mod, Installed = _installed, Latest = Clean(tag), WhatsNew = OneLine(body), Url = _url };
                _log = $"update check: {_mod} {_found.Latest} is out (installed {_installed})" +
                       (Compare(_found.Latest, DontShowFor.Value) == 0 ? ", 'Don't show this again' ticked for it" : "");
            }
            else _log = $"update check: {_mod} {_installed} is the newest (GitHub: {tag})";
        }
        catch (Exception e)
        {
            _log = $"update check failed, no notice this time ({(e is AggregateException a ? a.InnerException ?? e : e).GetType().Name}: {e.Message})";
        }
        finally { _done = true; }
    }

    // The release list (not releases/latest: the cockpit is a pre-release, and that call skips those).
    internal static string Fetch(string repo)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Plugin.Name.Replace(" ", "") + "-update-check");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=30").GetAwaiter().GetResult();
    }

    // Highest version among the published (non-draft) releases.
    internal static (string tag, string body) Pick(string json)
    {
        string bestTag = null, bestBody = null;
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var t) || t.ValueKind != JsonValueKind.String) continue;
            string tag = t.GetString();
            if (Parse(tag) == null) continue;
            if (bestTag == null || Compare(tag, bestTag) > 0)
            {
                bestTag = tag;
                bestBody = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "";
            }
        }
        return (bestTag, bestBody);
    }

    // "v0.4.4-alpha" -> "0.4.4-alpha"
    static string Clean(string tag) => tag.Trim().TrimStart('v', 'V');

    // Numbers only: "v1.2.0" -> [1,2,0]; "0.4.4-alpha" -> [0,4,4] (the cockpit is always alpha, so the label doesn't count).
    static int[] Parse(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = Clean(v);
        int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) v = v.Substring(0, cut);
        var parts = v.Split('.');
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return n;
    }

    // > 0: a is newer. Something that isn't a version counts as older than any version.
    internal static int Compare(string a, string b)
    {
        var x = Parse(a); var y = Parse(b);
        if (x == null || y == null) return x == null ? (y == null ? 0 : -1) : 1;
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }

    // "What's new" in one line: the first plain line of the release text (its summary), without markdown and
    // without the "Together with OTH ..." sentence. Empty when the text starts with a list or has none.
    internal static string OneLine(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("<!--")) continue;
            if (line.StartsWith("-") || line.StartsWith("*") || line.StartsWith("#") || line.StartsWith(">") || line.StartsWith("|")) return "";
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");   // [text](link) -> text
            line = line.Replace("**", "").Replace("`", "");
            var keep = new System.Text.StringBuilder();
            foreach (var s in System.Text.RegularExpressions.Regex.Split(line, @"(?<=[.!?])\s+"))
                if (!s.StartsWith("Together with", StringComparison.OrdinalIgnoreCase)) keep.Append(keep.Length > 0 ? " " : "").Append(s);
            line = keep.ToString().Trim();
            return line.Length > 180 ? line.Substring(0, 177).TrimEnd() + "..." : line;
        }
        return "";
    }

    // Opens the mod's Nexus page in the web browser. Returns a short note for the player.
    public static string OpenPage(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            Plugin.Info($"update notice: opened {url}");
            return "Opened in your web browser (alt-tab to it).";
        }
        catch (Exception e) { return $"Couldn't open the browser ({e.Message}): {url}"; }
    }
}
