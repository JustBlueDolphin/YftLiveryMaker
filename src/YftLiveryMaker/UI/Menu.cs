using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YftLiveryMaker.UI
{
    /// <summary>Paints context menus in the app's theme.</summary>
    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new Palette())
        {
            RoundedEdges = false;
        }

        static Theme T => Theme.Current;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool shortcut = e.Item is ToolStripMenuItem mi && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
            bool header = e.Item.Tag as string == "header";
            e.TextColor = !e.Item.Enabled && !header ? T.Faint : (shortcut || header ? T.Muted : T.Text);
            if (header) e.TextFont = Fonts.Caption;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            Gfx.FillRound(g, r, 6, T.Selection);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A small accent bar instead of the default boxed tick.
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.Item.ContentRectangle;
            Gfx.FillRound(g, new RectangleF(r.X + 1, r.Y + r.Height * 0.2f, 3, r.Height * 0.6f), 1.5f, T.Accent);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = T.Muted;
            base.OnRenderArrow(e);
        }

        sealed class Palette : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => T.Card;
            public override Color ImageMarginGradientBegin => T.Card;
            public override Color ImageMarginGradientMiddle => T.Card;
            public override Color ImageMarginGradientEnd => T.Card;
            public override Color MenuBorder => T.Border;
            public override Color MenuItemBorder => T.Selection;
            public override Color MenuItemSelected => T.Selection;
            public override Color MenuItemSelectedGradientBegin => T.Selection;
            public override Color MenuItemSelectedGradientEnd => T.Selection;
            public override Color SeparatorDark => T.Border;
            public override Color SeparatorLight => T.Border;
            public override Color CheckBackground => T.Card;
            public override Color CheckSelectedBackground => T.Selection;
            public override Color CheckPressedBackground => T.Selection;
        }
    }
}
