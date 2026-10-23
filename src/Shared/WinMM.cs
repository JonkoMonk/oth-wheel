using System;
using System.Runtime.InteropServices;

namespace OthWheel;

// Windows' legacy joystick API. Reads non-exclusively, so it works even while
// UnityDirectInput holds the wheel in exclusive mode (exclusive only blocks other exclusive users).
internal static class WinMM
{
    public const ushort MozaVid = 0x346E;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOYINFOEX
    {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax,
                    wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    const uint JOY_RETURNALL = 0xFF;

    [DllImport("winmm.dll")] static extern uint joyGetPosEx(uint id, ref JOYINFOEX info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint joyGetDevCapsW(UIntPtr id, out JOYCAPSW caps, uint size);

    // Returns the joystick id of the first device with the given USB vendor id, or -1.
    public static int Find(ushort vid, out JOYCAPSW caps)
    {
        caps = default;
        for (uint i = 0; i < 16; i++)
            if (joyGetDevCapsW((UIntPtr)i, out var c, (uint)Marshal.SizeOf<JOYCAPSW>()) == 0 && c.wMid == vid)
            {
                caps = c;
                return (int)i;
            }
        return -1;
    }

    public static bool Read(int id, out JOYINFOEX info)
    {
        info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
        return id >= 0 && joyGetPosEx((uint)id, ref info) == 0;
    }
}
