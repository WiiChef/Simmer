// Background worker: samples the energy meters once a second, runs the watt governor and applies
// the profile. The UI only reads Snapshot and calls SetProfile; it never touches the governor.
using System;
using System.Linq;
using System.Threading;

namespace PowerCap
{
    class Snapshot
    {
        public Reading R = new Reading();
        public Caps Applied;           // null while Windows Balanced is active
        public Profile Profile;
        public double Commanded;
        public int BatteryPct = -1; public bool OnAC; public double FullWh; public int MinutesLeft = -1;
        public bool PlanActive; public string PlanName = "";
        public int Hz;
        public double SocLimitW = double.NaN;   // effective SoC PL1 (ours or the firmware's), NaN = unknown
        public double SocFirmwareW = double.NaN; // what the ASUS fan mode sets when we hold no request
        public string SocProblem;                // why the SoC limit cannot be used, or null
        public double SocHeldW = double.NaN;     // the limit PowerCap holds right now, NaN = none
        public bool FanOn;                       // fan curve switched on
        public int FanPwm = -1;                  // PWM PowerCap set, -1 = the EC's own curve
        public double FanCurveC = double.NaN;    // temperature the curve uses (chip, with 3 C hysteresis)
        public string FanState;                  // why the EC has the fans while the curve is on, or null
        public int ChargeFw = -1;                // charge limit the firmware has now (%), -1 = unknown
        public int ChargeLimit;                  // limit PowerCap keeps (%), 0 = left to ASUS
        public string ChargeProblem;             // why PowerCap's limit isn't in place, or null
        public bool Draining;                    // discharging to the limit with the charger connected (Drain.cs)
    }

    sealed class Engine : IDisposable
    {
        public const int HistoryLen = 180;
        public readonly double[] HistCpu = new double[HistoryLen], HistGpu = new double[HistoryLen], HistSys = new double[HistoryLen], HistTarget = new double[HistoryLen];
        public int HistCount;
        public readonly object Lock = new object();

        readonly Meter _meter = new Meter();
        readonly Governor _gov = new Governor();
        readonly Telemetry _tele = new Telemetry();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        Thread _thread;
        volatile bool _stop;
        Profile _profile, _appliedProfile;
        bool _hwDirty = true;
        Caps _last;
        bool _weActivated;
        DateTime _foreignSince = DateTime.MinValue;   // when another plan became active (see the plan check in Tick)
        Snapshot _snap = new Snapshot();
        int _batteryTick;
        double _batPct = -1, _fullWh, _dischargeW; bool _ac;
        int _socTick, _socRecheck; double _socEff = double.NaN, _socFw = double.NaN, _socHeld = double.NaN, _other = double.NaN, _gpuTrim; string _socKey, _socFailKey;
        string _hotkeyAsus;   // last QuietFan value seen (Fn+F)

        public event Action<string> Notice;   // raised on the worker thread
        // Window visible: full sensor set every second. Hidden: no GPU engine % or core clocks, temperatures/fans every 10th tick.
        volatile bool _fg;
        public bool Foreground { get { return _fg; } set { if (_fg == value) return; _fg = value; _wake.Set(); } }
        int _slowTick, _fanTempTick;
        volatile bool _fast = true;
        // Quiet on battery (all profiles): efficient cores only, no boost, EPP >= 90, so light work doesn't wake
        // the fast cores at 4+ GHz. Measured idle: SoC 4.75 -> ~3.3 W, chip ~4 C cooler. Not in Windows default.
        static string QuietFile { get { return System.IO.Path.Combine(Store.Dir, "quiet.battery"); } }
        volatile bool _quiet = System.IO.File.Exists(QuietFile);
        public bool QuietOnBattery
        {
            get { return _quiet; }
            set { _quiet = value; try { if (value) System.IO.File.WriteAllText(QuietFile, ""); else System.IO.File.Delete(QuietFile); } catch { } Store.Log("quiet on battery: " + (value ? "on" : "off")); _wake.Set(); }
        }
        public volatile bool QuietActive;   // applied right now (on battery)

