using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OthWheel;

// Minimal DirectInput 8 client, called through the COM vtables directly (no extra DLLs to ship).
// Reads any game controller (8 axes, 4 POV hats, 128 buttons) and plays force feedback
// (constant force + damper) on one of them.
//
// Every device is read NON-exclusively, which works even while another program (or the game's
// own DirectInput library) holds it exclusively. Force feedback needs EXCLUSIVE access; that is
// tied to a top-level window of this process, and "background" mode so it keeps working when the
// window loses focus.
internal static unsafe class DInput
{
    public const int AxisCount = 8, PovCount = 4, ButtonCount = 128;
    // Axis order in the state (DIJOYSTATE2): X, Y, Z, Rx, Ry, Rz, Slider 1, Slider 2.
    public static readonly string[] AxisNames = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider1", "Slider2" };
    public const int RawMax = 65535;

    const int DI8DEVCLASS_GAMECTRL = 4, DIEDFL_ATTACHEDONLY = 1;
    const int DISCL_EXCLUSIVE = 1, DISCL_NONEXCLUSIVE = 2, DISCL_BACKGROUND = 8;
    const int StateSize = 272; // sizeof(DIJOYSTATE2)
    const int DIERR_INPUTLOST = unchecked((int)0x8007001E), DIERR_NOTACQUIRED = unchecked((int)0x8007000C);

