using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OthWheel;

// Keyboard injection (wheel buttons press the game's own keys) and a foreground check,
// so keys are never typed into another program.
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    // The union must be as large as MOUSEINPUT, or SendInput rejects the struct size.
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800;

    // One mouse wheel notch (120 units); positive = scroll up (the game's camera zoom in).
    public static void MouseWheel(int notches) => Mouse(new MOUSEINPUT { mouseData = unchecked((uint)(notches * 120)), dwFlags = MOUSEEVENTF_WHEEL });

    // Relative mouse movement in pixels (positive y = down), as the game's camera orbit reads it.
    public static void MouseMove(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Mouse(new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE });
    }

    // The game orbits the car camera with mouse movement while the right button is held.
    public static void MouseRight(bool down) => Mouse(new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP });

    static void Mouse(MOUSEINPUT mi)
    {
        var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = mi } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    // Physical keyboard state, for the mod's own hotkeys.
    public static bool IsKeyDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    static readonly uint _pid = (uint)Process.GetCurrentProcess().Id;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int max);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // The game's main window (DirectInput ties exclusive device access to a window of this process).
    public static IntPtr FindGameWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (GetWindowThreadProcessId(h, out var pid) == 0 || pid != _pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != "UnityWndClass") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static bool GameHasFocus()
    {
        var h = GetForegroundWindow();
        return h != IntPtr.Zero && GetWindowThreadProcessId(h, out var pid) != 0 && pid == _pid;
    }

    public static void Key(ushort vk, bool down)
    {
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    static bool IsExtended(ushort vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0xA3 or 0xA5;

    // Key names as used in the config file (same names as the game's own keyboard bindings).
    static readonly Dictionary<string, ushort> Names = BuildNames();

    static Dictionary<string, ushort> BuildNames()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20, ["Enter"] = 0x0D, ["Escape"] = 0x1B, ["Tab"] = 0x09, ["Backspace"] = 0x08,
            ["LeftShift"] = 0xA0, ["RightShift"] = 0xA1, ["LeftCtrl"] = 0xA2, ["RightCtrl"] = 0xA3,
            ["LeftAlt"] = 0xA4, ["RightAlt"] = 0xA5,
            ["UpArrow"] = 0x26, ["DownArrow"] = 0x28, ["LeftArrow"] = 0x25, ["RightArrow"] = 0x27,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Home"] = 0x24, ["End"] = 0x23, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        };
        for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
        for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c;
        for (int i = 1; i <= 12; i++) d["F" + i] = (ushort)(0x6F + i);
        return d;
    }

    // "H+LeftArrow" -> [H, LeftArrow]. Unknown names are reported and skipped.
    public static ushort[] ParseKeys(string spec, string forAction)
    {
        var list = new List<ushort>();
        foreach (var part in (spec ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Names.TryGetValue(part, out var vk)) list.Add(vk);
            else Plugin.Warn($"unknown key '{part}' for {forAction}, ignored");
        }
        return list.ToArray();
    }
}