        // Burst window (s): how long the SoC cap averages over. Short = a hard cap with little burst headroom; 0 = default
        // (15 s, 2 s with a GPU target). A GPU target never gets a window longer than 2 s, or it can't be held.
        public const int BurstMin = 1, BurstMax = 15;
        static string BurstFile { get { return System.IO.Path.Combine(Store.Dir, "burst.window"); } }
        volatile int _burst = LoadBurst();
        static int LoadBurst()
        {
            int v;
            try { return int.TryParse(System.IO.File.ReadAllText(BurstFile).Trim(), out v) && v >= BurstMin && v <= BurstMax ? v : 0; }
            catch { return 0; }
        }
        public int BurstWindow
        {
            get { return _burst; }
            set
            {
                if (value != 0 && (value < BurstMin || value > BurstMax)) throw new ArgumentOutOfRangeException("value");
                _burst = value;
                try { if (value == 0) System.IO.File.Delete(BurstFile); else Store.WriteAtomic(BurstFile, value.ToString()); } catch { }
                Store.Log("burst window: " + (value == 0 ? "default" : value + " s"));
                _wake.Set();
            }
        }
        uint SocWindow(Profile p)
        {
            uint w = _burst > 0 ? (uint)_burst * 1000000u : Pep.SlowWindow;
            return p.GpuW > 0 ? Math.Min(w, Pep.FastWindow) : w;
        }

        // Battery charge limit (Charge.cs). 0 = PowerCap leaves it to ASUS. Kept: checked with each battery read and
        // set again when ASUS's service, sleep or a charger change put another value in the firmware.
        static string ChargeFile { get { return System.IO.Path.Combine(Store.Dir, "charge.limit"); } }
        volatile int _charge = LoadCharge();
        volatile bool _chargeDirty = true;
        int _chargeFw = -1, _chargeFailed; string _chargeProblem;
        public static int LoadCharge()
        {
            int v;
            try { return int.TryParse(System.IO.File.ReadAllText(ChargeFile).Trim(), out v) && v >= Charge.Min && v <= Charge.Max ? v : 0; }
            catch { return 0; }
        }
        public int ChargeLimit
        {
            get { return _charge; }
            set
            {
                if (value != 0 && (value < Charge.Min || value > Charge.Max)) throw new ArgumentOutOfRangeException("value");
                _charge = value;
                try { if (value == 0) System.IO.File.Delete(ChargeFile); else Store.WriteAtomic(ChargeFile, value.ToString()); } catch { }
                Store.Log("charge limit: " + (value == 0 ? "left to ASUS" : value + " %"));
                _chargeFailed = 0; _chargeDirty = true; _wake.Set();
            }
        }

        void ChargeTick()
        {
            int want = _charge;
            _chargeFw = Charge.Read();
            if (want == 0) { _chargeProblem = null; _chargeFailed = 0; return; }
            if (!Pep.IsAdmin) { _chargeProblem = "needs Simmer running as administrator"; return; }
            if (_chargeFw < 0) { _chargeProblem = "battery driver not answering"; return; }
            if (_chargeFw == want) { _chargeProblem = null; _chargeFailed = 0; return; }
            if (_chargeFailed == want) return;   // failed once already: wait for the user to change it
            try
            {
                Charge.Set(want);
                Store.Log("charge limit: firmware had " + _chargeFw + " %, set " + want + " %");
                _chargeFw = want; _chargeProblem = null;
            }
            catch (Exception e)
            {
                _chargeFailed = want; _chargeProblem = e.Message; _chargeFw = Charge.Read();
                Store.Log("charge limit: " + e.Message);
            }
        }

