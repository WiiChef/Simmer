// Battery charge limit through the Qualcomm battery driver that is already installed (\\.\QCOMBATTMGR, OEM property
// 0x00040108), the same command ASUS's own service sends. Payload 03 AA <percent> 02 (02 = ASUS's restart margin);
// "full capacity" is 02 AA 64 01. Checked on UX3607OA / qcpmicglink8480 1.0.0.11414: 50 and 60 % stop charging at
// 49 / 59 % in Windows; a limit under the current charge stops charging but never discharges. Every set is read
// back from the firmware and the previous value is restored if it does not match. Needs administrator.
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerCap
{
    static class Charge
    {
        public const int Min = 50, Max = 100;
        const uint Prop = 0x00040108, IoRead = 0x80332050, IoWrite = 0x80332054;
        static readonly object Lock = new object();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, uint inLen, byte[] output, uint outLen, out uint returned, IntPtr ov);

        static byte[] Transfer(bool write, byte[] cmd)
        {
            var input = new byte[write ? 1032 : 8];
            Buffer.BlockCopy(BitConverter.GetBytes(Prop), 0, input, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(4u), 0, input, 4, 4);
            if (write) Buffer.BlockCopy(cmd, 0, input, 8, 4);
            using (var h = CreateFile(@"\\.\QCOMBATTMGR", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new InvalidOperationException("battery driver not available (error " + Marshal.GetLastWin32Error() + ")");
                var o = new byte[1024]; uint n;
                if (!DeviceIoControl(h, write ? IoWrite : IoRead, input, (uint)input.Length, o, (uint)o.Length, out n, IntPtr.Zero))
                    throw new InvalidOperationException("battery driver refused the request (error " + Marshal.GetLastWin32Error() + ")");
                Array.Resize(ref o, (int)Math.Min(n, (uint)o.Length));
                return o;
            }
        }

        static byte[] Command(int pct) { return pct >= 100 ? new byte[] { 2, 0xAA, 100, 1 } : new byte[] { 3, 0xAA, (byte)pct, 2 }; }

        static byte[] ReadRaw()
        {
            var d = Transfer(false, null);
            // Only the formats ASUS uses: 03 AA <pct> 02 (a limit), 02 AA 64 01 (full capacity) and 02 AA 64 00 (MyASUS
            // battery care turned off). Anything else means another firmware, so stay out of it.
            if (d.Length != 4 || d[1] != 0xAA || d[2] < Min || d[2] > Max || !((d[0] == 3 && d[3] == 2) || (d[0] == 2 && d[2] == 100 && d[3] <= 1)))
                throw new InvalidOperationException("unrecognised charge-limit reply " + BitConverter.ToString(d));
            return d;
        }

        // The firmware's current limit in %, or -1 when it can't be read (why goes to error).
        public static int Read() { string e; return Read(out e); }
        public static int Read(out string error)
        {
            error = null;
            try { lock (Lock) return ReadRaw()[2]; }
            catch (Exception e) { error = e.Message; return -1; }
        }

        // Sets the limit and confirms it from the firmware; on a mismatch puts the previous value back and throws.
        public static void Set(int pct)
        {
            if (pct < Min || pct > Max) throw new ArgumentOutOfRangeException("pct", "charge limit must be " + Min + "-" + Max + " %");
            lock (Lock)
            {
                var before = ReadRaw();
                var want = Command(pct);
                try
                {
                    Transfer(true, want);
                    System.Threading.Thread.Sleep(250);
                    if (BitConverter.ToString(ReadRaw()) != BitConverter.ToString(want)) throw new InvalidOperationException("the firmware did not take " + pct + " %");
                }
                catch (Exception e)
                {
                    string back;
                    try
                    {
                        Transfer(true, before);
                        System.Threading.Thread.Sleep(250);
                        var now = ReadRaw();
                        back = BitConverter.ToString(now) == BitConverter.ToString(before) ? "previous limit " + before[2] + " % restored" : "firmware now reports " + now[2] + " %";
                    }
                    catch { back = "could not restore the previous " + before[2] + " % (firmware state unknown)"; }
                    throw new InvalidOperationException(e.Message + "; " + back);
                }
            }
        }
    }
}
