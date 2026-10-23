using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OthWheel;

// Usage: DITest [seconds=5] [ffb=none|zero|push]
//   zero: open the force feedback device exclusively and play a 0-strength force (nothing moves)
//   push: +2000 for 0.4 s then -2000 for 0.4 s, prints which way the steering axis moved
internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "profiles") return ProfileTest();
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 5;
        string ffb = args.Length > 1 ? args[1] : "none";
        // DirectInput wants a top-level window of this process.
        IntPtr hwnd = CreateWindowExW(0, "STATIC", "DITest", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (!DInput.Init()) { Console.WriteLine("init failed: " + DInput.LastError); return 1; }
        var infos = DInput.Enumerate();
        Console.WriteLine($"{infos.Count} game controller(s):");
        var devs = new List<DInput.Device>();
        foreach (var i in infos)
        {
            var d = DInput.Device.Open(i, hwnd, false);
            Console.WriteLine($"  '{i.Name}' usb {i.UsbId} instance {i.Instance} -> " +
                              (d == null ? "open FAILED " + DInput.LastError : $"axes {d.Axes}, buttons {d.Buttons}, povs {d.Povs}, ffb {d.ForceFeedback}"));
            if (d != null) devs.Add(d);
        }

        foreach (var d in devs)
        {
            d.Read();
            Console.WriteLine($"  [{d.Info.Name}] status {d.Status} axes {string.Join(" ", d.Axis.Select((v, k) => $"{DInput.AxisNames[k]}={v}"))} pov {string.Join(",", d.Pov)} buttons [{string.Join(",", Enumerable.Range(0, 128).Where(b => d.Button[b]))}]");
        }

        var wheel = devs.FirstOrDefault(d => d.ForceFeedback);
        if (ffb != "none")
        {
            if (wheel == null) Console.WriteLine("no force feedback device");
            else if (!wheel.StartForceFeedback()) Console.WriteLine("force feedback start FAILED: " + wheel.Status);
            else
            {
                Console.WriteLine("force feedback started: " + wheel.Status);
                if (ffb == "zero")
                {
                    for (int t = 0; t < 10; t++) { Console.Write(wheel.SetForces(0, 0) ? "." : "x"); Thread.Sleep(50); }
                    Console.WriteLine();
                }
                else if (ffb == "push")
                {
                    foreach (int m in new[] { 2000, -2000 })
                    {
                        wheel.Read(); int before = wheel.Axis[0];
                        wheel.SetForces(m, 0); Thread.Sleep(400);
                        wheel.Read(); Console.WriteLine($"force {m}: X {before} -> {wheel.Axis[0]}");
                    }
                    wheel.SetForces(0, 0);
                }
                wheel.StopForceFeedback();
                Console.WriteLine("force feedback stopped, exclusive released");
            }
        }

        // Live changes for a few seconds.
        var last = devs.Select(d => (int[])d.Axis.Clone()).ToList();
        var lastB = devs.Select(d => (bool[])d.Button.Clone()).ToList();
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            for (int k = 0; k < devs.Count; k++)
            {
                var d = devs[k];
                if (!d.Read()) { Console.WriteLine($"[{d.Info.Name}] read failed {d.Status}"); continue; }
                for (int a = 0; a < DInput.AxisCount; a++)
                    if (Math.Abs(d.Axis[a] - last[k][a]) > 1500) { Console.WriteLine($"[{d.Info.Name}] {DInput.AxisNames[a]} {last[k][a]} -> {d.Axis[a]}"); last[k][a] = d.Axis[a]; }
                for (int b = 0; b < 128; b++)
                    if (d.Button[b] != lastB[k][b]) { Console.WriteLine($"[{d.Info.Name}] button {b} {(d.Button[b] ? "down" : "up")}"); lastB[k][b] = d.Button[b]; }
            }
            Thread.Sleep(10);
        }
        foreach (var d in devs) d.Dispose();
        Console.WriteLine("done");
        return 0;
    }

    // Built-in presets load, match the connected devices, and a setup survives save + load.
    static int ProfileTest()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }

        if (Directory.Exists(BepInEx.Paths.ConfigPath)) Directory.Delete(BepInEx.Paths.ConfigPath, true);
        var builtin = Profiles.BuiltIn();
        Check(builtin.Count >= 1, $"built-in presets loaded: {string.Join(", ", builtin.Select(p => p.Name))}");
        var r3 = builtin.FirstOrDefault(p => p.Devices.Any(d => d.UsbId == "346E:0005"));
        Check(r3 != null, "Moza R3 preset present");
        Check(r3 != null && r3.Buttons.Count == 21 && r3.Buttons["ResetCar"].Button == 32 && r3.Label(r3.Buttons["ResetCar"]) == "BOX", "R3 buttons and labels");
        Check(r3 != null && r3.Brake.Axis == "Rz" && Profiles.AxisIndex(r3.Brake.Axis) == 5 && r3.Ffb.Direction == -1, "R3 brake axis Rz, force feedback direction -1");

        DInput.Init();
        var connected = DInput.Enumerate();
        Console.WriteLine($"connected: {string.Join(", ", connected.Select(c => $"{c.Name} {c.UsbId}"))}");
        var map = r3 != null ? Profiles.Match(r3, connected) : null;
        Check(map != null, "R3 preset matches the connected wheel");
        var fake = new List<DInput.DeviceInfo> { new() { Name = "Other wheel", Product = new Guid("c24f046d-0000-0000-0000-504944564944") } };
        Check(fake[0].UsbId == "046D:C24F", $"USB id from product GUID ({fake[0].UsbId})");
        Check(r3 == null || Profiles.Match(r3, fake) == null, "R3 preset does not match a Logitech G29");

        var all = Profiles.All();
        Check(all.Count == builtin.Count, "no setups saved yet");
        var mine = r3.Clone();
        mine.Name = "My test setup";
        mine.Buttons["Horn"] = new ButtonRef { Device = 0, Button = ButtonRef.PovBase + 2 };
        string path = Profiles.Save(mine);
        Check(File.Exists(path), $"saved to {path}");
        all = Profiles.All();
        Check(all.Count == builtin.Count + 1 && all[0].Name == "My test setup" && all[0].Source == "wizard", "own setup loads first");
        Check(all[0].Buttons["Horn"].Button == 130 && all[0].Label(all[0].Buttons["Horn"]) == "hat 1 down", "hat button survives save/load");
        Check(all[0].Labels.Count == r3.Labels.Count, "labels copied into own setup");
        Console.WriteLine(fails == 0 ? "ALL OK" : $"{fails} FAILED");
        return fails;
    }
}