        // Above the limit with the charger in: drain down to it (Drain.cs), at whatever the laptop draws. Starts 2 %
        // over the limit (the firmware lets it settle a point under), stops at the limit. Windows sees the charger as
        // unplugged meanwhile, so the AC flag is only trusted when no drain runs.
        bool _draining; DateTime _drainRetry; string _drainSkip;
        void DrainTick()
        {
            int want = _charge, pct = (int)_batPct;
            if (_draining)
            {
                _batteryTick = Math.Min(_batteryTick, 5);   // fresh percentage while draining
                string why = !Drain.Beat() ? "ended by the kernel" : want == 0 ? "charge limit off" : pct < 0 ? "battery level unreadable" : pct <= want ? "reached " + pct + " %" : null;
                if (why == null) return;
                _draining = false;
                string problem = Drain.Stop();
                Store.Log("drain: " + why + (problem == null ? ", normal charging back" : "; " + problem + ", the driver keeps retrying"));
                if (why == "ended by the kernel") _drainRetry = DateTime.UtcNow.AddMinutes(2);
                _batteryTick = 0;
                return;
            }
            if (want == 0 || !_ac || pct < want + 2 || DateTime.UtcNow < _drainRetry) return;
            string skip = Drain.Unavailable();
            if (skip != null) { if (skip != _drainSkip) Store.Log("drain to the limit not possible: " + skip); _drainSkip = skip; _drainRetry = DateTime.UtcNow.AddMinutes(5); return; }
            try { Drain.Start(); _draining = true; _drainSkip = null; Store.Log("drain: " + pct + " % on the charger, discharging to " + want + " %"); }
            catch (Exception e) { Store.Log("drain: could not start: " + e.Message); _drainRetry = DateTime.UtcNow.AddMinutes(15); }
        }

        // Fan curve (Fan.cs). The curve object is replaced, never changed, so the worker can read it without a copy.
        FanCurve _fan = FanCurve.Load();
        public FanCurve FanCurve
        {
            get { lock (Lock) return _fan.Clone(); }
            set
            {
                var c = value.Clone(); c.Fix();
                lock (Lock) _fan = c;
                c.Save();
                Store.Log("fan curve: " + (c.On ? "on " : "off ") + string.Join(" ", c.T.Select((t, i) => t + "C " + c.P[i] + "%")));
                _wake.Set();
            }
        }
        readonly object _fanLock = new object();
        bool _fanManual; int _fanPwm = -1, _fanNoTemp, _fanCheck, _fanRetry; double _fanT = double.NaN; bool _fanHot; string _fanState;
        volatile bool _fanPaused;   // display off or sleeping: the EC has the fans

        public Engine(Profile p) { _profile = p.Clone(); _tele.ZoneNames = _meter.ZoneNames; }

        public Snapshot Snapshot { get { lock (Lock) return _snap; } }
        public Profile Profile { get { lock (Lock) return _profile.Clone(); } }

        public void Start()
        {
            try { Pep.ReleaseStale(); } catch { }
            // Manual mode left by an earlier run that the watchdog could not clean up: the curve retakes it if on.
            try { if (Pep.IsAdmin && Fan.Mode() == 2) { Fan.Automatic(); Store.Log("fan: manual mode left by an earlier run, handed back to the EC"); } } catch { }
            _thread = new Thread(Loop) { IsBackground = true, Name = "PowerCap engine" };
            _thread.Start();
        }

        // hardware = ASUS mode or refresh rate changed (slow calls, done only when needed)
        public void SetProfile(Profile p, bool hardware)
        {
            lock (Lock)
            {
                _profile = p.Clone();
                if (hardware) _hwDirty = true;
                Store.Save(_profile);   // saved under the lock, so a worker save can't overwrite a newer pick
            }
            Store.Log(string.Format("profile: {0} {1}{2}, epp {3}, boost {4}, {5} Hz, ASUS {6}", p.Preset, p.Mode,
                p.Mode == Mode.Target ? " " + p.TargetW.ToString("0.0") + " W" : p.Mode == Mode.Manual ? " caps " + p.PerfMhz + "/" + p.PrimeMhz : "", p.Epp, p.Boost, p.Hz == 0 ? "max" : p.Hz.ToString(), p.Asus));
            Safety.MarkPending(p.Preset + " " + p.Mode);
            _wake.Set();
        }

        void Loop()
        {
            while (!_stop)
            {
                try { Tick(); } catch (Exception e) { Store.Log("engine: " + e.Message); }
                // 1 s while something needs closed-loop control or someone is watching; else 3 s (fewer wakeups).
                _wake.WaitOne(_fast ? 1000 : 3000);
            }
        }

