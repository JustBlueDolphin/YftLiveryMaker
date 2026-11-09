using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YftLiveryMaker.UI
{
    /// <summary>One line in the list: title, file name and an optional coloured tag.</summary>
    internal struct LiveryListText
    {
        public string Title, Sub, Tag;
        public Color? TagColor;
    }

    /// <summary>Owner-drawn list of a car's liveries with thumbnails.</summary>
    internal sealed class LiveryList : ListBox, IThemeable
    {
        int hover = -1, dropTarget = -1;

        public Func<object, Image> Thumbnail { get; set; }
        public Func<object, LiveryListText> Describe { get; set; }

        public LiveryList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 60;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            Font = Fonts.Body;
            Gfx.DoubleBuffer(this);
            ApplyTheme(Theme.Current);
        }

        /// <summary>Item highlighted while an image is dragged over it (-1 for none).</summary>
        public int DropTarget
        {
            get => dropTarget;
            set { if (dropTarget != value) { dropTarget = value; Invalidate(); } }
        }

        public int IndexAtScreen(Point screen)
        {
            int i = IndexFromPoint(PointToClient(screen));
            return i == ListBox.NoMatches ? -1 : i;
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.Card;
            ForeColor = t.Text;
            if (IsHandleCreated) NativeTheme.Scrollbars(this, t.IsDark);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ItemHeight = (int)(60 * Gfx.Scale(this));
            NativeTheme.Scrollbars(this, Theme.Current.IsDark);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexFromPoint(e.Location);
            if (i == ListBox.NoMatches) i = -1;
            if (i != hover) { hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Current.Card)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            using (var b = new SolidBrush(t.Card)) g.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Gfx.Scale(this);
            var item = Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var r = new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4);
            if (e.Index == dropTarget)
                Gfx.FillRound(g, r, 8 * s, Color.FromArgb(70, t.Accent), t.Accent, 1.5f * s);
            else if (selected)
                Gfx.FillRound(g, r, 8 * s, t.Selection);
            else if (e.Index == hover)
                Gfx.FillRound(g, r, 8 * s, t.InputHover);

            int pad = (int)(8 * s);
            int thumb = e.Bounds.Height - 2 * pad;
            var tr = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + pad, thumb, thumb);
            using (var path = Gfx.Round(tr, 6 * s))
            {
                g.SetClip(path);
                var img = Thumbnail?.Invoke(item);
                if (img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, tr);
                }
                else
                {
                    int cell = Math.Max(4, thumb / 6);
                    using (var a = new SolidBrush(t.Checker1))
                    using (var b2 = new SolidBrush(t.Checker2))
                        for (int y = tr.Top; y < tr.Bottom; y += cell)
                            for (int x = tr.Left; x < tr.Right; x += cell)
                                g.FillRectangle((((x - tr.Left) / cell + (y - tr.Top) / cell) & 1) == 0 ? a : b2, x, y, cell, cell);
                }
                g.ResetClip();
                using (var pen = new Pen(t.Border)) g.DrawPath(pen, path);
            }

            var text = Describe?.Invoke(item) ?? new LiveryListText { Title = item.ToString() };
            int tx = tr.Right + (int)(10 * s);
            int tw = e.Bounds.Right - tx - pad;
            int half = e.Bounds.Height / 2;
            Gfx.Text(g, text.Title ?? "", Fonts.BodySemibold, new Rectangle(tx, e.Bounds.Y + (int)(6 * s), tw, half - (int)(4 * s)), t.Text,
                TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            int subX = tx;
            if (!string.IsNullOrEmpty(text.Tag))
            {
                var tagColor = text.TagColor ?? t.Accent;
                var ts = TextRenderer.MeasureText(text.Tag, Fonts.Caption, Size.Empty, TextFormatFlags.NoPadding);
                var pill = new RectangleF(tx, e.Bounds.Y + half + 3 * s, ts.Width + 12 * s, ts.Height + 4 * s);
                Gfx.FillRound(g, pill, pill.Height / 2, Color.FromArgb(t.IsDark ? 55 : 35, tagColor));
                Gfx.Text(g, text.Tag, Fonts.Caption, Rectangle.Round(pill), tagColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                subX = (int)(pill.Right + 6 * s);
            }
            Gfx.Text(g, text.Sub ?? "", Fonts.Small, new Rectangle(subX, e.Bounds.Y + half + (int)(2 * s), e.Bounds.Right - subX - pad, half - (int)(6 * s)), t.Muted,
                TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }
}
