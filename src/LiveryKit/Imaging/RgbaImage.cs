using System;

namespace LiveryKit.Imaging
{
    public enum AlphaKind
    {
        /// <summary>Every pixel is fully opaque.</summary>
        Opaque,
        /// <summary>Only fully opaque and fully transparent pixels.</summary>
        Binary,
        /// <summary>Has semi-transparent pixels (soft edges, fades).</summary>
        Smooth,
    }

    /// <summary>8-bit straight-alpha RGBA image, top row first.</summary>
    public sealed class RgbaImage
    {
        public RgbaImage(int width, int height)
            : this(width, height, new byte[checked(width * height * 4)])
        {
        }

        public RgbaImage(int width, int height, byte[] pixels)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException($"Image size {width}x{height} is not valid.");
            if (pixels == null || pixels.Length != (long)width * height * 4) throw new ArgumentException("Pixel buffer does not match the image size.");
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        public AlphaKind GetAlphaKind()
        {
            var px = Pixels;
            bool binary = false;
            for (int i = 3; i < px.Length; i += 4)
            {
                byte a = px[i];
                if (a == 255) continue;
                if (a != 0) return AlphaKind.Smooth;
                binary = true;
            }
            return binary ? AlphaKind.Binary : AlphaKind.Opaque;
        }

        public RgbaImage Clone() => new RgbaImage(Width, Height, (byte[])Pixels.Clone());
    }
}