        // POWERCAP_DEBUG=1: log where each tick's time goes (wall ms, averaged over 30 ticks).
        static readonly bool DebugTick = Environment.GetEnvironmentVariable("POWERCAP_DEBUG") == "1";
        readonly System.Diagnostics.Stopwatch _tsw = new System.Diagnostics.Stopwatch();
        readonly double[] _tms = new double[7]; int _tn;
        void Mark(int i) { if (!DebugTick) return; _tms[i] += _tsw.Elapsed.TotalMilliseconds; _tsw.Restart(); }

        void Tick()
        {
            if (DebugTick) _tsw.Restart();
            Profile p; bool hw;
            lock (Lock) { p = _profile.Clone(); hw = _hwDirty; _hwDirty = false; }
            // GetSystemPowerStatus is cheap, so unplugging takes effect on the next tick (the battery read is slower).
            bool quiet = _quiet && !_draining && p.Mode != Mode.Default && System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
            if (quiet) { p.EffOnly = true; p.Boost = 0; p.Epp = Math.Max(p.Epp, 90); }
            QuietActive = quiet;
            bool fanOn = _fan.On;
            bool fanLive = fanOn && !_fanPaused;   // the curve needs a fresh chip temperature (every other tick while far below it)
            bool slow = Foreground || (fanLive && _fanTempTick-- <= 0) || _slowTick-- <= 0;
            if (slow) _slowTick = 9;
            var r = _meter.Sample(slow, Foreground);
            Mark(0);

            // Fn+F (or MyASUS) changed the fan mode: adopt it instead of fighting it.
            string hot = Hardware.AsusHotkeyMode();
            if (hot != null && hot != _hotkeyAsus)
            {
                if (_hotkeyAsus != null && hot != p.Asus)
                {
                    lock (Lock) { _profile.Asus = hot; Store.Save(_profile); }
                    p.Asus = hot; hw = true;   // hw: re-read the fan mode's SoC limit (SetAsus repeats it, harmless)
                    Store.Log("fan mode changed outside Simmer (Fn+F): " + hot);
                }
                _hotkeyAsus = hot;
            }

            if (hw)
            {
                Hardware.SetAsus(p.Asus);   // refresh rate is left to Windows
            }
            Mark(1);

            // Someone picked another plan (Control Panel, another tool): treat it as "Windows default". Only once it has
            // held for 20 s: Qualcomm's qcppte service swaps the plan away and back several times after login.
            var active = Plan.Active();
            bool foreign = p.Mode != Mode.Default && _weActivated && active != Plan.Guid;
            if (!foreign) _foreignSince = DateTime.MinValue;
            else if (_foreignSince == DateTime.MinValue) { _foreignSince = DateTime.UtcNow; Store.Log("power plan switched away from Simmer; stepping aside if it stays for 20 s"); }
            if (foreign && (DateTime.UtcNow - _foreignSince).TotalSeconds >= 20)
            {
                _foreignSince = DateTime.MinValue;
                lock (Lock)
                {
                    p = Profile.ForPreset("default"); p.Asus = _profile.Asus; p.Hz = _profile.Hz;
                    _profile = p.Clone();
                    Store.Save(_profile);
                }
                _weActivated = false; _last = null;
                var n = Notice; if (n != null) n("Another power plan was selected, so Simmer stepped aside (Windows default).");
            }

            Mark(2);
            if (_batteryTick-- <= 0)
            {
                _batteryTick = Foreground ? 10 : 20;   // hidden: ~60 s at the 3 s background tick
                int pct; bool ac; double full, dis;
                Battery.Read(out pct, out ac, out full, out dis);
                _batPct = pct; _ac = ac; _fullWh = full; _dischargeW = dis;
                _chargeDirty = true;
            }
            if (_chargeDirty) { _chargeDirty = false; ChargeTick(); }
            DrainTick();

            Mark(3);
            Caps want = null;
            bool modeChanged = _appliedProfile == null || _appliedProfile.Mode != p.Mode || Math.Abs(_appliedProfile.TargetW - p.TargetW) > 0.01 || _appliedProfile.EffOnly != p.EffOnly;
            _gov.EffOnly = p.EffOnly;
            int het = p.EffOnly ? 3 : 2;
            if (modeChanged) _gov.Reset();
            switch (p.Mode)
            {
                case Mode.Default:
                    if (active == Plan.Guid) Plan.ActivateBalanced();
                    _weActivated = false; _last = null; Plan.Forget();
                    break;
                case Mode.Manual:
                    want = new Caps { Mhz = new[] { p.PerfMhz, p.PrimeMhz, p.PrimeMhz }, Epp = p.Epp, Boost = p.Boost, Hetero = het };
                    break;
                case Mode.Target:
                    // First tick after a change has no valid reading yet: start from the feed-forward caps.
                    var c = r.Valid ? _gov.Step(p.TargetW, r.Cpu, _ac) : (_last == null ? Governor.CapsFor(p.TargetW, p.EffOnly) : new[] { _last.Mhz[0], _last.Mhz[1] });
                    if (!r.Valid && _last == null) _gov.Commanded = p.TargetW;
                    want = new Caps { Mhz = new[] { c[0], c[1], c[1] }, Epp = p.Epp, Boost = p.Boost, Hetero = het };
                    break;
            }
            if (want != null)
            {
                want = Plan.Clamp(want);
                if (_last == null || !Close(want, _last) || active != Plan.Guid)
                {
                    Plan.Apply(want);
                    _last = want; _weActivated = true;
                }
            }
            _appliedProfile = p;
            Mark(4);
            SocLimit(p, hw, r);
            FanTick(r);
            Mark(5);

            var s = new Snapshot
            {
                R = r, Applied = p.Mode == Mode.Default ? null : _last, Profile = p, Commanded = _gov.Commanded,
                BatteryPct = (int)_batPct, OnAC = _ac, FullWh = _fullWh,
                PlanActive = Plan.Active() == Plan.Guid, PlanName = Plan.ActiveName, Hz = 0,   // refresh rate is no longer managed
                SocLimitW = _socEff, SocFirmwareW = _socFw, SocProblem = p.SocW > 0 || p.GpuW > 0 ? Pep.Problem : null, SocHeldW = _socHeld,
                FanOn = fanOn, FanPwm = _fanManual ? _fanPwm : -1, FanCurveC = _fanT, FanState = _fanState,
                ChargeFw = _chargeFw, ChargeLimit = _charge, ChargeProblem = _chargeProblem, Draining = _draining
            };
            _tele.Add(r, p, s.Applied, _gov.Commanded, _ac, _batPct, s.Hz, _socEff);
            // The curve ticks every second only while it matters: fans running, or the chip within 6 C of the first point.
            bool fanFast = fanLive && ((_fanManual && _fanPwm != 0) || double.IsNaN(r.ChipC) || r.ChipC >= _fan.T[0] - 6);
            bool fanCool = !fanFast && r.ChipC < _fan.T[0] - 10;
            if (slow) _fanTempTick = fanCool ? 1 : 0;
            _fast = Foreground || p.Mode == Mode.Target || p.GpuW > 0 || hw || fanFast;
            Mark(6);
            if (DebugTick && ++_tn == 30)
            {
                Store.Log(string.Format("tick ms: sample {0:0.00} hotkey {1:0.00} plan {2:0.00} battery {3:0.00} governor {4:0.00} soc {5:0.00} snapshot {6:0.00}",
                    _tms[0] / 30, _tms[1] / 30, _tms[2] / 30, _tms[3] / 30, _tms[4] / 30, _tms[5] / 30, _tms[6] / 30));
                Array.Clear(_tms, 0, 7); _tn = 0;
            }
            double drawW = r.Valid && r.System > 0.5 ? r.System : _dischargeW;
            if (!_ac && _batPct >= 0 && _fullWh > 0 && drawW > 0.5) s.MinutesLeft = (int)(_fullWh * _batPct / 100.0 / drawW * 60);
            lock (Lock)
            {
                _snap = s;
                if (r.Valid)
                {
                    Array.Copy(HistCpu, 1, HistCpu, 0, HistoryLen - 1); HistCpu[HistoryLen - 1] = r.Cpu;
                    Array.Copy(HistGpu, 1, HistGpu, 0, HistoryLen - 1); HistGpu[HistoryLen - 1] = r.Gpu;
                    Array.Copy(HistSys, 1, HistSys, 0, HistoryLen - 1); HistSys[HistoryLen - 1] = r.System;
                    Array.Copy(HistTarget, 1, HistTarget, 0, HistoryLen - 1); HistTarget[HistoryLen - 1] = p.Mode == Mode.Target ? p.TargetW : 0;
                    if (HistCount < HistoryLen) HistCount++;
                }
            }
        }

