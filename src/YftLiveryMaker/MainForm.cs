using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiveryKit;
using LiveryKit.Imaging;
using LiveryKit.Resources;
using LiveryKit.Textures;
using LiveryKit.Yft;
using YftLiveryMaker.UI;

namespace YftLiveryMaker
{
    public sealed class MainForm : Form
    {
        enum Tone { Normal, Muted, Good, Warn }

        // ------------------------------------------------------------ state
        ScanResult scan;
        CarInfo car;
        LiveryTemplate template;
        NamingScheme naming;
        CancellationTokenSource cts;
        bool busy, suppressGrid;
        int templateRequest, carVersion, previewRequest;
        string lastResultFolder, lastInstructions;
        Tone statusTone = Tone.Muted;
        readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, LiveryTemplate> loaded = new Dictionary<string, LiveryTemplate>(StringComparer.OrdinalIgnoreCase);
        ContextMenuStrip openMenu;

        sealed class Row
        {
            public string ImagePath;
            public string ImageName;
            public int Width, Height;
            public string Error;
            /// <summary>Result text from the last run, shown until something about the row changes.</summary>
            public string Done;
            /// <summary>The car's livery this image overwrites; null adds it as a new livery.</summary>
            public LiveryFile Replaces;
            /// <summary>File name typed for a new livery; null takes the next free number.</summary>
            public string CustomName;
            /// <summary>The .yft name (no extension) the row is saved as, worked out by RenumberRows.</summary>
            public string ModelName;
            public bool DisplayEdited, LabelEdited;
        }

        // ------------------------------------------------------------ controls
        Panel header, footer;
        TableLayoutPanel body;
        ToggleSwitch aiSwitch;
        bool aiSwitchQuiet;
        Label title, subtitle, carInfo, textureInfo, mipInfo, sizeInfo, statusLabel, outputHint, dropHint, previewCaption, listHint;
        readonly List<Label> mutedLabels = new List<Label>();
        readonly List<Card> cards = new List<Card>();
        Segmented themeSwitch, modeSwitch;
        ThemedComboBox carBox, templateBox, textureBox, formatBox, sizeBox;
        ImagePreview preview;
        DataGridView grid;
        LiveryList liveryList;
        SectionHeader liveriesHeader;
        Stepper mipsBox;
        ToggleSwitch edgeFix, saveDds;
        InputBox outputBox, logBox;
        ModernButton openFolderBtn, openYftBtn, addBtn, removeBtn, clearBtn, browseOutBtn, makeBtn, openResultBtn, openInstructionsBtn, replaceBtn;
        SlimProgress progress;
        DataGridViewImageColumn colThumb;
        DataGridViewTextBoxColumn colImage, colTarget, colDisplay, colLabel, colStatus;
        Image logo;

        public MainForm()
        {
            Text = "YFT Livery Maker";
            Font = Fonts.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(1300, 1100);
            MinimumSize = new Size(980, 720);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            DoubleBuffered = true;
            try
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
                {
                    if (s != null)
                    {
                        Icon = new Icon(s);
                        logo = new Icon(Icon, 64, 64).ToBitmap();
                    }
                }
            }
            catch { }

            BuildUi();
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            FormClosing += (s, e) => cts?.Cancel();
            LoadSettings();
            SetAiSwitch(AiApps.IsOn());
            ApplyTheme();
            UpdateAll();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeTheme.TitleBar(this, UI.Theme.Current);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Smaller screens: use all of it, and below a usable list height the cards scroll instead of squeezing the lists away.
            var wa = Screen.FromControl(this).WorkingArea;
            if (Width > wa.Width || Height > wa.Height) WindowState = FormWindowState.Maximized;
            body.AutoScrollMinSize = new Size(0, (int)(body.Padding.Vertical + body.RowStyles[0].Height + body.RowStyles[2].Height + 250 * Gfx.Scale(body)));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyTheme(); // native scrollbars only take a theme once their handles exist
        }

        // ============================================================ layout

