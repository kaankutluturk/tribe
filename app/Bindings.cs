using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tribe.App
{
    // The global menu binding, plus the inline capture control used on the misc tab.
    static class KeyNames
    {
        public static string Name(int key, int mods = 0)
        {
            string name;
            switch (key)
            {
                case 0: return "none";
                case 1: name = "mouse 1"; break; case 2: name = "mouse 2"; break; case 4: name = "mouse 3"; break; case 5: name = "mouse 4"; break; case 6: name = "mouse 5"; break;
                case 16: name = "shift"; break; case 17: name = "ctrl"; break; case 18: name = "alt"; break;
                case 256: name = "wheel up"; break; case 257: name = "wheel down"; break; case 258: name = "wheel left"; break; case 259: name = "wheel right"; break;
                default: name = ((Keys)key).ToString().ToLowerInvariant(); break;
            }
            return ((mods & 2) != 0 ? "ctrl + " : "") + ((mods & 4) != 0 ? "shift + " : "") + ((mods & 1) != 0 ? "alt + " : "") + ((mods & 8) != 0 ? "win + " : "") + name;
        }
    }

    // Watches the one bound key by asking Windows for its state on a timer; no global keyboard hook. While a binding
    // is being captured it looks at every key for those few seconds. A mouse hook exists only while the binding is
    // a scroll wheel direction, because the wheel has no state to ask for.
    sealed class KeyMonitor : IDisposable
    {
        delegate IntPtr Hook(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        readonly Hook wheel; IntPtr wh; readonly bool[] down = new bool[256];
        readonly Timer timer = new Timer { Interval = 15 };
        int watch; bool capturing;
        public Action<int, int, Point> Pressed, Released;

        static bool Held(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        public bool IsDown(int key) { return key > 0 && key < 256 && Held(key); }

        public KeyMonitor()
        {
            wheel = WheelEvent;
            timer.Tick += (s, e) => Poll();
            timer.Start();
        }

        /// <summary>The key to watch (0 none; 256-259 are the wheel directions).</summary>
        public void Watch(int key)
        {
            watch = key;
            if (key > 0 && key < 256) down[key] = Held(key);
            bool needWheel = key >= 256;
            if (needWheel && wh == IntPtr.Zero) wh = SetWindowsHookEx(14, wheel, GetModuleHandle(null), 0);
            else if (!needWheel && wh != IntPtr.Zero) { UnhookWindowsHookEx(wh); wh = IntPtr.Zero; }
        }

        // What is held right now is not a press: the click that opened the editor, the key that was just bound.
        public void BeginCapture() { for (int k = 1; k < 256; k++) down[k] = Held(k); capturing = true; }
        public void EndCapture() { capturing = false; Array.Clear(down, 0, down.Length); if (watch > 0 && watch < 256) down[watch] = Held(watch); }

        public static int Mods(int key)
        {
            int m = 0;
            if (Held(17) && key != 17) m |= 2;
            if (Held(16) && key != 16) m |= 4;
            if (Held(18) && key != 18) m |= 1;
            if ((Held(91) || Held(92)) && key != 91 && key != 92) m |= 8;
            return m;
        }

        void Poll()
        {
            if (capturing) { for (int k = 1; k < 256; k++) if (k < 160 || k > 165) Update(k); }   // 160-165: left/right twins of 16-18
            else if (watch > 0 && watch < 256) Update(watch);
        }
        void Update(int key)
        {
            bool held = Held(key);
            if (held == down[key]) return;
            down[key] = held;
            var handler = held ? Pressed : Released;
            if (handler != null) handler(key, Mods(key), Cursor.Position);
        }
        IntPtr WheelEvent(int c, IntPtr w, IntPtr l)
        {
            int msg = w.ToInt32();
            if (c >= 0 && (msg == 0x20a || msg == 0x20e))
            {
                var m = (Mouse)Marshal.PtrToStructure(l, typeof(Mouse));
                int delta = (short)(m.Data >> 16);
                int key = msg == 0x20a ? (delta > 0 ? 256 : 257) : (delta > 0 ? 259 : 258);
                if (Pressed != null) Pressed(key, Mods(key), new Point(m.X, m.Y));
            }
            return CallNextHookEx(wh, c, w, l);
        }
        public void Dispose()
        {
            timer.Stop(); timer.Dispose();
            if (wh != IntPtr.Zero) { UnhookWindowsHookEx(wh); wh = IntPtr.Zero; }
        }
    }

    sealed class BindingEditor : Control
    {
        readonly Func<int> key, mods; readonly Action<int, int> set; readonly KeyMonitor monitor;
        static BindingEditor active; public static bool Capturing { get { return active != null; } }
        int modifier; bool finishing;

        public BindingEditor(Func<int> getKey, Func<int> getMods, Action<int, int> setter, KeyMonitor input)
        {
            key = getKey; mods = getMods; set = setter; monitor = input; Height = 23; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var p = new Pen(active == this ? Skin.Accent : Skin.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            Skin.TextAt(e.Graphics, active == this ? "" : KeyNames.Name(key(), mods()), ClientRectangle, Skin.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        void Edit()
        {
            if (active != null) active.End();
            Focus(); active = this; modifier = 0; finishing = false;
            if (monitor != null) { monitor.BeginCapture(); monitor.Pressed += OnPress; monitor.Released += OnRelease; }
            Invalidate();
        }
        void End()
        {
            if (monitor != null) { monitor.Pressed -= OnPress; monitor.Released -= OnRelease; if (active == this) monitor.EndCapture(); }
            if (active == this) active = null;
            modifier = 0; Invalidate();
        }
        static bool IsModifier(int k) { return k == 16 || k == 17 || k == 18 || k == 91 || k == 92; }
        void OnPress(int k, int m, Point point) { if (active != this || finishing) return; if (IsModifier(k)) { modifier = k; return; } Accept(k == 27 ? 0 : k, k == 27 ? 0 : m); }
        void OnRelease(int k, int m, Point point) { if (active == this && !finishing && k == modifier) Accept(k, 0); }
        void Accept(int k, int m) { finishing = true; BeginInvoke(new Action(() => { if (active != this) return; set(k, m); End(); })); }
        // The wheel is captured from this control's own messages; it has the focus while a binding is being set.
        protected override void OnMouseWheel(MouseEventArgs e) { if (active == this) { int k = e.Delta > 0 ? 256 : 257; OnPress(k, KeyMonitor.Mods(k), Cursor.Position); } base.OnMouseWheel(e); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x20e && active == this) { int k = (short)((m.WParam.ToInt64() >> 16) & 0xffff) > 0 ? 259 : 258; OnPress(k, KeyMonitor.Mods(k), Cursor.Position); return; }
            base.WndProc(ref m);
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (active != this && e.Button == MouseButtons.Left) Edit(); base.OnMouseDown(e); }
        protected override bool ProcessCmdKey(ref Message message, Keys data)
        {
            if (active == this)
            {
                if (monitor == null) OnPress((int)(data & Keys.KeyCode), ((data & Keys.Control) != 0 ? 2 : 0) | ((data & Keys.Shift) != 0 ? 4 : 0) | ((data & Keys.Alt) != 0 ? 1 : 0), Cursor.Position);
                return true;
            }
            if (data == Keys.Enter || data == Keys.Space) { Edit(); return true; }
            return base.ProcessCmdKey(ref message, data);
        }
        protected override void OnLostFocus(EventArgs e) { if (active == this && !finishing) End(); base.OnLostFocus(e); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible && active == this) End(); base.OnVisibleChanged(e); }
        protected override void Dispose(bool disposing) { if (disposing) End(); base.Dispose(disposing); }
    }
}
