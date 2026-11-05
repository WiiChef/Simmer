// Scripted Qualcomm PEP driver session for diagnostics. Needs admin.
// Usage: pepcmd.exe out.txt cmd [cmd ...]   cmds: asus:MODE | ver | reg:NAME | unreg:HEXID | use:HEXID | get:DOM:PL | set:DOM:PL:MW:WINDOW | sleep:MS
// Every call logs the raw output words. MW / WINDOW accept "none" for 0xFFFFFFFF.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class PepCmd
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, byte[] i, uint il, byte[] o, uint ol, out uint r, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    static readonly StringBuilder Out = new StringBuilder();
    static IntPtr _h; static uint _client, _ver = 2;
    static void Log(string s) { Out.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff ") + s); }
    static uint[] Call(string what, uint code, byte[] input, int outLen)
    {
        var o = new byte[outLen];
        for (int i = 0; i < outLen; i++) o[i] = 0xCC;   // shows which words the driver wrote
        uint r;
        bool ok = DeviceIoControl(_h, code, input, (uint)input.Length, o, (uint)outLen, out r, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        var u = new uint[outLen / 4];
        for (int i = 0; i < u.Length; i++) u[i] = BitConverter.ToUInt32(o, i * 4);
        Log(string.Format("{0}: ok={1} err={2} bytes={3} out=[{4}]", what, ok, ok ? 0 : err, r, string.Join(" ", Array.ConvertAll(u, x => x == 0xCCCCCCCC ? "--" : x.ToString()))));
        return u;
    }
    static byte[] U32(params uint[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4); return b; }
    static long AsusSet(uint v)
    {
        var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h == new IntPtr(-1)) return -1;
        var i = U32(0x53564544, 8, 0x00110019, v); var o = new byte[16]; uint r;
        bool ok = DeviceIoControl(h, 0x0022240C, i, 16, o, 16, out r, IntPtr.Zero); CloseHandle(h);
        return ok ? BitConverter.ToUInt32(o, 0) : -1;
    }
    static uint Num(string s) { return s == "none" ? 0xFFFFFFFF : uint.Parse(s); }

    static int Main(string[] a)
    {
        try
        {
            _h = CreateFile(@"\\.\QCOM_PEPDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (_h == new IntPtr(-1)) { Log("open failed " + Marshal.GetLastWin32Error()); return 1; }
            for (int k = 1; k < a.Length; k++)
            {
                var p = a[k].Split(':');
                switch (p[0])
                {
                    case "ver": { var v = Call("ver", 0x88882140, new byte[4], 8); if (v[0] == 0 && v[1] != 0 && v[1] != 0xCCCCCCCC) _ver = v[1]; break; }
                    case "reg":
                        {
                            var reg = new byte[36]; Encoding.Unicode.GetBytes(p[1]).CopyTo(reg, 0); BitConverter.GetBytes(_ver).CopyTo(reg, 32);
                            var c = Call("reg " + p[1], 0x88882144, reg, 8);
                            if (c[0] == 0) { _client = c[1]; Log("client = 0x" + _client.ToString("X8")); }
                            break;
                        }
                    case "use": _client = Convert.ToUInt32(p[1], 16); Log("using client 0x" + _client.ToString("X8")); break;
                    case "unregx": { var inb = new byte[int.Parse(p[2])]; BitConverter.GetBytes(Convert.ToUInt32(p[1], 16)).CopyTo(inb, 0); Call("unregx " + a[k], 0x88882148, inb, int.Parse(p[3])); break; }
                    case "unreg": Call("unreg " + p[1], 0x88882148, U32(Convert.ToUInt32(p[1], 16)), 4); break;
                    case "get": Call("get " + p[1] + ":" + p[2], 0x88882150, U32(_client, uint.Parse(p[1]), uint.Parse(p[2])), 44); break;
                    case "set": Call("set " + string.Join(":", p, 1, p.Length - 1), 0x8888214C, U32(_client, uint.Parse(p[1]), uint.Parse(p[2]), Num(p[3]), Num(p[4]), 0), 4); break;
                    case "sleep": Thread.Sleep(int.Parse(p[1])); break;
                    case "asus": Log("asus mode " + p[1] + ": rc " + AsusSet(uint.Parse(p[1]))); break;   // 1 whisper, 0 standard, 2 performance
                }
            }
        }
        catch (Exception e) { Log("exception " + e.Message); }
        finally { if (_h != IntPtr.Zero) CloseHandle(_h); File.WriteAllText(a[0], Out.ToString()); }
        return 0;
    }
}