        // SoC power limit (Qualcomm PEP, admin only), from a fixed SoC cap and/or a GPU watt target.
        // There is no GPU-only limit in the driver, so the GPU target is held through the SoC limit:
        // limit = GPU target + what the rest of the SoC (CPU, memory, fabric) draws now, plus a slow trim
        // learned while the limit binds. The CPU keeps its own caps, so this mostly squeezes the GPU.
        // The ASUS fan mode writes the same limit and the last writer wins: the firmware value is read once
        // (release, read) and again 3 s after a fan-mode change, ours is re-written when it drifts, and a
        // limit at or above the firmware's own is not held at all (the driver rejects raising it).
        void SocLimit(Profile p, bool hw, Reading r)
        {
            if (p.SocW <= 0 && p.GpuW <= 0)
            {
                // Off: never touch the driver (each new client uses one of its few slots per boot).
                if (Pep.Holding) Pep.ReleaseLimit();
                _socEff = _socFw = _socHeld = _other = double.NaN; _gpuTrim = 0; _socKey = null;
                return;
            }
            string key = p.SocW + "/" + p.GpuW + "/" + _burst;
            bool settingsChanged = key != _socKey; _socKey = key;
            if (settingsChanged) { _gpuTrim = 0; _socHeld = double.NaN; }   // NaN: write again (the window may have changed)
            if (settingsChanged || hw) _socFailKey = null;
            if (hw) _socRecheck = 3;
            bool remeasure = double.IsNaN(_socFw) || hw || (_socRecheck > 0 && --_socRecheck == 0);

            double target = p.SocW > 0 ? p.SocW : double.MaxValue;
            if (p.GpuW > 0 && r.Valid)
            {
                double other = Math.Max(0, r.Soc - r.Gpu);
                _other = double.IsNaN(_other) ? other : 0.6 * _other + 0.4 * other;
                // While the limit binds (SoC at the held limit), nudge the trim so the GPU itself lands on target.
                if (!double.IsNaN(_socHeld) && r.Soc > _socHeld - 1.0)
                    _gpuTrim = Math.Max(-4, Math.Min(4, _gpuTrim + 0.08 * (p.GpuW - r.Gpu)));
                target = Math.Min(target, p.GpuW + _other + _gpuTrim);
            }
            else if (p.GpuW > 0 && !double.IsNaN(_other)) target = Math.Min(target, p.GpuW + _other + _gpuTrim);
            target = Math.Max(MinSocW, Math.Round(target * 2) / 2);   // 0.5 W steps

            if (remeasure)
            {
                Pep.ReleaseLimit(true);
                _socHeld = double.NaN;
                _socFw = Pep.EffectiveW();
                if (double.IsNaN(_socFw)) { _socEff = double.NaN; return; }   // not admin / driver unavailable
            }
            if (target >= _socFw - 0.25)
            {
                if (Pep.Holding) Pep.ReleaseLimit(true);
                _socHeld = double.NaN; _socEff = _socFw;
            }
            else if (double.IsNaN(_socHeld) || Math.Abs(target - _socHeld) >= 0.5 || _socTick-- <= 0)
            {
                // Periodic check (every 5 s) that the fan mode has not overwritten ours.
                bool verify = !double.IsNaN(_socHeld) && Math.Abs(target - _socHeld) < 0.5;
                _socTick = 5;
                double eff = verify ? Pep.EffectiveW() : double.NaN;
                if (!verify || double.IsNaN(eff) || Math.Abs(eff - _socHeld) > 0.25)
                {
                    if (_socFailKey != key)
                    {
                        if (Pep.Set(target, SocWindow(p))) _socHeld = target;
                        else _socFailKey = key;   // logged once by Pep; retried when the settings or fan mode change
                    }
                    eff = Pep.EffectiveW();
                }
                _socEff = eff;
            }
            if (settingsChanged && !double.IsNaN(_socFw))
                Store.Log(string.Format("SoC limit: {0}{1} -> {2} over {4:0.#} s (fan mode allows {3:0.0} W)",
                    p.SocW > 0 ? "cap " + p.SocW.ToString("0") + " W " : "", p.GpuW > 0 ? "GPU target " + p.GpuW.ToString("0.0") + " W" : "", target > 1000 ? "(waiting for a reading)" : target.ToString("0.0") + " W", _socFw, SocWindow(p) / 1e6));
        }
        const double MinSocW = 5;

