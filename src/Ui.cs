// The borderless tray flyout / full dashboard (Dashboard) and the system tray host (TrayApp).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PowerCap
{
    // Optional automatic AC / Battery profile slot bindings (stored in slot.bindings next to profiles.json).
    static class SlotBindings
    {
        public static int Ac = -1, Bat = -1;
        static string BindFile { get { return Path.Combine(Store.Dir, "slot.bindings"); } }

        static SlotBindings() { Load(); }

        public static void Load()
        {
            Ac = -1; Bat = -1;
            try
            {
                if (!File.Exists(BindFile)) return;
                var parts = File.ReadAllText(BindFile).Trim().Split(',');
                int a, b;
                if (parts.Length >= 1 && int.TryParse(parts[0], out a) && a >= -1 && a < Store.Slots) Ac = a;
                if (parts.Length >= 2 && int.TryParse(parts[1], out b) && b >= -1 && b < Store.Slots) Bat = b;
            }
            catch { }
        }

        public static void Save()
        {
            Store.WriteAtomic(BindFile, Ac + "," + Bat);
        }

        public static void ClearSlot(int i)
        {
            bool ch = false;
            if (Ac == i) { Ac = -1; ch = true; }
            if (Bat == i) { Bat = -1; ch = true; }
            if (ch) Save();
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly Engine _engine;
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        readonly SynchronizationContext _ui;
        Dashboard _form;
        DateTime _lastChange = DateTime.MinValue;
        bool _pending = Safety.IsPending, _offing;
        bool _recovering;
        System.Threading.Timer _fanResume;   // started after a revert: no AC/battery auto-profile until the user applies one
        bool? _lastAc;
        PowerWatch _power;

        // 3-minute chip temperature ring buffer synchronized with Engine's snapshots (for optional + °C chart overlay).
        readonly double[] _histChipC = new double[Engine.HistoryLen];
        int _histChipCount;
        Snapshot _lastHistSnap;
        readonly object _histLock = new object();

        public TrayApp(Profile p, bool showWindow, string notice, EventWaitHandle show)
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _engine = new Engine(p);
            _recovering = notice != null;
            _engine.Notice += msg => _ui.Post(_ => Balloon(msg), null);
            // Re-assert the saved profile at start (ASUS mode and refresh are not remembered by Windows/firmware).
            if (p.Mode != Mode.Default || notice == null) { _engine.SetProfile(p, true); _lastChange = DateTime.Now; _pending = true; }
            _engine.Start();
            _lastAc = CurrentAc();
            _power = new PowerWatch(paused => _engine.FanPause(paused), () => _ui.Post(_ => CheckPowerLine(), null));
            // Restart / shut down / sign out: hand the fans back to the EC now, since the process may be killed before
            // its exit handlers run. If the shutdown is cancelled the curve takes them back after 30 s.
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) =>
            {
                _engine.FanPause(true);
                Store.Log("Windows is ending the session: fans handed back to the EC");
                _fanResume = new System.Threading.Timer(_ => _engine.FanPause(false), null, 30000, Timeout.Infinite);
            };
            // Start with Windows follows the ★ profile (no separate toggle): bring an older setup in line once per launch.
            ThreadPool.QueueUserWorkItem(_ => { Startup.Migrate(); Startup.Sync(); });

            BuildMenu();
            _menu.Opening += (s, e) => BuildMenu();   // rebuilt each time: checks and saved profiles stay current
            _tray.ContextMenuStrip = _menu;
            _tray.Icon = Dashboard.AppIcon();
            _tray.Text = "Simmer";
            _tray.MouseMove += (s, e) => UpdateTip();
            _tray.Visible = true;
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (_form != null && !_form.IsDisposed)
                {
                    if (_form.Visible && _form.WindowState != FormWindowState.Minimized)
                    {
                        if (Form.ActiveForm == _form) { _form.Hide(); return; }
                        ShowWindow(true);
                        return;
                    }
                }
                ShowWindow(true);
            };
            _timer.Tick += (s, e) => OnTimer();
            _timer.Start();

            // Second instance asks us to show the window.
            ThreadPool.RegisterWaitForSingleObject(show, (st, to) => _ui.Post(_ => ShowWindow(false), null), null, -1, false);
            // "Simmer.exe --quit" asks us to exit cleanly (releases the SoC limit, leaves the plan as on Quit).
            ThreadPool.RegisterWaitForSingleObject(new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PowerCapV2.Quit"), (st, to) => _ui.Post(_ => Quit(), null), null, -1, true);

            if (showWindow) ShowWindow(false);
            if (notice != null) Balloon(notice);
        }

        public Engine Engine { get { return _engine; } }

        static bool? CurrentAc()
        {
            var st = SystemInformation.PowerStatus.PowerLineStatus;
            return st == PowerLineStatus.Online ? (bool?)true : st == PowerLineStatus.Offline ? (bool?)false : null;
        }

        void CheckPowerLine()
        {
            var ac = CurrentAc();
            if (!ac.HasValue) return;
            if (!_lastAc.HasValue) { _lastAc = ac; return; }
            if (ac.Value == _lastAc.Value) return;
            _lastAc = ac;
            if (_offing) return;
            int idx = ac.Value ? SlotBindings.Ac : SlotBindings.Bat;
            if (idx < 0 || idx >= Store.Slots) return;
            var slots = Store.LoadSlots();
            var sp = slots[idx];
            if (sp == null) return;
            if (_recovering) { Store.Log("auto slot skipped: recovering from a revert"); return; }
            var cur = _engine.Profile;
            Apply(sp.Clone(), sp.Asus != cur.Asus);
            Store.Log("auto slot (" + (ac.Value ? "AC" : "battery") + "): Profile " + (idx + 1));
        }

        void RecordChipTemp(Snapshot s)
        {
            if (s == null || ReferenceEquals(s, _lastHistSnap) || s.R == null || !s.R.Valid) return;
            _lastHistSnap = s;
            lock (_histLock)
            {
                Array.Copy(_histChipC, 1, _histChipC, 0, Engine.HistoryLen - 1);
                _histChipC[Engine.HistoryLen - 1] = s.R.ChipC;
                if (_histChipCount < Engine.HistoryLen) _histChipCount++;
            }
        }

        public void CopyChipHistory(out double[] chipC, out int count)
        {
            lock (_histLock) { chipC = (double[])_histChipC.Clone(); count = _histChipCount; }
        }

        public void ShowFanCurve()
        {
            ShowWindow(false);
            if (_form != null && !_form.IsDisposed) _form.OpenFanCurveTab();
        }

        // "Windows default" (reset button, --off) also hands the fans back to the ASUS fan mode.
        public void FanCurveOff() { var c = _engine.FanCurve; if (c.On) { c.On = false; _engine.FanCurve = c; } }
        public void SlotsChanged() { }   // tray menu is rebuilt on open

        public void ConfirmNow()
        {
            Safety.Confirm();
            _pending = false;
        }

        void Balloon(string msg)
        {
            _tray.BalloonTipTitle = "Simmer";
            _tray.BalloonTipText = msg;
            _tray.ShowBalloonTip(8000);
            Store.Log("notice: " + msg);
        }

        void BuildMenu()
        {
            _menu.Items.Clear();
            _menu.Items.Add("Open Simmer", null, (s, e) => ShowWindow(false)).Font = new Font(_menu.Font, FontStyle.Bold);
            _menu.Items.Add(new ToolStripSeparator());
            var cur = _engine.Profile.Preset;
            foreach (var name in Profile.PresetOrder)
            {
                var n = name;
                var it = new ToolStripMenuItem(Profile.PresetLabel(n), null, (s, e) => Apply(Profile.Keeping(n, _engine.Profile), true)) { Checked = cur == n };
                _menu.Items.Add(it);
            }
            var slots = Store.LoadSlots();
            if (slots.Any(x => x != null))
            {
                _menu.Items.Add(new ToolStripSeparator());
                int def = Store.DefaultSlot;
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] != null)
                    {
                        var sp = slots[i];
                        var tags = new List<string>();
                        if (i == def) tags.Add("startup");
                        if (i == SlotBindings.Ac) tags.Add("AC");
                        if (i == SlotBindings.Bat) tags.Add("battery");
                        string tagStr = tags.Count > 0 ? "[" + string.Join(", ", tags) + "] " : "";
                        _menu.Items.Add(new ToolStripMenuItem("Profile " + (i + 1) + "  " + tagStr + "(" + Profile.Summary(sp) + ")", null,
                            (s, e) => Apply(sp.Clone(), true)) { Checked = cur == sp.Preset });
                    }
            }
            _menu.Items.Add(new ToolStripSeparator());
            var fc = _engine.FanCurve;
            _menu.Items.Add(new ToolStripMenuItem("Fan curve", null, (s, e) => ShowFanCurve()) { Checked = fc.On });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, (s, e) => Quit());
        }

        public void Apply(Profile p, bool hardware)
        {
            _recovering = false;
            _engine.SetProfile(p, hardware);
            _lastChange = DateTime.Now; _pending = true;
            if (_form != null && !_form.IsDisposed) _form.SyncFromProfile(p);
        }

        public int SecondsUntilConfirmed
        {
            get
            {
                if (!_pending) return 0;
                return Math.Max(0, Safety.ConfirmSeconds - (int)(DateTime.Now - _lastChange).TotalSeconds);
            }
        }

        void OnTimer()
        {
            if (_pending && (DateTime.Now - _lastChange).TotalSeconds >= Safety.ConfirmSeconds) { Safety.Confirm(); _pending = false; }
            if (File.Exists(Elevation.QuitFlag)) { try { File.Delete(Elevation.QuitFlag); } catch { } Quit(); return; }
            // --off from another process: reset through the engine, and clear the flag (= done) once it has applied.
            if (!_offing && File.Exists(Elevation.OffFlag)) { _offing = true; FanCurveOff(); Apply(Profile.ForPreset("default"), true); Store.Log("--off requested"); }
            else if (_offing) { var sp = _engine.Snapshot.Profile; if (sp != null && sp.Mode == Mode.Default && !Pep.Holding) { _offing = false; Safety.Confirm(); _pending = false; try { File.Delete(Elevation.OffFlag); } catch { } } }
            CheckPowerLine();
            var snap = _engine.Snapshot;
            RecordChipTemp(snap);
            if (_form != null && _form.Visible) _form.Tick(snap);
        }

        // Tray tooltip, refreshed when the pointer is over the icon (63-char .NET Framework NotifyIcon limit).
        void UpdateTip()
        {
            var s = _engine.Snapshot;
            var p = s.Profile;
            string head;
            if (p == null || p.Mode == Mode.Default) head = "Windows default";
            else
            {
                string detail = p.SocW > 0 ? p.SocW.ToString("0.#") + "W SoC"
                    : p.Mode == Mode.Target ? p.TargetW.ToString("0.#") + "W"
                    : (p.PerfMhz == 0 && p.PrimeMhz == 0 ? "free" : "clocks");
                head = Profile.PresetLabel(p.Preset) + " · " + detail;
            }
            string t = string.Format("{0}\n{1:0.0}W laptop · CPU {2:0.0}W", head, s.R.System, s.R.Cpu)
                + (double.IsNaN(s.R.ChipC) ? "" : " · " + s.R.ChipC.ToString("0") + "°C");
            if (s.BatteryPct >= 0)
                t += "\n" + s.BatteryPct + "%" + (s.OnAC ? " AC" : s.MinutesLeft > 0 ? " · " + Dashboard.Hm(s.MinutesLeft) : "");
            if (t.Length > 63) t = t.Substring(0, 63);
            if (t != _tray.Text) _tray.Text = t;
        }

        public void ShowWindow(bool fromTray = false)
        {
            bool fresh = _form == null || _form.IsDisposed;
            if (fresh) _form = new Dashboard(this);
            _form.SyncFromProfile(_engine.Profile);
            var snap = _engine.Snapshot;
            RecordChipTemp(snap);
            _form.Tick(snap);
            // Create the window first: that applies the display scaling and sets the real size, so the position
            // below is computed for the size it will actually have (it used to grow past the screen edge after).
            if (!_form.IsHandleCreated) { var h = _form.Handle; }
            if (fresh || (!_form.Visible && !_form.UserMoved)) _form.PositionNearTray(fromTray);
            else _form.KeepOnScreen();
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        // Restarts PowerCap elevated (UAC) so the SoC limit can reach the Qualcomm driver.
        public void Elevate()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--elevated") { UseShellExecute = true, Verb = "runas" });
            }
            catch (System.ComponentModel.Win32Exception) { return; }   // UAC declined
            Quit();
        }

        public void Quit()
        {
            _timer.Stop();
            _engine.Shutdown();
            if (_power != null) _power.Dispose();
            Safety.Confirm();   // a clean quit: nothing crashed, so the next start needn't revert
            _tray.Visible = false;
            _tray.Dispose();
            if (_form != null) { _form.Quitting = true; _form.Close(); }
            ExitThread();
        }
    }

    static class Startup
    {
        public static string LinkPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Simmer.lnk"); } }
        static string Exe { get { return Application.ExecutablePath; } }
        // Elevated: a logon task (no UAC at login); otherwise the Startup shortcut. Cached: schtasks is slow.
        static bool? _enabled;
        public static bool Enabled { get { if (_enabled == null) _enabled = File.Exists(LinkPath) || Elevation.Exists(Elevation.LogonTask); return _enabled.Value; } }

        static object Shell() { return Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); }
        static object Call(object o, string m, params object[] a) { return o.GetType().InvokeMember(m, System.Reflection.BindingFlags.InvokeMethod, null, o, a); }
        static object Get(object o, string p) { return o.GetType().InvokeMember(p, System.Reflection.BindingFlags.GetProperty, null, o, null); }
        static void Set(object o, string p, object v) { o.GetType().InvokeMember(p, System.Reflection.BindingFlags.SetProperty, null, o, new[] { v }); }

        // Start with Windows exactly when a ★ startup profile is set.
        public static void Sync()
        {
            bool want = Store.DefaultSlot >= 0 && Store.LoadSlots()[Store.DefaultSlot] != null;
            if (want != Enabled) { Enable(want); Store.Log("start with Windows: " + (want ? "on (Profile " + (Store.DefaultSlot + 1) + ")" : "off (no startup profile)")); }
        }

        public static void Enable(bool on)
        {
            try
            {
                if (Pep.IsAdmin)
                {
                    if (on && Elevation.Create(Elevation.LogonTask, true, "--hidden --from-task")) { File.Delete(LinkPath); return; }
                    if (!on) Elevation.Delete(Elevation.LogonTask);
                }
                if (!on) { File.Delete(LinkPath); return; }
                var lnk = Call(Shell(), "CreateShortcut", LinkPath);
                Set(lnk, "TargetPath", Exe);
                Set(lnk, "Arguments", "--hidden");
                Set(lnk, "WorkingDirectory", Path.GetDirectoryName(Exe));
                Set(lnk, "Description", "Simmer (tray)");
                Call(lnk, "Save");
            }
            catch (Exception e) { Store.Log("startup shortcut: " + e.Message); }
            finally
            {
                // Re-read what is really registered: an unelevated run cannot remove the elevated logon task.
                _enabled = null;
                if (Enabled != on) Store.Log("start with Windows: could not turn " + (on ? "on" : "off") + (Pep.IsAdmin ? "" : " without administrator rights"));
            }
        }

        // An existing Startup entry (v1 launched pwsh powercap-ui.ps1) is pointed at this exe.
        public static void Migrate()
        {
            try
            {
                Elevation.Install();
                // the Startup shortcut from before the rename (Sync makes the new one when a ★ profile is set)
                var old = Path.Combine(Path.GetDirectoryName(LinkPath), "PowerCap.lnk");
                if (File.Exists(old)) { File.Delete(old); _enabled = null; Store.Log("removed the old PowerCap startup shortcut"); }
                if (Pep.IsAdmin && File.Exists(LinkPath)) { Enable(true); Store.Log("startup now uses the elevated logon task"); return; }
                if (!Enabled || !File.Exists(LinkPath)) return;
                var lnk = Call(Shell(), "CreateShortcut", LinkPath);
                var target = (string)Get(lnk, "TargetPath");
                if (!string.Equals(target, Exe, StringComparison.OrdinalIgnoreCase) || (string)Get(lnk, "Arguments") != "--hidden")
                {
                    Enable(true);
                    Store.Log("startup shortcut now launches " + Exe);
                }
            }
            catch (Exception e) { Store.Log("startup migrate: " + e.Message); }
        }
    }

    sealed class Dashboard : Form
    {
        // Logical dimensions: Simple view sits fixed at (0,0) inside Full view so the 260 ms 120 Hz grow animation needs zero relayout per frame.
        const float SimpleW = 448, SimpleH = 340, FullW = 904, FullH = 544;
        float LW { get { return _simple ? SimpleW : FullW; } }
        float LH { get { return _simple ? SimpleH : FullH; } }

        static readonly string ViewFile = Path.Combine(Store.Dir, "simple.view");
        bool _simple = File.Exists(ViewFile);
        bool _trayAnchored = true;
        public bool UserMoved { get; private set; }
        bool _dwmFramed;
        int _rightTab;          // 0 = CPU Tuning, 1 = Fan Curve
        bool _showTempLine;     // optional Chip °C overlay on the 3-minute history chart

        // Handheld-style SoC (CPU + GPU) watt tiles; 0 = Free (ASUS fan mode firmware PL1 decides). Wheel steps ±1 W.
        static readonly double[] SocTileW = { 5, 8, 13, 17, 22, 0 };
        static readonly string[] SocTileBase = { "5 W", "8 W", "13 W", "17 W", "22 W", "Free" };
        const double GpuOff = 25;   // slider right end = off
        const double Fabric = 1.5;  // memory + fabric + NPU idle share of the SoC budget, W

        double SplitBudget(Profile p)
        {
            double b = p.SocW > 0 ? p.SocW : !double.IsNaN(_snap.SocFirmwareW) ? _snap.SocFirmwareW : 22;
            return Math.Max(3, b - Fabric);
        }

        double SplitCpuW(Profile p, int cpuShare)
        {
            return Math.Max(2.5, Math.Min(30, Math.Round(SplitBudget(p) * cpuShare / 100.0 * 2) / 2));
        }

        void ApplySplit(Profile p)
        {
            if (p.CpuShare == 0) p.CpuShare = (int)Math.Round(_split.Value);
            p.GpuW = 0; p.Preset = "custom";
            if (p.CpuShare < 0) { p.Mode = Mode.Manual; p.PerfMhz = p.PrimeMhz = 0; return; }   // auto: no CPU cap
            p.Mode = Mode.Target;
            p.TargetW = SplitCpuW(p, p.CpuShare);
        }

        readonly TrayApp _app;
        Fonts _f;
        float _s = 1;
        Snapshot _snap = new Snapshot();
        Profile _p;
        public bool Quitting;
        readonly List<Widget> _w = new List<Widget>();
        Widget _hover, _capture;
        readonly ToolTip _tip;
        string _lastTip;

        // Header & Left Column widgets (Simple + Full)
        readonly LinkButton _trial, _min, _close, _view, _elevate, _splitAuto;
        readonly Segmented _slots, _socTiles, _asus;
        readonly Slider _split;
        readonly SplitCurveButton _fanSplit;
        readonly Toggle _quiet;
        Profile[] _slotData = Store.LoadSlots();

        // Full View widgets (Right Column + Bottom Chart)
        readonly Segmented _rightTabs, _mode, _boost;
        readonly Slider _target, _perf, _prime, _gpu, _epp;
        readonly Toggle _effOnly;
        // Charge limit box on the battery line: type 50-100 and Enter; empty hands it back to ASUS.
        readonly TextBox _charge; bool _chargeCancel, _chargeEdited; Font _chargeFont;
        readonly ToolTip _chargeTip = new ToolTip();

        void CommitCharge()
        {
            string t = _charge.Text.Trim(); int v;
            int want = t.Length == 0 ? 0 : int.TryParse(t, out v) ? Math.Max(Charge.Min, Math.Min(Charge.Max, v)) : _app.Engine.ChargeLimit;
            if (want != _app.Engine.ChargeLimit) { _app.Engine.ChargeLimit = want; _chargeWant = want; _chargeAt = DateTime.UtcNow; }
        }
        // After a commit the pill shows "…" until the firmware reads back the new limit, then "✓ Set" (or "failed").
        int _chargeWant = -1; DateTime _chargeAt;
        void InvalidateCharge() { Invalidate(Rectangle.Ceiling(R(20, 104, 162, 28))); }
        string ChargeStatus(Snapshot s, out Color c)
        {
            c = Theme.Muted;
            if (_chargeEdited) return "Enter";
            if (_chargeWant < 0 || (DateTime.UtcNow - _chargeAt).TotalSeconds > 8) return s.Draining ? "↓ drain" : null;
            if (s.ChargeProblem != null) { c = Theme.Amber; return "failed"; }
            if (s.ChargeLimit == _chargeWant && (_chargeWant == 0 || s.ChargeFw == _chargeWant)) { c = Theme.Green; return "✓ Set"; }
            return "…";
        }
        // PowerCap's limit in the accent colour; the firmware's own (ASUS) limit dimmed; amber when it isn't in place.
        Color ChargeColor(Snapshot s) { return s.ChargeProblem != null ? Theme.Amber : s.ChargeLimit > 0 ? Theme.Accent : Theme.Muted; }
        void ChargeText(Snapshot s)
        {
            if (_chargeEdited || s == null) return;
            int v = s.ChargeLimit > 0 ? s.ChargeLimit : s.ChargeFw;
            string t = v > 0 ? v.ToString() : "";
            if (_charge.Text != t) _charge.Text = t;
            _charge.ForeColor = ChargeColor(s);
            string tip = s.ChargeProblem != null ? "Charge limit not in place: " + s.ChargeProblem : s.Draining ? "Above the limit: draining to " + s.ChargeLimit + " % with the charger connected" : s.ChargeLimit > 0 ? "Charging stops at " + s.ChargeLimit + " % (Simmer keeps it; drains down to it when above)" : "Charge limit set by ASUS; type " + Charge.Min + "-" + Charge.Max + " to set your own";
            if (_chargeTip.GetToolTip(_charge) != tip) _chargeTip.SetToolTip(_charge, tip);
        }
        // Burst window box beside Quiet on battery: seconds the chip cap averages over (1-15); empty = default 15 s.
        readonly TextBox _burst; bool _burstCancel, _burstEdited; int _burstWant = -1; DateTime _burstAt;
        void CommitBurst()
        {
            string t = _burst.Text.Trim(); int v;
            int want = t.Length == 0 ? 0 : int.TryParse(t, out v) ? Math.Max(Engine.BurstMin, Math.Min(Engine.BurstMax, v)) : _app.Engine.BurstWindow;
            if (want != _app.Engine.BurstWindow) { _app.Engine.BurstWindow = want; _burstWant = want; _burstAt = DateTime.UtcNow; }
        }
        void InvalidateBurst() { Invalidate(Rectangle.Ceiling(R(180, 288, 150, 32))); }
        string BurstStatus(out Color c)
        {
            c = Theme.Muted;
            if (_burstEdited) return "Enter";
            if (_burstWant >= 0 && (DateTime.UtcNow - _burstAt).TotalSeconds <= 4) { c = Theme.Green; return "✓ Set"; }
            return null;
        }
        void BurstText()
        {
            if (_burstEdited) return;
            int v = _app.Engine.BurstWindow;
            string t = (v > 0 ? v : 15).ToString();
            if (_burst.Text != t) _burst.Text = t;
            _burst.ForeColor = v > 0 ? Theme.Accent : Theme.Muted;
            string tip = "Burst window: the chip cap averages power over this many seconds (" + Engine.BurstMin + "-" + Engine.BurstMax + ").\nShorter holds the cap harder with less burst headroom; empty = default 15 s (2 s with a GPU target).";
            if (_chargeTip.GetToolTip(_burst) != tip) _chargeTip.SetToolTip(_burst, tip);
        }
        readonly CurveEditor _curveEdit;
        readonly LinkButton _reset, _curveDefault, _curveSilent, _tempLineBtn;
        readonly bool _admin = Pep.IsAdmin;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("user32.dll")] static extern int GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);

        // Borderless, but with the minimize style so the taskbar button minimizes and restores it.
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.Style |= 0x00020000; return cp; } }   // WS_MINIMIZEBOX

        public Dashboard(TrayApp app)
        {
            _app = app;
            Text = "Simmer";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            KeyPreview = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Icon = AppIcon();

            _tip = new ToolTip { InitialDelay = 360, ReshowDelay = 120, AutoPopDelay = 8000, OwnerDraw = true, BackColor = Theme.CardHi, ForeColor = Theme.Text };
            _tip.Popup += OnTipPopup;
            _tip.Draw += OnTipDraw;

            // Header widgets
            _trial = Add(new LinkButton
            {
                Label = "",
                Color = Theme.Amber,
                Active = true,
                TinyFont = true,
                Tip = "Click to confirm and keep current profile now",
                Click = () => { _app.ConfirmNow(); UpdateTrialBadge(); Invalidate(); }
            });
            _slots = Add(new Segmented
            {
                Labels = new string[Store.Slots],
                ItemTips = new string[Store.Slots],
                Dots = new Color?[Store.Slots],
                SecondDots = new Color?[Store.Slots],
                Compact = true,
                TinyText = true,
                Accent = Theme.Green
            });
            _slots.Changed = i =>
            {
                if (_slotData[i] == null) { SaveSlot(i); return; }
                var p = _slotData[i].Clone();
                var cur = _app.Engine.Profile;
                _app.Apply(p, p.Asus != cur.Asus);
            };
            // Window buttons, top right: minimize to the taskbar, close to the tray (Exit is in the tray menu)
            _min = Add(new LinkButton { Color = Theme.Muted, IconPaint = PaintMinIcon, Tip = "Minimize", Click = () => WindowState = FormWindowState.Minimized });
            _close = Add(new LinkButton { Color = Theme.Muted, IconPaint = PaintCloseIcon, Tip = "Close to the tray (Exit is in the tray menu)", Click = Hide });
            _view = Add(new LinkButton { Label = "", Color = Theme.Accent, Click = () => SetView(!_simple) });

            // Left Column: Primary Controls Card
            _elevate = Add(new LinkButton
            {
                Label = "Restart as admin",
                Color = Theme.Pink,
                Active = true,
                TinyFont = true,
                Tip = "Relaunch elevated so Qualcomm PEP SoC/GPU caps and EC fan control work",
                Click = () => _app.Elevate()
            });
            _socTiles = Add(new Segmented
            {
                Labels = (string[])SocTileBase.Clone(),
                Accent = Theme.Pink,
                Tip = _admin ? "Click to set SoC watt cap · scroll mouse wheel for ±1 W exact cap" : "Requires administrator (click Restart as admin)"
            });
            _socTiles.Changed = i =>
            {
                var p = _app.Engine.Profile;
                p.SocW = SocTileW[i];
                if (_simple || p.CpuShare != 0) ApplySplit(p);
                _app.Apply(p, false);
            };
            _socTiles.OnWheel = delta =>
            {
                if (!_admin) return false;
                var p = _app.Engine.Profile;
                int step = Math.Sign(delta);
                if (step == 0) return false;
                double next;
                if (p.SocW <= 0)
                {
                    if (step > 0) return false;
                    int fw = !double.IsNaN(_snap.SocFirmwareW) ? Math.Min(40, Math.Max(6, (int)Math.Round(_snap.SocFirmwareW))) : 23;
                    next = Math.Max(5, Math.Min(39, fw - 1));
                }
                else
                {
                    int raw = (int)Math.Round(p.SocW) + step;
                    next = raw >= 40 ? 0 : Math.Max(5, raw);
                }
                if (Math.Abs(next - p.SocW) < 0.05) return false;
                p.SocW = next;
                if (_simple || p.CpuShare != 0) ApplySplit(p);
                _app.Apply(p, false);
                return true;
            };

            _splitAuto = Add(new LinkButton
            {
                Label = "Auto",
                Color = Theme.Accent,
                TinyFont = true,
                Tip = "Auto split: CPU uncapped, shares SoC budget freely with GPU"
            });
            _splitAuto.Click = () =>
            {
                var p = _app.Engine.Profile;
                p.CpuShare = p.CpuShare < 0 ? (int)(Math.Round(_split.Value / 5.0) * 5) : -1;
                ApplySplit(p);
                _app.Apply(p, false);
            };
            // Inline CPU/GPU balance bar: right = more CPU (Value = CpuShare 10..90%, left blue = CPU, right green = GPU).
            _split = Add(new Slider
            {
                Inline = true,
                Min = 10,
                Max = 90,
                Step = 5,
                Value = 65,
                Accent = Theme.Accent,
                Rest = Theme.Green,
                Tip = "Drag right for more CPU watts, left for more GPU headroom"
            });
            _split.Changed = v => Invalidate(Rectangle.Ceiling(R(CtlCard.X, 214, CtlCard.Width, 34)));
            _split.Committed = v =>
            {
                var p = _app.Engine.Profile;
                p.CpuShare = (int)Math.Round(v);
                ApplySplit(p);
                _app.Apply(p, false);
            };

            _asus = Add(new Segmented
            {
                Labels = new[] { "Whisper", "Standard", "Performance" },
                Compact = true,
                Accent = Theme.Purple,
                Tip = "ASUS firmware thermal & PL1 ceiling profile"
            });
            _asus.Changed = i => Change(p => p.Asus = Hardware.AsusNames[i], true);

            _fanSplit = Add(new SplitCurveButton
            {
                ToggleTip = "1-click toggle Simmer custom fan curve on/off",
                OpenTip = "Open 5-point fan curve editor"
            });
            _fanSplit.Toggled = () =>
            {
                var c = _app.Engine.FanCurve;
                c.On = !c.On;
                _app.Engine.FanCurve = c;
                SyncFanUi();
                Invalidate();
            };
            _fanSplit.Opened = () =>
            {
                if (_simple)
                {
                    _rightTab = 1;
                    _rightTabs.Selected = 1;
                    SetView(false);
                }
                else SetRightTab(_rightTab == 1 ? 0 : 1);
            };

            _quiet = Add(new Toggle
            {
                Label = "Quiet on battery",
                Compact = true,
                On = _app.Engine.QuietOnBattery,
                Tip = "On battery: run on efficient cores only, no boost, EPP ≥ 90"
            });
            _quiet.Changed = on => { _app.Engine.QuietOnBattery = on; Invalidate(); };

            _charge = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.CardHi, ForeColor = Theme.Muted, TextAlign = HorizontalAlignment.Right, MaxLength = 3, TabStop = false, ShortcutsEnabled = false };
            _charge.KeyPress += (o, e) => { if (!char.IsDigit(e.KeyChar) && e.KeyChar != '') e.Handled = true; else { _chargeEdited = true; InvalidateCharge(); } };
            _charge.Enter += (o, e) => InvalidateCharge();
            _charge.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Delete) _chargeEdited = true;
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (_chargeEdited) CommitCharge(); _chargeEdited = false; ChargeText(_snap); ActiveControl = null; InvalidateCharge(); }
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _chargeCancel = true; ActiveControl = null; }
            };
            // Only typed text is committed: focus alone (or clicking away) never changes the limit.
            _charge.Leave += (o, e) => { if (_chargeEdited && !_chargeCancel) CommitCharge(); _chargeEdited = _chargeCancel = false; ChargeText(_snap); InvalidateCharge(); };
            Controls.Add(_charge);

            _burst = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.CardHi, ForeColor = Theme.Muted, TextAlign = HorizontalAlignment.Right, MaxLength = 2, TabStop = false, ShortcutsEnabled = false };
            _burst.KeyPress += (o, e) => { if (!char.IsDigit(e.KeyChar) && e.KeyChar != '\b') e.Handled = true; else { _burstEdited = true; InvalidateBurst(); } };
            _burst.Enter += (o, e) => InvalidateBurst();
            _burst.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Delete) _burstEdited = true;
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (_burstEdited) CommitBurst(); _burstEdited = false; BurstText(); ActiveControl = null; InvalidateBurst(); }
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _burstCancel = true; ActiveControl = null; }
            };
            _burst.Leave += (o, e) => { if (_burstEdited && !_burstCancel) CommitBurst(); _burstEdited = _burstCancel = false; BurstText(); InvalidateBurst(); };
            Controls.Add(_burst);

            // Right Column: Tabbed CPU Tuning / Inline Fan Curve Card
            _rightTabs = Add(new Segmented
            {
                Labels = new[] { "CPU Tuning", "Fan Curve" },
                Compact = true,
                Selected = 0,
                Accent = Theme.Accent
            });
            _rightTabs.Changed = i => SetRightTab(i);

            _mode = Add(new Segmented
            {
                Labels = new[] { "Watt target", "Clock caps", "Windows" },
                Compact = true,
                Accent = Theme.Accent,
                Tip = "CPU control mode"
            });
            _mode.Changed = i =>
            {
                if (i == 2) { _app.Apply(Profile.Keeping("default", _app.Engine.Profile), false); return; }
                Change(p =>
                {
                    p.CpuShare = 0;
                    if (i == 0) p.Mode = Mode.Target;
                    else { CapsFromLive(p); p.Mode = Mode.Manual; }
                });
            };

            _effOnly = Add(new Toggle
            {
                Label = "E-cores only",
                Compact = true,
                Tip = "Schedule threads onto the 6 efficient cores only"
            });
            _effOnly.Changed = on => Change(p => p.EffOnly = on);

            _target = Add(new Slider
            {
                Label = "CPU watt target",
                Min = 2.5,
                Max = 30,
                Step = 0.5,
                Format = v => v.ToString("0.0") + " W",
                MarkerColor = Theme.Amber,
                Tip = "Closed-loop CPU power target (amber marker = governor commanded watts)"
            });
            _target.Committed = v => Change(p => { p.CpuShare = 0; p.Mode = Mode.Target; p.TargetW = v; });

            _perf = Add(new Slider
            {
                Label = "Efficient (6)",
                Min = 700,
                Max = Governor.PerfMax,
                Step = 50,
                Format = FmtMhz(Governor.PerfMax),
                Accent = Theme.Teal,
                MarkerColor = Theme.Amber,
                RightHint = "max 3.61",
                Tip = "Clock ceiling for the 6 efficient cores (amber marker = live clock)"
            });
            _perf.Committed = v => Change(p => { p.CpuShare = 0; CapsFromLive(p); p.Mode = Mode.Manual; p.PerfMhz = v >= Governor.PerfMax ? 0 : (int)v; });

            _prime = Add(new Slider
            {
                Label = "Fast (12)",
                Min = 700,
                Max = Governor.PrimeMax,
                Step = 50,
                Format = FmtMhz(Governor.PrimeMax),
                Accent = Theme.Accent,
                MarkerColor = Theme.Amber,
                RightHint = "max 4.45",
                Tip = "Clock ceiling for the 12 fast cores (amber marker = live clock)"
            });
            _prime.Committed = v => Change(p => { p.CpuShare = 0; CapsFromLive(p); p.Mode = Mode.Manual; p.PrimeMhz = v >= Governor.PrimeMax ? 0 : (int)v; });

            _gpu = Add(new Slider
            {
                Label = "GPU ceiling",
                Min = 2,
                Max = GpuOff,
                Step = 0.5,
                Value = GpuOff,
                Accent = Theme.Green,
                Format = v => v >= GpuOff ? "off" : v.ToString("0.0") + " W",
                MarkerColor = Theme.Amber,
                Tip = "Optional GPU watt target held via fast 2 s PEP SoC PL1"
            });
            _gpu.Committed = v => { var p = _app.Engine.Profile; p.GpuW = v >= GpuOff ? 0 : v; _app.Apply(p, false); };

            _epp = Add(new Slider
            {
                Label = "Energy pref (EPP)",
                Min = 0,
                Max = 100,
                Step = 5,
                Format = v => v <= 15 ? "speed · " + v : v >= 70 ? "battery · " + v : "balanced · " + v,
                Accent = Theme.Green,
                Tip = "Windows Energy Performance Preference (0 = speed, 100 = battery)"
            });
            _epp.Committed = v => Change(p => p.Epp = (int)v);

            _boost = Add(new Segmented
            {
                Labels = new[] { "Off", "Efficient", "Full" },
                Compact = true,
                Accent = Theme.Accent
            });
            _boost.Changed = i => Change(p => p.Boost = i == 0 ? 0 : i == 1 ? 3 : 2);


            _reset = Add(new LinkButton
            {
                Label = "Reset defaults",
                Tip = "Return to Windows Balanced, ASUS Standard, uncapped SoC, fan curve off",
                Click = () => { _app.FanCurveOff(); _app.Apply(Profile.ForPreset("default"), true); SyncFanUi(); }
            });

            // Inline Fan Curve Tab widgets
            var fc = _app.Engine.FanCurve;
            _curveEdit = Add(new CurveEditor { Curve = fc, CurveActive = fc.On, Tip = "Drag any of the 5 temperature/PWM points" });
            _curveEdit.Changed = _ => Invalidate(Rectangle.Ceiling(R(AdvCard.X, AdvCard.Y, AdvCard.Width, AdvCard.Height)));
            _curveEdit.Committed = k => { k.On = _app.Engine.FanCurve.On; _app.Engine.FanCurve = k; SyncFanUi(); Invalidate(); };

            // (on/off is the dot on the fan button in the SoC card, always visible: no second toggle here)

            _curveDefault = Add(new LinkButton
            {
                Label = "Default 50°",
                TinyFont = true,
                Tip = "50 °C 0% · 58 °C 20% · 66 °C 40% · 74 °C 65% · 82 °C 100%",
                Click = () => SetCurvePoints(new FanCurve())
            });
            _curveSilent = Add(new LinkButton
            {
                Label = "Silent <60°",
                TinyFont = true,
                Tip = "Fans stay off until 60 °C (60/66/72/78/84 °C)",
                Click = () => SetCurvePoints(new FanCurve { T = new double[] { 60, 66, 72, 78, 84 }, P = new double[] { 0, 25, 45, 70, 100 } })
            });

            // Chart Chip °C overlay toggle
            _tempLineBtn = Add(new LinkButton
            {
                Label = "+ °C",
                Color = Theme.Purple,
                TinyFont = true,
                Tip = "Toggle chip temperature (°C) line on the 3-minute chart",
                Click = () =>
                {
                    _showTempLine = !_showTempLine;
                    _tempLineBtn.Active = _showTempLine;
                    Invalidate(Rectangle.Ceiling(R(ChartCard.X, ChartCard.Y, ChartCard.Width, ChartCard.Height)));
                }
            });

            RefreshSlots();
            SyncFanUi();
            ApplyDpi(DeviceDpi);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tip != null) _tip.Dispose();
                if (_f != null) { _f.Dispose(); _f = null; }
                if (_bg != null) { _bg.Dispose(); _bg = null; }
            }
            base.Dispose(disposing);
        }

        void OnTipPopup(object sender, PopupEventArgs e)
        {
            string txt = _tip.GetToolTip(e.AssociatedControl);
            if (string.IsNullOrEmpty(txt)) { e.Cancel = true; return; }
            using (var g = CreateGraphics())
            using (var font = new Font(Theme.FontName, 9f, FontStyle.Regular, GraphicsUnit.Point))
            {
                var sz = g.MeasureString(txt, font);
                e.ToolTipSize = new Size((int)Math.Ceiling(sz.Width) + 14, (int)Math.Ceiling(sz.Height) + 8);
            }
        }

        void OnTipDraw(object sender, DrawToolTipEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.CardHi);
            using (var pen = new Pen(Theme.BorderHi, 1))
                g.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            using (var font = new Font(Theme.FontName, 9f, FontStyle.Regular, GraphicsUnit.Point))
                Draw.Text(g, e.ToolTipText, font, Theme.Text, new RectangleF(7, 0, e.Bounds.Width - 14, e.Bounds.Height));
        }

        static void PaintMinIcon(Graphics g, RectangleF r, float s, Color col)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var pen = new Pen(col, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx - 5 * s, cy, cx + 5 * s, cy);
        }

        static void PaintCloseIcon(Graphics g, RectangleF r, float s, Color col)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, d = 4.5f * s;
            using (var pen = new Pen(col, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
            }
        }

        public void OpenFanCurveTab()
        {
            if (_simple)
            {
                _rightTab = 1;
                _rightTabs.Selected = 1;
                SetView(false);
            }
            else SetRightTab(1);
        }

        void SetRightTab(int tab)
        {
            if (_rightTab == tab && _rightTabs.Selected == tab) return;
            _rightTab = tab;
            _rightTabs.Selected = tab;
            SyncFanUi();
            Layout_();
            DisposeBg();
            Invalidate();
            KickAnim();
        }

        void SetCurvePoints(FanCurve k)
        {
            k.On = _app.Engine.FanCurve.On;
            _app.Engine.FanCurve = k;
            _curveEdit.Curve = _app.Engine.FanCurve;
            SyncFanUi();
            Invalidate();
        }

        void SyncFanUi()
        {
            var fc = _app.Engine.FanCurve;
            bool on = fc.On;   // the requested state: the snapshot lags a click by up to a tick
            if (!_curveEdit.Dragging) _curveEdit.Curve = fc;
            _curveEdit.CurveActive = on;
            _fanSplit.On = on;
            _fanSplit.Warn = on && _snap.FanState != null;
            string arrow = !_simple && _rightTab == 1 ? " ▴" : " ▾";
            if (!on) _fanSplit.Label = "Curve off" + arrow;
            else if (_snap.FanState != null) _fanSplit.Label = "Curve · EC" + arrow;
            else
            {
                int pct = _snap.FanPwm >= 0
                    ? (int)Math.Round(_snap.FanPwm / 2.55)
                    : (!double.IsNaN(_snap.R.ChipC) ? (int)Math.Round(fc.At(_snap.R.ChipC)) : 0);
                _fanSplit.Label = "Curve · " + pct + "%" + arrow;
            }
            _fanSplit.OpenTip = _snap.FanState != null
                ? _snap.FanState + " (click to edit curve)"
                : "Edit 5-point fan curve inline";
        }

        static Func<double, string> FmtMhz(int max) { return v => v >= max ? "no cap" : (v / 1000.0).ToString("0.00") + " GHz"; }

        void CapsFromLive(Profile p)
        {
            if (p.Mode == Mode.Manual) return;
            var c = _snap.Applied;
            p.PerfMhz = c != null ? c.Mhz[0] : 0;
            p.PrimeMhz = c != null ? c.Mhz[1] : 0;
        }

        T Add<T>(T w) where T : Widget { _w.Add(w); return w; }

        static string ShortSlotTag(Profile sp)
        {
            if (sp.Mode == Mode.Default) return "Win";
            if (sp.SocW > 0) return Math.Round(sp.SocW).ToString("0") + "W";
            if (sp.Mode == Mode.Target) return Math.Round(sp.TargetW).ToString("0") + "W";
            if (sp.PerfMhz == 0 && sp.PrimeMhz == 0) return "Free";
            int mhz = sp.PrimeMhz > 0 ? sp.PrimeMhz : sp.PerfMhz;
            return (mhz / 1000.0).ToString("0.0") + "G";
        }

        void SlotLabels()
        {
            int def = Store.DefaultSlot;
            for (int i = 0; i < Store.Slots; i++)
            {
                var sp = _slotData[i];
                if (sp == null)
                {
                    _slots.Labels[i] = "+ Save";
                    _slots.Dots[i] = null;
                    _slots.SecondDots[i] = null;
                    _slots.ItemTips[i] = "Empty slot " + (i + 1) + " — click to save current settings";
                    continue;
                }
                _slots.Labels[i] = (i + 1) + (i == def ? "★ " : " ") + ShortSlotTag(sp);
                bool ac = SlotBindings.Ac == i, bat = SlotBindings.Bat == i;
                _slots.Dots[i] = ac ? (Color?)Theme.Green : bat ? (Color?)Theme.Accent : null;
                _slots.SecondDots[i] = ac && bat ? (Color?)Theme.Accent : null;

                var flags = new List<string>();
                if (i == def) flags.Add("Startup");
                if (ac) flags.Add("Auto on AC");
                if (bat) flags.Add("Auto on Battery");
                string prefix = flags.Count > 0 ? " (" + string.Join(" · ", flags) + ")" : "";
                _slots.ItemTips[i] = "Profile " + (i + 1) + prefix + ": " + Profile.Summary(sp) + " — right-click for options";
            }
        }

        void RefreshSlots()
        {
            _slotData = Store.LoadSlots();
            SlotBindings.Load();
            SlotLabels();
            _app.SlotsChanged();
            Invalidate();
        }

        void SaveSlot(int i)
        {
            Store.SaveSlot(i, _app.Engine.Profile);
            RefreshSlots();
            var p = _app.Engine.Profile; p.Preset = "slot" + (i + 1);
            _app.Apply(p, false);
        }

        void SlotMenu(int i, Point at)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Save current settings to Profile " + (i + 1), null, (s, e) => SaveSlot(i));
            var clear = m.Items.Add("Clear Profile " + (i + 1), null, (s, e) =>
            {
                Store.SaveSlot(i, null);
                if (Store.DefaultSlot == i) { Store.DefaultSlot = -1; Startup.Sync(); }
                SlotBindings.ClearSlot(i);
                RefreshSlots();
            });
            clear.Enabled = _slotData[i] != null;
            m.Items.Add(new ToolStripSeparator());

            // The ★ profile is the start-with-Windows switch: starring one turns it on, un-starring turns it off.
            var start = (ToolStripMenuItem)m.Items.Add("Start with Windows using Profile " + (i + 1), null, (s, e) =>
            {
                Store.DefaultSlot = Store.DefaultSlot == i ? -1 : i;
                Startup.Sync();
                RefreshSlots(); SyncFromProfile(_app.Engine.Profile);
            });
            start.Checked = Store.DefaultSlot == i;
            start.Enabled = _slotData[i] != null;

            var bindAc = (ToolStripMenuItem)m.Items.Add("Apply when plugged in (AC)", null, (s, e) =>
            {
                SlotBindings.Ac = SlotBindings.Ac == i ? -1 : i;
                SlotBindings.Save();
                RefreshSlots();
                if (SlotBindings.Ac == i && _slotData[i] != null && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online)
                {
                    var sp = _slotData[i].Clone();
                    _app.Apply(sp, sp.Asus != _app.Engine.Profile.Asus);
                }
            });
            bindAc.Checked = SlotBindings.Ac == i;
            bindAc.Enabled = _slotData[i] != null;

            var bindBat = (ToolStripMenuItem)m.Items.Add("Apply on battery", null, (s, e) =>
            {
                SlotBindings.Bat = SlotBindings.Bat == i ? -1 : i;
                SlotBindings.Save();
                RefreshSlots();
                if (SlotBindings.Bat == i && _slotData[i] != null && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
                {
                    var sp = _slotData[i].Clone();
                    _app.Apply(sp, sp.Asus != _app.Engine.Profile.Asus);
                }
            });
            bindBat.Checked = SlotBindings.Bat == i;
            bindBat.Enabled = _slotData[i] != null;

            m.Closed += (s, e) => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)Activate); };
            m.Show(this, at);
        }

        void Change(Action<Profile> edit, bool hardware = false)
        {
            var p = _app.Engine.Profile;
            if (p.Mode == Mode.Default) { var b = Profile.ForPreset("balanced"); b.Asus = p.Asus; p = b; hardware = true; }
            edit(p);
            p.Preset = MatchPreset(p);
            _app.Apply(p, hardware);
        }

        static string MatchPreset(Profile p)
        {
            foreach (var n in Profile.PresetOrder)
            {
                var q = Profile.ForPreset(n);
                if (q.Mode == p.Mode && q.Epp == p.Epp
                    && (p.Mode != Mode.Target || Math.Abs(q.TargetW - p.TargetW) < 0.01)
                    && (p.Mode != Mode.Manual || (q.PerfMhz == p.PerfMhz && q.PrimeMhz == p.PrimeMhz))) return n;
            }
            return "custom";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1;
            try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
            int round = 2;   // DWMWCP_ROUND: hardware-composited 8 px corners + flyout shadow + 1 px DWM frame
            try { _dwmFramed = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0; } catch { _dwmFramed = false; }
            int borderCol = 0x003C302B;   // COLORREF 0x00BBGGRR for #2b303c
            try { DwmSetWindowAttribute(Handle, 34, ref borderCol, 4); } catch { }
            try { ApplyDpi(GetDpiForWindow(Handle)); } catch { }
        }

        // Pulls the window back inside the working area of the screen it is on (a monitor removed, scaling changed).
        public void KeepOnScreen()
        {
            var wa = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width)), y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        public void PositionNearTray(bool fromCursorMonitor = false)
        {
            var scr = fromCursorMonitor ? Screen.FromPoint(Cursor.Position) : Screen.PrimaryScreen;
            var wa = scr.WorkingArea;
            var sb = scr.Bounds;
            int m = Math.Max(8, (int)Math.Round(10 * _s));
            int x, y;
            if (wa.Top > sb.Top) { x = wa.Right - Width - m; y = wa.Top + m; }
            else if (wa.Left > sb.Left) { x = wa.Left + m; y = wa.Bottom - Height - m; }
            else { x = wa.Right - Width - m; y = wa.Bottom - Height - m; }
            x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - Width - 4));
            y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - Height - 4));
            Location = new Point(x, y);
            _trayAnchored = true;
        }

        // ---- 260 ms 120 Hz DwmFlush-paced More/Less view grow/shrink animation ----
        Rectangle _viewFrom, _viewTo;
        double _viewT0 = -1;
        bool _toSimple;
        int _viewFrame0;
        const double ViewMs = 260;

        void SetView(bool simple)
        {
            if (_viewT0 >= 0) return;
            try { if (simple) File.WriteAllText(ViewFile, ""); else File.Delete(ViewFile); } catch { }
            _hover = null; _capture = null;
            var nc = Size - ClientSize;
            var to = new Size((int)Math.Round((simple ? SimpleW : FullW) * _s) + nc.Width, (int)Math.Round((simple ? SimpleH : FullH) * _s) + nc.Height);
            var scr = Screen.FromHandle(IsHandleCreated ? Handle : IntPtr.Zero);
            var wa = scr.WorkingArea;
            var sb = scr.Bounds;
            if (!Visible || WindowState != FormWindowState.Normal || to.Width > wa.Width || to.Height > wa.Height)
            {
                _simple = simple;
                Fit();
                if (_trayAnchored && !UserMoved) PositionNearTray(false); else KeepOnScreen();
                return;
            }
            // Growing: build the full-size background now, so the first frame of the animation is not a slow one.
            if (!simple) { _simple = false; Layout_(); DisposeBg(); _bg = PaintStatic(); }
            _toSimple = simple;
            _viewFrom = Bounds;

            int tx, ty;
            if (_trayAnchored && !UserMoved)
            {
                int m = Math.Max(8, (int)Math.Round(10 * _s));
                tx = wa.Left > sb.Left ? wa.Left + m : wa.Right - to.Width - m;
                ty = wa.Top > sb.Top ? wa.Top + m : wa.Bottom - to.Height - m;
            }
            else
            {
                tx = Left;
                ty = Top;
            }
            tx = Math.Max(wa.Left, Math.Min(tx, wa.Right - to.Width));
            ty = Math.Max(wa.Top, Math.Min(ty, wa.Bottom - to.Height));
            _viewTo = new Rectangle(tx, ty, to.Width, to.Height);
            _view.Label = simple ? "More ▸" : "◂ Less";
            SyncFanUi();
            Dirty(_view);
            Dirty(_fanSplit);

            MinimumSize = Size.Empty;
            SetStyle(ControlStyles.ResizeRedraw, false);   // the layout stays put: each frame paints only the strip it reveals
            _viewT0 = _animClock.Elapsed.TotalMilliseconds;
            KickAnim();
            _viewFrame0 = _animCount;
        }

        bool ViewStep(double now)
        {
            if (_viewT0 < 0) return false;
            double k = Math.Min(1, (now - _viewT0) / ViewMs), e = 1 - Math.Pow(1 - k, 3);   // ease-out cubic
            Func<int, int, int> lerp = (a, b) => (int)Math.Round(a + (b - a) * e);
            SetBounds(lerp(_viewFrom.X, _viewTo.X), lerp(_viewFrom.Y, _viewTo.Y), lerp(_viewFrom.Width, _viewTo.Width), lerp(_viewFrom.Height, _viewTo.Height));
            if (k < 1) return true;
            if (DebugPaint) Store.Log(string.Format("view anim: {0} frames in {1:0} ms = {2:0} fps", _animCount - _viewFrame0, now - _viewT0, (_animCount - _viewFrame0) * 1000 / (now - _viewT0)));
            _viewT0 = -1;
            SetStyle(ControlStyles.ResizeRedraw, true);
            if (_toSimple) { _simple = true; Layout_(); }
            SyncFanUi();
            float d = _dpi / 96f;
            MinimumSize = new Size((int)(LW * d * 0.65f), (int)(LH * d * 0.65f));
            if (_toSimple) DisposeBg();
            Invalidate();
            return false;
        }

        int _dpi = 96;
        void ApplyDpi(int dpi)
        {
            if (dpi < 48) dpi = 96;
            if (_f != null && dpi == _dpi) return;
            _dpi = dpi;
            Fit();
        }

        void Fit()
        {
            float d = _dpi / 96f;
            MinimumSize = new Size((int)(LW * d * 0.65f), (int)(LH * d * 0.65f));
            var wa = Screen.FromHandle(IsHandleCreated ? Handle : IntPtr.Zero).WorkingArea;
            float z = Math.Min(1.0f * d, Math.Min((wa.Width - 24) / LW, (wa.Height - 32) / LH));
            ClientSize = new Size((int)Math.Round(LW * z), (int)Math.Round(LH * z));
            _s = 0;   // force relayout even if scale unchanged
            Rescale();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (_f != null) Rescale(); }

        void Rescale()
        {
            if (ClientSize.Width < 10 || ClientSize.Height < 10) return;
            if (_viewT0 >= 0) return;   // view switch: same scale, window reveals or hides right/bottom quadrants
            float ns = Math.Min(ClientSize.Width / LW, ClientSize.Height / LH);
            if (_f != null && Math.Abs(ns - _s) < 0.004) return;
            _s = ns;
            if (_f != null) _f.Dispose();
            _f = new Fonts(_s);
            Layout_();
            DisposeBg();
            Invalidate();
        }

        void DisposeBg()
        {
            if (_bg != null) { _bg.Dispose(); _bg = null; }
        }

        RectangleF R(float x, float y, float w, float h) { return new RectangleF(x * _s, y * _s, w * _s, h * _s); }

        // ---- layout (logical px) ----
        // Simple view = [0..448] x [0..340]. Full view = [0..904] x [0..544].
        // AdvCard starts at X=448 (SimpleW), ChartCard & PartsCard start at Y=340 (SimpleH): zero bleed into Simple view!
        static readonly RectangleF
            HeroCard = new RectangleF(12, 44, 424, 96),
            CtlCard = new RectangleF(12, 150, 424, 178),
            AdvCard = new RectangleF(448, 12, 444, 316),
            ChartCard = new RectangleF(12, 340, 424, 192),
            PartsCard = new RectangleF(448, 340, 444, 192);

        Mode CurrentMode { get { var p = _p ?? _snap.Profile; return p != null ? p.Mode : Mode.Default; } }

        void UpdateTrialBadge()
        {
            int pend = _app.SecondsUntilConfirmed;
            bool vis = pend > 0;
            if (vis) _trial.Label = pend + "s · Keep";
            if (_trial.Visible != vis) _trial.Visible = vis;
        }

        void Layout_()
        {
            Mode cm = CurrentMode;
            bool full = !_simple;
            bool cpuTab = full && _rightTab == 0;
            bool fanTab = full && _rightTab == 1;

            UpdateTrialBadge();
            _elevate.Visible = !_admin;
            _view.Label = (_viewT0 >= 0 ? _toSimple : _simple) ? "More ▸" : "◂ Less";
            SlotLabels();

            // Visibility by view & active right-pane tab / CPU mode (zero dead sliders)
            _rightTabs.Visible = full;
            _tempLineBtn.Visible = full;

            _mode.Visible = cpuTab;
            _effOnly.Visible = cpuTab;
            _target.Visible = cpuTab && cm == Mode.Target;
            _perf.Visible = cpuTab && cm == Mode.Manual;
            _prime.Visible = cpuTab && cm == Mode.Manual;
            _gpu.Visible = cpuTab;
            _epp.Visible = cpuTab;
            _boost.Visible = cpuTab;
            _reset.Visible = cpuTab;

            _curveEdit.Visible = fanTab;
            _curveDefault.Visible = fanTab;
            _curveSilent.Visible = fanTab;

            // Header (identical in Simple & Full)
            _trial.Bounds = R(116, 12, 62, 22);
            _slots.Bounds = R(182, 11, 136, 24);
            _view.Bounds = R(322, 11, 54, 24);
            _min.Bounds = R(LW - 66, 11, 26, 24);
            _close.Bounds = R(LW - 38, 11, 26, 24);

            // Primary Controls Card (identical in Simple & Full)
            _elevate.Bounds = R(294, 155, 130, 20);
            _socTiles.Bounds = R(24, 179, 400, 30);
            _splitAuto.Bounds = R(58, 220, 42, 22);
            _split.Bounds = R(105, 217, 182, 28);
            _asus.Bounds = R(24, 253, 270, 30);
            _fanSplit.Bounds = R(300, 253, 124, 30);
            _quiet.Bounds = R(24, 292, 152, 24);
            // charge limit box between "Charge limit" and "%" (PaintHero); own font copy: Rescale disposes _f
            // (Control.Font ignores a font equal to the current one, so swap and dispose only when the size changes)
            var bf = _f.SmallBold;
            if (_chargeFont == null || !_chargeFont.Equals(bf))
            {
                var old = _chargeFont;
                _charge.Font = _burst.Font = _chargeFont = new Font(bf.FontFamily, bf.Size, bf.Style, bf.Unit);
                if (old != null) old.Dispose();
            }
            var cb = Rectangle.Round(R(100, 108, 22, 20));
            _charge.Bounds = new Rectangle(cb.X, cb.Y + (cb.Height - _charge.PreferredHeight) / 2, cb.Width, _charge.PreferredHeight);
            // burst window box between "Burst" and "s" (PaintPrimaryDynamic)
            var bb = Rectangle.Round(R(224, 294, 18, 20));
            _burst.Bounds = new Rectangle(bb.X, bb.Y + (bb.Height - _burst.PreferredHeight) / 2, bb.Width, _burst.PreferredHeight);

            // Right Column Top Bar
            _rightTabs.Bounds = R(462, 22, 196, 24);

            // Right Column Tab 0: CPU Tuning
            _mode.Bounds = R(462, 56, 264, 24);
            _effOnly.Bounds = R(738, 56, 140, 24);
            _target.Bounds = R(462, 90, 416, 56);
            _perf.Bounds = R(462, 90, 200, 56);
            _prime.Bounds = R(678, 90, 200, 56);
            _gpu.Bounds = R(462, 160, 200, 42);
            _epp.Bounds = R(678, 160, 200, 42);
            _boost.Bounds = R(678, 218, 200, 26);
            _reset.Bounds = R(746, 277, 132, 26);

            // Right Column Tab 1: Inline Fan Curve
            _curveEdit.Bounds = R(462, 54, 416, 224);
            _curveDefault.Bounds = R(462, 286, 80, 24);
            _curveSilent.Bounds = R(546, 286, 80, 24);

            // Bottom-Left Chart °C toggle
            _tempLineBtn.Bounds = R(382, 347, 42, 19);

            foreach (var w in _w) w.Scale = _s;
        }

        void SyncSocTiles(double socW)
        {
            for (int i = 0; i < SocTileBase.Length; i++) _socTiles.Labels[i] = SocTileBase[i];
            int exact = Array.FindIndex(SocTileW, w => Math.Abs(w - socW) < 0.05);
            if (exact >= 0) { _socTiles.Selected = exact; return; }
            // Custom mouse-wheel or file watt cap: show "X W*" on nearest watt preset so the highlight glides naturally.
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < SocTileW.Length - 1; i++)
            {
                double d = Math.Abs(SocTileW[i] - socW);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            _socTiles.Labels[best] = socW.ToString("0.#") + " W*";
            _socTiles.Selected = best;
        }

        public void SyncFromProfile(Profile p)
        {
            bool modeChanged = _p == null || _p.Mode != p.Mode;
            _p = p.Clone();

            int slot;
            _slots.Selected = p.Preset != null && p.Preset.StartsWith("slot") && int.TryParse(p.Preset.Substring(4), out slot) && slot >= 1 && slot <= Store.Slots ? slot - 1 : -1;
            _mode.Selected = p.Mode == Mode.Target ? 0 : p.Mode == Mode.Manual ? 1 : 2;

            if (!_target.Dragging) _target.Value = Math.Max(_target.Min, Math.Min(_target.Max, p.TargetW));
            if (p.Mode == Mode.Manual)
            {
                if (!_perf.Dragging) _perf.Value = p.PerfMhz == 0 ? Governor.PerfMax : p.PerfMhz;
                if (!_prime.Dragging) _prime.Value = p.PrimeMhz == 0 ? Governor.PrimeMax : p.PrimeMhz;
            }
            if (!_epp.Dragging) _epp.Value = p.Epp;
            _boost.Selected = p.Boost == 0 ? 0 : p.Boost == 3 || p.Boost == 4 ? 1 : 2;
            _asus.Selected = Array.IndexOf(Hardware.AsusNames, p.Asus);

            _target.Enabled = true;
            _perf.Enabled = true;
            _prime.Enabled = !p.EffOnly;
            _effOnly.On = p.EffOnly;

            _socTiles.Enabled = _admin;
            SyncSocTiles(p.SocW);

            if (!_split.Dragging && p.CpuShare > 0) _split.Value = Math.Max(_split.Min, Math.Min(_split.Max, p.CpuShare));
            _target.Auto = p.CpuShare > 0;
            _splitAuto.Active = p.CpuShare < 0;
            _split.Auto = p.CpuShare <= 0;

            if (!_gpu.Dragging) _gpu.Value = p.GpuW > 0 ? Math.Min(GpuOff, p.GpuW) : GpuOff;
            _gpu.Enabled = _admin;
            _epp.Enabled = _boost.Enabled = _mode.Enabled = true;
            _quiet.On = _app.Engine.QuietOnBattery;

            UpdateLiveSliderHints(_snap, p);
            SyncFanUi();
            Layout_();
            if (modeChanged) DisposeBg();
            Invalidate();
            KickAnim();
        }

        void UpdateLiveSliderHints(Snapshot s, Profile p)
        {
            var r = s.R;
            // Watt target live governor marker + caps hint
            _target.Marker = p.Mode == Mode.Target && s.Commanded > 0 ? (double?)s.Commanded : null;
            if (p.Mode == Mode.Target)
            {
                var c = s.Applied;
                string eCap = c != null ? Ghz(c.Mhz[0], Governor.PerfMax) : "–";
                string pCap = p.EffOnly ? "parked" : (c != null ? Ghz(c.Mhz[1], Governor.PrimeMax) : "–");
                _target.LeftHint = "Governor caps: E " + eCap + " · P " + pCap;
                _target.LeftHintColor = Theme.Amber;
                _target.RightHint = r.Valid ? "now " + r.Cpu.ToString("0.0") + " W" : "now –";
            }

            // Clock caps live cluster MHz markers + readouts
            int eMhz = r.ClusterMhz != null && r.ClusterMhz.Length > 0 ? r.ClusterMhz[0] : 0;
            int pMhz = r.ClusterMhz != null && r.ClusterMhz.Length > 2 ? Math.Max(r.ClusterMhz[1], r.ClusterMhz[2]) : 0;
            _perf.Marker = eMhz > 0 ? (double?)eMhz : null;
            _perf.LeftHint = eMhz > 0 ? "now " + (eMhz / 1000.0).ToString("0.00") + " GHz" : "now –";
            _prime.Marker = !p.EffOnly && pMhz > 0 ? (double?)pMhz : null;
            _prime.LeftHint = p.EffOnly ? "parked (E-only)" : (pMhz > 0 ? "now " + (pMhz / 1000.0).ToString("0.00") + " GHz" : "now –");

            // GPU live watts marker when GPU ceiling is set
            _gpu.Marker = p.GpuW > 0 && r.Valid ? (double?)r.Gpu : null;
        }

        void KickAnim()
        {
            if (!Visible || !IsHandleCreated) return;
            if (_animThread == null || !_animThread.IsAlive)
            {
                _animThread = new Thread(AnimLoop) { IsBackground = true, Name = "PowerCap frames" };
                _animThread.Start();
            }
            if (_animOn) return;
            _animFrames = 0; _animOn = true; _animLast = _animStart = _animClock.Elapsed.TotalMilliseconds; _animCount = 0;
            _animWake.Set();
        }

        void StopAnim()
        {
            _animOn = false; _animWake.Reset();
            if (DebugPaint && _animCount > 2)
            {
                double ms = _animClock.Elapsed.TotalMilliseconds - _animStart;
                Store.Log(string.Format("anim {0} frames in {1:0} ms = {2:0} fps", _animCount, ms, _animCount * 1000 / ms));
            }
        }

        public void Tick(Snapshot s)
        {
            _snap = s;
            ChargeText(s);
            BurstText();
            _curveEdit.LiveC = s.FanOn ? (!double.IsNaN(s.FanCurveC) ? s.FanCurveC : s.R.ChipC) : s.R.ChipC;
            _curveEdit.LivePwm = s.FanPwm;
            SyncFanUi();
            UpdateTrialBadge();

            if (s.Profile != null && (_p == null || s.Profile.Mode != _p.Mode || s.Profile.Preset != _p.Preset
                || s.Profile.Asus != _p.Asus || Math.Abs(s.Profile.SocW - _p.SocW) > 0.05 || s.Profile.EffOnly != _p.EffOnly))
                SyncFromProfile(s.Profile);
            else if (_p != null)
                UpdateLiveSliderHints(s, _p);

            double? live = s.R.Valid && s.R.Cpu + s.R.Gpu > 0.5 ? (double?)(100.0 * s.R.Cpu / (s.R.Cpu + s.R.Gpu)) : null;
            if (_p != null && _p.CpuShare < 0 && live.HasValue && !_split.Dragging)
                _split.Value = Math.Max(_split.Min, Math.Min(_split.Max, live.Value));

            _animFrames = 0;
            KickAnim();
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x02E0)   // WM_DPICHANGED
            {
                ApplyDpi((int)(m.WParam.ToInt64() & 0xFFFF));
                return;
            }
            if (m.Msg == 0x0084 && m.Result == (IntPtr)1 && _viewT0 < 0 && WindowState == FormWindowState.Normal)   // WM_NCHITTEST
            {
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point(unchecked((short)lp), unchecked((short)(lp >> 16))));
                int g = Math.Max(4, (int)Math.Round(5 * _s));
                bool l = pt.X < g, r = pt.X >= ClientSize.Width - g, t = pt.Y < g, b = pt.Y >= ClientSize.Height - g;
                if ((l || r || t || b) && At(pt) == null)
                {
                    if (t && l) m.Result = (IntPtr)13;
                    else if (t && r) m.Result = (IntPtr)14;
                    else if (b && l) m.Result = (IntPtr)16;
                    else if (b && r) m.Result = (IntPtr)17;
                    else if (l) m.Result = (IntPtr)10;
                    else if (r) m.Result = (IntPtr)11;
                    else if (t) m.Result = (IntPtr)12;
                    else if (b) m.Result = (IntPtr)15;
                }
            }
        }

        // ---- input ----
        Widget At(Point p)
        {
            var pf = new PointF(p.X, p.Y);
            for (int i = _w.Count - 1; i >= 0; i--)
                if (_w[i].Visible && _w[i].Bounds.Contains(pf)) return _w[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pf = new PointF(e.X, e.Y);
            if (_capture != null)
            {
                _capture.Move(pf);
                Dirty(_capture);
                if (_capture is Slider) Invalidate(Rectangle.Ceiling(R(HeroCard.X, HeroCard.Y, HeroCard.Width, HeroCard.Height)));
                return;
            }
            var h = At(e.Location);
            if (h != _hover)
            {
                if (_hover != null)
                {
                    _hover.Hover = false;
                    var sg = _hover as Segmented;
                    if (sg != null) sg.HoverIndex = -1;
                    Dirty(_hover);
                }
                _hover = h;
                if (h != null) h.Hover = true;
                var cur = h != null && h.Enabled ? Cursors.Hand : Cursors.Default;
                if (Cursor != cur) Cursor = cur;
            }
            if (h != null)
            {
                var sg = h as Segmented;
                int before = sg != null ? sg.HoverIndex : 0;
                h.Move(pf);
                if (sg == null || sg.HoverIndex != before || h is SplitCurveButton || h is CurveEditor) Dirty(h);
            }
            string tip = h != null ? h.TipAt(pf) : null;
            if (tip != _lastTip) { _lastTip = tip; _tip.SetToolTip(this, tip ?? ""); }
        }

        void Dirty(Widget w)
        {
            var r = w.Bounds;
            r.Inflate(10 * _s, 10 * _s);
            Invalidate(Rectangle.Ceiling(r));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != null)
            {
                _hover.Hover = false;
                var sg = _hover as Segmented;
                if (sg != null) sg.HoverIndex = -1;
                Dirty(_hover);
                _hover = null;
            }
            if (_lastTip != null) { _lastTip = null; _tip.SetToolTip(this, ""); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && At(e.Location) == _slots && _slots.Bounds.Width > 0)
            {
                SlotMenu(_slots.IndexAt(new PointF(e.X, e.Y)), e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            var h = At(e.Location);
            if (h == null)
            {
                // Dragging the borderless header moves the flyout freely.
                if (e.Y < 42 * _s && _viewT0 < 0)
                {
                    _trayAnchored = false;
                    UserMoved = true;
                    ReleaseCapture();
                    SendMessage(Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(2 /* HTCAPTION */), IntPtr.Zero);
                }
                return;
            }
            if (!h.Enabled) return;
            _capture = h; h.Pressed = true; Capture = true;
            h.Down(new PointF(e.X, e.Y));
            Dirty(h);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_capture == null) return;
            var c = _capture; _capture = null; c.Pressed = false; Capture = false;
            c.Up(new PointF(e.X, e.Y), c.Bounds.Contains(e.X, e.Y));
            Dirty(c);
            KickAnim();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var h = At(e.Location);
            if (h != null && h.Wheel(e.Delta)) { Dirty(h); KickAnim(); }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);   // a normal window now: it stays open until minimized or closed
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !_charge.Focused && !_burst.Focused) Hide();
            base.OnKeyDown(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            _app.Engine.Foreground = Visible && WindowState != FormWindowState.Minimized;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_app != null && _app.Engine != null)
                _app.Engine.Foreground = Visible && WindowState != FormWindowState.Minimized;
        }

        // ---- painting ----
        static readonly bool DebugPaint = Environment.GetEnvironmentVariable("POWERCAP_DEBUG") == "1";
        readonly System.Diagnostics.Stopwatch _paintSw = new System.Diagnostics.Stopwatch();
        double _paintMs; int _paints;

        protected override void OnPaint(PaintEventArgs e)
        {
            if (DebugPaint) _paintSw.Restart();
            PaintAll(e);
            if (!DebugPaint) return;
            _paintMs += _paintSw.Elapsed.TotalMilliseconds;
            if (++_paints == 50)
            {
                Store.Log(string.Format("paint avg {0:0.0} ms over 50, last clip {1}, client {2}, scale {3:0.000}, bg {4}, dpi {5}", _paintMs / 50, e.ClipRectangle, ClientSize, _s, _bg == null ? "-" : _bg.Size.ToString(), _dpi));
                _paints = 0; _paintMs = 0;
            }
        }

        void PaintAll(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (_bg == null) _bg = PaintStatic();
            var mode = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            var cr = e.ClipRectangle;
            g.DrawImage(_bg, cr, cr, GraphicsUnit.Pixel);
            g.CompositingMode = mode;

            var s = _snap;
            var r = _shown;
            var p = s.Profile ?? _p ?? new Profile();

            PaintHero(g, s, r, p);
            PaintPrimaryDynamic(g, s, p);
            if (!_simple || _viewT0 >= 0)
            {
                PaintRightPaneDynamic(g, s, r, p);
                PaintChart(g, s, p);
                PaintParts(g, r);
            }

            var clip = e.ClipRectangle;
            foreach (var w in _w)
                if (w.Visible && clip.IntersectsWith(Rectangle.Ceiling(w.Bounds)))
                    w.Paint(g, _f);

            if (!_dwmFramed && _viewT0 < 0)
            {
                using (var pen = new Pen(Color.FromArgb(43, 48, 60), 1))
                    g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        // Unified Hero Card: Laptop W + Battery on the left; SoC (with mini cap bar), CPU · GPU, and Chip on the right.
        void PaintHero(Graphics g, Snapshot s, Reading r, Profile p)
        {
            // Left column, three rows like the right one: laptop watts, battery, charge limit (box placed in Layout_)
            string sysTxt = r.Valid ? r.System.ToString("0.0") : "–";
            Draw.TextTypo(g, sysTxt, _f.Mid, Theme.Text, R(24, 50, 80, 28), StringAlignment.Near, StringAlignment.Center);
            float sysW = Draw.Measure(g, sysTxt, _f.Mid).Width;
            Draw.Text(g, "W LAPTOP", _f.TinyBold, Theme.Muted, new RectangleF(24 * _s + sysW + 5 * _s, 54 * _s, 78 * _s, 20 * _s), StringAlignment.Near, StringAlignment.Center);

            if (s.BatteryPct >= 0)
            {
                PaintBattery(g, R(25, 86, 19, 10), s.BatteryPct, s.OnAC);
                string bat = s.BatteryPct + "%" + (s.OnAC ? " · on AC" : s.MinutesLeft > 0 ? " · " + Hm(s.MinutesLeft) : "");
                Draw.Text(g, bat, _f.SmallBold, Theme.Text, R(51, 81, 128, 20));
            }
            else Draw.Text(g, "Battery –", _f.SmallBold, Theme.Muted, R(25, 81, 100, 20));

            // Charge limit pill: looks like an input (raised fill, border), accent border while editing, status beside it
            Draw.Text(g, "Charge limit", _f.Small, Theme.Muted, R(25, 108, 70, 20));
            var pill = R(94, 106, 44, 24);
            Theme.Fill(g, pill, 7 * _s, Theme.CardHi);
            Theme.Stroke(g, pill, 7 * _s, _charge.Focused || _chargeEdited ? Theme.Accent : Theme.Border, 1 * _s);
            Draw.Text(g, "%", _f.SmallBold, ChargeColor(s), R(122, 108, 12, 20));
            Color csc; string cst = ChargeStatus(s, out csc);
            if (cst != null) Draw.Text(g, cst, _f.TinyBold, csc, R(141, 108, 40, 20));

            // Row 1: SoC draw vs cap + mini cap bar
            var barR = R(232, 62, 62, 5);
            Theme.Fill(g, barR, 2.5f * _s, Theme.Bg);
            Theme.Stroke(g, barR, 2.5f * _s, Theme.Border);
            double effCap = !double.IsNaN(s.SocHeldW) ? s.SocHeldW
                : p.SocW > 0 ? p.SocW
                : !double.IsNaN(s.SocLimitW) ? s.SocLimitW
                : !double.IsNaN(s.SocFirmwareW) ? s.SocFirmwareW : 35;
            if (r.Valid && effCap > 0)
            {
                float fillW = (float)(barR.Width * Math.Max(0, Math.Min(1, r.Soc / effCap)));
                if (fillW > 1f)
                    Theme.Fill(g, new RectangleF(barR.X, barR.Y, fillW, barR.Height), 2.5f * _s, p.SocW > 0 || p.GpuW > 0 ? Theme.Pink : Theme.Accent);
            }
            string socTxt = !r.Valid ? "–"
                : !double.IsNaN(s.SocHeldW) ? r.Soc.ToString("0.0") + " / " + s.SocHeldW.ToString("0.0") + " W"
                : p.SocW > 0 ? r.Soc.ToString("0.0") + " / " + p.SocW.ToString("0.#") + " W"
                : !double.IsNaN(s.SocFirmwareW) ? r.Soc.ToString("0.0") + " / " + s.SocFirmwareW.ToString("0") + " W"
                : r.Soc.ToString("0.0") + " W · free";
            Draw.Text(g, socTxt, _f.SmallBold, Theme.Text, R(298, 54, 126, 20), StringAlignment.Far);

            // Row 2: CPU · GPU
            string gpu3d = !double.IsNaN(s.R.Gpu3d) && s.R.Gpu3d >= 10 ? " (" + s.R.Gpu3d.ToString("0") + "%)" : "";
            string cgTxt = r.Valid ? r.Cpu.ToString("0.0") + " W · " + r.Gpu.ToString("0.0") + " W" + gpu3d : "–";
            Draw.Text(g, cgTxt, _f.SmallBold, Theme.Text, R(256, 81, 168, 20), StringAlignment.Far);

            // Row 3: Chip °C + fan status dot & RPM
            int fan = Math.Max(s.R.FanRpm[0], s.R.FanRpm[1]);
            bool fanRunning = s.FanOn && s.FanPwm >= 0 ? s.FanPwm > 0 : fan > 0;
            string chipTxt = (double.IsNaN(r.ChipC) ? "–" : r.ChipC.ToString("0") + " °C")
                + (fan < 0 ? "" : fan == 0 ? " · fans off" : " · " + (fan / 1000.0).ToString("0.0") + "k rpm");
            Draw.Text(g, chipTxt, _f.SmallBold, Theme.Text, R(234, 108, 190, 20), StringAlignment.Far);
            float cw = Draw.Measure(g, chipTxt, _f.SmallBold).Width;
            float dotX = 424 * _s - cw - 11 * _s, dotY = 115 * _s, dotD = 6 * _s;
            if (dotX > 228 * _s)
                using (var b = new SolidBrush(fanRunning ? Theme.Purple : Theme.Teal))
                    g.FillEllipse(b, dotX, dotY, dotD, dotD);
        }

        // Dynamic readouts inside CtlCard: PEP status, one-line Split readout, and Quiet on battery state chip.
        void PaintPrimaryDynamic(Graphics g, Snapshot s, Profile p)
        {
            if (_admin)
            {
                string st = null;
                Color sc = Theme.Muted;
                if (s.SocProblem != null) { st = s.SocProblem; sc = Theme.Amber; }
                else if (!double.IsNaN(s.SocHeldW)) { st = "Chip limited to " + s.SocHeldW.ToString("0.0") + " W"; sc = Theme.Pink; }
                else if (p.SocW > 0 && !double.IsNaN(s.SocFirmwareW)) { st = Hardware.AsusLabel(p.Asus) + " caps at " + s.SocFirmwareW.ToString("0") + " W"; sc = Theme.Pink; }
                else if (!double.IsNaN(s.SocFirmwareW)) { st = Hardware.AsusLabel(p.Asus) + " allows " + s.SocFirmwareW.ToString("0") + " W"; sc = Theme.Muted; }
                if (st != null) Draw.Text(g, st, _f.TinyBold, sc, R(164, 157, 260, 18), StringAlignment.Far);
            }

            // One-line CPU/GPU Split readout
            string splitTxt;
            Color splitCol = Theme.Muted;
            if (_split.Dragging || p.CpuShare > 0)
            {
                int share = _split.Dragging ? (int)Math.Round(_split.Value) : p.CpuShare;
                double cpu = SplitCpuW(p, share);
                double gpu = Math.Max(0, SplitBudget(p) - cpu);
                splitTxt = "CPU " + cpu.ToString("0.#") + " · GPU " + gpu.ToString("0.#") + " W";
                splitCol = Theme.Text;
            }
            else if (p.CpuShare < 0) splitTxt = "auto · no CPU cap";
            else splitTxt = p.Mode == Mode.Default ? "Windows default" : p.Mode == Mode.Target ? "CPU " + p.TargetW.ToString("0.#") + " W" : "clock caps";
            Draw.Text(g, splitTxt, _f.TinyBold, splitCol, R(290, 217, 134, 28), StringAlignment.Far);

            // Burst window pill (same look as the charge limit pill)
            Draw.Text(g, "Burst", _f.Small, Theme.Muted, R(182, 294, 36, 20));
            var bpill = R(218, 292, 40, 24);
            Theme.Fill(g, bpill, 7 * _s, Theme.CardHi);
            Theme.Stroke(g, bpill, 7 * _s, _burst.Focused || _burstEdited ? Theme.Accent : Theme.Border, 1 * _s);
            Draw.Text(g, "s", _f.SmallBold, _app.Engine.BurstWindow > 0 ? Theme.Accent : Theme.Muted, R(243, 294, 12, 20));
            Color bsc; string bst = BurstStatus(out bsc);
            if (bst != null) Draw.Text(g, bst, _f.TinyBold, bsc, R(262, 294, 40, 20));

            // Quiet on battery live state badge (surfaces Engine.QuietActive)
            bool qOn = _app.Engine.QuietOnBattery;
            bool qAct = _app.Engine.QuietActive;
            string qTxt = !qOn ? "off" : qAct ? "active · E-only" : p.Mode == Mode.Default ? "standby · Win" : "standby on AC";
            float qw = Math.Max(42 * _s, Draw.Measure(g, qTxt, _f.TinyBold).Width + 14 * _s);
            var qRect = new RectangleF(424 * _s - qw, 294 * _s, qw, 20 * _s);
            if (qAct)
            {
                Theme.Fill(g, qRect, 4 * _s, Theme.Alpha(Theme.Green, 30));
                Theme.Stroke(g, qRect, 4 * _s, Theme.Alpha(Theme.Green, 120));
                Draw.Text(g, qTxt, _f.TinyBold, Theme.Green, qRect, StringAlignment.Center);
            }
            else
            {
                Theme.Stroke(g, qRect, 4 * _s, Theme.Border);
                Draw.Text(g, qTxt, _f.Tiny, Theme.Faint, qRect, StringAlignment.Center);
            }
        }

        // Dynamic readouts inside AdvCard (CPU Tuning / Inline Fan Curve).
        void PaintRightPaneDynamic(Graphics g, Snapshot s, Reading r, Profile p)
        {
            if (_rightTab == 0)
            {
                int eMhz = s.R.ClusterMhz != null && s.R.ClusterMhz.Length > 0 ? s.R.ClusterMhz[0] : 0;
                int pMhz = s.R.ClusterMhz != null && s.R.ClusterMhz.Length > 2 ? Math.Max(s.R.ClusterMhz[1], s.R.ClusterMhz[2]) : 0;
                string clkTxt = eMhz > 0 || pMhz > 0
                    ? "Clocks: E " + (eMhz / 1000.0).ToString("0.00") + " · P " + (p.EffOnly ? "off" : (pMhz / 1000.0).ToString("0.00") + " GHz")
                    : "Clocks: –";
                Draw.Text(g, clkTxt, _f.Tiny, Theme.Muted, R(664, 22, 170, 24), StringAlignment.Far);

                if (p.Mode == Mode.Default)
                {
                    var wb = R(462, 95, 416, 44);
                    Theme.Fill(g, wb, 7 * _s, Theme.Bg);
                    Theme.Stroke(g, wb, 7 * _s, Theme.Border);
                    Draw.Text(g, "Windows Balanced active — CPU governor stepped aside", _f.Small, Theme.Muted, wb, StringAlignment.Center);
                }
            }
            else
            {
                string chip = double.IsNaN(r.ChipC) ? "– °C" : r.ChipC.ToString("0") + " °C";
                string fans = s.R.FanRpm[0] >= 0 ? s.R.FanRpm[0] + " / " + s.R.FanRpm[1] + " rpm" : "fans –";
                string who = !s.FanOn ? "ASUS " + Hardware.AsusLabel(p.Asus)
                    : s.FanState != null ? "EC override"
                    : s.FanPwm == 0 ? "fans off"
                    : s.FanPwm > 0 ? Math.Round(s.FanPwm / 2.55) + "% PWM"
                    : "starting";
                Draw.Text(g, chip + " · " + fans + " · " + who, _f.TinyBold, s.FanOn && s.FanState != null ? Theme.Amber : Theme.Teal, R(662, 22, 172, 24), StringAlignment.Far);

                string foot = _curveEdit.DragSummary
                    ?? (s.FanOn && s.FanState != null ? s.FanState : "Off <" + _curveEdit.Curve.T[0].ToString("0") + " °C · ASUS ≥90 °C");
                Color footCol = _curveEdit.Dragging ? Theme.Text : (s.FanOn && s.FanState != null ? Theme.Amber : Theme.Faint);
                Draw.Text(g, foot, _f.Tiny, footCol, R(718, 286, 160, 24), StringAlignment.Far);
            }
        }

        void Card(Graphics g, RectangleF lr, string title)
        {
            var c = R(lr.X, lr.Y, lr.Width, lr.Height);
            Theme.Fill(g, c, 10 * _s, Theme.Card);
            Theme.Stroke(g, c, 10 * _s, Theme.Border);
            if (!string.IsNullOrEmpty(title))
                Draw.Text(g, title.ToUpperInvariant(), _f.TinyBold, Theme.Muted, new RectangleF(c.X + 12 * _s, c.Y + 7 * _s, c.Width - 24 * _s, 18 * _s));
        }

        void PaintBattery(Graphics g, RectangleF b, int pct, bool ac)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            Theme.Stroke(g, b, 1.8f * _s, Theme.Muted, 1.3f * _s);
            using (var br = new SolidBrush(Theme.Muted))
                g.FillRectangle(br, b.Right + 0.8f * _s, b.Y + b.Height * 0.27f, 2.1f * _s, b.Height * 0.46f);
            var col = ac ? Theme.Green : pct <= 15 ? Theme.Red : pct <= 30 ? Theme.Amber : Theme.Text;
            float fw = (b.Width - 4 * _s) * pct / 100f;
            if (fw > 0.6f)
                using (var br = new SolidBrush(col))
                    g.FillRectangle(br, b.X + 2 * _s, b.Y + 2 * _s, fw, b.Height - 4 * _s);
        }

        static string Ghz(int mhz, int max) { return mhz == 0 || mhz >= max ? "free" : (mhz / 1000.0).ToString("0.00") + " GHz"; }
        public static string Hm(int min) { return min >= 60 ? (min / 60) + "h " + (min % 60).ToString("00") + "m" : min + "m"; }

        // ---- cached static layer: background, cards, static dividers and titles; rebuilt on resize or tab switch ----
        Bitmap _bg;
        Bitmap PaintStatic()
        {
            int bw = Math.Max(1, Math.Max(ClientSize.Width, (int)Math.Ceiling(LW * _s)));
            int bh = Math.Max(1, Math.Max(ClientSize.Height, (int)Math.Ceiling(LH * _s)));
            var b = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Theme.Bg);

                // Header logo + brand
                PaintLogo(g, R(12, 14, 18, 18));
                Draw.Text(g, "Simmer", _f.Title, Theme.Text, R(33, 10, 82, 26));

                // Hero Card static labels & vertical divider
                Card(g, HeroCard, "");
                using (var pen = new Pen(Theme.Border, 1))
                    g.DrawLine(pen, 182 * _s, 56 * _s, 182 * _s, 128 * _s);
                Draw.Text(g, "SoC", _f.Small, Theme.Muted, R(194, 54, 38, 20));
                Draw.Text(g, "CPU · GPU", _f.Small, Theme.Muted, R(194, 81, 66, 20));
                Draw.Text(g, "Chip", _f.Small, Theme.Muted, R(194, 108, 42, 20));

                // Primary Controls Card
                Card(g, CtlCard, "SoC power cap");
                Draw.Text(g, "Split", _f.Small, Theme.Muted, R(24, 217, 34, 28));

                if (_simple) return b;

                // Right Column Card
                Card(g, AdvCard, "");
                if (_rightTab == 0)
                {
                    Draw.Text(g, "Processor turbo boost", _f.Small, Theme.Muted, R(462, 218, 200, 26));
                    using (var pen = new Pen(Theme.Border, 1))
                        g.DrawLine(pen, 462 * _s, 264 * _s, 878 * _s, 264 * _s);
                }

                // Bottom Row Cards
                Card(g, ChartCard, "Last 3 minutes");
                Card(g, PartsCard, "Where the power goes");
                using (var pen = new Pen(Theme.Border, 1))
                    g.DrawLine(pen, 462 * _s, 400 * _s, 878 * _s, 400 * _s);
            }
            return b;
        }

        // ---- smooth values: shown readings glide toward each new sample, paced by 120 Hz DwmFlush ----
        readonly Reading _shown = new Reading();
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        readonly ManualResetEvent _animWake = new ManualResetEvent(false);
        volatile bool _animOn;
        Thread _animThread;
        readonly System.Diagnostics.Stopwatch _animClock = System.Diagnostics.Stopwatch.StartNew();
        double _animLast, _animStart;
        int _animCount;
        volatile bool _animPainted;
        double _animFrames;

        void AnimLoop()
        {
            while (!IsDisposed)
            {
                _animWake.WaitOne();
                if (!_animOn) continue;
                _animPainted = false;
                try { Invoke((Action)AnimStep); }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch (Exception e) { Store.Log("anim: " + e.Message); _animOn = false; _animWake.Reset(); continue; }
                if (!_animOn) continue;
                if (_animPainted) { try { DwmFlush(); } catch { Thread.Sleep(8); } } else Thread.Sleep(8);
            }
        }

        static double Ease(double cur, double to, ref bool moving)
        {
            if (double.IsNaN(to)) return to;
            if (double.IsNaN(cur)) return to;
            double d = to - cur;
            if (Math.Abs(d) < 0.02) return to;
            moving = true;
            return cur + d * Widget.Rate(0.3);
        }

        void AnimStep()
        {
            var t = _snap.R;
            bool m = false;
            double now = _animClock.Elapsed.TotalMilliseconds;
            Widget.Dt = Math.Max(0.1, Math.Min(4, (now - _animLast) / (1000.0 / 60)));
            _animLast = now; _animCount++;
            _shown.Valid = t.Valid;
            for (int i = 0; i < 3; i++) _shown.Cluster[i] = Ease(_shown.Cluster[i], t.Cluster[i], ref m);
            _shown.Cpu = Ease(_shown.Cpu, t.Cpu, ref m); _shown.Gpu = Ease(_shown.Gpu, t.Gpu, ref m); _shown.Npu = Ease(_shown.Npu, t.Npu, ref m);
            _shown.Memory = Ease(_shown.Memory, t.Memory, ref m); _shown.Infra = Ease(_shown.Infra, t.Infra, ref m); _shown.Soc = Ease(_shown.Soc, t.Soc, ref m);
            _shown.System = Ease(_shown.System, t.System, ref m); _shown.Multimedia = Ease(_shown.Multimedia, t.Multimedia, ref m); _shown.Usbc = Ease(_shown.Usbc, t.Usbc, ref m);
            _shown.ChipC = Ease(_shown.ChipC, t.ChipC, ref m);
            _animFrames += Widget.Dt;
            bool readings = m && _animFrames <= 24;
            if (!readings) _shown.Cpu = t.Cpu;
            if (m || _animFrames <= 25)
            {
                Invalidate(Rectangle.Ceiling(R(HeroCard.X, HeroCard.Y, HeroCard.Width, HeroCard.Height)));
                if (!_simple) Invalidate(Rectangle.Ceiling(R(PartsCard.X, PartsCard.Y, PartsCard.Width, PartsCard.Height)));
            }
            bool widgets = false;
            foreach (var w in _w) if (w.Animate()) { widgets = true; Dirty(w); }
            bool view = ViewStep(now);
            Update();   // paint this frame now; the loop then waits for the vblank that shows it
            _animPainted = m || readings || widgets || view || _animFrames <= 25;
            if (!readings && !widgets && !view && _viewT0 < 0) StopAnim();
        }

        // Cleaner 3-minute history chart: even 5 W-multiple Y ticks, -3m/-90s/now labels, active cap dashed line, and optional + °C overlay.
        void PaintChart(Graphics g, Snapshot s, Profile p)
        {
            var e = _app.Engine;
            double[] cpu, gpu, sys, tgt;
            int n;
            lock (e.Lock)
            {
                cpu = (double[])e.HistCpu.Clone();
                gpu = (double[])e.HistGpu.Clone();
                sys = (double[])e.HistSys.Clone();
                tgt = (double[])e.HistTarget.Clone();
                n = e.HistCount;
            }

            // Legend (left of the + °C toggle button at x=382)
            float lx = 376 * _s;
            float ly = (ChartCard.Y + 8) * _s;
            foreach (var it in new[]
            {
                Tuple.Create("Cap", Theme.Amber, true),
                Tuple.Create("Laptop", Theme.Muted, false),
                Tuple.Create("GPU", Theme.Green, false),
                Tuple.Create("CPU", Theme.Accent, false)
            })
            {
                float tw = Draw.Measure(g, it.Item1, _f.Tiny).Width;
                lx -= tw + 4 * _s;
                Draw.Text(g, it.Item1, _f.Tiny, Theme.Muted, lx, ly);
                if (it.Item3)
                {
                    using (var dp = new Pen(it.Item2, 1.6f * _s) { DashStyle = DashStyle.Dash })
                        g.DrawLine(dp, lx - 11 * _s, ly + 7.5f * _s, lx - 2 * _s, ly + 7.5f * _s);
                }
                else
                {
                    using (var b = new SolidBrush(it.Item2))
                        g.FillRectangle(b, lx - 11 * _s, ly + 6 * _s, 8.5f * _s, 3 * _s);
                }
                lx -= 17 * _s;
            }

            var area = R(ChartCard.X + 30, ChartCard.Y + 32, ChartCard.Width - 46, ChartCard.Height - 52);
            int L = Engine.HistoryLen, start = L - n;

            double rawMax = 12;
            if (p.SocW > 0 && p.SocW <= 45) rawMax = Math.Max(rawMax, p.SocW);
            for (int i = start; i < L; i++) rawMax = Math.Max(rawMax, Math.Max(sys[i], Math.Max(cpu[i], tgt[i])));

            int steps;
            double max;
            if (rawMax <= 15) { max = 15; steps = 3; }
            else if (rawMax <= 20) { max = 20; steps = 4; }
            else if (rawMax <= 30) { max = 30; steps = 3; }
            else if (rawMax <= 45) { max = 45; steps = 3; }
            else { steps = 4; max = Math.Ceiling(rawMax / 20.0) * 20; }

            using (var pen = new Pen(Color.FromArgb(32, 36, 46), 1))
                for (int k = 0; k <= steps; k++)
                {
                    float yy = area.Bottom - area.Height * k / steps;
                    g.DrawLine(pen, area.X, yy, area.Right, yy);
                    Draw.Text(g, (max * k / steps).ToString("0"), _f.Tiny, Theme.Faint,
                        new RectangleF(area.X - 29 * _s, yy - 7 * _s, 25 * _s, 14 * _s), StringAlignment.Far);
                }

            // Time markers
            float ty = area.Bottom + 2 * _s;
            Draw.Text(g, "-3m", _f.Tiny, Theme.Faint, new RectangleF(area.X, ty, 40 * _s, 14 * _s), StringAlignment.Near);
            Draw.Text(g, "-90s", _f.Tiny, Theme.Faint, new RectangleF(area.X + area.Width / 2f - 25 * _s, ty, 50 * _s, 14 * _s), StringAlignment.Center);
            Draw.Text(g, "now", _f.Tiny, Theme.Faint, new RectangleF(area.Right - 40 * _s, ty, 40 * _s, 14 * _s), StringAlignment.Far);

            if (n < 2)
            {
                Draw.Text(g, "collecting…", _f.Small, Theme.Faint, area, StringAlignment.Center);
                return;
            }

            Func<int, double, PointF> pt = (i, v) => new PointF(
                area.X + area.Width * i / (L - 1),
                (float)(area.Bottom - area.Height * Math.Max(0, Math.Min(1, v / max))));

            // CPU filled gradient area
            var poly = new List<PointF> { new PointF(pt(start, 0).X, area.Bottom) };
            for (int i = start; i < L; i++) poly.Add(pt(i, cpu[i]));
            poly.Add(new PointF(pt(L - 1, 0).X, area.Bottom));
            using (var b = new LinearGradientBrush(new RectangleF(area.X, area.Y - 1, area.Width, area.Height + 2), Theme.Alpha(Theme.Accent, 88), Theme.Alpha(Theme.Accent, 5), 90f))
                g.FillPolygon(b, poly.ToArray());

            Line(g, start, L, sys, pt, Theme.Muted, 1.4f);
            Line(g, start, L, gpu, pt, Theme.Green, 1.4f);
            Line(g, start, L, cpu, pt, Theme.Accent, 2f);

            // Target / SoC cap dashed line
            using (var pen = new Pen(Theme.Amber, 1.3f * _s) { DashStyle = DashStyle.Dash })
            {
                bool drewTarget = false;
                for (int i = start + 1; i < L; i++)
                    if (tgt[i] > 0 && tgt[i - 1] > 0) { g.DrawLine(pen, pt(i - 1, tgt[i - 1]), pt(i, tgt[i])); drewTarget = true; }
                if (!drewTarget && p.SocW > 0 && p.SocW <= max)
                {
                    float cy = (float)(area.Bottom - area.Height * (p.SocW / max));
                    g.DrawLine(pen, area.X, cy, area.Right, cy);
                }
            }

            // Optional Chip °C overlay (25-90 °C mapped to plot height)
            if (_showTempLine)
            {
                double[] hc; int hn;
                _app.CopyChipHistory(out hc, out hn);
                int hStart = L - hn;
                var cPts = new List<PointF>();
                for (int i = Math.Max(start, hStart); i < L; i++)
                {
                    if (double.IsNaN(hc[i])) continue;
                    float x = area.X + area.Width * i / (L - 1);
                    float y = (float)(area.Bottom - area.Height * Math.Max(0, Math.Min(1, (hc[i] - 25.0) / 65.0)));
                    cPts.Add(new PointF(x, y));
                }
                if (cPts.Count > 1)
                    using (var pen = new Pen(Theme.Purple, 1.5f * _s) { DashStyle = DashStyle.Dot, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, cPts.ToArray());
                Draw.Text(g, "90°", _f.Tiny, Theme.Purple, new RectangleF(area.Right - 22 * _s, area.Y - 1 * _s, 22 * _s, 13 * _s), StringAlignment.Far);
                Draw.Text(g, "25°", _f.Tiny, Theme.Purple, new RectangleF(area.Right - 22 * _s, area.Bottom - 13 * _s, 22 * _s, 13 * _s), StringAlignment.Far);
            }
        }

        void Line(Graphics g, int start, int L, double[] v, Func<int, double, PointF> pt, Color c, float w)
        {
            var pts = new PointF[L - start];
            for (int i = start; i < L; i++) pts[i - start] = pt(i, v[i]);
            if (pts.Length > 1)
                using (var pen = new Pen(c, w * _s) { LineJoin = LineJoin.Round })
                    g.DrawLines(pen, pts);
        }

        struct RailItem
        {
            public string Name, ShortName;
            public double W;
            public Color Col;
            public RailItem(string n, string sn, double w, Color c) { Name = n; ShortName = sn; W = Math.Max(0, w); Col = c; }
        }

        // Two-tier Power Breakdown: Tier 1 System stacked bar (SoC vs Board/Display/SSD) + Tier 2 SoC-scaled rails with grouped <0.02 W idle rails.
        void PaintParts(Graphics g, Reading r)
        {
            Draw.Text(g, r.Valid ? r.System.ToString("0.00") + " W laptop total" : "–", _f.Tiny, Theme.Faint, R(670, 348, 208, 16), StringAlignment.Far);

            // Tier 1: System stacked bar (SoC vs Display/SSD/Wi-Fi/rest)
            double soc = Math.Max(0, r.Soc);
            double rest = Math.Max(0, r.System - soc - Math.Max(0, r.Usbc));
            double total = Math.Max(0.01, soc + rest);
            int socPct = r.Valid ? (int)Math.Round(soc / total * 100) : 0;

            var sb = R(462, 369, 416, 8);
            Theme.Fill(g, sb, 4 * _s, Theme.Bg);
            if (r.Valid)
            {
                float gap = 2 * _s;
                float sw = (float)Math.Max(6 * _s, Math.Min(sb.Width - 6 * _s, sb.Width * (soc / total)));
                Theme.Fill(g, new RectangleF(sb.X, sb.Y, sw - gap / 2f, sb.Height), 3.5f * _s, Theme.Pink);
                Theme.Fill(g, new RectangleF(sb.X + sw + gap / 2f, sb.Y, Math.Max(2 * _s, sb.Width - sw - gap / 2f), sb.Height), 3.5f * _s, Theme.Slate);
            }
            string socLbl = r.Valid ? string.Format("SoC total: {0:0.00} W ({1}%)", soc, socPct) : "SoC total: –";
            string restLbl = r.Valid ? string.Format("Display, SSD, Wi-Fi: {0:0.00} W ({1}%)", rest, 100 - socPct) : "Display, SSD, Wi-Fi: –";
            Draw.Text(g, socLbl, _f.Tiny, Theme.Text, R(462, 380, 200, 16), StringAlignment.Near);
            Draw.Text(g, restLbl, _f.Tiny, Theme.Muted, R(652, 380, 226, 16), StringAlignment.Far);

            // Tier 2: SoC-scaled sub-rails (group <0.02 W rails and cap visible bars at 5 + summary so bars always fit and scale legibly)
            string gpuLbl = !double.IsNaN(_snap.R.Gpu3d) && _snap.R.Gpu3d >= 5 ? "GPU · " + _snap.R.Gpu3d.ToString("0") + "% 3D" : "GPU";
            var all = new[]
            {
                new RailItem("Fast cores A+B (12)", "Fast", r.Cluster[1] + r.Cluster[2], Theme.Accent),
                new RailItem("Efficient cores (6)", "E-cores", r.Cluster[0], Theme.Teal),
                new RailItem(gpuLbl, "GPU", r.Gpu, Theme.Green),
                new RailItem("Memory", "Memory", r.Memory, Theme.Amber),
                new RailItem("Fabric / infra", "Fabric", r.Infra, Theme.Pink),
                new RailItem("NPU", "NPU", r.Npu, Theme.Purple),
                new RailItem("Media engine", "Media", r.Multimedia, Theme.Pink),
                new RailItem("USB-C ports", "USB-C", r.Usbc, Theme.Muted)
            };

            var active = new List<RailItem>();
            var idle = new List<RailItem>();
            foreach (var it in all)
            {
                if (!r.Valid || it.W >= 0.02) active.Add(it);
                else idle.Add(it);
            }

            // If more than 5 rails are >= 0.02 W simultaneously, fold the smallest excess ones into the bottom summary so layout never overflows.
            int maxBars = idle.Count > 0 ? 5 : 6;
            while (active.Count > maxBars)
            {
                int minIdx = 0;
                for (int i = 1; i < active.Count; i++) if (active[i].W <= active[minIdx].W) minIdx = i;
                idle.Insert(0, active[minIdx]);
                active.RemoveAt(minIdx);
            }
            if (active.Count == 0 && idle.Count > 0) { active.Add(idle[0]); idle.RemoveAt(0); }

            double maxRail = 0.5;
            foreach (var it in active) maxRail = Math.Max(maxRail, it.W * 1.08);

            int totalRows = active.Count + (idle.Count > 0 ? 1 : 0);
            float availH = 119f * _s;
            float lh = Math.Min(19.5f * _s, availH / Math.Max(1, totalRows));
            float y = 405f * _s, x = 462f * _s, w = 416f * _s;
            float nameW = 136f * _s, valW = 52f * _s, barX = x + nameW + 6f * _s, barW = w - nameW - valW - 12f * _s;

            foreach (var it in active)
            {
                Draw.Text(g, it.Name, _f.Small, Theme.Muted, new RectangleF(x, y, nameW, lh));
                var trackR = new RectangleF(barX, y + (lh - 6 * _s) / 2f, barW, 6 * _s);
                Theme.Fill(g, trackR, 3 * _s, Theme.Bg);
                if (r.Valid && it.W > 0.002)
                {
                    float fw = (float)(barW * Math.Min(1, it.W / maxRail));
                    Theme.Fill(g, new RectangleF(barX, trackR.Y, Math.Max(5 * _s, fw), trackR.Height), 3 * _s, it.Col);
                }
                Draw.Text(g, r.Valid ? it.W.ToString(it.W < 10 ? "0.00" : "0.0") + " W" : "–", _f.SmallBold, Theme.Text,
                    new RectangleF(barX + barW + 4 * _s, y, valW, lh), StringAlignment.Far);
                y += lh;
            }

            if (idle.Count > 0)
            {
                double idleSum = 0;
                var names = new List<string>();
                foreach (var it in idle) { idleSum += it.W; names.Add(it.ShortName); }
                string summary = "Idle rails · " + string.Join(", ", names) + " (" + idle.Count + ")";
                Draw.Text(g, summary, _f.Tiny, Theme.Faint, new RectangleF(x, y + 1 * _s, w - 58 * _s, lh));
                Draw.Text(g, r.Valid ? idleSum.ToString("0.00") + " W" : "–", _f.Tiny, Theme.Faint,
                    new RectangleF(x + w - 56 * _s, y + 1 * _s, 56 * _s, lh), StringAlignment.Far);
            }
        }

        static void PaintLogo(Graphics g, RectangleF r)
        {
            float sx = r.Width / 64f, sy = r.Height / 64f;
            var st = g.Save();
            g.TranslateTransform(r.X, r.Y);
            g.ScaleTransform(sx, sy);
            // Simmer: a pot with three wisps of steam over a small flame
            Theme.Fill(g, new RectangleF(2, 2, 60, 60), 14, Theme.Card);
            Theme.Stroke(g, new RectangleF(2, 2, 60, 60), 14, Theme.Border, 2f);
            using (var pen = new Pen(Theme.Text, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                foreach (float x in new[] { 22f, 32f, 42f })
                    g.DrawBezier(pen, x, 22, x - 5, 17, x + 5, 13, x, 8);
            using (var b = new SolidBrush(Theme.Accent))
            {
                g.FillRectangle(b, 10, 25, 44, 5);                                    // lid rim
                Theme.Fill(g, new RectangleF(14, 28, 36, 18), 6, Theme.Accent);       // pot body
                g.FillRectangle(b, 8, 31, 8, 4); g.FillRectangle(b, 48, 31, 8, 4);    // handles
            }
            using (var b = new SolidBrush(Theme.Amber))
                using (var flame = new GraphicsPath())
                {
                    flame.AddBezier(32, 48, 34, 52, 39, 54, 37, 58);
                    flame.AddBezier(37, 58, 35, 61, 29, 61, 27, 58);
                    flame.AddBezier(27, 58, 25, 54, 30, 52, 32, 48);
                    g.FillPath(b, flame);
                }
            g.Restore(st);
        }

        public static Icon AppIcon()
        {
            using (var bmp = new Bitmap(64, 64))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                PaintLogo(g, new RectangleF(0, 0, 64, 64));
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
