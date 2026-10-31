// AL-SeRry
// DualSense / DualSense Edge -> per-stick min/max deadzone + C# script -> virtual Xbox 360 pad (ViGEmBus).
// Raw HID, one blocking read per report, no polling loop. Compiles with the .NET Framework 4.8 csc.exe (C# 5).
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.CSharp;

namespace ControllerUnit
{
    // ---------------------------------------------------------------- script API
    public class State
    {
        public float LX, LY, RX, RY, LT, RT;
        public bool Cross, Circle, Square, Triangle, L1, R1, L3, R3;
        public bool Share, Options, PS, Touchpad, Up, Down, Left, Right;
        public float RawLX, RawLY, RawRX, RawRY;
        public double T, Dt;
    }

    public abstract class ScriptBase
    {
        public virtual void Update(State s) { }

        // Values of the "// @param" lines of the script; the bar / number box of the script row edit them live.
        public Dictionary<string, float> Params;
        protected float P(string name)
        {
            float v;
            return (Params != null && Params.TryGetValue(name, out v)) ? v : 0f;
        }

        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // Circular jitter: adds a small rotating offset (amplitude 0..1, frequency in Hz, t = s.T).
        public static void Jitter(ref float x, ref float y, float amplitude, float hz, double t)
        {
            double a = 2.0 * Math.PI * hz * t;
            x = Clamp(x + amplitude * (float)Math.Cos(a), -1f, 1f);
            y = Clamp(y + amplitude * (float)Math.Sin(a), -1f, 1f);
        }

        public static void Curve(ref float x, ref float y, float power)
        {
            float m = (float)Math.Sqrt(x * x + y * y);
            if (m <= 0f) { x = 0f; y = 0f; return; }
            float k = (float)Math.Pow(Math.Min(m, 1f), power) / m;
            x *= k; y *= k;
        }
    }

