// Inline 5-point fan curve editor widget (CurveEditor) and the display/sleep/AC watcher (PowerWatch)
// that pauses the curve while the screen is off and notifies TrayApp of AC/battery transitions.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PowerCap
{
    // Temperature (x, 30-95 C) against fan % (y, 0-100 %). Drag a point anywhere; Fix() keeps the shape safe.
    sealed class CurveEditor : Widget
    {
        public FanCurve Curve = new FanCurve();
        public Action<FanCurve> Changed, Committed;
        public bool CurveActive = true;
        public double LiveC = double.NaN;   // temperature the curve is using now
        public int LivePwm = -1;            // PWM PowerCap set, -1 = the EC has the fans
        int _drag = -1, _hot = -1;
        const double X0 = 30, X1 = 95;

        RectangleF Plot(float s) { return new RectangleF(Bounds.X + 36 * s, Bounds.Y + 10 * s, Bounds.Width - 46 * s, Bounds.Height - 28 * s); }
        PointF Pt(RectangleF a, double t, double p) { return new PointF((float)(a.X + (t - X0) / (X1 - X0) * a.Width), (float)(a.Bottom - p / 100.0 * a.Height)); }

        int Nearest(PointF p, float s)
        {
            var a = Plot(s);
            int best = -1;
            double bd = 16 * s;
            for (int i = 0; i < Curve.T.Length; i++)
            {
                var q = Pt(a, Curve.T[i], Curve.P[i]);
                double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        public bool Dragging { get { return _drag >= 0; } }

        public string DragSummary
        {
            get
            {
                if (_drag < 0 || _drag >= Curve.T.Length) return null;
                int pwm = FanCurve.Pwm(Curve.P[_drag]);
                return string.Format("Pt {0}: {1:0} °C · {2:0}% (~{3} rpm)", _drag + 1, Curve.T[_drag], Curve.P[_drag], FanCurve.Rpm(pwm));
            }
        }

        public override void Paint(Graphics g, Fonts f)
        {
            float s = f.S;
            var a = Plot(s);
            Theme.Fill(g, Bounds, 8 * s, Theme.Bg);
            Theme.Stroke(g, Bounds, 8 * s, Theme.Border);
            if (a.Width <= 10 || a.Height <= 10) return;

            using (var grid = new Pen(Color.FromArgb(30, 34, 43), 1))
            {
                for (int t = 30; t <= 90; t += 10)
                {
                    float x = Pt(a, t, 0).X;
                    g.DrawLine(grid, x, a.Y, x, a.Bottom);
                    Draw.Text(g, t + "°", f.Tiny, Theme.Faint, new RectangleF(x - 18 * s, a.Bottom + 2 * s, 36 * s, 14 * s), StringAlignment.Center);
                }
                for (int p = 0; p <= 100; p += 25)
                {
                    float y = Pt(a, X0, p).Y;
                    g.DrawLine(grid, a.X, y, a.Right, y);
                    Draw.Text(g, p + "%", f.Tiny, Theme.Faint, new RectangleF(Bounds.X + 2 * s, y - 7 * s, 31 * s, 14 * s), StringAlignment.Far);
                }
            }

            // Off zone (below the first point) and hand-back zone (90 C and over).
            float off = Pt(a, Curve.T[0], 0).X;
            if (off > a.X + 1)
            {
                using (var b = new SolidBrush(Theme.Alpha(Theme.Teal, 28))) g.FillRectangle(b, a.X, a.Y, off - a.X, a.Height);
                if (off - a.X > 36 * s)
                    Draw.Text(g, "fans off", f.Tiny, Theme.Teal, new RectangleF(a.X + 4 * s, a.Y + 3 * s, off - a.X - 6 * s, 14 * s));
            }
            float hb = Pt(a, Fan.HandBackC, 0).X;
            if (a.Right > hb + 1)
            {
                using (var b = new SolidBrush(Theme.Alpha(Theme.Pink, 32))) g.FillRectangle(b, hb, a.Y, a.Right - hb, a.Height);
                Draw.Text(g, "ASUS", f.Tiny, Theme.Pink, new RectangleF(hb, a.Y + 3 * s, a.Right - hb, 14 * s), StringAlignment.Center);
            }

            var strokeCol = !Enabled ? Theme.Faint : CurveActive ? Theme.Accent : Theme.Muted;
            var pts = new List<PointF> { Pt(a, X0, 0), Pt(a, Curve.T[0], 0) };
            for (int i = 0; i < Curve.T.Length; i++) pts.Add(Pt(a, Curve.T[i], Curve.P[i]));
            pts.Add(Pt(a, Fan.HandBackC, Curve.P[Curve.P.Length - 1]));
            using (var pen = new Pen(strokeCol, 2.2f * s) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts.ToArray());

            if (!double.IsNaN(LiveC) && Enabled)
            {
                double clampedC = Math.Max(X0, Math.Min(X1, LiveC));
                float x = Pt(a, clampedC, 0).X;
                using (var pen = new Pen(Theme.Amber, 1.2f * s) { DashStyle = DashStyle.Dash })
                    g.DrawLine(pen, x, a.Y, x, a.Bottom);
                if (CurveActive && LivePwm >= 0)
                {
                    var q = Pt(a, clampedC, LivePwm / 2.55);
                    using (var b = new SolidBrush(Theme.Amber)) g.FillEllipse(b, q.X - 3.8f * s, q.Y - 3.8f * s, 7.6f * s, 7.6f * s);
                }
            }

            for (int i = 0; i < Curve.T.Length; i++)
            {
                var q = Pt(a, Curve.T[i], Curve.P[i]);
                float r = (i == _drag || (Hover && i == _hot)) && Enabled ? 6.8f * s : 5.4f * s;
                using (var b = new SolidBrush(Theme.Text)) g.FillEllipse(b, q.X - r, q.Y - r, 2 * r, 2 * r);
                using (var pen = new Pen(strokeCol, 2 * s)) g.DrawEllipse(pen, q.X - r, q.Y - r, 2 * r, 2 * r);
            }

            if (_drag >= 0 && _drag < Curve.T.Length)
            {
                var q = Pt(a, Curve.T[_drag], Curve.P[_drag]);
                int pwm = FanCurve.Pwm(Curve.P[_drag]);
                string tag = Curve.T[_drag].ToString("0") + " °C · " + Curve.P[_drag].ToString("0") + "% ≈ " + FanCurve.Rpm(pwm) + " rpm";
                float tw = Math.Max(124 * s, Draw.Measure(g, tag, f.TinyBold).Width + 14 * s), th = 18 * s;
                float tx = Math.Max(a.X + 2 * s, Math.Min(a.Right - tw - 2 * s, q.X - tw / 2f));
                float ty = q.Y - 26 * s < a.Y + 2 * s ? q.Y + 10 * s : q.Y - 25 * s;
                var pill = new RectangleF(tx, ty, tw, th);
                Theme.Fill(g, pill, 5 * s, Theme.CardHi);
                Theme.Stroke(g, pill, 5 * s, Theme.BorderHi);
                Draw.Text(g, tag, f.TinyBold, Theme.Text, pill, StringAlignment.Center);
            }
        }

        public override void Move(PointF p)
        {
            float s = Scale;
            if (_drag < 0) { _hot = Nearest(p, s); return; }
            var a = Plot(s);
            if (a.Width <= 1 || a.Height <= 1) return;
            var c = Curve.Clone();
            c.T[_drag] = Math.Round(X0 + (p.X - a.X) / a.Width * (X1 - X0));
            c.P[_drag] = Math.Round((a.Bottom - p.Y) / a.Height * 100 / 5) * 5;
            // Neighbours stay put: the dragged point is held between them.
            if (_drag > 0) c.T[_drag] = Math.Max(c.T[_drag], c.T[_drag - 1] + FanCurve.MinGap);
            if (_drag < c.T.Length - 1) c.T[_drag] = Math.Min(c.T[_drag], c.T[_drag + 1] - FanCurve.MinGap);
            if (_drag > 0) c.P[_drag] = Math.Max(c.P[_drag], c.P[_drag - 1]);
            if (_drag < c.P.Length - 1) c.P[_drag] = Math.Min(c.P[_drag], c.P[_drag + 1]);
            c.Fix();
            Curve = c;
            if (Changed != null) Changed(c);
        }

        public override void Down(PointF p)
        {
            if (Enabled) _drag = Nearest(p, Scale);
        }

        public override void Up(PointF p, bool inside)
        {
            if (_drag < 0) return;
            _drag = -1;
            if (Committed != null) Committed(Curve.Clone());
        }
    }

    // Hidden window for WM_POWERBROADCAST: display off/on (GUID_CONSOLE_DISPLAY_STATE), sleep/resume, and AC/battery switches.
    // Modern Standby throttles desktop apps, so the fan curve hands the fans to the EC before that, not during it.
    sealed class PowerWatch : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
        [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr h);
        static Guid DisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        readonly Action<bool> _paused;
        readonly Action _powerChanged;
        IntPtr _reg;

        public PowerWatch(Action<bool> paused, Action powerChanged = null)
        {
            _paused = paused;
            _powerChanged = powerChanged;
            CreateHandle(new CreateParams());
            _reg = RegisterPowerSettingNotification(Handle, ref DisplayState, 0);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0218)   // WM_POWERBROADCAST
            {
                int ev = m.WParam.ToInt32();
                if (ev == 0x0004) _paused(true);                        // PBT_APMSUSPEND
                else if (ev == 0x0012 || ev == 0x0007)                  // PBT_APMRESUMEAUTOMATIC / RESUMESUSPEND
                {
                    _paused(false);
                    if (_powerChanged != null) _powerChanged();
                }
                else if (ev == 0x000A)                                  // PBT_APMPOWERSTATUSCHANGE (AC <-> Battery)
                {
                    if (_powerChanged != null) _powerChanged();
                }
                else if (ev == 0x8013 && m.LParam != IntPtr.Zero)       // PBT_POWERSETTINGCHANGE: {GUID, DWORD len, data}
                {
                    var g = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                    if (g == DisplayState) _paused(Marshal.ReadInt32(m.LParam, 20) == 0);   // 0 off, 1 on, 2 dimmed
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_reg != IntPtr.Zero) UnregisterPowerSettingNotification(_reg);
            _reg = IntPtr.Zero;
            DestroyHandle();
        }
    }
}
