// P/Invoke declarations: powrprof (power plans), PDH (energy meters), ATK ACPI (ASUS modes), display settings.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PowerCap
{
    static class Native
    {
        // ---- powrprof ----
        [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, out IntPtr dst);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] public static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, string name, uint size);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buf, ref uint size);
        [DllImport("powrprof.dll")] public static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint access, uint index, byte[] buf, ref uint size);
        [DllImport("powrprof.dll")] public static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, [Out] PROCESSOR_POWER_INFORMATION[] outBuf, uint outLen);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSOR_POWER_INFORMATION { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        // ---- PDH ----
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint fmt, IntPtr type, out PDH_FMT_COUNTERVALUE value);
        [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
        // Wildcard counters: buffer of PDH_FMT_COUNTERVALUE_ITEM_W { wchar_t* name; PDH_FMT_COUNTERVALUE value; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint fmt, ref uint bytes, out uint count, IntPtr buffer);
        [StructLayout(LayoutKind.Explicit)]
        public struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; }
        public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;

        // ---- ATK ACPI (ASUS) ----
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, byte[] i, uint il, byte[] o, uint ol, ref uint r, IntPtr ov);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        // DEVS call; returns 1 on success, 0 if the firmware rejects the value, -1 if the driver is missing.
        public static long AtkSet(uint dev, uint value) { return AtkCall(0x53564544u, dev, value); }
        // DSTS (read-only status query); -1 on failure. Some devices return a package: dword picks the element.
        public static long AtkGet(uint dev, int dword = 0) { return AtkCall(0x53545344u, dev, 0, dword); }
        static long AtkCall(uint method, uint dev, uint value, int dword = 0)
        {
            var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) return -1;
            var i = new byte[16];
            BitConverter.GetBytes(method).CopyTo(i, 0); BitConverter.GetBytes(8u).CopyTo(i, 4);
            BitConverter.GetBytes(dev).CopyTo(i, 8); BitConverter.GetBytes(value).CopyTo(i, 12);
            var o = new byte[16]; uint r = 0;
            bool ok = DeviceIoControl(h, 0x0022240C, i, 16, o, 16, ref r, IntPtr.Zero);
            CloseHandle(h);
            return ok && r >= dword * 4 + 4 ? BitConverter.ToUInt32(o, dword * 4) : -1;
        }

        // ---- display ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
        static DEVMODE NewMode() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }
        public static int CurrentHz() { var d = NewMode(); EnumDisplaySettings(null, -1, ref d); return d.dmDisplayFrequency; }
        public static int[] RefreshRates()
        {
            var cur = NewMode(); EnumDisplaySettings(null, -1, ref cur);
            var list = new SortedSet<int>();
            for (int i = 0; ; i++)
            {
                var d = NewMode();
                if (!EnumDisplaySettings(null, i, ref d)) break;
                if (d.dmPelsWidth == cur.dmPelsWidth && d.dmPelsHeight == cur.dmPelsHeight) list.Add(d.dmDisplayFrequency);
            }
            var a = new int[list.Count]; list.CopyTo(a); return a;
        }
        public static int SetHz(int hz)   // 0 = DISP_CHANGE_SUCCESSFUL
        {
            var d = NewMode(); EnumDisplaySettings(null, -1, ref d);
            d.dmDisplayFrequency = hz; d.dmFields = 0x400000;
            return ChangeDisplaySettings(ref d, 1);
        }
    }
}
