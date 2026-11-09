using System;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YftLiveryMaker.UI
{
    /// <summary>Colour palette for the whole window. Custom controls read <see cref="Current"/> when they paint.</summary>
    public sealed class Theme
    {
        public bool IsDark;
        public Color Window, Header, Card, Input, InputHover, Border, Focus, Text, Muted, Faint;
        public Color Accent, AccentHover, AccentPressed, OnAccent, Good, Warn, Bad, Selection, GridLine, Checker1, Checker2;

        public static readonly Theme Dark = new Theme
        {
            IsDark = true,
            Window = Hex(0x0E1014), Header = Hex(0x0A0C0F), Card = Hex(0x161920), Input = Hex(0x1E222A), InputHover = Hex(0x262B35),
            Border = Hex(0x2A2F39), Focus = Hex(0xF0533A), Text = Hex(0xE8EAEE), Muted = Hex(0x99A1AE), Faint = Hex(0x5D6471),
            Accent = Hex(0xF0533A), AccentHover = Hex(0xFF6A51), AccentPressed = Hex(0xD6432C), OnAccent = Color.White,
            Good = Hex(0x43D18D), Warn = Hex(0xF4B04A), Bad = Hex(0xFF6B6B), Selection = Hex(0x352321), GridLine = Hex(0x22262E),
            Checker1 = Hex(0x5C6068), Checker2 = Hex(0x71757D),
        };

        public static readonly Theme Light = new Theme
        {
            IsDark = false,
            Window = Hex(0xF2F3F6), Header = Hex(0xFFFFFF), Card = Hex(0xFFFFFF), Input = Hex(0xF4F5F8), InputHover = Hex(0xEBEDF1),
            Border = Hex(0xDCDFE5), Focus = Hex(0xE2482C), Text = Hex(0x1A1D23), Muted = Hex(0x5D6472), Faint = Hex(0x9AA1AD),
            Accent = Hex(0xE2482C), AccentHover = Hex(0xEE5B40), AccentPressed = Hex(0xC63C23), OnAccent = Color.White,
            Good = Hex(0x13864A), Warn = Hex(0xB25A06), Bad = Hex(0xC0392B), Selection = Hex(0xFFEAE4), GridLine = Hex(0xE8EAEE),
            Checker1 = Hex(0x9A9DA3), Checker2 = Hex(0xB3B6BB),
        };

        public static Theme Current { get; private set; } = Dark;

        public static void Use(bool dark) => Current = dark ? Dark : Light;

        static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    /// <summary>Controls that need more than ambient Back/ForeColor to follow the theme.</summary>
    internal interface IThemeable
    {
        void ApplyTheme(Theme t);
    }

    internal static class Fonts
    {
        static readonly string[] Installed = new InstalledFontCollection().Families.Select(f => f.Name).ToArray();
        static bool Has(string name) => Installed.Contains(name, StringComparer.OrdinalIgnoreCase);

        public static readonly string IconFamily = Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        static readonly string MonoFamily = Has("Cascadia Mono") ? "Cascadia Mono" : "Consolas";

        public static readonly Font Body = new Font("Segoe UI", 9.5F);
        public static readonly Font BodySemibold = new Font("Segoe UI Semibold", 9.5F);
        public static readonly Font Caption = new Font("Segoe UI Semibold", 8.25F);
        public static readonly Font Small = new Font("Segoe UI", 8.75F);
        public static readonly Font Section = new Font("Segoe UI Semibold", 11F);
        public static readonly Font Title = new Font("Segoe UI Semibold", 15F);
        public static readonly Font Button = new Font("Segoe UI Semibold", 9.5F);
        public static readonly Font BigButton = new Font("Segoe UI Semibold", 11F);
        public static readonly Font Mono = new Font(MonoFamily, 8.75F);
        public static readonly Font Empty = new Font("Segoe UI Semibold", 12F);

        public static Font Icon(float size) => new Font(IconFamily, size);
    }

    /// <summary>Glyphs from Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
    internal static class Glyphs
    {
        public const string Folder = "";
        public const string File = "";
        public const string Add = "";
        public const string Delete = "";
        public const string Clear = "";
        public const string ChevronDown = "";
        public const string Check = "";
        public const string Moon = "";
        public const string Sun = "";
        public const string Info = "";
        public const string Play = "";
        public const string Stop = "";
        public const string Minus = "";
        public const string Images = "";
        public const string Copy = "";
        public const string Replace = "";
        public const string Edit = "";
    }

    internal static class NativeTheme
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Dark or light scrollbars (and other common-control parts) for a native window.</summary>
        public static void Scrollbars(Control c, bool dark)
        {
            if (c == null || !c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        /// <summary>Drop-down lists use the file-dialog theme to get dark scrollbars.</summary>
        public static void DropDown(Control c, bool dark)
        {
            if (c == null || !c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, dark ? "DarkMode_CFD" : "Explorer", null); } catch { }
        }

        /// <summary>Title bar colours (dark mode on Windows 10 1809+, caption colours on Windows 11).</summary>
        public static void TitleBar(Form f, Theme t)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int dark = t.IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref dark, 4);
                int caption = ColorTranslator.ToWin32(t.Header);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                int text = ColorTranslator.ToWin32(t.Text);
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
                int border = ColorTranslator.ToWin32(t.IsDark ? t.Border : t.Border);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
                // Make the frame repaint now rather than on the next activation.
                SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
            }
            catch
            {
                // Older Windows: the title bar just stays default.
            }
        }
    }
}
