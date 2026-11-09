using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiveryKit.Imaging
{
    public static class ImageSaver
    {
        public static void SavePng(RgbaImage img, string path)
        {
            var bgra = new byte[img.Pixels.Length];
            var s = img.Pixels;
            for (int i = 0; i < s.Length; i += 4)
            {
                bgra[i] = s[i + 2]; bgra[i + 1] = s[i + 1]; bgra[i + 2] = s[i]; bgra[i + 3] = s[i + 3];
            }
            var src = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, bgra, img.Width * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}
