using System;
using System.IO;

namespace LiveryKit.Imaging
{
    /// <summary>Truevision TGA: uncompressed and RLE, true-colour, grayscale and colour-mapped.</summary>
    public static class TgaReader
    {
        public static RgbaImage Decode(byte[] d)
        {
            if (d.Length < 18) throw new InvalidDataException("TGA file is too small.");
            int idLength = d[0];
            int colorMapType = d[1];
            int imageType = d[2];
            int cmFirst = d[3] | (d[4] << 8);
            int cmLength = d[5] | (d[6] << 8);
            int cmDepth = d[7];
            int width = d[12] | (d[13] << 8);
            int height = d[14] | (d[15] << 8);
            int depth = d[16];
            int descriptor = d[17];
            bool topDown = (descriptor & 0x20) != 0;
            bool rightToLeft = (descriptor & 0x10) != 0;
            int alphaBits = descriptor & 0x0F;

            bool rle = imageType >= 9;
            int baseType = rle ? imageType - 8 : imageType;
            if (baseType < 1 || baseType > 3) throw new InvalidDataException($"Unsupported TGA image type {imageType}.");
            if (width <= 0 || height <= 0) throw new InvalidDataException("TGA has no size.");

            int pos = 18 + idLength;

            byte[] palette = null;
            int palEntryBytes = 0;
            if (colorMapType == 1)
            {
                palEntryBytes = (cmDepth + 7) / 8;
                palette = new byte[cmLength * 4];
                for (int i = 0; i < cmLength; i++)
                {
                    ReadColor(d, pos + i * palEntryBytes, cmDepth, true, palette, i * 4);
                }
                pos += cmLength * palEntryBytes;
            }

            int bpp = (depth + 7) / 8;
            if (baseType == 1 && palette == null) throw new InvalidDataException("Colour-mapped TGA without a palette.");
            if (baseType == 2 && depth != 15 && depth != 16 && depth != 24 && depth != 32) throw new InvalidDataException($"Unsupported TGA bit depth {depth}.");
            if (baseType == 3 && depth != 8 && depth != 16) throw new InvalidDataException($"Unsupported grayscale TGA bit depth {depth}.");

            // 32-bit files that declare no alpha bits are usually written with garbage in A; treat as opaque.
            bool useAlpha = (depth == 32 && alphaBits > 0) || (depth == 16 && baseType == 2 && alphaBits > 0) || (baseType == 3 && depth == 16);

            var outPx = new byte[width * height * 4];
            int count = width * height;
            var pixel = new byte[4];
            int n = 0;

            void Emit(int srcPos)
            {
                DecodePixel(d, srcPos, baseType, depth, useAlpha, palette, cmFirst, pixel);
                int x = n % width;
                int y = n / width;
                if (rightToLeft) x = width - 1 - x;
                if (!topDown) y = height - 1 - y;
                int o = (y * width + x) * 4;
                outPx[o] = pixel[0]; outPx[o + 1] = pixel[1]; outPx[o + 2] = pixel[2]; outPx[o + 3] = pixel[3];
                n++;
            }

            if (!rle)
            {
                if (pos + (long)count * bpp > d.Length) throw new InvalidDataException("TGA pixel data is truncated.");
                for (int i = 0; i < count; i++) Emit(pos + i * bpp);
            }
            else
            {
                while (n < count)
                {
                    if (pos >= d.Length) throw new InvalidDataException("TGA RLE data is truncated.");
                    int header = d[pos++];
                    int run = (header & 0x7F) + 1;
                    if ((header & 0x80) != 0)
                    {
                        if (pos + bpp > d.Length) throw new InvalidDataException("TGA RLE data is truncated.");
                        for (int i = 0; i < run && n < count; i++) Emit(pos);
                        pos += bpp;
                    }
                    else
                    {
                        if (pos + run * bpp > d.Length) throw new InvalidDataException("TGA RLE data is truncated.");
                        for (int i = 0; i < run && n < count; i++) Emit(pos + i * bpp);
                        pos += run * bpp;
                    }
                }
            }

            return new RgbaImage(width, height, outPx);
        }

        static void DecodePixel(byte[] d, int p, int baseType, int depth, bool useAlpha, byte[] palette, int cmFirst, byte[] o)
        {
            switch (baseType)
            {
                case 1:
                    {
                        int index = depth == 16 ? (d[p] | (d[p + 1] << 8)) : d[p];
                        index -= cmFirst;
                        if (index < 0 || index * 4 + 3 >= palette.Length) { o[0] = o[1] = o[2] = 0; o[3] = 255; return; }
                        Buffer.BlockCopy(palette, index * 4, o, 0, 4);
                        return;
                    }
                case 3:
                    o[0] = o[1] = o[2] = d[p];
                    o[3] = (depth == 16 && useAlpha) ? d[p + 1] : (byte)255;
                    return;
                default:
                    ReadColor(d, p, depth, useAlpha, o, 0);
                    return;
            }
        }

        static void ReadColor(byte[] d, int p, int depth, bool useAlpha, byte[] o, int oi)
        {
            switch (depth)
            {
                case 15:
                case 16:
                    {
                        int v = d[p] | (d[p + 1] << 8);
                        int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
                        o[oi] = (byte)((r << 3) | (r >> 2));
                        o[oi + 1] = (byte)((g << 3) | (g >> 2));
                        o[oi + 2] = (byte)((b << 3) | (b >> 2));
                        o[oi + 3] = (depth == 16 && useAlpha) ? ((v & 0x8000) != 0 ? (byte)255 : (byte)0) : (byte)255;
                        return;
                    }
                case 24:
                    o[oi] = d[p + 2]; o[oi + 1] = d[p + 1]; o[oi + 2] = d[p]; o[oi + 3] = 255;
                    return;
                case 32:
                    o[oi] = d[p + 2]; o[oi + 1] = d[p + 1]; o[oi + 2] = d[p]; o[oi + 3] = useAlpha ? d[p + 3] : (byte)255;
                    return;
                default:
                    throw new InvalidDataException($"Unsupported TGA colour depth {depth}.");
            }
        }
    }
}
