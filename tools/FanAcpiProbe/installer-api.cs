using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class FanProbeInstallApi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid Class; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError=true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr cls, IntPtr hwnd);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool SetupDiOpenDeviceInfo(IntPtr set, string id, IntPtr hwnd, uint flags, ref DeviceInfo device);
    [DllImport("setupapi.dll", SetLastError=true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo device,
        uint property, out uint type, byte[] buffer, uint bytes, out uint needed);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceInfo device,
        uint property, byte[] buffer, uint bytes);
    [DllImport("newdev.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool DiInstallDriver(IntPtr hwnd, string inf, uint flags, out bool reboot);
    [DllImport("newdev.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool DiUninstallDriver(IntPtr hwnd, string inf, uint flags, out bool reboot);

    // Only this literal device is addressable; there is no class-filter operation.
    private const string Target = @"ACPI\ACPI0013\0";
    private static IntPtr Open(out DeviceInfo device)
    {
        IntPtr set=SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if(set==new IntPtr(-1)) throw new Win32Exception();
        device=new DeviceInfo();
        device.Size=(uint)Marshal.SizeOf(typeof(DeviceInfo));
        if(!SetupDiOpenDeviceInfo(set, Target, IntPtr.Zero, 0, ref device)) {
            int error=Marshal.GetLastWin32Error(); SetupDiDestroyDeviceInfoList(set);
            throw new Win32Exception(error);
        }
        return set;
    }
    public static string[] GetUpperFilters()
    {
        DeviceInfo device;
        IntPtr set=Open(out device);
        try {
            byte[] buffer=new byte[65536]; uint type, needed;
            if(!SetupDiGetDeviceRegistryProperty(set,ref device,0x11,out type,buffer,(uint)buffer.Length,out needed)) {
                int error=Marshal.GetLastWin32Error();
                if(error==13) return new string[0]; // absent SPDRP_UPPERFILTERS
                throw new Win32Exception(error);
            }
            if(type!=7 || needed>buffer.Length || needed%2!=0) throw new InvalidOperationException("Invalid UpperFilters property");
            return Encoding.Unicode.GetString(buffer,0,(int)needed).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries);
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }
    public static void SetUpperFilters(string[] filters)
    {
        DeviceInfo device;
        IntPtr set=Open(out device);
        try {
            foreach(string filter in filters)
                if(String.IsNullOrEmpty(filter) || filter.IndexOf('\0')>=0) throw new ArgumentException("Invalid filter");
            byte[] bytes=filters.Length==0 ? null : Encoding.Unicode.GetBytes(String.Join("\0",filters)+"\0\0");
            if(!SetupDiSetDeviceRegistryProperty(set,ref device,0x11,bytes,bytes==null ? 0u : (uint)bytes.Length))
                throw new Win32Exception();
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }
}

