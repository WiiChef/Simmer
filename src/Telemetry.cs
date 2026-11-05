// Telemetry log for tuning PowerCap from real use: logs\telemetry-YYYY-MM-DD.csv, one row per 5 s
// (averages over those seconds, plus the peak CPU second). Files older than 30 days are deleted.
using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PowerCap
{
    sealed class Telemetry
    {
        public const int Period = 5;
        const string Header = "time,ac,battery_pct,mode,preset,target_w,asus,hz,epp,boost,cap_eff_mhz,cap_fast_mhz,commanded_w,cpu_w,cpu_peak_w,eff_w,fast_a_w,fast_b_w,gpu_w,npu_w,mem_w,infra_w,soc_w,system_w,samples,chip_c,chip_peak_c,zones_c,fan1_rpm,fan2_rpm,eff_only,gpu_peak_w,gpu_busy_pct,gpu_busy_peak_pct,gpu_3d_pct,media_w,soc_limit_w,gpu_target_w";
        static string Dir { get { return Path.Combine(Store.Dir, "logs"); } }
        readonly double[] _sum = new double[11];
        double _gpuPeak, _gpuBusy, _gpuBusyPeak, _gpu3d; int _nGpu;
        double _peak, _chip, _chipPeak; int _n, _ticks, _nChip; string _zones = ""; int[] _fan = { -1, -1 };

        public string[] ZoneNames = new string[0];

        public Telemetry()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                foreach (var f in Directory.GetFiles(Dir, "telemetry-*.csv").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30))) File.Delete(f);
            }
            catch { }
        }

        public void Add(Reading r, Profile p, Caps applied, double commanded, bool ac, double batPct, int hz, double socLimitW)
        {
            if (r.Valid)
            {
                var v = new[] { r.Cpu, r.Cluster[0], r.Cluster[1], r.Cluster[2], r.Gpu, r.Npu, r.Memory, r.Infra, r.Soc, r.System, r.Multimedia };
                for (int i = 0; i < v.Length; i++) _sum[i] += v[i];
                _peak = Math.Max(_peak, r.Cpu);
                _gpuPeak = Math.Max(_gpuPeak, r.Gpu);
                if (!double.IsNaN(r.GpuBusy)) { _gpuBusy += r.GpuBusy; _gpu3d += r.Gpu3d; _gpuBusyPeak = Math.Max(_gpuBusyPeak, r.GpuBusy); _nGpu++; }
                _n++;
                if (!double.IsNaN(r.ChipC)) { _chip += r.ChipC; _chipPeak = Math.Max(_chipPeak, r.ChipC); _nChip++; }
                // all zones as name=C, separated by spaces (last sample of the period)
                var zs = new System.Text.StringBuilder();
                for (int i = 0; i < r.ZoneC.Length && i < ZoneNames.Length; i++)
                    if (!double.IsNaN(r.ZoneC[i])) zs.Append(ZoneNames[i]).Append('=').Append(r.ZoneC[i].ToString("0.0", CultureInfo.InvariantCulture)).Append(' ');
                _zones = zs.ToString().Trim();
                _fan = r.FanRpm;
            }
            if (++_ticks < Period) return;
            if (_n > 0)
            {
                var c = CultureInfo.InvariantCulture;
                Func<int, string> avg = i => (_sum[i] / _n).ToString("0.000", c);
                string line = string.Join(",", new[] {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ac ? "1" : "0", ((int)batPct).ToString(), p.Mode.ToString(), p.Preset,
                    p.Mode == Mode.Target ? p.TargetW.ToString("0.0", c) : "", p.Asus, hz.ToString(), p.Epp.ToString(), p.Boost.ToString(),
                    applied == null ? "" : applied.Mhz[0].ToString(), applied == null ? "" : applied.Mhz[1].ToString(),
                    p.Mode == Mode.Target ? commanded.ToString("0.00", c) : "",
                    avg(0), _peak.ToString("0.000", c), avg(1), avg(2), avg(3), avg(4), avg(5), avg(6), avg(7), avg(8), avg(9), _n.ToString(),
                    _nChip > 0 ? (_chip / _nChip).ToString("0.0", c) : "", _nChip > 0 ? _chipPeak.ToString("0.0", c) : "", _zones,
                    _fan[0] < 0 ? "" : _fan[0].ToString(), _fan[1] < 0 ? "" : _fan[1].ToString(), p.EffOnly ? "1" : "0",
                    _gpuPeak.ToString("0.000", c), _nGpu > 0 ? (_gpuBusy / _nGpu).ToString("0.0", c) : "", _nGpu > 0 ? _gpuBusyPeak.ToString("0.0", c) : "",
                    _nGpu > 0 ? (_gpu3d / _nGpu).ToString("0.0", c) : "", avg(10),
                    double.IsNaN(socLimitW) ? "" : socLimitW.ToString("0.0", c), p.GpuW > 0 ? p.GpuW.ToString("0.0", c) : "" });
                string day = DateTime.Now.ToString("yyyy-MM-dd");
                if (day != _bufDay) Flush();
                _bufDay = day; _buf.Append(line).Append(Environment.NewLine);
                if (++_bufRows >= FlushRows) Flush();
            }
            Array.Clear(_sum, 0, _sum.Length); _peak = 0; _n = 0; _ticks = 0; _chip = 0; _chipPeak = 0; _nChip = 0; _gpuPeak = 0; _gpuBusy = 0; _gpuBusyPeak = 0; _gpu3d = 0; _nGpu = 0;
        }

        // Rows are kept in memory and written 12 at a time (one disk write per minute or three, not every 5 ticks);
        // Engine.Shutdown flushes the rest.
        const int FlushRows = 12;
        readonly System.Text.StringBuilder _buf = new System.Text.StringBuilder();
        string _bufDay, _file, _fileDay; int _bufRows;
        public void Flush()
        {
            if (_bufRows == 0) return;
            try
            {
                if (_fileDay != _bufDay)
                {
                    string f = Path.Combine(Dir, "telemetry-" + _bufDay + ".csv");
                    // A file started by an older version has fewer columns: continue in a new one.
                    if (File.Exists(f) && File.ReadLines(f).FirstOrDefault() != Header) f = f.Replace(".csv", "-b.csv");
                    if (!File.Exists(f)) File.WriteAllText(f, Header + Environment.NewLine);
                    _file = f; _fileDay = _bufDay;
                }
                File.AppendAllText(_file, _buf.ToString());
            }
            catch { }
            _buf.Clear(); _bufRows = 0;
        }
    }
}

