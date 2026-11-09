using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace YftLiveryMaker.UI
{
    internal enum DialogKind { Question, Info, Warning, Error, Success }

    /// <summary>Message / confirmation box in the app's theme (replaces MessageBox).</summary>
    internal sealed class ThemedDialog : Form
    {
        readonly FlowLayoutPanel body;
        readonly FlowLayoutPanel buttons;
        readonly Panel footer;
        readonly TableLayoutPanel content;
        const int FooterHeight = 64;
        readonly int width;
        readonly StringBuilder copyText = new StringBuilder();
        readonly DialogKind kind;

        public ThemedDialog(string caption, DialogKind kind, string heading, int width = 470)
        {
            this.kind = kind;
            this.width = width;
            var t = Theme.Current;

            Text = caption;
            Font = Fonts.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            BackColor = t.Card;
            ForeColor = t.Text;

            content = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(24, 22, 28, 18),
                Margin = new Padding(0),
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            content.Controls.Add(new KindBadge(kind) { Margin = new Padding(0, 0, 16, 0) }, 0, 0);

            body = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            content.Controls.Add(body, 1, 0);

            var head = new Label
            {
                Text = heading,
                Font = Fonts.Section,
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Margin = new Padding(0, 2, 0, 10),
            };
            body.Controls.Add(head);
            copyText.AppendLine(heading).AppendLine();

            footer = new Panel { Dock = DockStyle.Fill, BackColor = t.Window, Padding = new Padding(20, 14, 20, 14), Margin = new Padding(0) };
            footer.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            footer.Controls.Add(buttons);

            // Message on top, button bar below. The window is sized to the message in OnLoad.
            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 2,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, FooterHeight));
            root.Controls.Add(content, 0, 0);
            root.Controls.Add(footer, 0, 1);
            Controls.Add(root);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeTheme.TitleBar(this, Theme.Current);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Esc closes a single-button box too.
            if (CancelButton == null && buttons.Controls.Count > 0) CancelButton = (IButtonControl)buttons.Controls[0];

            // Size the window to the message (Windows' own auto-size for fixed dialogs comes out a little narrow).
            float scale = DeviceDpi / 96f;
            var pref = content.GetPreferredSize(Size.Empty);
            ClientSize = new Size(pref.Width, pref.Height + (int)(FooterHeight * scale));

            var owner = Owner;
            if (owner != null && owner.Visible && owner.WindowState != FormWindowState.Minimized)
                Location = new Point(owner.Left + (owner.Width - Width) / 2, owner.Top + (owner.Height - Height) / 2);
            else
                CenterToScreen();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (kind == DialogKind.Warning) SystemSounds.Exclamation.Play();
            else if (kind == DialogKind.Error) SystemSounds.Hand.Play();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                try { Clipboard.SetText(copyText.ToString().TrimEnd()); } catch { }
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        public void AddText(string text, bool muted = false)
        {
            var l = new Label
            {
                Text = text,
                Font = muted ? Fonts.Small : Fonts.Body,
                ForeColor = muted ? Theme.Current.Muted : Theme.Current.Text,
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Margin = new Padding(0, 0, 0, muted ? 4 : 8),
            };
            body.Controls.Add(l);
            copyText.AppendLine(text);
        }

        /// <summary>A single-line text box; it gets the focus when the dialog opens.</summary>
        public InputBox AddInput(string initial)
        {
            var box = new InputBox { Width = width, Margin = new Padding(0, 2, 0, 12) };
            box.Box.Text = initial ?? "";
            body.Controls.Add(box);
            Shown += (s, e) => { box.Box.Focus(); box.Box.SelectAll(); };
            return box;
        }

        public void AddPath(string path)
        {
            body.Controls.Add(new PathChip(path, width) { Margin = new Padding(0, 0, 0, 12) });
            copyText.AppendLine("  " + path);
        }

        public void AddBullets(IEnumerable<string> items)
        {
            var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(2, 0, 0, i == list.Count - 1 ? 12 : 4) };
                row.Controls.Add(new Label { Text = "•", AutoSize = true, ForeColor = Theme.Current.Accent, Font = Fonts.BodySemibold, Margin = new Padding(0, 0, 6, 0) });
                row.Controls.Add(new Label { Text = list[i], AutoSize = true, MaximumSize = new Size(width - 20, 0), Margin = new Padding(0) });
                body.Controls.Add(row);
                copyText.AppendLine("  - " + list[i]);
            }
        }

        public ModernButton AddButton(string text, DialogResult result, bool primary = false)
        {
            var b = new ModernButton
            {
                Text = text,
                Kind = primary ? ButtonKind.Primary : ButtonKind.Secondary,
                DialogResult = result,
                MinimumSize = new Size(96, 0),
                Margin = new Padding(8, 0, 0, 0),
            };
            buttons.Controls.Add(b);
            if (primary) AcceptButton = b;
            if (result == DialogResult.Cancel) CancelButton = b;
            return b;
        }

        /// <summary>A button that does something without closing the dialog (e.g. copy details).</summary>
        public ModernButton AddAction(string text, string glyph, Action action)
        {
            var b = new ModernButton { Text = text, Glyph = glyph, Kind = ButtonKind.Ghost, Margin = new Padding(8, 0, 0, 0) };
            b.Click += (s, e) => action();
            buttons.Controls.Add(b);
            return b;
        }

        public static void Show(IWin32Window owner, string caption, DialogKind kind, string heading, string message)
        {
            using (var d = new ThemedDialog(caption, kind, heading))
            {
                if (!string.IsNullOrEmpty(message)) d.AddText(message);
                d.AddButton("OK", DialogResult.OK, primary: true);
                d.ShowDialog(owner);
            }
        }

        /// <summary>Round coloured badge with ?, i, !, x or a tick.</summary>
        sealed class KindBadge : Control
        {
            readonly DialogKind kind;

            public KindBadge(DialogKind kind)
            {
                this.kind = kind;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Size = new Size(40, 40);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var t = Theme.Current;
                var g = e.Graphics;
                g.Clear(Gfx.Behind(this));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill;
                string mark;
                switch (kind)
                {
                    case DialogKind.Warning: fill = t.Warn; mark = "!"; break;
                    case DialogKind.Error: fill = t.Bad; mark = "×"; break;
                    case DialogKind.Success: fill = t.Good; mark = "✓"; break;
                    case DialogKind.Info: fill = t.Accent; mark = "i"; break;
                    default: fill = t.Accent; mark = "?"; break;
                }
                using (var halo = new SolidBrush(Color.FromArgb(t.IsDark ? 60 : 45, fill)))
                    g.FillEllipse(halo, 0, 0, Width - 1, Height - 1);
                float inset = Width * 0.14f;
                using (var b = new SolidBrush(fill))
                    g.FillEllipse(b, inset, inset, Width - 1 - 2 * inset, Height - 1 - 2 * inset);
                using (var f = new Font("Segoe UI Semibold", 13F))
                    TextRenderer.DrawText(g, mark, f, ClientRectangle, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>A path in a rounded monospace box, wrapped at any character so long paths stay readable.</summary>
        sealed class PathChip : Control
        {
            const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.Left;
            readonly int width;

            public PathChip(string path, int width)
            {
                this.width = width;
                Text = path;
                Font = Fonts.Mono;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AutoSize = true;
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                float s = Gfx.Scale(this);
                int w = (int)(width * s);
                int padX = (int)(12 * s), padY = (int)(8 * s);
                var text = TextRenderer.MeasureText(Text, Font, new Size(w - 2 * padX, int.MaxValue), Flags);
                return new Size(w, text.Height + 2 * padY);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var t = Theme.Current;
                var g = e.Graphics;
                g.Clear(Gfx.Behind(this));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = Gfx.Scale(this);
                Gfx.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 7 * s, t.Input, t.Border);
                int padX = (int)(12 * s), padY = (int)(8 * s);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(padX, padY, Width - 2 * padX, Height - 2 * padY), t.Text, Flags);
            }
        }
    }

    /// <summary>The modern Windows folder picker (the old FolderBrowserDialog is the tree-view one).</summary>
    internal static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCoClass { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);

        const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        /// <summary>Returns the chosen folder, or null if cancelled.</summary>
        public static string Pick(IWin32Window owner, string title, string startFolder, string okLabel = null)
        {
            IFileDialog dialog = null;
            try
            {
                dialog = (IFileDialog)new FileOpenDialogCoClass();
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(okLabel)) dialog.SetOkButtonLabel(okLabel);
                string start = ExistingFolder(startFolder);
                if (start != null)
                {
                    try
                    {
                        SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
                        dialog.SetFolder(item);
                    }
                    catch { }
                }

                int hr = dialog.Show(owner?.Handle ?? IntPtr.Zero);
                if (hr == ERROR_CANCELLED) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out var result);
                result.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                return path;
            }
            catch (COMException)
            {
                return FallbackPick(owner, title, startFolder);
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        static string ExistingFolder(string path)
        {
            try
            {
                for (var d = string.IsNullOrEmpty(path) ? null : new DirectoryInfo(path); d != null; d = d.Parent)
                    if (d.Exists) return d.FullName;
            }
            catch { }
            return null;
        }

        static string FallbackPick(IWin32Window owner, string title, string startFolder)
        {
            using (var dlg = new FolderBrowserDialog { Description = title, SelectedPath = ExistingFolder(startFolder) ?? "" })
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
        }
    }
}
