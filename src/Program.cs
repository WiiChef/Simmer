// Entry point.
//   Simmer.exe                 open the window (or bring the running one forward)
//   Simmer.exe --hidden        start in the tray (Startup shortcut)
//   Simmer.exe --quit          ask the running instance to exit cleanly
//   Simmer.exe --elevated      restart as administrator (waits for the old instance to exit)
//   A normal start hands over to the elevated "PowerCap" task (see Elevation.cs); --from-task marks that run.
//   Simmer.exe --off           back to Windows Balanced / ASUS Standard, SoC limit released, fan curve off, then exit
//   Simmer.exe --charge-boot     the "Simmer (boot)" task, as SYSTEM before login: fans back to the EC, saved charge limit back
//   Simmer.exe --fan-watchdog PID   started by the fan curve: hands the fans back to the EC if PID dies or hangs (Fan.cs)
//   Simmer.exe --data DIR      settings/log folder (the elevated tasks run the Program Files copy)
//   Simmer.exe --check         run the once-per-boot crash check, then exit
//   Simmer.exe --selftest W THREADS SECONDS CSV   hold W CPU watts under a THREADS-thread load, log to CSV
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PowerCap
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // per-monitor v2
            var a = args.Select(x => x.ToLowerInvariant()).ToArray();
            if (a.Contains("--quit"))
            {
                try { using (var q = EventWaitHandle.OpenExisting(@"Local\PowerCapV2.Quit")) q.Set(); return 0; }
                catch (UnauthorizedAccessException) { try { File.WriteAllText(Elevation.QuitFlag, ""); return 0; } catch { return 1; } }   // elevated instance
                catch { return 1; }
            }
            if (a.Length >= 2 && a[0] == "--fan-watchdog") return Fan.Watchdog(int.Parse(a[1]));
            if (a.Contains("--off")) return Off();
            if (a.Contains("--charge-boot")) return ChargeBoot();
            if (a.Contains("--drain-test")) return DrainTest();
            if (a.Contains("--check")) { int k = Array.IndexOf(a, "--crash-lookback-days"); if (k >= 0 && k + 1 < a.Length) Safety.TestCrashLookbackDays = int.Parse(a[k + 1]); var r = Safety.CheckOncePerBoot(); Store.Log("check: " + (r ?? "ok")); return 0; }
            if (a.Length >= 5 && a[0] == "--selftest") return SelfTest(double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(a[2]), int.Parse(a[3]), args[4]);

#if !SELFTEST
            if (!Pep.IsAdmin && !a.Contains("--from-task") && !a.Contains("--elevated") && Elevation.Relaunch(a.Contains("--hidden"))) return 0;
            bool created;
            Mutex mutex; EventWaitHandle show;
            // An elevated instance's objects carry a high integrity label: a normal start cannot open them
            // (access denied), and the elevated one is already running, so just leave.
            try { mutex = new Mutex(true, @"Local\PowerCapV2", out created); show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PowerCapV2.Show"); }
            catch (UnauthorizedAccessException) { return 0; }
            using (mutex)
            using (show)
            {
                // --elevated: started by "Restart as administrator"; the old instance is still quitting.
                if (!created && a.Contains("--elevated"))
                    try { created = mutex.WaitOne(10000); } catch (AbandonedMutexException) { created = true; }
                if (!created) { show.Set(); return 0; }
                // A SoC limit held in the driver and manual fan mode both outlive the process, so give them back on any exit.
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { if (Pep.Holding) Pep.ReleaseLimit(); } catch { } try { if (Fan.Owned) Fan.Automatic(); } catch { } };
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { if (Pep.Holding) Pep.ReleaseLimit(); } catch { } try { if (Fan.Owned) Fan.Automatic(); } catch { } };
                // A UI-thread error goes to the log (stack included) instead of .NET's Continue/Quit dialog.
                Application.ThreadException += (s, e) => Store.Log("UI error: " + e.Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { File.Delete(Elevation.QuitFlag); } catch { }
                try { File.Delete(Elevation.OffFlag); } catch { }
                string notice = Safety.CheckOncePerBoot() ?? Safety.CheckUnfinished();
                // After a revert, start from the safe profile: the startup slot may hold what caused it.
                var app = new TrayApp(notice != null ? Profile.ForPreset("default") : Store.LoadStartup(), !a.Contains("--hidden"), notice, show);
                Application.Run(app);
                GC.KeepAlive(mutex);
            }
