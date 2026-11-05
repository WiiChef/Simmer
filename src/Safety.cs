// ASUS mode / refresh rate helpers, and crash safety:
// every change is "pending" until it has run for 60 s (a clean quit also confirms it). Once per boot, if Windows
// restarted with a change still pending, or logged a crash / forced power-off (Kernel-Power 41) while the PowerCap plan was
// active, PowerCap reverts to Windows Balanced, ASUS Standard and the maximum refresh rate.
using System;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;

namespace PowerCap
{
    static class Hardware
    {
        public static readonly string[] AsusNames = { "whisper", "standard", "performance" };
        static uint AsusValue(string n) { return n == "whisper" ? 1u : n == "performance" ? 2u : 0u; }
        public static string AsusLabel(string n) { return n == "whisper" ? "Whisper" : n == "performance" ? "Performance" : "Standard"; }

        // The firmware can't report the mode. The ASUS hotkey service (Fn+F, MyASUS) records the one it set in
        // QuietFan (0 standard, 1 whisper, 2 performance; same values as DEVS 0x00110019). Users can only read it, and
        // PowerCap's own changes don't update it, so the engine follows changes of it rather than its value.
        const string HotkeyKey = @"SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys";
        public static string AsusHotkeyMode()
        {
            try
            {
                using (var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = hk.OpenSubKey(HotkeyKey))
                {
                    var v = k == null ? null : k.GetValue("QuietFan") as int?;
                    return v == 0 ? "standard" : v == 1 ? "whisper" : v == 2 ? "performance" : null;
                }
            }
            catch { return null; }
        }

        // The last mode PowerCap set is remembered in the profile.
        public static bool SetAsus(string name)
        {
            long rc = Native.AtkSet(0x00110019, AsusValue(name));
            if (rc != 1) Store.Log("ASUS mode " + name + " rejected, code " + rc);
            return rc == 1;
        }

        public static int[] Rates() { try { return Native.RefreshRates(); } catch { return new int[0]; } }
        public static int CurrentHz() { try { return Native.CurrentHz(); } catch { return 0; } }

        // Picks the lowest available rate >= want (0 = max). Returns the rate now in use.
        public static int SetRefresh(int want)
        {
            var rates = Rates();
            if (rates.Length == 0) return 0;
            int target = want <= 0 ? rates[rates.Length - 1] : rates.Where(r => r >= want).DefaultIfEmpty(rates[rates.Length - 1]).First();
            if (CurrentHz() == target) return target;
            int rc = Native.SetHz(target);
            if (rc != 0) Store.Log("refresh change to " + target + " Hz failed, code " + rc);
            return CurrentHz();
        }
    }

    static class Safety
    {
        public const int ConfirmSeconds = 60;
        public static int TestCrashLookbackDays;   // test only: --check --crash-lookback-days N

        public static void MarkPending(string what)
        {
            try { File.WriteAllText(Store.PendingFile, "{\"At\":\"" + DateTime.Now.ToString("o") + "\",\"What\":\"" + what.Replace("\"", "'") + "\"}"); } catch { }
        }
        public static void Confirm() { try { File.Delete(Store.PendingFile); } catch { } }
        public static bool IsPending { get { return File.Exists(Store.PendingFile); } }

        static DateTime BootTime()
        {
            using (var q = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                foreach (ManagementObject o in q.Get()) return ManagementDateTimeConverter.ToDateTime((string)o["LastBootUpTime"]);
            return DateTime.Now;
        }

        // Back to stock. Each step is independent so one failure can't block the others.
        public static void ResetToDefault()
        {
            try { Plan.ActivateBalanced(); } catch { }
            try { Hardware.SetAsus("standard"); } catch { }
            try { if (Pep.IsAdmin) Pep.ReleaseLimit(); } catch { }
            try { FanCurve.TurnOff(); if (Pep.IsAdmin && Fan.Mode() != 0) Fan.Automatic(); } catch { }
            try { Store.Save(Profile.ForPreset("default")); } catch { }
            Confirm();
            Store.Log("reset to Windows Balanced, ASUS Standard, fan curve off, max refresh");
        }

        // Runs once per boot. Returns the reason when it reverted, else null.
        public static string CheckOncePerBoot()
        {
            DateTime boot;
            try { boot = BootTime(); } catch { return null; }
            DateTime seen = DateTime.MinValue;
            try { seen = DateTime.Parse(File.ReadAllText(Store.BootFile).Trim()); } catch { }
            if (Math.Abs((boot - seen).TotalSeconds) < 5) return null;   // already checked this boot
            try { File.WriteAllText(Store.BootFile, boot.ToString("o")); } catch { }

            string reason = null;
            if (IsPending)
            {
                DateTime at = boot;
                try
                {
                    var s = File.ReadAllText(Store.PendingFile);
                    int i = s.IndexOf("\"At\":\"") + 6;
                    at = DateTime.Parse(s.Substring(i, s.IndexOf('"', i) - i));
                }
                catch { }
                if (at < boot) reason = "Windows restarted less than a minute after a Simmer change";
            }
            if (reason == null && Plan.IsActive && CrashSince(TestCrashLookbackDays > 0 ? DateTime.Now.AddDays(-TestCrashLookbackDays) : boot.AddMinutes(-5)))
                reason = "Windows crashed or lost power while Simmer limits were active";
            if (reason == null) return null;
            ResetToDefault();
            Store.Log(reason);
            return reason + ", so Simmer went back to Windows defaults.";
        }

        // Every start (after the boot check): a change still pending from this boot means the last PowerCap
        // process ended (crashed, was killed) within a minute of it; a clean quit confirms. Same revert as above.
        public static string CheckUnfinished()
        {
            if (!IsPending) return null;
            const string reason = "Simmer stopped less than a minute after a change";
            ResetToDefault();
            Store.Log(reason);
            return reason + ", so it went back to Windows defaults.";
        }

        static bool CrashSince(DateTime since)
        {
            try
            {
                string ms = ((long)(DateTime.Now - since).TotalMilliseconds).ToString();
                var q = new EventLogQuery("System", PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]");
                using (var r = new EventLogReader(q)) using (var e = r.ReadEvent()) return e != null;
            }
            catch { return false; }
        }
    }
}