        // Fan curve, once per tick (1 s while the fans run or the chip is within 6 C of the first point, else 3 s). The EC keeps the fans while the curve is off, paused, the chip is at
        // 90 C (until it is back under 80 C), there has been no temperature for 5 ticks, or after a mailbox error (retried after 30 ticks).
        void FanTick(Reading r)
        {
            Fan.Beat();
            lock (_fanLock)
            {
                var c = _fan;
                if (!c.On || _fanPaused || !Pep.IsAdmin)
                {
                    if (_fanManual) FanAuto(!c.On ? "curve off" : _fanPaused ? "display off or sleep" : "not admin");
                    _fanT = double.NaN;
                    _fanState = !c.On ? null : _fanPaused ? "paused while the display is off" : "needs Simmer running as administrator";
                    return;
                }
                double t = r.ChipC;
                if (double.IsNaN(t))
                {
                    if (++_fanNoTemp >= 5) { if (_fanManual) FanAuto("no chip temperature"); _fanState = "no chip temperature: ASUS fan mode in charge"; }
                    return;
                }
                _fanNoTemp = 0;
                if (t >= Fan.HandBackC) _fanHot = true; else if (t < Fan.RetakeC) _fanHot = false;
                if (_fanHot)
                {
                    if (_fanManual) FanAuto("chip at " + t.ToString("0") + " C");
                    _fanState = "chip over " + Fan.HandBackC + " °C: ASUS fan mode in charge until " + Fan.RetakeC + " °C";
                    return;
                }
                if (_fanRetry > 0) { _fanRetry--; return; }
                // 3 C hysteresis: rising follows the chip at once, falling waits until it is 3 C under the level in use.
                if (double.IsNaN(_fanT) || t >= _fanT) _fanT = t; else if (t < _fanT - Fan.Hyst) _fanT = t + Fan.Hyst;
                int want = FanCurve.Pwm(c.At(_fanT));
                try
                {
                    if (!_fanManual)
                    {
                        Fan.StartWatchdog();
                        Fan.Manual();
                        _fanManual = true; _fanPwm = -1; _fanCheck = 10;
                        Store.Log("fan curve: Simmer drives the fans (chip " + t.ToString("0.0") + " C)");
                    }
                    else if (--_fanCheck <= 0)
                    {
                        _fanCheck = 10;   // Fn+F or the EC itself may leave manual mode
                        if (Fan.Mode() != 2) { Store.Log("fan curve: the EC left manual mode, taking it back"); Fan.Manual(); _fanPwm = -1; }
                    }
                    int pwm = FanRamp(want, r);
                    if (pwm != _fanPwm) { Fan.SetPwm(pwm); _fanPwm = pwm; }
                    _fanState = null;
                }
                catch (Exception e)
                {
                    FanAuto("mailbox error: " + e.Message);
                    _fanRetry = 30;
                    _fanState = "fan control error, retrying: ASUS fan mode in charge";
                }
            }
        }

