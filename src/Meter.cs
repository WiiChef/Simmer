// Live power readings from the Snapdragon energy meters (PDH "Energy Meter" counters, milliwatts),
// plus per-core clocks and battery state.
using System;
using System.Management;

namespace PowerCap
{
    class Reading
    {
        public double[] Cluster = new double[3];   // W
        public double Cpu, Gpu, Npu, Memory, Infra, Soc, System, Multimedia, Usbc;
        public double[] CoreMhz = new double[0];
        public int[] ClusterMhz = new int[3];      // highest current clock in each efficiency class
        public bool Valid;
        public double ChipC = double.NaN;          // hottest SoC thermal zone
        public double[] ZoneC = new double[0];     // every readable zone, same order as Meter.ZoneNames
        public int[] FanRpm = { -1, -1 };          // CPU fan, second fan; -1 = unknown
        public double GpuBusy = double.NaN;        // % of the busiest GPU engine (Task Manager style), all processes
        public double Gpu3d = double.NaN;          // % of the busiest 3D engine
    }

    sealed class Meter : IDisposable
    {
        static readonly string[] Names = { "CPU_CLUSTER_0", "CPU_CLUSTER_1", "CPU_CLUSTER_2", "GPU", "NPU", "MEMORY", "INFRA", "SOC", "SYSTEM", "MULTIMEDIA", "USBC_TOTAL" };
        IntPtr _q;
        readonly IntPtr[] _c = new IntPtr[Names.Length];
        bool _primed;
        public readonly int[] CoreClass;   // efficiency class of each logical processor
        // Thermal zones (\Thermal Zone Information(*)\High Precision Temperature, tenths of a kelvin).
        // TZ37 reads ~86 C at idle and TZ91 ~29 C (likely a skin/ambient sensor); neither tracks the SoC,
        // so they are logged but not part of the chip temperature.
        public string[] ZoneNames = new string[0];
        IntPtr[] _zones = new IntPtr[0];
        static readonly string[] NotChip = { "TZ37", "TZ91" };
        IntPtr _gpuEng;   // \GPU Engine(*)\Utilization Percentage (one instance per process and engine)
        // Thermal zones cost ~8 ms per collect (ACPI), 50x the energy meters, so they have their own query and the
        // engine reads them (and the fans) less often while the window is hidden. Between reads the last values repeat.
        IntPtr _tq;
        double[] _lastZones = new double[0]; double _lastChip = double.NaN; int[] _lastFan = { -1, -1 };

