// Always run elevated (the SoC/GPU limits need admin) without a UAC prompt on every start.
// Two Task Scheduler tasks run Simmer with highest privileges as the current user:
//   "Simmer"          no trigger; a normal (non-elevated) start runs it and exits
//   "Simmer (logon)"  at logon, --hidden; replaces the Startup shortcut when "Start with Windows" is on
// The tasks never run the exe in this (user-writable) folder: anything running as the user could swap it and
// get admin on the next start. The first elevated run (UAC once) copies the exe to Program Files\Simmer
// (admins-only ACL) and points the tasks there; settings and logs stay in this folder (--data).
// A normal start whose exe differs from the installed copy (a new build) asks UAC once instead, and that
// elevated run installs itself. Both tasks allow starting on battery and have no time limit.
using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Forms;

namespace PowerCap
{
    static class Elevation
    {
        public const string Task = "Simmer", LogonTask = "Simmer (logon)";
        // As SYSTEM at boot, before login: only puts the saved charge limit back (--charge-boot; exits if none is set).
        public const string BootTask = "Simmer (boot)";
        const string OldTask = "PowerCap", OldLogonTask = "PowerCap (logon)";   // before the rename
        static string Exe { get { return Application.ExecutablePath; } }
        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Simmer"); } }
        static string Installed { get { return Path.Combine(InstallDir, "Simmer.exe"); } }
        // Written by the elevated install next to the installed exe: where its settings live when started without --data.
        public static string DataPathFile { get { return Path.Combine(InstallDir, "data.path"); } }

        static int Schtasks(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(10000); return p.ExitCode; }
            }
            catch { return -1; }
        }

        public static bool Exists(string name) { return Schtasks("/query /tn \"" + name + "\"") == 0; }

        static string Hash(string f)
        {
            try { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(s)); }
            catch { return null; }
        }
        static bool SameAsInstalled { get { var a = Hash(Exe); return a != null && a == Hash(Installed); } }

        // Non-elevated start: hand over to the elevated task (no prompt), else ask UAC once. True = this process should exit.
        public static bool Relaunch(bool hidden)
        {
            if (SameAsInstalled && Schtasks("/run /tn \"" + (hidden && Exists(LogonTask) ? LogonTask : Task) + "\"") == 0) return true;
            return RunAs(hidden ? "--hidden --from-task" : "--from-task", false) != int.MinValue;
        }

        // Starts this exe elevated (UAC). Returns its exit code when wait, 0 when not, int.MinValue when declined.
        public static int RunAs(string args, bool wait)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = true, Verb = "runas" }))
                {
                    if (!wait) return 0;
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return int.MinValue; }   // UAC declined: run without the SoC limits
        }

        // Elevated: copy this exe to the protected folder (when it is a different build) and (re)create the tasks.
        public static void Install()
        {
            if (!Pep.IsAdmin) return;
            try
            {
                Directory.CreateDirectory(InstallDir);   // inherits Program Files' ACL: users can read and run, not write
                if (!string.Equals(Path.GetFullPath(Exe), Path.GetFullPath(Installed), StringComparison.OrdinalIgnoreCase))
                {
                    if (!SameAsInstalled) { File.Copy(Exe, Installed, true); Store.Log("installed this build to " + Installed); }
                    // The drain driver (tools\BmdDrain), when built. Loaded only during a drain, so normally not in use.
                    var sys = Path.Combine(Path.GetDirectoryName(Exe), "SimmerBmdDrain.sys");
                    if (File.Exists(sys) && Hash(sys) != Hash(Drain.Sys))
                        try { File.Copy(sys, Drain.Sys, true); Store.Log("installed the drain driver"); } catch (Exception e) { Store.Log("drain driver not installed: " + e.Message); }
                    File.WriteAllText(DataPathFile, Store.Dir);
                }
            }
            catch (Exception e) { Store.Log("install to " + InstallDir + " failed: " + e.Message); return; }   // keep the old tasks
            if (Create(Task, false, "--from-task")) Store.Log("elevated start task ready");
            if (Exists(LogonTask) || Exists(OldLogonTask)) Create(LogonTask, true, "--hidden --from-task");
            Create(BootTask, false, "--charge-boot", true);
            // After the rename from PowerCap: drop the old tasks and the old installed copy.
            if (Exists(OldTask) || Exists(OldLogonTask))
            {
                Delete(OldTask); Delete(OldLogonTask);
                Store.Log("removed the old PowerCap tasks");
            }
            var oldDir = Path.Combine(Path.GetDirectoryName(InstallDir), "PowerCap");
            try { if (Directory.Exists(oldDir)) { Directory.Delete(oldDir, true); Store.Log("removed " + oldDir); } } catch { }
        }

        public static bool Create(string name, bool atLogon, string args, bool atBoot = false)
        {
            if (!File.Exists(Installed)) return false;
            args += " --data \"" + Store.Dir.TrimEnd('\\') + "\"";
            string user = WindowsIdentity.GetCurrent().Name;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "<RegistrationInfo><Description>" + (atBoot ? "Simmer, battery charge limit at boot" : "Simmer, elevated (SoC/GPU limits need admin)") + "</Description></RegistrationInfo>\n" +
                "<Triggers>" + (atLogon ? "<LogonTrigger><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger>" : atBoot ? "<BootTrigger><Delay>PT5S</Delay></BootTrigger>" : "") + "</Triggers>\n" +
                (atBoot
                    ? "<Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n"   // SYSTEM
                    : "<Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n") +
                "<Settings><MultipleInstancesPolicy>" + (atBoot ? "IgnoreNew" : "Parallel") + "</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>" + (atBoot ? "PT10M" : "PT0S") + "</ExecutionTimeLimit><AllowHardTerminate>" + (atBoot ? "true" : "false") + "</AllowHardTerminate><Priority>4</Priority></Settings>\n" +
                "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Installed) + "</Command><Arguments>" + SecurityElement.Escape(args) + "</Arguments>" +
                "<WorkingDirectory>" + SecurityElement.Escape(InstallDir) + "</WorkingDirectory></Exec></Actions>\n</Task>";
            // Written in the admins-only folder, so nothing unelevated can swap it before schtasks reads it.
            string f = Path.Combine(InstallDir, "task.xml");
            try
            {
                File.WriteAllText(f, xml, System.Text.Encoding.Unicode);
                int rc = Schtasks("/create /tn \"" + name + "\" /xml \"" + f + "\" /f");
                if (rc != 0) Store.Log("task " + name + " not created, schtasks " + rc);
                return rc == 0;
            }
            catch (Exception e) { Store.Log("task " + name + ": " + e.Message); return false; }
            finally { try { File.Delete(f); } catch { } }
        }

        // A normal process cannot signal the elevated one's events (integrity level), so --quit and --off leave these files.
        public static string QuitFlag { get { return Path.Combine(Store.Dir, "quit.request"); } }
        public static string OffFlag { get { return Path.Combine(Store.Dir, "off.request"); } }

        public static void Delete(string name) { Schtasks("/delete /tn \"" + name + "\" /f"); }
    }
}