        // Smooth ramping: the PWM moves toward the curve at RampUp / RampDown per second instead of jumping (30 -> 255
        // in ~20 s up, ~110 s down; the slow fall keeps a steep curve from hunting on short temperature dips). A stopped
        // fan starts at PWM 30 (its lowest reliable start) and stops from there.
        // After a takeover (_fanPwm -1) the ramp starts from the speed the EC left the fans at.
        const double RampUp = 10, RampDown = 2;
        const int RampFloor = 30;
        readonly System.Diagnostics.Stopwatch _fanRampClock = System.Diagnostics.Stopwatch.StartNew();
        double _fanRampAt;
        int FanRamp(int want, Reading r)
        {
            double now = _fanRampClock.Elapsed.TotalSeconds, dt = Math.Max(0.5, Math.Min(3, now - _fanRampAt));
            _fanRampAt = now;
            int cur = _fanPwm;
            if (cur < 0)
            {
                int rpm = Math.Max(r.FanRpm[0], r.FanRpm[1]);
                cur = rpm >= 0 ? FanCurve.PwmForRpm(rpm) : want;
                if (cur > 0 && cur < RampFloor) cur = RampFloor;
            }
            if (want > cur) return cur < RampFloor ? Math.Min(want, RampFloor) : Math.Min(want, cur + (int)Math.Ceiling(RampUp * dt));
            if (want < cur)
            {
                int next = cur - (int)Math.Ceiling(RampDown * dt);
                return next < RampFloor ? (want < RampFloor ? want : RampFloor) : Math.Max(want, next);
            }
            return cur;
        }