        void BuildUi()
        {
            header = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(22, 12, 22, 12) };
            header.Paint += (s, e) =>
            {
                var t = UI.Theme.Current;
                if (logo != null)
                {
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    int sz = (int)(40 * Gfx.Scale(header));
                    e.Graphics.DrawImage(logo, header.Padding.Left, (header.Height - sz) / 2, sz, sz);
                }
                using (var pen = new Pen(t.Border)) e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            title = new Label { Text = "YFT Livery Maker", Font = Fonts.Title, AutoSize = true, Location = new Point(76, 13), BackColor = Color.Transparent };
            subtitle = Muted("Your livery art → GTA V / FiveM livery .yft, a 10-mipmap DDS texture and the meta data, in one go.");
            subtitle.Location = new Point(78, 46);
            subtitle.BackColor = Color.Transparent;
            themeSwitch = new Segmented { Width = 196, Height = 36, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            themeSwitch.SetItems(new[] { "Dark", "Light" }, new[] { Glyphs.Moon, Glyphs.Sun });
            themeSwitch.SelectedIndex = UI.Theme.Current.IsDark ? 0 : 1;
            themeSwitch.SelectedIndexChanged += (s, e) =>
            {
                UI.Theme.Use(themeSwitch.SelectedIndex == 0);
                Settings.Set("theme", themeSwitch.SelectedIndex == 0 ? "dark" : "light");
                ApplyTheme();
            };
            // Lets AI apps (Claude Desktop, Cursor, ...) make liveries with this program, over MCP.
            aiSwitch = new ToggleSwitch { Text = "AI apps", Margin = new Padding(0), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            aiSwitch.CheckedChanged += (s, e) => OnAiSwitchChanged();
            header.Controls.AddRange(new Control[] { title, subtitle, themeSwitch, aiSwitch });
            header.Resize += (s, e) =>
            {
                themeSwitch.Location = new Point(header.Width - header.Padding.Right - themeSwitch.Width, (header.Height - themeSwitch.Height) / 2);
                aiSwitch.Location = new Point(themeSwitch.Left - aiSwitch.Width - 24, (header.Height - aiSwitch.Height) / 2);
            };

            footer = BuildFooter();

            body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 0), ColumnCount = 2, RowCount = 3, AutoScroll = true };
            Gfx.DoubleBuffer(body);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 262));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 280));

            body.Controls.Add(BuildCarCard(), 0, 0);
            body.Controls.Add(BuildPreviewCard(), 1, 0);
            body.Controls.Add(BuildLiveryCard(), 0, 1);
            body.Controls.Add(BuildCarLiveriesCard(), 1, 1);
            body.Controls.Add(BuildTextureCard(), 0, 2);
            body.Controls.Add(BuildOutputCard(), 1, 2);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);
        }

        Card NewCard(string step, string heading, Control content, Control headerExtra = null, SectionHeader sectionHeader = null)
        {
            var card = new Card();
            cards.Add(card);
            content.Dock = DockStyle.Fill;
            card.Controls.Add(content);
            var sh = sectionHeader ?? new SectionHeader { Step = step, Title = heading };
            if (headerExtra == null)
            {
                card.Controls.Add(sh);
            }
            else
            {
                var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                sh.Dock = DockStyle.Fill;
                row.Controls.Add(sh, 0, 0);
                headerExtra.Anchor = AnchorStyles.Right | AnchorStyles.Top;
                row.Controls.Add(headerExtra, 1, 0);
                card.Controls.Add(row);
            }
            return card;
        }

        Label Caption(string text)
        {
            var l = new Label { Text = text.ToUpperInvariant(), Font = Fonts.Caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 2) };
            mutedLabels.Add(l);
            return l;
        }

        Label Muted(string text, int maxWidth = 0)
        {
            var l = new Label { Text = text, Font = Fonts.Small, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
            if (maxWidth > 0) l.MaximumSize = new Size(maxWidth, 0);
            mutedLabels.Add(l);
            return l;
        }

        static TableLayoutPanel Grid(int cols, int rows)
        {
            var t = new TableLayoutPanel { ColumnCount = cols, RowCount = rows, Margin = new Padding(0), Padding = new Padding(0, 4, 0, 0) };
            Gfx.DoubleBuffer(t);
            return t;
        }

        Control BuildCarCard()
        {
            var t = Grid(4, 5);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 2, 0, 8) };
            openFolderBtn = new ModernButton { Text = "Open car folder", Glyph = Glyphs.Folder };
            openFolderBtn.Click += async (s, e) => await BrowseCarFolderAsync();
            openYftBtn = new ModernButton { Text = "Open a livery .yft", Glyph = Glyphs.File };
            openYftBtn.Click += async (s, e) => await BrowseYftAsync();
            var hint = Muted("or drop the car's folder or a livery .yft anywhere on this window");
            hint.Margin = new Padding(6, 9, 0, 0);
            buttons.Controls.AddRange(new Control[] { openFolderBtn, openYftBtn, hint });
            t.Controls.Add(buttons, 0, 0);
            t.SetColumnSpan(buttons, 4);

            t.Controls.Add(Caption("Car"), 0, 1);
            carBox = new ThemedComboBox { Dock = DockStyle.Fill };
            carBox.SelectedIndexChanged += (s, e) => OnCarChanged();
            t.Controls.Add(carBox, 1, 1);
            t.SetColumnSpan(carBox, 3);

            t.Controls.Add(Caption("Template"), 0, 2);
            templateBox = new ThemedComboBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 14, 3) };
            templateBox.SelectedIndexChanged += async (s, e) => await OnTemplateChangedAsync();
            t.Controls.Add(templateBox, 1, 2);
            t.Controls.Add(Caption("Texture"), 2, 2);
            textureBox = new ThemedComboBox { Dock = DockStyle.Fill };
            textureBox.SelectedIndexChanged += (s, e) => { UpdateTextureInfo(); RefreshRowStatuses(); UpdateMipInfo(); };
            t.Controls.Add(textureBox, 3, 2);

            textureInfo = Muted("", 780);
            t.Controls.Add(textureInfo, 1, 3);
            t.SetColumnSpan(textureInfo, 3);

            carInfo = new Label { AutoSize = true, Font = Fonts.Small, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(840, 0) };
            t.Controls.Add(carInfo, 0, 4);
            t.SetColumnSpan(carInfo, 4);

            return NewCard("1", "Pick the car", t);
        }

        Control BuildPreviewCard()
        {
            var t = Grid(1, 2);
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            preview = new ImagePreview { Dock = DockStyle.Fill, Placeholder = "Pick a car to see\nits liveries", Margin = new Padding(0, 4, 0, 0) };
            previewCaption = Muted("", 320);
            previewCaption.Margin = new Padding(0, 6, 0, 0);
            t.Controls.Add(preview, 0, 0);
            t.Controls.Add(previewCaption, 0, 1);
            return NewCard(null, "Preview", t);
        }

        Control BuildLiveryCard()
        {
            var t = Grid(1, 2);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            dropHint = Muted("Each image becomes a livery. Click SAVE AS to add it as a new livery or replace one of the car's liveries. Names and labels can be edited.");
            dropHint.Margin = new Padding(32, 0, 0, 8);
            t.Controls.Add(dropHint, 0, 0);

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                AllowDrop = true,
                EnableHeadersVisualStyles = false,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 34,
                Margin = new Padding(0),
            };
            Gfx.DoubleBuffer(grid);
            grid.RowTemplate.Height = 64;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.DefaultCellStyle.Font = Fonts.Body;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);

            colThumb = new DataGridViewImageColumn { HeaderText = "", Width = 72, ImageLayout = DataGridViewImageCellLayout.Zoom, Resizable = DataGridViewTriState.False };
            colImage = new DataGridViewTextBoxColumn { HeaderText = "YOUR IMAGE", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 22 };
            colTarget = new DataGridViewTextBoxColumn { HeaderText = "SAVE AS", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 30 };
            colDisplay = new DataGridViewTextBoxColumn { HeaderText = "MOD SHOP NAME", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 20 };
            colLabel = new DataGridViewTextBoxColumn { HeaderText = "LABEL", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 13 };
            colStatus = new DataGridViewTextBoxColumn { HeaderText = "STATUS", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 18 };
            grid.Columns.AddRange(colThumb, colImage, colTarget, colDisplay, colLabel, colStatus);
            colThumb.DefaultCellStyle.Padding = new Padding(8, 6, 4, 6);

            grid.CellValueChanged += OnGridCellChanged;
            grid.CellPainting += PaintTargetCell;
            grid.CellMouseClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex == colTarget.Index && e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                {
                    grid.ClearSelection();
                    grid.Rows[e.RowIndex].Selected = true;
                    ShowTargetMenu(e.RowIndex);
                }
            };
            grid.CellMouseEnter += (s, e) => grid.Cursor = e.ColumnIndex == colTarget.Index && e.RowIndex >= 0 ? Cursors.Hand : Cursors.Default;
            grid.DragEnter += OnDragEnter;
            grid.DragDrop += OnDragDrop;
            grid.KeyDown += (s, e) =>
            {
                if (grid.IsCurrentCellInEditMode) return;
                if (e.KeyCode == Keys.Delete) RemoveSelected();
                else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.F4) && grid.CurrentCell?.ColumnIndex == colTarget.Index)
                {
                    ShowTargetMenu(grid.CurrentCell.RowIndex);
                    e.Handled = true;
                }
            };
            grid.SelectionChanged += (s, e) => { if (removeBtn != null) removeBtn.Enabled = !busy && grid.SelectedRows.Count > 0; };
            grid.EditingControlShowing += (s, e) =>
            {
                var th = UI.Theme.Current;
                e.Control.BackColor = th.Input;
                e.Control.ForeColor = th.Text;
            };
            grid.Paint += PaintEmptyGrid;
            grid.Resize += (s, e) => { if (grid.Rows.Count == 0) grid.Invalidate(); };
            t.Controls.Add(grid, 0, 1);

            var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            addBtn = new ModernButton { Text = "Add images", Glyph = Glyphs.Add, Kind = ButtonKind.Primary };
            addBtn.Click += async (s, e) => await BrowseImagesAsync(null);
            removeBtn = new ModernButton { Text = "Remove", Glyph = Glyphs.Delete };
            removeBtn.Click += (s, e) => RemoveSelected();
            clearBtn = new ModernButton { Text = "Clear", Kind = ButtonKind.Ghost, Margin = new Padding(0) };
            clearBtn.Click += (s, e) => { grid.Rows.Clear(); RenumberRows(); liveryList.Invalidate(); UpdateAll(); };
            tools.Controls.AddRange(new Control[] { addBtn, removeBtn, clearBtn });

            return NewCard("2", "Your liveries", t, tools);
        }

        Control BuildCarLiveriesCard()
        {
            var t = Grid(1, 3);
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            liveryList = new LiveryList { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4), AllowDrop = true };
            liveryList.Thumbnail = o => ThumbFor(o as LiveryFile);
            liveryList.Describe = o => DescribeLivery(o as LiveryFile);
            liveryList.SelectedIndexChanged += async (s, e) =>
            {
                replaceBtn.Enabled = !busy && (liveryList.SelectedItem as LiveryFile)?.YftPath != null;
                await ShowLiveryPreviewAsync(liveryList.SelectedItem as LiveryFile);
            };
            liveryList.DoubleClick += async (s, e) => await ReplaceSelectedWithImageAsync();
            liveryList.DragEnter += (s, e) => e.Effect = !busy && DroppedImages(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            liveryList.DragOver += (s, e) =>
            {
                int i = liveryList.IndexAtScreen(new Point(e.X, e.Y));
                bool ok = !busy && i >= 0 && (liveryList.Items[i] as LiveryFile)?.YftPath != null && DroppedImages(e).Count > 0;
                e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
                liveryList.DropTarget = ok ? i : -1;
            };
            liveryList.DragLeave += (s, e) => liveryList.DropTarget = -1;
            liveryList.DragDrop += async (s, e) =>
            {
                int i = liveryList.DropTarget;
                liveryList.DropTarget = -1;
                var images = DroppedImages(e);
                if (busy || i < 0 || images.Count == 0) return;
                await AddImagesAsync(images, liveryList.Items[i] as LiveryFile);
            };
            t.Controls.Add(liveryList, 0, 0);

            listHint = Muted("Double-click a livery or drop an image on it to replace it.", 320);
            listHint.Margin = new Padding(0, 2, 0, 6);
            t.Controls.Add(listHint, 0, 1);

            replaceBtn = new ModernButton
            {
                Text = "Replace with an image",
                Glyph = Glyphs.Replace,
                AutoSize = false,
                Height = 34,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0),
                Enabled = false,
            };
            replaceBtn.Click += async (s, e) => await ReplaceSelectedWithImageAsync();
            t.Controls.Add(replaceBtn, 0, 2);

            liveriesHeader = new SectionHeader { Title = "Liveries on this car" };
            return NewCard(null, null, t, sectionHeader: liveriesHeader);
        }

        void PaintEmptyGrid(object sender, PaintEventArgs e)
        {
            if (grid.Rows.Count > 0) return;
            var t = UI.Theme.Current;
            float s = Gfx.Scale(grid);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = new RectangleF(10 * s, grid.ColumnHeadersHeight + 10 * s, grid.Width - 20 * s, grid.Height - grid.ColumnHeadersHeight - 20 * s);
            if (area.Height < 40) return;
            using (var path = Gfx.Round(area, 10 * s))
            using (var pen = new Pen(t.Border, 1.5f * s) { DashStyle = DashStyle.Dash })
                g.DrawPath(pen, path);
            var r = Rectangle.Round(area);
            Gfx.Icon(g, Glyphs.Images, 22, new Rectangle(r.X, r.Y + r.Height / 2 - (int)(52 * s), r.Width, (int)(40 * s)), t.Faint);
            Gfx.Text(g, "Drop your livery images here", Fonts.Empty, new Rectangle(r.X, r.Y + r.Height / 2 - (int)(8 * s), r.Width, (int)(26 * s)), t.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Gfx.Text(g, "add them as new liveries, or drop one on a livery on the right to replace it", Fonts.Small, new Rectangle(r.X, r.Y + r.Height / 2 + (int)(18 * s), r.Width, (int)(22 * s)), t.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>SAVE AS cell: a NEW / REPLACE pill, the file name, and a chevron to show it opens a menu.</summary>
        void PaintTargetCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != colTarget.Index) return;
            e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            var data = grid.Rows[e.RowIndex].Tag as Row;
            if (data == null) { e.Handled = true; return; }

            var t = UI.Theme.Current;
            var g = e.Graphics;
            float s = Gfx.Scale(grid);
            var cell = e.CellBounds;
            bool replace = data.Replaces != null;
            string tag = replace ? "REPLACE" : "NEW";
            Color tagColor = replace ? t.Warn : t.Accent;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var ts = TextRenderer.MeasureText(tag, Fonts.Caption, Size.Empty, TextFormatFlags.NoPadding);
            float ph = ts.Height + 6 * s;
            var pill = new RectangleF(cell.X + 10 * s, cell.Y + (cell.Height - ph) / 2f, ts.Width + 16 * s, ph);
            Gfx.FillRound(g, pill, ph / 2, Color.FromArgb(t.IsDark ? 50 : 32, tagColor), tagColor);
            Gfx.Text(g, tag, Fonts.Caption, Rectangle.Round(pill), tagColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int chevronW = (int)(26 * s);
            int nx = (int)(pill.Right + 10 * s);
            string name = (data.ModelName ?? "…") + ".yft";
            string sub = replace ? (LiveryDisplayName(data.Replaces) ?? "") : data.CustomName != null ? "your own name" : "next free number";
            var nameRect = new Rectangle(nx, cell.Y + (int)(10 * s), cell.Right - nx - chevronW, cell.Height / 2 - (int)(8 * s));
            var subRect = new Rectangle(nx, cell.Y + cell.Height / 2 + (int)(1 * s), cell.Right - nx - chevronW, cell.Height / 2 - (int)(8 * s));
            Gfx.Text(g, name, Fonts.BodySemibold, nameRect, t.Text, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, sub, Fonts.Small, subRect, t.Muted, TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Gfx.Icon(g, Glyphs.ChevronDown, 8, new Rectangle(cell.Right - chevronW, cell.Y, chevronW - (int)(6 * s), cell.Height), busy ? t.Faint : t.Muted);
            using (var pen = new Pen(t.GridLine)) g.DrawLine(pen, cell.Left, cell.Bottom - 1, cell.Right, cell.Bottom - 1);
            e.Handled = true;
        }

        Control BuildTextureCard()
        {
            var t = Grid(4, 4);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            t.Controls.Add(Caption("Format"), 0, 0);
            formatBox = new ThemedComboBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 18, 3) };
            formatBox.Items.AddRange(new object[]
            {
                "DXT5  ·  smooth transparency (recommended)",
                "DXT1  ·  half the size, on/off transparency",
                "BC7  ·  sharpest, DirectX 11 only",
                "Uncompressed (A8R8G8B8)  ·  very large",
            });
            formatBox.SelectedIndex = 0;
            formatBox.SelectedIndexChanged += (s, e) => UpdateMipInfo();
            t.Controls.Add(formatBox, 1, 0);

            t.Controls.Add(Caption("Mipmaps"), 2, 0);
            mipsBox = new Stepper { Minimum = 1, Maximum = 13, Value = 10, Margin = new Padding(0, 3, 0, 3) };
            mipsBox.ValueChanged += (s, e) => UpdateMipInfo();
            t.Controls.Add(mipsBox, 3, 0);

            t.Controls.Add(Caption("Size"), 0, 1);
            sizeBox = new ThemedComboBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 18, 3) };
            sizeBox.Items.AddRange(new object[] { "Same as the car's livery", "Keep my image's size", "1024 (longest side)", "2048 (longest side)", "4096 (longest side)" });
            sizeBox.SelectedIndex = 0;
            sizeBox.SelectedIndexChanged += (s, e) => UpdateMipInfo();
            t.Controls.Add(sizeBox, 1, 1);

            mipInfo = new Label { AutoSize = true, Font = Fonts.BodySemibold, Margin = new Padding(0, 9, 0, 0), MaximumSize = new Size(470, 0) };
            t.Controls.Add(mipInfo, 2, 1);
            t.SetColumnSpan(mipInfo, 2);

            edgeFix = new ToggleSwitch { Text = "Clean up transparent edges (stops dark or white outlines)", Checked = true, Margin = new Padding(0, 10, 0, 0) };
            t.Controls.Add(edgeFix, 0, 2);
            t.SetColumnSpan(edgeFix, 4);

            sizeInfo = new Label { AutoSize = true, Font = Fonts.Small, Margin = new Padding(0, 10, 0, 0), MaximumSize = new Size(800, 0) };
            t.Controls.Add(sizeInfo, 0, 3);
            t.SetColumnSpan(sizeInfo, 4);

            return NewCard("3", "Texture (DDS)", t);
        }

        Control BuildOutputCard()
        {
            var t = Grid(2, 5);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            modeSwitch = new Segmented { Dock = DockStyle.Top, Height = 36, Margin = new Padding(0, 2, 0, 8) };
            modeSwitch.SetItems(new[] { "New folder", "Into resource" });
            modeSwitch.SelectedIndexChanged += (s, e) => UpdateOutputHint();
            t.Controls.Add(modeSwitch, 0, 0);
            t.SetColumnSpan(modeSwitch, 2);

            outputBox = new InputBox { Dock = DockStyle.Top, Margin = new Padding(0, 0, 6, 0) };
            browseOutBtn = new ModernButton { Text = "", Glyph = Glyphs.Folder, Margin = new Padding(0), AutoSize = false, Size = new Size(38, 32) };
            browseOutBtn.Click += (s, e) => BrowseOutput();
            t.Controls.Add(outputBox, 0, 1);
            t.Controls.Add(browseOutBtn, 1, 1);

            outputHint = Muted("", 320);
            outputHint.Margin = new Padding(0, 6, 0, 0);
            t.Controls.Add(outputHint, 0, 2);
            t.SetColumnSpan(outputHint, 2);

            saveDds = new ToggleSwitch { Text = "Also save the .dds textures", Checked = true, Margin = new Padding(0, 8, 0, 0) };
            t.Controls.Add(saveDds, 0, 3);
            t.SetColumnSpan(saveDds, 2);

            makeBtn = new ModernButton
            {
                Text = "Make liveries",
                Glyph = Glyphs.Play,
                Kind = ButtonKind.Primary,
                Big = true,
                AutoSize = false,
                Height = 44,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Margin = new Padding(0, 8, 0, 0),
            };
            makeBtn.Click += async (s, e) => { if (busy) cts?.Cancel(); else await MakeAsync(); };
            t.Controls.Add(makeBtn, 0, 4);
            t.SetColumnSpan(makeBtn, 2);

            return NewCard("4", "Output", t);
        }

        Panel BuildFooter()
        {
            var f = new Panel { Dock = DockStyle.Bottom, Height = 140, Padding = new Padding(20, 8, 20, 14) };
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            statusLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = Fonts.BodySemibold, Text = "Ready.", Margin = new Padding(0, 0, 12, 0) };
            progress = new SlimProgress { Dock = DockStyle.Fill, Margin = new Padding(0, 13, 14, 13) };
            openInstructionsBtn = new ModernButton { Text = "What to do next", Glyph = Glyphs.Info, Visible = false, Anchor = AnchorStyles.Right };
            openInstructionsBtn.Click += (s, e) => { if (lastInstructions != null && File.Exists(lastInstructions)) Process.Start("notepad.exe", "\"" + lastInstructions + "\""); };
            openResultBtn = new ModernButton { Text = "Open folder", Glyph = Glyphs.Folder, Visible = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            openResultBtn.Click += (s, e) => { if (lastResultFolder != null) Process.Start("explorer.exe", "\"" + lastResultFolder + "\""); };
            top.Controls.Add(statusLabel, 0, 0);
            top.Controls.Add(progress, 1, 0);
            top.Controls.Add(openInstructionsBtn, 2, 0);
            top.Controls.Add(openResultBtn, 3, 0);

            logBox = new InputBox(multiline: true) { Dock = DockStyle.Fill };
            var spacer = new Panel { Dock = DockStyle.Top, Height = 6 };
            f.Controls.Add(logBox);
            f.Controls.Add(spacer);
            f.Controls.Add(top);
            return f;
        }

        // ============================================================ theme

        void ApplyTheme()
        {
            var t = UI.Theme.Current;
            SuspendLayout();
            BackColor = t.Window;
            ForeColor = t.Text;
            header.BackColor = t.Header;
            footer.BackColor = t.Window;
            foreach (var c in cards) { c.BackColor = t.Card; c.ForeColor = t.Text; }
            foreach (var l in mutedLabels) l.ForeColor = t.Muted;
            Walk(this, t);
            ThemeGrid(t);
            NativeTheme.Scrollbars(body, t.IsDark);
            RefreshRowStatuses();
            statusLabel.ForeColor = ToneColor(statusTone);
            UpdateAll();
            ResumeLayout(true);
            NativeTheme.TitleBar(this, t);
            Invalidate(true);
        }

        static void Walk(Control root, UI.Theme t)
        {
            foreach (Control c in root.Controls)
            {
                if (c is IThemeable th) th.ApplyTheme(t);
                if (c is ThemedComboBox cb && cb.IsHandleCreated)
                    NativeTheme.DropDown(cb, t.IsDark);
                Walk(c, t);
            }
        }

        void ThemeGrid(UI.Theme t)
        {
            grid.BackgroundColor = t.Card;
            grid.GridColor = t.GridLine;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = t.Card,
                ForeColor = t.Muted,
                SelectionBackColor = t.Card,
                SelectionForeColor = t.Muted,
                Font = Fonts.Caption,
                Padding = new Padding(6, 0, 6, 0),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
            };
            grid.DefaultCellStyle.BackColor = t.Card;
            grid.DefaultCellStyle.ForeColor = t.Text;
            grid.DefaultCellStyle.SelectionBackColor = t.Selection;
            grid.DefaultCellStyle.SelectionForeColor = t.Text;
            foreach (Control c in grid.Controls)
            {
                if (!(c is ScrollBar bar)) continue;
                if (!(bar.Tag is string tag && tag == "themed"))
                {
                    // Grid scrollbars are created lazily (when rows first overflow), so theme them as they appear.
                    bar.Tag = "themed";
                    bar.HandleCreated += (s, e) => NativeTheme.Scrollbars((Control)s, UI.Theme.Current.IsDark);
                    bar.VisibleChanged += (s, e) => NativeTheme.Scrollbars((Control)s, UI.Theme.Current.IsDark);
                }
                NativeTheme.Scrollbars(bar, t.IsDark);
                bar.Invalidate();
            }
            if (grid.IsHandleCreated) NativeTheme.Scrollbars(grid, t.IsDark);
            grid.Invalidate();
        }

        Color ToneColor(Tone tone)
        {
            var t = UI.Theme.Current;
            switch (tone)
            {
                case Tone.Good: return t.Good;
                case Tone.Warn: return t.Warn;
                case Tone.Muted: return t.Muted;
                default: return t.Text;
            }
        }

        // ============================================================ car

        async Task BrowseCarFolderAsync()
        {
            string folder = FolderPicker.Pick(this, "Pick the car's resource folder, a vehicle pack, or your resources folder",
                Settings.Get("lastCarFolder", ""), "Use this folder");
            if (folder != null) await LoadCarAsync(folder);
        }

        async Task BrowseYftAsync()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Pick one of the car's livery .yft files",
                Filter = "GTA V fragments (*.yft)|*.yft|All files|*.*",
                InitialDirectory = Settings.Get("lastCarFolder", ""),
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) await LoadCarAsync(dlg.FileName);
            }
        }

        public async Task OpenPathsAsync(IEnumerable<string> paths)
        {
            var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            var carPath = list.FirstOrDefault(p => Directory.Exists(p) || p.EndsWith(".yft", StringComparison.OrdinalIgnoreCase));
            if (carPath != null) await LoadCarAsync(carPath);
            var images = list.Where(p => File.Exists(p) && ImageLoader.IsSupported(p)).ToList();
            if (images.Count > 0) await AddImagesAsync(images);
        }

        public async Task LoadCarAsync(string path)
        {
            SetBusy(true, "Looking for cars with YFT liveries in " + path + " …");
            try
            {
                var result = await Task.Run(() => CarScanner.Scan(path));
                scan = result;
                Settings.Set("lastCarFolder", File.Exists(path) ? Path.GetDirectoryName(path) : path);
                foreach (var w in result.Warnings) Log("note: " + w);

                carBox.BeginUpdate();
                carBox.Items.Clear();
                foreach (var c in result.Cars) carBox.Items.Add(c);
                carBox.EndUpdate();

                if (result.Cars.Count == 0)
                {
                    car = null;
                    template = null;
                    templateBox.Items.Clear();
                    textureBox.Items.Clear();
                    liveryList.Items.Clear();
                    preview.Image = null;
                    previewCaption.Text = "";
                    Status("No livery .yft files found in " + path + ". Pick the folder that holds the car (or use Open a livery .yft).", Tone.Warn);
                    UpdateAll();
                    return;
                }

                var pick = result.Preferred ?? result.Cars.FirstOrDefault(c => c.DefaultTemplate != null) ?? result.Cars[0];
                Log($"Found {result.Cars.Count} car(s) with YFT liveries in {result.ScannedFolder}.");
                Status($"Found {result.Cars.Count} car{(result.Cars.Count == 1 ? "" : "s")}. Pick the one you made the livery for.", Tone.Normal);
                carBox.SelectedItem = pick;
                if (carBox.SelectedItem != pick) OnCarChanged();
            }
            catch (Exception ex)
            {
                ShowError(this, ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        void OnCarChanged()
        {
            car = carBox.SelectedItem as CarInfo;
            int version = ++carVersion;
            lock (loaded) loaded.Clear();

            foreach (DataGridViewRow r in grid.Rows)
            {
                var data = (Row)r.Tag;
                data.Done = null;
                // A replacement only makes sense for the car it was picked on.
                if (data.Replaces != null && (car == null || !car.Liveries.Contains(data.Replaces))) data.Replaces = null;
            }

            var liveries = car == null ? new List<LiveryFile>() : OrderedLiveries(car);
            liveryList.BeginUpdate();
            liveryList.Items.Clear();
            foreach (var l in liveries) liveryList.Items.Add(l);
            liveryList.EndUpdate();
            liveriesHeader.Title = car == null ? "Liveries on this car" : $"Liveries on this car ({liveries.Count})";
            liveriesHeader.Invalidate();

            templateBox.BeginUpdate();
            templateBox.Items.Clear();
            foreach (var l in liveries.Where(x => x.YftPath != null)) templateBox.Items.Add(l);
            templateBox.EndUpdate();

            if (car == null) { UpdateAll(); return; }
            _ = LoadLiveryThumbnailsAsync(liveries, version);

            var preferred = scan?.PreferredLivery != null && car.Liveries.Contains(scan.PreferredLivery) && scan.PreferredLivery.Usable
                ? scan.PreferredLivery
                : car.DefaultTemplate;
            if (preferred != null)
            {
                templateBox.SelectedItem = preferred;
            }
            else
            {
                template = null;
                textureBox.Items.Clear();
                preview.Image = null;
                previewCaption.Text = "";
                Status(car.Liveries.Any(l => l.Escrowed)
                    ? "This car's livery .yft files are escrow-locked. Use Open a livery .yft to pick an unlocked one."
                    : "This car's livery .yft files weren't found here. Use Open a livery .yft to pick one.", Tone.Warn);
                UpdateAll();
            }
        }

        static List<LiveryFile> OrderedLiveries(CarInfo c) =>
            c.Liveries.OrderBy(l => Naming.TrailingNumber(l.ModelName) ?? int.MaxValue).ThenBy(l => l.ModelName, StringComparer.OrdinalIgnoreCase).ToList();

        async Task OnTemplateChangedAsync()
        {
            var lf = templateBox.SelectedItem as LiveryFile;
            int request = ++templateRequest;
            template = null;
            textureBox.Items.Clear();
            if (lf == null) { UpdateAll(); return; }
            if (liveryList.Items.Contains(lf) && liveryList.SelectedItem != lf) liveryList.SelectedItem = lf;
            if (lf.Escrowed)
            {
                Status($"{lf.ModelName}.yft is escrow-locked (encrypted). Pick another livery to copy from.", Tone.Warn);
                UpdateAll();
                return;
            }

            try
            {
                var t = await Task.Run(() => GetTemplate(lf.YftPath));
                if (request != templateRequest) return;
                template = t;
                foreach (var tex in t.Textures) textureBox.Items.Add(tex);
                if (t.Suggested != null) textureBox.SelectedItem = t.Suggested;
                RefreshRowStatuses();
                if (t.Textures.Count == 0)
                    Status($"{lf.ModelName}.yft has no texture inside it to replace. Pick another livery to copy from.", Tone.Warn);
                else
                    Status($"Ready. Add your livery images for {car?.DisplayName ?? lf.ModelName}.", Tone.Normal);
                textureBox.Enabled = t.Textures.Count > 1;
            }
            catch (Exception ex)
            {
                if (request != templateRequest) return;
                Status(ex.Message, Tone.Warn);
                Log("error: " + ex.Message);
            }
            RecomputeNaming();
            liveryList.Invalidate();
            UpdateAll();
        }

        void RecomputeNaming()
        {
            var lf = templateBox.SelectedItem as LiveryFile;
            if (car == null || lf == null) { naming = null; return; }
            var labels = new HashSet<string>(car.Liveries.Select(l => l.Label).Where(l => !string.IsNullOrEmpty(l)), StringComparer.OrdinalIgnoreCase);
            naming = Naming.For(car, lf, scan?.AllYftNames, labels);
            colLabel.Visible = !naming.LiteralLabels;
            RenumberRows();
        }

        /// <summary>Loads (and caches) a livery .yft. Safe to call from worker threads.</summary>
        LiveryTemplate GetTemplate(string path)
        {
            lock (loaded)
            {
                if (loaded.TryGetValue(path, out var t)) return t;
            }
            var fresh = LiveryTemplate.Load(path);
            lock (loaded) loaded[path] = fresh;
            return fresh;
        }

        static string ThumbKey(LiveryFile lf)
        {
            try { return lf.YftPath + "|" + File.GetLastWriteTimeUtc(lf.YftPath).Ticks; }
            catch { return lf.YftPath; }
        }

        Image ThumbFor(LiveryFile lf)
        {
            if (lf?.YftPath == null) return null;
            return thumbs.TryGetValue(ThumbKey(lf), out var img) ? img : null;
        }

        async Task LoadLiveryThumbnailsAsync(List<LiveryFile> liveries, int version)
        {
            foreach (var lf in liveries.Where(l => l.Usable))
            {
                if (version != carVersion) return;
                string key = ThumbKey(lf);
                if (thumbs.ContainsKey(key)) continue;
                Image bmp = null;
                try
                {
                    bmp = await Task.Run(() =>
                    {
                        var tpl = GetTemplate(lf.YftPath);
                        var img = tpl.Suggested == null ? null : tpl.DecodePreview(tpl.Suggested, 128);
                        return img == null ? null : (Image)UiHelpers.Thumbnail(img, 96);
                    });
                }
                catch
                {
                    // A broken file just shows without a thumbnail.
                }
                thumbs[key] = bmp;
                if (version == carVersion) liveryList.Invalidate();
            }
        }

        static string LiveryDisplayName(LiveryFile lf) =>
            lf == null ? null : !string.IsNullOrWhiteSpace(lf.LabelText) ? lf.LabelText : !string.IsNullOrWhiteSpace(lf.Label) ? lf.Label : lf.ModelName;

        LiveryListText DescribeLivery(LiveryFile lf)
        {
            if (lf == null) return new LiveryListText();
            var t = UI.Theme.Current;
            var text = new LiveryListText { Title = LiveryDisplayName(lf), Sub = lf.ModelName + ".yft" };
            if (lf.YftPath == null) { text.Tag = "MISSING"; text.TagColor = t.Bad; }
            else if (lf.Escrowed) { text.Tag = "LOCKED"; text.TagColor = t.Warn; }
            else if (RowsReplacing(lf).Any()) { text.Tag = "REPLACING"; text.TagColor = t.Warn; }
            else if (template != null && CarScanner.PathEquals(lf.YftPath, template.FilePath)) { text.Tag = "TEMPLATE"; text.TagColor = t.Accent; }
            return text;
        }

        IEnumerable<Row> RowsReplacing(LiveryFile lf) =>
            grid.Rows.Cast<DataGridViewRow>().Select(r => (Row)r.Tag).Where(d => d.Replaces == lf);

        async Task ShowLiveryPreviewAsync(LiveryFile lf)
        {
            int request = ++previewRequest;
            if (lf == null)
            {
                preview.Image = null;
                previewCaption.Text = "";
                return;
            }
            if (!lf.Usable)
            {
                preview.Image = null;
                previewCaption.Text = $"{LiveryDisplayName(lf)}\n{(lf.Escrowed ? "Escrow-locked: can't be opened or previewed." : "The .yft file wasn't found.")}";
                return;
            }

            previewCaption.Text = $"{LiveryDisplayName(lf)}  ·  loading…";
            try
            {
                var texChoice = template != null && CarScanner.PathEquals(lf.YftPath, template.FilePath) ? textureBox.SelectedItem as TemplateTexture : null;
                var (bmp, info) = await Task.Run(() =>
                {
                    var tpl = GetTemplate(lf.YftPath);
                    var tex = texChoice ?? tpl.Suggested;
                    if (tex == null) return ((Image)null, "no livery texture inside");
                    var img = tpl.DecodePreview(tex, 512);
                    if (img == null) return ((Image)null, $"{tex.Width} × {tex.Height} {tex.FormatName}");
                    double k = Math.Min(420.0 / img.Width, 420.0 / img.Height);
                    var small = k < 1 ? Resizer.Resize(img, Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k))) : img;
                    return ((Image)UiHelpers.ToBitmap(small), $"{tex.Width} × {tex.Height} {tex.FormatName}  ·  {tex.MipLevels} mipmap{(tex.MipLevels == 1 ? "" : "s")}");
                });
                if (request != previewRequest) return;
                preview.Image = bmp;
                previewCaption.Text = $"{LiveryDisplayName(lf)}  ·  {lf.ModelName}.yft\n{info}";
            }
            catch (Exception ex)
            {
                if (request != previewRequest) return;
                preview.Image = null;
                previewCaption.Text = ex.Message;
            }
        }

        async Task ReplaceSelectedWithImageAsync()
        {
            if (busy || !(liveryList.SelectedItem is LiveryFile lf) || lf.YftPath == null) return;
            await BrowseImagesAsync(lf);
        }

        // ============================================================ images

        async Task BrowseImagesAsync(LiveryFile replace)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = replace == null ? "Pick your livery images" : $"Pick the image to replace {replace.ModelName}.yft with",
                Filter = ImageLoader.DialogFilter,
                Multiselect = replace == null,
                InitialDirectory = Settings.Get("lastImageFolder", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Settings.Set("lastImageFolder", Path.GetDirectoryName(dlg.FileNames[0]));
                await AddImagesAsync(dlg.FileNames, replace);
            }
        }

        static List<string> DroppedImages(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(f => File.Exists(f) && ImageLoader.IsSupported(f)).ToList() : new List<string>();

        /// <summary>Adds images as liveries. With <paramref name="replace"/>, the first image replaces that livery.</summary>
        public async Task AddImagesAsync(IEnumerable<string> paths, LiveryFile replace = null)
        {
            var added = new List<(DataGridViewRow Row, Row Data)>();
            suppressGrid = true;
            foreach (var p in paths)
            {
                if (!ImageLoader.IsSupported(p)) { Log($"skipped {Path.GetFileName(p)}: not an image type the tool reads"); continue; }
                var data = new Row { ImagePath = p, ImageName = UiHelpers.PrettyName(p) };
                if (replace != null && added.Count == 0)
                {
                    // Taking over a livery another image already targets: that one goes back to "new".
                    foreach (var other in RowsReplacing(replace).ToList()) other.Replaces = null;
                    data.Replaces = replace;
                }
                int i = grid.Rows.Add();
                var row = grid.Rows[i];
                row.Tag = data;
                row.Cells[colImage.Index].Value = Path.GetFileName(p);
                row.Cells[colDisplay.Index].Value = data.Replaces != null ? LiveryDisplayName(data.Replaces) : data.ImageName;
                row.Cells[colStatus.Index].Value = "reading…";
                added.Add((row, data));
            }
            suppressGrid = false;
            RenumberRows();
            liveryList.Invalidate();
            UpdateAll();
            if (replace != null && added.Count > 0)
            {
                grid.ClearSelection();
                added[0].Row.Selected = true;
                Status($"{Path.GetFileName(added[0].Data.ImagePath)} will replace {replace.ModelName}.yft.", Tone.Normal);
            }

            foreach (var (row, data) in added)
            {
                try
                {
                    var thumb = await Task.Run(() =>
                    {
                        var img = ImageLoader.Load(data.ImagePath);
                        data.Width = img.Width;
                        data.Height = img.Height;
                        return UiHelpers.Thumbnail(img, 96);
                    });
                    if (row.DataGridView == null) continue;
                    suppressGrid = true;
                    row.Cells[colThumb.Index].Value = thumb;
                    row.Cells[colImage.Index].Value = $"{Path.GetFileName(data.ImagePath)}\n{data.Width} × {data.Height}";
                    suppressGrid = false;
                    data.Error = null;
                }
                catch (Exception ex)
                {
                    data.Error = ex.Message;
                    Log("error: " + ex.Message);
                }
                if (row.DataGridView != null) SetRowStatus(row);
            }
            UpdateAll();
        }

        void SetRowStatus(DataGridViewRow row)
        {
            var t = UI.Theme.Current;
            var data = (Row)row.Tag;
            var cell = row.Cells[colStatus.Index];
            suppressGrid = true;
            try
            {
                if (data.Error != null)
                {
                    cell.Value = "can't read: " + data.Error;
                    cell.Style.ForeColor = cell.Style.SelectionForeColor = t.Bad;
                    return;
                }
                if (data.Done != null)
                {
                    cell.Value = data.Done;
                    cell.Style.ForeColor = cell.Style.SelectionForeColor = t.Good;
                    return;
                }
                var tex = textureBox.SelectedItem as TemplateTexture;
                if (tex != null && data.Width > 0 && Math.Abs((double)data.Width / data.Height - (double)tex.Width / tex.Height) > 0.01)
                {
                    cell.Value = $"ready · will be stretched to {tex.Width} × {tex.Height}";
                    cell.Style.ForeColor = cell.Style.SelectionForeColor = t.Warn;
                    return;
                }
                cell.Value = data.Width > 0 ? (data.Replaces != null ? "ready · replaces the old one" : "ready") : "reading…";
                cell.Style.ForeColor = cell.Style.SelectionForeColor = t.Muted;
            }
            finally
            {
                suppressGrid = false;
            }
        }

        void RefreshRowStatuses()
        {
            foreach (DataGridViewRow r in grid.Rows) SetRowStatus(r);
        }

        void RemoveSelected()
        {
            if (busy) return;
            foreach (DataGridViewRow r in grid.SelectedRows.Cast<DataGridViewRow>().ToList()) grid.Rows.Remove(r);
            RenumberRows();
            liveryList.Invalidate();
            UpdateAll();
        }

        /// <summary>Works out every row's file name and label: replacements keep the old livery's, new ones count on from the car's last.</summary>
        void RenumberRows()
        {
            if (naming == null) return;
            suppressGrid = true;
            int auto = 0, labelIndex = 0;
            foreach (DataGridViewRow row in grid.Rows)
            {
                var data = (Row)row.Tag;
                string display = row.Cells[colDisplay.Index].Value as string ?? "";
                string label;
                if (data.Replaces != null)
                {
                    data.ModelName = data.Replaces.ModelName;
                    label = naming.LiteralLabels ? display : (data.Replaces.Label ?? data.Replaces.ModelName.ToUpperInvariant());
                }
                else
                {
                    data.ModelName = data.CustomName ?? naming.ModelName(auto++);
                    label = naming.Label(labelIndex++, display);
                }
                if (!data.LabelEdited) row.Cells[colLabel.Index].Value = label;
                row.Cells[colLabel.Index].ReadOnly = data.Replaces != null;
                row.Cells[colTarget.Index].Value = data.ModelName;
            }
            suppressGrid = false;
            grid.InvalidateColumn(colTarget.Index);
        }

        void OnGridCellChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (suppressGrid || e.RowIndex < 0) return;
            // Only the user's edits count; the other cells are written by the app itself.
            if (e.ColumnIndex != colDisplay.Index && e.ColumnIndex != colLabel.Index) return;
            var row = grid.Rows[e.RowIndex];
            var data = (Row)row.Tag;
            if (data.Done != null) { data.Done = null; SetRowStatus(row); }
            if (e.ColumnIndex == colLabel.Index) data.LabelEdited = true;
            else
            {
                data.DisplayEdited = true;
                if (naming != null && naming.LiteralLabels && !data.LabelEdited)
                {
                    suppressGrid = true;
                    row.Cells[colLabel.Index].Value = row.Cells[colDisplay.Index].Value;
                    suppressGrid = false;
                }
            }
        }

        // ------------------------------------------------------------ SAVE AS menu

        void ShowTargetMenu(int rowIndex)
        {
            if (busy || rowIndex < 0 || rowIndex >= grid.Rows.Count || car == null || naming == null) return;
            var data = (Row)grid.Rows[rowIndex].Tag;
            var t = UI.Theme.Current;

            var menu = new ContextMenuStrip
            {
                Renderer = new ThemedMenuRenderer(),
                ShowImageMargin = true,
                ShowCheckMargin = false,
                ImageScalingSize = new Size(40, 40),
                Font = Fonts.Body,
                MaximumSize = new Size(560, Screen.FromControl(this).WorkingArea.Height - 40),
            };
            menu.Closed += (s, e) => BeginInvoke((Action)(() => { if (openMenu == menu) openMenu = null; menu.Dispose(); }));
            openMenu = menu;

            int autoBefore = grid.Rows.Cast<DataGridViewRow>().Take(rowIndex)
                .Count(r => ((Row)r.Tag).Replaces == null && ((Row)r.Tag).CustomName == null);
            string next = data.Replaces == null && data.CustomName == null ? data.ModelName : naming.ModelName(autoBefore);

            var asNew = new ToolStripMenuItem("Add as a new livery", GlyphImage(Glyphs.Add, t.Accent))
            {
                ShortcutKeyDisplayString = next + ".yft",
                Checked = data.Replaces == null && data.CustomName == null,
            };
            asNew.Click += (s, e) => SetTarget(data, null, null);

            var custom = new ToolStripMenuItem("New livery with my own file name…", GlyphImage(Glyphs.Edit, t.Muted))
            {
                ShortcutKeyDisplayString = data.CustomName != null ? data.CustomName + ".yft" : "",
                Checked = data.Replaces == null && data.CustomName != null,
            };
            custom.Click += (s, e) =>
            {
                string name = PromptFileName(data);
                if (name == null) return;
                var existing = car.Liveries.FirstOrDefault(l => l.YftPath != null && string.Equals(l.ModelName, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) SetTarget(data, existing, null);
                else SetTarget(data, null, name);
            };

            menu.Items.Add(asNew);
            menu.Items.Add(custom);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("REPLACE ONE OF THE CAR'S LIVERIES") { Enabled = false, Tag = "header" });
            foreach (var lf in OrderedLiveries(car).Where(l => l.YftPath != null))
            {
                bool taken = RowsReplacing(lf).Any(d => d != data);
                var item = new ToolStripMenuItem(LiveryDisplayName(lf), ThumbFor(lf))
                {
                    ShortcutKeyDisplayString = lf.ModelName + ".yft" + (taken ? "  ·  taken" : lf.Escrowed ? "  ·  locked" : ""),
                    Checked = data.Replaces == lf,
                    Enabled = !taken,
                };
                item.Click += (s, e) => SetTarget(data, lf, null);
                menu.Items.Add(item);
            }

            var rect = grid.GetCellDisplayRectangle(colTarget.Index, rowIndex, false);
            menu.Show(grid, new Point(rect.Left, rect.Bottom));
        }

        void SetTarget(Row data, LiveryFile replaces, string customName)
        {
            data.Replaces = replaces;
            data.CustomName = replaces == null ? customName : null;
            data.Done = null;
            data.LabelEdited = false;
            var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag == data);
            if (row != null && !data.DisplayEdited)
            {
                suppressGrid = true;
                row.Cells[colDisplay.Index].Value = replaces != null ? LiveryDisplayName(replaces) : data.ImageName;
                suppressGrid = false;
            }
            RenumberRows();
            RefreshRowStatuses();
            liveryList.Invalidate();
            if (replaces != null && liveryList.Items.Contains(replaces)) liveryList.SelectedItem = replaces;
            UpdateAll();
        }

        string PromptFileName(Row data)
        {
            using (var d = new ThemedDialog("New livery file name", DialogKind.Info, "Name the new livery file"))
            {
                d.AddText("Letters, numbers, _ and - only. It's saved as <name>.yft and listed in carcols.meta. Typing the name of one of the car's liveries replaces that livery.", muted: true);
                var box = d.AddInput(data.CustomName ?? data.ModelName ?? naming?.ModelName(0) ?? "");
                d.AddButton("Cancel", DialogResult.Cancel);
                d.AddButton("Use this name", DialogResult.OK, primary: true);
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                string name = box.Box.Text.Trim();
                if (name.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (!Naming.IsValidModelName(name))
                {
                    ThemedDialog.Show(this, "YFT Livery Maker", DialogKind.Warning, "That name won't work", "Use letters, numbers, _ or - (up to 63 characters), like mycar_livery_30.");
                    return null;
                }
                return name;
            }
        }

        static Image GlyphImage(string glyph, Color color)
        {
            var bmp = new Bitmap(40, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var f = new Font(Fonts.IconFamily, 16F, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(color))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(glyph, f, b, new RectangleF(0, 0, 40, 40), sf);
            }
            return bmp;
        }

        // ============================================================ settings / info

        LiveryTextureFormat SelectedFormat
        {
            get
            {
                switch (formatBox.SelectedIndex)
                {
                    case 1: return LiveryTextureFormat.DXT1;
                    case 2: return LiveryTextureFormat.BC7;
                    case 3: return LiveryTextureFormat.A8R8G8B8;
                    default: return LiveryTextureFormat.DXT5;
                }
            }
        }

        TextureSizeMode SelectedSize => (TextureSizeMode)Math.Max(0, sizeBox.SelectedIndex);

        bool InstallMode => modeSwitch.SelectedIndex == 1;

        (int W, int H) PlannedSize()
        {
            var tex = textureBox.SelectedItem as TemplateTexture;
            if (tex == null) return (0, 0);
            int Longest(int n) => tex.Width >= tex.Height ? n : Math.Max(4, n * tex.Width / tex.Height);
            int Other(int n) => tex.Width >= tex.Height ? Math.Max(4, n * tex.Height / tex.Width) : n;
            switch (SelectedSize)
            {
                case TextureSizeMode.Longest1024: return (Longest(1024), Other(1024));
                case TextureSizeMode.Longest2048: return (Longest(2048), Other(2048));
                case TextureSizeMode.Longest4096: return (Longest(4096), Other(4096));
                case TextureSizeMode.KeepImage: return (0, 0);
                default: return (tex.Width, tex.Height);
            }
        }

        void UpdateMipInfo()
        {
            var t = UI.Theme.Current;
            var fmt = SelectedFormat;
            var (w, h) = PlannedSize();
            int wanted = mipsBox.Value;
            if (w == 0)
            {
                mipInfo.Text = SelectedSize == TextureSizeMode.KeepImage
                    ? $"Each image keeps its own size (power of two), up to {wanted} mipmaps."
                    : "";
                mipInfo.ForeColor = t.Muted;
                sizeInfo.Text = "";
                return;
            }
            int max = TextureBuilder.MaxMipLevels(w, h, fmt);
            int levels = Math.Min(wanted, max);
            int lw = Math.Max(1, w >> (levels - 1)), lh = Math.Max(1, h >> (levels - 1));
            mipInfo.Text = levels < wanted
                ? $"⚠  {w} × {h} can only hold {max} mipmaps (smallest stays {(TextureBuilder.IsCompressed(fmt) ? "4 × 4" : "1 × 1")}), so you'll get {levels}."
                : $"✓  {w} × {h} with {levels} mipmaps ({w} × {h} down to {lw} × {lh})";
            mipInfo.ForeColor = levels < wanted ? t.Warn : t.Good;

            long bytes = TextureBuilder.EstimateSize(w, h, levels, fmt);
            bool big = bytes > 16L * 1024 * 1024;
            sizeInfo.Text = $"Each livery texture uses {UiHelpers.Size(bytes)} of game memory."
                            + (big ? "  That's large: FiveM warns about assets over 16 MB and they can stream in late." : "");
            sizeInfo.ForeColor = big ? t.Warn : t.Muted;
        }

        void UpdateTextureInfo()
        {
            var tex = textureBox.SelectedItem as TemplateTexture;
            if (tex == null || template == null)
            {
                textureInfo.Text = "";
                return;
            }
            string mips = tex.MipLevels == 1 ? "1 mipmap" : tex.MipLevels + " mipmaps";
            textureInfo.Text = $"New liveries are built from {template.ModelName}.yft  ·  {tex.Width} × {tex.Height} {tex.FormatName}  ·  {mips}"
                               + (template.IsGen9 ? "  ·  Enhanced edition file" : "");
        }

        void UpdateCarInfo()
        {
            if (car == null)
            {
                carInfo.Text = scan == null ? "Open the car's resource folder, a whole vehicle pack, or one of its livery .yft files." : "";
                carInfo.ForeColor = UI.Theme.Current.Muted;
                return;
            }
            carInfo.ForeColor = UI.Theme.Current.Text;
            var lines = new List<string>();
            string root = car.ResourceRoot;
            string Rel(string p) => p == null ? null : CarScanner.MakeRelative(root, p);
            if (car.Kit != null) lines.Add($"•  Mod kit {car.Kit.KitName} in {Rel(car.Kit.FilePath)}. New liveries are added to it.");
            else lines.Add("•  No carcols.meta lists these liveries, so a ready-made mod kit is written with instructions.");
            if (naming != null)
            {
                if (naming.LiteralLabels) lines.Add("•  This car's mod shop shows the label text directly, so the Mod shop name goes straight into carcols.meta.");
                else if (car.NamesLuaPath != null) lines.Add($"•  Mod shop names are kept in {Rel(car.NamesLuaPath)}.");
                else lines.Add("•  Mod shop names go in a new livery_names.lua (added to fxmanifest.lua).");
                lines.Add($"•  Next free livery: {naming.ModelName(0)}.yft  (or replace one of the {car.Liveries.Count(l => l.YftPath != null)} on the right)");
            }
            carInfo.Text = string.Join("\n", lines);
        }

        void UpdateOutputHint()
        {
            bool canInstall = car != null && car.Liveries.Count > 0 && scan != null && !car.DisplayName.EndsWith("(single file)");
            modeSwitch.SetItemEnabled(1, canInstall);
            if (!canInstall && InstallMode) modeSwitch.SelectedIndex = 0;
            outputHint.Text = InstallMode
                ? "Writes straight into the car's resource. Originals are backed up to this folder first."
                : "Makes a folder laid out like your resource here. Copy it over the resource to install.";
        }

        void UpdateAll()
        {
            UpdateTextureInfo();
            UpdateCarInfo();
            UpdateMipInfo();
            UpdateOutputHint();
            bool ready = !busy && template != null && textureBox.SelectedItem != null && grid.Rows.Count > 0
                         && grid.Rows.Cast<DataGridViewRow>().All(r => ((Row)r.Tag).Error == null && ((Row)r.Tag).Width > 0);
            makeBtn.Enabled = busy || ready;
            removeBtn.Enabled = !busy && grid.SelectedRows.Count > 0;
            clearBtn.Enabled = !busy && grid.Rows.Count > 0;
            replaceBtn.Enabled = !busy && (liveryList.SelectedItem as LiveryFile)?.YftPath != null;
            dropHint.Visible = grid.Rows.Count > 0;
        }

        void LoadSettings()
        {
            outputBox.Text = Settings.Get("outputFolder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YFT Livery Maker"));
            if (int.TryParse(Settings.Get("format", "0"), out int f) && f >= 0 && f < formatBox.Items.Count) formatBox.SelectedIndex = f;
            if (int.TryParse(Settings.Get("size", "0"), out int s) && s >= 0 && s < sizeBox.Items.Count) sizeBox.SelectedIndex = s;
            if (int.TryParse(Settings.Get("mips", "10"), out int m) && m >= 1 && m <= 13) mipsBox.Value = m;
            edgeFix.Checked = Settings.Get("edgeFix", "1") == "1";
            saveDds.Checked = Settings.Get("saveDds", "1") == "1";
            modeSwitch.SelectedIndex = Settings.Get("mode", "export") == "install" ? 1 : 0;
        }

        void SaveSettings()
        {
            Settings.Set("outputFolder", outputBox.Text);
            Settings.Set("format", formatBox.SelectedIndex.ToString());
            Settings.Set("size", sizeBox.SelectedIndex.ToString());
            Settings.Set("mips", mipsBox.Value.ToString());
            Settings.Set("edgeFix", edgeFix.Checked ? "1" : "0");
            Settings.Set("saveDds", saveDds.Checked ? "1" : "0");
            Settings.Set("mode", InstallMode ? "install" : "export");
        }

        void BrowseOutput()
        {
            string folder = FolderPicker.Pick(this, InstallMode ? "Where should backups and .dds files go?" : "Where should the liveries go?",
                outputBox.Text, "Use this folder");
            if (folder != null) outputBox.Text = folder;
        }

        // ============================================================ make

        async Task MakeAsync()
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            var job = BuildJob();
            if (job == null) return;

            if (job.Mode == OutputMode.InstallIntoResource && !ConfirmInstall(job)) return;

            SaveSettings();
            cts = new CancellationTokenSource();
            SetBusy(true, "Starting…");
            logBox.Box.Clear();
            openResultBtn.Visible = openInstructionsBtn.Visible = false;
            progress.Done = false;
            progress.Value = 0;
            suppressGrid = true;
            foreach (DataGridViewRow r in grid.Rows)
            {
                ((Row)r.Tag).Done = null;
                r.Cells[colStatus.Index].Value = "waiting";
                r.Cells[colStatus.Index].Style.ForeColor = r.Cells[colStatus.Index].Style.SelectionForeColor = UI.Theme.Current.Muted;
            }
            suppressGrid = false;

            await RunJobAsync(job);
        }

        MakeJob BuildJob()
        {
            var lf = templateBox.SelectedItem as LiveryFile;
            var tex = textureBox.SelectedItem as TemplateTexture;
            if (template == null || tex == null || lf == null) return null;
            RenumberRows();

            var job = new MakeJob
            {
                Car = car,
                Template = template,
                TextureHash = tex.NameHash,
                OutputFolder = outputBox.Text.Trim(),
                Mode = InstallMode ? OutputMode.InstallIntoResource : OutputMode.ExportToFolder,
                SaveDds = saveDds.Checked,
                SizeMode = SelectedSize,
                LiteralLabels = naming?.LiteralLabels ?? false,
                TemplateLabel = naming?.TemplateLabel,
                TakenYftNames = scan?.AllYftNames,
                Texture = new TextureSettings
                {
                    Format = SelectedFormat,
                    MipLevels = mipsBox.Value,
                    FixTransparentEdges = edgeFix.Checked,
                },
            };
            foreach (DataGridViewRow r in grid.Rows)
            {
                var data = (Row)r.Tag;
                string display = r.Cells[colDisplay.Index].Value as string;
                job.Liveries.Add(new LiveryRequest
                {
                    ImagePath = data.ImagePath,
                    DisplayName = display,
                    ModelName = data.ModelName,
                    Label = job.LiteralLabels ? display : r.Cells[colLabel.Index].Value as string,
                    // A replacement is built from the livery it replaces, so it keeps that exact model.
                    TemplatePath = data.Replaces != null && data.Replaces.Usable ? data.Replaces.YftPath : null,
                });
            }
            return job;
        }

        async Task RunJobAsync(MakeJob job)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var token = cts.Token;
                var result = await Task.Run(() => LiveryMaker.Run(job, (p, s) => BeginInvoke((Action)(() => OnProgress(p, s))), token));

                for (int i = 0; i < grid.Rows.Count && i < result.Liveries.Count; i++)
                {
                    var m = result.Liveries[i];
                    ((Row)grid.Rows[i].Tag).Done = $"✓  {(m.Replaced ? "replaced · " : "")}{m.Yft.Width}×{m.Yft.Height} {m.Yft.Format} · {m.Yft.MipLevels} mips";
                    SetRowStatus(grid.Rows[i]);
                }

                foreach (var m in result.Liveries)
                {
                    Log($"✓ {m.Request.ModelName}.yft{(m.Replaced ? " (replaced)" : "")}  →  {m.YftPath}");
                    foreach (var c in m.Yft.Checks) Log("    checked: " + c);
                }
                foreach (var n in result.Notes) Log("• " + n);
                foreach (var w in result.Warnings) Log("! " + w);
                foreach (var s in result.NextSteps) Log("TODO: " + s);
                if (result.Backups.Count > 0) Log("Backups: " + Path.GetDirectoryName(result.Backups[0]));
                Log("Instructions: " + result.InstructionsPath);

                lastResultFolder = result.OutputFolder;
                lastInstructions = result.InstructionsPath;
                openResultBtn.Visible = openInstructionsBtn.Visible = true;
                progress.Value = 1;
                progress.Done = true;
                int replaced = result.Liveries.Count(m => m.Replaced), added = result.Liveries.Count - replaced;
                string what = added > 0 && replaced > 0 ? $"{added} new and {replaced} replaced livery files"
                    : replaced > 0 ? $"{replaced} livery file{(replaced == 1 ? "" : "s")} replaced"
                    : $"{added} livery .yft file{(added == 1 ? "" : "s")} made";
                Status($"Done: {what} in {sw.Elapsed.TotalSeconds:0.0} s" + (result.Warnings.Count > 0 ? " (see warnings below)." : "."),
                    result.Warnings.Count > 0 ? Tone.Warn : Tone.Good);

                // The car's files changed: rescan so the list and the next numbers are up to date.
                if (job.Mode == OutputMode.InstallIntoResource && scan != null) await ReloadAfterInstallAsync();
            }
            catch (OperationCanceledException)
            {
                Status("Cancelled. Finished files are complete; the rest weren't started.", Tone.Warn);
            }
            catch (Exception ex)
            {
                Status("Failed: " + ex.Message, Tone.Warn);
                Log("error: " + ex);
                ShowError(this, ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        bool ConfirmInstall(MakeJob job)
        {
            using (var d = BuildInstallDialog(job))
                return d.ShowDialog(this) == DialogResult.OK;
        }

        ThemedDialog BuildInstallDialog(MakeJob job)
        {
            var own = new HashSet<string>(car.Liveries.Where(l => l.YftPath != null).Select(l => l.ModelName), StringComparer.OrdinalIgnoreCase);
            var replaced = job.Liveries.Where(l => own.Contains(l.ModelName)).Select(l => l.ModelName + ".yft").ToList();
            var added = job.Liveries.Where(l => !own.Contains(l.ModelName)).Select(l => l.ModelName + ".yft").ToList();
            string List(List<string> names) => names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";

            var changes = new List<string>();
            if (added.Count > 0) changes.Add($"{added.Count} new livery file{(added.Count == 1 ? "" : "s")}: {List(added)}");
            if (replaced.Count > 0) changes.Add($"{replaced.Count} replaced: {List(replaced)}");
            if (added.Count > 0)
                changes.Add(car.Kit != null
                    ? $"{Path.GetFileName(car.Kit.FilePath)} gets the new entries in mod kit {car.Kit.KitName}"
                    : "a ready-made mod kit is written next to the backups, with instructions");
            if (naming != null && !naming.LiteralLabels)
                changes.Add(car.NamesLuaPath != null
                    ? $"{Path.GetFileName(car.NamesLuaPath)} gets the mod shop names"
                    : "a new livery_names.lua holds the mod shop names (fxmanifest.lua loads it)");

            string heading = added.Count > 0 && replaced.Count > 0
                ? $"Add {added.Count} and replace {replaced.Count} livery{(replaced.Count == 1 ? "" : "s")} on {car.DisplayName}?"
                : replaced.Count > 0
                    ? $"Replace {replaced.Count} livery{(replaced.Count == 1 ? "" : "s")} on {car.DisplayName}?"
                    : $"Put {added.Count} livery{(added.Count == 1 ? "" : "s")} into {car.DisplayName}?";

            var d = new ThemedDialog("Put into the car's resource", DialogKind.Question, heading);
            d.AddText("Writes into", muted: true);
            d.AddPath(car.ResourceRoot);
            d.AddBullets(changes);
            d.AddText("Every file it changes or replaces is backed up first, to", muted: true);
            d.AddPath(Path.Combine(job.OutputFolder, "Backups"));
            d.AddButton("Cancel", DialogResult.Cancel);
            d.AddButton(replaced.Count > 0 && added.Count == 0 ? "Replace" : "Put it in", DialogResult.OK, primary: true);
            return d;
        }

        void SetAiSwitch(bool on)
        {
            aiSwitchQuiet = true;
            aiSwitch.Checked = on;
            aiSwitchQuiet = false;
        }

        void OnAiSwitchChanged()
        {
            if (aiSwitchQuiet) return;
            bool on = aiSwitch.Checked;
            if (on)
            {
                using (var d = BuildAiDialog())
                {
                    if (d.ShowDialog(this) != DialogResult.OK)
                    {
                        SetAiSwitch(false);
                        return;
                    }
                }
            }

            var (done, failed) = AiApps.Set(on);
            SetAiSwitch(AiApps.IsOn());
            string apps = done.Count <= 1 ? done.FirstOrDefault() : string.Join(", ", done.Take(done.Count - 1)) + " and " + done.Last();
            if (on && done.Count > 0) Status($"AI apps: added to {apps}. Restart {(done.Count == 1 ? "it" : "them")}, then ask for a livery in the chat.", Tone.Good);
            else if (!on) Status(done.Count > 0 ? $"AI apps: removed from {apps}." : "AI apps: off.", Tone.Normal);
            if (failed.Count > 0) ThemedDialog.Show(this, "AI apps", DialogKind.Warning, "Some AI apps couldn't be changed", string.Join("\n\n", failed));
        }

        ThemedDialog BuildAiDialog()
        {
            if (AiApps.RunningFromTemp)
            {
                var t = new ThemedDialog("AI apps", DialogKind.Warning, "Unzip it first");
                t.AddText("YFT Livery Maker is running from a temporary folder, probably straight from the zip, so AI apps would lose it when Windows clears that folder.");
                t.AddText("Unzip it to a folder you'll keep (like Documents), start it from there and turn this on again.", muted: true);
                t.AddButton("OK", DialogResult.Cancel, primary: true);
                return t;
            }

            var apps = AiApps.Installed().Select(a => a.Name).Distinct().ToList();
            var d = new ThemedDialog("AI apps", DialogKind.Question, apps.Count > 0 ? "Let AI apps make liveries for you?" : "No AI apps found", width: 520);
            d.AddText("AI apps can then find your cars and make liveries with YFT Livery Maker when you ask them in the chat. They start it on this PC when they need it.");
            if (apps.Count > 0)
            {
                d.AddText("Adds it to", muted: true);
                d.AddBullets(apps);
                d.AddText("Restart them, then ask something like", muted: true);
            }
            else
            {
                d.AddText("Claude Desktop, Cursor, Windsurf, VS Code and LM Studio aren't installed. Copy the setup into any app that supports MCP servers, then ask something like", muted: true);
            }
            d.AddPath(@"Make a livery for the car in D:\server\resources\[cars]\mycar from C:\Users\me\Desktop\livery.png");
            d.AddText("For Claude Code, run", muted: true);
            d.AddPath(AiApps.ClaudeCodeCommand);
            d.AddAction("Copy setup", Glyphs.Copy, () =>
            {
                try { Clipboard.SetText(AiApps.SetupText); } catch { }
            });
            if (apps.Count > 0)
            {
                d.AddButton("Cancel", DialogResult.Cancel);
                d.AddButton("Add", DialogResult.OK, primary: true);
            }
            else d.AddButton("OK", DialogResult.Cancel, primary: true);
            return d;
        }

        async Task ReloadAfterInstallAsync()
        {
            string carText = car?.DisplayName;
            string folder = scan.ScannedFolder;
            var rows = grid.Rows.Cast<DataGridViewRow>().Select(r => (Row)r.Tag).ToList();
            await LoadCarAsync(folder);
            var again = carBox.Items.Cast<CarInfo>().FirstOrDefault(c => c.DisplayName == carText);
            if (again != null) carBox.SelectedItem = again;
            foreach (var d in rows)
            {
                // These images are now part of the car; making them again would replace what was just written.
                d.Replaces = again?.Liveries.FirstOrDefault(l => string.Equals(l.ModelName, d.ModelName, StringComparison.OrdinalIgnoreCase));
                d.CustomName = null;
                d.LabelEdited = false;
            }
            RenumberRows();
            liveryList.Invalidate();
        }

        void OnProgress(double p, string s)
        {
            progress.Value = (float)p;
            statusLabel.Text = s;
            statusLabel.ForeColor = UI.Theme.Current.Text;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^\[(\d+)/\d+\]");
            if (!m.Success) return;
            int i = int.Parse(m.Groups[1].Value) - 1;
            if (i < 0 || i >= grid.Rows.Count) return;
            suppressGrid = true;
            grid.Rows[i].Cells[colStatus.Index].Value = s.Substring(s.IndexOf(':') + 1).Trim();
            grid.Rows[i].Cells[colStatus.Index].Style.ForeColor = grid.Rows[i].Cells[colStatus.Index].Style.SelectionForeColor = UI.Theme.Current.Text;
            for (int k = 0; k < i; k++)
            {
                var c = grid.Rows[k].Cells[colStatus.Index];
                if ((c.Value as string)?.StartsWith("✓") != true)
                {
                    c.Value = "✓  done";
                    c.Style.ForeColor = c.Style.SelectionForeColor = UI.Theme.Current.Good;
                }
            }
            suppressGrid = false;
        }

        // ============================================================ misc

        void SetBusy(bool on, string status = null)
        {
            busy = on;
            foreach (var c in new Control[] { openFolderBtn, openYftBtn, carBox, templateBox, addBtn, formatBox, sizeBox, mipsBox, edgeFix, saveDds, modeSwitch, outputBox, browseOutBtn, themeSwitch })
                c.Enabled = !on;
            textureBox.Enabled = !on && textureBox.Items.Count > 1;
            grid.ReadOnly = on;
            if (!on)
            {
                // ReadOnly on the grid resets per-column flags; restore the app-owned ones.
                colImage.ReadOnly = colTarget.ReadOnly = colStatus.ReadOnly = true;
                foreach (DataGridViewRow r in grid.Rows) r.Cells[colLabel.Index].ReadOnly = ((Row)r.Tag).Replaces != null;
            }
            makeBtn.Text = on ? "Cancel" : "Make liveries";
            makeBtn.Glyph = on ? Glyphs.Stop : Glyphs.Play;
            if (status != null) Status(status, Tone.Normal);
            grid.InvalidateColumn(colTarget.Index);
            UpdateAll();
        }

        void Status(string text, Tone tone)
        {
            statusTone = tone;
            statusLabel.Text = text;
            statusLabel.ForeColor = ToneColor(tone);
        }

        void Log(string line)
        {
            logBox.Box.AppendText(line + Environment.NewLine);
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        async void OnDragDrop(object sender, DragEventArgs e)
        {
            if (busy || !(e.Data.GetData(DataFormats.FileDrop) is string[] paths)) return;
            await OpenPathsAsync(paths);
        }

        // ============================================================ developer aids (screenshot mode)

        /// <summary>Waits for background loads like a user would before pressing anything.</summary>
        async Task WaitUntilReadyAsync()
        {
            for (int waited = 0; waited < 15000 && !makeBtn.Enabled; waited += 50) await Task.Delay(50);
            for (int waited = 0; waited < 8000 && car != null && car.Liveries.Any(l => l.Usable && !thumbs.ContainsKey(ThumbKey(l))); waited += 50) await Task.Delay(50);
        }

        /// <summary>Presses "Make liveries" (new-folder mode) into the given folder.</summary>
        public async Task MakeForTestAsync(string outputFolder)
        {
            await WaitUntilReadyAsync();
            modeSwitch.SelectedIndex = 0;
            outputBox.Text = outputFolder;
            await MakeAsync();
        }

        /// <summary>Points the first image at one of the car's liveries, as if picked from SAVE AS.</summary>
        public async Task ReplaceFirstForTestAsync(string modelName)
        {
            await WaitUntilReadyAsync();
            var lf = car?.Liveries.FirstOrDefault(l => string.Equals(l.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
            if (lf != null && grid.Rows.Count > 0) SetTarget((Row)grid.Rows[0].Tag, lf, null);
        }

        public void ClearLogForTest() => logBox.Box.Clear();

        /// <summary>Opens the SAVE AS menu for the first image and saves a picture of it.</summary>
        public async Task CaptureMenuAsync(string path)
        {
            await WaitUntilReadyAsync();
            if (grid.Rows.Count == 0) return;
            ShowTargetMenu(0);
            await Task.Delay(500);
            var menu = openMenu;
            if (menu == null || !menu.Visible) return;
            SaveScreenshot(menu.Handle, menu.Width, menu.Height, path);
            menu.Close();
        }

        /// <summary>Opens one of the app's dialogs with real content and saves a picture of it.</summary>
        public async Task CaptureDialogAsync(string kind, string path)
        {
            if (kind == "confirm") await WaitUntilReadyAsync();
            ThemedDialog d;
            switch (kind)
            {
                case "confirm":
                    modeSwitch.SelectedIndex = 1;
                    d = BuildInstallDialog(BuildJob());
                    break;
                case "rename":
                    d = new ThemedDialog("New livery file name", DialogKind.Info, "Name the new livery file");
                    d.AddText("Letters, numbers, _ and - only. It's saved as <name>.yft and listed in carcols.meta. Typing the name of one of the car's liveries replaces that livery.", muted: true);
                    d.AddInput("mycar_livery_30");
                    d.AddButton("Cancel", DialogResult.Cancel);
                    d.AddButton("Use this name", DialogResult.OK, primary: true);
                    break;
                case "ai":
                    d = BuildAiDialog();
                    break;
                case "warning":
                    d = BuildErrorDialog(new LiveryException("mycar_livery_7.yft is locked by FiveM Asset Escrow (encrypted), so no tool can open it. Pick one of the car's other livery .yft files that isn't escrowed."));
                    break;
                default:
                    d = BuildErrorDialog(new IOException("The process cannot access the file 'carcols.meta' because it is being used by another process."));
                    break;
            }
            using (d)
            {
                d.StartPosition = FormStartPosition.CenterParent;
                d.Show(this);
                d.Location = new Point(Left + (Width - d.Width) / 2, Top + (Height - d.Height) / 2);
                await Task.Delay(600);
                SaveScreenshot(d.Handle, d.Width, d.Height, path);
                d.Close();
            }
        }

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        /// <summary>Saves what the window really looks like on screen (title bar and custom painting included).</summary>
        public void SaveScreenshot(string path) => SaveScreenshot(Handle, Width, Height, path);

        static void SaveScreenshot(IntPtr handle, int width, int height, string path)
        {
            using (var bmp = new Bitmap(width, height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(handle, hdc, 2); }
                    finally { g.ReleaseHdc(hdc); }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // ============================================================ errors

        public static void ShowError(IWin32Window owner, Exception ex)
        {
            if (ex == null) return;
            if (ex is AggregateException agg && agg.InnerExceptions.Count == 1) ex = agg.InnerException;

            // Errors can arrive from worker threads; show them on the UI thread.
            var ui = owner as Control ?? (Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null);
            if (ui != null && !ui.IsDisposed && ui.IsHandleCreated && ui.InvokeRequired)
            {
                var captured = ex;
                ui.Invoke((Action)(() => ShowError(ui, captured)));
                return;
            }

            var parent = owner ?? (ui != null && !ui.IsDisposed ? ui : null);
            try
            {
                using (var d = BuildErrorDialog(ex)) d.ShowDialog(parent);
            }
            catch
            {
                MessageBox.Show(parent, ex.Message, "YFT Livery Maker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static ThemedDialog BuildErrorDialog(Exception ex)
        {
            if (ex is LiveryException)
            {
                var w = new ThemedDialog("YFT Livery Maker", DialogKind.Warning, "Heads up");
                w.AddText(ex.Message);
                w.AddButton("OK", DialogResult.OK, primary: true);
                return w;
            }
            var d = new ThemedDialog("YFT Livery Maker", DialogKind.Error, "Something went wrong");
            d.AddText(ex.Message);
            d.AddText(ex.GetType().Name + "  ·  Copy details puts the full error on the clipboard", muted: true);
            d.AddButton("OK", DialogResult.OK, primary: true);
            string details = ex.ToString();
            d.AddAction("Copy details", Glyphs.Copy, () => { try { Clipboard.SetText(details); } catch { } });
            return d;
        }
    }
}
