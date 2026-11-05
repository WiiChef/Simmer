// One reversible SoC power-limit write test on the Qualcomm PEP driver. Needs admin.
// Reads SoC PL1, samples power for BASE s, sets PL1 to MW for HOLD s, then always writes the original
// value back (finally block + console-close handler) and reads it again.
// Usage: peptest.exe MW HOLD BASE out.txt
// Set layout (from DeckPowerControl.exe): 24 bytes {client, domain, pl, mW (0xFFFFFFFF = no limit), window 0xFFFFFFFF, 0}.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class PepTest
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, byte[] i, uint il, byte[] o, uint ol, out uint r, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    delegate bool CtrlHandler(int type);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(CtrlHandler h, bool add);

    const uint Domain = 1, Pl = 0;   // SoC, PL1 (sustained)
    static readonly StringBuilder Out = new StringBuilder();
    static string _file;
    static IntPtr _h; static uint _client, _origMw; static bool _changed;
    static CtrlHandler _ctrl;

    static void Log(string s) { s = DateTime.Now.ToString("HH:mm:ss ") + s; Out.AppendLine(s); Console.WriteLine(s); try { if (_file != null) File.WriteAllText(_file, Out.ToString()); } catch { } }

    static uint[] Call(uint code, byte[] input, int outLen, out bool ok)
    {
        var o = new byte[outLen]; uint r;
        ok = DeviceIoControl(_h, code, input, (uint)input.Length, o, (uint)outLen, out r, IntPtr.Zero);
        var u = new uint[outLen / 4];
        for (int i = 0; i < u.Length; i++) u[i] = BitConverter.ToUInt32(o, i * 4);
        return u;
    }
    static byte[] U32(params uint[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4); return b; }

    // out: status, domain, pl, cur mW, cur window us, max mW, max window, ?, ?, effective mW, effective window
    static uint[] Get() { bool ok; var g = Call(0x88882150, U32(_client, Domain, Pl), 44, out ok); return ok ? g : null; }
    static string Fmt(uint[] g) { return g == null ? "read failed" : string.Format("status {0} set {1} mW/{2} us, max {3} mW, effective {4} mW/{5} us", g[0], g[3], g[4], g[5], g[9], g[10]); }
    static uint Set(uint mw) { bool ok; var r = Call(0x8888214C, U32(_client, Domain, Pl, mw, 0xFFFFFFFF, 0), 4, out ok); return ok ? r[0] : 0xFFFFFFFE; }

    static void Restore()
    {
        if (!_changed) return;
        uint st = Set(_origMw);
        Log("restore to " + _origMw + " mW: status " + st + " -> " + Fmt(Get()));
        if (st == 0) _changed = false;
    }

    static int Main(string[] a)
    {
        uint mw = uint.Parse(a[0]); int hold = int.Parse(a[1]), bas = int.Parse(a[2]); _file = a[3];
        _ctrl = t => { Restore(); return false; };
        SetConsoleCtrlHandler(_ctrl, true);
        try { Run(mw, hold, bas); }
        catch (Exception e) { Log("exception: " + e.Message); }
        finally { Restore(); if (_h != IntPtr.Zero) CloseHandle(_h); }
        return 0;
    }

    static void Run(uint mw, int hold, int bas)
    {
        _h = CreateFile(@"\\.\QCOM_PEPDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (_h == new IntPtr(-1)) { _h = IntPtr.Zero; Log("open failed, err " + Marshal.GetLastWin32Error()); return; }
        bool ok;
        var v = Call(0x88882140, new byte[4], 8, out ok);
        uint ver = ok && v[0] == 0 && v[1] != 0 ? v[1] : 2;
        var reg = new byte[36]; Encoding.Unicode.GetBytes("PowerCapTest").CopyTo(reg, 0); BitConverter.GetBytes(ver).CopyTo(reg, 32);
        var c = Call(0x88882144, reg, 8, out ok);
        if (!ok || c[0] != 0) { Log("register failed"); return; }
        _client = c[1];
        var before = Get();
        Log("before: " + Fmt(before));
        if (before == null || before[0] != 0 || before[3] == 0) { Log("no current PL1 to restore to; not writing"); return; }
        _origMw = before[3];

        var gpu = new PerformanceCounter("Energy Meter", "Power", "GPU"); var soc = new PerformanceCounter("Energy Meter", "Power", "SOC");
        var cpu = new[] { "CPU_CLUSTER_0", "CPU_CLUSTER_1", "CPU_CLUSTER_2" }; var cc = Array.ConvertAll(cpu, n => new PerformanceCounter("Energy Meter", "Power", n));
        gpu.NextValue(); soc.NextValue(); foreach (var x in cc) x.NextValue();
        Action<string, int> sample = (phase, secs) =>
        {
            double sg = 0, ss = 0, sc = 0; int n = 0;
            for (int i = 0; i < secs; i++)
            {
                Thread.Sleep(1000);
                double g = gpu.NextValue() / 1000, s = soc.NextValue() / 1000, cp = 0; foreach (var x in cc) cp += x.NextValue() / 1000;
                Log(string.Format("{0,-8} t={1,3}  SoC {2,6:0.00} W  GPU {3,6:0.00} W  CPU {4,6:0.00} W", phase, i + 1, s, g, cp));
                if (i >= secs / 2) { sg += g; ss += s; sc += cp; n++; }
            }
            Log(string.Format("{0} second-half mean: SoC {1:0.00} W, GPU {2:0.00} W, CPU {3:0.00} W", phase, ss / n, sg / n, sc / n));
        };

        sample("base", bas);
        _changed = true;
        uint st = Set(mw);
        Log("set PL1 " + mw + " mW: status " + st + " -> " + Fmt(Get()));
        if (st != 0) { Log("set rejected"); return; }
        sample("limited", hold);
        Restore();
        sample("after", bas);
    }
}
