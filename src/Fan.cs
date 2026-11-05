// Fan curve: PowerCap drives both fans from the chip temperature through the ASUS EC mailbox (same path as
// tools\AsusFanControl): root\wmi ATD_STS ec_write_command_byte / ec_read_command_byte, bank 1:
//   0x82 fan mode (0 = the EC's own curve, 2 = manual), read back with 0x02
//   0x8C select fan 0 or 1, 0x8A its PWM (0-255, 0 = stop), read back with 0x0A
// Measured on this laptop (both fans, cool): PWM 25 starts a stopped fan, 15 stalls a running one,
// 30 = 900 RPM, 60 = 2200, 120 = 4300, 255 = 7800.
// Manual mode replaces the EC's curve, so every way out hands it back (mode 0): curve off, quit, display off or
// sleep, the chip at 90 C, five ticks without a temperature, a mailbox error, and a watchdog process
// (Simmer.exe --fan-watchdog PID) for when this process is killed or its engine stops ticking.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Management;

namespace PowerCap
{
    class FanCurve
    {
        public bool On;
        public double[] T = { 50, 58, 66, 74, 82 };   // chip C, rising
        public double[] P = { 0, 20, 40, 65, 100 };   // fan %, never lower than the point before
        public const double MinT = 35, MaxT = 85, MinGap = 3, MinLastP = 50;
        public FanCurve Clone() { return new FanCurve { On = On, T = (double[])T.Clone(), P = (double[])P.Clone() }; }

        // Fan % at t: off below the first point, linear between points, the last value above the last point.
        public double At(double t)
        {
            if (t < T[0]) return 0;
            for (int i = 1; i < T.Length; i++)
                if (t < T[i]) return P[i - 1] + (P[i] - P[i - 1]) * (t - T[i - 1]) / (T[i] - T[i - 1]);
            return P[P.Length - 1];
        }

        // Any non-zero % runs at least PWM 30, the lowest speed that reliably starts a stopped fan.
        public static int Pwm(double pct) { return pct < 0.5 ? 0 : Math.Max(30, Math.Min(255, (int)Math.Round(pct * 2.55))); }

        // Measured PWM -> RPM (average of the two fans), for labels only.
        static readonly int[] PwmPts = { 0, 25, 30, 45, 60, 75, 90, 105, 120, 140, 160, 180, 200, 230, 255 };
        static readonly int[] RpmPts = { 0, 700, 900, 1500, 2200, 2700, 3250, 3725, 4200, 4700, 5300, 5850, 6275, 7050, 7600 };
        public static int Rpm(int pwm)
        {
            if (pwm <= 0) return 0;
            for (int i = 1; i < PwmPts.Length; i++)
                if (pwm <= PwmPts[i]) return RpmPts[i - 1] + (RpmPts[i] - RpmPts[i - 1]) * (pwm - PwmPts[i - 1]) / (PwmPts[i] - PwmPts[i - 1]);
            return RpmPts[RpmPts.Length - 1];
        }
        // Inverse of Rpm(): where a ramp starts when Simmer takes over fans the EC was already spinning.
        public static int PwmForRpm(int rpm)
        {
            if (rpm < RpmPts[1]) return 0;
            for (int i = 1; i < RpmPts.Length; i++)
                if (rpm <= RpmPts[i]) return PwmPts[i - 1] + (PwmPts[i] - PwmPts[i - 1]) * (rpm - RpmPts[i - 1]) / (RpmPts[i] - RpmPts[i - 1]);
            return 255;
        }

        // Keeps the shape safe whatever the file or the editor produced: temperatures inside 35-85 C and at
        // least 3 C apart, the fan never slower at a higher temperature, and at least 50 % at the last point.
        public void Fix()
        {
            int n = T.Length;
            for (int i = 0; i < n; i++)
            {
                T[i] = Math.Round(Math.Max(MinT + i * MinGap, Math.Min(MaxT - (n - 1 - i) * MinGap, T[i])));
                if (i > 0) T[i] = Math.Max(T[i], T[i - 1] + MinGap);
                P[i] = Math.Round(Math.Max(0, Math.Min(100, P[i])));
                if (i > 0) P[i] = Math.Max(P[i], P[i - 1]);
            }
            P[n - 1] = Math.Max(P[n - 1], MinLastP);
        }

        static string File_ { get { return Path.Combine(Store.Dir, "fan.curve"); } }

        // fan.curve: "on" or "off", then one "temperature:percent" line per point.
        public static FanCurve Load()
        {
            var c = new FanCurve();
            try
            {
                if (!File.Exists(File_)) return c;
                var lines = File.ReadAllLines(File_).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                var pts = lines.Skip(1).Select(x => x.Split(':')).ToArray();
                if (pts.Length >= 2 && pts.Length <= 8 && pts.All(x => x.Length == 2))
                {
                    c.T = pts.Select(x => double.Parse(x[0], CultureInfo.InvariantCulture)).ToArray();
                    c.P = pts.Select(x => double.Parse(x[1], CultureInfo.InvariantCulture)).ToArray();
                }
                c.On = lines.Length > 0 && lines[0] == "on";
                c.Fix();
            }
            catch { c = new FanCurve(); Store.Log("fan.curve unreadable: default curve, off"); }
            return c;
        }

        public void Save()
        {
            var s = (On ? "on" : "off") + Environment.NewLine + string.Join(Environment.NewLine, T.Select((t, i) => t.ToString(CultureInfo.InvariantCulture) + ":" + P[i].ToString(CultureInfo.InvariantCulture)));
            Store.WriteAtomic(File_, s);
        }

