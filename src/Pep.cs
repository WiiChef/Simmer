// SoC power limit (CPU + GPU + memory together) through the Qualcomm PEP driver, \\.\QCOM_PEPDriver.
// Admin only. Protocol from DeckPowerControl.exe, checked with tools\PepCmd.cs (see README):
//   0x88882140 version   in 4 bytes                        out {status, version}
//   0x88882144 register  in {wchar name[16], version}      out {status, client}   status 3 = no free slot
//   0x88882150 get       in {client, domain, pl}           out {status, request mW, window, effective mW, window, max mW, ...}
//   0x8888214C set       in {client, domain, pl, mW, window, 0}  out {status}      mW 0xFFFFFFFF = release
// Clients survive closing the handle (and unregister rejects every layout tried), so one client id is kept per
// boot in pep.client. The ASUS fan mode writes the same limit (Whisper 22 W, Performance 53 W); the last writer
// wins, so the engine re-applies ours when the effective value drifts. Releasing hands control back to ASUS.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PowerCap
{
    static class Pep
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, byte[] i, uint il, byte[] o, uint ol, out uint r, IntPtr ov);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        const uint Soc = 1, Pl1 = 0, Release = 0xFFFFFFFF;
        // PL1 averages power over its window. The firmware uses 15 s, which lets the GPU burst and then clamp hard
        // (a 5-11 W swing around an 8 W target); 2 s holds it within ~0.3 W. 0.5 s is rejected (status 29).
        public const uint SlowWindow = 15000000, FastWindow = 2000000;
        static readonly uint WindowOverride = ParseWindow(Environment.GetEnvironmentVariable("POWERCAP_PEP_WINDOW_US"));   // tests
        static uint ParseWindow(string s) { uint v; return uint.TryParse(s, out v) && v >= 1000 ? v : 0; }
        const uint StBadClient = 24;
        static readonly object Lock = new object();
        static IntPtr _h = IntPtr.Zero;
        static uint _client;
        static bool _ready;
        public static string Problem;        // why the limit is unavailable, or null
        public static bool Holding;          // we have (or may have) a request in the driver; cleared only by a successful release
        static bool _closed;                 // set on quit: no new requests after the final release
        static bool _releaseFailLogged;

        static string ClientFile { get { return Path.Combine(Store.Dir, "pep.client"); } }

        public static bool IsAdmin
        {
            get { try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
        }

        static uint[] Call(uint code, byte[] input, int outLen)
        {
            var o = new byte[outLen]; uint r;
            if (!DeviceIoControl(_h, code, input, (uint)input.Length, o, (uint)outLen, out r, IntPtr.Zero)) return null;
            var u = new uint[outLen / 4];
            for (int i = 0; i < u.Length; i++) u[i] = BitConverter.ToUInt32(o, i * 4);
            return u;
        }
        static byte[] U32(params uint[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4); return b; }
        static uint[] Get() { return Call(0x88882150, U32(_client, Soc, Pl1), 44); }

        // Opens the driver and finds a working client: the saved one if still valid this boot, else a new registration.
        static bool Ready(bool allowRegister = true)
        {
            if (_ready) return true;
            if (!IsAdmin) { Problem = "needs Simmer running as administrator"; return false; }
            if (_h == IntPtr.Zero)
            {
                var h = CreateFile(@"\\.\QCOM_PEPDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                if (h == new IntPtr(-1)) { Problem = "Qualcomm PEP driver not available (error " + Marshal.GetLastWin32Error() + ")"; return false; }
                _h = h;
            }
            uint saved;
            try { if (uint.TryParse(File.ReadAllText(ClientFile).Trim(), System.Globalization.NumberStyles.HexNumber, null, out saved)) { _client = saved; var g = Get(); if (g != null && g[0] == 0) { _ready = true; Problem = null; return true; } } } catch { }
            if (!allowRegister) return false;
            var v = Call(0x88882140, new byte[4], 8);
            uint ver = v != null && v[0] == 0 && v[1] != 0 ? v[1] : 2;
            var reg = new byte[36];
            Encoding.Unicode.GetBytes("PowerCap").CopyTo(reg, 0);
            BitConverter.GetBytes(ver).CopyTo(reg, 32);
            var c = Call(0x88882144, reg, 8);
            if (c == null || c[0] != 0) { Problem = "PEP driver has no free client slot (status " + (c == null ? "?" : c[0].ToString()) + "); a reboot clears it"; return false; }
            _client = c[1];
            try { File.WriteAllText(ClientFile, _client.ToString("X8")); } catch { }
            Store.Log("PEP client registered: 0x" + _client.ToString("X8"));
            _ready = true; Problem = null;
            return true;
        }

        // Effective SoC PL1 in W (whoever set it), or NaN.
        public static double EffectiveW()
        {
            lock (Lock)
            {
                if (!Ready()) return double.NaN;
                var g = Get();
                if (g == null || g[0] != 0) { if (g != null && g[0] == StBadClient) _ready = false; return double.NaN; }
                return g[9] / 1000.0;
            }
        }

        public static bool Set(double watts, uint window)
        {
            lock (Lock)
            {
                if (_closed || !Ready()) return false;
                uint mw = (uint)Math.Round(watts * 1000);
                var r = Call(0x8888214C, U32(_client, Soc, Pl1, mw, WindowOverride != 0 ? WindowOverride : window, 0), 4);
                if (r == null || r[0] != 0)
                {
                    if (r != null && r[0] == StBadClient) _ready = false;
                    Store.Log("PEP set SoC PL1 " + watts.ToString("0.0") + " W rejected: status " + (r == null ? "ioctl failed" : r[0].ToString()));
                    return false;
                }
                Holding = true;
                return true;
            }
        }

        // At start: a previous PowerCap that was killed may have left its request in the driver. Uses only the
        // saved client (no new registration); after a reboot it is gone and there is nothing to release.
        public static void ReleaseStale()
        {
            lock (Lock)
            {
                if (!IsAdmin || !File.Exists(ClientFile) || !Ready(false)) return;
                var g = Get();
                if (g == null || g[0] != 0 || g[1] == Release) return;   // g[1] = this client's request
                // Held until released: if this fails, the engine's off path keeps retrying (ReleaseLimit).
                Holding = true;
                var r = Call(0x8888214C, U32(_client, Soc, Pl1, Release, Release, 0), 4);
                if (r != null && r[0] == 0) Holding = false;
                Store.Log("PEP: released a SoC limit left by an earlier run (" + (g[1] / 1000.0).ToString("0.0") + " W), status " + (r == null ? "?" : r[0].ToString()));
            }
        }

        // Withdraws our request so the firmware (ASUS fan mode) owns the limit again. Safe to call any time.
        public static void ReleaseLimit(bool quiet = false)
        {
            lock (Lock)
            {
                if (!Ready()) return;
                var r = Call(0x8888214C, U32(_client, Soc, Pl1, Release, Release, 0), 4);
                if (r != null && r[0] == 0) { if (Holding && !quiet) Store.Log("PEP SoC limit released"); Holding = false; _releaseFailLogged = false; }
                else if (!_releaseFailLogged) { _releaseFailLogged = true; Store.Log("PEP release failed: status " + (r == null ? "ioctl failed" : r[0].ToString()) + " (retrying)"); }
            }
        }

        // On quit: final release, and no Set() afterwards even from a worker tick that is still running.
        public static void Close()
        {
            lock (Lock)
            {
                _closed = true;
                if (Holding) ReleaseLimit();
            }
        }
    }
}
