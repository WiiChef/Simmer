// Read-only probe of the Qualcomm PEP driver power limits (\\.\QCOM_PEPDriver). Needs admin.
// Protocol taken from DeckPowerControl.exe: version 0x88882140, register 0x88882144, get limit 0x88882150.
// Never calls set (0x8888214C). Usage: pepprobe.exe [out.txt]
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class PepProbe
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, byte[] i, uint il, byte[] o, uint ol, out uint r, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static readonly StringBuilder Out = new StringBuilder();
    static void Log(string s) { Out.AppendLine(s); Console.WriteLine(s); }

    static uint[] Call(IntPtr h, uint code, byte[] input, int outLen, out bool ok, out int err)
    {
        var o = new byte[outLen]; uint r;
        ok = DeviceIoControl(h, code, input, (uint)input.Length, o, (uint)outLen, out r, IntPtr.Zero);
        err = Marshal.GetLastWin32Error();
        var u = new uint[outLen / 4];
        for (int i = 0; i < u.Length; i++) u[i] = BitConverter.ToUInt32(o, i * 4);
        return u;
    }

    static int Main(string[] a)
    {
        try { Run(); } catch (Exception e) { Log("exception: " + e.Message); }
        if (a.Length > 0) File.WriteAllText(a[0], Out.ToString());
        return 0;
    }

    static void Run()
    {
        var h = CreateFile(@"\\.\QCOM_PEPDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h == new IntPtr(-1)) { Log("open failed, err " + Marshal.GetLastWin32Error()); return; }
        try
        {
            bool ok; int err;
            var v = Call(h, 0x88882140, new byte[4], 8, out ok, out err);
            Log(string.Format("version: ok={0} err={1} status={2} version={3}", ok, err, v[0], v[1]));
            uint ver = v[0] == 0 && v[1] != 0 ? v[1] : 2;

            var reg = new byte[36];
            Encoding.Unicode.GetBytes("PowerCapProbe").CopyTo(reg, 0);
            BitConverter.GetBytes(ver).CopyTo(reg, 32);
            var c = Call(h, 0x88882144, reg, 8, out ok, out err);
            Log(string.Format("register: ok={0} err={1} status={2} client=0x{3:X8}", ok, err, c[0], c[1]));
            if (!ok || c[0] != 0) return;
            uint client = c[1];

            Log("get limit (out = 11 x u32: status, en, ?, cur mW, min mW, max mW, step, tw min, tw max, ?, ?)");
            for (uint dom = 0; dom < 3; dom++)
                for (uint pl = 0; pl < 4; pl++)
                {
                    var q = new byte[12];
                    BitConverter.GetBytes(client).CopyTo(q, 0); BitConverter.GetBytes(dom).CopyTo(q, 4); BitConverter.GetBytes(pl).CopyTo(q, 8);
                    var g = Call(h, 0x88882150, q, 44, out ok, out err);
                    Log(string.Format("dom {0} pl {1}: ok={2} err={3} raw=[{4}]", dom, pl, ok, err, string.Join(" ", Array.ConvertAll(g, x => x.ToString()))));
                }
        }
        finally { CloseHandle(h); }
    }
}
