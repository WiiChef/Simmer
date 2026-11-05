// Draining down to the charge limit with the charger connected (the limit itself only stops charging). The
// test-signed SimmerBmdDrain.sys (tools\BmdDrain) puts the battery into ACPI "discharge with AC present" (_BMC bit 2)
// and takes it out again; Windows shows the charger as unplugged meanwhile. The kernel ends the drain by itself when
// Simmer stops its heartbeat (30 s), closes the handle or exits, at shutdown and after 4 hours, so a hang or crash
// can't leave the battery discharging. Works only while Windows runs in test-signing mode. Needs administrator.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PowerCap
{
    static class Drain
    {
        const string Service = "SimmerBmdDrain";
        const uint IoStart = 0x833BE000, IoBeat = 0x833BE004, IoStop = 0x833BE008;
        const int Minutes = 240;
        public static string Sys { get { return Path.Combine(Elevation.InstallDir, "SimmerBmdDrain.sys"); } }
        static SafeFileHandle _h;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, uint inLen, byte[] output, uint outLen, out uint returned, IntPtr ov);
        [StructLayout(LayoutKind.Sequential)] struct DevPropKey { public Guid Fmtid; public uint Pid; }
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint inst, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(uint inst, ref DevPropKey key, out uint type, byte[] buf, ref uint size, uint flags);

        // Why a drain can't run here, or null.
        public static string Unavailable()
        {
            if (!Pep.IsAdmin) return "needs Simmer running as administrator";
            string opts = null;
            try { opts = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "SystemStartOptions", null) as string; } catch { }
            if (opts == null || opts.IndexOf("TESTSIGNING", StringComparison.OrdinalIgnoreCase) < 0) return "Windows test-signing mode is off";
            if (!File.Exists(Sys)) return "drain driver not installed";
            return null;
        }
        public static bool Active { get { return _h != null; } }

        // The device names change between boots; the kernel checks they are this laptop's battery and PMIC.
        static string Pdo(string instance)
        {
            uint inst, type, size = 256; var buf = new byte[size];
            var key = new DevPropKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 16 };   // DEVPKEY_Device_PDOName
            if (CM_Locate_DevNodeW(out inst, instance, 0) != 0 || CM_Get_DevNode_PropertyW(inst, ref key, out type, buf, ref size, 0) != 0)
                throw new InvalidOperationException("device " + instance + " not found");
            return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        }

        static void Sc(string args, params int[] ok)
        {
            var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            p.StandardOutput.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode != 0 && Array.IndexOf(ok, p.ExitCode) < 0) throw new InvalidOperationException("sc " + args.Split(' ')[0] + " failed (" + p.ExitCode + ")");
        }

        struct Status { public uint Active, Control, Saved, Reason; public int Result; }
        static Status Call(uint code, byte[] input)
        {
            var o = new byte[32]; uint n;
            if (!DeviceIoControl(_h, code, input, input == null ? 0 : (uint)input.Length, o, 32, out n, IntPtr.Zero) || n != 32 || BitConverter.ToUInt32(o, 0) != 32)
                throw new InvalidOperationException("drain driver refused the request (error " + Marshal.GetLastWin32Error() + ")");
            return new Status { Active = BitConverter.ToUInt32(o, 8), Control = BitConverter.ToUInt32(o, 12), Saved = BitConverter.ToUInt32(o, 16), Reason = BitConverter.ToUInt32(o, 20), Result = BitConverter.ToInt32(o, 28) };
        }

        // Loads the driver (only for the length of a drain) and starts discharging; throws with the reason if not.
        public static void Start()
        {
            if (_h != null) return;
            string bat = Pdo(@"ACPI\PNP0C0A\2&daba3ff&0"), pmic = Pdo(@"ACPI\QCOM0F8E\2&daba3ff&0");
            Sc("create " + Service + " type= kernel start= demand binPath= \"\\??\\" + Sys + "\"", 1073);   // 1073: exists
            Sc("config " + Service + " binPath= \"\\??\\" + Sys + "\"");
            Sc("start " + Service, 1056);   // 1056: already running
            _h = CreateFile(@"\\.\" + Service, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (_h.IsInvalid) { int e = Marshal.GetLastWin32Error(); _h = null; throw new InvalidOperationException("drain driver not answering (error " + e + ")"); }
            var input = new byte[132];
            Buffer.BlockCopy(BitConverter.GetBytes(Minutes), 0, input, 0, 4);
            var b = Encoding.Unicode.GetBytes(bat); var m = Encoding.Unicode.GetBytes(pmic);
            if (b.Length > 62 || m.Length > 62) { Stop(); throw new InvalidOperationException("unexpected device names"); }
            Buffer.BlockCopy(b, 0, input, 4, b.Length); Buffer.BlockCopy(m, 0, input, 68, m.Length);
            Status s;
            try { s = Call(IoStart, input); } catch { Stop(); throw; }
            if (s.Result != 0 || s.Active != 1)
            {
                string p = Stop();
                throw new InvalidOperationException((s.Result == unchecked((int)0xC00000A3) ? "firmware not ready (calibrating or already discharging)" : "firmware refused (0x" + s.Result.ToString("X8") + ")") + (p == null ? "" : "; " + p));
            }
        }

        // False when the kernel has ended the drain itself (heartbeat missed during sleep, 4 hour limit).
        public static bool Beat()
        {
            if (_h == null) return false;
            try { var s = Call(IoBeat, null); return s.Result == 0 && s.Active == 1; } catch { return false; }
        }

        // Ends the drain and unloads the driver. Returns null once the firmware is confirmed back to normal charging;
        // otherwise why not, and the driver stays loaded so its timer keeps retrying every 5 s.
        public static string Stop()
        {
            if (_h == null) return null;
            string problem = "restore not confirmed";
            for (int i = 0; i < 5 && problem != null; i++)
            {
                // The kernel clears Active only once a fresh read shows the saved control back (or it never started).
                try { if (Call(IoStop, null).Active == 0) problem = null; }
                catch (Exception e) { problem = e.Message; }
            }
            _h.Dispose(); _h = null;   // closing also makes the kernel restore
            if (problem == null) Unload();
            return problem;
        }

        static void Unload() { try { Sc("stop " + Service, 1062); } catch { } }   // 1062: not running
    }
}
