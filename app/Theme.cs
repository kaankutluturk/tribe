using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tribe.App
{
    // Palette and the menu's own controls: near-black ground, off-white text, amber for selection and focus.
    static class Skin
    {
        public static readonly Color Background = Color.FromArgb(9, 13, 10), Panel = Color.FromArgb(13, 19, 14), Border = Color.FromArgb(44, 62, 46),
            Text = Color.FromArgb(222, 232, 214), Muted = Color.FromArgb(122, 142, 118), Footnote = Color.FromArgb(86, 102, 84), Accent = Color.FromArgb(232, 176, 72);
        public static readonly Color Good = Color.FromArgb(156, 214, 88), Bad = Color.FromArgb(236, 88, 72), Warn = Color.FromArgb(236, 178, 72);
        public static readonly Color Inner = Color.FromArgb(19, 28, 21), Well = Color.FromArgb(10, 15, 11), Trough = Color.FromArgb(5, 8, 6);
        public static Font Font = new Font("Tahoma", 11, FontStyle.Regular, GraphicsUnit.Pixel), Logo = new Font("Consolas", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        public static float Scale = 1f;

        // Called before any control exists, so text metrics already match the scale Form.Scale applies later.
        public static void InitializeScale(float scale)
        {
            Scale = scale;
            Font = new Font("Tahoma", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Logo = new Font("Consolas", 18 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        }
        public static int D(Graphics g, float value) { return D(value); }
        public static int D(float value) { return (int)Math.Round(value * Scale); }
        public static void TextAt(Graphics g, string text, Rectangle rect, Color color, TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        { TextRenderer.DrawText(g, text, Font, rect, color, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis); }
    }

    sealed class FieldGroup : Panel
    {
        public FieldGroup(string title, int x, int y, int width, int height) { Text = title; SetBounds(x, y, width, height); BackColor = Skin.Panel; DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; int y = Skin.D(g, 7), x = Skin.D(g, 11);
            using (var pen = new Pen(Color.Black)) g.DrawRectangle(pen, 0, y, Width - 1, Height - y - 1);
            using (var pen = new Pen(Skin.Border)) g.DrawRectangle(pen, 1, y + 1, Width - 3, Height - y - 3);
            int width = TextRenderer.MeasureText(Text, Skin.Font).Width + Skin.D(g, 6);
            using (var brush = new SolidBrush(BackColor)) g.FillRectangle(brush, x, y - Skin.D(g, 6), width, Skin.D(g, 14));
            Skin.TextAt(g, Text, new Rectangle(x + Skin.D(g, 3), 0, width, Skin.D(g, 15)), Skin.Text);
        }
    }

    class MicroButton : Control
    {
        bool hover, pressed; public Action Action;
        public MicroButton(string text, Action action)
        {
            Text = text; Action = action; Height = 23; Font = Skin.Font; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Capture = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { bool click = pressed && ClientRectangle.Contains(e.Location); pressed = false; Capture = false; Invalidate(); if (click) OnClick(EventArgs.Empty); base.OnMouseUp(e); }
        protected override void OnClick(EventArgs e) { if (Enabled && Action != null) Action(); base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color top = pressed ? Skin.Well : hover ? Color.FromArgb(32, 48, 34) : Color.FromArgb(24, 36, 26);
            using (var b = new LinearGradientBrush(ClientRectangle, top, Skin.Well, 90)) e.Graphics.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Focused ? Skin.Accent : Skin.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            Skin.TextAt(e.Graphics, Text, new Rectangle(5, 0, Width - 10, Height), Enabled ? Skin.Text : Skin.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    sealed class MicroCheck : Control
    {
        readonly Func<bool> get; readonly Action<bool> set;
        public MicroCheck(string text, Func<bool> getter, Action<bool> setter)
        {
            Text = text; get = getter; set = setter; Height = 20; TabStop = true; AccessibleRole = AccessibleRole.CheckButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Enabled) { Focus(); set(!get()); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space && Enabled) { set(!get()); Invalidate(); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; int size = Skin.D(g, 10), y = (Height - size) / 2;
            using (var b = new SolidBrush(Skin.Well)) g.FillRectangle(b, 0, y, size, size);
            using (var p = new Pen(Focused ? Skin.Accent : Skin.Border)) g.DrawRectangle(p, 0, y, size, size);
            if (get()) using (var b = new LinearGradientBrush(new Rectangle(2, y + 2, size - 3, size - 3), ControlPaint.Light(Skin.Accent, 0.6f), Skin.Accent, 90)) g.FillRectangle(b, 2, y + 2, size - 3, size - 3);
            Skin.TextAt(g, Text, new Rectangle(Skin.D(g, 18), 0, Width - Skin.D(g, 18), Height), Enabled ? Skin.Text : Skin.Muted);
        }
    }

    sealed class MicroSlider : Control
    {
        public string Suffix = "";   // unit shown after the value
        public string Format = "0";  // how the value is shown
        readonly Func<double> get; readonly Action<double> set; readonly double min, max, step;
        bool dragging;
        public MicroSlider(string text, double min, double max, double step, Func<double> getter, Action<double> setter)
        {
            Text = text; this.min = min; this.max = max; this.step = step; get = getter; set = setter; Height = 36; TabStop = true; AccessibleRole = AccessibleRole.Slider;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        }
        void Set(double value) { set(Math.Max(min, Math.Min(max, Math.Round(value / step) * step))); Invalidate(); }
        void Position(int x) { int pad = Skin.D(12); Set(min + (max - min) * Math.Max(0, Math.Min(1, (x - pad) / (double)Math.Max(1, Width - 2 * pad)))); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus(); int pad = Skin.D(12);
                if (e.X < pad) Set(get() - step); else if (e.X >= Width - pad) Set(get() + step); else { dragging = true; Capture = true; Position(e.X); }
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e) { if (dragging) Position(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; base.OnMouseUp(e); }
        protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || base.IsInputKey(key); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) { Set(get() + (e.KeyCode == Keys.Right ? step : -step)); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; int pad = Skin.D(g, 12), barY = Skin.D(g, 24), barH = Math.Max(3, Skin.D(g, 4));
            Skin.TextAt(g, Text, new Rectangle(pad, 0, Width - pad * 2, Skin.D(g, 19)), Enabled ? Skin.Text : Skin.Muted);
            Skin.TextAt(g, get().ToString(Format, System.Globalization.CultureInfo.InvariantCulture) + Suffix, new Rectangle(pad, 0, Width - pad * 2, Skin.D(g, 19)), Skin.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using (var b = new SolidBrush(Skin.Trough)) g.FillRectangle(b, pad, barY, Width - pad * 2, barH);
            using (var b = new SolidBrush(Enabled ? Skin.Accent : Skin.Border)) g.FillRectangle(b, pad + 1, barY + 1, (int)((Width - pad * 2 - 2) * Math.Max(0, Math.Min(1, (get() - min) / (max - min)))), Math.Max(1, barH - 2));
            using (var p = new Pen(Focused ? Skin.Accent : Skin.Border)) g.DrawRectangle(p, pad, barY, Width - pad * 2, barH);
            Skin.TextAt(g, "-", new Rectangle(0, barY - Skin.D(g, 6), pad, Skin.D(g, 16)), Skin.Muted);
            Skin.TextAt(g, "+", new Rectangle(Width - pad, barY - Skin.D(g, 6), pad, Skin.D(g, 16)), Skin.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    sealed class MenuTabs : Control
    {
        public readonly string[] Tabs; public int Selected; public Action<int> Change;
        public MenuTabs(params string[] tabs)
        {
            Tabs = tabs; Height = 40; TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        }
        int[] ColumnWidths()
        {
            var widths = new int[Tabs.Length]; int total = 0, pad = Skin.D(16);
            for (int i = 0; i < Tabs.Length; i++) { widths[i] = TextRenderer.MeasureText(Tabs[i], Skin.Font, Size.Empty, TextFormatFlags.NoPadding).Width + pad; total += widths[i]; }
            if (total > 0 && Width > 0)
            {
                int assigned = 0;
                for (int i = 0; i < widths.Length; i++) { int share = i == widths.Length - 1 ? Width - assigned : (int)Math.Round(widths[i] * (Width / (double)total)); assigned += share; widths[i] = share; }
            }
            return widths;
        }
        public void Select(int index) { Selected = index; Invalidate(); if (Change != null) Change(index); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Focus(); var widths = ColumnWidths(); int x = 0; for (int i = 0; i < widths.Length; i++) { x += widths[i]; if (e.X < x) { Select(i); break; } } }
            base.OnMouseDown(e);
        }
        protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || base.IsInputKey(key); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) { Select((Selected + (e.KeyCode == Keys.Right ? 1 : Tabs.Length - 1)) % Tabs.Length); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var widths = ColumnWidths(); int x = 0;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var r = new Rectangle(x, 0, widths[i], Height - 5);
                Skin.TextAt(e.Graphics, Tabs[i], r, i == Selected ? Color.White : Skin.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                using (var p = new Pen(i == Selected ? Skin.Accent : Skin.Border, 2)) e.Graphics.DrawLine(p, r.Left + 3, Height - 5, r.Right - 3, Height - 5);
                x += widths[i];
            }
        }
    }
}