    // ---------------------------------------------------------------- native
    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public static readonly IntPtr INVALID = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS caps);

        [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, ref SP_DEVINFO_DATA info);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        // service name of a device node (CM_DRP_SERVICE = 5) and its parent: used to recognise ViGEmBus virtual pads
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] public static extern int CM_Get_DevNode_Registry_PropertyW(int inst, int prop, out int type, byte[] buf, ref int len, int flags);
        [DllImport("cfgmgr32.dll")] public static extern int CM_Get_Parent(out int parent, int inst, int flags);
        [DllImport("kernel32.dll")] public static extern bool CancelIoEx(IntPtr h, IntPtr ov);

        // mouse and keyboard output (M&K mode and the on-screen keyboard)
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, ref INPUT i, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr h, int index, int value);

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;      // 1 = keyboard
            [FieldOffset(8)] public ushort vk;
            [FieldOffset(10)] public ushort scan;
            [FieldOffset(12)] public uint flags;
            [FieldOffset(16)] public uint time;
            [FieldOffset(24)] public IntPtr extra;
        }

        const uint KEYUP = 2, UNICODE = 4, EXTENDED = 1;
        static void Key(ushort vk, ushort scan, uint flags)
        {
            INPUT i = new INPUT(); i.type = 1; i.vk = vk; i.scan = scan; i.flags = flags;
            SendInput(1, ref i, 40);
        }
        // types any text, whatever the keyboard layout of the target program (Arabic included)
        public static void TypeText(string s)
        {
            foreach (char c in s) { Key(0, c, UNICODE); Key(0, c, UNICODE | KEYUP); }
        }
        public static void PressVk(ushort vk, bool extended)
        {
            Key(vk, 0, extended ? EXTENDED : 0); Key(vk, 0, (extended ? EXTENDED : 0) | KEYUP);
        }
        static bool IsExt(ushort v) { return (v >= 0x21 && v <= 0x28) || v == 0x2D || v == 0x2E; }
        // Alt+Tab keeps Alt held, like a real keyboard, so the window switcher with the thumbnails stays open: every further press of
        // the button moves to the next window (Alt+Shift+Tab: back), arrow keys / Enter sent meanwhile work inside the switcher, and
        // four seconds after the last press Alt is let go, which picks the highlighted window.
        const int AltHoldMs = 4000;
        static bool altHeld;
        static System.Threading.Timer altTimer;
        static readonly object altLock = new object();

        static void ArmAlt()
        {
            if (altTimer == null) altTimer = new System.Threading.Timer(delegate { ReleaseAlt(); }, null, AltHoldMs, System.Threading.Timeout.Infinite);
            else altTimer.Change(AltHoldMs, System.Threading.Timeout.Infinite);
        }

        public static void ReleaseAlt()
        {
            lock (altLock) { if (altHeld) { Key(0x12, 0, KEYUP); altHeld = false; } }
        }

        static void AltTab(ushort[] vks)
        {
            lock (altLock)
            {
                if (!altHeld) { Key(0x12, 0, 0); altHeld = true; }
                bool shift = Array.IndexOf(vks, (ushort)0x10) >= 0;
                if (shift) Key(0x10, 0, 0);
                Key(0x09, 0, 0); Key(0x09, 0, KEYUP);
                if (shift) Key(0x10, 0, KEYUP);
                ArmAlt();
            }
        }

        // a key command such as Ctrl+C: all keys down in order, then up in reverse order
        public static void PressCombo(ushort[] vks)
        {
            if (Array.IndexOf(vks, (ushort)0x12) >= 0 && Array.IndexOf(vks, (ushort)0x09) >= 0) { AltTab(vks); return; }
            lock (altLock) { if (altHeld) ArmAlt(); }   // another key while the switcher is open: keep Alt held a little longer
            foreach (ushort v in vks) Key(v, 0, IsExt(v) ? EXTENDED : 0);
            for (int i = vks.Length - 1; i >= 0; i--) Key(vks[i], 0, (IsExt(vks[i]) ? EXTENDED : 0) | KEYUP);
        }
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr ov);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibrary(string path);

        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
        [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int L, R, T, B; }
    }

    // ---------------------------------------------------------------- XInput (Xbox One, Series X|S, GameSir in Xbox mode)
    static class XIn
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
        [StructLayout(LayoutKind.Sequential)]
        public struct STATE { public uint dwPacketNumber; public GAMEPAD Gamepad; }

        // ordinal 100 = XInputGetStateEx: same as XInputGetState but also reports the Xbox (guide) button
        [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern int GetEx(int user, out STATE s);
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int GetPlain(int user, out STATE s);
        static bool noEx;

        public static int Get(int user, out STATE s)
        {
            try
            {
                if (!noEx) return GetEx(user, out s);
            }
            catch (Exception) { noEx = true; }
            try { return GetPlain(user, out s); }
            catch (Exception) { s = new STATE(); return 1167; }   // no XInput on this machine
        }
    }

    // ---------------------------------------------------------------- ViGEm
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct XUSB_REPORT
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    // DualShock 4 input report (what a Sony pad sends): sticks 0..255 (128 = centre, 0 = up), d-pad hat in the low
    // 4 bits of wButtons, bSpecial bit0 = PS button, bit1 = touchpad click.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DS4_REPORT
    {
        public byte bThumbLX, bThumbLY, bThumbRX, bThumbRY;
        public ushort wButtons;
        public byte bSpecial, bTriggerL, bTriggerR;
    }

    sealed class Pad : IDisposable
    {
        const uint OK = 0x20000000;
        [DllImport("ViGEmClient.dll")] static extern IntPtr vigem_alloc();
        [DllImport("ViGEmClient.dll")] static extern void vigem_free(IntPtr c);
        [DllImport("ViGEmClient.dll")] static extern uint vigem_connect(IntPtr c);
        [DllImport("ViGEmClient.dll")] static extern void vigem_disconnect(IntPtr c);
        [DllImport("ViGEmClient.dll")] static extern IntPtr vigem_target_x360_alloc();
        [DllImport("ViGEmClient.dll")] static extern void vigem_target_free(IntPtr t);
        [DllImport("ViGEmClient.dll")] static extern uint vigem_target_add(IntPtr c, IntPtr t);
        [DllImport("ViGEmClient.dll")] static extern uint vigem_target_remove(IntPtr c, IntPtr t);
        [DllImport("ViGEmClient.dll")] static extern uint vigem_target_x360_update(IntPtr c, IntPtr t, XUSB_REPORT r);
        [DllImport("ViGEmClient.dll")] static extern IntPtr vigem_target_ds4_alloc();
        [DllImport("ViGEmClient.dll")] static extern uint vigem_target_ds4_update(IntPtr c, IntPtr t, DS4_REPORT r);

        static bool loaded;
        IntPtr client, target;
        public readonly bool IsDs4;

        // ViGEmClient.dll is embedded in the exe; unpack once and load it by full path.
        static void EnsureDll()
        {
            if (loaded) return;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControllerUnitOf5belan");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "ViGEmClient.dll");
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ViGEmClient.dll"))
            {
                if (s == null) throw new Exception("ViGEmClient.dll is missing from the exe");
                if (!File.Exists(file) || new FileInfo(file).Length != s.Length)
                {
                    using (FileStream f = File.Create(file)) s.CopyTo(f);
                }
            }
            if (Native.LoadLibrary(file) == IntPtr.Zero) throw new Exception("could not load ViGEmClient.dll");
            loaded = true;
        }

        [DllImport("ViGEmClient.dll")] static extern void vigem_target_set_vid(IntPtr t, ushort vid);
        [DllImport("ViGEmClient.dll")] static extern void vigem_target_set_pid(IntPtr t, ushort pid);

        public Pad(bool ds4, ushort ds4Pid)
        {
            EnsureDll();
            IsDs4 = ds4;
            client = vigem_alloc();
            if (client == IntPtr.Zero) throw new Exception("vigem_alloc failed");
            uint r = vigem_connect(client);
            if (r != OK) { vigem_free(client); client = IntPtr.Zero; throw new Exception("ViGEmBus driver not found (0x" + r.ToString("X") + ")"); }
            target = ds4 ? vigem_target_ds4_alloc() : vigem_target_x360_alloc();
            if (ds4) { vigem_target_set_vid(target, 0x054C); vigem_target_set_pid(target, ds4Pid); }
            r =vigem_target_add(client, target);
            if (r != OK) { vigem_target_free(target); vigem_disconnect(client); vigem_free(client); throw new Exception("could not add virtual pad (0x" + r.ToString("X") + ")"); }
        }

        public void Send(XUSB_REPORT r) { vigem_target_x360_update(client, target, r); }
        public void SendDs4(DS4_REPORT r) { vigem_target_ds4_update(client, target, r); }

        // everything released, sticks centred
        public void SendNeutral()
        {
            if (IsDs4)
            {
                DS4_REPORT d = new DS4_REPORT();
                d.bThumbLX = d.bThumbLY = d.bThumbRX = d.bThumbRY = 128;
                d.wButtons = 8;   // d-pad hat: none
                SendDs4(d);
            }
            else Send(new XUSB_REPORT());
        }

        public void Dispose()
        {
            if (client == IntPtr.Zero) return;
            vigem_target_remove(client, target);
            vigem_target_free(target);
            vigem_disconnect(client);
            vigem_free(client);
            client = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- engine
    sealed class Cfg
    {
        public readonly float LMin, LMax, RMin, RMax;
        public readonly int Curve;   // right stick response: 0 Standard, 1 Linear, 2 Dynamic
        public readonly float Dyn;   // Dynamic level 0..1 (0 = Standard)
        public readonly float Ads;   // right stick multiplier while L2 is held (ADS sensitivity)
        public Cfg(float a, float b, float c, float d) : this(a, b, c, d, 1, 0.5f, 1f) { }
        public readonly bool AdsL1;  // the ADS button is L1 instead of L2
        public Cfg(float a, float b, float c, float d, int curve, float dyn, float ads) : this(a, b, c, d, curve, dyn, ads, false) { }
        public Cfg(float a, float b, float c, float d, int curve, float dyn, float ads, bool adsL1)
        { LMin = a; LMax = b; RMin = c; RMax = d; Curve = curve; Dyn = dyn; Ads = ads; AdsL1 = adsL1; }
    }

    // The three "Aim Response Curve Types" of the newer Call of Duty games, applied to the right stick
    // (stick tilt 0..1 -> output 0..1). The games describe them as:
    //   Standard: simple power curve from aim stick to aim rate     Linear: aim stick maps directly to aim rate
    //   Dynamic:  reverse S-curve for fine aim rate control
    // The exact in-game formulas are not published, so these are approximations of those descriptions.
    static class AimCurve
    {
        public const float StdPower = 1.5f;   // Standard: output = tilt ^ StdPower
        const float RevS = 0.6f;              // Dynamic at level 1: depth of the reverse S (0 = none, 1 = flat middle)

        // m = stick tilt 0..1, dyn = Dynamic level 0..1 (0 = identical to Standard)
        public static float Shape(int type, float dyn, float m)
        {
            if (m <= 0f) return 0f;
            if (m >= 1f) return 1f;
            if (type == 1) return m;
            float std = (float)Math.Pow(m, StdPower);
            if (type == 0) return std;
            float rev = m + RevS * (float)Math.Sin(2.0 * Math.PI * m) / (float)(2.0 * Math.PI);
            return std + (rev - std) * dyn;
        }

        public static void Apply(ref float x, ref float y, int type, float dyn)
        {
            if (type == 1) return;
            float m = (float)Math.Sqrt(x * x + y * y);
            if (m <= 0f) return;
            float k = Shape(type, dyn, Math.Min(1f, m)) / m;
            x *= k; y *= k;
        }
    }

    sealed class Engine
    {
        // chiaki-ng gets to see ONLY our virtual pad (SDL_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT), so whichever physical pad
        // is plugged in (DualSense, DualShock 4, Xbox, GameSir) never reaches the game twice.
        public string VisibleToChiaki
        {
            get { return Ds4Mode ? "0x054C/0x" + VirtualPid.ToString("X4") : "0x045E/0x028E"; }
        }
        public ushort VirtualPid = 0x05C4;   // virtual DualShock 4 product id; differs from a physical DualShock 4 in use
        public volatile string Input = "auto";   // auto | ds5 | ds4 | xinput
        volatile bool reselect;
        IntPtr curHandle;
        public ushort PhysDs4Pid;
        const float MinGap = 0.05f;
        const float AdsStart = 0.25f;   // L2 pressed at least this far = aiming down sights

        public volatile Cfg Cfg = new Cfg(0.10f, 1f, 0.10f, 1f);
        public volatile ScriptItem[] Scripts = new ScriptItem[0];
        public volatile bool Connected, PadOn;
        public volatile string DeviceName = "", PadError;
        public float RawLX, RawLY, RawRX, RawRY, OutLX, OutLY, OutRX, OutRY;
        public int ReportsPerSec;

        Pad pad;
        readonly object padLock = new object();
        volatile bool run = true;
        XUSB_REPORT last;
        DS4_REPORT lastDs4;
        bool haveLast;
        public volatile bool Ds4Mode = true;   // true: virtual pad is a DualShock 4 (touchpad works), false: Xbox 360
        readonly Stopwatch clock = Stopwatch.StartNew();
        double prevT;
        int count;
        long countStart;
        readonly State st = new State();

        public void Start()
        {
            Thread t = new Thread(ReadLoop);
            t.IsBackground = true;
            t.Priority = ThreadPriority.Highest;
            t.Name = "hid-reader";
            t.Start();
        }

        public void Stop() { run = false; SetPad(false); }

        public bool SetPad(bool on)
        {
            lock (padLock)
            {
                if (on && pad == null)
                {
                    try
                    {
                        // a physical DualShock 4 keeps its own id: the virtual one takes the other known id
                        ushort phys = 0;
                        foreach (HidDev d in EnumHid()) if (!d.Virtual && d.Kind == 4) phys = d.Pid;
                        VirtualPid = phys == 0x05C4 ? (ushort)0x09CC : (ushort)0x05C4;
                        pad = new Pad(Ds4Mode, VirtualPid); PadOn = true; PadError = null; haveLast = false;
                    }
                    catch (Exception e) { PadError = e.Message; PadOn = false; return false; }
                }
                else if (!on && pad != null)
                {
                    try { pad.SendNeutral(); } catch (Exception) { }
                    pad.Dispose();
                    pad = null;
                    PadOn = false;
                }
            }
            return true;
        }

        // ---- device discovery
        // Kind 5 = DualSense / DualSense Edge, Kind 4 = DualShock 4
        public sealed class HidDev { public string Path, Name; public int InLen, OutLen, Kind; public ushort Pid; public bool Virtual; }

        // Our own virtual pads (and any other ViGEm pad) hang below the ViGEmBus driver in the device tree.
        static bool IsVirtual(int devInst)
        {
            int cur = devInst;
            for (int depth = 0; depth < 8; depth++)
            {
                byte[] buf = new byte[256];
                int len = buf.Length, type;
                if (Native.CM_Get_DevNode_Registry_PropertyW(cur, 0x5, out type, buf, ref len, 0) == 0 && len > 2)
                {
                    string svc = Encoding.Unicode.GetString(buf, 0, len - 2);
                    if (svc.Equals("ViGEmBus", StringComparison.OrdinalIgnoreCase)) return true;
                }
                int parent;
                if (Native.CM_Get_Parent(out parent, cur, 0) != 0) break;
                cur = parent;
            }
            return false;
        }

        // every Sony pad Windows shows as HID, virtual ones included (flagged)
        public static List<HidDev> EnumHid()
        {
            List<HidDev> found = new List<HidDev>();
            Guid g; Native.HidD_GetHidGuid(out g);
            IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == Native.INVALID) return found;
            try
            {
                for (int i = 0; ; i++)
                {
                    Native.SP_DEVICE_INTERFACE_DATA d = new Native.SP_DEVICE_INTERFACE_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    int need;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        Native.SP_DEVINFO_DATA di = new Native.SP_DEVINFO_DATA();
                        di.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out need, ref di)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        IntPtr h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h == Native.INVALID) continue;
                        try
                        {
                            Native.HIDD_ATTRIBUTES a = new Native.HIDD_ATTRIBUTES();
                            a.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                            if (!Native.HidD_GetAttributes(h, ref a) || a.VendorID != 0x054C) continue;
                            int kind; string name;
                            if (a.ProductID == 0x0CE6 || a.ProductID == 0x0DF2) { kind = 5; name = a.ProductID == 0x0DF2 ? "DualSense Edge" : "DualSense"; }
                            else if (a.ProductID == 0x05C4 || a.ProductID == 0x09CC || a.ProductID == 0x0BA0) { kind = 4; name = "DualShock 4"; }
                            else continue;
                            IntPtr pp;
                            if (!Native.HidD_GetPreparsedData(h, out pp)) continue;
                            Native.HIDP_CAPS caps;
                            Native.HidP_GetCaps(pp, out caps);
                            Native.HidD_FreePreparsedData(pp);
                            if (caps.UsagePage != 1 || caps.Usage != 5) continue;
                            HidDev dev = new HidDev();
                            dev.Path = path; dev.Name = name; dev.Kind = kind; dev.Pid = a.ProductID;
                            dev.InLen = caps.InputReportByteLength; dev.OutLen = caps.OutputReportByteLength;
                            dev.Virtual = IsVirtual(di.DevInst);
                            found.Add(dev);
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return found;
        }

        static HidDev FindHid(int wanted)
        {
            foreach (HidDev d in EnumHid())
                if (!d.Virtual && (wanted == 0 || d.Kind == wanted)) return d;   // never read our own virtual pad
            return null;
        }

        // Bluetooth DualSense starts in a 10-byte "simple" report; any valid 0x31 output report switches it to the full one.
        static void BluetoothInit(IntPtr h, int outLen)
        {
            byte[] o = new byte[78];
            o[0] = 0x31; o[2] = 0x10;
            uint crc = 0xFFFFFFFF;
            crc = Crc(crc, 0xA2);
            for (int i = 0; i < 74; i++) crc = Crc(crc, o[i]);
            crc = ~crc;
            o[74] = (byte)crc; o[75] = (byte)(crc >> 8); o[76] = (byte)(crc >> 16); o[77] = (byte)(crc >> 24);
            int w;
            Native.WriteFile(h, o, o.Length, out w, IntPtr.Zero);
        }

        static uint Crc(uint c, byte b)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            return c;
        }

        // Xbox One / Series X|S and GameSir (in Xbox mode) come through XInput. An Xbox 360 virtual pad of ours would be
        // read back by XInput as well, so XInput is only used while our virtual pad is a DualShock 4 (or off).
        bool XInputAllowed() { lock (padLock) { return pad == null || pad.IsDs4; } }

        static int FindXInput()
        {
            for (int i = 0; i < 4; i++)
            {
                XIn.STATE s;
                if (XIn.Get(i, out s) == 0) return i;
            }
            return -1;
        }

        void NeutralPad()
        {
            if (mkActive && !MkMode) ReleaseMouse();
            lock (padLock) { if (pad != null) { try { pad.SendNeutral(); } catch (Exception) { } haveLast = false; } }
        }

        // chosen in Settings: auto (DualSense, then DualShock 4, then XInput), ds5, ds4 or xinput
        public void SetInput(string v)
        {
            Input = v;
            reselect = true;
            IntPtr h = curHandle;
            if (h != IntPtr.Zero) Native.CancelIoEx(h, IntPtr.Zero);   // wakes the blocking read
        }

        void ReadLoop()
        {
            while (run)
            {
                reselect = false;
                string pref = Input;
                HidDev dev = null;
                if (pref == "auto" || pref == "ds5") dev = FindHid(5);
                if (dev == null && (pref == "auto" || pref == "ds4")) dev = FindHid(4);
                if (dev != null) { RunHid(dev); continue; }
                if ((pref == "auto" || pref == "xinput") && XInputAllowed())
                {
                    int idx = FindXInput();
                    if (idx >= 0) { RunXInput(idx); continue; }
                }
                Connected = false;
                Thread.Sleep(500);
            }
        }

        void RunHid(HidDev dev)
        {
            IntPtr h = Native.CreateFile(dev.Path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Native.INVALID) h = Native.CreateFile(dev.Path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Native.INVALID) { Thread.Sleep(500); return; }

            bool bt = (dev.Kind == 5 && dev.InLen >= 78) || (dev.Kind == 4 && dev.InLen != 64);
            if (dev.Kind == 5 && bt) BluetoothInit(h, dev.OutLen);
            DeviceName = dev.Name + (bt ? " (Bluetooth)" : " (USB)");
            if (dev.Kind == 4) PhysDs4Pid = dev.Pid;
            Connected = true;
            countStart = clock.ElapsedTicks;
            count = 0;
            curHandle = h;

            byte[] buf = new byte[dev.InLen];
            while (run && !reselect)
            {
                int n;
                if (!Native.ReadFile(h, buf, buf.Length, out n, IntPtr.Zero)) break;
                if (n <= 0) continue;
                if (dev.Kind == 5) OnReport(buf, n); else OnDs4Report(buf, n);
            }
            curHandle = IntPtr.Zero;
            PhysDs4Pid = 0;
            Connected = false;
            Native.CloseHandle(h);
            NeutralPad();
        }

        // XInput has no blocking read: poll every millisecond (timer resolution raised to 1 ms meanwhile)
        void RunXInput(int idx)
        {
            DeviceName = "Xbox / GameSir controller (XInput)";
            Connected = true;
            countStart = clock.ElapsedTicks;
            count = 0;
            Native.timeBeginPeriod(1);
            try
            {
                uint lastPacket = 0;
                bool first = true;
                while (run && !reselect && XInputAllowed())
                {
                    XIn.STATE s;
                    if (XIn.Get(idx, out s) != 0) break;   // unplugged
                    bool changed = first || s.dwPacketNumber != lastPacket;
                    first = false; lastPacket = s.dwPacketNumber;
                    FillXInput(s);
                    Handle(changed);
                    Thread.Sleep(1);
                }
            }
            finally { Native.timeEndPeriod(1); }
            Connected = false;
            NeutralPad();
        }


        // ---- one report in, one report out
        static void Dead(ref float x, ref float y, float min, float max)
        {
            float m = (float)Math.Sqrt(x * x + y * y);
            if (m <= min || m <= 0f) { x = 0f; y = 0f; return; }
            if (max < min + MinGap) max = min + MinGap;
            float k = Math.Min(1f, (m - min) / (max - min)) / m;
            x *= k; y *= k;
        }

        static short ToShort(float v) { return (short)(Math.Max(-1f, Math.Min(1f, v)) * 32767f); }

        static byte Axis8(float v) { return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(128.0 + v * 127.0))); }

        // Same buttons as a Sony pad: touchpad click and Share are separate buttons.
        static DS4_REPORT BuildDs4(State s)
        {
            DS4_REPORT d = new DS4_REPORT();
            d.bThumbLX = Axis8(s.LX); d.bThumbLY = Axis8(-s.LY);   // DS4: 0 = up
            d.bThumbRX = Axis8(s.RX); d.bThumbRY = Axis8(-s.RY);
            int hat = 8;
            if (s.Up && s.Right) hat = 1; else if (s.Right && s.Down) hat = 3; else if (s.Down && s.Left) hat = 5; else if (s.Left && s.Up) hat = 7;
            else if (s.Up) hat = 0; else if (s.Right) hat = 2; else if (s.Down) hat = 4; else if (s.Left) hat = 6;
            int b = hat;
            if (s.Square) b |= 0x10; if (s.Cross) b |= 0x20; if (s.Circle) b |= 0x40; if (s.Triangle) b |= 0x80;
            if (s.L1) b |= 0x100; if (s.R1) b |= 0x200;
            if (s.LT > 0.2f) b |= 0x400; if (s.RT > 0.2f) b |= 0x800;
            if (s.Share) b |= 0x1000; if (s.Options) b |= 0x2000; if (s.L3) b |= 0x4000; if (s.R3) b |= 0x8000;
            d.wButtons = (ushort)b;
            byte sp = 0;
            if (s.PS) sp |= 0x01;
            if (s.Touchpad) sp |= 0x02;
            d.bSpecial = sp;
            d.bTriggerL = (byte)(Math.Max(0f, Math.Min(1f, s.LT)) * 255f);
            d.bTriggerR = (byte)(Math.Max(0f, Math.Min(1f, s.RT)) * 255f);
            return d;
        }

        // what every input backend fills in before Handle() runs (sticks: up = +1, triggers 0..1)
        public sealed class Raw
        {
            public float LX, LY, RX, RY, LT, RT;
            public bool Up, Down, Left, Right, Square, Cross, Circle, Triangle, L1, R1, Share, Options, L3, R3, PS, Touchpad;
            public bool Touch;      // a finger is on the touchpad (DualSense / DualShock 4 only)
            public int TouchX, TouchY;   // 0..1919 / 0..1079 (DualSense), DualShock 4 is 0..1919 / 0..941
        }

        // first finger of the touchpad: 4 bytes at p: [bit7 = no contact | id][x low][x high nibble | y low nibble][y high]
        static void Touch(Raw r, byte[] b, int p)
        {
            r.Touch = (b[p] & 0x80) == 0;
            r.TouchX = b[p + 1] | ((b[p + 2] & 0x0F) << 8);
            r.TouchY = (b[p + 2] >> 4) | (b[p + 3] << 4);
        }
        readonly Raw raw = new Raw();
        public Raw LastRaw { get { return raw; } }   // used by the self-test

        static void Hat(Raw r, int hat)
        {
            r.Up = hat == 0 || hat == 1 || hat == 7;
            r.Right = hat == 1 || hat == 2 || hat == 3;
            r.Down = hat == 3 || hat == 4 || hat == 5;
            r.Left = hat == 5 || hat == 6 || hat == 7;
        }

        // DualSense / DualSense Edge: USB report 0x01 (64 bytes) or Bluetooth report 0x31 (78 bytes)
        void OnReport(byte[] b, int n)
        {
            int c;
            if (b[0] == 0x01 && n >= 64) c = 1;
            else if (b[0] == 0x31 && n >= 78) c = 2;
            else return;
            Raw r = raw;
            r.LX = (b[c] - 127.5f) / 127.5f; r.LY = -(b[c + 1] - 127.5f) / 127.5f;
            r.RX = (b[c + 2] - 127.5f) / 127.5f; r.RY = -(b[c + 3] - 127.5f) / 127.5f;
            r.LT = b[c + 4] / 255f; r.RT = b[c + 5] / 255f;
            byte b0 = b[c + 7], b1 = b[c + 8], b2 = b[c + 9];
            Hat(r, b0 & 0x0F);
            r.Square = (b0 & 0x10) != 0; r.Cross = (b0 & 0x20) != 0; r.Circle = (b0 & 0x40) != 0; r.Triangle = (b0 & 0x80) != 0;
            r.L1 = (b1 & 0x01) != 0; r.R1 = (b1 & 0x02) != 0;
            r.Share = (b1 & 0x10) != 0; r.Options = (b1 & 0x20) != 0; r.L3 = (b1 & 0x40) != 0; r.R3 = (b1 & 0x80) != 0;
            r.PS = (b2 & 0x01) != 0; r.Touchpad = (b2 & 0x02) != 0;
            Touch(r, b, c + 32);
            Handle(true);
        }

        // DualShock 4: report 0x01 (USB 64 bytes, or the 10-byte Bluetooth "simple" report: same first bytes) or Bluetooth 0x11
        void OnDs4Report(byte[] b, int n)
        {
            int c;
            if (b[0] == 0x01 && n >= 10) c = 1;
            else if (b[0] == 0x11 && n >= 14) c = 3;
            else return;
            Raw r = raw;
            r.LX = (b[c] - 127.5f) / 127.5f; r.LY = -(b[c + 1] - 127.5f) / 127.5f;
            r.RX = (b[c + 2] - 127.5f) / 127.5f; r.RY = -(b[c + 3] - 127.5f) / 127.5f;
            byte b0 = b[c + 4], b1 = b[c + 5], b2 = b[c + 6];
            r.LT = b[c + 7] / 255f; r.RT = b[c + 8] / 255f;
            Hat(r, b0 & 0x0F);
            r.Square = (b0 & 0x10) != 0; r.Cross = (b0 & 0x20) != 0; r.Circle = (b0 & 0x40) != 0; r.Triangle = (b0 & 0x80) != 0;
            r.L1 = (b1 & 0x01) != 0; r.R1 = (b1 & 0x02) != 0;
            r.Share = (b1 & 0x10) != 0; r.Options = (b1 & 0x20) != 0; r.L3 = (b1 & 0x40) != 0; r.R3 = (b1 & 0x80) != 0;
            r.PS = (b2 & 0x01) != 0; r.Touchpad = (b2 & 0x02) != 0;
            if (n >= c + 38) Touch(r, b, c + 34); else r.Touch = false;
            Handle(true);
        }

        public void FeedDs4(byte[] b, int n) { OnDs4Report(b, n); }   // self-test entry

        // Xbox One / Series / GameSir: A B X Y become cross / circle / square / triangle. View (Back) becomes the touchpad
        // click (the scoreboard in Call of Duty is on that button on both), Menu = options, the Xbox button = PS.
        void FillXInput(XIn.STATE x)
        {
            Raw r = raw;
            XIn.GAMEPAD g = x.Gamepad;
            ushort w = g.wButtons;
            r.LX = Math.Max(-1f, g.sThumbLX / 32767f); r.LY = Math.Max(-1f, g.sThumbLY / 32767f);   // XInput: up = +
            r.RX = Math.Max(-1f, g.sThumbRX / 32767f); r.RY = Math.Max(-1f, g.sThumbRY / 32767f);
            r.LT = g.bLeftTrigger / 255f; r.RT = g.bRightTrigger / 255f;
            r.Up = (w & 0x0001) != 0; r.Down = (w & 0x0002) != 0; r.Left = (w & 0x0004) != 0; r.Right = (w & 0x0008) != 0;
            r.Options = (w & 0x0010) != 0; r.Touchpad = (w & 0x0020) != 0;
            r.L3 = (w & 0x0040) != 0; r.R3 = (w & 0x0080) != 0; r.L1 = (w & 0x0100) != 0; r.R1 = (w & 0x0200) != 0;
            r.PS = (w & 0x0400) != 0;
            r.Cross = (w & 0x1000) != 0; r.Circle = (w & 0x2000) != 0; r.Square = (w & 0x4000) != 0; r.Triangle = (w & 0x8000) != 0;
            r.Share = false; r.Touch = false;
        }

        // ---- M&K mode: the right stick moves the mouse, Cross = left click, Circle = right click, left stick scrolls.
        // The game gets a neutral pad meanwhile. Two button combos (chosen in Settings) switch the mode / show the keyboard.
        public static readonly string[] BtnNames = { "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right", "Square", "Cross (X)", "Circle", "Triangle",
            "L1", "R1", "Share", "Options", "L3", "R3", "PS", "Touchpad", "L2", "R2" };

        public static string ComboText(int mask)
        {
            List<string> p = new List<string>();
            for (int i = 0; i < BtnNames.Length; i++) if ((mask & (1 << i)) != 0) p.Add(BtnNames[i]);
            return p.Count == 0 ? "not set" : string.Join(" + ", p.ToArray());
        }

        const float MouseSpeed = 1500f;   // pixels per second at full stick
        public volatile bool MkMode;
        public volatile int MkCombo = (1 << 0) | (1 << 5), KbCombo = (1 << 1) | (1 << 5), PadCombo = (1 << 1) | (1 << 7);
        public event Action MkChanged, KeyboardRequested, PadRequested;
        public int[] PadKeys = new int[120];   // controller combo of each panel button (0 = none); pressing it runs the button
        public event Action<int> PadHotkey;
        bool menuActive;                        // the shortcut panel is open: the controller drives it, the game gets a neutral pad
        // keys in M&K mode: 1-4 move the keyboard selection (d-pad), 5 types the selected key (L3), 6 backspace (Square),
        // 7 / 8 caret left / right (L1 / R1), 9 switches the language (L2), 10 Enter (R2)
        public event Action<int> KbKey;
        int navPrev;
        public volatile int CaptureState;   // 0 idle, 1 waiting for the buttons, 2 done (CaptureMask holds them)
        public volatile int CaptureMask;
        int capPeak, prevMask, suppress;
        bool capHeld, mkActive, lDown, rDown;
        double mAccX, mAccY, wheelAcc, mkPrevT;

        public void BeginCapture() { capPeak = 0; capHeld = false; CaptureState = 1; }

        static int MaskOf(Raw r)
        {
            int m = 0;
            if (r.Up) m |= 1 << 0; if (r.Down) m |= 1 << 1; if (r.Left) m |= 1 << 2; if (r.Right) m |= 1 << 3;
            if (r.Square) m |= 1 << 4; if (r.Cross) m |= 1 << 5; if (r.Circle) m |= 1 << 6; if (r.Triangle) m |= 1 << 7;
            if (r.L1) m |= 1 << 8; if (r.R1) m |= 1 << 9; if (r.Share) m |= 1 << 10; if (r.Options) m |= 1 << 11;
            if (r.L3) m |= 1 << 12; if (r.R3) m |= 1 << 13; if (r.PS) m |= 1 << 14; if (r.Touchpad) m |= 1 << 15;
            if (r.LT > 0.5f) m |= 1 << 16; if (r.RT > 0.5f) m |= 1 << 17;
            return m;
        }

        static int Pop(int v) { int n = 0; while (v != 0) { n += v & 1; v >>= 1; } return n; }

        void Combos(int mask)
        {
            int prev = prevMask; prevMask = mask;
            suppress &= mask;   // a combo's buttons stay out of the clicks until they are released
            if (CaptureState == 1)
            {
                if (mask != 0) { if (Pop(mask) >= Pop(capPeak)) capPeak = mask; capHeld = true; }
                else if (capHeld) { capHeld = false; CaptureMask = capPeak; CaptureState = 2; }
                return;
            }
            int mk = MkCombo, kb = KbCombo;
            if (mk != 0 && (mask & mk) == mk && (prev & mk) != mk)
            {
                suppress |= mk;
                MkMode = !MkMode;
                Action h = MkChanged; if (h != null) h();
            }
            if (kb != 0 && (mask & kb) == kb && (prev & kb) != kb)
            {
                suppress |= kb;
                Action h = KeyboardRequested; if (h != null) h();
            }
            int pd = PadCombo;
            if (pd != 0 && (mask & pd) == pd && (prev & pd) != pd)
            {
                suppress |= pd;
                Action h = PadRequested; if (h != null) h();
            }
            int[] pk = PadKeys;
            for (int i = 0; i < pk.Length; i++)
            {
                int hk = pk[i];
                if (hk != 0 && (mask & hk) == hk && (prev & hk) != hk)
                {
                    suppress |= hk;
                    Action<int> h2 = PadHotkey; if (h2 != null) h2(i);
                }
            }
        }

        void ReleaseMouse()
        {
            if (lDown) { Native.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); lDown = false; }
            if (rDown) { Native.mouse_event(0x0010, 0, 0, 0, IntPtr.Zero); rDown = false; }
            mkActive = false;
            lock (padLock) { haveLast = false; }   // the virtual pad takes over again
        }

        void DoMouse(float rx, float ry, float ly, int mask)
        {
            if (!mkActive)
            {
                mkActive = true; mAccX = mAccY = wheelAcc = 0; navPrev = mask; navDir = 0; touchPrev = false;
                mkPrevT = (double)clock.ElapsedTicks / Stopwatch.Frequency;
                NeutralPad();   // the game sees a released pad
            }
            double now = (double)clock.ElapsedTicks / Stopwatch.Frequency;
            double dt = Math.Min(0.05, now - mkPrevT); mkPrevT = now;
            mAccX += rx * MouseSpeed * dt;
            mAccY -= ry * MouseSpeed * dt;
            // the touchpad moves the pointer too (relative, like a laptop pad)
            Raw tr = raw;
            if (tr.Touch)
            {
                if (touchPrev)
                {
                    mAccX += (tr.TouchX - touchX) * TouchGain;
                    mAccY += (tr.TouchY - touchY) * TouchGain;
                    lastMoveT = now;
                }
                touchX = tr.TouchX; touchY = tr.TouchY;
            }
            touchPrev = tr.Touch;
            int ix = (int)mAccX, iy = (int)mAccY;
            if (ix != 0 || iy != 0)
            {
                mAccX -= ix; mAccY -= iy;
                Native.mouse_event(0x0001, unchecked((uint)ix), unchecked((uint)iy), 0, IntPtr.Zero);
            }
            if (rx * rx + ry * ry > 0.04f) lastMoveT = now;
            if (Math.Abs(ly) > 0.35f && !Osk.Open)   // with the keyboard open the left stick moves its selection instead
            {
                wheelAcc += ly * dt * 1200.0;
                int w = (int)wheelAcc;
                if (w != 0) { wheelAcc -= w; Native.mouse_event(0x0800, 0, 0, unchecked((uint)w), IntPtr.Zero); }
            }
            else wheelAcc = 0;
            KbKeys(mask, now);
            bool l = ((mask & (1 << 5)) != 0 && (suppress & (1 << 5)) == 0) || (mask & (1 << 15)) != 0;   // X or the touchpad click
            bool r = (mask & (1 << 6)) != 0 && (suppress & (1 << 6)) == 0;
            if (l != lDown) { lDown = l; Native.mouse_event(l ? 0x0002u : 0x0004u, 0, 0, 0, IntPtr.Zero); }
            if (r != rDown) { rDown = r; Native.mouse_event(r ? 0x0008u : 0x0010u, 0, 0, 0, IntPtr.Zero); }
        }

        const double TouchGain = 1.6;   // pointer pixels per touchpad unit
        bool touchPrev;
        int touchX, touchY;
        double lastMoveT, navT;
        int navDir;   // 1 up, 2 down, 3 left, 4 right: the direction the left stick holds

        void KbKeys(int mask, double now)
        {
            int down = mask & ~navPrev;
            navPrev = mask;
            Action<int> k = KbKey;
            if (k == null) return;
            if ((down & (1 << 4)) != 0) k(6);
            if ((down & (1 << 8)) != 0) k(7);
            if ((down & (1 << 9)) != 0) k(8);
            if ((down & (1 << 16)) != 0) k(9);
            if ((down & (1 << 17)) != 0) k(10);
            if (!Osk.Open && !PadPanel.Open) { navDir = 0; return; }
            if ((down & (1 << 0)) != 0 && (suppress & (1 << 0)) == 0) k(1);
            if ((down & (1 << 1)) != 0 && (suppress & (1 << 1)) == 0) k(2);
            if ((down & (1 << 2)) != 0) k(3);
            if ((down & (1 << 3)) != 0) k(4);
            // left stick = the same as the arrows: one step per push, repeating while it is held
            float lx = st.LX, ly = st.LY;
            int dir = 0;
            if (Math.Max(Math.Abs(lx), Math.Abs(ly)) > 0.6f)
                dir = Math.Abs(ly) >= Math.Abs(lx) ? (ly > 0 ? 1 : 2) : (lx > 0 ? 4 : 3);
            if (dir != navDir) { navDir = dir; navT = now + 0.35; if (dir != 0) k(dir); }
            else if (dir != 0 && now >= navT) { navT = now + 0.12; k(dir); }
            // X types the selected key, unless the pointer was just moved (then it is an ordinary click)
            if ((down & (1 << 5)) != 0 && (suppress & (1 << 5)) == 0 && now - lastMoveT > 0.6)
            {
                suppress |= 1 << 5;
                k(5);
            }
        }

        void Tally(bool countIt)
        {
            if (countIt) count++;
            long el = clock.ElapsedTicks - countStart;
            if (el >= Stopwatch.Frequency) { ReportsPerSec = count; count = 0; countStart = clock.ElapsedTicks; }
        }

        // one input state in, one virtual pad report out (countIt: the device really sent something new)
        void Handle(bool countIt)
        {
            Raw r = raw;
            State s = st;
            float lx = r.LX, ly = r.LY, rx = r.RX, ry = r.RY;
            s.RawLX = lx; s.RawLY = ly; s.RawRX = rx; s.RawRY = ry;
            RawLX = lx; RawLY = ly; RawRX = rx; RawRY = ry;

            Cfg cfg = Cfg;
            Dead(ref lx, ref ly, cfg.LMin, cfg.LMax);
            Dead(ref rx, ref ry, cfg.RMin, cfg.RMax);
            AimCurve.Apply(ref rx, ref ry, cfg.Curve, cfg.Dyn);                    // response curve of this slot (right stick)
            if (cfg.Ads != 1f && (cfg.AdsL1 ? r.L1 : r.LT >= AdsStart))           // ADS sensitivity while the ADS button (L2, or L1 if chosen) is held
            {
                rx = ScriptBase.Clamp(rx * cfg.Ads, -1f, 1f);
                ry = ScriptBase.Clamp(ry * cfg.Ads, -1f, 1f);
            }
            OutLX = lx; OutLY = ly; OutRX = rx; OutRY = ry;
            s.LX = lx; s.LY = ly; s.RX = rx; s.RY = ry;

            s.LT = r.LT; s.RT = r.RT;
            s.Up = r.Up; s.Down = r.Down; s.Left = r.Left; s.Right = r.Right;
            s.Square = r.Square; s.Cross = r.Cross; s.Circle = r.Circle; s.Triangle = r.Triangle;
            s.L1 = r.L1; s.R1 = r.R1; s.Share = r.Share; s.Options = r.Options; s.L3 = r.L3; s.R3 = r.R3;
            s.PS = r.PS; s.Touchpad = r.Touchpad;

            int mask = MaskOf(r);
            Combos(mask);
            if (MkMode) { DoMouse(rx, ry, s.LY, mask); Tally(countIt); return; }
            if (PadPanel.Open)
            {
                // the shortcut panel works without M&K mode: d-pad / left stick move, Cross runs, the game sees a released pad
                if (!menuActive) { menuActive = true; navPrev = mask; navDir = 0; NeutralPad(); }
                KbKeys(mask, (double)clock.ElapsedTicks / Stopwatch.Frequency);
                Tally(countIt);
                return;
            }
            menuActive = false;
            if (mkActive) ReleaseMouse();

            double now =(double)clock.ElapsedTicks / Stopwatch.Frequency;
            s.T = now; s.Dt = now - prevT; prevT = now;

            // scripts of the active slot, in list order; one failing script never stops the others
            ScriptItem[] list = Scripts;
            for (int i = 0; i < list.Length; i++)
            {
                ScriptItem it = list[i];
                ScriptBase sc = it.Instance;
                if (sc == null || it.Failed) continue;
                long t0 = Stopwatch.GetTimestamp();
                sc.Params = it.Values;
                try { sc.Update(s); }
                catch (Exception e) { it.Error = e.GetType().Name + ": " + e.Message; it.Failed = true; }
                it.Ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                it.Runs++;
                if (it.Ms > 20.0 && !it.Failed) { it.Error = "Update() took " + it.Ms.ToString("0") + " ms - switched off"; it.Failed = true; }
            }

            ushort btn = 0;
            if (s.Up) btn |= 0x0001; if (s.Down) btn |= 0x0002; if (s.Left) btn |= 0x0004; if (s.Right) btn |= 0x0008;
            if (s.Options) btn |= 0x0010; if (s.Share || s.Touchpad) btn |= 0x0020;
            if (s.L3) btn |= 0x0040; if (s.R3) btn |= 0x0080; if (s.L1) btn |= 0x0100; if (s.R1) btn |= 0x0200;
            if (s.PS) btn |= 0x0400;
            if (s.Cross) btn |= 0x1000; if (s.Circle) btn |= 0x2000; if (s.Square) btn |= 0x4000; if (s.Triangle) btn |= 0x8000;

            XUSB_REPORT xr = new XUSB_REPORT();
            xr.wButtons = btn;
            xr.bLeftTrigger = (byte)(Math.Max(0f, Math.Min(1f, s.LT)) * 255f);
            xr.bRightTrigger = (byte)(Math.Max(0f, Math.Min(1f, s.RT)) * 255f);
            xr.sThumbLX = ToShort(s.LX); xr.sThumbLY = ToShort(s.LY); xr.sThumbRX = ToShort(s.RX); xr.sThumbRY = ToShort(s.RY);

            lock (padLock)
            {
                if (pad != null && pad.IsDs4)
                {
                    DS4_REPORT d = BuildDs4(s);
                    if (!haveLast || d.bThumbLX != lastDs4.bThumbLX || d.bThumbLY != lastDs4.bThumbLY ||
                        d.bThumbRX != lastDs4.bThumbRX || d.bThumbRY != lastDs4.bThumbRY || d.wButtons != lastDs4.wButtons ||
                        d.bSpecial != lastDs4.bSpecial || d.bTriggerL != lastDs4.bTriggerL || d.bTriggerR != lastDs4.bTriggerR)
                    {
                        pad.SendDs4(d);
                        lastDs4 = d; haveLast = true;
                    }
                }
                else if (pad != null && (!haveLast || xr.wButtons != last.wButtons || xr.bLeftTrigger != last.bLeftTrigger ||
                    xr.bRightTrigger != last.bRightTrigger || xr.sThumbLX != last.sThumbLX || xr.sThumbLY != last.sThumbLY ||
                    xr.sThumbRX != last.sThumbRX || xr.sThumbRY != last.sThumbRY))
                {
                    pad.Send(xr);
                    last = xr; haveLast = true;
                }
            }

            Tally(countIt);
        }
    }

    // ---------------------------------------------------------------- settings
    // One script belonging to one slot. Code lives in scripts\<id>.cs next to the exe.
    sealed class ScriptItem
    {
        public string Id, Name, Code;
        public bool On;
        public volatile ScriptBase Instance;
        public volatile string Error;
        public volatile bool Failed;
        public string CompiledCode;
        public double Ms;
        public long Runs;   // how many reports this script has executed on (Apply Scripts uses it as proof)
        public List<ParamDef> Defs = new List<ParamDef>();
        public volatile Dictionary<string, float> Values = new Dictionary<string, float>();

        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

        // Re-reads the "// @param" lines of Code, keeping the current (or saved) values that still exist.
        public void Reparse(Dictionary<string, float> saved)
        {
            List<ParamDef> defs = ScriptParams.Parse(Code);
            Dictionary<string, float> old = saved != null ? saved : Values;
            Dictionary<string, float> vals = new Dictionary<string, float>();
            foreach (ParamDef d in defs)
            {
                float v;
                if (!old.TryGetValue(d.Name, out v)) v = d.Default;
                vals[d.Name] = Math.Max(d.Min, Math.Min(d.Max, v));
            }
            Defs = defs;
            Values = vals;
        }
    }

    sealed class Slot
    {
        public string Name; public float LMin, LMax, RMin, RMax;
        public int Curve = 1;       // right stick response: 0 Standard, 1 Linear (no shaping = how the program behaved before), 2 Dynamic
        public float Dyn = 0.5f;    // Dynamic level 0..1 (0 = Standard)
        public float Ads = 1f;      // ADS sensitivity: right stick multiplier while the ADS button is held (1 = normal)
        public bool AdsL1;          // the ADS button is L1 (some players aim with the bumper) instead of L2
        public bool AimOn;          // the Aim card was added to this slot by the user; without it the stick is passed straight through (linear, no ADS change)
        public Slot(string n, float a, float b, float c, float d) { Name = n; LMin = a; LMax = b; RMin = c; RMax = d; }
    }

    sealed class Settings
    {
        public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string File_ = Path.Combine(Dir, "settings.ini");
        public static readonly string OldScriptFile = Path.Combine(Dir, "script.cs");
        public static readonly string ScriptDir = Path.Combine(Dir, "scripts");
        public Slot[] Slots = new Slot[]
        {
            new Slot("Black Ops 2", 0.12f, 1.00f, 0.12f, 1.00f),
            new Slot("Black Ops 6", 0.08f, 0.95f, 0.08f, 0.95f),
            new Slot("Modern Warfare", 0.10f, 0.95f, 0.10f, 0.95f),
            new Slot("Drift Fix", 0.20f, 1.00f, 0.20f, 1.00f),
            new Slot("Competitive", 0.04f, 0.90f, 0.04f, 0.90f),
            new Slot("Custom", 0.10f, 1.00f, 0.10f, 1.00f),
            new Slot("Slot 7", 0.10f, 1.00f, 0.10f, 1.00f),
            new Slot("Slot 8", 0.10f, 1.00f, 0.10f, 1.00f),
            new Slot("Slot 9", 0.10f, 1.00f, 0.10f, 1.00f),
            new Slot("Slot 10", 0.10f, 1.00f, 0.10f, 1.00f)
        };
        public const int MaxSlots = 10;
        public int SlotCount = 1;   // slots in use; the user adds more by hand with the + button
        public int DefaultSlot;     // the slot that is applied when the program starts
        public List<ScriptItem>[] Scripts;

        // a new slot starts clean: default name and deadzones, no scripts (whatever the entry held before is dropped)
        public bool AddSlot()
        {
            if (SlotCount >= MaxSlots) return false;
            int i = SlotCount;
            foreach (ScriptItem it in Scripts[i]) DeleteCode(it);
            Scripts[i] = new List<ScriptItem>();
            Slots[i] = new Slot("Slot " + (i + 1), 0.10f, 1.00f, 0.10f, 1.00f);
            SlotCount++;
            return true;
        }

        public void RemoveSlot(int i)
        {
            if (SlotCount <= 1 || i < 0 || i >= SlotCount) return;
            foreach (ScriptItem it in Scripts[i]) DeleteCode(it);
            for (int j = i; j < SlotCount - 1; j++) { Slots[j] = Slots[j + 1]; Scripts[j] = Scripts[j + 1]; }
            int last = SlotCount - 1;
            Slots[last] = new Slot("Slot " + (last + 1), 0.10f, 1.00f, 0.10f, 1.00f);
            Scripts[last] = new List<ScriptItem>();
            SlotCount--;
            if (Active > i) Active--;
            if (Active >= SlotCount) Active = SlotCount - 1;
            if (DefaultSlot == i) DefaultSlot = 0; else if (DefaultSlot > i) DefaultSlot--;
        }
        public int Active, SeedVer; public bool Link; public string Chiaki = "";
        // when both are set, Launch starts the stream directly (command line), which honours the saved resolution
        public string Ps4Host = "", Ps4Nick = "";
        public bool Ds4 = true;   // "Sony mode": the virtual pad is a DualShock 4 (touchpad click works as on a Sony pad)
        public string Input = "auto";   // which controller is read: auto, ds5, ds4 or xinput
        public int MkCombo = (1 << 0) | (1 << 5);   // buttons that switch M&K mode (default D-pad Up + Cross); bits as in Engine.BtnNames
        public int KbCombo = (1 << 1) | (1 << 5);     // buttons that show the on-screen keyboard (default D-pad Down + Cross)
        public int PadCombo = (1 << 1) | (1 << 7);    // buttons that show the shortcut panel (default D-pad Down + Triangle)
        public string[] Pad = new string[120];        // buttons of the shortcut panel (12 per page, up to 10 pages): a program path or "keys:Ctrl+C|", or empty
        public int[] PadKeys = new int[120];          // controller combo (button bits) that runs each panel button directly, 0 = none
        public int PadPages = 1;
        public string Theme = "dsman";   // "dsman" (DsMaN Default identity) or "current" (the look before the identity)
        public string[] Quick = new string[10];   // shortcut buttons of the on-screen keyboard: "path|label" or empty

        public Settings()
        {
            Scripts = new List<ScriptItem>[Slots.Length];
            for (int i = 0; i < Scripts.Length; i++) Scripts[i] = new List<ScriptItem>();
        }

        static float F(string s, float d)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : d;
        }

        static bool ValidId(string id)
        {
            if (id == null || id.Length != 8) return false;
            foreach (char c in id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        static string CodePath(ScriptItem it) { return Path.Combine(ScriptDir, it.Id + ".cs"); }

        public static void SaveCode(ScriptItem it)
        {
            try { Directory.CreateDirectory(ScriptDir); File.WriteAllText(CodePath(it), it.Code, Encoding.UTF8); } catch (Exception) { }
        }

        public static void DeleteCode(ScriptItem it)
        {
            try { File.Delete(CodePath(it)); } catch (Exception) { }
        }

        static string ReadCode(ScriptItem it)
        {
            try { return File.ReadAllText(CodePath(it), Encoding.UTF8); } catch (Exception) { return ScriptHost.Template; }
        }

        public static Settings Load()
        {
            Settings st = new Settings();
            Dictionary<string, Dictionary<string, float>> savedParams = new Dictionary<string, Dictionary<string, float>>();
            try
            {
                foreach (string line in File.ReadAllLines(File_, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1);
                    if (k == "active") int.TryParse(v, out st.Active);
                    else if (k == "link") st.Link = v.Trim() == "1";
                    else if (k == "chiaki") st.Chiaki = v.Trim();
                    else if (k == "ps4host") st.Ps4Host = v.Trim();
                    else if (k == "ps4nick") st.Ps4Nick = v.Trim();
                    else if (k == "ds4") st.Ds4 = v.Trim() != "0";
                    else if (k == "theme") st.Theme = v.Trim();
                    else if (k == "input") st.Input = v.Trim();
                    else if (k == "mkcombo") { int n; if (int.TryParse(v.Trim(), out n)) st.MkCombo = n; }
                    else if (k == "kbcombo") { int n; if (int.TryParse(v.Trim(), out n)) st.KbCombo = n; }
                    else if (k == "padcombo") { int n; if (int.TryParse(v.Trim(), out n)) st.PadCombo = n; }
                    else if (k == "padpages") { int n; if (int.TryParse(v.Trim(), out n)) st.PadPages = Math.Max(1, Math.Min(10, n)); }
                    else if (k.StartsWith("padkey")) { int n, m; if (int.TryParse(k.Substring(6), out n) && n >= 0 && n < st.PadKeys.Length && int.TryParse(v.Trim(), out m)) st.PadKeys[n] = m; }
                    else if (k.StartsWith("pad") && k.Length >= 4 && k.Length <= 6 && char.IsDigit(k[3])) { int n; if (int.TryParse(k.Substring(3), out n) && n >= 0 && n < st.Pad.Length) st.Pad[n] = v.Trim(); }
                    else if (k == "defaultslot") { int n; if (int.TryParse(v.Trim(), out n)) st.DefaultSlot = n; }
                    else if (k == "slotcount") { int n; if (int.TryParse(v.Trim(), out n)) st.SlotCount = Math.Max(1, Math.Min(MaxSlots, n)); }
                    else if (k.StartsWith("quick") && k.Length == 6 && char.IsDigit(k[5])) st.Quick[k[5] - '0'] = v.Trim();
                    else if (k.StartsWith("aim") && k.Length == 4 && char.IsDigit(k[3]))
                    {
                        int i = k[3] - '0';
                        string[] p = v.Split('|');
                        int cv;
                        if (i < st.Slots.Length && p.Length >= 3 && p.Length <= 5 && int.TryParse(p[0].Trim(), out cv))
                        {
                            st.Slots[i].Curve = Math.Max(0, Math.Min(2, cv));
                            st.Slots[i].Dyn = Math.Max(0f, Math.Min(1f, F(p[1], 0.5f)));
                            st.Slots[i].Ads = Math.Max(0f, Math.Min(2f, F(p[2], 1f)));
                            st.Slots[i].AdsL1 = p.Length >= 4 && p[3].Trim() == "1";
                            st.Slots[i].AimOn = p.Length == 5 && p[4].Trim() == "1";   // older files had the card built in: it is now added by hand
                        }
                    }
                    else if (k == "seeded") int.TryParse(v.Trim(), out st.SeedVer);
                    else if (k.StartsWith("param") && k.Length == 6)
                    {
                        string[] p = v.Split(new char[] { '|' }, 3);
                        if (p.Length == 3)
                        {
                            Dictionary<string, float> d;
                            if (!savedParams.TryGetValue(p[0].Trim(), out d)) { d = new Dictionary<string, float>(); savedParams[p[0].Trim()] = d; }
                            d[p[1]] = F(p[2], 0f);
                        }
                    }
                    else if (k.StartsWith("slot") && k.Length == 5)
                    {
                        int i = k[4] - '0';
                        string[] p = v.Split('|');
                        if (i >= 0 && i < st.Slots.Length && p.Length == 5)
                        {
                            Slot cur = st.Slots[i];   // update in place so the aim settings of the slot survive
                            cur.Name = p[0]; cur.LMin = F(p[1], 0.1f); cur.LMax = F(p[2], 1f); cur.RMin = F(p[3], 0.1f); cur.RMax = F(p[4], 1f);
                        }
                    }
                    else if (k.StartsWith("script") && k.Length == 7 && char.IsDigit(k[6]))
                    {
                        int i = k[6] - '0';
                        string[] p = v.Split(new char[] { '|' }, 3);
                        if (i < st.Scripts.Length && p.Length == 3 && ValidId(p[0].Trim()))
                        {
                            ScriptItem it = new ScriptItem();
                            it.Id = p[0].Trim(); it.On = p[1].Trim() == "1"; it.Name = p[2];
                            it.Code = ReadCode(it);
                            st.Scripts[i].Add(it);
                        }
                    }
                }
            }
            catch (Exception) { }
            foreach (List<ScriptItem> l in st.Scripts)
                foreach (ScriptItem it in l)
                {
                    Dictionary<string, float> d;
                    savedParams.TryGetValue(it.Id, out d);
                    it.Reparse(d != null ? d : new Dictionary<string, float>());
                }
            if (st.DefaultSlot < 0 || st.DefaultSlot >= st.SlotCount) st.DefaultSlot = 0;
            st.Active = st.DefaultSlot;   // the program always starts on its default slot

            // one global script.cs from the earlier version: keep it as an OFF script in slot 1
            bool any = false;
            foreach (List<ScriptItem> l in st.Scripts) if (l.Count > 0) any = true;
            if (!any && File.Exists(OldScriptFile))
            {
                try
                {
                    ScriptItem it = new ScriptItem();
                    it.Id = ScriptItem.NewId(); it.Name = "Imported script"; it.On = false;
                    it.Code = File.ReadAllText(OldScriptFile, Encoding.UTF8);
                    it.Reparse(null);
                    SaveCode(it);
                    st.Scripts[0].Add(it);
                    st.Save();
                }
                catch (Exception) { }
            }
            return st;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("active=" + Active);
                sb.AppendLine("link=" + (Link ? "1" : "0"));
                sb.AppendLine("chiaki=" + Chiaki);
                sb.AppendLine("ps4host=" + Ps4Host);
                sb.AppendLine("ps4nick=" + Ps4Nick);
                sb.AppendLine("input=" + Input);
                sb.AppendLine("mkcombo=" + MkCombo);
                sb.AppendLine("kbcombo=" + KbCombo);
                sb.AppendLine("padcombo=" + PadCombo);
                sb.AppendLine("padpages=" + PadPages);
                for (int i = 0; i < Pad.Length; i++)
                    if (!string.IsNullOrEmpty(Pad[i])) sb.AppendLine("pad" + i + "=" + Pad[i]);
                for (int i = 0; i < PadKeys.Length; i++)
                    if (PadKeys[i] != 0) sb.AppendLine("padkey" + i + "=" + PadKeys[i]);
                sb.AppendLine("slotcount=" + SlotCount);
                sb.AppendLine("defaultslot=" + DefaultSlot);
                for (int i = 0; i < Quick.Length; i++)
                    if (!string.IsNullOrEmpty(Quick[i])) sb.AppendLine("quick" + i + "=" + Quick[i]);
                sb.AppendLine("ds4=" + (Ds4 ? "1" : "0"));
                sb.AppendLine("theme=" + Theme);
                sb.AppendLine("seeded=" + SeedVer);
                for (int i = 0; i < Slots.Length; i++)
                {
                    Slot s = Slots[i];
                    sb.AppendLine("slot" + i + "=" + s.Name.Replace("|", " ").Replace("=", " ") + "|" +
                        s.LMin.ToString("0.00", CultureInfo.InvariantCulture) + "|" + s.LMax.ToString("0.00", CultureInfo.InvariantCulture) + "|" +
                        s.RMin.ToString("0.00", CultureInfo.InvariantCulture) + "|" + s.RMax.ToString("0.00", CultureInfo.InvariantCulture));
                    sb.AppendLine("aim" + i + "=" + s.Curve + "|" + s.Dyn.ToString("0.00", CultureInfo.InvariantCulture) + "|" +
                        s.Ads.ToString("0.00", CultureInfo.InvariantCulture) + "|" + (s.AdsL1 ? "1" : "0") + "|" + (s.AimOn ? "1" : "0"));
                }
                for (int i = 0; i < Scripts.Length; i++)
                    foreach (ScriptItem it in Scripts[i])
                        sb.AppendLine("script" + i + "=" + it.Id + "|" + (it.On ? "1" : "0") + "|" +
                            it.Name.Replace("|", " ").Replace("\r", " ").Replace("\n", " "));
                for (int i = 0; i < Scripts.Length; i++)
                    foreach (ScriptItem it in Scripts[i])
                        foreach (KeyValuePair<string, float> kv in it.Values)
                            sb.AppendLine("param" + i + "=" + it.Id + "|" + kv.Key + "|" + kv.Value.ToString("0.####", CultureInfo.InvariantCulture));
                File.WriteAllText(File_, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- script parameters + library
    // "// @param Name default min max Label text" at the top of a script gives it a bar and a number box.
    sealed class ParamDef
    {
        public string Name, Label;
        public float Default, Min, Max;
    }

    static class ScriptParams
    {
        static readonly Regex Rx = new Regex(@"^\s*//\s*@param\s+(\w+)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s*(.*)$", RegexOptions.Compiled);

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

        public static List<ParamDef> Parse(string code)
        {
            List<ParamDef> r = new List<ParamDef>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string line in code.Split('\n'))
            {
                Match m = Rx.Match(line.TrimEnd('\r'));
                if (!m.Success) continue;
                ParamDef d = new ParamDef();
                d.Name = m.Groups[1].Value;
                if (!seen.Add(d.Name)) continue;
                d.Default = F(m.Groups[2].Value); d.Min = F(m.Groups[3].Value); d.Max = F(m.Groups[4].Value);
                if (d.Max <= d.Min) continue;
                d.Label = m.Groups[5].Value.Trim();
                if (d.Label.Length == 0) d.Label = d.Name;
                d.Default = Math.Max(d.Min, Math.Min(d.Max, d.Default));
                r.Add(d);
            }
            return r;
        }
    }

    sealed class LibEntry
    {
        public string Name, Description, Code;
        public bool Blank;
        public int Seed = 1;   // first version that puts this script into the selected slot automatically
        public bool IsAim;     // not a script: adds the Aim card (response curve + ADS sensitivity) to the selected slot
        public LibEntry(string n, string d, string c, bool blank) { Name = n; Description = d; Code = c; Blank = blank; }
    }

    static class ScriptLibrary
    {
        // only the blank script is offered; the user writes the others (the ready-made ones were removed on request)
        public static readonly LibEntry[] Items = new LibEntry[]
        {
            new LibEntry("Aim", "Right stick response curve (Standard / Linear / Dynamic) and ADS sensitivity, with L1 or L2 as the aim button. One per slot.", "", false) { IsAim = true, Seed = 0 },
            new LibEntry("Blank script", "An empty script with the full reference and examples.", ScriptHost.Template, true)
        };
    }

    // ---------------------------------------------------------------- script compiler
    static class ScriptHost
    {
        public const string Template =
@"// Runs on every controller report (up to ~1000 times a second) while the virtual pad is ON.
// Whatever you assign to s.* is what the game receives. This is C# - write it inside the class.
//
//   s.LX s.LY s.RX s.RY      sticks after the deadzone, -1..1   (LY / RY: up = +1)
//   s.LT s.RT                triggers, 0..1
//   s.Cross s.Circle s.Square s.Triangle s.L1 s.R1 s.L3 s.R3
//   s.Share s.Options s.PS s.Touchpad s.Up s.Down s.Left s.Right      (true / false)
//   s.RawLX s.RawLY s.RawRX s.RawRY   sticks before the deadzone (read only)
//   s.T  seconds since start      s.Dt  seconds since the previous report
//
// Bar + number box: put a line like   // @param Strength 0.10 0 0.5 Label text   (name, default, min, max, label)
// at the top; it shows next to the script in the list. Read the value in code with   P(""Strength"")
//
// Helpers: Clamp(v, lo, hi)   Lerp(a, b, t)   Curve(ref x, ref y, power)
//          Jitter(ref x, ref y, amplitude, hz, s.T)   small circular wobble added to a stick
// Fields you declare here keep their value between reports. Keep Update() fast (> 20 ms switches it off).
// The compiler is C# 5: no string interpolation ($""..."") and no ?. operator.

public override void Update(State s)
{
    // Example 1: response curve on the right stick (power > 1 = finer aim near the centre)
    // Curve(ref s.RX, ref s.RY, 1.4f);

    // Example 2: swap the bumpers
    // bool t = s.L1; s.L1 = s.R1; s.R1 = t;

    // Example 3: left trigger also presses L1 past 30 %
    // if (s.LT > 0.3f) s.L1 = true;

    // Example 4: jitter on the right stick, only while it is pushed (amplitude 0.08, 12 Hz).
    // The stream to the console has its own send rate, so keep hz low (about 5-30) and test it.
    // if (Math.Sqrt(s.RX * s.RX + s.RY * s.RY) > 0.15) Jitter(ref s.RX, ref s.RY, 0.08f, 12f, s.T);
}
";

        public static ScriptBase Compile(string code, out string error)
        {
            error = null;
            try
            {
                CSharpCodeProvider prov = new CSharpCodeProvider();
                CompilerParameters cp = new CompilerParameters();
                cp.GenerateInMemory = true;
                cp.GenerateExecutable = false;
                cp.CompilerOptions = "/optimize+ /warn:0";
                cp.ReferencedAssemblies.Add("System.dll");
                cp.ReferencedAssemblies.Add("System.Core.dll");
                cp.ReferencedAssemblies.Add(Assembly.GetExecutingAssembly().Location);
                // everything on line 1 so the user's line numbers match the compiler's
                string src = "using System; using System.Collections.Generic; using System.Linq; using ControllerUnit; " +
                             "namespace UserCode { public class UserScript : ScriptBase { " + code + "\n} }";
                CompilerResults res = prov.CompileAssemblyFromSource(cp, src);
                if (res.Errors.HasErrors)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (CompilerError e in res.Errors)
                        if (!e.IsWarning) sb.AppendLine("line " + e.Line + ": " + e.ErrorText);
                    error = sb.ToString().Trim();
                    return null;
                }
                Type t = res.CompiledAssembly.GetType("UserCode.UserScript");
                return (ScriptBase)Activator.CreateInstance(t);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }
    }

    // ---------------------------------------------------------------- UI
    // ---------------------------------------------------------------- themes
    // "AL-SeRry" in the DsMaN wordmark typeface (Snap ITC), baked to an outline by the identity's own procedure
    // (GDI+ GraphicsPath.AddString -> path data). Only the outline is stored, never the font file.
    static class Wm
    {
        public const double W = 550.3, H = 95.8, Href = 80;
        public const string Path = "M55.2 34.5L48.4 15L41.8 39ZM94.6 62.5L52 75.7L62.9 61.1L59.8 50.3L44 51.5L39.4 64.1L46.3 69.5L0 72.8L11.3 64.4L26.3 6.1L17.8 0.6L75.2 2.5L61.8 7.3L85.3 59.7ZM161.1 77.4L154.7 69.9L88.7 73.5L97.7 67.2L100.1 11.6L86.8 7.6L134.5 2.4L122 11.5L130.3 56.4L147.8 47.5L153.4 34.6ZM196.4 31.1L195.1 46.1L168.2 51.3L165.1 30.5ZM261.4 1.9L269.7 28.1C265.2 24.2 261.1 21.2 257.5 19.1C253.9 17 251 16 248.7 16C247 16 245.6 16.5 244.7 17.4C243.7 18.3 243.2 19.6 243.2 21.3C243.2 23.4 244.2 25.2 246.3 26.9C248.4 28.5 251.6 30 256 31.3C265.7 34.3 272.3 37.2 275.8 40C279.2 42.8 280.9 46.4 280.9 50.8C280.9 57.9 277.4 63.7 270.4 68.3C263.5 72.9 254.5 75.2 243.5 75.2C237.9 75.2 232.4 74.2 226.8 72.1C221.3 70.1 216.2 67.2 211.5 63.5L211.5 75.2L202.8 49.1C206.8 52.4 210.8 54.9 214.7 56.6C218.6 58.4 222.2 59.3 225.4 59.3C228.8 59.3 231.5 58.5 233.4 57.1C235.4 55.6 236.3 53.6 236.3 51C236.3 48.5 235.6 46.6 234 45.1C232.5 43.7 229 42.1 223.7 40.5C215.9 38 210.1 35.1 206.3 31.7C202.5 28.3 200.6 24.3 200.6 19.7C200.6 13.9 203.1 9.2 208.2 5.5C213.3 1.8 219.9 0 228 0C233.4 0 238.6 1 243.8 2.9C248.9 4.8 254.1 7.8 259.5 11.8ZM296.1 39.4C297.2 40.3 298.4 40.9 299.8 41.4C301.2 41.9 302.6 42.1 304.1 42.1C306.7 42.1 308.8 41.3 310.4 39.8C312 38.3 312.8 36.4 312.8 34.1C312.8 32.9 312.7 31.9 312.4 31C312 30.1 311.5 29.2 310.8 28.4C307.4 28.9 304.4 30.1 301.9 32C299.5 33.8 297.5 36.3 296.1 39.4ZM344.7 74.2L340.3 66.1C336 68.8 331.7 70.9 327.3 72.2C323 73.6 318.5 74.2 313.9 74.2C304.9 74.2 297.7 72 292.3 67.5C286.8 63 284.1 57.1 284.1 49.7C284.1 45.8 284.9 42 286.4 38.4C287.8 34.8 289.9 31.6 292.6 28.9C295.2 26.3 298.3 24.3 301.7 23C305.1 21.7 308.9 21 313 21C320.9 21 327.5 22.7 332.6 26.1C337.8 29.5 340.3 33.8 340.3 39C340.3 44.2 338 48.6 333.2 52.1C328.5 55.6 322.6 57.4 315.4 57.4C313.7 57.4 312 57.2 310.5 57C309 56.7 307.5 56.3 306.1 55.7C309.2 57.8 312.2 59.3 315.1 60.3C318.1 61.3 321.1 61.8 324.2 61.8C328 61.8 331.5 61 334.7 59.3C337.9 57.6 340.6 55.1 342.9 51.9ZM375.3 35C376.4 35.6 377.5 36.1 378.7 36.4C379.8 36.7 380.9 36.9 381.9 36.9C383.3 36.9 384.5 36.1 385.5 34.6C386.6 33.1 387.1 31.3 387.1 29.4C387.1 27 386.3 24.9 384.7 23C383.1 21.2 380.6 19.6 377.2 18.1ZM426.8 69L394.1 80L398 72.6L379.5 50.1C378.7 50 378 49.9 377.2 49.8C376.4 49.6 375.6 49.5 374.7 49.3L376.1 66.7L387.1 76.4L345.8 71.8L352.7 66.9L359.8 13.5L344.4 6.8L381.9 2.5C384.7 2.1 387.2 1.9 389.3 1.7C391.3 1.6 393.1 1.5 394.7 1.5C404.6 1.5 412.1 3.5 417.2 7.6C422.3 11.6 424.9 17.6 424.9 25.5C424.9 32 422.7 37.4 418.4 41.5C414 45.6 407.5 48.4 398.9 50ZM488.1 21.6L480.4 52.6C478.1 49.8 475.6 47.7 472.9 46.3C470.3 45 467.4 44.3 464.2 44.3C460.2 44.3 456.9 45.6 454.3 48.2C451.7 50.8 450.4 54.2 450.4 58.2C450.4 60.7 451 63.1 452.2 65.4C453.4 67.6 455.4 69.9 458.1 72.4L426.8 74.1L432.4 69.5L435.1 26.3L427.4 23.1L452.1 21.8L448 32.9C451.7 29.1 455.2 26.2 458.5 24.4C461.9 22.5 465.2 21.6 468.4 21.6C471 21.6 473.5 22.1 475.7 23.2C477.9 24.3 479.8 25.9 481.3 27.9ZM550 21.1L539.7 26.2C543.2 33.4 545.9 40 547.6 46C549.4 52 550.3 57.2 550.3 61.8C550.3 66.8 549.5 71.3 548 75.6C546.5 79.8 544.4 83.4 541.6 86.5C538.9 89.4 535.7 91.7 532.2 93.3C528.7 94.8 525 95.6 521 95.6C517.3 95.6 513.6 95.2 510.1 94.3C506.5 93.4 503 92.1 499.7 90.4L491 95.8L503.8 75.2C506.8 78.7 509.8 81.4 512.8 83.2C515.9 84.9 519 85.8 522.2 85.8C526.4 85.8 529.7 84.5 532.3 81.8C534.8 79.1 536.1 75.6 536.1 71.1C536.1 69.4 535.9 67.8 535.5 66.2C535.1 64.6 534.5 63 533.8 61.4C531.3 65.2 528.3 68.1 524.6 70.1C521 72.1 516.9 73.1 512.4 73.1C505.6 73.1 500.1 71.2 495.8 67.4C491.5 63.6 489.4 58.7 489.4 52.9C489.4 48.4 490.3 44.1 492.3 40C494.2 35.8 497.3 31.6 501.5 27.2L489 24.6L518.5 21.2C516.2 23.8 514.5 26.5 513.4 29.3C512.2 32.1 511.6 34.7 511.6 37.1C511.6 39.7 512.2 41.8 513.5 43.4C514.7 45.1 516.3 45.9 518.3 45.9C521 45.9 523.3 44.8 525 42.7C526.8 40.5 527.7 37.7 527.7 34.3C527.7 32 527.4 29.9 527 28.1C526.5 26.2 525.7 24.5 524.7 23Z";
    }

    sealed class ThemeDef
    {
        public string Id, Name, Blurb;
        public string Text, Dim, WinBg, Back, FieldBg, FieldEdge, FieldEdgeW, RedTop, RedBot, CloseHover, Accent, Ok, Err;
        public int CardRadius, WinBtnW, WinBtnH;
        public bool Gradient, Acrylic, Wordmark;
    }

    static class Th
    {
        // the one theme of the program: the DsMaN "Default" identity (values from identity.json of the theme package)
        public static readonly ThemeDef DsMaN = new ThemeDef
        {
            Id = "dsman", Name = "Default",
            Blurb = "Dark-green gradient glass with the cartoon name.",
            Text = "#E2F0E6", Dim = "#7EA08E", WinBg = "#FF142C21", Back = "#FF142C21",
            FieldBg = "#FF163024", FieldEdge = "#5A34C759", FieldEdgeW = "1.5",
            RedTop = "#FFE04848", RedBot = "#FF862B2B", CloseHover = "#E0E04848",
            Accent = "#34C759", Ok = "#34C759", Err = "#FF7B72",
            CardRadius = 18, WinBtnW = 38, WinBtnH = 32, Gradient = true, Acrylic = false, Wordmark = true
        };

        public static ThemeDef T = DsMaN;

        public static ThemeDef ById(string id) { return DsMaN; }
        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static Color TextC { get { return C(T.Text); } }
        public static Color DimC { get { return C(T.Dim); } }
        public static Color BackC { get { return C(T.Back); } }
        public static Color AccentC { get { return C(T.Accent); } }
        public static Color OkC { get { return C(T.Ok); } }
        public static Color ErrC { get { return C(T.Err); } }

        // fills the @Tokens@ of the XAML text with the values of the active theme
        public static string Fill(string xaml)
        {
            return xaml.Replace("@Text@", T.Text).Replace("@Dim@", T.Dim).Replace("@WinBg@", T.WinBg)
                .Replace("@FieldBg@", T.FieldBg).Replace("@FieldEdgeW@", T.FieldEdgeW).Replace("@FieldEdge@", T.FieldEdge)
                .Replace("@RedTop@", T.RedTop).Replace("@RedBot@", T.RedBot).Replace("@CloseHover@", T.CloseHover)
                .Replace("@CardRadius@", T.CardRadius.ToString()).Replace("@WinBtnW@", T.WinBtnW.ToString())
                .Replace("@WinBtnH@", T.WinBtnH.ToString());
        }

        // DsMaN window background: one gradient #345240 -> #0F2116 (CSS 152 deg) over the whole window
        public static Brush WindowBrush(ThemeDef t)
        {
            if (!t.Gradient) return new SolidColorBrush(C(t.WinBg));
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0.266, 0.058);
            g.EndPoint = new Point(0.734, 0.942);
            g.GradientStops.Add(new GradientStop(C("#345240"), 0.0));
            g.GradientStops.Add(new GradientStop(C("#0F2116"), 1.0));
            return g;
        }

        // the cartoon name: shadow, dark outline, yellow->green fill, white shine (proportions of the identity)
        public static FrameworkElement WordmarkView(double heightPx)
        {
            double H = Wm.Href, dx = 0.053 * H, dy = 0.105 * H, sd = 0.04 * H;
            double sh = 0.237 * H, ol = 0.184 * H, shine = 0.053 * H;
            double padX = 0.13 * H + dx, padTop = 0.13 * H, padBottom = 0.13 * H + dy;
            Canvas cv = new Canvas();
            cv.Width = Wm.W + 2 * padX;
            cv.Height = Wm.H + padTop + padBottom;
            Geometry g = Geometry.Parse("F0 " + Wm.Path);   // F0 = even-odd, like GDI+
            g.Freeze();
            Action<Brush, Brush, double, double, double> layer = delegate (Brush fill, Brush stroke, double thick, double ox, double oy)
            {
                System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
                p.Data = g; p.Fill = fill; p.Stroke = stroke; p.StrokeThickness = thick;
                p.StrokeLineJoin = PenLineJoin.Round;
                Canvas.SetLeft(p, padX + ox); Canvas.SetTop(p, padTop + oy);
                cv.Children.Add(p);
            };
            Brush shadow = new SolidColorBrush(Color.FromArgb(120, 0, 25, 8));
            Brush ink = new SolidColorBrush(C("#082E14"));
            LinearGradientBrush fillB = new LinearGradientBrush(C("#FFFC96"), C("#34C759"), new Point(0, 0), new Point(0, 1));
            Brush shineB = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
            layer(shadow, shadow, sh, dx, dy);
            layer(ink, ink, ol, 0, 0);
            layer(fillB, null, 0, 0, 0);
            layer(null, shineB, shine, -sd, -sd);
            Viewbox vb = new Viewbox();
            vb.Height = heightPx; vb.Stretch = Stretch.Uniform; vb.HorizontalAlignment = HorizontalAlignment.Left;
            vb.Child = cv;
            return vb;
        }

        // 16 px icon of the title strip (from the icon embedded in the exe)
        // the logo of the header: the cartoon-realistic controller picture embedded as logo.png (falls back to the icon)
        public static ImageSource LogoImage()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                {
                    if (s != null) return BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                }
            }
            catch (Exception) { }
            return AppIcon(256);
        }

        // the embedded icon frame closest to px pixels (32 for the title strip, 256 for the big logo)
        public static ImageSource AppIcon(int px)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                {
                    if (s == null) return null;
                    BitmapDecoder d = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    BitmapFrame best = d.Frames[0];
                    foreach (BitmapFrame f in d.Frames) if (Math.Abs(f.PixelWidth - px) < Math.Abs(best.PixelWidth - px)) best = f;
                    return best;
                }
            }
            catch (Exception) { return null; }
        }
    }

    static class Xaml
    {
        public const string Resources = @"
<WindowChrome.WindowChrome>
  <WindowChrome CaptionHeight='40' ResizeBorderThickness='6' GlassFrameThickness='0' CornerRadius='0' UseAeroCaptionButtons='False'/>
</WindowChrome.WindowChrome>
<Window.Resources>
  <LinearGradientBrush x:Key='GlassFill' StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#30FFFFFF' Offset='0'/><GradientStop Color='#0FFFFFFF' Offset='1'/></LinearGradientBrush>
  <LinearGradientBrush x:Key='GlassHover' StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#48FFFFFF' Offset='0'/><GradientStop Color='#1CFFFFFF' Offset='1'/></LinearGradientBrush>
  <LinearGradientBrush x:Key='GlassEdge' StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#70FFFFFF' Offset='0'/><GradientStop Color='#18FFFFFF' Offset='1'/></LinearGradientBrush>
  <LinearGradientBrush x:Key='Sheen' StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#38FFFFFF' Offset='0'/><GradientStop Color='#00FFFFFF' Offset='1'/></LinearGradientBrush>
  <LinearGradientBrush x:Key='Green' StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#FF2AAC47' Offset='0'/><GradientStop Color='#FF0E700E' Offset='1'/></LinearGradientBrush>
  <LinearGradientBrush x:Key='GreenHover' StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#FF36D45A' Offset='0'/><GradientStop Color='#FF138F13' Offset='1'/></LinearGradientBrush>
  <LinearGradientBrush x:Key='Red' StartPoint='0,0' EndPoint='0,1'><GradientStop Color='@RedTop@' Offset='0'/><GradientStop Color='@RedBot@' Offset='1'/></LinearGradientBrush>

  <ControlTemplate x:Key='SbThumb' TargetType='Thumb'>
    <Border x:Name='tb' CornerRadius='5' Margin='2' Background='#6634C759' BorderBrush='#3334C759' BorderThickness='1'/>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='tb' Property='Background' Value='#AA34C759'/></Trigger>
      <Trigger Property='IsDragging' Value='True'><Setter TargetName='tb' Property='Background' Value='#EE34C759'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <ControlTemplate x:Key='SbPage' TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>
  <Style TargetType='ScrollBar'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='12'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Border Background='#18000000' CornerRadius='6' Margin='0,2'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Template='{StaticResource SbPage}'/></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb Template='{StaticResource SbThumb}'/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Template='{StaticResource SbPage}'/></Track.IncreaseRepeatButton>
          </Track>
        </Border>
      </ControlTemplate>
    </Setter.Value></Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='Height' Value='12'/>
        <Setter Property='Template'><Setter.Value>
          <ControlTemplate TargetType='ScrollBar'>
            <Border Background='#18000000' CornerRadius='6' Margin='2,0'>
              <Track x:Name='PART_Track'>
                <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageLeftCommand' Template='{StaticResource SbPage}'/></Track.DecreaseRepeatButton>
                <Track.Thumb><Thumb Template='{StaticResource SbThumb}'/></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageRightCommand' Template='{StaticResource SbPage}'/></Track.IncreaseRepeatButton>
              </Track>
            </Border>
          </ControlTemplate>
        </Setter.Value></Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType='TextBlock'><Setter Property='Foreground' Value='@Text@'/></Style>
  <Style x:Key='Muted' TargetType='TextBlock'><Setter Property='Foreground' Value='@Dim@'/></Style>

  <Style x:Key='Card' TargetType='Border'>
    <Setter Property='CornerRadius' Value='@CardRadius@'/>
    <Setter Property='Padding' Value='18'/>
    <Setter Property='Background' Value='{StaticResource GlassFill}'/>
    <Setter Property='BorderBrush' Value='{StaticResource GlassEdge}'/>
    <Setter Property='BorderThickness' Value='1'/>
  </Style>

  <Style x:Key='Glass' TargetType='Button'>
    <Setter Property='Foreground' Value='@Text@'/>
    <Setter Property='Padding' Value='16,8'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Grid>
          <Border x:Name='bd' CornerRadius='14' Background='{StaticResource GlassFill}' BorderBrush='{StaticResource GlassEdge}' BorderThickness='1'/>
          <Border x:Name='sh' CornerRadius='14,14,40,40' Margin='1' Height='20' VerticalAlignment='Top' Background='{StaticResource Sheen}' IsHitTestVisible='False'/>
          <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{StaticResource GlassHover}'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter Property='Opacity' Value='0.72'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='GreenBtn' TargetType='Button' BasedOn='{StaticResource Glass}'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Grid>
          <Border x:Name='bd' CornerRadius='14' Background='{StaticResource Green}' BorderBrush='#80A8F0B8' BorderThickness='1'/>
          <Border CornerRadius='14,14,40,40' Margin='1' Height='20' VerticalAlignment='Top' Background='{StaticResource Sheen}' IsHitTestVisible='False'/>
          <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.88'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter Property='Opacity' Value='0.7'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='RedBtn' TargetType='Button' BasedOn='{StaticResource Glass}'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Grid>
          <Border x:Name='bd' CornerRadius='14' Background='{StaticResource Red}' BorderBrush='#80F0B0AA' BorderThickness='1'/>
          <Border CornerRadius='14,14,40,40' Margin='1' Height='20' VerticalAlignment='Top' Background='{StaticResource Sheen}' IsHitTestVisible='False'/>
          <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.88'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter Property='Opacity' Value='0.7'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='WinBtn' TargetType='Button'>
    <Setter Property='Foreground' Value='@Text@'/>
    <Setter Property='Width' Value='38'/>
    <Setter Property='Height' Value='32'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Grid>
          <Border x:Name='bd' CornerRadius='13' Background='{StaticResource GlassFill}' BorderBrush='{StaticResource GlassEdge}' BorderThickness='1'/>
          <Path Data='M13,19 L25,19' Stroke='White' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' IsHitTestVisible='False'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#46FFFFFF'/><Setter TargetName='bd' Property='BorderBrush' Value='#96FFFFFF'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter Property='Opacity' Value='0.7'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='WinClose' TargetType='Button' BasedOn='{StaticResource WinBtn}'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Grid>
          <Border x:Name='bd' CornerRadius='13' Background='{StaticResource GlassFill}' BorderBrush='{StaticResource GlassEdge}' BorderThickness='1'/>
          <Path Data='M14,11 L24,21 M14,21 L24,11' Stroke='White' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' IsHitTestVisible='False'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{StaticResource Red}'/><Setter TargetName='bd' Property='BorderBrush' Value='#5AFFFFFF'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter Property='Opacity' Value='0.7'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Switch' TargetType='CheckBox'>
    <Setter Property='Foreground' Value='@Text@'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='CheckBox'>
        <StackPanel Orientation='Horizontal' Background='Transparent'>
          <Grid Width='52' Height='30'>
            <Border x:Name='track' CornerRadius='15' Background='#40FFFFFF' BorderBrush='#30FFFFFF' BorderThickness='1'/>
            <Ellipse x:Name='knob' Width='26' Height='26' Fill='White' HorizontalAlignment='Left' Margin='2,0,0,0'/>
          </Grid>
          <ContentPresenter Margin='10,0,0,0' VerticalAlignment='Center'/>
        </StackPanel>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='track' Property='Background' Value='{StaticResource Green}'/>
            <Setter TargetName='knob' Property='HorizontalAlignment' Value='Right'/>
            <Setter TargetName='knob' Property='Margin' Value='0,0,2,0'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Seg' TargetType='RadioButton'>
    <Setter Property='Foreground' Value='@Text@'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Margin' Value='3,0'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RadioButton'>
        <Grid>
          <Border x:Name='bd' CornerRadius='14' Background='{StaticResource GlassFill}' BorderBrush='{StaticResource GlassEdge}' BorderThickness='1'/>
          <Border x:Name='sh' CornerRadius='14,14,40,40' Margin='1' Height='18' VerticalAlignment='Top' Background='{StaticResource Sheen}' IsHitTestVisible='False'/>
          <ContentPresenter Margin='6,9' HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{StaticResource GlassHover}'/></Trigger>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='bd' Property='Background' Value='{StaticResource Green}'/><Setter TargetName='bd' Property='BorderBrush' Value='#80A8F0B8'/></Trigger>
          <MultiTrigger>
            <MultiTrigger.Conditions><Condition Property='IsChecked' Value='True'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions>
            <Setter TargetName='bd' Property='Background' Value='{StaticResource GreenHover}'/>
            <Setter TargetName='bd' Property='BorderBrush' Value='#E0FFFFFF'/>
          </MultiTrigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Field' TargetType='TextBox'>
    <Setter Property='Foreground' Value='@Text@'/>
    <Setter Property='CaretBrush' Value='@Text@'/>
    <Setter Property='Padding' Value='10,7'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='TextBox'>
        <Border CornerRadius='12' Background='@FieldBg@' BorderBrush='@FieldEdge@' BorderThickness='@FieldEdgeW@'>
          <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
        </Border>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='TrackFill' TargetType='RepeatButton'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RepeatButton'><Grid Height='28' Background='Transparent'><Border Height='4' CornerRadius='2' Background='{StaticResource Green}' VerticalAlignment='Center'/></Grid></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='TrackEmpty' TargetType='RepeatButton'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RepeatButton'><Grid Height='28' Background='Transparent'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='Knob' TargetType='Thumb'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Thumb'><Ellipse Width='22' Height='22' Fill='White' Stroke='#55000000' StrokeThickness='0.5'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='Slider'>
    <Setter Property='Height' Value='28'/>
    <Setter Property='IsMoveToPointEnabled' Value='True'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Slider'>
        <Grid Height='28'>
          <Border Height='4' CornerRadius='2' Background='#40FFFFFF' VerticalAlignment='Center'/>
          <Track x:Name='PART_Track'>
            <Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Style='{StaticResource TrackFill}'/></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb Style='{StaticResource Knob}'/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Style='{StaticResource TrackEmpty}'/></Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
</Window.Resources>";

        const string Head = @"<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
  FontFamily='Bahnschrift, Segoe UI' FontSize='13' Foreground='@Text@' UseLayoutRounding='True' SnapsToDevicePixels='True'
  WindowStyle='None' Background='@WinBg@' ";

        public const string Main = Head + @"Title='Controller' Width='1010' SizeToContent='Height' MinWidth='780' ResizeMode='CanMinimize' WindowStartupLocation='CenterScreen'>"
            + Resources + @"
  <Grid>
  <Grid.RowDefinitions><RowDefinition x:Name='rowStrip' Height='40'/><RowDefinition Height='*'/></Grid.RowDefinitions>
  <Grid Grid.Row='0'>
    <StackPanel Orientation='Horizontal' VerticalAlignment='Center' Margin='24,0,0,0'>
      <Image x:Name='imgIcon' Width='16' Height='16' Margin='0,0,8,0' Visibility='Collapsed'/>
      <TextBlock x:Name='txtStrip' Text='AL-SeRry' FontSize='12' FontWeight='Bold' Style='{StaticResource Muted}' VerticalAlignment='Center'/>
    </StackPanel>
    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,14,0' WindowChrome.IsHitTestVisibleInChrome='True'>
      <Button x:Name='btnMin' Style='{StaticResource WinBtn}' Content='&#xE921;' Margin='0,0,8,0'/>
      <Button x:Name='btnClose' Style='{StaticResource WinClose}' Content='&#xE8BB;'/>
    </StackPanel>
  </Grid>
  <Grid Grid.Row='1'>
  <Grid x:Name='pageHome' Margin='24,14,24,22'>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>

    <Grid Grid.Row='0' Margin='0,0,0,14'>
      <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
        <Image x:Name='imgLogo' Width='108' Height='108' Margin='0,0,14,0' Visibility='Collapsed' VerticalAlignment='Center'/>
        <StackPanel VerticalAlignment='Center'>
          <TextBlock x:Name='txtTitle' Text='AL-SeRry' FontSize='26' FontWeight='SemiBold'/>
        </StackPanel>
      </StackPanel>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center' WindowChrome.IsHitTestVisibleInChrome='True'>
        <Button x:Name='btnMk' Content='M&amp;K mode' Style='{StaticResource Glass}' Margin='0,0,8,0' ToolTip='Mouse and keyboard mode: the right stick moves the mouse, Cross = left click, Circle = right click, left stick scrolls. The game gets nothing meanwhile. The buttons for it are set in Settings.'/>
        <Button x:Name='btnKb' Visibility='Collapsed' Content='Keyboard' Style='{StaticResource Glass}' Margin='0,0,8,0' ToolTip='On-screen keyboard (English / Arabic)'/>
        <Button x:Name='btnSettings' Content='Settings' Style='{StaticResource Glass}' Margin='0,0,8,0'/>
        <Button x:Name='btnScript' Content='Tools' Style='{StaticResource Glass}' Margin='0,0,8,0'/>
      </StackPanel>
    </Grid>

    <Border Grid.Row='1' Style='{StaticResource Card}' Margin='0,0,0,12' Padding='18,12'>
      <Grid>
        <StackPanel VerticalAlignment='Center'>
          <TextBlock Text='Virtual controller' FontWeight='SemiBold' FontSize='15'/>
          <TextBlock x:Name='txtDevice' Style='{StaticResource Muted}' Text='Looking for a controller...'/>
          <TextBlock x:Name='txtPadHint' Style='{StaticResource Muted}' Text='Turn it on, then start chiaki-ng.'/>
        </StackPanel>
        <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center'>
          <CheckBox x:Name='chkDs4' Style='{StaticResource Switch}' Content='Sony mode' Margin='0,0,24,0' ToolTip='ON: the virtual controller is a DualShock 4, so the touchpad click and Share are separate buttons like on a Sony pad. OFF: Xbox 360 pad.'/>
          <CheckBox x:Name='chkPad' Style='{StaticResource Switch}'/>
        </StackPanel>
      </Grid>
    </Border>

    <Grid Grid.Row='2' Margin='0,0,0,12'>
      <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='12'/><ColumnDefinition/></Grid.ColumnDefinitions>
      <Border Grid.Column='0' Style='{StaticResource Card}'>
        <StackPanel>
          <TextBlock Text='Left stick  (L3)' FontWeight='SemiBold' FontSize='15'/>
          <Canvas x:Name='cvL' Width='220' Height='220' Margin='0,10,0,8'/>
          <Grid><TextBlock Text='Deadzone Min' Style='{StaticResource Muted}'/><TextBlock x:Name='lblLMin' HorizontalAlignment='Right'/></Grid>
          <Slider x:Name='slLMin' Minimum='0' Maximum='0.6' SmallChange='0.01' LargeChange='0.05' TickFrequency='0.01' IsSnapToTickEnabled='True'/>
          <Grid><TextBlock Text='Deadzone Max' Style='{StaticResource Muted}'/><TextBlock x:Name='lblLMax' HorizontalAlignment='Right'/></Grid>
          <Slider x:Name='slLMax' Minimum='0.5' Maximum='1' SmallChange='0.01' LargeChange='0.05' TickFrequency='0.01' IsSnapToTickEnabled='True'/>
        </StackPanel>
      </Border>
      <Border Grid.Column='2' Style='{StaticResource Card}'>
        <StackPanel>
          <TextBlock Text='Right stick  (R3)' FontWeight='SemiBold' FontSize='15'/>
          <Canvas x:Name='cvR' Width='220' Height='220' Margin='0,10,0,8'/>
          <Grid><TextBlock Text='Deadzone Min' Style='{StaticResource Muted}'/><TextBlock x:Name='lblRMin' HorizontalAlignment='Right'/></Grid>
          <Slider x:Name='slRMin' Minimum='0' Maximum='0.6' SmallChange='0.01' LargeChange='0.05' TickFrequency='0.01' IsSnapToTickEnabled='True'/>
          <Grid><TextBlock Text='Deadzone Max' Style='{StaticResource Muted}'/><TextBlock x:Name='lblRMax' HorizontalAlignment='Right'/></Grid>
          <Slider x:Name='slRMax' Minimum='0.5' Maximum='1' SmallChange='0.01' LargeChange='0.05' TickFrequency='0.01' IsSnapToTickEnabled='True'/>
        </StackPanel>
      </Border>
    </Grid>

    <Border Grid.Row='3' Style='{StaticResource Card}' Margin='0,0,0,12' Padding='18,14'>
      <StackPanel>
        <Grid Margin='0,0,0,10'>
          <TextBlock Text='Slots' FontWeight='SemiBold' FontSize='15' VerticalAlignment='Center'/>
          <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center'>
            <Button x:Name='btnRemoveSlot' Content='Remove' Style='{StaticResource Glass}' Padding='14,0' Height='28' FontSize='12' Margin='0,0,8,0' ToolTip='Remove the selected slot (asks first)'/>
            <Button x:Name='btnDefaultSlot' Content='Set default' Style='{StaticResource Glass}' Padding='14,0' Height='28' FontSize='12' Margin='0,0,8,0' ToolTip='Make the selected slot the one applied when the program starts'/>
            <Button x:Name='btnAddSlot' Content='+' Style='{StaticResource Glass}' Padding='0' Width='38' Height='28' FontSize='18' Margin='0,0,16,0' ToolTip='Add a slot. Right-click a slot to remove it.'/>
            <CheckBox x:Name='chkLink' Style='{StaticResource Switch}' Content='Link sticks'/>
          </StackPanel>
        </Grid>
        <UniformGrid x:Name='slots' Rows='1' Margin='-3,0'/>
        <Grid Margin='0,12,0,0' Visibility='Collapsed'>
          <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <TextBox x:Name='txtName' Style='{StaticResource Field}' MaxLength='24'/>
          <Button x:Name='btnSave' Grid.Column='1' Content='Save to slot' Style='{StaticResource GreenBtn}' Margin='10,0,0,0'/>
          <Button x:Name='btnRevert' Grid.Column='2' Content='Revert' Style='{StaticResource Glass}' Margin='8,0,0,0'/>
        </Grid>
      </StackPanel>
    </Border>

    <TextBlock Grid.Row='4' x:Name='txtFooter' Visibility='Collapsed' Style='{StaticResource Muted}' FontSize='12' TextWrapping='Wrap'/>
  </Grid>

  <Grid x:Name='pageScripts' Margin='24,14,24,22' Visibility='Collapsed'>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
    <Grid Margin='0,0,0,14'>
      <StackPanel VerticalAlignment='Center'>
        <TextBlock Text='Tools' FontSize='26' FontWeight='SemiBold'/>
        <TextBlock x:Name='txtSlotInfo' Style='{StaticResource Muted}' Margin='0,2,0,0'/>
      </StackPanel>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center' WindowChrome.IsHitTestVisibleInChrome='True'>
        <Button x:Name='btnHome' Content='Home' Style='{StaticResource Glass}' Margin='0,0,8,0'/>
      </StackPanel>
    </Grid>
    <StackPanel Grid.Row='1' Orientation='Horizontal' Margin='0,0,0,12'>
      <RadioButton x:Name='tabShort' GroupName='stab' Style='{StaticResource Seg}' Width='150' Margin='0,0,8,0'><TextBlock Text='Shortcuts' FontWeight='SemiBold'/></RadioButton>
      <RadioButton x:Name='tabCtl' GroupName='stab' Style='{StaticResource Seg}' IsChecked='True' Width='150'><TextBlock Text='Controller' FontWeight='SemiBold'/></RadioButton>
    </StackPanel>
    <Grid x:Name='tabController' Grid.Row='2'>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
    <Button x:Name='btnAdd' Grid.Row='0' Content='+   Add Script' Style='{StaticResource Glass}' Padding='16,13' FontSize='16' FontWeight='SemiBold' Margin='0,0,0,12'/>
    <Grid Grid.Row='1' Margin='0,0,0,12'>
      <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
      <Button x:Name='btnApply' Content='Apply Scripts' Style='{StaticResource GreenBtn}' Padding='18,10'/>
      <Button x:Name='btnVerify' Grid.Column='1' Content='Verify Controller' Style='{StaticResource Glass}' Padding='18,10' Margin='8,0,0,0'/>
      <TextBlock x:Name='txtActionStatus' Grid.Column='2' Style='{StaticResource Muted}' FontSize='12' TextWrapping='Wrap' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' Margin='14,0,0,0' MaxHeight='56'/>
    </Grid>
    <Border Grid.Row='2' Style='{StaticResource Card}' Padding='12' Height='430'>
      <Grid>
        <ScrollViewer VerticalScrollBarVisibility='Auto'>
          <ScrollViewer.OpacityMask>
            <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
              <GradientStop Color='#00000000' Offset='0'/><GradientStop Color='#FF000000' Offset='0.04'/>
              <GradientStop Color='#FF000000' Offset='0.96'/><GradientStop Color='#00000000' Offset='1'/>
            </LinearGradientBrush>
          </ScrollViewer.OpacityMask>
          <StackPanel x:Name='scriptList'/>
        </ScrollViewer>
        <TextBlock x:Name='txtEmpty' Style='{StaticResource Muted}' Text='No scripts in this slot yet. Press Add Script.' HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Grid>
    </Border>
    <Border Grid.Row='3' Style='{StaticResource Card}' Margin='0,12,0,0' Padding='18,14'>
      <StackPanel>
        <TextBlock Text='Slot' FontWeight='SemiBold' FontSize='15'/>
        <TextBlock Text='Scripts you add belong to the selected slot.' Style='{StaticResource Muted}' Margin='0,0,0,10'/>
        <UniformGrid x:Name='slots2' Rows='1' Margin='-3,0'/>
      </StackPanel>
    </Border>
    </Grid>
    <Grid x:Name='tabShortcuts' Grid.Row='2' Visibility='Collapsed'>
      <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
      <Button x:Name='subSwitch' Content='&#x25C0;     Keyboard     &#x25B6;' Style='{StaticResource GreenBtn}' HorizontalAlignment='Left' Width='240' Padding='0,9' Margin='0,0,0,12' ToolTip='Switch between the Keyboard and the Controller shortcuts'/>
      <Border x:Name='shortKeyboard' Grid.Row='1' Style='{StaticResource Card}' Padding='18,14'>
        <StackPanel>
          <TextBlock FontWeight='SemiBold' FontSize='15'>Shortcut buttons of the <Run Text='&quot;KEYBOARD&quot;' Foreground='#34C759' FontWeight='ExtraBold'/></TextBlock>
          <TextBlock Style='{StaticResource Muted}' Text='Click a side button to make it start a program or send a key command such as Ctrl+C. The letters are only a picture.' TextWrapping='Wrap' Margin='0,2,0,14'/>
          <Viewbox x:Name='kbView' StretchDirection='DownOnly' Stretch='Uniform' HorizontalAlignment='Center'/>
        </StackPanel>
      </Border>
      <Border x:Name='shortController' Grid.Row='1' Style='{StaticResource Card}' Padding='18,14' MinHeight='260' Visibility='Collapsed'>
        <StackPanel>
          <TextBlock FontWeight='SemiBold' FontSize='15'>Shortcut panel of the <Run Text='&quot;CONTROLLER&quot;' Foreground='#34C759' FontWeight='ExtraBold'/></TextBlock>
          <StackPanel Orientation='Horizontal' Margin='0,10,0,12'>
            <Button x:Name='btnPadAdd' Content='+' Style='{StaticResource Glass}' Padding='0' Width='38' Height='28' FontSize='18' Margin='0,0,8,0' ToolTip='Add a page (up to 10)'/>
            <Button x:Name='btnPadDel' Content='&#x2212;' Style='{StaticResource Glass}' Padding='0' Width='38' Height='28' FontSize='18' ToolTip='Delete this page'/>
          </StackPanel>
          <Viewbox x:Name='padView' StretchDirection='DownOnly' Stretch='Uniform' HorizontalAlignment='Center'/>
        </StackPanel>
      </Border>
    </Grid>
  </Grid>
  </Grid>
  </Grid>
</Window>";

        public const string ScriptWin = Head + @"Title='Script' Width='820' Height='640' MinWidth='600' MinHeight='420' WindowStartupLocation='CenterOwner'>"
            + Resources + @"
  <Grid>
  <Grid.RowDefinitions><RowDefinition Height='40'/><RowDefinition Height='*'/></Grid.RowDefinitions>
  <Grid Grid.Row='0'>
    <TextBlock Text='{Binding Title, RelativeSource={RelativeSource AncestorType=Window}}' FontSize='12' FontWeight='SemiBold' Style='{StaticResource Muted}' VerticalAlignment='Center' Margin='20,0,0,0'/>
    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0' WindowChrome.IsHitTestVisibleInChrome='True'>
      <Button x:Name='btnWinClose' Style='{StaticResource WinClose}' Content='&#xE8BB;'/>
    </StackPanel>
  </Grid>
  <Grid Grid.Row='1' Margin='20,6,20,18'>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
    <Grid Margin='0,0,0,12'>
      <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
      <TextBox x:Name='txtName' Style='{StaticResource Field}' MaxLength='32' FontSize='15' FontWeight='SemiBold'/>
      <Button x:Name='btnApply' Grid.Column='1' Content='Save &amp; run   Ctrl+Enter' Style='{StaticResource GreenBtn}' Margin='10,0,0,0'/>
      <Button x:Name='btnReset' Grid.Column='2' Content='Reset to template' Style='{StaticResource Glass}' Margin='8,0,0,0'/>
    </Grid>
    <TextBox x:Name='txtCode' Grid.Row='1' Style='{StaticResource Field}' FontFamily='Consolas' FontSize='13' AcceptsReturn='True' AcceptsTab='True'
             VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Auto' Padding='8'/>
    <TextBlock x:Name='lblStatus' Grid.Row='2' Margin='2,10,0,0' Style='{StaticResource Muted}' TextWrapping='Wrap'/>
  </Grid>
  </Grid>
</Window>";

        public const string LibraryWin = Head + @"Title='Add Script' Width='620' Height='600' MinWidth='460' MinHeight='360' WindowStartupLocation='CenterOwner'>"
            + Resources + @"
  <Grid>
  <Grid.RowDefinitions><RowDefinition Height='40'/><RowDefinition Height='*'/></Grid.RowDefinitions>
  <Grid Grid.Row='0'>
    <TextBlock Text='{Binding Title, RelativeSource={RelativeSource AncestorType=Window}}' FontSize='12' FontWeight='SemiBold' Style='{StaticResource Muted}' VerticalAlignment='Center' Margin='20,0,0,0'/>
    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0' WindowChrome.IsHitTestVisibleInChrome='True'>
      <Button x:Name='btnWinClose' Style='{StaticResource WinClose}' Content='&#xE8BB;'/>
    </StackPanel>
  </Grid>
  <Grid Grid.Row='1' Margin='20,6,20,18'>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/></Grid.RowDefinitions>
    <StackPanel Margin='0,0,0,12'>
      <TextBlock Text='Add a script' FontSize='22' FontWeight='SemiBold'/>
      <TextBlock Style='{StaticResource Muted}' Text='It is added to the selected slot, switched OFF. Turn it on in the list.'/>
    </StackPanel>
    <ScrollViewer Grid.Row='1' VerticalScrollBarVisibility='Auto'><StackPanel x:Name='libList'/></ScrollViewer>
  </Grid>
  </Grid>
</Window>";

        public const string Dialog = Head + @"Title='Controller' SizeToContent='WidthAndHeight' ResizeMode='NoResize' WindowStartupLocation='CenterOwner'>"
            + Resources + @"
  <Border BorderBrush='#5534C759' BorderThickness='1.5' Padding='28,34,28,24' MinWidth='400' MaxWidth='480'>
    <StackPanel>
      <TextBlock x:Name='txtMsg' FontSize='15' TextWrapping='Wrap' Margin='0,0,0,24'/>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'>
        <Button x:Name='btnNo' Style='{StaticResource Glass}' Padding='20,9' Margin='0,0,10,0'/>
        <Button x:Name='btnYes' Style='{StaticResource GreenBtn}' Padding='20,9'/>
      </StackPanel>
    </StackPanel>
  </Border>
</Window>";

        public const string SettingsWin = Head +@"Title='Settings' Width='680' Height='860' ResizeMode='NoResize' WindowStartupLocation='CenterOwner'>"
            + Resources + @"
  <Grid>
  <Grid.RowDefinitions><RowDefinition Height='40'/><RowDefinition Height='*'/></Grid.RowDefinitions>
  <Grid Grid.Row='0'>
    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0' WindowChrome.IsHitTestVisibleInChrome='True'>
      <Button x:Name='btnWinClose' Style='{StaticResource WinClose}' Content='&#xE8BB;'/>
    </StackPanel>
  </Grid>
  <Grid Grid.Row='1' Margin='20,6,20,18'>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
    <StackPanel Margin='0,0,0,12'>
      <TextBlock Text='Settings' FontSize='22' FontWeight='SemiBold'/>
      <TextBlock Style='{StaticResource Muted}' Text='Controller, mouse and keyboard, and the look of the program.'/>
    </StackPanel>
    <ScrollViewer Grid.Row='1' VerticalScrollBarVisibility='Auto'>
      <ScrollViewer.OpacityMask>
        <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
          <GradientStop Color='#00000000' Offset='0'/><GradientStop Color='#FF000000' Offset='0.03'/>
          <GradientStop Color='#FF000000' Offset='0.97'/><GradientStop Color='#00000000' Offset='1'/>
        </LinearGradientBrush>
      </ScrollViewer.OpacityMask>
    <StackPanel Margin='0,6,10,6'>
    <Border Style='{StaticResource Card}' VerticalAlignment='Top' Margin='0,0,0,12'>
      <StackPanel>
        <TextBlock Text='Input controller' FontWeight='SemiBold' FontSize='15'/>
        <TextBlock Style='{StaticResource Muted}' Text='Which controller the program reads. Applies at once. Xbox One / Series X|S and GameSir work through XInput (GameSir: put it in Xbox / XInput mode). XInput is not used while the virtual pad is in Xbox 360 mode.' TextWrapping='Wrap' Margin='0,2,0,10'/>
        <StackPanel x:Name='inputList'/>
      </StackPanel>
    </Border>
    <Border Style='{StaticResource Card}' VerticalAlignment='Top' Margin='0,0,0,12'>
      <StackPanel>
        <TextBlock Text='Mouse &amp; keyboard (M&amp;K mode)' FontWeight='SemiBold' FontSize='15'/>
        <TextBlock Style='{StaticResource Muted}' Text='In M&amp;K mode the right stick moves the mouse, Cross = left click, Circle = right click, the left stick scrolls. Press Record, then hold the buttons you want together and let go.' TextWrapping='Wrap' Margin='0,2,0,10'/>
        <StackPanel x:Name='mkPanel'/>
      </StackPanel>
    </Border>
    <Border Style='{StaticResource Card}' VerticalAlignment='Top' Margin='0,0,0,12'>
      <Grid>
        <StackPanel Margin='0,0,70,0'>
          <TextBlock Text='Start with Windows' FontWeight='SemiBold' FontSize='15'/>
          <TextBlock Style='{StaticResource Muted}' Text='Starts in the background with its icon in the tray. Click the icon to open the window.' TextWrapping='Wrap' Margin='0,2,0,0'/>
        </StackPanel>
        <CheckBox x:Name='chkAutoStart' Style='{StaticResource Switch}' HorizontalAlignment='Right' VerticalAlignment='Center'/>
      </Grid>
    </Border>
    <Border Style='{StaticResource Card}' VerticalAlignment='Top'>
      <StackPanel>
        <TextBlock Text='Theme' FontWeight='SemiBold' FontSize='15'/>
        <TextBlock Style='{StaticResource Muted}' Text='The look of the program.' TextWrapping='Wrap' Margin='0,2,0,10'/>
        <StackPanel x:Name='themeList'/>
      </StackPanel>
    </Border>
    </StackPanel>
    </ScrollViewer>
    <Grid Grid.Row='2' Margin='0,12,0,0' Visibility='Collapsed'>
      <TextBlock x:Name='txtThemeNote' Style='{StaticResource Muted}' TextWrapping='Wrap' VerticalAlignment='Center' Margin='0,0,190,0' FontSize='12'/>
      <Button x:Name='btnThemeApply' Content='Apply &amp; restart' Style='{StaticResource GreenBtn}' HorizontalAlignment='Right' Padding='18,10'/>
    </Grid>
  </Grid>
  </Grid>
</Window>";
    }

    // On-screen keyboard (English / Arabic) in the program's look. It never takes focus, so the text goes to whatever
    // program was in front; use it with the mouse or with M&K mode (Cross = click).
    static class Osk
    {
        static Window win;
        static StackPanel keys;
        static bool arabic;
        static int shift;   // 0 off, 1 next key only, 2 locked

        static readonly string[][] EnN = { new[] { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "[", "]", "\\" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", ";", "'" },
            new[] { "z", "x", "c", "v", "b", "n", "m", ",", ".", "/" } };
        static readonly string[][] EnS = { new[] { "~", "!", "@", "#", "$", "%", "^", "&", "*", "(", ")", "_", "+" },
            new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "{", "}", "|" },
            new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L", ":", "\"" },
            new[] { "Z", "X", "C", "V", "B", "N", "M", "<", ">", "?" } };
        static readonly string[][] ArN = { new[] { "ذ", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" },
            new[] { "ض", "ص", "ث", "ق", "ف", "غ", "ع", "ه", "خ", "ح", "ج", "د", "\\" },
            new[] { "ش", "س", "ي", "ب", "ل", "ا", "ت", "ن", "م", "ك", "ط" },
            new[] { "ئ", "ء", "ؤ", "ر", "لا", "ى", "ة", "و", "ز", "ظ" } };
        static readonly string[][] ArS = { new[] { "ّ", "!", "@", "#", "$", "%", "^", "&", "*", ")", "(", "_", "+" },
            new[] { "َ", "ً", "ُ", "ٌ", "لإ", "إ", "‘", "÷", "×", "؛", "<", ">", "|" },
            new[] { "ِ", "ٍ", "]", "[", "لأ", "أ", "ـ", "،", "/", ":", "\"" },
            new[] { "~", "ْ", "}", "{", "لآ", "آ", "’", ",", ".", "؟" } };

        public static bool IsOpen { get { return win != null && win.IsVisible; } }
        public static volatile bool Open;   // readable from the input thread
        static int selR = 1, selC = 0;
        static readonly List<List<Button>> grid = new List<List<Button>>();

        static void Hide() { win.Hide(); Open = false; }

        public static void Toggle()
        {
            if (IsOpen) { Hide(); return; }
            if (PadPanel.IsOpen) PadPanel.Toggle();   // one panel at a time
            if (win == null) Build();
            win.Show();
            Open = true;
            Place();
            Highlight();
        }

        // controller keys (see Engine.KbKey)
        public static void Nav(int code)
        {
            switch (code)
            {
                case 6: Native.PressVk(0x08, false); return;
                case 7: Native.PressVk(0x25, true); return;
                case 8: Native.PressVk(0x27, true); return;
                case 9: arabic = !arabic; if (win != null) Rebuild(); return;
                case 10: Native.PressVk(0x0D, false); return;
            }
            if (busy || !IsOpen || grid.Count == 0) return;
            if (code == 5)
            {
                if (zone == 1)
                {
                    Action a = grid[selR][Math.Min(selC, grid[selR].Count - 1)].Tag as Action;
                    if (a != null) a();
                }
                else QuickClick(qi);
                return;
            }
            if (zone == 1)
            {
                if (code == 3)
                {
                    if (selC > 0) selC--;
                    else { zone = 0; qi = Math.Min(selR, QuickRows - 1); }   // off the left edge: the shortcut buttons
                }
                else if (code == 4)
                {
                    if (selC < grid[selR].Count - 1) selC++;
                    else { zone = 2; qi = QuickRows + Math.Min(selR, QuickRows - 1); }
                }
                else
                {
                    int nr = selR + (code == 1 ? -1 : 1);
                    if (nr < 0 || nr >= grid.Count) return;
                    double cx = Center(grid[selR][Math.Min(selC, grid[selR].Count - 1)]), best = double.MaxValue;
                    int bc = 0;
                    for (int i = 0; i < grid[nr].Count; i++)
                    {
                        double d = Math.Abs(Center(grid[nr][i]) - cx);
                        if (d < best) { best = d; bc = i; }
                    }
                    selR = nr; selC = bc;
                }
            }
            else
            {
                int off = zone == 0 ? 0 : QuickRows, idx = qi - off;
                if (code == 1) idx = Math.Max(0, idx - 1);
                else if (code == 2) idx = Math.Min(QuickRows - 1, idx + 1);
                else if (code == (zone == 0 ? 4 : 3))   // back towards the keys
                {
                    selR = Math.Min(idx, grid.Count - 1);
                    selC = zone == 0 ? 0 : grid[selR].Count - 1;
                    zone = 1;
                }
                qi = off + idx;
            }
            Highlight();
        }

        // ---- shortcut buttons on both sides of the keys: each starts a program / shortcut picked by the user
        const int QuickRows = 5;
        public static string[] Quick = new string[QuickRows * 2];   // "path|label" or empty (index 0..4 left column, 5..9 right)
        public static Action QuickSave;
        static readonly Button[] qBtn = new Button[QuickRows * 2];
        static int zone = 1, qi;   // selection: 0 left shortcuts, 1 keys, 2 right shortcuts
        static bool busy;          // a file dialog is open

        static string QPath(int i) { string q = Quick[i]; if (string.IsNullOrEmpty(q)) return ""; int p = q.IndexOf('|'); return p < 0 ? q : q.Substring(0, p); }

        // a shortcut is either a program ("path|") or a key command ("keys:Ctrl+C|")
        public static bool IsKeys(int i) { return QPath(i).StartsWith("keys:"); }
        public static string KeysOf(int i) { return QPath(i).Substring(5); }

        public static string ComboText(System.Windows.Input.ModifierKeys m, System.Windows.Input.Key k)
        {
            List<string> p = new List<string>();
            if ((m & System.Windows.Input.ModifierKeys.Control) != 0) p.Add("Ctrl");
            if ((m & System.Windows.Input.ModifierKeys.Alt) != 0) p.Add("Alt");
            if ((m & System.Windows.Input.ModifierKeys.Shift) != 0) p.Add("Shift");
            if ((m & System.Windows.Input.ModifierKeys.Windows) != 0) p.Add("Win");
            string kn = k.ToString();
            if (k >= System.Windows.Input.Key.D0 && k <= System.Windows.Input.Key.D9) kn = ((int)k - (int)System.Windows.Input.Key.D0).ToString();
            p.Add(kn);
            return string.Join("+", p.ToArray());
        }

        public static ushort[] ComboVks(string combo)
        {
            List<ushort> v = new List<ushort>();
            foreach (string t in combo.Split('+'))
            {
                string s = t.Trim();
                if (s == "Ctrl") v.Add(0x11); else if (s == "Alt") v.Add(0x12); else if (s == "Shift") v.Add(0x10); else if (s == "Win") v.Add(0x5B);
                else
                {
                    System.Windows.Input.Key k;
                    if (s.Length == 1 && char.IsDigit(s[0])) k = (System.Windows.Input.Key)((int)System.Windows.Input.Key.D0 + (s[0] - '0'));
                    else if (!Enum.TryParse(s, out k)) continue;
                    int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                    if (vk != 0) v.Add((ushort)vk);
                }
            }
            return v.ToArray();
        }

        public static void RefreshQuick() { for (int i = 0; i < qBtn.Length; i++) QuickShow(i); }

        public static ImageSource IconOf(string path)
        {
            try
            {
                using (System.Drawing.Icon ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (ic == null) return null;
                    System.Windows.Media.Imaging.BitmapSource bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
            }
            catch (Exception) { return null; }
        }

        // the shortcut buttons exist twice: on the keyboard window (qBtn) and on the picture in the Shortcuts tab (pBtn)
        static readonly Button[] pBtn = new Button[QuickRows * 2];

        static void QuickShow(int i) { Fill(qBtn[i], i); Fill(pBtn[i], i); }

        // a picture of the keyboard for the Shortcuts tab: the letters are only drawn, the side buttons are live
        public static FrameworkElement BuildPreview(Action<Button, int> click)
        {
            StackPanel all = new StackPanel { Orientation = Orientation.Horizontal };
            StackPanel left = new StackPanel(), right = new StackPanel(), mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < QuickRows; i++) { left.Children.Add(PreviewSide(i, click)); right.Children.Add(PreviewSide(QuickRows + i, click)); }
            for (int r = 0; r < 4; r++)
            {
                StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                if (r == 2) row.Margin = new Thickness(26, 0, 0, 0);
                if (r == 3) row.Margin = new Thickness(52, 0, 0, 0);
                foreach (string k in EnN[r]) row.Children.Add(DeadKey(k, 46));
                if (r == 0) row.Children.Add(DeadKey("⌫", 92));
                if (r == 2) row.Children.Add(DeadKey("Enter", 92));
                mid.Children.Add(row);
            }
            StackPanel bot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            bot.Children.Add(DeadKey("⇧ Shift", 100)); bot.Children.Add(DeadKey("EN  →  ع", 100)); bot.Children.Add(DeadKey("Space", 300));
            bot.Children.Add(DeadKey("Tab", 62)); bot.Children.Add(DeadKey("←", 52)); bot.Children.Add(DeadKey("→", 52)); bot.Children.Add(DeadKey("Esc", 62));
            mid.Children.Add(bot);
            all.Children.Add(left); all.Children.Add(mid); all.Children.Add(right);
            return all;
        }

        static Button DeadKey(string text, double w)
        {
            Button b = Key(text, w, delegate { }, false);
            b.IsHitTestVisible = false;   // cannot be pressed or selected
            b.Opacity = 0.55;
            return b;
        }

        static Button PreviewSide(int i, Action<Button, int> click)
        {
            Button b = new Button();
            b.Width = 74; b.Height = 44; b.Margin = new Thickness(3);
            b.Style = KeyStyle();
            b.Foreground = new SolidColorBrush(Th.TextC);
            b.Click += delegate { click(b, i); };
            b.MouseRightButtonUp += delegate { click(b, i); };   // left or right click opens the menu
            pBtn[i] = b;
            Fill(b, i);
            return b;
        }

        static void Fill(Button b, int i)
        {
            if (b == null) return;   // not built yet
            string path = QPath(i);
            if (IsKeys(i))
            {
                b.Content = new TextBlock { Text = KeysOf(i), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 66, HorizontalAlignment = HorizontalAlignment.Center };
                b.ToolTip = "Sends " + KeysOf(i) + "\nRight-click: change or remove";
                return;
            }
            if (path == "")
            {
                b.Content = new TextBlock { Text = "+", FontSize = 22, Foreground = new SolidColorBrush(Th.DimC), HorizontalAlignment = HorizontalAlignment.Center };
                b.ToolTip = "Add a shortcut: click to pick a program";
                return;
            }
            string label = Path.GetFileNameWithoutExtension(path);
            StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            ImageSource ico = IconOf(path);
            if (ico != null) sp.Children.Add(new Image { Source = ico, Width = 22, Height = 22, Margin = new Thickness(0, 0, 0, 1) });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10, MaxWidth = 66, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
            b.Content = sp;
            b.ToolTip = path + "\nRight-click: change or remove";
        }

        public static void QuickPick(int i)
        {
            Microsoft.Win32.OpenFileDialog d = new Microsoft.Win32.OpenFileDialog();
            d.Title = "Pick a program or shortcut";
            d.Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.url|All files|*.*";
            d.DereferenceLinks = false;   // keep a .lnk as it is so its arguments still apply
            busy = true;
            try
            {
                if (d.ShowDialog() == true) { Quick[i] = d.FileName; QuickShow(i); Action s = QuickSave; if (s != null) s(); }
            }
            finally { busy = false; }
        }

        static void QuickClick(int i)
        {
            string path = QPath(i);
            if (path == "") { QuickPick(i); return; }
            if (IsKeys(i)) { Native.PressCombo(ComboVks(KeysOf(i))); return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(path);
                psi.UseShellExecute = true;
                try { string dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir; } catch (Exception) { }
                Process.Start(psi);
            }
            catch (Exception ex) { Dlg.Show(null, ex.Message, "OK", null, false); }
        }

        static Button QuickButton(int i)
        {
            Button b = new Button();
            b.Width = 74; b.Height = 44; b.Margin = new Thickness(3);
            b.Style = KeyStyle();
            b.Foreground = new SolidColorBrush(Th.TextC);
            b.Click += delegate { QuickClick(i); };
            b.Tag = (Action)delegate { QuickClick(i); };
            ContextMenu cm = new ContextMenu();
            MenuItem change = new MenuItem { Header = "Change..." };
            change.Click += delegate { QuickPick(i); };
            MenuItem remove = new MenuItem { Header = "Remove" };
            remove.Click += delegate { Quick[i] = ""; QuickShow(i); Action s = QuickSave; if (s != null) s(); };
            cm.Items.Add(change); cm.Items.Add(remove);
            b.ContextMenu = cm;
            qBtn[i] = b;
            QuickShow(i);
            return b;
        }

        static double Center(Button b)
        {
            return b.TranslatePoint(new Point(b.ActualWidth / 2, 0), keys).X;
        }

        static void Highlight()
        {
            if (grid.Count == 0) return;
            selR = Math.Min(selR, grid.Count - 1);
            selC = Math.Min(selC, grid[selR].Count - 1);
            for (int r = 0; r < grid.Count; r++)
                for (int c = 0; c < grid[r].Count; c++)
                {
                    bool on = zone == 1 && r == selR && c == selC;
                    Button b = grid[r][c];
                    b.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
                    b.BorderThickness = new Thickness(on ? 2.5 : 1);
                }
            for (int i = 0; i < qBtn.Length; i++)
            {
                Button b = qBtn[i];
                if (b == null) continue;
                bool on = zone != 1 && i == qi;
                b.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
                b.BorderThickness = new Thickness(on ? 2.5 : 1);
            }
        }

        static void Place()
        {
            win.UpdateLayout();
            Rect wa = SystemParameters.WorkArea;
            win.Left = wa.Left + (wa.Width - win.ActualWidth) / 2;
            win.Top = wa.Bottom - win.ActualHeight - 24;
        }

        public static Style KeyStyle()
        {
            ControlTemplate t = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border x:Name='b' CornerRadius='9' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#6634C759'/></Trigger>" +
                "<Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='#BB34C759'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            Style s = new Style(typeof(Button));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            s.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))));
            s.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Th.TextC)));
            s.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF))));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 17.0));
            s.Setters.Add(new Setter(Control.FocusableProperty, false));
            return s;
        }

        static Button Key(string text, double w, Action click, bool active)
        {
            Button b = new Button();
            b.Content = text; b.Width = w; b.Height = 44; b.Margin = new Thickness(3);
            b.Style = KeyStyle();
            if (active) b.Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x34, 0xC7, 0x59));
            b.Click += delegate { click(); };
            b.Tag = click;
            return b;
        }

        static string Show(string s)
        {
            if (s.Length == 1 && s[0] >= 'ً' && s[0] <= 'ْ') return "◌" + s;   // diacritics are drawn on a dotted circle
            return s;
        }

        static void Send(string s)
        {
            Native.TypeText(s);
            if (shift == 1) { shift = 0; Rebuild(); }
        }

        static void Rebuild()
        {
            keys.Children.Clear();
            grid.Clear();
            string[][] n = arabic ? ArN : EnN, sh = arabic ? ArS : EnS;
            for (int r = 0; r < 4; r++)
            {
                StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                if (r == 2) row.Margin = new Thickness(26, 0, 0, 0);
                if (r == 3) row.Margin = new Thickness(52, 0, 0, 0);
                for (int i = 0; i < n[r].Length; i++)
                {
                    string ch = shift != 0 ? sh[r][i] : n[r][i];
                    string send = ch;
                    row.Children.Add(Key(Show(ch), 46, delegate { Send(send); }, false));
                }
                if (r == 0) row.Children.Add(Key("⌫", 92, delegate { Native.PressVk(0x08, false); }, false));
                if (r == 2) row.Children.Add(Key("Enter", 92, delegate { Native.PressVk(0x0D, false); }, false));
                keys.Children.Add(row);
                grid.Add(RowButtons(row));
            }
            StackPanel bot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            bot.Children.Add(Key(shift == 2 ? "⇪ Lock" : "⇧ Shift", 100, delegate { shift = shift == 0 ? 1 : (shift == 1 ? 2 : 0); Rebuild(); }, shift != 0));
            bot.Children.Add(Key(arabic ? "ع  →  EN" : "EN  →  ع", 100, delegate { arabic = !arabic; Rebuild(); }, false));
            bot.Children.Add(Key("Space", 300, delegate { Native.TypeText(" "); }, false));
            bot.Children.Add(Key("Tab", 62, delegate { Native.PressVk(0x09, false); }, false));
            bot.Children.Add(Key("←", 52, delegate { Native.PressVk(0x25, true); }, false));
            bot.Children.Add(Key("→", 52, delegate { Native.PressVk(0x27, true); }, false));
            bot.Children.Add(Key("Esc", 62, delegate { Native.PressVk(0x1B, false); }, false));
            keys.Children.Add(bot);
            grid.Add(RowButtons(bot));
            if (win != null && win.IsVisible) { win.UpdateLayout(); Highlight(); }
        }

        static List<Button> RowButtons(StackPanel row)
        {
            List<Button> l = new List<Button>();
            foreach (UIElement u in row.Children) { Button b = u as Button; if (b != null) l.Add(b); }
            return l;
        }

        static void Build()
        {
            win = new Window();
            win.WindowStyle = WindowStyle.None;
            win.ResizeMode = ResizeMode.NoResize;
            win.ShowInTaskbar = false;
            win.ShowActivated = false;
            win.Topmost = true;
            win.SizeToContent = SizeToContent.WidthAndHeight;
            win.FontFamily = new FontFamily("Bahnschrift, Segoe UI");
            win.Background = Th.WindowBrush(Th.T);
            win.FlowDirection = FlowDirection.LeftToRight;
            win.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(win).Handle;
                Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x08000000 | 0x00000080);   // NOACTIVATE | TOOLWINDOW
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);   // rounded corners
            };
            Border frame = new Border();
            frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x34, 0xC7, 0x59));
            frame.BorderThickness = new Thickness(1.5);
            frame.Padding = new Thickness(12, 6, 12, 12);
            StackPanel all = new StackPanel();
            Grid bar = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
            bar.MouseLeftButtonDown += delegate { try { win.DragMove(); } catch (Exception) { } };
            StackPanel ttl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Image ic = new Image { Source = Th.AppIcon(16), Width = 16, Height = 16, Margin = new Thickness(2, 0, 8, 0) };
            ttl.Children.Add(ic);
            ttl.Children.Add(new TextBlock { Text = "D-pad / L3 stick move · ✕ type · □ ⌫ · L1/R1 ◀ ▶ · L2 EN/ع · R2 Enter", Foreground = new SolidColorBrush(Th.DimC), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(ttl);
            Button close = Key("✕", 34, delegate { Hide(); }, false);
            close.Height = 26; close.FontSize = 12; close.HorizontalAlignment = HorizontalAlignment.Right;
            bar.Children.Add(close);
            all.Children.Add(bar);
            keys = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            StackPanel left = new StackPanel(), right = new StackPanel();
            for (int i = 0; i < QuickRows; i++) { left.Children.Add(QuickButton(i)); right.Children.Add(QuickButton(QuickRows + i)); }
            StackPanel mid = new StackPanel { Orientation = Orientation.Horizontal };
            mid.Children.Add(left);
            mid.Children.Add(keys);
            mid.Children.Add(right);
            all.Children.Add(mid);
            frame.Child = all;
            win.Content = frame;
            Rebuild();
        }
    }

    // start with Windows: a value in the current user's Run key; the program then starts hidden in the tray (--startup)
    static class AutoStart
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "AL-SeRry";

        public static bool IsOn()
        {
            try { using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath)) return k != null && k.GetValue(ValueName) != null; }
            catch (Exception) { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(ValueName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --startup");
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception) { }
        }
    }

    // confirmation / message window in the look of the program (instead of the plain Windows message box)
    static class Dlg
    {
        // asks for a line of text; returns it, or null when cancelled
        public static string Ask(Window owner, string prompt, string initial)
        {
            Window w = (Window)XamlReader.Parse(Th.Fill(Xaml.Dialog));
            if (owner != null && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            TextBlock msg = (TextBlock)w.FindName("txtMsg");
            msg.Text = prompt;
            msg.Margin = new Thickness(0, 0, 0, 12);
            TextBox tb = new TextBox { Style = (Style)w.FindResource("Field"), Text = initial ?? "", MaxLength = 30, FontSize = 15, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 20) };
            ((Panel)msg.Parent).Children.Insert(1, tb);
            Button by = (Button)w.FindName("btnYes"), bn = (Button)w.FindName("btnNo");
            by.Content = "OK"; bn.Content = "Cancel";
            string result = null;
            by.Click += delegate { result = tb.Text.Trim(); w.Close(); };
            bn.Click += delegate { w.Close(); };
            tb.KeyDown += delegate (object s, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) { result = tb.Text.Trim(); w.Close(); } };
            w.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);
                w.Background = Th.WindowBrush(Th.T);
            };
            w.Loaded += delegate { tb.Focus(); tb.SelectAll(); };
            w.ShowDialog();
            return result;
        }

        // asks the user to hold a controller button combination and let go; returns its button bits, or 0 when cancelled / nothing
        public static int Combo(Window owner, Engine eng)
        {
            Window w = (Window)XamlReader.Parse(Th.Fill(Xaml.Dialog));
            if (owner != null && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            TextBlock msg = (TextBlock)w.FindName("txtMsg");
            ((Button)w.FindName("btnYes")).Visibility = Visibility.Collapsed;
            Button cancel = (Button)w.FindName("btnNo");
            cancel.Content = "Cancel";
            cancel.Click += delegate { w.Close(); };
            int result = 0;
            DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            if (!eng.Connected) msg.Text = "Connect a controller first.";
            else
            {
                msg.Text = "Hold the controller buttons you want together, then let go.";
                eng.BeginCapture();
                int ticks = 0;
                t.Tick += delegate
                {
                    ticks++;
                    if (eng.CaptureState == 2) { result = eng.CaptureMask; w.Close(); }
                    else if (ticks > 150) w.Close();
                };
                t.Start();
            }
            w.Closed += delegate { t.Stop(); eng.CaptureState = 0; };
            w.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);
                w.Background = Th.WindowBrush(Th.T);
            };
            w.ShowDialog();
            return result;
        }

        // asks the user to press a key command (Ctrl+C ...); returns its text, or null when cancelled
        public static string Keys(Window owner)
        {
            Window w = (Window)XamlReader.Parse(Th.Fill(Xaml.Dialog));
            if (owner != null && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            TextBlock msg = (TextBlock)w.FindName("txtMsg");
            msg.Text = "Press the keys for this button.\nEsc cancels. Windows keeps some combinations for itself (Alt+Tab ...): pick those below.";
            string result = null;
            WrapPanel presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
            foreach (string pc in new string[] { "Alt+Tab", "Alt+Shift+Tab", "Win+Tab", "Win+D", "Alt+F4", "Win+E" })
            {
                string pick = pc;
                Button pb = new Button { Content = pc, Style = (Style)w.FindResource("Glass"), Padding = new Thickness(14, 6, 14, 6), FontSize = 12, Margin = new Thickness(0, 0, 8, 8) };
                pb.Click += delegate { result = pick; w.Close(); };
                presets.Children.Add(pb);
            }
            ((Panel)msg.Parent).Children.Insert(1, presets);
            ((Button)w.FindName("btnYes")).Visibility = Visibility.Collapsed;
            Button cancel = (Button)w.FindName("btnNo");
            cancel.Content = "Cancel";
            cancel.Click += delegate { w.Close(); };
            w.PreviewKeyDown += delegate (object s, System.Windows.Input.KeyEventArgs e)
            {
                e.Handled = true;
                System.Windows.Input.Key k = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                System.Windows.Input.ModifierKeys mods = System.Windows.Input.Keyboard.Modifiers;
                if (k == System.Windows.Input.Key.LeftCtrl || k == System.Windows.Input.Key.RightCtrl || k == System.Windows.Input.Key.LeftShift || k == System.Windows.Input.Key.RightShift
                    || k == System.Windows.Input.Key.LeftAlt || k == System.Windows.Input.Key.RightAlt || k == System.Windows.Input.Key.LWin || k == System.Windows.Input.Key.RWin
                    || k == System.Windows.Input.Key.ImeProcessed || k == System.Windows.Input.Key.DeadCharProcessed) return;   // wait for the real key
                if (k == System.Windows.Input.Key.Escape && mods == System.Windows.Input.ModifierKeys.None) { w.Close(); return; }
                result = Osk.ComboText(mods, k);
                w.Close();
            };
            w.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);
                w.Background = Th.WindowBrush(Th.T);
            };
            w.ShowDialog();
            return result;
        }

        public static bool Confirm(Window owner, string text, string yes, bool danger) { return Show(owner, text, yes, "Cancel", danger); }

        public static bool Show(Window owner, string text, string yes, string no, bool danger)
        {
            Window w = (Window)XamlReader.Parse(Th.Fill(Xaml.Dialog));
            if (owner != null && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ((TextBlock)w.FindName("txtMsg")).Text = text;
            Button by = (Button)w.FindName("btnYes"), bn = (Button)w.FindName("btnNo");
            by.Content = yes;
            by.Style = (Style)w.FindResource(danger ? "RedBtn" : "GreenBtn");
            if (no == null) bn.Visibility = Visibility.Collapsed; else bn.Content = no;
            bool result = false;
            by.Click += delegate { result = true; w.Close(); };
            bn.Click += delegate { w.Close(); };
            w.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);
                w.Background = Th.WindowBrush(Th.T);
            };
            w.ShowDialog();
            return result;
        }
    }

    // DualSense drawn as clean lines in the program's identity (green glow over dark glass), every button shown:
    // L1/R1, d-pad, face buttons, sticks, touchpad, Create, Options, PS, mute and the speaker grill. 600 x 430 units.
    static class PadArt
    {
        static readonly Color Acc = Color.FromRgb(0x34, 0xC7, 0x59);

        static System.Windows.Shapes.Path P(Canvas c, string data, Brush stroke, double th, Brush fill)
        {
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            p.Data = Geometry.Parse(data);
            p.Stroke = stroke; p.StrokeThickness = th; p.Fill = fill;
            p.StrokeLineJoin = PenLineJoin.Round; p.StrokeStartLineCap = PenLineCap.Round; p.StrokeEndLineCap = PenLineCap.Round;
            c.Children.Add(p);
            return p;
        }

        static void Ring(Canvas c, double cx, double cy, double r, Brush stroke, double th, Brush fill)
        {
            System.Windows.Shapes.Ellipse e = new System.Windows.Shapes.Ellipse();
            e.Width = e.Height = r * 2; e.Stroke = stroke; e.StrokeThickness = th; e.Fill = fill;
            Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
            c.Children.Add(e);
        }

        static void Pill(Canvas c, double x, double y, double w, double h, Brush stroke, double th)
        {
            System.Windows.Shapes.Rectangle r = new System.Windows.Shapes.Rectangle();
            r.Width = w; r.Height = h; r.RadiusX = r.RadiusY = Math.Min(w, h) / 2;
            r.Stroke = stroke; r.StrokeThickness = th;
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            c.Children.Add(r);
        }

        // style 0 = glow outline over glass, 1 = plain thin lines, 2 = solid dark body with bright buttons (like a silhouette)
        // style 3: the layout of the user's reference picture (solid body, side seams, plain round face buttons, ringed sticks)
        // in the program's colours, plus the PS button with the PlayStation mark as on a real DualSense
        static Canvas BuildRef()
        {
            Canvas c = new Canvas { Width = 600, Height = 430 };
            Brush acc = new SolidColorBrush(Acc);
            Brush dark = new SolidColorBrush(Color.FromRgb(0x0C, 0x1C, 0x12));
            Brush bodyFill = new LinearGradientBrush(Color.FromRgb(0x22, 0x40, 0x2F), Color.FromRgb(0x0C, 0x1C, 0x12), 90);
            Brush padFill = new SolidColorBrush(Color.FromRgb(0x09, 0x15, 0x0E));

            // L1 / R1
            P(c, "M 136,128 C 141,105 170,96 211,96 L 213,107 C 176,107 151,114 145,131 Z", acc, 2, dark);
            P(c, "M 464,128 C 459,105 430,96 389,96 L 387,107 C 424,107 449,114 455,131 Z", acc, 2, dark);

            const string bodyData =
                "M 300,110 C 360,110 400,106 440,113 C 480,119 505,131 515,160 C 535,215 560,300 565,350 " +
                "C 570,395 545,415 520,405 C 495,395 470,345 445,318 C 425,305 400,298 370,298 L 230,298 " +
                "C 200,298 175,305 155,318 C 130,345 105,395 80,405 C 55,415 30,395 35,350 " +
                "C 40,300 65,215 85,160 C 95,131 120,119 160,113 C 200,106 240,110 300,110 Z";
            System.Windows.Shapes.Path body = P(c, bodyData, acc, 2.5, bodyFill);
            body.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Acc, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.45 };

            // top edge line, side seams, lower grip rims: clipped to the body so they end on its edge
            Canvas inner = new Canvas { Width = 600, Height = 430, Clip = Geometry.Parse(bodyData) };
            c.Children.Add(inner);
            P(inner, "M 175,121 C 230,114 270,115 300,115 C 330,115 370,114 425,121", acc, 1.8, null);
            P(inner, "M 210,124 C 212,160 220,188 228,206 C 210,222 196,240 192,262 C 186,292 168,306 142,326 C 122,344 100,372 74,410", acc, 2, null);
            P(inner, "M 390,124 C 388,160 380,188 372,206 C 390,222 404,240 408,262 C 414,292 432,306 458,326 C 478,344 500,372 526,410", acc, 2, null);
            P(inner, "M 46,320 C 44,372 58,398 90,408", acc, 2, null);
            P(inner, "M 554,320 C 556,372 542,398 510,408", acc, 2, null);

            // touchpad, Create / Options
            P(c, "M 222,118 L 378,118 Q 384,118 383,125 L 372,198 Q 371,206 363,206 L 237,206 Q 229,206 228,198 L 217,125 Q 216,118 222,118 Z", acc, 2, padFill);
            P(c, "M 207,138 L 207,152", acc, 6, null);
            P(c, "M 393,138 L 393,152", acc, 6, null);

            // d-pad: bright arrow keys with dark arrow marks
            P(c, "M 152,150 L 168,150 L 168,165 L 160,174 L 152,165 Z", acc, 1.5, acc);
            P(c, "M 152,220 L 168,220 L 168,205 L 160,196 L 152,205 Z", acc, 1.5, acc);
            P(c, "M 126,177 L 126,193 L 141,193 L 150,185 L 141,177 Z", acc, 1.5, acc);
            P(c, "M 194,177 L 194,193 L 179,193 L 170,185 L 179,177 Z", acc, 1.5, acc);
            P(c, "M 160,157 L 164,163 L 156,163 Z", dark, 1, dark);
            P(c, "M 160,213 L 164,207 L 156,207 Z", dark, 1, dark);
            P(c, "M 133,185 L 139,181 L 139,189 Z", dark, 1, dark);
            P(c, "M 187,185 L 181,181 L 181,189 Z", dark, 1, dark);

            // face buttons: plain bright circles
            Ring(c, 440, 156, 14, acc, 0, acc);
            Ring(c, 469, 185, 14, acc, 0, acc);
            Ring(c, 440, 214, 14, acc, 0, acc);
            Ring(c, 411, 185, 14, acc, 0, acc);

            // sticks: thick outer ring, inner ring, dark cap
            foreach (double sx in new double[] { 232, 368 })
            {
                Ring(c, sx, 252, 31, acc, 3.5, dark);
                Ring(c, sx, 252, 21, acc, 3, padFill);
                Ring(c, sx, 252, 12, new SolidColorBrush(Color.FromArgb(0x99, 0x34, 0xC7, 0x59)), 1.5, null);
            }

            // speaker grill
            foreach (double dx in new double[] { 288, 294, 300, 306, 312 }) Ring(c, dx, 222, 2.3, acc, 1.2, null);
            foreach (double dx in new double[] { 291, 297, 303, 309 }) Ring(c, dx, 230, 2.3, acc, 1.2, null);

            // PS button: the PlayStation mark (upright P over a flat S), then the mute button below it
            P(c, "M 279,268 C 283,262 291,262 298,265 C 305,268 313,268 320,262", acc, 3.4, null);   // the flat S
            // the upright P, standing on the S; F0 = even-odd, so the bowl keeps its hole
            P(c, "F0 M 292,238 L 301,240 L 301,246 C 310,245 315,249 314,255 C 313,261 307,263 301,261 L 301,269 L 292,267 Z " +
                "M 301,250 C 305,249 307,251 306,254 C 305,256 303,256 301,256 Z", dark, 1.4, acc);
            P(c, "M 289,282 L 311,282", acc, 6, null);
            P(c, "M 291,282 L 309,282", dark, 2.5, null);
            return c;
        }

        public static Canvas Build(int style)
        {
            if (style == 3) return BuildRef();
            Canvas c = new Canvas { Width = 600, Height = 430 };
            Brush acc = new SolidColorBrush(Acc);
            Brush light = style == 1 ? acc : new SolidColorBrush(Color.FromRgb(0xA8, 0xF0, 0xBE));
            Brush dim = new SolidColorBrush(Color.FromArgb(0x66, 0x34, 0xC7, 0x59));
            Brush glass = style == 1 ? null
                : style == 2 ? (Brush)new LinearGradientBrush(Color.FromRgb(0x2A, 0x4A, 0x37), Color.FromRgb(0x0F, 0x21, 0x16), 90)
                : new LinearGradientBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90);
            Brush btnFill = style == 1 ? null : style == 2 ? acc : new SolidColorBrush(Color.FromArgb(0x22, 0x34, 0xC7, 0x59));
            Brush edge = new SolidColorBrush(Acc);   // outline of the body and the bumpers
            Brush padFill = style == 2 ? null : btnFill;   // touchpad and sticks stay body-coloured in the solid style
            if (style == 2) { light = edge; acc = new SolidColorBrush(Color.FromRgb(0x0F, 0x21, 0x16)); }   // dark symbols on bright buttons

            // L1 / R1 bumpers, behind the body
            P(c, "M 112,126 C 122,104 160,95 210,100", edge, 7, null);
            P(c, "M 488,126 C 478,104 440,95 390,100", edge, 7, null);

            // body
            System.Windows.Shapes.Path body = P(c,
                "M 300,108 C 360,108 400,104 440,112 C 480,118 505,130 515,160 C 535,215 560,300 565,350 " +
                "C 570,395 545,415 520,405 C 495,395 470,345 445,318 C 425,305 400,298 370,298 L 230,298 " +
                "C 200,298 175,305 155,318 C 130,345 105,395 80,405 C 55,415 30,395 35,350 " +
                "C 40,300 65,215 85,160 C 95,130 120,118 160,112 C 200,104 240,108 300,108 Z", edge, style == 1 ? 2.5 : 3, glass);
            if (style != 1) body.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Acc, BlurRadius = style == 2 ? 14 : 24, ShadowDepth = 0, Opacity = style == 2 ? 0.45 : 0.6 };

            // grip seams (the two-tone line of the real pad)
            P(c, "M 214,130 C 206,185 200,222 182,262 C 172,284 162,302 150,322", dim, 1.5, null);
            P(c, "M 386,130 C 394,185 400,222 418,262 C 428,284 438,302 450,322", dim, 1.5, null);

            // touchpad
            P(c, "M 222,116 L 378,116 Q 384,116 383,123 L 372,198 Q 371,206 363,206 L 237,206 Q 229,206 228,198 L 217,123 Q 216,116 222,116 Z", light, 2, padFill);

            // Create / Options
            Pill(c, 199, 124, 7, 20, light, 2);
            Pill(c, 394, 124, 7, 20, light, 2);

            // d-pad: four arrow keys around (160,185)
            P(c, "M 152,150 L 168,150 L 168,165 L 160,174 L 152,165 Z", light, 2, btnFill);
            P(c, "M 152,220 L 168,220 L 168,205 L 160,196 L 152,205 Z", light, 2, btnFill);
            P(c, "M 126,177 L 126,193 L 141,193 L 150,185 L 141,177 Z", light, 2, btnFill);
            P(c, "M 194,177 L 194,193 L 179,193 L 170,185 L 179,177 Z", light, 2, btnFill);
            P(c, "M 160,157 L 164,163 L 156,163 Z", acc, 1, acc);
            P(c, "M 160,213 L 164,207 L 156,207 Z", acc, 1, acc);
            P(c, "M 133,185 L 139,181 L 139,189 Z", acc, 1, acc);
            P(c, "M 187,185 L 181,181 L 181,189 Z", acc, 1, acc);

            // face buttons around (440,185) with their symbols
            Ring(c, 440, 157, 13, light, 2, btnFill);
            Ring(c, 468, 185, 13, light, 2, btnFill);
            Ring(c, 440, 213, 13, light, 2, btnFill);
            Ring(c, 412, 185, 13, light, 2, btnFill);
            P(c, "M 440,150 L 447,162 L 433,162 Z", acc, 1.8, null);
            Ring(c, 468, 185, 6, acc, 1.8, null);
            P(c, "M 434,207 L 446,219 M 446,207 L 434,219", acc, 1.8, null);
            P(c, "M 406.5,179.5 L 417.5,179.5 L 417.5,190.5 L 406.5,190.5 Z", acc, 1.8, null);

            // sticks
            foreach (double sx in new double[] { 232, 368 })
            {
                Ring(c, sx, 250, 30, light, 2, null);
                Ring(c, sx, 250, 20, style == 2 ? light : acc, 1.5, padFill);
                Ring(c, sx, 250, 8, dim, 1.5, null);
            }

            // speaker grill under the touchpad
            foreach (double dx in new double[] { 288, 294, 300, 306, 312 }) Ring(c, dx, 215, 1.8, light, 0, light);
            foreach (double dx in new double[] { 291, 297, 303, 309 }) Ring(c, dx, 222, 1.8, light, 0, light);

            // PS button and mute button between the sticks
            Ring(c, 300, 247, 11, light, 2, btnFill);
            TextBlock ps = new TextBlock { Text = "PS", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = acc, Width = 22, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(ps, 289); Canvas.SetTop(ps, 241);
            c.Children.Add(ps);
            Pill(c, 289, 270, 22, 7, light, 2);
            return c;
        }

        // writes the drawing as a transparent PNG (twice the size): AL-SeRry.exe --export-art file.png
        public static void Export(string file, int style)
        {
            Canvas c = Build(style);
            c.Measure(new Size(600, 430)); c.Arrange(new Rect(0, 0, 600, 430));
            System.Windows.Media.Imaging.RenderTargetBitmap bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 860, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(c);
            System.Windows.Media.Imaging.PngBitmapEncoder enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(file)) enc.Save(fs);
        }
    }

    // PlayStation button icons drawn in code, ported from the retBox program (src\Glyphs.cs, the PS3 look): black glossy discs with
    // coloured symbols, L1 / R1 / L2 / R2 shoulders, lit d-pad arms, stick caps, SHARE / OPTIONS pills.
    // Names follow Engine.BtnNames: Up Down Left Right Square Cross Circle Triangle L1 R1 Share Options L3 R3 PS Touchpad L2 R2.
    static class Glyphs
    {
        static Color H(string h) { return (Color)ColorConverter.ConvertFromString(h); }
        static Color Mix(Color a, Color b, double k) { return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k)); }
        // "Classic" look of retBox (PlayStation 3 Classic): flat colours, no gloss, drawn small and scaled softly like old games
        static bool flat;
        static Brush Lin(Color a, Color b)
        {
            if (flat) { SolidColorBrush sb = new SolidColorBrush(Mix(a, b, 0.5)); sb.Freeze(); return sb; }
            LinearGradientBrush g = new LinearGradientBrush(a, b, 90); g.Freeze(); return g;
        }

        static System.Windows.Shapes.Ellipse Disc(double x, double y, double d, Brush b)
        {
            System.Windows.Shapes.Ellipse e = new System.Windows.Shapes.Ellipse { Width = d, Height = d, Fill = b };
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            return e;
        }

        static System.Windows.Shapes.Ellipse Gloss(double x, double y, double w, double h)
        {
            if (flat) return new System.Windows.Shapes.Ellipse { Width = 0, Height = 0 };
            System.Windows.Shapes.Ellipse e = new System.Windows.Shapes.Ellipse { Width = w, Height = h, Fill = Lin(Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)) };
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            return e;
        }

        static TextBlock Label(string text, double size, double width, double top, Brush fg)
        {
            TextBlock t = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, FontSize = size, Foreground = fg, Width = width, TextAlignment = TextAlignment.Center };
            Canvas.SetTop(t, top);
            return t;
        }

        // one icon, s pixels high (shoulders and pills are wider), in the classic flat look
        public static FrameworkElement Make(string btn, double s)
        {
            flat = true;
            try
            {
                int px = (int)Math.Max(20, Math.Round(s * 0.7));   // draw small, then scale: the soft, slightly blocky look of old games
                FrameworkElement big = Build(btn, px);
                big.Measure(new Size(px * 4, px)); big.Arrange(new Rect(0, 0, big.DesiredSize.Width, px));
                System.Windows.Media.Imaging.RenderTargetBitmap rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(big.DesiredSize.Width), px, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(big); rtb.Freeze();
                Image im = new Image { Source = rtb, Height = s, Width = s * rtb.PixelWidth / rtb.PixelHeight, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.Linear);
                return im;
            }
            finally { flat = false; }
        }

        static FrameworkElement Build(string btn, double s)
        {
            Canvas c = new Canvas { Width = 100, Height = 100 };
            double w = 100;
            switch (btn)
            {
                case "Cross": case "Circle": case "Square": case "Triangle": Face(c, btn); break;
                case "L1": case "R1": case "L2": case "R2":
                    w = 170; c.Width = w; Shoulder(c, w, btn, btn[1] == '2'); break;
                case "Share": case "Options": case "Touchpad":
                    w = btn == "Touchpad" ? 250 : 210; c.Width = w; Pill(c, w, btn == "Share" ? "SHARE" : btn == "Options" ? "OPTIONS" : "TOUCHPAD"); break;
                case "L3": case "R3": StickCap(c, btn); break;
                case "PS":
                    c.Children.Add(Disc(0, 0, 100, Lin(H("#4A4A4A"), H("#050505"))));
                    c.Children.Add(Disc(5, 5, 90, Lin(H("#2A2A2A"), H("#0B0B0B"))));
                    c.Children.Add(Label("PS", 40, 100, 26, Brushes.White));
                    c.Children.Add(Gloss(14, 9, 72, 36));
                    break;
                default: DPadArm(c, btn); break;   // Up Down Left Right
            }
            return new Viewbox { Height = s, Width = s * w / 100, Child = c, Stretch = Stretch.Uniform };
        }

        static void Face(Canvas c, string b)
        {
            c.Children.Add(Disc(0, 0, 100, Lin(H("#4A4A4A"), H("#050505"))));
            c.Children.Add(Disc(5, 5, 90, Lin(H("#2A2A2A"), H("#0B0B0B"))));
            System.Windows.Shapes.Shape sym;
            if (b == "Cross")
                sym = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 32,32 L 68,68 M 68,32 L 32,68"), Stroke = new SolidColorBrush(H("#7FB2FF")), StrokeThickness = 9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            else if (b == "Circle")
                sym = new System.Windows.Shapes.Ellipse { Width = 42, Height = 42, Stroke = new SolidColorBrush(H("#FF5B5B")), StrokeThickness = 8 };
            else if (b == "Square")
                sym = new System.Windows.Shapes.Rectangle { Width = 38, Height = 38, Stroke = new SolidColorBrush(H("#F07FD2")), StrokeThickness = 8 };
            else
                sym = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 50,27 L 73,67 L 27,67 Z"), Stroke = new SolidColorBrush(H("#3FE0A4")), StrokeThickness = 8, StrokeLineJoin = PenLineJoin.Round };
            if (sym is System.Windows.Shapes.Ellipse) { Canvas.SetLeft(sym, 29); Canvas.SetTop(sym, 29); }
            if (sym is System.Windows.Shapes.Rectangle) { Canvas.SetLeft(sym, 31); Canvas.SetTop(sym, 31); }
            c.Children.Add(sym);
            c.Children.Add(Gloss(14, 9, 72, 36));
        }

        static void Shoulder(Canvas c, double w, string text, bool trigger)
        {
            System.Windows.Shapes.Rectangle r = new System.Windows.Shapes.Rectangle { Width = w - 4, Height = trigger ? 92 : 76, RadiusX = trigger ? 26 : 38, RadiusY = trigger ? 30 : 38,
                Fill = Lin(H("#5A5A5A"), H("#141414")), Stroke = new SolidColorBrush(H("#000000")), StrokeThickness = 4 };
            Canvas.SetLeft(r, 2); Canvas.SetTop(r, trigger ? 4 : 12);
            c.Children.Add(r);
            c.Children.Add(Label(text, 46, w, 21, Brushes.White));
            if (flat) return;
            System.Windows.Shapes.Rectangle gl = new System.Windows.Shapes.Rectangle { Width = w - 30, Height = 20, RadiusX = 10, RadiusY = 10, Fill = Lin(Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)) };
            Canvas.SetLeft(gl, 15); Canvas.SetTop(gl, trigger ? 10 : 17);
            c.Children.Add(gl);
        }

        static void Pill(Canvas c, double w, string text)
        {
            System.Windows.Shapes.Rectangle r = new System.Windows.Shapes.Rectangle { Width = w - 4, Height = 64, RadiusX = 32, RadiusY = 32, Fill = Lin(H("#3A3A3A"), H("#0A0A0A")), Stroke = Brushes.Black, StrokeThickness = 4 };
            Canvas.SetLeft(r, 2); Canvas.SetTop(r, 18);
            c.Children.Add(r);
            c.Children.Add(Label(text, 34, w, 28, new SolidColorBrush(H("#E8E8E8"))));
        }

        static void StickCap(Canvas c, string text)
        {
            c.Children.Add(Disc(0, 0, 100, Lin(H("#3E3E3E"), H("#050505"))));
            c.Children.Add(Disc(14, 14, 72, Lin(H("#2C2C2C"), H("#121212"))));
            c.Children.Add(Label(text, 34, 100, 29, Brushes.White));
        }

        // the d-pad cross, only the pressed arm is lit
        static void DPadArm(Canvas c, string dir)
        {
            Brush body = Lin(H("#4A4A4A"), H("#151515")), lit = Lin(H("#FFFFFF"), H("#BDBDBD"));
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 36,4 H 64 V 36 H 96 V 64 H 64 V 96 H 36 V 64 H 4 V 36 H 36 Z"), Fill = body, Stroke = Brushes.Black, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round });
            string arrow = dir == "Up" ? "M 50,12 L 60,26 L 40,26 Z" : dir == "Down" ? "M 50,88 L 60,74 L 40,74 Z" : dir == "Left" ? "M 12,50 L 26,40 L 26,60 Z" : "M 88,50 L 74,40 L 74,60 Z";
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(arrow), Fill = lit });
        }

        static readonly string[] Names = { "Up", "Down", "Left", "Right", "Square", "Cross", "Circle", "Triangle", "L1", "R1", "Share", "Options", "L3", "R3", "PS", "Touchpad", "L2", "R2" };

        // a button combination (bits as in Engine.BtnNames) as a row of icons joined by "+"
        public static FrameworkElement Combo(int mask, double s)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            bool first = true;
            for (int i = 0; i < Names.Length; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                if (!first) row.Children.Add(new TextBlock { Text = "+", FontSize = s * 0.7, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Th.DimC), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) });
                first = false;
                FrameworkElement g = Make(Names[i], s);
                g.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(g);
            }
            if (first) row.Children.Add(new TextBlock { Text = "not set", FontSize = s * 0.5, Foreground = new SolidColorBrush(Th.DimC), VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
    }

    // chiaki-ng settings that cut picture quality and latency (the same three keys as "chiaki tuning\apply_low_latency.ps1"):
    // smallest resolution presets and the lightest renderer. chiaki rewrites its settings on exit, so they are set just before a start.
    static class ChiakiTuning
    {
        public static void Apply()
        {
            try
            {
                if (Process.GetProcessesByName("chiaki").Length > 0) return;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Chiaki\Chiaki\settings"))
                {
                    if (k == null) return;
                    k.SetValue("resolution_local_ps4", "360p", Microsoft.Win32.RegistryValueKind.String);
                    k.SetValue("resolution_remote_ps4", "360p", Microsoft.Win32.RegistryValueKind.String);
                    k.SetValue("placebo_preset", "fast", Microsoft.Win32.RegistryValueKind.String);
                }
            }
            catch (Exception) { }
        }
    }

    // what a shortcut value means: a program path, or "keys:Ctrl+C|" (a key command); empty = nothing
    static class Sc
    {
        public static string PathOf(string q) { if (string.IsNullOrEmpty(q)) return ""; int p = q.IndexOf('|'); return p < 0 ? q : q.Substring(0, p); }
        public static bool IsKeys(string q) { return PathOf(q).StartsWith("keys:"); }
        public static string KeysOf(string q) { return PathOf(q).Substring(5); }

        public static void Run(string q)
        {
            string path = PathOf(q);
            if (path == "") return;
            if (IsKeys(q)) { Native.PressCombo(Osk.ComboVks(KeysOf(q))); return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(path);
                psi.UseShellExecute = true;
                try { string dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir; } catch (Exception) { }
                Process.Start(psi);
            }
            catch (Exception ex) { Dlg.Show(null, ex.Message, "OK", null, false); }
        }

        // the file dialog for a program or shortcut; null when cancelled
        public static string Pick()
        {
            Microsoft.Win32.OpenFileDialog d = new Microsoft.Win32.OpenFileDialog();
            d.Title = "Pick a program or shortcut";
            d.Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.url|All files|*.*";
            d.DereferenceLinks = false;   // keep a .lnk as it is so its arguments still apply
            return d.ShowDialog() == true ? d.FileName : null;
        }

        // a value is "main|label|picture": the label (own text) and the picture (own image file) are optional
        static string Part(string q, int n)
        {
            if (string.IsNullOrEmpty(q)) return "";
            string[] p = q.Split(new char[] { '|' }, 3);
            return n < p.Length ? p[n] : "";
        }
        public static string LabelOf(string q) { return Part(q, 1); }
        public static string ImageOf(string q) { return Part(q, 2); }
        public static string Compose(string main, string label, string image)
        {
            return main + "|" + (label ?? "").Replace("|", " ") + "|" + (image ?? "");
        }

        // a picture file chosen by the user; null when cancelled
        public static string PickImage()
        {
            Microsoft.Win32.OpenFileDialog d = new Microsoft.Win32.OpenFileDialog();
            d.Title = "Pick a picture for the button";
            d.Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.gif|All files|*.*";
            return d.ShowDialog() == true ? d.FileName : null;
        }

        static ImageSource LoadImage(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
                System.Windows.Media.Imaging.BitmapImage bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(file);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelHeight = 90;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception) { return null; }
        }

        // hotMask = the controller combo that runs the button directly (small icons under the content), 0 = none
        // look: the user's picture replaces the program icon; the user's text replaces the picture / name
        public static void Fill(Button b, string q, int hotMask)
        {
            if (b == null) return;
            string path = PathOf(q), label = LabelOf(q);
            ImageSource own = LoadImage(ImageOf(q));
            StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            string tip;
            if (path == "")
            {
                sp.Children.Add(new TextBlock { Text = "+", FontSize = 24, Foreground = new SolidColorBrush(Th.DimC), HorizontalAlignment = HorizontalAlignment.Center });
                tip = "Empty: set it in Scripts > Shortcuts > Controller";
            }
            else
            {
                bool keys = IsKeys(q);
                tip = keys ? "Sends " + KeysOf(q) : path;
                if (own != null)
                {
                    sp.Children.Add(new Image { Source = own, Height = label != "" ? 38 : 50, MaxWidth = 130, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, label != "" ? 2 : 0) });
                    if (label != "") sp.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
                }
                else if (label != "")
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 17, FontWeight = FontWeights.SemiBold, MaxWidth = 130, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
                else if (keys)
                    sp.Children.Add(new TextBlock { Text = KeysOf(q), FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130, HorizontalAlignment = HorizontalAlignment.Center });
                else
                {
                    ImageSource ico = Osk.IconOf(path);
                    if (ico != null) sp.Children.Add(new Image { Source = ico, Width = 30, Height = 30, Margin = new Thickness(0, 0, 0, 2) });
                    sp.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontSize = 12, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
                }
            }
            if (hotMask != 0)
            {
                FrameworkElement icons = Glyphs.Combo(hotMask, 18);
                icons.HorizontalAlignment = HorizontalAlignment.Center;
                icons.Margin = new Thickness(0, 4, 0, 0);
                sp.Children.Add(icons);
                tip += "\nController shortcut: " + Engine.ComboText(hotMask);
            }
            b.Content = sp;
            b.ToolTip = tip;
        }
    }
    // The shortcut panel: 12 big buttons per page (programs or key commands), up to 10 pages, opened with the controller combo
    // (default D-pad Down + Triangle). While it is open the controller drives it: d-pad / left stick move, Cross runs the
    // button, L1 / R1 change the page. Each button can also have its own controller combo that runs it without opening the panel.
    static class PadPanel
    {
        public const int Cols = 4, Rows = 3, PerPage = Cols * Rows, MaxPages = 10;
        public static string[] Items = new string[PerPage * MaxPages];
        public static int[] Keys = new int[PerPage * MaxPages];
        public static int Pages = 1;
        public static Action Save;
        public static volatile bool Open;
        static Window win;
        static readonly Button[] btn = new Button[PerPage];
        static readonly Button[] pBtn = new Button[PerPage];   // the same buttons on the picture in the Shortcuts tab
        static TextBlock lblPage, pLblPage;
        static int sel, page, pPage;

        public static bool IsOpen { get { return win != null && win.IsVisible; } }

        public static void Refresh()
        {
            if (page >= Pages) page = Pages - 1;
            if (pPage >= Pages) pPage = Pages - 1;
            for (int i = 0; i < PerPage; i++)
            {
                Sc.Fill(btn[i], Items[page * PerPage + i], Keys[page * PerPage + i]);
                Sc.Fill(pBtn[i], Items[pPage * PerPage + i], Keys[pPage * PerPage + i]);
            }
            if (lblPage != null) lblPage.Text = (page + 1) + " / " + Pages;
            if (pLblPage != null) pLblPage.Text = (pPage + 1) + " / " + Pages;
        }

        static Button Make(int i, Action<Button, int> click, bool rightClickToo)
        {
            Button b = new Button();
            b.Width = 150; b.Height = 82; b.Margin = new Thickness(5);
            b.Style = Osk.KeyStyle();
            b.Foreground = new SolidColorBrush(Th.TextC);
            b.Click += delegate { click(b, i); };
            if (rightClickToo) b.MouseRightButtonUp += delegate { click(b, i); };   // the picture in the Shortcuts tab: either mouse button opens the menu
            return b;
        }

        static Grid BuildGrid(Button[] store, Action<Button, int> click, bool rightClickToo)
        {
            Grid g = new Grid();
            for (int c = 0; c < Cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < Rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < PerPage; i++)
            {
                Button b = Make(i, click, rightClickToo);
                store[i] = b;
                Grid.SetColumn(b, i % Cols); Grid.SetRow(b, i / Cols);
                g.Children.Add(b);
            }
            return g;
        }

        // "<  1 / 3  >" under the buttons
        static StackPanel Pager(Action prev, Action next, out TextBlock label)
        {
            StackPanel p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            Button l = new Button { Content = "\u25C0", Width = 46, Height = 30, FontSize = 12, Style = Osk.KeyStyle(), Margin = new Thickness(0, 0, 10, 0) };
            Button r = new Button { Content = "\u25B6", Width = 46, Height = 30, FontSize = 12, Style = Osk.KeyStyle(), Margin = new Thickness(10, 0, 0, 0) };
            TextBlock t = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Th.TextC), MinWidth = 60, TextAlignment = TextAlignment.Center };
            l.Click += delegate { prev(); };
            r.Click += delegate { next(); };
            p.Children.Add(l); p.Children.Add(t); p.Children.Add(r);
            label = t;
            return p;
        }

        static void Go(ref int p, int d) { p = Math.Max(0, Math.Min(Pages - 1, p + d)); }

        // the picture for the Shortcuts tab: every button opens its menu; the extra buttons add / remove a page
        // click gets the absolute index of the button (page * 12 + position)
        public static FrameworkElement BuildPreview(Action<Button, int> click)
        {
            StackPanel all = new StackPanel();
            all.Children.Add(BuildGrid(pBtn, delegate (Button b, int i) { click(b, pPage * PerPage + i); }, true));
            all.Children.Add(Pager(delegate { Go(ref pPage, -1); Refresh(); }, delegate { Go(ref pPage, 1); Refresh(); }, out pLblPage));
            Refresh();
            return all;
        }

        public static void ShowPage(int p) { pPage = Math.Max(0, Math.Min(Pages - 1, p)); Refresh(); }
        public static int PreviewPage { get { return pPage; } }

        // deletes page p: the pages after it move up one place (never below one page)
        public static void RemovePage(int p)
        {
            if (Pages <= 1 || p < 0 || p >= Pages) return;
            for (int i = p * PerPage; i < (Pages - 1) * PerPage; i++) { Items[i] = Items[i + PerPage]; Keys[i] = Keys[i + PerPage]; }
            for (int i = (Pages - 1) * PerPage; i < Pages * PerPage; i++) { Items[i] = ""; Keys[i] = 0; }
            Pages--;
            if (pPage >= Pages) pPage = Pages - 1;
            Refresh();
        }

        public static void Toggle()
        {
            if (IsOpen) { win.Hide(); Open = false; return; }
            if (Osk.IsOpen) Osk.Toggle();   // one panel at a time
            if (win == null) Build();
            Refresh();
            win.Show();
            Open = true;
            win.UpdateLayout();
            Rect wa = SystemParameters.WorkArea;
            win.Left = wa.Left + (wa.Width - win.ActualWidth) / 2;
            win.Top = wa.Bottom - win.ActualHeight - 24;
            Highlight();
        }

        // developer check: AL-SeRry.exe --pad-shot file.png draws the panel with sample buttons into a picture
        public static void Shot(string file)
        {
            Pages = 3;
            Items[0] = "keys:Ctrl+C|"; Items[1] = "keys:Ctrl+Shift+Esc||"; Items[2] = @"C:\Windows\notepad.exe|Study|"; Items[3] = @"C:\Windows\System32\calc.exe";
            Items[4] = @"C:\Windows\System32\calc.exe|Maths|" + Path.Combine(Settings.Dir, "source", "logo.png");
            Keys[0] = (1 << 2) | (1 << 6);
            Toggle();
            FrameworkElement fe = (FrameworkElement)win.Content;
            ((Border)fe).Background = Th.WindowBrush(Th.T);   // the picture has no window behind it
            fe.UpdateLayout();
            System.Windows.Media.Imaging.RenderTargetBitmap bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(fe.ActualWidth), (int)Math.Ceiling(fe.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(fe);
            System.Windows.Media.Imaging.PngBitmapEncoder enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(file)) enc.Save(fs);
            win.Hide();
        }

        // 1 up, 2 down, 3 left, 4 right, 5 run the selected button, 7 previous page, 8 next page
        public static void Nav(int code)
        {
            if (!IsOpen) return;
            if (code == 1 && sel >= Cols) sel -= Cols;
            else if (code == 2 && sel + Cols < PerPage) sel += Cols;
            else if (code == 3 && sel % Cols > 0) sel--;
            else if (code == 4 && sel % Cols < Cols - 1) sel++;
            else if (code == 5) { Sc.Run(Items[page * PerPage + sel]); return; }
            else if (code == 7) { Go(ref page, -1); Refresh(); }
            else if (code == 8) { Go(ref page, 1); Refresh(); }
            Highlight();
        }

        static void Highlight()
        {
            for (int i = 0; i < btn.Length; i++)
            {
                if (btn[i] == null) continue;
                bool on = i == sel;
                btn[i].BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
                btn[i].BorderThickness = new Thickness(on ? 2.5 : 1);
            }
        }

        static void Build()
        {
            win = new Window();
            win.WindowStyle = WindowStyle.None;
            win.ResizeMode = ResizeMode.NoResize;
            win.ShowInTaskbar = false;
            win.ShowActivated = false;
            win.Topmost = true;
            win.SizeToContent = SizeToContent.WidthAndHeight;
            win.FontFamily = new FontFamily("Bahnschrift, Segoe UI");
            win.Background = Th.WindowBrush(Th.T);
            win.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(win).Handle;
                Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x08000000 | 0x00000080);   // NOACTIVATE | TOOLWINDOW
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);
            };
            Border frame = new Border();
            frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x34, 0xC7, 0x59));
            frame.BorderThickness = new Thickness(1.5);
            frame.Padding = new Thickness(12, 6, 12, 12);
            StackPanel all = new StackPanel();
            Grid bar = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
            bar.MouseLeftButtonDown += delegate { try { win.DragMove(); } catch (Exception) { } };
            StackPanel ttl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            ttl.Children.Add(new Image { Source = Th.AppIcon(16), Width = 16, Height = 16, Margin = new Thickness(2, 0, 8, 0) });
            ttl.Children.Add(new TextBlock { Text = "D-pad / L3 stick move \u00B7 \u2715 run \u00B7 L1 / R1 page", Foreground = new SolidColorBrush(Th.DimC), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(ttl);
            Button close = new Button { Content = "\u2715", Width = 34, Height = 26, FontSize = 12, Style = Osk.KeyStyle(), HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += delegate { win.Hide(); Open = false; };
            bar.Children.Add(close);
            all.Children.Add(bar);
            all.Children.Add(BuildGrid(btn, delegate (Button b, int i) { Sc.Run(Items[page * PerPage + i]); }, false));
            all.Children.Add(Pager(delegate { Go(ref page, -1); Refresh(); }, delegate { Go(ref page, 1); Refresh(); }, out lblPage));
            frame.Child = all;
            win.Content = frame;
        }
    }
    // Updates from GitHub Releases: the newest release of the project ("owner/name") is compared with AppInfo.Version.
    // When it is newer, the header shows the update icon; a click downloads Controller.exe from that release, the program closes,
    // the file replaces the running one and the program starts again. Without a click it stays on the current version.
    static class Updater
    {
        public const string Repo = "abo5lood10/Controller";   // "owner/name" of the GitHub project that publishes the releases
        public static volatile string LatestVersion;    // not null when a newer release exists
        static string assetUrl, shaUrl;
        public static event Action Found;

        static bool Configured { get { return !Repo.StartsWith("OWNER/"); } }

        // developer check: AL-SeRry.exe --update-demo shows the update icon as if v9.9.9 had been released (nothing can be installed)
        public static void Demo() { LatestVersion = "9.9.9"; }

        static int[] Parts(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            int dash = v.IndexOfAny(new char[] { '-', '+' });
            if (dash >= 0) v = v.Substring(0, dash);
            string[] p = v.Split('.');
            int[] r = new int[3];
            for (int i = 0; i < 3 && i < p.Length; i++) { int n; int.TryParse(p[i], out n); r[i] = n; }
            return r;
        }

        // true when version a is newer than version b ("v2.5.0" vs "2.4.0")
        public static bool IsNewer(string a, string b)
        {
            int[] x = Parts(a), y = Parts(b);
            for (int i = 0; i < 3; i++) { if (x[i] != y[i]) return x[i] > y[i]; }
            return false;
        }

        static string Get(string url)
        {
            System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;   // TLS 1.2
            System.Net.HttpWebRequest rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            rq.UserAgent = "Controller-Updater";
            rq.Timeout = 12000;
            using (System.Net.WebResponse rs = rq.GetResponse())
            using (StreamReader rd = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return rd.ReadToEnd();
        }

        // looks at the newest release in the background; says nothing when offline or when there is nothing new
        public static void CheckAsync()
        {
            if (!Configured) return;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    string json = Get("https://api.github.com/repos/" + Repo + "/releases/latest");
                    System.Text.RegularExpressions.Match tag = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    System.Text.RegularExpressions.Match exe = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/Controller\\.exe)\"");
                    System.Text.RegularExpressions.Match sha = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/Controller\\.exe\\.sha256)\"");
                    if (!tag.Success || !exe.Success) return;
                    if (!IsNewer(tag.Groups[1].Value, AppInfo.Version)) { LatestVersion = null; return; }
                    assetUrl = exe.Groups[1].Value;
                    shaUrl = sha.Success ? sha.Groups[1].Value : null;
                    LatestVersion = tag.Groups[1].Value.TrimStart('v', 'V');
                    Action f = Found; if (f != null) f();
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // downloads the new Controller.exe next to the running one and starts the swap; returns an error text, or null when the
        // program should now close (the helper waits for it, replaces the file and starts the program again)
        public static string Install()
        {
            if (LatestVersion == null || assetUrl == null) return "No update is available.";
            string dst = Assembly.GetExecutingAssembly().Location;
            string fresh = dst + ".new";
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                using (System.Net.WebClient wc = new System.Net.WebClient())
                {
                    wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "Controller-Updater";
                    wc.DownloadFile(assetUrl, fresh);
                    byte[] data = File.ReadAllBytes(fresh);
                    if (data.Length < 200000 || data[0] != (byte)'M' || data[1] != (byte)'Z') { File.Delete(fresh); return "The downloaded file is not a valid program."; }
                    if (shaUrl != null)
                    {
                        string want = System.Text.RegularExpressions.Regex.Match(wc.DownloadString(shaUrl), "[0-9a-fA-F]{64}").Value.ToLowerInvariant();
                        string got;
                        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                            got = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
                        if (want != "" && want != got) { File.Delete(fresh); return "The download did not match its checksum, so it was not installed."; }
                    }
                }
                string ps = Path.Combine(Path.GetTempPath(), "controller_update.ps1");
                File.WriteAllText(ps,
                    "param($procId, $new, $dst)\r\n" +
                    "try { Wait-Process -Id $procId -Timeout 30 -ErrorAction SilentlyContinue } catch {}\r\n" +
                    "for ($i = 0; $i -lt 40; $i++) { try { Move-Item -LiteralPath $new -Destination $dst -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 } }\r\n" +
                    "Start-Process -FilePath $dst -WorkingDirectory (Split-Path $dst)\r\n", Encoding.UTF8);
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps + "\" " + Process.GetCurrentProcess().Id + " \"" + fresh + "\" \"" + dst + "\"");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                Process.Start(psi);
                return null;
            }
            catch (Exception ex) { try { if (File.Exists(fresh)) File.Delete(fresh); } catch (Exception) { } return "The update failed: " + ex.Message; }
        }
    }

    static class AppInfo
    {
        public const string Version = "2.5.1";   // shown next to the name in the header; bump it with every release
    }

    sealed class StickView
    {
        const double S = 220, R = 100, C = 110;
        readonly System.Windows.Shapes.Ellipse minRing, maxRing, raw, outDot;

        static System.Windows.Shapes.Ellipse Ring(Canvas cv, double d, Brush stroke, Brush fill)
        {
            System.Windows.Shapes.Ellipse e = new System.Windows.Shapes.Ellipse();
            e.Width = e.Height = d; e.Stroke = stroke; e.Fill = fill; e.StrokeThickness = 1.5;
            cv.Children.Add(e);
            Canvas.SetLeft(e, C - d / 2); Canvas.SetTop(e, C - d / 2);
            return e;
        }

        static Brush B(string hex) { Brush b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        public StickView(Canvas cv)
        {
            System.Windows.Shapes.Ellipse back = Ring(cv, S - 4, B("#33FFFFFF"), B("#30000000"));
            back.StrokeThickness = 1;
            for (int i = 0; i < 2; i++)
            {
                System.Windows.Shapes.Line l = new System.Windows.Shapes.Line();
                l.Stroke = B("#22FFFFFF"); l.StrokeThickness = 1;
                if (i == 0) { l.X1 = 2; l.X2 = S - 2; l.Y1 = l.Y2 = C; } else { l.Y1 = 2; l.Y2 = S - 2; l.X1 = l.X2 = C; }
                cv.Children.Add(l);
            }
            maxRing = Ring(cv, 10, B(Th.T.Accent), Brushes.Transparent);
            minRing = Ring(cv, 10, B("#FFFF6B61"), Brushes.Transparent);
            raw = Ring(cv, 10, Brushes.Transparent, B("#99B5CCBB"));
            outDot = Ring(cv, 14, Brushes.Transparent, B(Th.T.Accent));
        }

        static void Place(System.Windows.Shapes.Ellipse e, double d)
        {
            e.Width = e.Height = d;
            Canvas.SetLeft(e, C - d / 2); Canvas.SetTop(e, C - d / 2);
        }

        public void SetRings(double min, double max) { Place(minRing, 2 * R * min + 0.01); Place(maxRing, 2 * R * max); }

        static void Dot(System.Windows.Shapes.Ellipse e, double x, double y)
        {
            double m = Math.Sqrt(x * x + y * y);
            if (m > 1) { x /= m; y /= m; }
            Canvas.SetLeft(e, C + x * R - e.Width / 2);
            Canvas.SetTop(e, C - y * R - e.Height / 2);
        }

        public void SetDots(double rx, double ry, double ox, double oy) { Dot(raw, rx, ry); Dot(outDot, ox, oy); }
    }

    sealed class MainWin
    {
        readonly Engine eng;
        readonly Settings set;
        readonly Window win;
        readonly Slider slLMin, slLMax, slRMin, slRMax;
        readonly TextBlock lblLMin, lblLMax, lblRMin, lblRMax, txtDevice, txtPadHint, txtFooter, txtSlotInfo, txtEmpty, txtActionStatus;
        readonly CheckBox chkPad, chkLink, chkDs4;
        Button btnMk;
        bool mkShown;
        readonly TextBox txtName;
        RadioButton[] slotBtns, slotBtns2;
        Button btnAddSlot, btnRemoveSlot, btnDefaultSlot, btnUpdate;
        System.Windows.Forms.MenuItem trayUpdate;
        System.Windows.Forms.NotifyIcon tray;
        readonly StickView viewL, viewR;
        readonly FrameworkElement pageHome, pageScripts;
        readonly StackPanel scriptList;
        readonly Dictionary<ScriptItem, TextBlock> statusLabels = new Dictionary<ScriptItem, TextBlock>();
        int active, rebuildVer;
        bool busy;
        DispatcherTimer saveTimer;

        T Get<T>(string name) where T : class { return (T)win.FindName(name); }

        public MainWin(Engine e)
        {
            eng = e;
            set = Settings.Load();
            Th.T = Th.ById(set.Theme);
            // ready-made scripts that are new since the version stored in settings go into the selected slot, switched OFF
            int latest = set.SeedVer;
            foreach (LibEntry le in ScriptLibrary.Items)
            {
                if (le.Blank || le.IsAim || le.Seed <= set.SeedVer) continue;
                string wanted = le.Name;   // never add a second copy of a script that is already there (added by hand)
                if (!set.Scripts[set.Active].Exists(delegate (ScriptItem x) { return x.Name == wanted; }))
                    set.Scripts[set.Active].Add(NewFromLibrary(le));
                if (le.Seed > latest) latest = le.Seed;
            }
            if (latest != set.SeedVer) { set.SeedVer = latest; set.Save(); }

            // ADS sensitivity is built into every slot now: carry the value of the old ADS script over and drop the script
            bool migrated = false;
            for (int si = 0; si < set.Scripts.Length; si++)
            {
                ScriptItem old = set.Scripts[si].Find(delegate (ScriptItem x) { return x.Name == "ADS Sensitivity (L2)"; });
                if (old == null) continue;
                float sens;
                if (old.On && old.Values.TryGetValue("Sensitivity", out sens)) set.Slots[si].Ads = Math.Max(0f, Math.Min(2f, sens));
                set.Scripts[si].Remove(old);
                Settings.DeleteCode(old);
                migrated = true;
            }
            if (migrated) set.Save();
            win = (Window)XamlReader.Parse(Th.Fill(Xaml.Main));
            slLMin = Get<Slider>("slLMin"); slLMax = Get<Slider>("slLMax"); slRMin = Get<Slider>("slRMin"); slRMax = Get<Slider>("slRMax");
            lblLMin = Get<TextBlock>("lblLMin"); lblLMax = Get<TextBlock>("lblLMax"); lblRMin = Get<TextBlock>("lblRMin"); lblRMax = Get<TextBlock>("lblRMax");
            txtDevice = Get<TextBlock>("txtDevice"); txtPadHint = Get<TextBlock>("txtPadHint"); txtFooter = Get<TextBlock>("txtFooter");
            txtSlotInfo = Get<TextBlock>("txtSlotInfo"); txtEmpty = Get<TextBlock>("txtEmpty");
            txtActionStatus = Get<TextBlock>("txtActionStatus");
            chkPad = Get<CheckBox>("chkPad"); chkLink = Get<CheckBox>("chkLink"); txtName = Get<TextBox>("txtName");
            chkDs4 = Get<CheckBox>("chkDs4");
            pageHome = Get<FrameworkElement>("pageHome"); pageScripts = Get<FrameworkElement>("pageScripts");
            scriptList = Get<StackPanel>("scriptList");
            viewL = new StickView(Get<Canvas>("cvL")); viewR = new StickView(Get<Canvas>("cvR"));
            btnAddSlot = Get<Button>("btnAddSlot");
            btnAddSlot.Click += delegate { AddSlot(); };
            btnRemoveSlot = Get<Button>("btnRemoveSlot");
            btnRemoveSlot.Click += delegate { RemoveSlot(active); };
            btnDefaultSlot = Get<Button>("btnDefaultSlot");
            btnDefaultSlot.Click += delegate { SetDefaultSlot(); };
            Get<RadioButton>("tabCtl").Checked += delegate { Get<FrameworkElement>("tabController").Visibility = Visibility.Visible; Get<FrameworkElement>("tabShortcuts").Visibility = Visibility.Collapsed; };
            Get<RadioButton>("tabShort").Checked += delegate { Get<FrameworkElement>("tabController").Visibility = Visibility.Collapsed; Get<FrameworkElement>("tabShortcuts").Visibility = Visibility.Visible; BuildShortcutKeyboard(); };
            Button subSwitch = Get<Button>("subSwitch");
            bool subKeyboard = true;   // which part of the Shortcuts tab is shown
            subSwitch.Click += delegate
            {
                subKeyboard = !subKeyboard;
                subSwitch.Content = "◀     " + (subKeyboard ? "Keyboard" : "Controller") + "     ▶";
                Get<FrameworkElement>("shortKeyboard").Visibility = subKeyboard ? Visibility.Visible : Visibility.Collapsed;
                Get<FrameworkElement>("shortController").Visibility = subKeyboard ? Visibility.Collapsed : Visibility.Visible;
                if (!subKeyboard) BuildShortcutPad();
            };
            RebuildSlotButtons();

            slLMin.ValueChanged += delegate { SliderMoved(true, true); };
            slLMax.ValueChanged += delegate { SliderMoved(true, false); };
            slRMin.ValueChanged += delegate { SliderMoved(false, true); };
            slRMax.ValueChanged += delegate { SliderMoved(false, false); };

            chkLink.IsChecked = set.Link;
            chkLink.Click += delegate { set.Link = chkLink.IsChecked == true; set.Save(); if (set.Link) { busy = true; slRMin.Value = slLMin.Value; slRMax.Value = slLMax.Value; busy = false; Push(); } };
            chkPad.Click += delegate { TogglePad(chkPad.IsChecked == true); };
            chkDs4.IsChecked = set.Ds4;
            eng.Ds4Mode = set.Ds4;
            eng.Input = set.Input;
            eng.MkCombo = set.MkCombo; eng.KbCombo = set.KbCombo; eng.PadCombo = set.PadCombo;
            PadPanel.Items = set.Pad;
            PadPanel.Keys = set.PadKeys;
            PadPanel.Pages = set.PadPages;
            PadPanel.Save = delegate { set.Save(); };
            eng.PadKeys = set.PadKeys;
            // a button's own controller combo runs it without opening the panel
            eng.PadHotkey += delegate (int idx) { win.Dispatcher.BeginInvoke(new Action(delegate { if (idx >= 0 && idx < PadPanel.Items.Length) Sc.Run(PadPanel.Items[idx]); })); };
            Osk.Quick = set.Quick;
            Osk.QuickSave = delegate { set.Save(); };
            chkDs4.Click += delegate { ChangePadType(chkDs4.IsChecked == true); };
            Get<Button>("btnScript").Click += delegate { ShowPage(true); };
            Get<Button>("btnHome").Click += delegate { ShowPage(false); };
            Get<Button>("btnAdd").Click += delegate { OpenLibrary(); };
            Get<Button>("btnApply").Click += delegate { ApplyScripts(); };
            Get<Button>("btnVerify").Click += delegate { VerifyController(); };
            Get<Button>("btnMin").Click += delegate { HideToTray(); };
            Get<Button>("btnClose").Click += delegate { win.Close(); };

            win.SourceInitialized += delegate { Glass(); };
            win.Closed += delegate { eng.Stop(); };

            // theme-specific parts: window background, title wordmark and the 16 px icon of the DsMaN identity
            win.Background = Th.WindowBrush(Th.T);
            Get<Button>("btnSettings").Click += delegate { OpenSettings(); };
            btnMk = Get<Button>("btnMk");
            btnMk.Click += delegate { eng.MkMode = !eng.MkMode; };
            Get<Button>("btnKb").Click += delegate { Osk.Toggle(); };
            eng.KeyboardRequested += delegate { win.Dispatcher.BeginInvoke(new Action(Osk.Toggle)); };
            eng.PadRequested += delegate { win.Dispatcher.BeginInvoke(new Action(PadPanel.Toggle)); };
            eng.KbKey += delegate(int code) { win.Dispatcher.BeginInvoke(new Action(delegate { if (PadPanel.Open) { if (code <= 5 || code == 7 || code == 8) PadPanel.Nav(code); } else Osk.Nav(code); })); };
            if (Th.T.Wordmark)
            {
                // the name is the cartoon wordmark next to the logo (none in the title strip)
                TextBlock title = Get<TextBlock>("txtTitle");
                StackPanel host = (StackPanel)title.Parent;
                StackPanel nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                nameRow.Children.Add(Th.WordmarkView(42));
                nameRow.Children.Add(new TextBlock { Text = "v" + AppInfo.Version, FontSize = 12, Foreground = new SolidColorBrush(Th.DimC), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 8) });
                host.Children.Insert(0, nameRow);
                title.Visibility = Visibility.Collapsed;
                Get<TextBlock>("txtStrip").Visibility = Visibility.Collapsed;
                // no title strip: minimise / close join the header buttons on the same row, nothing above the logo
                Get<RowDefinition>("rowStrip").Height = new GridLength(0);
                Get<Grid>("pageHome").Margin = new Thickness(24, 6, 24, 10);
                ((Grid)((FrameworkElement)Get<Image>("imgLogo").Parent).Parent).Margin = new Thickness(0, 0, 0, 6);   // header row: tight to the first card
                StackPanel bar = (StackPanel)btnMk.Parent;
                StackPanel wb = (StackPanel)Get<Button>("btnMin").Parent;
                ((Panel)wb.Parent).Children.Remove(wb);
                wb.Margin = new Thickness(6, 0, 0, 0);
                bar.Children.Add(wb);

                // update icon (green like the A button of the Xbox 360 pad): only visible when a newer release exists
                btnUpdate = new Button { Style = (Style)win.FindResource("Glass"), Padding = new Thickness(0), Width = 42, Height = 32, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
                Canvas ic = new Canvas { Width = 24, Height = 24 };
                System.Windows.Shapes.Path arrow = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 12,4 L 12,15 M 7,10.5 L 12,15.5 L 17,10.5 M 5,20 L 19,20"),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x5D, 0xC2, 0x1E)), StrokeThickness = 2.6,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
                };
                arrow.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x5D, 0xC2, 0x1E), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7 };
                ic.Children.Add(arrow);
                btnUpdate.Content = new Viewbox { Width = 20, Height = 20, Child = ic };
                btnUpdate.Click += delegate { StartUpdate(); };
                bar.Children.Insert(0, btnUpdate);
                // the logo: big and sharp, before the name
                ImageSource logo = Th.LogoImage();
                Image big = Get<Image>("imgLogo");
                if (logo != null)
                {
                    big.Source = logo;
                    big.Width = big.Height = 72;
                    RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.HighQuality);
                    big.Visibility = Visibility.Visible;
                }
            }

            active = set.Active;
            LoadSlot(active);

            // updates: one check a few seconds after the start, then every six hours
            Updater.Found += delegate { win.Dispatcher.BeginInvoke(new Action(ShowUpdate)); };
            DispatcherTimer upT = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            upT.Tick += delegate { upT.Interval = TimeSpan.FromHours(6); Updater.CheckAsync(); };
            upT.Start();

            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(16);
            t.Tick += delegate { Tick(); };
            t.Start();
        }

        public Window Window { get { return win; } }

        RadioButton[] MakeSlotButtons(string gridName, string group)
        {
            UniformGrid grid = Get<UniformGrid>(gridName);
            grid.Children.Clear();
            grid.Columns = Math.Max(6, set.SlotCount);   // a slot keeps its width however few there are
            RadioButton[] arr = new RadioButton[set.SlotCount];
            for (int i = 0; i < arr.Length; i++)
            {
                int idx = i;
                RadioButton rb = new RadioButton();
                rb.GroupName = group;
                rb.Style = (Style)win.FindResource("Seg");
                rb.Checked += delegate { if (!busy) LoadSlot(idx); };
                ContextMenu cm = new ContextMenu();
                MenuItem rm = new MenuItem { Header = "Remove this slot" };
                rm.Click += delegate { RemoveSlot(idx); };
                cm.Items.Add(rm);
                cm.Opened += delegate { rm.IsEnabled = set.SlotCount > 1; };
                rb.ContextMenu = cm;
                arr[i] = rb;
                grid.Children.Add(rb);
            }
            return arr;
        }

        void RebuildSlotButtons()
        {
            slotBtns = MakeSlotButtons("slots", "slotA");
            slotBtns2 = MakeSlotButtons("slots2", "slotB");
            btnAddSlot.IsEnabled = set.SlotCount < Settings.MaxSlots;
            btnAddSlot.Opacity = btnAddSlot.IsEnabled ? 1.0 : 0.45;
            UpdateSlotButtons();
        }

        // ---- Shortcuts tab: a picture of the keyboard; its side buttons choose what they do (program or key command)
        bool kbViewBuilt;

        void ShortcutChanged()
        {
            set.Save();
            Osk.RefreshQuick();
        }

        bool padViewBuilt;

        void BuildShortcutKeyboard()
        {
            if (kbViewBuilt) return;
            kbViewBuilt = true;
            Get<Viewbox>("kbView").Child = Osk.BuildPreview(delegate (Button b, int i)
            {
                ShowShortcutMenu(b, delegate { return Osk.Quick[i]; }, delegate (string v) { Osk.Quick[i] = v; ShortcutChanged(); });
            });
        }

        void BuildShortcutPad()
        {
            if (padViewBuilt) return;
            padViewBuilt = true;
            Get<Viewbox>("padView").Child = PadPanel.BuildPreview(
                delegate (Button b, int i)
                {
                    ShowShortcutMenu(b, delegate { return PadPanel.Items[i]; },
                        delegate (string v) { PadPanel.Items[i] = v; if (v == "") PadPanel.Keys[i] = 0; set.Save(); PadPanel.Refresh(); },
                        delegate { return PadPanel.Keys[i]; },
                        delegate (int m) { PadPanel.Keys[i] = m; set.Save(); PadPanel.Refresh(); });
                });
            Button add = Get<Button>("btnPadAdd"), del = Get<Button>("btnPadDel");
            Action sync = delegate
            {
                del.Visibility = PadPanel.Pages > 1 ? Visibility.Visible : Visibility.Collapsed;   // never fewer than one page
                add.IsEnabled = PadPanel.Pages < PadPanel.MaxPages;
                add.Opacity = add.IsEnabled ? 1.0 : 0.45;
            };
            add.Click += delegate
            {
                if (PadPanel.Pages >= PadPanel.MaxPages) return;
                PadPanel.Pages++; set.PadPages = PadPanel.Pages; set.Save(); PadPanel.ShowPage(PadPanel.Pages - 1); sync();
            };
            del.Click += delegate
            {
                if (PadPanel.Pages <= 1) return;
                if (!Dlg.Confirm(win, "Delete page " + (PadPanel.PreviewPage + 1) + " with its buttons?", "Delete", true)) return;
                PadPanel.RemovePage(PadPanel.PreviewPage); set.PadPages = PadPanel.Pages; set.Save(); sync();
            };
            sync();
        }

        // the small menu of a shortcut button: Program / Keys / Clear (Clear only when something is set)
        void ShowShortcutMenu(Button target, Func<string> get, Action<string> put)
        {
            ShowShortcutMenu(target, get, put, null, null);
        }

        // getKey / putKey (optional): the controller combo that runs the button directly, 0 = none
        void ShowShortcutMenu(Button target, Func<string> get, Action<string> put, Func<int> getKey, Action<int> putKey)
        {
            System.Windows.Controls.Primitives.Popup pop = new System.Windows.Controls.Primitives.Popup();
            pop.PlacementTarget = target;
            pop.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            pop.StaysOpen = false;
            pop.AllowsTransparency = true;
            Border box = new Border();
            box.Background = Th.WindowBrush(Th.T);
            box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x34, 0xC7, 0x59));
            box.BorderThickness = new Thickness(1.5);
            box.CornerRadius = new CornerRadius(12);
            box.Padding = new Thickness(8);
            box.Margin = new Thickness(0, 4, 0, 10);
            StackPanel sp = new StackPanel();
            Func<string, Button> item = delegate (string t)
            {
                return new Button { Content = t, Style = (Style)win.FindResource("Glass"), Padding = new Thickness(18, 7, 18, 7), FontSize = 13, MinWidth = 130, Margin = new Thickness(0, 2, 0, 2) };
            };
            Button bp = item("Program"), bk = item("Keys"), bc = item("Clear");
            bool has = !string.IsNullOrEmpty(get());
            bc.IsEnabled = has;
            bc.Opacity = has ? 1.0 : 0.45;
            bp.Click += delegate { pop.IsOpen = false; string p = Sc.Pick(); if (p != null) put(p); };
            bk.Click += delegate
            {
                pop.IsOpen = false;
                string combo = Dlg.Keys(win);
                if (combo != null) put("keys:" + combo + "|");
            };
            bc.Click += delegate { pop.IsOpen = false; put(""); };
            sp.Children.Add(bp); sp.Children.Add(bk);
            if (getKey != null)
            {
                int cur = getKey();
                Button bh = item("Hotkey");
                if (cur != 0)
                {
                    StackPanel hc = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                    hc.Children.Add(new TextBlock { Text = "Hotkey  ", VerticalAlignment = VerticalAlignment.Center });
                    hc.Children.Add(Glyphs.Combo(cur, 20));
                    bh.Content = hc;
                }
                bh.IsEnabled = has;   // a hotkey runs the button, so it needs something to run
                bh.Opacity = has ? 1.0 : 0.45;
                bh.Click += delegate
                {
                    pop.IsOpen = false;
                    int m = Dlg.Combo(win, eng);
                    if (m > 0) putKey(m);
                };
                sp.Children.Add(bh);
                if (cur != 0)
                {
                    Button br = item("Remove hotkey");
                    br.Click += delegate { pop.IsOpen = false; putKey(0); };
                    sp.Children.Add(br);
                }
                // own look: a text (replaces the icon) and / or a picture of the user's choice
                Button bl = item("Text"), bi = item("Picture");
                bl.IsEnabled = bi.IsEnabled = has;
                bl.Opacity = bi.Opacity = has ? 1.0 : 0.45;
                bl.Click += delegate
                {
                    pop.IsOpen = false;
                    string q0 = get();
                    string t = Dlg.Ask(win, "Write the text of this button (it replaces the icon):", Sc.LabelOf(q0));
                    if (t != null) put(Sc.Compose(Sc.PathOf(q0), t, Sc.ImageOf(q0)));
                };
                bi.Click += delegate
                {
                    pop.IsOpen = false;
                    string q0 = get();
                    string p = Sc.PickImage();
                    if (p != null) put(Sc.Compose(Sc.PathOf(q0), Sc.LabelOf(q0), p));
                };
                sp.Children.Add(bl); sp.Children.Add(bi);
                if (has && (Sc.LabelOf(get()) != "" || Sc.ImageOf(get()) != ""))
                {
                    Button bz = item("Reset look");
                    bz.Click += delegate { pop.IsOpen = false; string q0 = get(); put(Sc.Compose(Sc.PathOf(q0), "", "")); };
                    sp.Children.Add(bz);
                }
            }
            sp.Children.Add(bc);
            box.Child = sp;
            pop.Child = box;
            pop.IsOpen = true;
        }
        // Remove is off with a single slot; Default shows green while the selected slot is the one applied at start
        void UpdateSlotButtons()
        {
            btnRemoveSlot.IsEnabled = set.SlotCount > 1;
            btnRemoveSlot.Opacity = btnRemoveSlot.IsEnabled ? 1.0 : 0.45;
            bool isDef = active == set.DefaultSlot;
            btnDefaultSlot.Content = isDef ? "★ Default" : "Set default";
            btnDefaultSlot.Style = (Style)win.FindResource(isDef ? "GreenBtn" : "Glass");
        }

        void SetDefaultSlot()
        {
            set.DefaultSlot = active;
            set.Save();
            RefreshSlotNames();
            UpdateSlotButtons();
        }

        // ---- updates: the icon (and a tray item) appear only when a newer release exists
        void ShowUpdate()
        {
            string tip = "Update available: v" + Updater.LatestVersion + " (click to install)";
            if (btnUpdate != null) { btnUpdate.ToolTip = tip; btnUpdate.Visibility = Visibility.Visible; }
            if (trayUpdate != null) { trayUpdate.Text = "Update to v" + Updater.LatestVersion; trayUpdate.Visible = true; }
        }

        void StartUpdate()
        {
            if (Updater.LatestVersion == null) return;
            ShowMain();
            if (!Dlg.Confirm(win, "Update to v" + Updater.LatestVersion + " now?\nThe program closes and starts again by itself.", "Update", false)) return;
            if (btnUpdate != null) { btnUpdate.IsEnabled = false; btnUpdate.ToolTip = "Downloading..."; }
            Thread t = new Thread(delegate ()
            {
                string err = Updater.Install();
                win.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (err == null) { win.Close(); return; }
                    if (btnUpdate != null) btnUpdate.IsEnabled = true;
                    Dlg.Show(win, err, "OK", null, false);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---- tray icon: the program lives in the background; the icon opens the window
        public void SetupTray()
        {
            tray = new System.Windows.Forms.NotifyIcon();
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch (Exception) { }
            tray.Text = "Controller";
            System.Windows.Forms.ContextMenu cm = new System.Windows.Forms.ContextMenu();
            cm.MenuItems.Add("Open", delegate { ShowMain(); });
            trayUpdate = new System.Windows.Forms.MenuItem("Update", delegate { StartUpdate(); });
            trayUpdate.Visible = false;   // shown only when an update exists
            cm.MenuItems.Add(trayUpdate);
            if (Updater.LatestVersion != null) ShowUpdate();
            cm.MenuItems.Add("Exit", delegate { win.Close(); });
            tray.ContextMenu = cm;
            tray.MouseClick += delegate(object s, System.Windows.Forms.MouseEventArgs ev) { if (ev.Button == System.Windows.Forms.MouseButtons.Left) ShowMain(); };
            tray.Visible = true;
            win.Closed += delegate { tray.Visible = false; tray.Dispose(); };
            // a second start of the program (desktop shortcut) brings the window of the running one up instead
            EventWaitHandle ev2 = new EventWaitHandle(false, EventResetMode.AutoReset, "ControllerUnitOf5belan.show");
            Thread th = new Thread(delegate () { while (true) { ev2.WaitOne(); win.Dispatcher.BeginInvoke(new Action(ShowMain)); } });
            th.IsBackground = true;
            th.Start();
            // the desktop shortcut "chiaki-ng" (AL-SeRry.exe --chiaki) asks the running copy to start chiaki-ng with the tuning
            EventWaitHandle ev3 = new EventWaitHandle(false, EventResetMode.AutoReset, "ControllerUnitOf5belan.chiaki");
            Thread th3 = new Thread(delegate () { while (true) { ev3.WaitOne(); win.Dispatcher.BeginInvoke(new Action(LaunchChiaki)); } });
            th3.IsBackground = true;
            th3.Start();
        }

        public void ShowMain()
        {
            win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
        }

        void HideToTray() { win.Hide(); }

        void AddSlot()
        {
            if (!set.AddSlot()) return;
            set.Active = set.SlotCount - 1;
            set.Save();
            RebuildSlotButtons();
            LoadSlot(set.Active);
        }

        void RemoveSlot(int i)
        {
            if (set.SlotCount <= 1) return;
            if (!Dlg.Confirm(win, "Remove slot " + (i + 1) + "?\nIts scripts are removed with it.", "Remove", true)) return;
            set.RemoveSlot(i);
            set.Save();
            RebuildSlotButtons();
            LoadSlot(set.Active);
        }

        void ShowPage(bool scripts)
        {
            pageHome.Visibility = scripts ? Visibility.Collapsed : Visibility.Visible;   // collapsed, so the window fits the page shown
            pageScripts.Visibility = scripts ? Visibility.Visible : Visibility.Collapsed;
        }

        // Windows 11 acrylic backdrop + dark title bar + rounded corners; falls back to a solid green tint.
        void Glass()
        {
            IntPtr h = new WindowInteropHelper(win).Handle;
            HwndSource src = HwndSource.FromHwnd(h);
            int on = 1, round = 2, acrylic = 3;
            Native.DwmSetWindowAttribute(h, 20, ref on, 4);
            Native.DwmSetWindowAttribute(h, 33, ref round, 4);
            if (!Th.T.Acrylic) return;   // DsMaN identity: opaque gradient window, no acrylic backdrop
            int r = Native.DwmSetWindowAttribute(h, 38, ref acrylic, 4);
            if (r == 0 && src != null)
            {
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.MARGINS m = new Native.MARGINS(); m.L = m.R = m.T = m.B = -1;
                Native.DwmExtendFrameIntoClientArea(h, ref m);
            }
            else win.Background = new SolidColorBrush(Th.BackC);
        }

        // ---- deadzone sliders and slots
        void SliderMoved(bool left, bool isMin)
        {
            if (busy) return;
            busy = true;
            Slider lo = left ? slLMin : slRMin, hi = left ? slLMax : slRMax;
            if (hi.Value < lo.Value + 0.05)
            {
                if (isMin) lo.Value = Math.Round(hi.Value - 0.05, 2); else hi.Value = Math.Round(lo.Value + 0.05, 2);
            }
            if (set.Link)
            {
                Slider olo = left ? slRMin : slLMin, ohi = left ? slRMax : slLMax;
                olo.Value = lo.Value; ohi.Value = hi.Value;
            }
            busy = false;
            Push();
        }

        void Push()
        {
            Slot aim = set.Slots[active];
            // the sliders are the slot: every move is kept (saved a moment later), there is no Save button
            aim.LMin = (float)slLMin.Value; aim.LMax = (float)slLMax.Value; aim.RMin = (float)slRMin.Value; aim.RMax = (float)slRMax.Value;
            QueueSave();
            eng.Cfg = new Cfg((float)slLMin.Value, (float)slLMax.Value, (float)slRMin.Value, (float)slRMax.Value, aim.AimOn ? aim.Curve : 1, aim.AimOn ? aim.Dyn : 0.5f, aim.AimOn ? aim.Ads : 1f, aim.AimOn && aim.AdsL1);
            lblLMin.Text = slLMin.Value.ToString("0.00"); lblLMax.Text = slLMax.Value.ToString("0.00");
            lblRMin.Text = slRMin.Value.ToString("0.00"); lblRMax.Text = slRMax.Value.ToString("0.00");
            viewL.SetRings(slLMin.Value, slLMax.Value);
            viewR.SetRings(slRMin.Value, slRMax.Value);
        }

        void RefreshSlotNames()
        {
            busy = true;
            for (int i = 0; i < slotBtns.Length; i++)
            {
                foreach (RadioButton rb in new RadioButton[] { slotBtns[i], slotBtns2[i] })
                {
                    rb.Content = new TextBlock { Text = (i + 1) + (i == set.DefaultSlot ? " ★" : ""), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Th.TextC) };
                    rb.IsChecked = i == active;
                }
            }
            busy = false;
        }

        void LoadSlot(int i)
        {
            active = i;
            Slot s = set.Slots[i];
            busy = true;
            slLMax.Value = s.LMax; slLMin.Value = s.LMin; slRMax.Value = s.RMax; slRMin.Value = s.RMin;
            txtName.Text = s.Name;
            busy = false;
            Push();
            RefreshSlotNames();
            UpdateSlotButtons();
            txtSlotInfo.Text = "Scripts for slot " + (i + 1);
            set.Active = i; set.Save();
            BuildRows();
            Rebuild();
        }

        void TogglePad(bool on)
        {
            if (!eng.SetPad(on)) chkPad.IsChecked = false;
        }

        // ---- Settings window: theme choice (both themes are always listed, so you can go back)
        void RestartApp()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--wait");
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception) { return; }
            win.Close();   // the new copy waits for this one to let go of the single-instance lock
        }

        void OpenSettings()
        {
            Window w = (Window)XamlReader.Parse(Th.Fill(Xaml.SettingsWin));
            w.Owner = win;
            ((Button)w.FindName("btnWinClose")).Click += delegate { w.Close(); };
            CheckBox autoStart = (CheckBox)w.FindName("chkAutoStart");
            autoStart.IsChecked = AutoStart.IsOn();
            autoStart.Click += delegate { AutoStart.Set(autoStart.IsChecked == true); autoStart.IsChecked = AutoStart.IsOn(); };
            StackPanel list = (StackPanel)w.FindName("themeList");
            TextBlock note = (TextBlock)w.FindName("txtThemeNote");
            Button apply = (Button)w.FindName("btnThemeApply");
            string inUse = Th.T.Id;
            string pending = inUse;
            Action refresh = delegate
            {
                apply.IsEnabled = pending != inUse;
                apply.Opacity = apply.IsEnabled ? 1.0 : 0.45;
                note.Text = pending == inUse ? "This theme is in use now."
                    : "The program restarts to apply it. The virtual controller (and the input of a running stream) stops for a few seconds.";
            };
            StackPanel inputs = (StackPanel)w.FindName("inputList");
            string[][] kinds = new string[][] {
                new string[] { "auto", "Automatic", "DualSense / Edge first, then DualShock 4, then Xbox / GameSir" },
                new string[] { "ds5", "DualSense / DualSense Edge", "PS5 controller (USB or Bluetooth)" },
                new string[] { "ds4", "DualShock 4", "PS4 controller (USB or Bluetooth)" },
                new string[] { "xinput", "Xbox One / Series X|S / GameSir", "View button = touchpad click, Menu = Options, Xbox button = PS" } };
            foreach (string[] kd in kinds)
            {
                string id = kd[0];
                StackPanel c = new StackPanel();
                c.Width = 520;
                c.Children.Add(new TextBlock { Text = kd[1], FontWeight = FontWeights.Bold, FontSize = 14 });
                TextBlock sub = new TextBlock { Text = kd[2], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Opacity = 0.8 };
                sub.Foreground = new SolidColorBrush(Th.TextC);
                c.Children.Add(sub);
                RadioButton r = new RadioButton();
                r.GroupName = "input";
                r.Style = (Style)win.FindResource("Seg");
                r.Margin = new Thickness(0, 0, 0, 8);
                r.Content = c;
                r.IsChecked = set.Input == id;
                r.Checked += delegate
                {
                    if (set.Input == id) return;
                    set.Input = id; set.Save();
                    eng.SetInput(id);
                    txtFooter.Text = "Input controller: " + kd[1] + ".";
                };
                inputs.Children.Add(r);
            }
            StackPanel mkp = (StackPanel)w.FindName("mkPanel");
            string[] what = { "Switch M&K mode", "Show the keyboard", "Show the shortcut panel" };
            for (int ci = 0; ci < 3; ci++)
            {
                int cidx = ci;
                Func<int> getCombo = delegate { return cidx == 0 ? set.MkCombo : cidx == 1 ? set.KbCombo : set.PadCombo; };
                Action<int> setCombo = delegate (int m)
                {
                    if (cidx == 0) { set.MkCombo = m; eng.MkCombo = m; }
                    else if (cidx == 1) { set.KbCombo = m; eng.KbCombo = m; }
                    else { set.PadCombo = m; eng.PadCombo = m; }
                };
                Grid g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                tx.Children.Add(new TextBlock { Text = what[ci], FontWeight = FontWeights.SemiBold });
                ContentControl cur = new ContentControl { Margin = new Thickness(0, 4, 0, 0) };   // the buttons as icons, or a short message
                Action<string> say = delegate (string m) { cur.Content = new TextBlock { Text = m, FontSize = 12, Opacity = 0.85 }; };
                Action show = delegate { cur.Content = Glyphs.Combo(getCombo(), 15.2); };   // was 30, then 12 (-60%), then +15%, now +10%
                show();
                tx.Children.Add(cur);
                g.Children.Add(tx);
                Button rec = new Button { Content = "Record", Padding = new Thickness(16, 7, 16, 7), Style = (Style)win.FindResource("Glass") };
                Grid.SetColumn(rec, 1);
                g.Children.Add(rec);
                rec.Click += delegate
                {
                    if (!eng.Connected) { say("Connect a controller first."); return; }
                    eng.BeginCapture();
                    rec.IsEnabled = false;
                    say("Hold the buttons together, then let go...");
                    DispatcherTimer ct = new DispatcherTimer();
                    ct.Interval = TimeSpan.FromMilliseconds(100);
                    int ticks = 0;
                    ct.Tick += delegate
                    {
                        ticks++;
                        if (eng.CaptureState == 2)
                        {
                            ct.Stop();
                            int m = eng.CaptureMask;
                            eng.CaptureState = 0;
                            rec.IsEnabled = true;
                            setCombo(m);
                            set.Save();
                            show();
                        }
                        else if (ticks > 150)
                        {
                            ct.Stop();
                            eng.CaptureState = 0;
                            rec.IsEnabled = true;
                            show();   // nothing recorded: the old buttons stay
                        }
                    };
                    ct.Start();
                };
                mkp.Children.Add(g);
            }
            foreach (ThemeDef td in new ThemeDef[] { Th.DsMaN })
            {
                ThemeDef def = td;
                StackPanel content = new StackPanel();
                content.Width = 520;
                StackPanel head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = def.Name, FontWeight = FontWeights.Bold, FontSize = 15 });
                if (def.Id == inUse) head.Children.Add(new TextBlock { Text = "   (in use)", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(head);
                TextBlock blurb = new TextBlock { Text = def.Blurb, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
                blurb.Foreground = new SolidColorBrush(Th.TextC);   // readable on the green of the selected card
                blurb.Opacity = 0.8;
                content.Children.Add(blurb);
                StackPanel sw = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (string hex in new string[] { def.Text, def.Dim, def.Accent, def.Back })
                {
                    Border dot = new Border();
                    dot.Width = 30; dot.Height = 14; dot.CornerRadius = new CornerRadius(5); dot.Margin = new Thickness(0, 0, 6, 0);
                    dot.Background = new SolidColorBrush(Th.C(hex));
                    dot.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)); dot.BorderThickness = new Thickness(1);
                    sw.Children.Add(dot);
                }
                content.Children.Add(sw);
                RadioButton rb = new RadioButton();
                rb.GroupName = "theme";
                rb.Style = (Style)win.FindResource("Seg");
                rb.Margin = new Thickness(0, 0, 0, 8);
                rb.Content = content;
                rb.IsChecked = def.Id == inUse;
                rb.Checked += delegate { pending = def.Id; refresh(); };
                list.Children.Add(rb);
            }
            apply.Click += delegate
            {
                if (pending == inUse) return;
                set.Theme = pending;
                set.Save();
                RestartApp();
            };
            refresh();
            w.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);   // rounded corners
                w.Background = new SolidColorBrush(Th.BackC);
            };
            w.Show();
        }

        // Sony mode (DualShock 4) <-> Xbox 360. A running chiaki-ng picks its controllers at start, so it must be relaunched.
        void ChangePadType(bool ds4)
        {
            set.Ds4 = ds4;
            set.Save();
            eng.Ds4Mode = ds4;
            if (eng.PadOn)
            {
                eng.SetPad(false);
                if (!eng.SetPad(true)) chkPad.IsChecked = false;
            }
            txtFooter.Text = "Controller type changed to " + (ds4 ? "Sony (DualShock 4)" : "Xbox 360") +
                ". If chiaki-ng is running, use Scripts > Verify Controller (it offers a restart).";
        }

        public void StartChiaki() { LaunchChiaki(); }

        void LaunchChiaki()
        {
            if (Process.GetProcessesByName("chiaki").Length > 0)
            {
                txtFooter.Text = "chiaki-ng is already running. Close it, then launch it from here so it ignores the physical controller.";
                return;
            }
            string path = set.Chiaki;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // chiaki-ng lives inside the program folder; the old place (next to it on the desktop) is only a fallback
                string inside = Path.Combine(Settings.Dir, "chiaki-ng", "app", "portable", "chiaki-ng-Win", "chiaki.exe");
                string beside = Path.Combine(Directory.GetParent(Settings.Dir.TrimEnd('\\')).FullName, "chiaki-ng", "app", "portable", "chiaki-ng-Win", "chiaki.exe");
                string guess = File.Exists(inside) ? inside : beside;
                if (File.Exists(guess)) path = guess;
                else
                {
                    Microsoft.Win32.OpenFileDialog d = new Microsoft.Win32.OpenFileDialog();
                    d.Title = "Select chiaki.exe"; d.Filter = "chiaki.exe|chiaki.exe|Programs|*.exe";
                    if (d.ShowDialog() != true) return;
                    path = d.FileName;
                }
                set.Chiaki = path; set.Save();
            }
            if (!eng.PadOn) { chkPad.IsChecked = true; TogglePad(true); Thread.Sleep(500); }
            if (!eng.PadOn) return;
            ChiakiTuning.Apply();   // low resolution + light renderer, every start
            ProcessStartInfo psi = new ProcessStartInfo(path);
            psi.UseShellExecute = false;
            if (set.Ps4Host.Length > 0 && set.Ps4Nick.Length > 0)
                psi.Arguments = "--exit-app-on-stream-exit stream \"" + set.Ps4Nick + "\" " + set.Ps4Host;
            psi.WorkingDirectory = Path.GetDirectoryName(path);
            psi.EnvironmentVariables["SDL_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT"] = eng.VisibleToChiaki;
            Process.Start(psi);
        }

        // ---- scripts (per slot, several per slot, each with its own ON/OFF)
        // Compiles whatever is ON for the active slot (cached) and hands the live list to the engine.
        void Rebuild()
        {
            int ver = ++rebuildVer;
            List<ScriptItem> items = new List<ScriptItem>(set.Scripts[active]);
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ScriptItem> live = new List<ScriptItem>();
                foreach (ScriptItem it in items)
                {
                    if (!it.On) continue;
                    if (it.Instance == null || it.CompiledCode != it.Code)
                    {
                        string err;
                        ScriptBase sb = ScriptHost.Compile(it.Code, out err);
                        it.CompiledCode = it.Code;
                        it.Error = err;
                        it.Failed = sb == null;
                        it.Instance = sb;
                    }
                    if (it.Instance != null) live.Add(it);
                }
                if (ver == rebuildVer) eng.Scripts = live.ToArray();
            });
        }

        string StatusOf(ScriptItem it, out bool err)
        {
            err = false;
            if (!it.On) return "Off";
            if (it.Error != null) { err = true; return "Error - " + it.Error; }
            if (it.Instance == null || it.CompiledCode != it.Code) return "Compiling...";
            return "Running - " + it.Ms.ToString("0.000") + " ms" + (eng.PadOn ? "" : "   (waiting for the virtual pad)");
        }

        // ---- built-in Aim card (first thing in the script list): response curve + ADS sensitivity, one set per slot
        void DrawCurve(Canvas cv, int type, float dyn)
        {
            cv.Children.Clear();
            double w = cv.Width, h = cv.Height;
            System.Windows.Shapes.Rectangle frame = new System.Windows.Shapes.Rectangle();
            frame.Width = w; frame.Height = h; frame.RadiusX = 8; frame.RadiusY = 8; frame.StrokeThickness = 1;
            frame.Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            cv.Children.Add(frame);
            System.Windows.Shapes.Line diag = new System.Windows.Shapes.Line();
            diag.X1 = 0; diag.Y1 = h; diag.X2 = w; diag.Y2 = 0; diag.StrokeThickness = 1;
            diag.Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            diag.StrokeDashArray = new DoubleCollection(new double[] { 3, 3 });
            cv.Children.Add(diag);
            PointCollection pts = new PointCollection();
            for (int i = 0; i <= 40; i++)
            {
                float m = i / 40f;
                pts.Add(new Point(m * w, h - AimCurve.Shape(type, dyn, m) * h));
            }
            System.Windows.Shapes.Polyline pl = new System.Windows.Shapes.Polyline();
            pl.Points = pts; pl.StrokeThickness = 2.5;
            pl.Stroke = new SolidColorBrush(Th.AccentC);
            cv.Children.Add(pl);
        }

        // label + slider + number box; onChange gets the value from either of them
        Grid NumRow(string label, double min, double max, double value, Action<double> onChange)
        {
            Grid pg = new Grid();
            pg.Margin = new Thickness(0, 10, 0, 0);
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label };
            lbl.Style = (Style)win.FindResource("Muted");
            Slider sl = new Slider();
            sl.Minimum = min; sl.Maximum = max;
            sl.SmallChange = (max - min) / 100.0; sl.LargeChange = (max - min) / 10.0;
            sl.Value = value;
            sl.Margin = new Thickness(0, 0, 14, 0);
            TextBox tb = new TextBox();
            tb.Style = (Style)win.FindResource("Field");
            tb.Width = 80; tb.Padding = new Thickness(6, 4, 6, 4);
            tb.Text = FmtNum((float)value);
            bool sync = false;
            sl.ValueChanged += delegate
            {
                if (sync) return;
                onChange(sl.Value);
                sync = true; tb.Text = FmtNum((float)sl.Value); sync = false;
            };
            Action commit = delegate
            {
                float v;
                if (float.TryParse(tb.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    v = (float)Math.Max(min, Math.Min(max, v));
                    sync = true; sl.Value = v; sync = false;
                    onChange(v);
                    tb.Text = FmtNum(v);
                }
                else tb.Text = FmtNum((float)sl.Value);
            };
            tb.LostFocus += delegate { commit(); };
            tb.KeyDown += delegate (object s, KeyEventArgs k) { if (k.Key == Key.Enter) { commit(); Keyboard.ClearFocus(); } };
            Grid.SetColumn(sl, 1);
            Grid.SetColumn(tb, 2);
            pg.Children.Add(lbl);
            pg.Children.Add(sl);
            pg.Children.Add(tb);
            return pg;
        }

        void BuildAimCard()
        {
            Slot sl = set.Slots[active];
            Border b = new Border();
            b.Style = (Style)win.FindResource("Card");
            b.CornerRadius = new CornerRadius(16);
            b.Padding = new Thickness(14, 12, 14, 12);
            b.Margin = new Thickness(0, 0, 0, 10);
            StackPanel col = new StackPanel();

            Grid head = new Grid();
            head.Children.Add(new TextBlock { Text = "Aim - slot " + (active + 1), FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            Button rm = new Button { Content = "Delete", Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Right };
            rm.Style = (Style)win.FindResource("Glass");
            rm.Click += delegate
            {
                if (!Dlg.Confirm(win, "Remove the Aim from slot " + (active + 1) + "?\nThe stick goes back to a plain 1:1 response.", "Delete", true)) return;
                sl.AimOn = false;
                set.Save();
                Push();
                BuildRows();
            };
            head.Children.Add(rm);
            col.Children.Add(head);
            TextBlock sub = new TextBlock { Text = "Right stick: how stick movement becomes aim speed (like Aim Response Curve in the newer Call of Duty games). Each slot keeps its own.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
            sub.Style = (Style)win.FindResource("Muted");
            col.Children.Add(sub);

            Canvas preview = new Canvas();
            preview.Width = 150; preview.Height = 84;
            preview.VerticalAlignment = VerticalAlignment.Top;
            TextBlock desc = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 14, 0) };
            desc.Style = (Style)win.FindResource("Muted");
            Grid dynRow = null;
            string[] names = { "Standard", "Linear", "Dynamic" };
            string[] texts =
            {
                "Standard - a simple power curve: slow at first, then it speeds up as you push further.",
                "Linear - the stick maps straight to aim speed (1:1). No extra shaping from the program.",
                "Dynamic - a reverse S-curve: quick response to small movements, a flatter middle for fine aim, fast again near full tilt. Level 0 = Standard."
            };
            Action refresh = delegate
            {
                desc.Text = texts[sl.Curve];
                if (dynRow != null) { dynRow.IsEnabled = sl.Curve == 2; dynRow.Opacity = sl.Curve == 2 ? 1.0 : 0.45; }
                DrawCurve(preview, sl.Curve, sl.Dyn);
            };

            UniformGrid seg = new UniformGrid();
            seg.Rows = 1;
            seg.Margin = new Thickness(-3, 0, -3, 0);
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                RadioButton rb = new RadioButton();
                rb.GroupName = "curve";
                rb.Style = (Style)win.FindResource("Seg");
                rb.Content = new TextBlock { Text = names[i], FontSize = 13, FontWeight = FontWeights.SemiBold };
                rb.IsChecked = sl.Curve == i;
                rb.Checked += delegate { sl.Curve = idx; Push(); QueueSave(); refresh(); };
                seg.Children.Add(rb);
            }

            StackPanel left = new StackPanel();
            left.Children.Add(seg);
            left.Children.Add(desc);
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(left);
            Grid.SetColumn(preview, 1);
            top.Children.Add(preview);
            col.Children.Add(top);

            dynRow = NumRow("Dynamic level, % (0 = Standard)", 0, 100, Math.Round(sl.Dyn * 100.0), delegate (double v) { sl.Dyn = (float)(v / 100.0); Push(); QueueSave(); DrawCurve(preview, sl.Curve, sl.Dyn); });
            col.Children.Add(dynRow);
            // ADS sensitivity + which button is the ADS button: L1 (bumper) or L2 (trigger), some players aim with one, some with the other
            Grid adsRow = NumRow("ADS sensitivity (while aiming)", 0, 2, sl.Ads, delegate (double v) { sl.Ads = (float)v; Push(); QueueSave(); });
            adsRow.ColumnDefinitions.Insert(2, new ColumnDefinition { Width = GridLength.Auto });
            foreach (UIElement ue in adsRow.Children) if (Grid.GetColumn(ue) == 2) Grid.SetColumn(ue, 3);   // the number box moves one column right
            StackPanel adsBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 2; i++)
            {
                bool isL1 = i == 0;
                RadioButton ab = new RadioButton();
                ab.GroupName = "adsbtn";
                ab.Style = (Style)win.FindResource("Seg");
                ab.Width = 56; ab.Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0);
                ab.Content = new TextBlock { Text = isL1 ? "L1" : "L2", FontSize = 13, FontWeight = FontWeights.SemiBold };
                ab.ToolTip = isL1 ? "Aim with L1 (the bumper)" : "Aim with L2 (the trigger)";
                ab.IsChecked = sl.AdsL1 == isL1;
                ab.Checked += delegate { sl.AdsL1 = isL1; Push(); QueueSave(); };
                adsBtns.Children.Add(ab);
            }
            Grid.SetColumn(adsBtns, 2);
            adsRow.Children.Add(adsBtns);
            col.Children.Add(adsRow);
            refresh();

            b.Child = col;
            scriptList.Children.Add(b);
        }

        void BuildRows()
        {
            scriptList.Children.Clear();
            statusLabels.Clear();
            List<ScriptItem> items = set.Scripts[active];
            txtEmpty.Visibility = Visibility.Collapsed;
            if (set.Slots[active].AimOn) BuildAimCard();
            if (items.Count == 0)
            {
                TextBlock none = new TextBlock { Text = "No scripts in this slot yet. Press Add Script.", Margin = new Thickness(6, 12, 6, 0) };
                none.Style = (Style)win.FindResource("Muted");
                scriptList.Children.Add(none);
            }
            foreach (ScriptItem it in items)
            {
                ScriptItem item = it;
                Border b = new Border();
                b.Style = (Style)win.FindResource("Card");
                b.CornerRadius = new CornerRadius(16);
                b.Padding = new Thickness(14, 10, 14, 10);
                b.Margin = new Thickness(0, 0, 0, 8);
                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                CheckBox sw = new CheckBox();
                sw.Style = (Style)win.FindResource("Switch");
                sw.IsChecked = item.On;
                sw.VerticalAlignment = VerticalAlignment.Center;
                RoutedEventHandler flipped = delegate
                {
                    item.On = sw.IsChecked == true;
                    if (item.On) { item.Instance = null; item.Failed = false; item.Error = null; }
                    set.Save();
                    Rebuild();
                };
                sw.Checked += flipped;
                sw.Unchecked += flipped;
                g.Children.Add(sw);

                StackPanel info = new StackPanel();
                info.Margin = new Thickness(14, 0, 10, 0);
                info.VerticalAlignment = VerticalAlignment.Center;
                TextBlock name = new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
                TextBlock status = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                status.Style = (Style)win.FindResource("Muted");
                info.Children.Add(name);
                info.Children.Add(status);
                Grid.SetColumn(info, 1);
                g.Children.Add(info);
                statusLabels[item] = status;

                Button edit = new Button { Content = "Edit", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
                edit.Style = (Style)win.FindResource("Glass");
                edit.Click += delegate { OpenEditor(item); };
                Grid.SetColumn(edit, 2);
                g.Children.Add(edit);

                Button del = new Button { Content = "Delete", Padding = new Thickness(16, 7, 16, 7) };
                del.Style = (Style)win.FindResource("Glass");
                del.Click += delegate { DeleteScript(item); };
                Grid.SetColumn(del, 3);
                g.Children.Add(del);

                StackPanel col = new StackPanel();
                col.Children.Add(g);
                foreach (ParamDef pd in item.Defs) col.Children.Add(ParamRow(item, pd));
                b.Child = col;
                scriptList.Children.Add(b);
            }
        }

        ScriptItem NewFromLibrary(LibEntry e)
        {
            ScriptItem it = new ScriptItem();
            it.Id = ScriptItem.NewId();
            it.Name = e.Name;
            it.Code = e.Code;
            it.On = false;
            it.Reparse(null);
            Settings.SaveCode(it);
            return it;
        }

        void AddFromLibrary(LibEntry e)
        {
            if (e.IsAim)
            {
                set.Slots[active].AimOn = true;
                set.Save();
                Push();
                BuildRows();
                return;
            }
            ScriptItem it = NewFromLibrary(e);
            if (e.Blank) it.Name = "Script " + (set.Scripts[active].Count + 1);
            set.Scripts[active].Add(it);
            set.Save();
            BuildRows();
            Rebuild();
            if (e.Blank) OpenEditor(it);
        }

        void OpenLibrary()
        {
            Window w = (Window)XamlReader.Parse(Th.Fill(Xaml.LibraryWin));
            w.Owner = win;
            StackPanel list = (StackPanel)w.FindName("libList");
            ((Button)w.FindName("btnWinClose")).Click += delegate { w.Close(); };
            foreach (LibEntry le in ScriptLibrary.Items)
            {
                LibEntry entry = le;
                if (entry.IsAim && set.Slots[active].AimOn) continue;   // this slot already has its Aim card
                StackPanel content = new StackPanel();
                content.Width = 520;
                content.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, FontSize = 15 });
                TextBlock desc = new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
                desc.Style = (Style)win.FindResource("Muted");
                content.Children.Add(desc);
                Button b = new Button { Content = content, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
                b.Style = (Style)win.FindResource("Glass");
                b.Click += delegate { w.Close(); AddFromLibrary(entry); };
                list.Children.Add(b);
            }
            w.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);   // rounded corners
                w.Background = new SolidColorBrush(Th.BackC);
            };
            w.ShowDialog();
        }

        // ---- bar + number box for a script's "@param" lines
        static string FmtNum(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        void QueueSave()
        {
            if (saveTimer == null)
            {
                saveTimer = new DispatcherTimer();
                saveTimer.Interval = TimeSpan.FromMilliseconds(600);
                saveTimer.Tick += delegate { saveTimer.Stop(); set.Save(); };
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SetParam(ScriptItem item, string name, float v)
        {
            Dictionary<string, float> d = new Dictionary<string, float>(item.Values);
            d[name] = v;
            item.Values = d;
            QueueSave();
        }

        Grid ParamRow(ScriptItem item, ParamDef pd)
        {
            Grid pg = new Grid();
            pg.Margin = new Thickness(0, 10, 0, 0);
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock lbl = new TextBlock { Text = pd.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = pd.Label };
            lbl.Style = (Style)win.FindResource("Muted");
            float cur;
            if (!item.Values.TryGetValue(pd.Name, out cur)) cur = pd.Default;
            Slider sl = new Slider();
            sl.Minimum = pd.Min; sl.Maximum = pd.Max;
            sl.SmallChange = (pd.Max - pd.Min) / 100.0; sl.LargeChange = (pd.Max - pd.Min) / 10.0;
            sl.Value = cur;
            sl.Margin = new Thickness(0, 0, 14, 0);
            TextBox tb = new TextBox();
            tb.Style = (Style)win.FindResource("Field");
            tb.Width = 80; tb.Padding = new Thickness(6, 4, 6, 4);
            tb.Text = FmtNum(cur);
            bool sync = false;
            sl.ValueChanged += delegate
            {
                if (sync) return;
                SetParam(item, pd.Name, (float)sl.Value);
                sync = true; tb.Text = FmtNum((float)sl.Value); sync = false;
            };
            Action commit = delegate
            {
                float v;
                if (float.TryParse(tb.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    v = Math.Max(pd.Min, Math.Min(pd.Max, v));
                    sync = true; sl.Value = v; sync = false;
                    SetParam(item, pd.Name, v);
                    tb.Text = FmtNum(v);
                }
                else
                {
                    float old;
                    item.Values.TryGetValue(pd.Name, out old);
                    tb.Text = FmtNum(old);
                }
            };
            tb.LostFocus += delegate { commit(); };
            tb.KeyDown += delegate (object s, KeyEventArgs k) { if (k.Key == Key.Enter) { commit(); Keyboard.ClearFocus(); } };
            Grid.SetColumn(sl, 1);
            Grid.SetColumn(tb, 2);
            pg.Children.Add(lbl);
            pg.Children.Add(sl);
            pg.Children.Add(tb);
            return pg;
        }

        // ---- Apply Scripts / Verify Controller (Scripts page)
        void SetStatus(string text, bool ok)
        {
            txtActionStatus.Text = text;
            txtActionStatus.ToolTip = text;
            txtActionStatus.Foreground = new SolidColorBrush(ok ? Th.OkC : Th.ErrC);
        }

        void After(int ms, Action a)
        {
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }

        // Recompiles every script that is ON in the selected slot, hands the live list to the engine,
        // then waits for real reports and checks that each script actually executed.
        void ApplyScripts()
        {
            List<ScriptItem> on = set.Scripts[active].FindAll(delegate (ScriptItem x) { return x.On; });
            int slotNo = active + 1;
            if (on.Count == 0)
            {
                ++rebuildVer;
                eng.Scripts = new ScriptItem[0];
                SetStatus("No script is switched ON in slot " + slotNo + ", so nothing is applied.", true);
                return;
            }
            SetStatus("Applying " + on.Count + " script(s)...", true);
            foreach (ScriptItem it in on) { it.Instance = null; it.Failed = false; it.Error = null; }
            int ver = ++rebuildVer;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ScriptItem> live = new List<ScriptItem>();
                foreach (ScriptItem it in on)
                {
                    string err;
                    ScriptBase sb = ScriptHost.Compile(it.Code, out err);
                    it.CompiledCode = it.Code; it.Error = err; it.Failed = sb == null; it.Instance = sb;
                    if (sb != null) live.Add(it);
                }
                if (ver == rebuildVer) eng.Scripts = live.ToArray();
                long[] before = new long[live.Count];
                for (int k = 0; k < live.Count; k++) before[k] = live[k].Runs;
                Thread.Sleep(600);
                List<string> running = new List<string>(), notRunning = new List<string>();
                foreach (ScriptItem it in on)
                {
                    int idx = live.IndexOf(it);
                    if (idx >= 0 && !it.Failed && it.Runs > before[idx]) running.Add(it.Name);
                    else if (it.Error != null) notRunning.Add(it.Name + " (" + it.Error + ")");
                    else notRunning.Add(it.Name);
                }
                bool padOn = eng.PadOn, connected = eng.Connected, ok;
                StringBuilder m = new StringBuilder();
                if (!connected)
                {
                    ok = false;
                    m.Append(live.Count + " of " + on.Count + " script(s) compiled and loaded, but the DualSense is not connected, so I could not confirm they are executing.");
                }
                else if (notRunning.Count == 0)
                {
                    ok = padOn;
                    m.Append("OK - all " + running.Count + " script(s) of slot " + slotNo + " are running: " + string.Join(", ", running.ToArray()) + ".");
                    if (!padOn) m.Append(" But the virtual controller is OFF, so nothing reaches the game.");
                }
                else
                {
                    ok = false;
                    m.Append(running.Count + " of " + on.Count + " running. NOT running: " + string.Join("; ", notRunning.ToArray()));
                }
                string text = m.ToString();
                win.Dispatcher.BeginInvoke(new Action(delegate { SetStatus(text, ok); }));
            });
        }

        // Checks that chiaki-ng uses only this program's virtual controller (from the log of its running session).
        // chiaki cannot be forced to change controllers while it runs, so on a problem it offers a proper restart.
        void VerifyController()
        {
            if (!eng.Connected) { SetStatus("The DualSense is not connected to this program - plug it in first.", false); return; }
            StringBuilder m = new StringBuilder();
            m.Append("DualSense: connected, " + eng.ReportsPerSec + " Hz. ");
            if (!eng.PadOn)
            {
                chkPad.IsChecked = true;
                TogglePad(true);
                if (!eng.PadOn) { SetStatus(m + "The virtual controller could not be turned on (" + eng.PadError + ").", false); return; }
            }

            Process[] ch = Process.GetProcessesByName("chiaki");
            if (ch.Length == 0)
            {
                SetStatus(m.ToString().TrimEnd(), true);   // only the DualSense line when chiaki-ng is not running
                return;
            }

            DateTime started = DateTime.MinValue;
            try { started = ch[0].StartTime; } catch (Exception) { }
            List<string> opened = new List<string>();
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chiaki", "Chiaki", "log");
                FileInfo newest = null;
                foreach (FileInfo f in new DirectoryInfo(logDir).GetFiles("*.log"))
                    if (f.LastWriteTime >= started && (newest == null || f.LastWriteTime > newest.LastWriteTime)) newest = f;
                if (newest == null)
                {
                    m.Append("chiaki-ng has no session log yet - wait a few seconds and verify again.");
                    SetStatus(m.ToString(), false);
                    return;
                }
                using (FileStream fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader rd = new StreamReader(fs))
                {
                    string line;
                    while ((line = rd.ReadLine()) != null)
                        if (line.IndexOf("Controller ", StringComparison.Ordinal) >= 0 && line.IndexOf(" opened:", StringComparison.Ordinal) >= 0) opened.Add(line);
                }
            }
            catch (Exception ex) { SetStatus(m + "Could not read chiaki's log: " + ex.Message, false); return; }

            bool virt = false, phys = false;
            foreach (string n in opened)
            {
                if (n.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0) phys = true;          // the real pad
                else if (n.IndexOf("Xbox 360", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         n.IndexOf("PS4 Controller", StringComparison.OrdinalIgnoreCase) >= 0) virt = true; // our virtual pad
                else phys = true;
            }
            if (virt && !phys)
            {
                m.Append("OK - chiaki-ng uses ONLY this program's virtual controller (the physical DualSense is hidden from it).");
                SetStatus(m.ToString(), true);
                return;
            }
            string problem = !virt ? "chiaki-ng did not open the virtual controller" : "chiaki-ng also sees the physical DualSense, so its input does not go only through this program";
            SetStatus(m + "PROBLEM - " + problem + ".", false);
            if (!Dlg.Confirm(win, "Problem: " + problem + ".\n\nRestart chiaki-ng from this window now? The stream ends for a few seconds.", "Restart", false)) return;
            SetStatus("Restarting chiaki-ng from this window...", true);
            foreach (Process p in ch)
            {
                try { p.CloseMainWindow(); if (!p.WaitForExit(6000)) p.Kill(); } catch (Exception) { }
            }
            After(12000, delegate { LaunchChiaki(); After(9000, delegate { VerifyController(); }); });
        }

        void DeleteScript(ScriptItem it)
        {
            if (!Dlg.Confirm(win, "Delete \"" + it.Name + "\"?", "Delete", true)) return;
            set.Scripts[active].Remove(it);
            Settings.DeleteCode(it);
            set.Save();
            BuildRows();
            Rebuild();
        }

        void OpenEditor(ScriptItem item)
        {
            Window w = (Window)XamlReader.Parse(Th.Fill(Xaml.ScriptWin));
            w.Owner = win;
            TextBox name = (TextBox)w.FindName("txtName");
            TextBox code = (TextBox)w.FindName("txtCode");
            TextBlock status = (TextBlock)w.FindName("lblStatus");
            name.Text = item.Name;
            code.Text = item.Code;
            w.Title = "Script - slot " + (active + 1);
            ((Button)w.FindName("btnWinClose")).Click += delegate { w.Close(); };

            Action save = delegate
            {
                string n = name.Text.Trim();
                if (n.Length > 0) item.Name = n;
                item.Code = code.Text;
                item.Reparse(null);
                Settings.SaveCode(item);
                set.Save();
                item.Instance = null; item.Failed = false; item.Error = null;
                BuildRows();
                Rebuild();
            };
            ((Button)w.FindName("btnApply")).Click += delegate { save(); };
            ((Button)w.FindName("btnReset")).Click += delegate { code.Text = ScriptHost.Template; };
            w.PreviewKeyDown += delegate (object s, KeyEventArgs k)
            {
                if (k.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { save(); k.Handled = true; }
            };
            w.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1, round = 2; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);   // rounded corners
                w.Background = new SolidColorBrush(Th.BackC);
            };

            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(400);
            t.Tick += delegate
            {
                bool err;
                string s = StatusOf(item, out err);
                if (!item.On) s = "Saved. The switch for this script is OFF - turn it on in the list to run it.";
                status.Text = s;
                status.Foreground = err ? new SolidColorBrush(Th.ErrC) : new SolidColorBrush(Th.DimC);
            };
            t.Start();
            w.Closed += delegate { t.Stop(); };
            w.Show();
        }

        // ---- 60 Hz UI refresh
        void Tick()
        {
            viewL.SetDots(eng.RawLX, eng.RawLY, eng.OutLX, eng.OutLY);
            viewR.SetDots(eng.RawRX, eng.RawRY, eng.OutRX, eng.OutRY);
            txtDevice.Text = eng.Connected ? eng.DeviceName + " connected" : "Looking for a controller...";
            bool mk = eng.MkMode;
            if (mk != mkShown)
            {
                mkShown = mk;
                btnMk.Style = (Style)win.FindResource(mk ? "GreenBtn" : "Glass");
                btnMk.Content = mk ? "M&K mode: ON" : "M&K mode";
            }
            bool on = eng.PadOn;
            if (chkPad.IsChecked != on) chkPad.IsChecked = on;
            txtPadHint.Text = eng.PadError != null ? "Error: " + eng.PadError : (on ? "Active - the game sees this virtual controller." : "Off - turn it on, then start chiaki-ng.");
            int running = 0;
            foreach (ScriptItem it in eng.Scripts) if (!it.Failed) running++;
            txtFooter.Text = "Reports: " + eng.ReportsPerSec + " Hz    Scripts running: " + running +
                "\nGrey dot = raw stick, green dot = after deadzone.  Red ring = Min, green ring = Max.";
            if (pageScripts.Visibility == Visibility.Visible)
            {
                foreach (KeyValuePair<ScriptItem, TextBlock> kv in statusLabels)
                {
                    bool err;
                    kv.Value.Text = StatusOf(kv.Key, out err);
                    kv.Value.Foreground = err ? new SolidColorBrush(Th.ErrC) : new SolidColorBrush(Th.DimC);
                }
            }
        }
    }

    static class Program
    {
        // `--selftest` writes selftest.txt next to the exe: virtual pad create/remove + script compile checks.
        static void SelfTest()
        {
            StringBuilder o = new StringBuilder();
            try
            {
                Pad p = new Pad(true, 0x05C4);
                p.SendNeutral();
                p.Dispose();
                Pad x = new Pad(false, 0);
                x.SendNeutral();
                x.Dispose();
                o.AppendLine("virtual pads (DualShock 4 and Xbox 360): OK");
            }
            catch (Exception e) { o.AppendLine("virtual pad: FAIL " + e.Message); }
            try
            {
                // DualShock 4 USB report: stick right-up, L2 half, cross + hat up-right, options, PS
                Engine e = new Engine();
                byte[] b = new byte[64];
                b[0] = 0x01; b[1] = 128; b[2] = 128; b[3] = 255; b[4] = 0; b[5] = 0x21; b[6] = 0x20; b[7] = 0x01; b[8] = 128; b[9] = 0;
                e.FeedDs4(b, 64);
                Engine.Raw r = e.LastRaw;
                o.AppendLine("DS4 parser: RX=" + r.RX.ToString("0.00") + " (1.00) RY=" + r.RY.ToString("0.00") + " (1.00) LT=" + r.LT.ToString("0.00") + " (0.50) cross=" + r.Cross + " up=" + r.Up + " right=" + r.Right + " options=" + r.Options + " PS=" + r.PS + " touchpad=" + r.Touchpad + " (expect True/True/True/True/True/False)");
                b[0] = 0x11; Array.Clear(b, 1, 63); b[1] = 0xC0; b[2] = 0x00; b[3] = 128; b[4] = 128; b[5] = 128; b[6] = 128; b[7] = 0x08; b[8] = 0x00; b[9] = 0x02;
                e.FeedDs4(b, 64);
                o.AppendLine("DS4 Bluetooth report 0x11: ok=" + (e.LastRaw.Touchpad && !e.LastRaw.Cross && !e.LastRaw.Up) + " (expect True)");
                // M&K mode: D-pad Up + Cross switches it on, then the right stick moves the real mouse pointer
                e.MkMode = false;   // the parser tests above held Up + Cross, which already switched it
                int kbHits = 0;
                e.KeyboardRequested += delegate { kbHits++; };
                Array.Clear(b, 0, 64); b[0] = 1; b[1] = b[2] = b[3] = b[4] = 128; b[5] = 0x08; e.FeedDs4(b, 64); b[5] = 0x04; e.FeedDs4(b, 64); b[5] = 0x24; e.FeedDs4(b, 64); b[5] = 0x08; e.FeedDs4(b, 64);
                o.AppendLine("Keyboard combo with M&K off: events=" + kbHits + " (expect 1), mk=" + e.MkMode + " (expect False)");
                Array.Clear(b, 0, 64); b[0] = 1; b[1] = b[2] = b[3] = b[4] = 128; b[5] = 0x08;
                e.FeedDs4(b, 64);
                b[5] = 0x20; e.FeedDs4(b, 64);   // D-pad Up + Cross down
                o.AppendLine("combo debug: mk=" + e.MkCombo + " up=" + e.LastRaw.Up + " cross=" + e.LastRaw.Cross + " on=" + e.MkMode);
                b[5] = 0x08; e.FeedDs4(b, 64);
                Native.POINT p0, p1;
                Native.GetCursorPos(out p0);
                b[3] = 255;
                for (int k = 0; k < 20; k++) { e.FeedDs4(b, 64); Thread.Sleep(5); }
                Native.GetCursorPos(out p1);
                bool wasOn = e.MkMode;
                o.AppendLine("M&K mode: on=" + wasOn + " (True), pointer moved right by " + (p1.X - p0.X) + " px, vertical " + (p1.Y - p0.Y) + " px (expect > 0 and 0)");
                b[3] = 128; e.FeedDs4(b, 64);
                // touchpad: a finger sliding 100 units right moves the pointer by about 160 px
                Native.GetCursorPos(out p0);
                b[35] = 0; b[36] = 100; b[37] = 0; b[38] = 0; e.FeedDs4(b, 64);
                b[36] = 200; e.FeedDs4(b, 64);
                b[35] = 0x80; e.FeedDs4(b, 64);
                Native.GetCursorPos(out p1);
                o.AppendLine("Touchpad pointer: moved " + (p1.X - p0.X) + " px right (expect ~160)");
                b[5] = 0x20; e.FeedDs4(b, 64); b[5] = 0x08; e.FeedDs4(b, 64);
                o.AppendLine("M&K mode off again by the same combo: " + !e.MkMode + " (True)");
                XIn.STATE xs = new XIn.STATE();
                int xr0 = XIn.Get(0, out xs);
                o.AppendLine("XInput: available (call returned " + xr0 + "; 0 = pad found, 1167 = none connected)");
            }
            catch (Exception e) { o.AppendLine("multi-controller parsers: FAIL " + e.Message); }
            try
            {
                Pad vp = new Pad(true, 0x09CC);
                Thread.Sleep(1500);
                int virt = 0, phys = 0;
                foreach (Engine.HidDev d in Engine.EnumHid()) { if (d.Virtual) virt++; else phys++; }
                vp.Dispose();
                o.AppendLine("own virtual pad detected as virtual: " + (virt >= 1) + " (virtual " + virt + ", physical Sony " + phys + ")");
            }
            catch (Exception e) { o.AppendLine("virtual detection: FAIL " + e.Message); }
            string err;
            long t0 = Stopwatch.GetTimestamp();
            ScriptBase s = ScriptHost.Compile(ScriptHost.Template.Replace("// Curve(", "Curve(").Replace("// bool t", "bool t").Replace("// if (Math", "if (Math"), out err);
            o.AppendLine("script compile: " + (s != null ? "OK" : "FAIL " + err) + " (" + ((Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency) + " ms)");
            if (s != null)
            {
                State st = new State(); st.RX = 0.5f; st.RY = 0f; st.L1 = true; st.R1 = false; st.T = 0.0;
                s.Update(st);
                // curve 0.5^1.4 = 0.379, then jitter at t=0 adds +0.08 on X: 0.459, Y stays 0
                o.AppendLine("script run: RX=" + st.RX.ToString("0.000") + " (expect 0.459)  RY=" + st.RY.ToString("0.000") + " (expect 0.000)  L1=" + st.L1 + " R1=" + st.R1 + " (expect False/True)");
            }
            ScriptBase bad = ScriptHost.Compile("public override void Update(State s) { s.LX = ; }", out err);
            o.AppendLine("bad script rejected: " + (bad == null) + " -> " + err);
            foreach (LibEntry le in ScriptLibrary.Items)
            {
                ScriptItem it = new ScriptItem(); it.Code = le.Code; it.Reparse(null);
                ScriptBase sb = ScriptHost.Compile(le.Code, out err);
                if (sb == null) { o.AppendLine("lib '" + le.Name + "': FAIL " + err); continue; }
                sb.Params = it.Values;
                State t = new State(); t.RX = 0.5f; t.RY = 0f; t.LT = 1f; t.RT = 1f; t.T = 0.25; t.Dt = 0.001;
                sb.Update(t);
                o.AppendLine("lib '" + le.Name + "': OK params=" + it.Defs.Count + "  RX=" + t.RX.ToString("0.000") + " RY=" + t.RY.ToString("0.0000") + " RT=" + t.RT.ToString("0"));
            }
            // aim curves: print a table and check the properties the design promises
            StringBuilder cvs = new StringBuilder("aim curves (tilt: standard / linear / dynamic0 / dynamic50 / dynamic100):");
            foreach (float m in new float[] { 0.10f, 0.25f, 0.50f, 0.75f, 0.90f })
                cvs.Append("  " + m.ToString("0.00") + ": " + AimCurve.Shape(0, 0.5f, m).ToString("0.000") + "/" + AimCurve.Shape(1, 0.5f, m).ToString("0.000") + "/" +
                    AimCurve.Shape(2, 0f, m).ToString("0.000") + "/" + AimCurve.Shape(2, 0.5f, m).ToString("0.000") + "/" + AimCurve.Shape(2, 1f, m).ToString("0.000"));
            o.AppendLine(cvs.ToString());
            bool mono = true, same = true;
            foreach (float lv in new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float prev = -1f;
                for (int i = 0; i <= 1000; i++)
                {
                    float m = i / 1000f, v = AimCurve.Shape(2, lv, m);
                    if (v + 1e-6f < prev) mono = false;
                    prev = v;
                }
            }
            for (int i = 0; i <= 100; i++) { float m = i / 100f; if (Math.Abs(AimCurve.Shape(2, 0f, m) - AimCurve.Shape(0, 0f, m)) > 1e-6f) same = false; }
            o.AppendLine("aim curve monotonic: " + mono + " ; dynamic level 0 equals standard: " + same);
            float vx = 0.3f, vy = 0.4f;
            AimCurve.Apply(ref vx, ref vy, 0, 0f);
            o.AppendLine("direction kept by the curve (0.3:0.4 ratio = 0.750): " + (vx / vy).ToString("0.000") + " ; length 0.5 -> " + Math.Sqrt(vx * vx + vy * vy).ToString("0.000") + " (standard 0.5^1.5 = 0.354)");
            o.AppendLine("update compare: v2.5.1>2.5.0=" + Updater.IsNewer("v2.5.1", "2.5.0") + " (True) 2.5.0>2.5.0=" + Updater.IsNewer("2.5.0", "2.5.0") + " (False) 2.4.9>2.5.0=" + Updater.IsNewer("2.4.9", "2.5.0") + " (False) 3.0>2.9.9=" + Updater.IsNewer("3.0", "2.9.9") + " (True) 2.10.0>2.9.0=" + Updater.IsNewer("2.10.0", "2.9.0") + " (True) v2.6.0-beta>2.5.0=" + Updater.IsNewer("v2.6.0-beta", "2.5.0") + " (True)");
            File.WriteAllText(Path.Combine(Settings.Dir, "selftest.txt"), o.ToString());
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") { SelfTest(); return; }
            if (args.Length > 1 && args[0] == "--pad-shot") { PadPanel.Shot(args[1]); return; }
            if (args.Length > 1 && args[0] == "--press") { Native.PressCombo(Osk.ComboVks(args[1])); Thread.Sleep(5500); Native.ReleaseAlt(); return; }   // developer check of a key command
            if (args.Length > 1 && args[0] == "--export-art") { int st = 0; if (args.Length > 2) int.TryParse(args[2], out st); PadArt.Export(args[1], st); return; }
            bool created;
            bool chiakiStart = args.Length > 0 && args[0] == "--chiaki";   // the desktop shortcut of chiaki-ng
            bool startup = args.Length > 0 && (args[0] == "--startup" || chiakiStart);   // started by Windows / by that shortcut: stay in the tray
            if (args.Length > 0 && args[0] == "--update-demo") Updater.Demo();
            using (Mutex m = new Mutex(true, "ControllerUnitOf5belan.single", out created))
            {
                if (!created)
                {
                    // "--wait": started by the theme switch; wait for the old copy to quit instead of giving up
                    if (args.Length == 0 || args[0] != "--wait")
                    {
                        // already running (maybe only in the tray): ask that copy to show its window
                        if (chiakiStart)
                        {
                            try { EventWaitHandle.OpenExisting("ControllerUnitOf5belan.chiaki").Set(); } catch (Exception) { }
                        }
                        else if (!startup)
                        {
                            try { EventWaitHandle.OpenExisting("ControllerUnitOf5belan.show").Set(); } catch (Exception) { }
                        }
                        return;
                    }
                    try { if (!m.WaitOne(15000)) return; }
                    catch (AbandonedMutexException) { }   // the old copy ended without releasing it: we own it now
                }
                Engine eng = new Engine();
                eng.Start();
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                MainWin w = new MainWin(eng);
                w.SetupTray();
                if (chiakiStart) w.Window.Dispatcher.BeginInvoke(new Action(w.StartChiaki), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (startup) { app.MainWindow = w.Window; app.Run(); }   // never shown until the tray icon is clicked
                else app.Run(w.Window);
            }
        }
    }
}