    [DllImport("dinput8.dll")] static extern int DirectInput8Create(IntPtr hinst, int version, in Guid riid, out IntPtr ppvOut, IntPtr punkOuter);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr name);

    static readonly Guid IID_IDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    static readonly Guid GUID_XAxis = new("A36D02E0-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_YAxis = new("A36D02E1-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_ZAxis = new("A36D02E2-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_RxAxis = new("A36D02F4-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_RyAxis = new("A36D02F5-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_RzAxis = new("A36D02E3-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_Slider = new("A36D02E4-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_POV = new("A36D02F2-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GUID_ConstantForce = new("13541C20-8E33-11D0-9AD0-00A0C9A06E35");
    static readonly Guid GUID_Damper = new("13541C28-8E33-11D0-9AD0-00A0C9A06E35");

    static IntPtr _di;
    public static string LastError = "";

    static void** Vtbl(IntPtr com) => *(void***)com;

    public static bool Init()
    {
        if (_di != IntPtr.Zero) return true;
        int hr = DirectInput8Create(GetModuleHandleW(IntPtr.Zero), 0x0800, IID_IDirectInput8W, out _di, IntPtr.Zero);
        if (hr < 0) { LastError = $"DirectInput8Create 0x{hr:X8}"; _di = IntPtr.Zero; return false; }
        BuildDataFormat();
        return true;
    }

    // --- device list ---------------------------------------------------------------------------

    public sealed class DeviceInfo
    {
        public Guid Instance, Product;
        public string Name = "";
        // USB ids: DirectInput's product GUID starts with PID<<16 | VID for HID devices.
        public ushort Vid => (ushort)(BitConverter.ToUInt32(Product.ToByteArray(), 0) & 0xFFFF);
        public ushort Pid => (ushort)(BitConverter.ToUInt32(Product.ToByteArray(), 0) >> 16);
        public string UsbId => $"{Vid:X4}:{Pid:X4}";
    }

    static List<DeviceInfo> _enumResult;

    // A delegate rather than [UnmanagedCallersOnly]: the game's interop UnityEngine.CoreModule also
    // defines that attribute, which makes the name ambiguous. Kept in a field so it is never collected.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumDevicesCallback(IntPtr inst, IntPtr ctx);
    static readonly EnumDevicesCallback _enumCallback = EnumCallback;
    static readonly IntPtr _enumCallbackPtr = Marshal.GetFunctionPointerForDelegate(_enumCallback);

    static int EnumCallback(IntPtr inst, IntPtr ctx)
    {
        try
        {
            // DIDEVICEINSTANCEW: dwSize, guidInstance @4, guidProduct @20, dwDevType @36, tszInstanceName @40, tszProductName @560.
            var d = new DeviceInfo
            {
                Instance = *(Guid*)(inst + 4),
                Product = *(Guid*)(inst + 20),
                Name = Marshal.PtrToStringUni(inst + 560) ?? "",
            };
            if (string.IsNullOrWhiteSpace(d.Name)) d.Name = Marshal.PtrToStringUni(inst + 40) ?? "";
            _enumResult?.Add(d);
        }
        catch { }
        return 1; // DIENUM_CONTINUE
    }

    public static List<DeviceInfo> Enumerate()
    {
        var list = new List<DeviceInfo>();
        if (!Init()) return list;
        _enumResult = list;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int, int>)Vtbl(_di)[4];
        int hr = fn(_di, DI8DEVCLASS_GAMECTRL, _enumCallbackPtr, IntPtr.Zero, DIEDFL_ATTACHEDONLY);
        _enumResult = null;
        if (hr < 0) LastError = $"EnumDevices 0x{hr:X8}";
        return list;
    }

    // --- data format (c_dfDIJoystick2 without the velocity/acceleration/force parts) ------------
    // dinput8.dll doesn't export c_dfDIJoystick2 (it lives in a static .lib), so it's built here.

    static IntPtr _format, _formatObjs;
    static readonly List<IntPtr> _guidMem = new();

    static IntPtr GuidPtr(Guid g)
    {
        var p = Marshal.AllocHGlobal(16);
        *(Guid*)p = g;
        _guidMem.Add(p);
        return p;
    }

    static void BuildDataFormat()
    {
        if (_format != IntPtr.Zero) return;
        const uint ANY = 0x00FFFF00, OPTIONAL = 0x80000000, AXIS = 0x3, POV = 0x10, BUTTON = 0xC, ASPECTPOSITION = 0x100;
        var objs = new List<(IntPtr guid, uint ofs, uint type, uint flags)>();
        var axisGuids = new[] { GUID_XAxis, GUID_YAxis, GUID_ZAxis, GUID_RxAxis, GUID_RyAxis, GUID_RzAxis, GUID_Slider, GUID_Slider };
        for (int i = 0; i < AxisCount; i++) objs.Add((GuidPtr(axisGuids[i]), (uint)(i * 4), OPTIONAL | AXIS | ANY, ASPECTPOSITION));
        for (int i = 0; i < PovCount; i++) objs.Add((GuidPtr(GUID_POV), (uint)(32 + i * 4), OPTIONAL | POV | ANY, 0));
        for (int i = 0; i < ButtonCount; i++) objs.Add((IntPtr.Zero, (uint)(48 + i), OPTIONAL | BUTTON | ANY, 0));

        // DIOBJECTDATAFORMAT on x64: const GUID* (8), dwOfs, dwType, dwFlags, padding = 24 bytes.
        _formatObjs = Marshal.AllocHGlobal(24 * objs.Count);
        for (int i = 0; i < objs.Count; i++)
        {
            byte* o = (byte*)_formatObjs + i * 24;
            *(IntPtr*)o = objs[i].guid;
            *(uint*)(o + 8) = objs[i].ofs;
            *(uint*)(o + 12) = objs[i].type;
            *(uint*)(o + 16) = objs[i].flags;
            *(uint*)(o + 20) = 0;
        }
        // DIDATAFORMAT on x64: dwSize, dwObjSize, dwFlags, dwDataSize, dwNumObjs, padding, rgodf (8) = 32 bytes.
        _format = Marshal.AllocHGlobal(32);
        byte* f = (byte*)_format;
        *(uint*)f = 32; *(uint*)(f + 4) = 24; *(uint*)(f + 8) = 1 /* DIDF_ABSAXIS */;
        *(uint*)(f + 12) = StateSize; *(uint*)(f + 16) = (uint)objs.Count; *(uint*)(f + 20) = 0;
        *(IntPtr*)(f + 24) = _formatObjs;
    }

    // --- one opened device ----------------------------------------------------------------------

    public sealed class Device : IDisposable
    {
        public readonly DeviceInfo Info;
        public bool ForceFeedback { get; private set; }
        public int Axes, Buttons, Povs;
        public bool Exclusive { get; private set; }
        public string Status = "";

        // Latest state. Axes are 0..65535 (absent axes read 0 or 32767, depending on the driver).
        public readonly int[] Axis = new int[AxisCount];
        // POV in hundredths of a degree (0 = up, 9000 = right), -1 = centered.
        public readonly int[] Pov = new int[PovCount];
        public readonly bool[] Button = new bool[ButtonCount];

        IntPtr _dev, _state, _hwnd;
        IntPtr _constEffect, _damperEffect, _effectMem;

        Device(DeviceInfo info) { Info = info; }

        public static Device Open(DeviceInfo info, IntPtr hwnd, bool exclusive)
        {
            if (!Init()) return null;
            var d = new Device(info) { _hwnd = hwnd };
            if (!d.Create(exclusive)) { d.Dispose(); return null; }
            return d;
        }

        bool Create(bool exclusive)
        {
            IntPtr dev;
            Guid g = Info.Instance;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)Vtbl(_di)[3];
            int hr = create(_di, &g, &dev, IntPtr.Zero);
            if (hr < 0) { Status = LastError = $"CreateDevice 0x{hr:X8}"; return false; }
            _dev = dev;
            _state = Marshal.AllocHGlobal(StateSize);

            // DIDEVCAPS: dwSize, dwFlags, dwDevType, dwAxes, dwButtons, dwPOVs, ...
            uint* caps = stackalloc uint[11];
            caps[0] = 44;
            if (Call(3, (IntPtr)caps) >= 0)
            {
                ForceFeedback = (caps[1] & 0x100) != 0;
                Axes = (int)caps[3]; Buttons = (int)caps[4]; Povs = (int)caps[5];
            }

            hr = Call(11, _format);
            if (hr < 0) { Status = LastError = $"SetDataFormat 0x{hr:X8}"; return false; }

            // All axes 0..65535, like winmm, whatever the device reports natively.
            int* range = stackalloc int[6];
            range[0] = 24; range[1] = 16; range[2] = 0; range[3] = 0 /* DIPH_DEVICE */; range[4] = 0; range[5] = RawMax;
            SetProperty(4 /* DIPROP_RANGE */, (IntPtr)range);

            if (!SetCooperative(exclusive)) return false;
            Acquire();
            return true;
        }

        int Call(int slot, IntPtr arg)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl(_dev)[slot];
            return fn(_dev, arg);
        }

        int SetProperty(int prop, IntPtr header)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Vtbl(_dev)[6];
            return fn(_dev, (IntPtr)prop, header);
        }

        bool SetCooperative(bool exclusive)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)Vtbl(_dev)[13];
            int hr = fn(_dev, _hwnd, (exclusive ? DISCL_EXCLUSIVE : DISCL_NONEXCLUSIVE) | DISCL_BACKGROUND);
            if (hr < 0) { Status = LastError = $"SetCooperativeLevel 0x{hr:X8}"; return false; }
            Exclusive = exclusive;
            return true;
        }

        int Acquire()
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(_dev)[7];
            return fn(_dev);
        }

        void Unacquire()
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(_dev)[8];
            fn(_dev);
        }

        // Reads the current state into Axis/Pov/Button. False if the device is gone.
        public bool Read()
        {
            if (_dev == IntPtr.Zero) return false;
            var poll = (delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(_dev)[25];
            var get = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)Vtbl(_dev)[9];
            poll(_dev);
            int hr = get(_dev, StateSize, _state);
            if (hr == DIERR_INPUTLOST || hr == DIERR_NOTACQUIRED)
            {
                int ahr = Acquire();
                if (ahr < 0) { Status = $"acquire 0x{ahr:X8}"; return false; }
                poll(_dev);
                hr = get(_dev, StateSize, _state);
            }
            if (hr < 0) { Status = $"read 0x{hr:X8}"; return false; }
            int* s = (int*)_state;
            for (int i = 0; i < AxisCount; i++) Axis[i] = s[i];
            for (int i = 0; i < PovCount; i++) { uint p = (uint)s[8 + i]; Pov[i] = (p & 0xFFFF) == 0xFFFF ? -1 : (int)p; }
            byte* b = (byte*)_state + 48;
            for (int i = 0; i < ButtonCount; i++) Button[i] = (b[i] & 0x80) != 0;
            Status = "ok";
            return true;
        }

        // --- force feedback -------------------------------------------------------------------

        // Switches to exclusive access and starts a constant force + damper. False (with Status) if
        // another program still holds the device exclusively.
        public bool StartForceFeedback()
        {
            if (!ForceFeedback) { Status = "device has no force feedback"; return false; }
            if (_constEffect != IntPtr.Zero) return true;
            Unacquire();
            if (!Exclusive && !SetCooperative(true)) { SetCooperative(false); Acquire(); return false; }

            // The wheel's own centering spring would fight our forces.
            int* autocenter = stackalloc int[5];
            autocenter[0] = 20; autocenter[1] = 16; autocenter[2] = 0; autocenter[3] = 0; autocenter[4] = 0; // DIPROPAUTOCENTER_OFF
            SetProperty(9 /* DIPROP_AUTOCENTER */, (IntPtr)autocenter);

            int hr = Acquire();
            if (hr < 0) { Status = $"exclusive acquire 0x{hr:X8} (held by another program?)"; SetCooperative(false); Acquire(); return false; }

            // Effect memory stays allocated for the device's lifetime: DIEFFECT (80) + axes (4) +
            // direction (4) + DICONSTANTFORCE (4) + DICONDITION (24).
            _effectMem = Marshal.AllocHGlobal(256);
            new Span<byte>((void*)_effectMem, 256).Clear();
            _constEffect = CreateEffect(GUID_ConstantForce, 4, false);
            _damperEffect = CreateEffect(GUID_Damper, 24, true);
            if (_constEffect == IntPtr.Zero) { StopForceFeedback(); return false; }
            Status = "force feedback on";
            return true;
        }

        IntPtr Effect(bool condition) => _effectMem + (condition ? 128 : 0);

        IntPtr CreateEffect(Guid type, int paramSize, bool condition)
        {
            // Two DIEFFECT blocks: constant force at 0, condition at 128; their parameters at +96.
            byte* e = (byte*)Effect(condition);
            uint* axes = (uint*)(e + 80);
            int* dir = (int*)(e + 84);
            byte* param = e + 96;
            axes[0] = 0;  // DIJOFS_X: the steering axis
            dir[0] = 0;
            *(uint*)e = 80;                         // dwSize
            *(uint*)(e + 4) = 0x10 | 0x2;           // DIEFF_CARTESIAN | DIEFF_OBJECTOFFSETS
            *(uint*)(e + 8) = 0xFFFFFFFF;           // dwDuration INFINITE
            *(uint*)(e + 12) = 0;                   // dwSamplePeriod
            *(uint*)(e + 16) = 10000;               // dwGain
            *(uint*)(e + 20) = 0xFFFFFFFF;          // dwTriggerButton DIEB_NOTRIGGER
            *(uint*)(e + 24) = 0;                   // dwTriggerRepeatInterval
            *(uint*)(e + 28) = 1;                   // cAxes
            *(IntPtr*)(e + 32) = (IntPtr)axes;      // rgdwAxes
            *(IntPtr*)(e + 40) = (IntPtr)dir;       // rglDirection
            *(IntPtr*)(e + 48) = IntPtr.Zero;       // lpEnvelope
            *(uint*)(e + 56) = (uint)paramSize;     // cbTypeSpecificParams
            *(IntPtr*)(e + 64) = (IntPtr)param;     // lpvTypeSpecificParams
            *(uint*)(e + 72) = 0;                   // dwStartDelay
            if (condition)
            {
                int* c = (int*)param; // DICONDITION: offset, +coef, -coef, +sat, -sat, deadband
                c[0] = 0; c[1] = 0; c[2] = 0; c[3] = 10000; c[4] = 10000; c[5] = 0;
            }
            else *(int*)param = 0;

            IntPtr effect;
            Guid g = type;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, IntPtr*, IntPtr, int>)Vtbl(_dev)[18];
            int hr = create(_dev, &g, (IntPtr)e, &effect, IntPtr.Zero);
            if (hr < 0) { Status = LastError = $"CreateEffect 0x{hr:X8}"; return IntPtr.Zero; }
            var start = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Vtbl(effect)[7];
            hr = start(effect, 1, 0);
            if (hr < 0) Status = LastError = $"StartEffect 0x{hr:X8}";
            return effect;
        }

        int _sentConst = int.MinValue, _sentDamper = int.MinValue;

        // magnitude -10000..10000 (DirectInput units, sign as the device defines it), damper 0..10000.
        // False if the device stopped accepting updates (lost); call StartForceFeedback again.
        public bool SetForces(int magnitude, int damper)
        {
            if (_constEffect == IntPtr.Zero) return false;
            bool ok = true;
            // Every update is a USB transfer; skip tiny changes.
            if (_sentConst == int.MinValue || Math.Abs(magnitude - _sentConst) >= 10 || (magnitude == 0) != (_sentConst == 0))
            {
                *(int*)((byte*)Effect(false) + 96) = magnitude;
                ok &= Update(_constEffect, Effect(false));
                _sentConst = magnitude;
            }
            if (_damperEffect != IntPtr.Zero && (_sentDamper == int.MinValue || Math.Abs(damper - _sentDamper) >= 50 || (damper == 0) != (_sentDamper == 0)))
            {
                int* c = (int*)((byte*)Effect(true) + 96);
                c[1] = damper; c[2] = damper;
                ok &= Update(_damperEffect, Effect(true));
                _sentDamper = damper;
            }
            if (!ok) { _sentConst = _sentDamper = int.MinValue; StopForceFeedback(); }
            return ok;
        }

        bool Update(IntPtr effect, IntPtr parameters)
        {
            var set = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Vtbl(effect)[6];
            int hr = set(effect, parameters, 0x100 /* DIEP_TYPESPECIFICPARAMS */);
            if (hr >= 0) return true;
            Status = LastError = $"effect update 0x{hr:X8}";
            return false;
        }

        // Releases the effects and exclusive access (input keeps working, non-exclusive).
        public void StopForceFeedback()
        {
            foreach (var e in new[] { _constEffect, _damperEffect })
            {
                if (e == IntPtr.Zero) continue;
                try
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(e)[8])(e);   // Stop
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(e)[2])(e);  // Release
                }
                catch { }
            }
            _constEffect = _damperEffect = IntPtr.Zero;
            _sentConst = _sentDamper = int.MinValue;
            if (_effectMem != IntPtr.Zero) { Marshal.FreeHGlobal(_effectMem); _effectMem = IntPtr.Zero; }
            if (_dev != IntPtr.Zero && Exclusive)
            {
                Unacquire();
                SetCooperative(false);
                Acquire();
            }
        }

        public bool ForceFeedbackRunning => _constEffect != IntPtr.Zero;

        public void Dispose()
        {
            StopForceFeedback();
            if (_dev != IntPtr.Zero)
            {
                try
                {
                    Unacquire();
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(_dev)[2])(_dev);
                }
                catch { }
                _dev = IntPtr.Zero;
            }
            if (_state != IntPtr.Zero) { Marshal.FreeHGlobal(_state); _state = IntPtr.Zero; }
        }
    }
}
