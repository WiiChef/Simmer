// What the user picked (simmer.json) and the built-in presets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace PowerCap
{
    enum Mode { Default, Target, Manual }

    class Profile
    {
        public string Preset = "default";   // eco | balanced | perf | default | custom
        public Mode Mode = Mode.Default;
        public double TargetW = 10;         // CPU watts (Target mode)
        public int PerfMhz, PrimeMhz;       // Manual mode caps, 0 = no cap
        public int Epp = 50;
        public int Boost = 2;               // 0 off, 3 efficient, 2 aggressive
        public int Hz;                      // 0 = max
        public string Asus = "standard";    // whisper | standard | performance
        public bool EffOnly;                // run everything on the 6 efficient cores (scheduling policy)
        public int CpuShare;                // simple view split bar: CPU % of the SoC budget (CPU target), 0 = not used
        public double SocW;                 // SoC (CPU + GPU) power limit in W through the PEP driver, 0 = off
        public double GpuW;                 // GPU watt target, held through the SoC limit, 0 = off
        public Profile Clone() { return (Profile)MemberwiseClone(); }

        // Handheld-style ladder: CPU watt targets, plus Turbo (no caps) and plain Windows.
        public static readonly string[] PresetOrder = { "saver", "eco", "balanced", "perf", "turbo", "default" };
        // Presets set the CPU side only; ASUS fan mode and turbo boost are the user's separate choices (see Keeping).
        public static Profile ForPreset(string name)
        {
            switch (name)
            {
                case "saver": return new Profile { Preset = name, Mode = Mode.Target, TargetW = 4.5, Epp = 90, Boost = 0, Hz = 0 };
                case "eco": return new Profile { Preset = name, Mode = Mode.Target, TargetW = 7, Epp = 75, Boost = 0, Hz = 0 };
                case "balanced": return new Profile { Preset = name, Mode = Mode.Target, TargetW = 11, Epp = 50, Boost = 3, Hz = 0 };
                case "perf": return new Profile { Preset = name, Mode = Mode.Target, TargetW = 18, Epp = 35, Boost = 2, Hz = 0 };
                case "turbo": return new Profile { Preset = name, Mode = Mode.Manual, PerfMhz = 0, PrimeMhz = 0, Epp = 15, Boost = 2, Hz = 0 };
                default: return new Profile { Preset = "default", Mode = Mode.Default, Epp = 50, Boost = 2, Hz = 0, Asus = "standard" };
            }
        }
        // A preset sets the CPU target itself, so the split stops driving it (CpuShare 0).
        public static Profile Keeping(string name, Profile current) { var p = ForPreset(name); p.Asus = current.Asus; if (name != "default") { p.Boost = current.Boost; p.EffOnly = current.EffOnly; p.SocW = current.SocW; p.GpuW = current.GpuW; } return p; }
        public static string Summary(Profile p)
        {
            string cpu = p.Mode == Mode.Target ? p.TargetW.ToString("0.#") + " W"
                : p.Mode == Mode.Manual ? (p.PerfMhz == 0 && p.PrimeMhz == 0 ? "no limit" : "fixed clocks") : "Windows";
            return cpu + (p.EffOnly ? " · efficient only" : "") + (p.SocW > 0 ? " · SoC " + p.SocW.ToString("0") + " W" : "") + (p.GpuW > 0 ? " · GPU " + p.GpuW.ToString("0.#") + " W" : "") + " · " + Hardware.AsusLabel(p.Asus);
        }
        public static string PresetLabel(string p)
        {
            if (p != null && p.StartsWith("slot")) return "Profile " + p.Substring(4);
            switch (p) { case "saver": return "Max saver"; case "eco": return "Eco"; case "balanced": return "Balanced"; case "perf": return "Performance"; case "turbo": return "Turbo"; case "default": return "Windows default"; default: return "Custom"; }
        }
    }

    static class Store
    {
        // Settings and logs: --data DIR (the elevated tasks run the Program Files copy, see Elevation.cs), else the
        // folder the install recorded when this is that copy, else the exe's folder.
        public static readonly string Dir = DataDir();
        static string DataDir()
        {
            string b = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                var a = Environment.GetCommandLineArgs();
                int i = Array.FindIndex(a, x => string.Equals(x, "--data", StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i + 1 < a.Length && Directory.Exists(a[i + 1])) return a[i + 1];
                if (string.Equals(b.TrimEnd('\\'), Elevation.InstallDir, StringComparison.OrdinalIgnoreCase) && File.Exists(Elevation.DataPathFile))
                {
                    string d = File.ReadAllText(Elevation.DataPathFile).Trim();
                    if (Directory.Exists(d)) return d;
                }
            }
            catch { }
            return b;
        }
        // Files from before the rename (powercap.*) move to simmer.* once.
        static string File_(string name)
        {
            string f = Path.Combine(Dir, "simmer" + name), old = Path.Combine(Dir, "powercap" + name);
            try { if (!File.Exists(f) && File.Exists(old)) File.Move(old, f); } catch { }
            return f;
        }
        public static readonly string SettingsFile = File_(".json");
        public static readonly string PendingFile = File_(".pending.json");
        public static readonly string BootFile = File_(".lastboot");
        public static readonly string LogFile = File_(".log");
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        // Files are user-editable: anything outside what the UI can produce is clamped or rejected here,
        // so a bad value never reaches the UI or the hardware.
        static readonly string[] KnownPresets = { "saver", "eco", "balanced", "perf", "turbo", "default", "custom", "slot1", "slot2", "slot3" };
        static double Fin(object o, double lo, double hi) { double v = Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormatException("not a number"); return Math.Max(lo, Math.Min(hi, v)); }
        static int Int(object o, int lo, int hi) { return (int)Fin(o, lo, hi); }
        static Profile FromDict(Dictionary<string, object> d)
        {
            var p = new Profile();
            p.Preset = (string)d["Preset"];
            if (Array.IndexOf(KnownPresets, p.Preset) < 0) p.Preset = "custom";
            Mode m;
            if (!Enum.TryParse((string)d["Mode"], false, out m) || !Enum.IsDefined(typeof(Mode), m) || char.IsDigit(((string)d["Mode"])[0])) throw new FormatException("bad mode");
            p.Mode = m;
            p.TargetW = Fin(d["TargetW"], 2.5, 30);
            p.PerfMhz = Int(d["PerfMhz"], 0, 10000); p.PrimeMhz = Int(d["PrimeMhz"], 0, 10000);
            p.Epp = Int(d["Epp"], 0, 100);
            p.Boost = Int(d["Boost"], 0, 3); if (p.Boost == 1) p.Boost = 2;   // the UI offers 0, 3, 2
            p.Hz = Int(d["Hz"], 0, 1000); p.Asus = (string)d["Asus"];
            if (Array.IndexOf(Hardware.AsusNames, p.Asus) < 0) p.Asus = "standard";
            p.EffOnly = d.ContainsKey("EffOnly") && Convert.ToBoolean(d["EffOnly"]);
            // SoC below the driver's 5 W floor is not accepted by it; GPU target 0 = off, else 2-25 W.
            p.SocW = d.ContainsKey("SocW") ? Fin(d["SocW"], 0, 100) : 0; if (p.SocW > 0 && p.SocW < 5) p.SocW = 5;
            p.CpuShare = d.ContainsKey("CpuShare") ? Int(d["CpuShare"], -1, 90) : 0; if (p.CpuShare > 0 && p.CpuShare < 10) p.CpuShare = 10;
            p.GpuW = d.ContainsKey("GpuW") ? Fin(d["GpuW"], 0, 25) : 0; if (p.GpuW > 0 && p.GpuW < 2) p.GpuW = 2;
            return p;
        }
        static Dictionary<string, object> ToDict(Profile p)
        {
            return new Dictionary<string, object> {
                { "Version", 2 }, { "Preset", p.Preset }, { "Mode", p.Mode.ToString() }, { "TargetW", p.TargetW },
                { "PerfMhz", p.PerfMhz }, { "PrimeMhz", p.PrimeMhz }, { "Epp", p.Epp }, { "Boost", p.Boost },
                { "Hz", p.Hz }, { "Asus", p.Asus }, { "EffOnly", p.EffOnly }, { "SocW", p.SocW }, { "CpuShare", p.CpuShare }, { "GpuW", p.GpuW } };
        }

        public static Profile Load()
        {
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                if (!d.ContainsKey("Version")) return Profile.ForPreset("default");   // v1 file
                return FromDict(d);
            }
            catch { return Profile.ForPreset("default"); }
        }

        public static void Save(Profile p)
        {
            WriteAtomic(SettingsFile, Json.Serialize(ToDict(p)));
        }

        // Write to a temp file, then swap it in: an interrupted save leaves the old file, never half of one.
        static readonly object SaveLock = new object();
        public static bool WriteAtomic(string file, string text)
        {
            lock (SaveLock)
            {
                string tmp = file + ".tmp";
                try
                {
                    File.WriteAllText(tmp, text);
                    if (File.Exists(file)) File.Replace(tmp, file, null, true); else File.Move(tmp, file);
                    return true;
                }
                catch (Exception e) { try { File.Delete(tmp); } catch { } Log("save " + Path.GetFileName(file) + " failed: " + e.Message); return false; }
            }
        }

        // ---- user profile slots 1-3 (profiles.json) ----
        public const int Slots = 3;

        // "Apply at Windows startup" (like Afterburner): this slot is loaded whenever PowerCap starts. -1 = none.
        static string DefaultFile { get { return Path.Combine(Dir, "default.slot"); } }
        public static int DefaultSlot
        {
            get { try { int i; return File.Exists(DefaultFile) && int.TryParse(File.ReadAllText(DefaultFile).Trim(), out i) && i >= 0 && i < Slots ? i : -1; } catch { return -1; } }
            set { try { if (value < 0) File.Delete(DefaultFile); else File.WriteAllText(DefaultFile, value.ToString()); } catch { } }
        }
        public static Profile LoadStartup()
        {
            int d = DefaultSlot;
            var s = d >= 0 ? LoadSlots()[d] : null;
            if (s == null) return Load();
            var p = s.Clone(); p.Preset = "slot" + (d + 1);
            Log("startup profile: Profile " + (d + 1));
            return p;
        }
        static string SlotFile { get { return Path.Combine(Dir, "profiles.json"); } }
        public static Profile[] LoadSlots()
        {
            var r = new Profile[Slots];
            try
            {
                var a = Json.Deserialize<object[]>(File.ReadAllText(SlotFile));
                for (int i = 0; i < Slots && i < a.Length; i++)
                {
                    var d = a[i] as Dictionary<string, object>;
                    try { if (d != null) { r[i] = FromDict(d); r[i].Preset = "slot" + (i + 1); } } catch { Log("profile " + (i + 1) + " ignored: invalid values"); }
                }
            }
            catch { }
            return r;
        }
        public static void SaveSlot(int i, Profile p)
        {
            var all = LoadSlots();
            if (p != null) { p = p.Clone(); p.Preset = "slot" + (i + 1); }
            all[i] = p;
            WriteAtomic(SlotFile, Json.Serialize(all.Select(x => x == null ? null : ToDict(x)).ToArray()));
            Log(p == null ? "profile " + (i + 1) + " cleared" : "profile " + (i + 1) + " saved: " + Profile.Summary(p));
        }
        public static void Log(string s)
        {
            try
            {
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(LogFile);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine);
            }
            catch { }
        }
    }
}