#endif
            return 0;
        }

        // --off. A running instance would re-apply its profile (and only it can release its SoC request), so it is
        // asked through a flag file and resets itself; without one, reset here (elevated, for the SoC limit).
        static int Off()
        {
            bool running;
            try { using (Mutex.OpenExisting(@"Local\PowerCapV2")) running = true; }
            catch (UnauthorizedAccessException) { running = true; }   // elevated instance
            catch { running = false; }
            if (running)
            {
                try { File.WriteAllText(Elevation.OffFlag, ""); } catch { return 1; }
                for (int i = 0; i < 50 && File.Exists(Elevation.OffFlag); i++) Thread.Sleep(100);
                if (!File.Exists(Elevation.OffFlag)) return 0;
                try { File.Delete(Elevation.OffFlag); } catch { }
                return 1;
            }
            if (!Pep.IsAdmin) { int rc = Elevation.RunAs("--off", true); return rc == int.MinValue ? 1 : rc; }
            Safety.ResetToDefault();
            return 0;
        }

        // --drain-test (elevated): a 20 s drain through the installed driver, to check it works without waiting to be
        // above the charge limit.
        static int DrainTest()
        {
            string skip = Drain.Unavailable();
            if (skip != null) { Store.Log("drain test: " + skip); return 1; }
            try
            {
                Drain.Start(); Store.Log("drain test: started");
                for (int i = 0; i < 4; i++) { Thread.Sleep(5000); if (!Drain.Beat()) { Store.Log("drain test: kernel ended it"); break; } }
                int pct; bool ac; double full, dis; Battery.Read(out pct, out ac, out full, out dis);
                Store.Log("drain test: during the drain " + pct + " %, AC " + (ac ? "online" : "offline") + ", discharging " + dis.ToString("0.0") + " W");
            }
            catch (Exception e) { Store.Log("drain test: " + e.Message); }
            string p = Drain.Stop();
            Store.Log("drain test: " + (p == null ? "stopped, normal charging confirmed, driver unloaded" : p));
            return p == null ? 0 : 1;
        }

        // --charge-boot: run at boot by the "Simmer (boot)" task as SYSTEM, before anyone logs in. Puts the saved charge
        // limit into the firmware and keeps checking for 3 minutes, since the ASUS service may rewrite it after boot.
        static int ChargeBoot()
        {
            // Backstop for the fans: a restart can kill Simmer while it drives them, leaving the EC in manual mode until
            // the next login. Hand them back now, unless a Simmer app is already up and driving them.
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("Simmer").Length <= 1 && Fan.Mode() == 2)
                { Fan.Automatic(); Store.Log("boot: fans were left in manual mode, handed back to the EC"); }
            }
            catch (Exception e) { Store.Log("boot: fan check " + e.Message); }

            int want = Engine.LoadCharge();
            if (want == 0) return 0;   // limit left to ASUS
            // The battery driver may not answer yet this early in boot: logs the first answer, and if none ever came.
            bool read = false; string why = null;
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    string err; int fw = Charge.Read(out err);
                    if (err != null && err != why) { why = err; Store.Log("boot: charge limit read failed " + (i * 15) + " s in: " + err); }
                    if (fw > 0 && fw != want) { Charge.Set(want); Store.Log("boot: charge limit firmware had " + fw + " %, set " + want + " %"); }
                    else if (fw == want && !read) Store.Log("boot: charge limit already " + want + " % (" + (i * 15) + " s in)");
                    if (fw > 0) read = true;
                }
                catch (Exception e) { Store.Log("boot: charge limit " + e.Message); }
                Thread.Sleep(15000);
            }
            if (!read) Store.Log("boot: charge limit unreadable for 3 minutes");
            return 0;
        }

        // Headless test of the watt loop: same engine as the app, with a synthetic load.
        static volatile bool _burn = true;
        static int SelfTest(double watts, int threads, int seconds, string csv)
        {
            var p = Profile.ForPreset("balanced"); p.Preset = "custom"; p.TargetW = watts; p.Hz = Hardware.CurrentHz(); p.Asus = "standard"; p.EffOnly = Environment.GetEnvironmentVariable("POWERCAP_EFFONLY") == "1";
            var e = new Engine(p);
            var w = new StreamWriter(csv);
            w.WriteLine("t,target,cpu,c0w,c1w,c2w,soc,system,commanded,perfMhz,primeMhz");
            e.SetProfile(p, false);
            e.Start();
            Thread.Sleep(3000);
            var ts = Enumerable.Range(0, threads).Select(i => new Thread(() => { double x = 1.0001; while (_burn) { x = x * 1.0000001 + 0.0000001; if (x > 2) x = 1.0001; } }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
            ts.ForEach(t => t.Start());
            try
            {
                for (int t = 0; t < seconds; t++)
                {
                    Thread.Sleep(1000);
                    var s = e.Snapshot;
                    var c = s.Applied ?? new Caps();
                    w.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2:F2},{3:F2},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9},{10}",
                        t, watts, s.R.Cpu, s.R.Cluster[0], s.R.Cluster[1], s.R.Cluster[2], s.R.Soc, s.R.System, s.Commanded, c.Mhz[0], c.Mhz[1]));
                    w.Flush();
                }
            }
            finally
            {
                _burn = false;
                e.Shutdown();
                Safety.ResetToDefault();
                w.Close();
            }
            return 0;
        }
    }
}