        public static void TurnOff() { var c = Load(); if (c.On) { c.On = false; c.Save(); } }
    }

    static class Fan
    {
        public const double HandBackC = 90, RetakeC = 80, Hyst = 3;
        static readonly object Lock = new object();
        static ManagementObject _ec;
        public static volatile bool Owned;   // this process put the EC in manual mode

        static ManagementObject Ec()
        {
            if (_ec == null)
                using (var c = new ManagementClass(@"root\wmi", "ATD_STS", null))
                    foreach (ManagementObject o in c.GetInstances()) { _ec = o; break; }
            if (_ec == null) throw new InvalidOperationException("ASUS ATD_STS provider not found");
            return _ec;
        }
        static int Read(byte feature)
        {
            lock (Lock)
            {
                try
                {
                    var e = Ec(); var i = e.GetMethodParameters("ec_read_command_byte");
                    i["commandcode"] = feature; i["param0"] = (byte)1;
                    using (var o = e.InvokeMethod("ec_read_command_byte", i, null)) return Convert.ToInt32(o["value"]);
                }
                catch { _ec = null; throw; }
            }
        }
        // ECCW(bank 1, feature, value): the WMI argument order is value, feature, bank.
        static void Write(byte feature, byte value)
        {
            lock (Lock)
            {
                int rc;
                try
                {
                    var e = Ec(); var i = e.GetMethodParameters("ec_write_command_byte");
                    i["commandcode"] = value; i["param0"] = feature; i["param1"] = (byte)1;
                    using (var o = e.InvokeMethod("ec_write_command_byte", i, null)) rc = Convert.ToInt32(o["value"]);
                }
                catch { _ec = null; throw; }
                if (rc != 0) throw new InvalidOperationException("mailbox write 0x" + feature.ToString("X2") + "=" + value + " refused (" + rc + ")");
            }
        }

        public static int Mode() { return Read(0x02); }

        public static void Manual()
        {
            lock (Lock)
            {
                Owned = true;   // before the write: a failure part-way still gets handed back
                Write(0x82, 2);
                if (Mode() != 2) throw new InvalidOperationException("manual fan mode not accepted");
            }
        }

        public static void SetPwm(int pwm)
        {
            lock (Lock)
                for (byte fan = 0; fan < 2; fan++)
                {
                    Write(0x8C, fan); Write(0x8A, (byte)pwm);
                    int rb = Read(0x0A);
                    if (rb != pwm) throw new InvalidOperationException("fan " + fan + " PWM readback " + rb + ", wanted " + pwm);
                }
        }

        // Hands the fans back to the EC's own curve (the ASUS fan mode). True when the EC confirms it.
        public static bool Automatic()
        {
            lock (Lock)
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { Write(0x82, 0); if (Mode() == 0) { Owned = false; return true; } }
                    catch (Exception e) { if (attempt == 2) Store.Log("fan: handing back to the EC failed: " + e.Message); }
                    System.Threading.Thread.Sleep(200);
                }
            return false;
        }

        // ---- heartbeat + watchdog ----
        const string BeatName = @"Local\PowerCapFanBeat";
        static MemoryMappedFile _beat;
        static MemoryMappedViewAccessor _beatView;
        static Process _watchdog;

        // Called every engine tick: the watchdog hands the fans back when this stops changing.
        public static void Beat()
        {
            try
            {
                if (_beatView == null) { _beat = MemoryMappedFile.CreateOrOpen(BeatName, 8); _beatView = _beat.CreateViewAccessor(0, 8); }
                _beatView.Write(0, DateTime.UtcNow.Ticks);
            }
            catch { }
        }

        public static void StartWatchdog()
        {
            if (_watchdog != null && !_watchdog.HasExited) return;
            Beat();
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            _watchdog = Process.Start(new ProcessStartInfo(exe, "--fan-watchdog " + Process.GetCurrentProcess().Id + " --data \"" + Store.Dir.TrimEnd('\\') + "\"") { UseShellExecute = false, CreateNoWindow = true });
        }

        // Simmer.exe --fan-watchdog PID: lives as long as that PowerCap. If it exits with the fans in manual mode
        // (killed, crashed), or its engine stops ticking for 10 s, the fans go back to the EC.
        public static int Watchdog(int pid)
        {
            Process owner;
            try { owner = Process.GetProcessById(pid); }
            catch { HandBackIfManual("Simmer was already gone"); return 0; }
            MemoryMappedViewAccessor view = null;
            try { view = MemoryMappedFile.OpenExisting(BeatName).CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read); } catch { }
            long last = 0; DateTime changed = DateTime.UtcNow; bool handed = false;
            while (!owner.WaitForExit(2000))
            {
                long b = 0;
                try { if (view == null) view = MemoryMappedFile.OpenExisting(BeatName).CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read); b = view.ReadInt64(0); } catch { }
                if (b != last) { last = b; changed = DateTime.UtcNow; handed = false; continue; }
                if (!handed && (DateTime.UtcNow - changed).TotalSeconds > 10) { handed = true; HandBackIfManual("Simmer stopped ticking"); }
            }
            HandBackIfManual("Simmer exited");
            return 0;
        }

        static void HandBackIfManual(string why)
        {
            try { if (Mode() == 0) return; } catch { }
            bool ok = Automatic();
            Store.Log("fan watchdog: " + why + " with the fans in manual mode; " + (ok ? "handed back to the EC" : "hand-back FAILED"));
        }
    }
}
