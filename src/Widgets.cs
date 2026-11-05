// Owner-drawn dark-theme widget kit: cards, segmented controls, sliders, inline split bar, toggles, pills, and split-buttons.
// Everything is painted by Dashboard; widgets hold state, layout, animation, and mouse handling.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PowerCap
{
    static class Theme
    {
        public static readonly Color Bg = Color.Black, Card = Color.FromArgb(10, 10, 12), CardHi = Color.FromArgb(24, 25, 30),   // AMOLED: true black
            Border = Color.FromArgb(38, 42, 52), BorderHi = Color.FromArgb(58, 65, 80),
            Text = Color.FromArgb(232, 234, 237), Muted = Color.FromArgb(147, 156, 171),
            Faint = Color.FromArgb(115, 124, 140), Accent = Color.FromArgb(76, 194, 255), Green = Color.FromArgb(91, 211, 140),
            Amber = Color.FromArgb(245, 184, 75), Red = Color.FromArgb(255, 107, 107), Purple = Color.FromArgb(180, 140, 255),
            Pink = Color.FromArgb(255, 128, 191), Teal = Color.FromArgb(64, 214, 200), Slate = Color.FromArgb(78, 86, 104);
        public const string FontName = "Segoe UI";

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }
        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w = 1)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }
    }

    abstract class Widget
    {
        public RectangleF Bounds;
        public float Scale = 1f;
        public bool Enabled = true, Visible = true, Hover, Pressed;
        public string Tip;
        public abstract void Paint(Graphics g, Fonts f);
        public virtual void Down(PointF p) { }
        public virtual void Move(PointF p) { }
        public virtual void Up(PointF p, bool inside) { }
        public virtual bool Wheel(int delta) { return false; }
        public virtual string TipAt(PointF p) { return Tip; }
        // Advances any transition by one frame; true while still moving.
        public virtual bool Animate() { return false; }
        // Time since the last frame in 60 Hz frames (0.5 at 120 Hz), so easing speed does not depend on the refresh rate.
        public static double Dt = 1;
        public static double Rate(double perFrame60) { return 1 - Math.Pow(1 - perFrame60, Dt); }
        // Eases v toward target; true while still moving.
        protected static bool Glide(ref double v, double target, double eps)
        {
            if (double.IsNaN(v) || Math.Abs(target - v) < eps) { bool moved = !double.IsNaN(v) && v != target; v = target; return moved; }
            v += (target - v) * Rate(0.28);
            return true;
        }
    }

    sealed class Fonts : IDisposable
    {
        public Font Title, H, Body, Bold, Small, SmallBold, Tiny, TinyBold, Big, Mid;
        public float S;
        public Fonts(float scale)
        {
            S = scale;
            Title = new Font(Theme.FontName, 14.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            H = new Font(Theme.FontName, 12.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            Body = new Font(Theme.FontName, 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Bold = new Font(Theme.FontName, 12.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            Small = new Font(Theme.FontName, 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            SmallBold = new Font(Theme.FontName, 11.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            Tiny = new Font(Theme.FontName, 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            TinyBold = new Font(Theme.FontName, 10.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            Big = new Font("Segoe UI Semibold", 38f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Mid = new Font("Segoe UI Semibold", 20f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        public void Dispose()
        {
            foreach (var x in new[] { Title, H, Body, Bold, Small, SmallBold, Tiny, TinyBold, Big, Mid }) x.Dispose();
        }
    }

    static class Draw
    {
        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
        {
            if (string.IsNullOrEmpty(s)) return;
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(s, f, b, r, sf);
        }
        public static void Text(Graphics g, string s, Font f, Color c, float x, float y)
        {
            if (string.IsNullOrEmpty(s)) return;
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }
        public static void TextTypo(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
        {
            if (string.IsNullOrEmpty(s)) return;
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(s, f, b, r, sf);
        }
        public static SizeF Measure(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return SizeF.Empty;
            return g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);
        }
    }

    // A row of mutually exclusive options (supports standard, compact, tiny-pill, wheel-stepping, and per-slot indicator dots).
    sealed class Segmented : Widget
    {
        public string[] Labels;
        public string[] ItemTips;
        public Color?[] Dots;         // optional corner indicator dot per item (e.g. AC/Battery bound slots)
        public Color?[] SecondDots;   // optional second corner dot when bound to both
        public int Selected = -1, HoverIndex = -1;
        public Action<int> Changed;
        public Func<int, bool> OnWheel;
        public Color Accent = Theme.Accent;
        public bool Compact, TinyText;
        int _down = -1;
        double _pos = double.NaN;   // highlight position (index units), slides toward Selected

        public override bool Animate()
        {
            if (Selected < 0) { _pos = double.NaN; return false; }
            if (double.IsNaN(_pos)) { _pos = Selected; return false; }
            return Glide(ref _pos, Selected, 0.004);
        }

        public int IndexAt(PointF p)
        {
            if (!Bounds.Contains(p) || Labels == null || Labels.Length == 0) return -1;
            return Math.Max(0, Math.Min(Labels.Length - 1, (int)((p.X - Bounds.X) / (Bounds.Width / Labels.Length))));
        }

        public override string TipAt(PointF p)
        {
            int i = IndexAt(p);
            if (i >= 0 && ItemTips != null && i < ItemTips.Length && !string.IsNullOrEmpty(ItemTips[i])) return ItemTips[i];
            return Tip;
        }

        public override void Paint(Graphics g, Fonts f)
        {
            if (Labels == null || Labels.Length == 0) return;
            float s = f.S, w = Bounds.Width / Labels.Length, rad = (Compact ? 6f : 7f) * s;
            Theme.Fill(g, Bounds, rad, Theme.Bg);
            Theme.Stroke(g, Bounds, rad, Theme.Border);
            for (int i = 0; i < Labels.Length; i++)
                if (Enabled && i == HoverIndex && i != Selected)
                    Theme.Fill(g, new RectangleF(Bounds.X + i * w + 2 * s, Bounds.Y + 2 * s, w - 4 * s, Bounds.Height - 4 * s), rad - 2 * s, Theme.CardHi);
            if (Selected >= 0 && Selected < Labels.Length)
            {
                double at = double.IsNaN(_pos) ? Selected : _pos;
                var hr = new RectangleF(Bounds.X + (float)at * w + 2 * s, Bounds.Y + 2 * s, w - 4 * s, Bounds.Height - 4 * s);
                Theme.Fill(g, hr, rad - 2 * s, Enabled ? Theme.Alpha(Accent, 46) : Theme.CardHi);
                Theme.Stroke(g, hr, rad - 2 * s, Enabled ? Theme.Alpha(Accent, 180) : Theme.Faint, 1.2f * s);
            }
            Font reg = TinyText ? f.Tiny : Compact ? f.Small : f.Body;
            Font selF = TinyText ? f.TinyBold : Compact ? f.SmallBold : f.Bold;
            for (int i = 0; i < Labels.Length; i++)
            {
                var r = new RectangleF(Bounds.X + i * w + 2 * s, Bounds.Y + 2 * s, w - 4 * s, Bounds.Height - 4 * s);
                bool sel = i == Selected;
                var col = !Enabled ? Theme.Faint : sel ? Theme.Text : (i == HoverIndex ? Theme.Text : Theme.Muted);
                if (TinyText) Draw.TextTypo(g, Labels[i], sel ? selF : reg, col, r, StringAlignment.Center);
                else Draw.Text(g, Labels[i], sel ? selF : reg, col, r, StringAlignment.Center);

                if (Dots != null && i < Dots.Length && Dots[i].HasValue)
                {
                    float dr = 4f * s, dx = r.Right - 6f * s, dy = r.Y + 3f * s;
                    using (var b = new SolidBrush(Dots[i].Value)) g.FillEllipse(b, dx, dy, dr, dr);
                    if (SecondDots != null && i < SecondDots.Length && SecondDots[i].HasValue)
                        using (var b = new SolidBrush(SecondDots[i].Value)) g.FillEllipse(b, dx - 5.5f * s, dy, dr, dr);
                }
            }
        }

        public override void Move(PointF p) { HoverIndex = IndexAt(p); }
        public override void Down(PointF p) { _down = IndexAt(p); }
        public override void Up(PointF p, bool inside)
        {
            int i = IndexAt(p);
            if (Enabled && i >= 0 && i == _down) { Selected = i; if (Changed != null) Changed(i); }
            _down = -1;
        }
        public override bool Wheel(int delta) { return Enabled && OnWheel != null && OnWheel(delta); }
    }

    // Horizontal slider (standard labeled mode with optional sub-hints and live marker, or compact Inline track-only mode).
    sealed class Slider : Widget
    {
        public double Min, Max, Value, Step = 1;
        public Func<double, string> Format = v => v.ToString("0");
        public string Label, LeftHint, RightHint;
        public Color Accent = Theme.Accent, LeftHintColor = Color.Empty;
        public Action<double> Changed, Committed;
        public double? Marker;   // live governor / clock / GPU marker
        public Color MarkerColor = Theme.Amber;
        public bool Auto;        // value is driven automatically: hollow knob and dimmed fill
        public bool Inline;      // track-only mode (for one-line CPU/GPU budget split)
        public Color? Rest;      // right-side fill color for two-tone split bar
        bool _drag;
        float _dragX;
        double _shown = double.NaN;

        public override bool Animate() { return !_drag && Glide(ref _shown, Value, (Max - Min) * 0.0015); }

        RectangleF Track(float s)
        {
            if (Inline) return new RectangleF(Bounds.X + 7 * s, Bounds.Y + (Bounds.Height - 4 * s) / 2f, Bounds.Width - 14 * s, 4 * s);
            return new RectangleF(Bounds.X + 7 * s, Bounds.Y + 29 * s, Bounds.Width - 14 * s, 4 * s);
        }

        double ValueAt(PointF p)
        {
            var t = Track(Scale);
            if (t.Width <= 1) return Value;
            double v = Min + (p.X - t.X) / t.Width * (Max - Min);
            v = Math.Round(v / Step) * Step;
            return Math.Max(Min, Math.Min(Max, v));
        }

        public override void Paint(Graphics g, Fonts f)
        {
            float s = f.S;
            bool auto = Auto && !_drag;
            if (!Inline)
            {
                var head = new RectangleF(Bounds.X, Bounds.Y, Bounds.Width, 18 * s);
                Draw.Text(g, Label, f.Small, Enabled ? Theme.Text : Theme.Faint, head);
                Draw.Text(g, auto ? "auto · " + Format(Value) : Format(Value), f.SmallBold, !Enabled ? Theme.Faint : auto ? Theme.Muted : Accent, head, StringAlignment.Far);
            }

            var t = Track(s);
            double kv = double.IsNaN(_shown) ? Value : _shown;
            float x = _drag ? Math.Max(t.X, Math.Min(t.Right, _dragX)) : (float)(t.X + Math.Max(0, Math.Min(1, (kv - Min) / (Max - Min))) * t.Width);

            if (Rest.HasValue)
            {
                int aLeft = !Enabled ? 50 : auto ? 85 : 225;
                int aRight = !Enabled ? 40 : auto ? 70 : 210;
                Theme.Fill(g, t, 2 * s, Theme.Border);
                if (x > t.X + 1) Theme.Fill(g, new RectangleF(t.X, t.Y, x - t.X, t.Height), 2 * s, Theme.Alpha(Accent, aLeft));
                if (t.Right > x + 1) Theme.Fill(g, new RectangleF(x, t.Y, t.Right - x, t.Height), 2 * s, Theme.Alpha(Rest.Value, aRight));
            }
            else
            {
                Theme.Fill(g, t, 2 * s, Theme.Border);
                if (x > t.X + 0.5f)
                    Theme.Fill(g, new RectangleF(t.X, t.Y, x - t.X, t.Height), 2 * s, !Enabled ? Theme.Faint : auto ? Theme.Alpha(Accent, 90) : Accent);
            }

            if (Marker.HasValue && Enabled && !Inline)
            {
                float mx = (float)(t.X + (Math.Max(Min, Math.Min(Max, Marker.Value)) - Min) / (Max - Min) * t.Width);
                using (var b = new SolidBrush(MarkerColor))
                    g.FillPolygon(b, new[] { new PointF(mx - 4 * s, t.Y - 7 * s), new PointF(mx + 4 * s, t.Y - 7 * s), new PointF(mx, t.Y - 1 * s) });
            }

            float r = (Hover || _drag) && Enabled ? 7.5f * s : 6.5f * s;
            using (var b = new SolidBrush(!Enabled ? Theme.Faint : auto ? Theme.Card : Theme.Text))
                g.FillEllipse(b, x - r, t.Y + t.Height / 2 - r, 2 * r, 2 * r);
            if (Enabled)
                using (var pen = new Pen(auto ? Theme.Alpha(Accent, 145) : Accent, 2 * s))
                    g.DrawEllipse(pen, x - r, t.Y + t.Height / 2 - r, 2 * r, 2 * r);

            if (!Inline)
            {
                float hy = t.Bottom + 6 * s;
                if (LeftHint != null)
                    Draw.Text(g, LeftHint, f.Tiny, LeftHintColor != Color.Empty ? LeftHintColor : Theme.Faint, new RectangleF(Bounds.X, hy, Bounds.Width * 0.68f, 14 * s), StringAlignment.Near, StringAlignment.Center);
                if (RightHint != null)
                    Draw.Text(g, RightHint, f.Tiny, Theme.Faint, new RectangleF(Bounds.X + Bounds.Width * 0.5f, hy, Bounds.Width * 0.5f, 14 * s), StringAlignment.Far, StringAlignment.Center);
            }
        }

        public override void Down(PointF p) { if (!Enabled) return; _drag = true; _dragX = p.X; Set(ValueAt(p)); }
        public override void Move(PointF p) { if (_drag) { _dragX = p.X; Set(ValueAt(p)); } }
        public override void Up(PointF p, bool inside)
        {
            if (!_drag) return;
            _drag = false;
            var t = Track(Scale);
            if (t.Width > 1) _shown = Min + Math.Max(0, Math.Min(1, (_dragX - t.X) / t.Width)) * (Max - Min);
            if (Committed != null) Committed(Value);
        }
        public override bool Wheel(int delta)
        {
            if (!Enabled) return false;
            Set(Math.Max(Min, Math.Min(Max, Value + Math.Sign(delta) * Step)));
            if (Committed != null) Committed(Value);
            return true;
        }
        void Set(double v) { if (v == Value) return; Value = v; if (Changed != null) Changed(v); }
        public bool Dragging { get { return _drag; } }
    }

    sealed class Toggle : Widget
    {
        public bool On, Compact;
        public string Label;
        public Action<bool> Changed;
        double _k = double.NaN;   // knob position 0..1

        public override bool Animate() { return Glide(ref _k, On ? 1 : 0, 0.01); }

        public override void Paint(Graphics g, Fonts f)
        {
            float s = f.S;
            float swW = (Compact ? 34f : 36f) * s, swH = (Compact ? 18f : 19f) * s;
            var sw = new RectangleF(Bounds.X, Bounds.Y + (Bounds.Height - swH) / 2f, swW, swH);
            double kk = double.IsNaN(_k) ? (On ? 1 : 0) : _k;
            Theme.Fill(g, sw, swH / 2f, Enabled ? Theme.Mix(Theme.BorderHi, Theme.Accent, kk) : Theme.Border);
            float d = swH - 6 * s;
            float k = (float)(sw.X + 3 * s + kk * (swW - d - 6 * s));
            using (var b = new SolidBrush(!Enabled ? Theme.Faint : On ? Theme.Bg : Theme.Text))
                g.FillEllipse(b, k, sw.Y + 3 * s, d, d);
            Draw.Text(g, Label, Compact ? f.Small : f.Body, !Enabled ? Theme.Faint : Hover ? Theme.Text : Theme.Text,
                new RectangleF(sw.Right + 8 * s, Bounds.Y, Bounds.Width - swW - 8 * s, Bounds.Height));
        }

        public override void Up(PointF p, bool inside)
        {
            if (inside && Enabled) { On = !On; if (Changed != null) Changed(On); }
        }
    }

    // Pill / link button with optional active state and custom vector icon callback.
    sealed class LinkButton : Widget
    {
        public string Label;
        public Action Click;
        public Color Color = Theme.Muted;
        public bool Active, TinyFont;
        public Action<Graphics, RectangleF, float, Color> IconPaint;

        public override void Paint(Graphics g, Fonts f)
        {
            float s = f.S, rad = 6 * s;
            if (Active)
            {
                Theme.Fill(g, Bounds, rad, Hover ? Theme.Alpha(Color, 62) : Theme.Alpha(Color, 38));
                Theme.Stroke(g, Bounds, rad, Theme.Alpha(Color, 175), 1.1f * s);
            }
            else
            {
                Theme.Fill(g, Bounds, rad, Hover ? Theme.CardHi : Theme.Card);
                Theme.Stroke(g, Bounds, rad, Hover ? Theme.BorderHi : Theme.Border);
            }
            var fg = !Enabled ? Theme.Faint : Active ? (Color == Theme.Accent ? Theme.Text : Color) : Hover ? Theme.Text : Color;
            if (IconPaint != null) IconPaint(g, Bounds, s, fg);
            else Draw.Text(g, Label, TinyFont ? f.TinyBold : f.SmallBold, fg, Bounds, StringAlignment.Center);
        }

        public override void Up(PointF p, bool inside) { if (inside && Enabled && Click != null) Click(); }
    }

    // Two-zone split button for "[● | Curve · 0% ▾]": left dot toggles curve in 1 click, right label opens inline editor.
    sealed class SplitCurveButton : Widget
    {
        public bool On, Warn;
        public string Label = "Curve off ▾";
        public string ToggleTip = "Toggle custom fan curve on/off";
        public string OpenTip = "Edit 5-point fan curve";
        public Action Toggled, Opened;
        int _hoverPart = -1, _downPart = -1;   // 0 = left dot toggle, 1 = right open label

        float SplitX { get { return Bounds.X + 28f * Scale; } }
        int PartAt(PointF p) { if (!Bounds.Contains(p)) return -1; return p.X < SplitX ? 0 : 1; }

        public override string TipAt(PointF p)
        {
            int part = PartAt(p);
            return part == 0 ? ToggleTip : part == 1 ? OpenTip : Tip;
        }

        public override void Paint(Graphics g, Fonts f)
        {
            float s = f.S, rad = 7 * s, sx = Bounds.X + 28 * s;
            var tint = Warn ? Theme.Amber : Theme.Purple;
            Theme.Fill(g, Bounds, rad, Theme.Bg);
            var leftR = new RectangleF(Bounds.X + 1 * s, Bounds.Y + 1 * s, 27 * s, Bounds.Height - 2 * s);
            var rightR = new RectangleF(sx, Bounds.Y + 1 * s, Bounds.Right - sx - 1 * s, Bounds.Height - 2 * s);
            if (Hover && Enabled)
            {
                if (_hoverPart == 0) Theme.Fill(g, leftR, rad - 1 * s, Theme.CardHi);
                else if (_hoverPart == 1) Theme.Fill(g, rightR, rad - 1 * s, Theme.CardHi);
            }
            using (var pen = new Pen(Theme.Border, 1)) g.DrawLine(pen, sx, Bounds.Y + 4 * s, sx, Bounds.Bottom - 4 * s);
            Theme.Stroke(g, Bounds, rad, On ? Theme.Alpha(tint, 170) : Theme.BorderHi, On ? 1.2f * s : 1f);

            // Left status dot
            float dr = 4.2f * s, cx = Bounds.X + 14 * s, cy = Bounds.Y + Bounds.Height / 2f;
            if (On)
            {
                using (var b = new SolidBrush(tint)) g.FillEllipse(b, cx - dr, cy - dr, 2 * dr, 2 * dr);
            }
            else
            {
                using (var b = new SolidBrush(Theme.CardHi)) g.FillEllipse(b, cx - dr, cy - dr, 2 * dr, 2 * dr);
                using (var pen = new Pen(Theme.Muted, 1.3f * s)) g.DrawEllipse(pen, cx - dr, cy - dr, 2 * dr, 2 * dr);
            }

            // Right label
            var col = !Enabled ? Theme.Faint : On ? tint : (_hoverPart == 1 && Hover ? Theme.Text : Theme.Muted);
            Draw.Text(g, Label, f.SmallBold, col, rightR, StringAlignment.Center);
        }

        public override void Move(PointF p) { _hoverPart = PartAt(p); }
        public override void Down(PointF p) { _downPart = PartAt(p); }
        public override void Up(PointF p, bool inside)
        {
            int part = PartAt(p);
            if (Enabled && inside && part >= 0 && part == _downPart)
            {
                if (part == 0) { if (Toggled != null) Toggled(); }
                else { if (Opened != null) Opened(); }
            }
            _downPart = -1;
        }
    }
}
