// Closed-loop CPU watt target. One control variable, "commanded watts", is mapped to per-cluster MHz
// caps through a curve measured on this laptop (tools\Calibrate.cs, 18-thread load, ASUS Standard):
//
//   efficient (6)  fast (12)   CPU W battery / AC
//   1500           700         2.4 / 3.2    Fast cores are capped first (700 MHz is their hardware
//   2000           700         2.9 / 4.1    floor; lower caps change nothing), then the efficient
//   2400           700         3.4 / 4.9    cores. Below ~1500/700 Windows stops using the fast cores
//   3000           700         3.9 / 6.5    (~1 W, everything on 6 slow cores) and that state is
//   free           700         4.3 / 9.3    sticky, so the curve ends at 1500/700. Jumping straight
//   free           800         6.0          to a low pair from idle could also trigger it; the loop
//   free           1000        9.0          moves in small steps. Core parking (max cores < 100 %)
//   free           1200       10.4          makes Windows ignore the MHz caps, so it is never used.
//   free           1600       13.3
//   free           2000       16.5          On AC the efficient cores clock higher, so the same caps
//   free           2400       19.3          draw more there; the feedback loop corrects for it.
//   free           3200       22.0
//   free           free       26.0
//
// "Efficient cores only" (hetero scheduling policy 3, nothing runs on the fast cores) has its own
// curve, efficient-cluster cap only (18 threads, AC): 700 0.4 W, 1000 0.9, 1500 1.4, 2000 2.25,
// 2400 3.1, 2800 3.8, 3200 5.9, free 7.3 W.
//
// The table is only the starting point. Each second the loop compares measured CPU watts with the
// target and moves the commanded watts (fast when over, slower when under), so ASUS mode, AC power,
// temperature and the kind of load are all corrected for by measurement.
using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace PowerCap
{
    class Governor
    {
        public const int PerfMax = 3609, PrimeMax = 4454;
        // c0 (efficient cluster), c1 (fast clusters), expected CPU W under full load on battery
        static readonly double[,] Curve = {
            { 1500, 700, 2.4 }, { 2000, 700, 2.9 }, { 2400, 700, 3.4 }, { 3000, 700, 3.9 },
            { PerfMax, 700, 4.3 }, { PerfMax, 800, 6.0 },
            { PerfMax, 1000, 9.0 }, { PerfMax, 1200, 10.4 }, { PerfMax, 1600, 13.3 }, { PerfMax, 2000, 16.5 },
            { PerfMax, 2400, 19.3 }, { PerfMax, 3200, 22.0 }, { PerfMax, PrimeMax, 26.0 } };
        static readonly double[,] EffCurve = {
            { 700, 0, 0.4 }, { 1000, 0, 0.9 }, { 1500, 0, 1.4 }, { 2000, 0, 2.25 }, { 2400, 0, 3.1 },
            { 2800, 0, 3.8 }, { 3200, 0, 5.9 }, { PerfMax, 0, 7.3 } };
        static double[,] CurveFor(bool effOnly) { return effOnly ? EffCurve : Curve; }
        public static double FloorW(bool effOnly) { return CurveFor(effOnly)[0, 2]; }
        public static double TopW(bool effOnly) { var c = CurveFor(effOnly); return c[c.GetLength(0) - 1, 2]; }
        public bool EffOnly;

        public double Commanded;    // W on the curve
        public double Filtered;     // smoothed measured CPU W
        double _target = -1;

        // Feed-forward: the caps expected to give `watts` under full load. 0 = no cap.
        public static int[] CapsFor(double watts, bool effOnly = false)
        {
            var Curve = CurveFor(effOnly);
            int n = Curve.GetLength(0);
            if (watts >= Curve[n - 1, 2]) return new[] { 0, 0 };
            if (watts <= Curve[0, 2]) return new[] { (int)Curve[0, 0], (int)Curve[0, 1] };
            for (int i = 1; i < n; i++)
            {
                if (watts > Curve[i, 2]) continue;
                double t = (watts - Curve[i - 1, 2]) / (Curve[i, 2] - Curve[i - 1, 2]);
                int c0 = (int)Math.Round(Curve[i - 1, 0] + t * (Curve[i, 0] - Curve[i - 1, 0]));
                int c1 = (int)Math.Round(Curve[i - 1, 1] + t * (Curve[i, 1] - Curve[i - 1, 1]));
                c0 = (int)(Math.Round(c0 / 10.0) * 10); c1 = (int)(Math.Round(c1 / 10.0) * 10);
                return new[] { c0 >= PerfMax ? 0 : c0, c1 >= PrimeMax ? 0 : c1 };
            }
            return new[] { 0, 0 };
        }

        public void Reset() { _target = -1; }

        // ---- learning ----
        // When the loop has held the target under a limiting load for a while, the commanded watts that
        // did it are remembered per power source and target (learned.json). The next time that target is
        // chosen the loop starts there instead of at the built-in table, so it settles faster.
        static readonly Dictionary<string, double> Learned = Load();
        static DateTime _lastSave = DateTime.MinValue;
        static bool _dirty;
        int _steady;
        bool _ac;
        static string LearnedFile { get { return Path.Combine(Store.Dir, "learned.json"); } }
        string Key(bool ac, double target) { return (ac ? "ac" : "battery") + (EffOnly ? "-eff" : "") + "@" + (Math.Round(target * 2) / 2).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }
        static Dictionary<string, double> Load()
        {
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, double>>(File.ReadAllText(LearnedFile)) ?? new Dictionary<string, double>(); }
            catch { return new Dictionary<string, double>(); }
        }
        static void SaveLearned(bool force)
        {
            if (!_dirty || (!force && (DateTime.Now - _lastSave).TotalSeconds < 60)) return;
            try { lock (Learned) File.WriteAllText(LearnedFile, new JavaScriptSerializer().Serialize(Learned)); _dirty = false; _lastSave = DateTime.Now; } catch { }
        }
        public static void Flush() { SaveLearned(true); }
        public static int LearnedCount { get { lock (Learned) return Learned.Count; } }

        double Start(double target, bool ac)
        {
            double v;
            lock (Learned) if (Learned.TryGetValue(Key(ac, target), out v)) return v;
            return target;
        }

        void Learn(double target, double cpuW, double ceiling)
        {
            // Only when the caps are what limits the CPU: on target, and not opened up to the ceiling.
            bool limiting = Math.Abs(Filtered - target) < Math.Max(0.3, target * 0.08) && Commanded < ceiling - 0.5;
            _steady = limiting ? _steady + 1 : 0;
            if (_steady < 20 || _steady % 10 != 0) return;
            string k = Key(_ac, target);
            lock (Learned)
            {
                double old;
                double v = Learned.TryGetValue(k, out old) ? old * 0.7 + Commanded * 0.3 : Commanded;
                if (!Learned.ContainsKey(k) || Math.Abs(old - v) > 0.05)
                {
                    if (!Learned.ContainsKey(k)) Store.Log(string.Format("learned {0}: start at {1:0.00} W on the curve (table: {2:0.00})", k, v, target));
                    Learned[k] = v; _dirty = true;
                }
            }
            SaveLearned(false);
        }

        // Called once a second with the measured CPU watts. Returns the caps {efficient, fast} to apply.
        public int[] Step(double target, double cpuW, bool ac)
        {
            if (Math.Abs(target - _target) > 0.01 || ac != _ac)
            {
                // New target or power source: start from what worked before, else the table.
                _target = target; _ac = ac; _steady = 0;
                Commanded = Start(target, ac);
                Filtered = cpuW;
            }
            Filtered = 0.5 * Filtered + 0.5 * cpuW;
            // A real overshoot (load jump) is cut from the raw reading within a second or two. Otherwise
            // correct symmetrically on the smoothed reading: at fixed caps CPU watts wobble about +-1 W,
            // and reacting harder downwards than upwards would sit below the target on average.
            if (cpuW > target * 1.15 + 0.5) Commanded -= 0.8 * (cpuW - target);
            else Commanded += 0.3 * (target - Filtered);
            // Under light load the loop would wind up to "no cap" and a sudden heavy load would then
            // overshoot for several seconds. Limit how far above the target the caps may open.
            double ceiling = Math.Min(TopW(EffOnly), Math.Max(target * 2.0 + 3, target + 6));
            Commanded = Math.Max(FloorW(EffOnly), Math.Min(ceiling, Commanded));
            Learn(target, cpuW, ceiling);
            return CapsFor(Commanded, EffOnly);
        }
    }
}



