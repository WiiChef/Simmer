// Measures CPU watts vs. per-cluster MHz cap under an all-core load, in the PowerCap plan.
// Usage: calibrate.exe [threads] [settleSec] [sampleSec]   -> CSV on stdout. Always re-activates Balanced at the end.
using System;
using System.Linq;
using System.Threading;

namespace PowerCap
{
    static class Calibrate
    {
        static volatile bool _run = true;
        static long _ops;   // work done, in millions of loop iterations
        static void Burn()
        {
            double x = 1.0001; long n = 0;
            while (_run) { x = x * 1.0000001 + 0.0000001; if (x > 2) x = 1.0001; n += (long)x; if ((n & 0xFFFFF) == 0) { Interlocked.Increment(ref _ops); Thread.Yield(); } }
        }

        static void Main(string[] a)
        {
            int threads = a.Length > 0 ? int.Parse(a[0]) : Environment.ProcessorCount;
            int settle = a.Length > 1 ? int.Parse(a[1]) : 3, sample = a.Length > 2 ? int.Parse(a[2]) : 5;
            string[] steps = a.Length > 3 ? a[3].Split(',') : new[] { "0", "3600", "3000", "2600", "2200", "1800", "1500", "1200", "1000", "800" };
            var m = new Meter();
            var max = Topology.MaxMhz();
            Console.Error.WriteLine("classes: " + string.Join(",", m.CoreClass) + "  maxMhz: " + string.Join(",", max));
            Console.Error.WriteLine("plan " + Plan.Guid);
            var ts = Enumerable.Range(0, threads).Select(i => new Thread(Burn) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
            try
            {
                // idle baseline first
                Plan.Apply(new Caps { Epp = 50, Boost = 2 });
                Thread.Sleep(2000); m.Sample();
                Report(m, "idle", 0, sample);
                ts.ForEach(t => t.Start());
                foreach (var st in steps)
                {
                    var p = st.Split('/').Select(int.Parse).ToArray(); int c0 = p[0], c1 = p.Length > 1 ? p[1] : p[0];
                    Plan.Apply(new Caps { Mhz = new[] { c0, c1, c1 }, Cores = p.Length > 2 ? p[2] : 100, MaxState = p.Length > 3 ? p[3] : 100, Epp = 50, Boost = p.Length > 4 ? p[4] : 2 });
                    Thread.Sleep(settle * 1000); m.Sample();
                    Report(m, "load" + threads, st, sample);
                }
            }
            finally
            {
                _run = false;
                Plan.ActivateBalanced();
            }
        }

        static System.Diagnostics.PerformanceCounter[] _util;
        static void Report(Meter m, string tag, object cap, int sec)
        {
            if (_util == null) { _util = Enumerable.Range(0, Environment.ProcessorCount).Select(i => new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", i.ToString())).ToArray(); foreach (var u in _util) u.NextValue(); }
            foreach (var u in _util) u.NextValue();
            var acc = new double[8]; var mhz = new int[3]; int n = 0; double peak = 0;
            long ops0 = Interlocked.Read(ref _ops); var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int s = 0; s < sec; s++)
            {
                Thread.Sleep(1000);
                var r = m.Sample(); if (!r.Valid) continue;
                acc[0] += r.Cluster[0]; acc[1] += r.Cluster[1]; acc[2] += r.Cluster[2]; acc[3] += r.Cpu;
                acc[4] += r.Gpu; acc[5] += r.Soc; acc[6] += r.System; acc[7] += r.Memory; peak = Math.Max(peak, r.Cpu);
                for (int k = 0; k < 3; k++) mhz[k] = Math.Max(mhz[k], r.ClusterMhz[k]);
                n++;
            }
            if (n == 0) n = 1;
            double opsPerSec = (Interlocked.Read(ref _ops) - ops0) / sw.Elapsed.TotalSeconds;
            var busy = _util.Select(u => u.NextValue()).ToArray();
            double b0 = busy.Where((v, i) => m.CoreClass[i] == 0).Average(), b1 = busy.Where((v, i) => m.CoreClass[i] == 1).Average();
            mhz[0] = (int)b0; mhz[1] = (int)b1; mhz[2] = (int)Math.Round(peak);
            Console.WriteLine("{0},{1},{2:F2},{3:F2},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F2},{10},{11},{12}", tag, cap,
                acc[0] / n, acc[1] / n, acc[2] / n, acc[3] / n, acc[4] / n, acc[5] / n, acc[6] / n, acc[7] / n, mhz[0], mhz[1], mhz[2] + "," + opsPerSec.ToString("0.0"));
        }
    }
}





