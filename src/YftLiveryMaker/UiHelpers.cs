using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LiveryKit.Imaging;

namespace YftLiveryMaker
{
    internal static class UiHelpers
    {
        /// <summary>Fits the image in size x size over a checkerboard so transparency is visible.</summary>
        public static Bitmap Thumbnail(RgbaImage img, int size)
        {
            if (img == null) return null;
            double scale = Math.Min((double)size / img.Width, (double)size / img.Height);
            int w = Math.Max(1, (int)Math.Round(img.Width * scale)), h = Math.Max(1, (int)Math.Round(img.Height * scale));
            var small = (w == img.Width && h == img.Height) ? img : Resizer.Resize(img, w, h);

            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var art = ToBitmap(small))
            {
                int ox = (size - w) / 2, oy = (size - h) / 2;
                DrawChecker(g, new Rectangle(ox, oy, w, h));
                g.DrawImage(art, ox, oy, w, h);
            }
            return bmp;
        }

        public static void DrawChecker(Graphics g, Rectangle r)
        {
            int cell = Math.Max(4, Math.Min(r.Width, r.Height) / 16);
            using (var a = new SolidBrush(Color.FromArgb(138, 138, 138)))
            using (var b = new SolidBrush(Color.FromArgb(172, 172, 172)))
            {
                for (int y = r.Top; y < r.Bottom; y += cell)
                    for (int x = r.Left; x < r.Right; x += cell)
                        g.FillRectangle((((x - r.Left) / cell + (y - r.Top) / cell) & 1) == 0 ? a : b,
                            x, y, Math.Min(cell, r.Right - x), Math.Min(cell, r.Bottom - y));
            }
        }

        public static Bitmap ToBitmap(RgbaImage img)
        {
            var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[img.Width * 4];
                var px = img.Pixels;
                for (int y = 0; y < img.Height; y++)
                {
                    int s = y * img.Width * 4;
                    for (int x = 0; x < img.Width; x++)
                    {
                        int i = s + x * 4, o = x * 4;
                        row[o] = px[i + 2]; row[o + 1] = px[i + 1]; row[o + 2] = px[i]; row[o + 3] = px[i + 3];
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        public static string PrettyName(string path)
        {
            var words = Path.GetFileNameWithoutExtension(path)
                .Replace('_', ' ').Replace('-', ' ').Replace('.', ' ')
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        public static string Size(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
    }

    /// <summary>A few remembered choices, stored as key=value lines under %AppData%.</summary>
    internal static class Settings
    {
        static string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YFT Livery Maker", "settings.txt");
        static Dictionary<string, string> values;

        /// <summary>Points settings at another file (used by the screenshot/test mode so it never touches real settings).</summary>
        public static void UseFile(string path)
        {
            FilePath = path;
            values = null;
        }

        public static string Get(string key, string fallback = null)
        {
            Load();
            return values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
        }

        public static void Set(string key, string value)
        {
            Load();
            values[key] = value ?? "";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, values.Select(kv => kv.Key + "=" + kv.Value.Replace("\r", " ").Replace("\n", " ")));
            }
            catch
            {
                // Remembering choices is a convenience; never let it break the app.
            }
        }

        static void Load()
        {
            if (values != null) return;
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch
            {
                values.Clear();
            }
        }
    }
}
