using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiveryKit.Textures;

namespace LiveryKit.Imaging
{
    /// <summary>Loads livery artwork from PNG, JPG, BMP, GIF, TIFF, TGA, PSD/PSB or DDS.</summary>
    public static class ImageLoader
    {
        public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".tga", ".psd", ".psb", ".dds", ".webp" };

        public static string DialogFilter =>
            "Livery images|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|All files|*.*";

        public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        public static RgbaImage Load(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { throw new IOException($"Couldn't read '{Path.GetFileName(path)}': {ex.Message}", ex); }

            try
            {
                return Decode(data, Path.GetExtension(path));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                throw new InvalidDataException($"'{Path.GetFileName(path)}' couldn't be opened as an image: {ex.Message}", ex);
            }
        }

        public static RgbaImage Decode(byte[] data, string extensionHint = null)
        {
            if (data.Length < 4) throw new InvalidDataException("The file is empty.");
            if (DdsFile.IsDds(data)) return DdsFile.DecodeTopLevel(data);
            if (PsdReader.IsPsd(data)) return PsdReader.Decode(data);
            if (string.Equals(extensionHint, ".tga", StringComparison.OrdinalIgnoreCase)) return TgaReader.Decode(data);
            return DecodeWic(data);
        }

        static RgbaImage DecodeWic(byte[] data)
        {
            using (var ms = new MemoryStream(data, false))
            {
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames[0];
                if (frame.Format != PixelFormats.Bgra32)
                {
                    frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                }

                int w = frame.PixelWidth, h = frame.PixelHeight;
                var px = new byte[checked(w * h * 4)];
                frame.CopyPixels(px, w * 4, 0);
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte b = px[i];
                    px[i] = px[i + 2];
                    px[i + 2] = b;
                }
                return new RgbaImage(w, h, px);
            }
        }
    }
}
