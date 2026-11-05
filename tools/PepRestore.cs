// Restores SoC PL1 on the Qualcomm PEP driver after a test, trying several Set variants until the
// read-back shows the target. Needs admin. Usage: peprestore.exe TARGET_MW out.txt
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class PepRestore
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, byte[] i, uint il, byte[] o, uint ol, out uint r, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    static readonly StringBuilder Out = new StringBuilder();
    static IntPtr _h; static uint _client;
    static void Log(string s) { Out.AppendLine(DateTime.Now.ToString("HH:mm:ss ") + s); }
    static uint[] Call(uint code, byte[] input, int outLen)
    {
        var o = new byte[outLen]; uint r;
        bool ok = DeviceIoControl(_h, code, input, (uint)input.Length, o, (uint)outLen, out r, IntPtr.Zero);
        var u = new uint[outLen / 4];
        for (int i = 0; i < u.Length; i++) u[i] = BitConverter.ToUInt32(o, i * 4);
        if (!ok) u[0] = 0xFFFFFFFE;
        return u;
    }
    static byte[] U32(params uint[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4); return b; }
    static uint[] Get(uint pl) { return Call(0x88882150, U32(_client, 1, pl), 44); }
    static string Fmt(uint[] g) { return string.Format("status {0} set {1} mW/{2} us, max {3} mW/{4} us, effective {5} mW/{6} us", g[0], g[3], g[4], g[5], g[6], g[9], g[10]); }

    static int Main(string[] a)
    {
        uint target = uint.Parse(a[0]);
        try
        {
            _h = CreateFile(@"\\.\QCOM_PEPDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (_h == new IntPtr(-1)) { Log("open failed " + Marshal.GetLastWin32Error()); return 1; }
            var v = Call(0x88882140, new byte[4], 8);
            var reg = new byte[36]; Encoding.Unicode.GetBytes("PCRestore").CopyTo(reg, 0); BitConverter.GetBytes(v[0] == 0 && v[1] != 0 ? v[1] : 2u).CopyTo(reg, 32);
            var c = Call(0x88882144, reg, 8); _client = c[1];
            Log("client 0x" + _client.ToString("X8") + " status " + c[0]);
            Log("PL1 now: " + Fmt(Get(0)));
            Log("PL2 now: " + Fmt(Get(1)));
            // Variants: explicit 15 s window; no-limit sentinel; default window sentinel; after a pause.
            var tries = new[] { new uint[] { target, 15000000 }, new uint[] { target, 28000000 }, new uint[] { 0xFFFFFFFF, 0xFFFFFFFF },
                                new uint[] { target, 0xFFFFFFFF }, new uint[] { 0xFFFFFFFF, 15000000 } };
            foreach (var t in tries)
            {
                var r = Call(0x8888214C, U32(_client, 1, 0, t[0], t[1], 0), 4);
                var g = Get(0);
                Log(string.Format("set {0} mW / {1}: status {2} -> {3}", t[0], t[1], r[0], Fmt(g)));
                if (g[9] >= target) { Log("restored"); break; }
                Thread.Sleep(1500);
            }
        }
        catch (Exception e) { Log("exception " + e.Message); }
        finally { if (_h != IntPtr.Zero) CloseHandle(_h); File.WriteAllText(a[1], Out.ToString()); }
        return 0;
    }
}