        public Meter()
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out _q) != 0) _q = IntPtr.Zero;
            for (int i = 0; i < Names.Length && _q != IntPtr.Zero; i++)
                Native.PdhAddEnglishCounter(_q, @"\Energy Meter(" + Names[i] + @")\Power", IntPtr.Zero, out _c[i]);
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out _tq) != 0) _tq = IntPtr.Zero;
            try
            {
                var names = new System.Diagnostics.PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames();
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                var zn = new System.Collections.Generic.List<string>(); var zc = new System.Collections.Generic.List<IntPtr>();
                foreach (var n in names)
                {
                    IntPtr c;
                    if (_tq != IntPtr.Zero && Native.PdhAddEnglishCounter(_tq, @"\Thermal Zone Information(" + n + @")\High Precision Temperature", IntPtr.Zero, out c) == 0)
                    { zn.Add(n.Replace(@"\_SB.", "").Replace(@"\_sb.", "").ToUpperInvariant()); zc.Add(c); }
                }
                ZoneNames = zn.ToArray(); _zones = zc.ToArray();
            }
            catch { }
            if (_q != IntPtr.Zero && Native.PdhAddEnglishCounter(_q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuEng) != 0) _gpuEng = IntPtr.Zero;
            if (_q != IntPtr.Zero) Native.PdhCollectQueryData(_q);   // first sample after opening is always zero
            CoreClass = Topology.EfficiencyClasses();
        }

        double Get(int i)
        {
            if (_c[i] == IntPtr.Zero) return 0;
            Native.PDH_FMT_COUNTERVALUE v;
            if (Native.PdhGetFormattedCounterValue(_c[i], Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, IntPtr.Zero, out v) != 0) return 0;
            double w = v.Double / 1000.0;
            return (w < 0 || w > 200 || double.IsNaN(w)) ? 0 : w;
        }

        public Reading Sample(bool slow = true, bool foreground = true)
        {
            var r = new Reading();
            bool temps = slow && _tq != IntPtr.Zero && Native.PdhCollectQueryData(_tq) == 0;
            if (_q != IntPtr.Zero && Native.PdhCollectQueryData(_q) == 0)
            {
                for (int i = 0; i < 3; i++) r.Cluster[i] = Get(i);
                r.Cpu = r.Cluster[0] + r.Cluster[1] + r.Cluster[2];
                r.Gpu = Get(3); r.Npu = Get(4); r.Memory = Get(5); r.Infra = Get(6); r.Soc = Get(7);
                r.System = Get(8); r.Multimedia = Get(9); r.Usbc = Get(10);
                r.ZoneC = new double[_zones.Length];
                if (!temps && _lastZones.Length == _zones.Length) { Array.Copy(_lastZones, r.ZoneC, _zones.Length); r.ChipC = _lastChip; }
                for (int i = 0; i < _zones.Length && temps; i++)
                {
                    Native.PDH_FMT_COUNTERVALUE v;
                    double t = double.NaN;
                    if (Native.PdhGetFormattedCounterValue(_zones[i], Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, IntPtr.Zero, out v) == 0 && v.Double > 2732)
                        t = v.Double / 10.0 - 273.15;   // unpopulated zones read 0 K
                    if (t > 110) t = double.NaN;
                    r.ZoneC[i] = t;
                    if (!double.IsNaN(t) && Array.IndexOf(NotChip, ZoneNames[i]) < 0 && (double.IsNaN(r.ChipC) || t > r.ChipC)) r.ChipC = t;
                }
                if (temps) { _lastZones = (double[])r.ZoneC.Clone(); _lastChip = r.ChipC; }
                if (foreground) GpuEngines(r);   // per-process array: the costliest counter to format
                // Occasional glitch samples (e.g. 67 W CPU on a 7 W load): the CPU clusters are part of
                // the SoC meter, so a CPU sum above SoC is not a real reading.
                r.Valid = _primed && r.Soc > 0 && r.Cpu <= r.Soc + 0.5 && r.Soc <= r.System + 1;
                _primed = true;
            }
            // ASUS fan speed, low 16 bits = RPM / 100 (bit 16 = present). DSTS 0x00110035 returns both fans as a package:
            // its first dword repeats fan 0, the second is fan 1.
            if (!slow) { r.FanRpm[0] = _lastFan[0]; r.FanRpm[1] = _lastFan[1]; }
            for (int i = 0; i < 2 && slow; i++)
            {
                long v = i == 0 ? Native.AtkGet(0x00110013) : Native.AtkGet(0x00110035, 1);
                if (v >= 0 && (v & 0x10000) != 0) r.FanRpm[i] = (int)(v & 0xFFFF) * 100;
            }
            if (slow) { _lastFan[0] = r.FanRpm[0]; _lastFan[1] = r.FanRpm[1]; }
            if (!foreground) return r;
            r.CoreMhz = Topology.CoreMhz();
            for (int i = 0; i < r.CoreMhz.Length && i < CoreClass.Length; i++)
            {
                int k = Math.Min(2, CoreClass[i]);
                r.ClusterMhz[k] = Math.Max(r.ClusterMhz[k], (int)r.CoreMhz[i]);
            }
            return r;
        }

        // Sums each engine over processes, then takes the busiest engine (same as Task Manager's GPU %).
        void GpuEngines(Reading r)
        {
            if (_gpuEng == IntPtr.Zero) return;
            uint bytes = 0, n;
            if (Native.PdhGetFormattedCounterArray(_gpuEng, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref bytes, out n, IntPtr.Zero) != 0x800007D2 || bytes == 0) return;   // PDH_MORE_DATA
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (Native.PdhGetFormattedCounterArray(_gpuEng, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref bytes, out n, buf) != 0) return;
                var eng = new System.Collections.Generic.Dictionary<string, double>();
                int size = IntPtr.Size + 16;
                for (int i = 0; i < n; i++)
                {
                    IntPtr item = buf + i * size;
                    string name = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(item));
                    if (System.Runtime.InteropServices.Marshal.ReadInt32(item, IntPtr.Size) != 0) continue;   // CStatus
                    double v = BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(item, IntPtr.Size + 8));
                    int k = name == null ? -1 : name.IndexOf("phys_", StringComparison.Ordinal);
                    if (k < 0 || double.IsNaN(v) || v < 0) continue;
                    string key = name.Substring(k);
                    double s; eng.TryGetValue(key, out s); eng[key] = s + v;
                }
                double busy = 0, d3 = 0;
                foreach (var kv in eng)
                {
                    double v = Math.Min(100, kv.Value);
                    busy = Math.Max(busy, v);
                    if (kv.Key.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) d3 = Math.Max(d3, v);
                }
                r.GpuBusy = busy; r.Gpu3d = d3;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }

        public void Dispose()
        {
            if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
            if (_tq != IntPtr.Zero) { Native.PdhCloseQuery(_tq); _tq = IntPtr.Zero; }
        }
    }

    static class Topology
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetLogicalProcessorInformationEx(int rel, byte[] buf, ref uint len);

        // Efficiency class per logical processor (RelationProcessorCore records).
        public static int[] EfficiencyClasses()
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(0, null, ref len);
            var b = new byte[len];
            var res = new System.Collections.Generic.List<int>();
            if (!GetLogicalProcessorInformationEx(0, b, ref len)) return new int[Environment.ProcessorCount];
            var map = new int[Environment.ProcessorCount];
            int off = 0;
            while (off < len)
            {
                int size = BitConverter.ToInt32(b, off + 4);
                int eff = b[off + 9];
                int groupCount = BitConverter.ToUInt16(b, off + 30);
                for (int gI = 0; gI < groupCount; gI++)
                {
                    // GROUP_AFFINITY: KAFFINITY Mask (8 bytes on 64-bit), WORD Group, WORD[3] reserved
                    ulong mask = BitConverter.ToUInt64(b, off + 32 + gI * 16);
                    for (int bit = 0; bit < 64; bit++)
                        if ((mask & (1UL << bit)) != 0 && bit < map.Length) map[bit] = eff;
                }
                off += size;
            }
            return map;
        }

        public static double[] CoreMhz()
        {
            int n = Environment.ProcessorCount;
            var a = new Native.PROCESSOR_POWER_INFORMATION[n];
            uint sz = (uint)(n * System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.PROCESSOR_POWER_INFORMATION)));
            var r = new double[n];
            if (Native.CallNtPowerInformation(11, IntPtr.Zero, 0, a, sz) != 0) return r;
            for (int i = 0; i < n; i++) r[i] = a[i].CurrentMhz;
            return r;
        }

        public static int[] MaxMhz()
        {
            int n = Environment.ProcessorCount;
            var a = new Native.PROCESSOR_POWER_INFORMATION[n];
            uint sz = (uint)(n * System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.PROCESSOR_POWER_INFORMATION)));
            var r = new int[n];
            if (Native.CallNtPowerInformation(11, IntPtr.Zero, 0, a, sz) != 0) return r;
            for (int i = 0; i < n; i++) r[i] = (int)a[i].MaxMhz;
            return r;
        }
    }

    static class Battery
    {
        // Returns (percent, onAC, design-independent full capacity in Wh or 0, discharge W or 0).
        static double _fullWh; static int _fullAge;
        public static void Read(out int pct, out bool ac, out double fullWh, out double dischargeW)
        {
            Native.SYSTEM_POWER_STATUS s;
            Native.GetSystemPowerStatus(out s);
            pct = s.BatteryLifePercent > 100 ? -1 : s.BatteryLifePercent;
            ac = s.ACLineStatus == 1;
            fullWh = _fullWh; dischargeW = 0;
            try
            {
                // Full-charge capacity moves over weeks: query it once every ~20 reads.
                if (_fullWh <= 0 || _fullAge++ >= 20)
                {
                    _fullAge = 0;
                    using (var q = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                        foreach (ManagementObject o in q.Get()) { fullWh = _fullWh = Convert.ToDouble(o["FullChargedCapacity"]) / 1000.0; break; }
                }
                using (var q = new ManagementObjectSearcher(@"root\wmi", "SELECT DischargeRate FROM BatteryStatus"))
                    foreach (ManagementObject o in q.Get()) { dischargeW = Convert.ToDouble(o["DischargeRate"]) / 1000.0; break; }
            }
            catch { }
        }
    }
}

