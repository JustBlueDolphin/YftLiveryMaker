using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YftLiveryMaker.UI
{
    internal static class Gfx
    {
        public static float Scale(Control c) => c.DeviceDpi / 96f;

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color fill, Color? border = null, float borderWidth = 1f)
        {
            using (var path = Round(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border.HasValue)
                    using (var pen = new Pen(border.Value, borderWidth)) g.DrawPath(pen, path);
            }
        }

        public static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static Color Behind(Control c) => c.Parent?.BackColor ?? Theme.Current.Window;

        public static void Text(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags) =>
            TextRenderer.DrawText(g, text, font, r, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        public static void Icon(Graphics g, string glyph, float size, Rectangle r, Color color)
        {
            using (var f = Fonts.Icon(size))
                TextRenderer.DrawText(g, glyph, f, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static void DoubleBuffer(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(c, true, null);
        }
    }

    /// <summary>Rounded surface that groups a step of the UI.</summary>
    internal sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(16, 12, 16, 14);
            Margin = new Padding(6);
            Dock = DockStyle.Fill;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            Gfx.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10 * s, t.Card, t.Border);
        }
    }

    /// <summary>"(1) Title" heading with a round step badge.</summary>
    internal sealed class SectionHeader : Control
    {
        public string Step { get; set; }
        public string Title { get; set; }

        public SectionHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 30;
            Dock = DockStyle.Top;
            Margin = new Padding(0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            int x = 0;
            if (!string.IsNullOrEmpty(Step))
            {
                float d = 22 * s;
                var circle = new RectangleF(0, (Height - d) / 2f - 1, d, d);
                using (var b = new SolidBrush(t.Accent)) g.FillEllipse(b, circle);
                Gfx.Text(g, Step, Fonts.Caption, Rectangle.Round(circle), t.OnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x = (int)(d + 10 * s);
            }
            Gfx.Text(g, Title ?? "", Fonts.Section, new Rectangle(x, 0, Width - x, Height - 2), t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    internal enum ButtonKind { Primary, Secondary, Ghost }

    internal sealed class ModernButton : Button
    {
        bool hover, down;

        public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
        public string Glyph { get; set; }
        public bool Big { get; set; }

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            AutoSize = true;
            Margin = new Padding(0, 0, 8, 0);
        }

        Font TextFont => Big ? Fonts.BigButton : Fonts.Button;

        public override Size GetPreferredSize(Size proposedSize)
        {
            float s = Gfx.Scale(this);
            var ts = TextRenderer.MeasureText(Text ?? "", TextFont, Size.Empty, TextFormatFlags.NoPadding);
            int glyph = string.IsNullOrEmpty(Glyph) ? 0 : (int)(22 * s);
            int padX = (int)((Big ? 26 : 16) * s);
            return new Size(Math.Max(MinimumSize.Width, ts.Width + glyph + padX * 2), Math.Max(MinimumSize.Height, (int)((Big ? 42 : 32) * s)));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = (Big ? 9 : 7) * s;

            Color fill, fore;
            Color? border = null;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    fill = !Enabled ? Gfx.Mix(t.Accent, t.Card, 0.6f) : down ? t.AccentPressed : hover ? t.AccentHover : t.Accent;
                    fore = Enabled ? t.OnAccent : Gfx.Mix(t.OnAccent, t.Card, 0.35f);
                    break;
                case ButtonKind.Ghost:
                    fill = down ? t.Border : hover ? t.InputHover : Gfx.Behind(this);
                    fore = Enabled ? (hover ? t.Text : t.Muted) : t.Faint;
                    break;
                default:
                    fill = down ? t.Border : hover ? t.InputHover : t.Input;
                    border = t.Border;
                    fore = Enabled ? t.Text : t.Faint;
                    break;
            }
            Gfx.FillRound(g, r, radius, fill, border);

            if (Focused && ShowFocusCues)
            {
                var f = RectangleF.Inflate(r, -2 * s, -2 * s);
                using (var path = Gfx.Round(f, radius - 2 * s))
                using (var pen = new Pen(Kind == ButtonKind.Primary ? t.OnAccent : t.Focus, 1.5f * s) { DashStyle = DashStyle.Dot })
                    g.DrawPath(pen, path);
            }

            var ts = TextRenderer.MeasureText(Text ?? "", TextFont, Size.Empty, TextFormatFlags.NoPadding);
            int glyphW = string.IsNullOrEmpty(Glyph) ? 0 : (int)(22 * s);
            int x = (Width - ts.Width - glyphW) / 2;
            if (glyphW > 0)
            {
                Gfx.Icon(g, Glyph, Big ? 11f : 9.5f, new Rectangle(x, 0, (int)(16 * s), Height), fore);
                x += glyphW;
            }
            Gfx.Text(g, Text ?? "", TextFont, new Rectangle(x, 0, ts.Width + 2, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    /// <summary>Drop-down list painted to match the theme (closed box and list items).</summary>
    internal sealed class ThemedComboBox : ComboBox, IThemeable
    {
        const int WM_PAINT = 0x000F;
        bool hover;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        public ThemedComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            IntegralHeight = false;
            MaxDropDownItems = 14;
            ItemHeight = 24;
            Font = Fonts.Body;
            Margin = new Padding(0, 3, 0, 3);
            ApplyTheme(Theme.Current);
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.Input;
            ForeColor = t.Text;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ItemHeight = (int)(24 * Gfx.Scale(this));
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            // The closed box is painted in WM_PAINT. Windows also asks for the selected item outside
            // WM_PAINT (focus/selection changes); drawing it here as well doubles the text.
            if ((e.State & DrawItemState.ComboBoxEdit) != 0) { Invalidate(); return; }
            var t = Theme.Current;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(selected ? t.Selection : t.Input)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds;
            r.X += 8; r.Width -= 12;
            Gfx.Text(e.Graphics, GetItemText(Items[e.Index]), Font, r, t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT && DropDownStyle == ComboBoxStyle.DropDownList)
            {
                var ps = new PAINTSTRUCT();
                IntPtr hdc = BeginPaint(Handle, ref ps);
                try
                {
                    // Shapes go through a buffer (no flicker); text is drawn straight on the window so it
                    // gets ClearType like every other label (GDI text in a bitmap comes out aliased).
                    using (var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp)) PaintFrame(g);
                        using (var target = Graphics.FromHdc(hdc))
                        {
                            target.DrawImageUnscaled(bmp, 0, 0);
                            PaintContent(target);
                        }
                    }
                }
                finally
                {
                    EndPaint(Handle, ref ps);
                }
                return;
            }
            base.WndProc(ref m);
        }

        void PaintFrame(Graphics g)
        {
            var t = Theme.Current;
            float s = Gfx.Scale(this);
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.FillRound(g, r, 7 * s, Enabled && hover ? t.InputHover : t.Input, Focused ? t.Focus : t.Border, Focused ? 1.5f : 1f);
        }

        void PaintContent(Graphics g)
        {
            var t = Theme.Current;
            float s = Gfx.Scale(this);
            int arrow = (int)(28 * s);
            string text = SelectedIndex >= 0 ? GetItemText(Items[SelectedIndex]) : "";
            Gfx.Text(g, text, Font, new Rectangle((int)(10 * s), 0, Width - arrow - (int)(10 * s), Height), Enabled ? t.Text : t.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Gfx.Icon(g, Glyphs.ChevronDown, 8, new Rectangle(Width - arrow, 0, arrow - (int)(4 * s), Height), Enabled ? t.Muted : t.Faint);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnSelectedIndexChanged(EventArgs e) { Invalidate(); base.OnSelectedIndexChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    /// <summary>Pill-shaped on/off switch.</summary>
    internal sealed class ToggleSwitch : CheckBox
    {
        bool hover;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            AutoSize = true;
            Font = Fonts.Body;
            Margin = new Padding(0, 6, 0, 0);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            float s = Gfx.Scale(this);
            var ts = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.NoPadding);
            return new Size((int)(46 * s) + ts.Width + 4, Math.Max((int)(26 * s), ts.Height + 6));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            float w = 36 * s, h = 20 * s;
            var track = new RectangleF(1, (Height - h) / 2f, w, h);
            Color fill = Checked ? (Enabled ? (hover ? t.AccentHover : t.Accent) : Gfx.Mix(t.Accent, t.Card, 0.6f)) : (hover && Enabled ? t.InputHover : t.Input);
            Gfx.FillRound(g, track, h / 2, fill, Checked ? (Color?)null : t.Border);
            float d = h - 6 * s;
            float x = Checked ? track.Right - d - 3 * s : track.X + 3 * s;
            using (var b = new SolidBrush(Checked ? t.OnAccent : (Enabled ? t.Muted : t.Faint))) g.FillEllipse(b, x, track.Y + 3 * s, d, d);
            if (Focused && ShowFocusCues)
                using (var path = Gfx.Round(RectangleF.Inflate(track, 2 * s, 2 * s), h / 2 + 2 * s))
                using (var pen = new Pen(t.Focus, 1.2f * s)) g.DrawPath(pen, path);
            Gfx.Text(g, Text ?? "", Font, new Rectangle((int)(w + 10 * s), 0, Width - (int)(w + 10 * s), Height), Enabled ? t.Text : t.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Two or more options side by side, one selected (radio-button group).</summary>
    internal sealed class Segmented : Control
    {
        int selected, hover = -1;
        string[] items = new string[0];
        string[] glyphs;
        bool[] disabled = new bool[0];

        public event EventHandler SelectedIndexChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Font = Fonts.BodySemibold;
            Height = 34;
            Cursor = Cursors.Hand;
        }

        public void SetItems(string[] texts, string[] icons = null)
        {
            items = texts;
            glyphs = icons;
            disabled = new bool[texts.Length];
            Invalidate();
        }

        public void SetItemEnabled(int index, bool enabled)
        {
            if (index < disabled.Length && disabled[index] == enabled) { disabled[index] = !enabled; Invalidate(); }
        }

        public bool IsItemEnabled(int index) => index >= 0 && index < disabled.Length && !disabled[index];

        public int SelectedIndex
        {
            get => selected;
            set
            {
                if (value < 0 || value >= items.Length || value == selected) return;
                selected = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        int SegmentAt(int x) => items.Length == 0 ? -1 : Math.Max(0, Math.Min(items.Length - 1, x * items.Length / Math.Max(1, Width)));

        protected override void OnMouseMove(MouseEventArgs e) { int h = SegmentAt(e.X); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = SegmentAt(e.X);
            if (i >= 0 && !disabled[i]) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
            for (int i = selected + step; step != 0 && i >= 0 && i < items.Length; i += step)
            {
                if (!disabled[i]) { SelectedIndex = i; break; }
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            var outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.FillRound(g, outer, 8 * s, t.Input, Focused && ShowFocusCues ? t.Focus : t.Border);
            if (items.Length == 0) return;

            float segW = (Width - 6 * s) / items.Length;
            for (int i = 0; i < items.Length; i++)
            {
                var r = new RectangleF(3 * s + i * segW, 3 * s, segW, Height - 6 * s);
                bool sel = i == selected;
                bool off = disabled[i] || !Enabled;
                if (sel) Gfx.FillRound(g, r, 6 * s, off ? Gfx.Mix(t.Accent, t.Input, 0.5f) : t.Accent);
                else if (i == hover && !off) Gfx.FillRound(g, r, 6 * s, t.InputHover);
                Color fore = sel ? t.OnAccent : off ? t.Faint : (i == hover ? t.Text : t.Muted);

                string text = items[i];
                string glyph = glyphs != null && i < glyphs.Length ? glyphs[i] : null;
                var ts = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding);
                int gw = glyph != null ? (int)(20 * s) : 0;
                int x = (int)(r.X + (r.Width - ts.Width - gw) / 2);
                if (glyph != null) Gfx.Icon(g, glyph, 9.5f, new Rectangle(x, (int)r.Y, (int)(14 * s), (int)r.Height), fore);
                Gfx.Text(g, text, Font, new Rectangle(x + gw, (int)r.Y, ts.Width + 2, (int)r.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }
    }

    /// <summary>"−  10  +" number picker.</summary>
    internal sealed class Stepper : Control
    {
        int value = 10, hover;

        public int Minimum { get; set; } = 1;
        public int Maximum { get; set; } = 13;
        public event EventHandler ValueChanged;

        public Stepper()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Size = new Size(118, 32);
            Font = Fonts.BodySemibold;
        }

        public int Value
        {
            get => value;
            set
            {
                int v = Math.Max(Minimum, Math.Min(Maximum, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        int Zone(int x) { int w = (int)(34 * Gfx.Scale(this)); return x < w ? -1 : x > Width - w ? 1 : 0; }

        protected override void OnMouseMove(MouseEventArgs e) { int z = Zone(e.X); if (z != hover) { hover = z; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            Value += Zone(e.X);
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Enabled) Value += Math.Sign(e.Delta);
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right || e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus) Value++;
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus) Value--;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            Gfx.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 7 * s, t.Input, Focused ? t.Focus : t.Border, Focused ? 1.5f : 1f);
            int w = (int)(34 * s);
            if (hover != 0 && Enabled)
            {
                var hr = hover < 0 ? new RectangleF(3 * s, 3 * s, w - 3 * s, Height - 6 * s) : new RectangleF(Width - w, 3 * s, w - 3 * s, Height - 6 * s);
                Gfx.FillRound(g, hr, 5 * s, t.InputHover);
            }
            Color sign = Enabled ? t.Muted : t.Faint;
            Gfx.Icon(g, Glyphs.Minus, 9, new Rectangle(0, 0, w, Height), value > Minimum ? sign : t.Faint);
            Gfx.Icon(g, Glyphs.Add, 9, new Rectangle(Width - w, 0, w, Height), value < Maximum ? sign : t.Faint);
            Gfx.Text(g, value.ToString(), Font, new Rectangle(w, 0, Width - 2 * w, Height), Enabled ? t.Text : t.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Text box with a rounded, themed frame.</summary>
    internal sealed class InputBox : Panel, IThemeable
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None };
        readonly bool multiline;

        public InputBox(bool multiline = false)
        {
            this.multiline = multiline;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box.Font = multiline ? Fonts.Mono : Fonts.Body;
            if (multiline)
            {
                Box.Multiline = true;
                Box.ReadOnly = true;
                Box.ScrollBars = ScrollBars.Vertical;
                Box.WordWrap = true;
                Box.Dock = DockStyle.Fill;
                Padding = new Padding(10, 8, 4, 6);
            }
            else
            {
                Height = 32;
                Padding = new Padding(10, 0, 8, 0);
            }
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Box.HandleCreated += (s, e) => NativeTheme.Scrollbars(Box, Theme.Current.IsDark);
            ApplyTheme(Theme.Current);
        }

        public override string Text { get => Box.Text; set => Box.Text = value; }

        public void ApplyTheme(Theme t)
        {
            Box.BackColor = multiline ? Gfx.Mix(t.Input, t.Window, 0.4f) : t.Input;
            Box.ForeColor = multiline ? t.Muted : t.Text;
            NativeTheme.Scrollbars(Box, t.IsDark);
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (multiline) return;
            float s = Gfx.Scale(this);
            Box.Left = (int)(10 * s);
            Box.Width = Math.Max(10, Width - (int)(18 * s));
            Box.Top = (Height - Box.Height) / 2;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            Gfx.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 7 * s, Box.BackColor, Box.Focused && !multiline ? t.Focus : t.Border, Box.Focused && !multiline ? 1.5f : 1f);
        }
    }

    /// <summary>Thin rounded progress bar.</summary>
    internal sealed class SlimProgress : Control
    {
        float value;
        bool done;

        public SlimProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 10;
        }

        public float Value { get => value; set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public bool Done { get => done; set { done = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            float h = 6 * s;
            var track = new RectangleF(0, (Height - h) / 2f, Width - 1, h);
            Gfx.FillRound(g, track, h / 2, t.Input, t.Border);
            if (value > 0)
            {
                var fill = new RectangleF(track.X, track.Y, Math.Max(h, track.Width * value), h);
                Gfx.FillRound(g, fill, h / 2, done ? t.Good : t.Accent);
            }
        }
    }

    /// <summary>Shows an image over a theme-coloured checkerboard, so transparency is visible.</summary>
    internal sealed class ImagePreview : Control
    {
        Image image;

        public string Placeholder { get; set; } = "";

        public ImagePreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public Image Image
        {
            get => image;
            set { image = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(Gfx.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            var frame = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Gfx.Round(frame, 8 * s))
            {
                g.SetClip(path);
                int cell = Math.Max(6, (int)(12 * s));
                using (var a = new SolidBrush(t.Checker1))
                using (var b = new SolidBrush(t.Checker2))
                    for (int y = 0; y < Height; y += cell)
                        for (int x = 0; x < Width; x += cell)
                            g.FillRectangle(((x / cell + y / cell) & 1) == 0 ? a : b, x, y, cell, cell);
                if (image != null)
                {
                    float scale = Math.Min((Width - 12 * s) / image.Width, (Height - 12 * s) / image.Height);
                    float w = image.Width * scale, h = image.Height * scale;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, (Width - w) / 2, (Height - h) / 2, w, h);
                }
                g.ResetClip();
                using (var pen = new Pen(t.Border)) g.DrawPath(pen, path);
            }
            if (image == null && !string.IsNullOrEmpty(Placeholder))
            {
                var r = ClientRectangle;
                using (var b = new SolidBrush(Color.FromArgb(t.IsDark ? 150 : 170, t.Card)))
                using (var path = Gfx.Round(new RectangleF(r.Width * 0.12f, r.Height * 0.38f, r.Width * 0.76f, r.Height * 0.24f), 8 * s))
                    g.FillPath(b, path);
                Gfx.Text(g, Placeholder, Fonts.Small, r, t.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }
    }
}