        void FanAuto(string why)
        {
            bool ok = Fan.Automatic();
            _fanManual = !ok; _fanPwm = -1;
            Store.Log("fan curve: ASUS fan mode in charge (" + why + ")" + (ok ? "" : ", hand-back FAILED, retrying"));
        }

        // Display off / sleep (UI thread): hand the fans back now, not on the next tick.
        public void FanPause(bool paused)
        {
            _fanPaused = paused;
            lock (_fanLock)
            {
                if (paused && _fanManual) FanAuto("display off or sleep");
                if (!paused) _fanT = double.NaN;
            }
            if (!paused) _chargeDirty = true;   // resume / display on: check the charge limit is still in place
            _wake.Set();
        }

        // Skip rewrites for changes under 30 MHz.
        static bool Close(Caps a, Caps b)
        {
            for (int i = 0; i < 3; i++)
                if ((a.Mhz[i] == 0) != (b.Mhz[i] == 0) || Math.Abs(a.Mhz[i] - b.Mhz[i]) >= 30) return false;
            return a.Epp == b.Epp && a.Boost == b.Boost && a.MaxState == b.MaxState && a.Cores == b.Cores && a.Hetero == b.Hetero;
        }

        // On quit: in Target mode leave the feed-forward caps for the target, so the plan keeps roughly
        // the same limit without the loop running.
        public void Shutdown()
        {
            _stop = true; _wake.Set();
            bool joined = _thread == null || _thread.Join(3000);
            // Final release; Pep refuses new requests from here on, so a tick that is still running can't re-set it.
            try { Pep.Close(); } catch { }
            _fanPaused = true;   // a stuck tick can't take the fans again
            if (Monitor.TryEnter(_fanLock, 3000))
                try { if (_fanManual || Fan.Owned) FanAuto("quit"); } finally { Monitor.Exit(_fanLock); }
            else Store.Log("fan curve: tick stuck at quit; the watchdog hands the fans back");
            if (joined && _draining) { _draining = false; string d = Drain.Stop(); Store.Log("drain: stopped at quit" + (d == null ? "" : "; " + d)); }
            Governor.Flush();
            if (joined) _tele.Flush();
            var p = Profile;
            try
            {
                if (joined && p.Mode == Mode.Target && Plan.IsActive)   // a stuck tick could still write the plan
                {
                    var c = Governor.CapsFor(p.TargetW, p.EffOnly);
                    Plan.Apply(new Caps { Mhz = new[] { c[0], c[1], c[1] }, Epp = p.Epp, Boost = p.Boost, Hetero = p.EffOnly ? 3 : 2 });
                }
            }
            catch { }
        }

        public void Dispose() { _meter.Dispose(); }
    }
}


