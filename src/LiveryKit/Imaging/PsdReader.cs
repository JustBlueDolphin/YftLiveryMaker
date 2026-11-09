using System;
using System.IO;

namespace LiveryKit.Imaging
{
    /// <summary>
    /// Reads the flattened (composite) image Photoshop stores in .psd/.psb files.
    /// Photoshop only writes it when "Maximize PSD and PSB File Compatibility" is on (the default).
    /// </summary>
    public static class PsdReader
    {
        public static bool IsPsd(byte[] d) => d.Length >= 26 && d[0] == '8' && d[1] == 'B' && d[2] == 'P' && d[3] == 'S';

        public static RgbaImage Decode(byte[] d)
        {
            if (!IsPsd(d)) throw new InvalidDataException("Not a Photoshop file.");
            int version = BE16(d, 4);
            if (version != 1 && version != 2) throw new InvalidDataException($"Unsupported PSD version {version}.");
            bool psb = version == 2;
            int channels = BE16(d, 12);
            int height = (int)BE32(d, 14);
            int width = (int)BE32(d, 18);
            int depth = BE16(d, 22);
            int mode = BE16(d, 24);

            if (depth != 8 && depth != 16)
                throw new InvalidDataException($"PSD is {depth}-bit. Save it as 8-bit or 16-bit, or export a PNG.");
            if (mode != 3 && mode != 1)
                throw new InvalidDataException("PSD colour mode must be RGB or Grayscale. Convert it (Image > Mode > RGB Color) or export a PNG.");

            long pos = 26;
            pos += 4 + BE32(d, pos);                      // colour mode data
            pos += 4 + BE32(d, pos);                      // image resources

            long layerSectionStart = pos;
            long layerSectionLength = psb ? (long)BE64(d, pos) : BE32(d, pos);
            pos += psb ? 8 : 4;
            bool mergedHasTransparency = false;
            if (layerSectionLength > 0)
            {
                // Layer info: a negative layer count means the first extra channel is the merged alpha.
                long layerInfoLength = psb ? (long)BE64(d, pos) : BE32(d, pos);
                if (layerInfoLength > 0)
                {
                    int layerCount = (short)BE16(d, pos + (psb ? 8 : 4));
                    mergedHasTransparency = layerCount < 0;
                }
            }
            pos = layerSectionStart + (psb ? 8 : 4) + layerSectionLength;

            int compression = BE16(d, pos);
            pos += 2;

            int colorChannels = mode == 3 ? 3 : 1;
            if (channels < colorChannels) throw new InvalidDataException("PSD has too few channels.");
            bool hasAlpha = mergedHasTransparency && channels > colorChannels;
            int readChannels = colorChannels + (hasAlpha ? 1 : 0);

            int bytesPerSample = depth / 8;
            int rowBytes = width * bytesPerSample;
            var planes = new byte[readChannels][];
            for (int c = 0; c < readChannels; c++) planes[c] = new byte[(long)rowBytes * height];

            if (compression == 0)
            {
                long planeSize = (long)rowBytes * height;
                if (pos + planeSize * readChannels > d.Length) throw new InvalidDataException("PSD image data is truncated.");
                for (int c = 0; c < readChannels; c++)
                {
                    Buffer.BlockCopy(d, (int)(pos + planeSize * c), planes[c], 0, (int)planeSize);
                }
            }
            else if (compression == 1)
            {
                int countSize = psb ? 4 : 2;
                long countsStart = pos;
                long dataPos = pos + (long)channels * height * countSize;
                for (int c = 0; c < channels; c++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        long ci = countsStart + ((long)c * height + y) * countSize;
                        int len = psb ? (int)BE32(d, ci) : BE16(d, ci);
                        if (c < readChannels) UnpackBits(d, dataPos, len, planes[c], y * rowBytes, rowBytes);
                        dataPos += len;
                    }
                }
            }
            else
            {
                throw new InvalidDataException($"PSD composite uses compression {compression}, which isn't supported. Export a PNG instead.");
            }

            var img = new RgbaImage(width, height);
            var px = img.Pixels;
            for (int i = 0, n = width * height; i < n; i++)
            {
                int s = i * bytesPerSample; // 16-bit samples are big-endian, so this is the high byte
                byte r = planes[0][s];
                byte g = colorChannels == 3 ? planes[1][s] : r;
                byte b = colorChannels == 3 ? planes[2][s] : r;
                byte a = hasAlpha ? planes[colorChannels][s] : (byte)255;
                if (hasAlpha && a > 0 && a < 255)
                {
                    // The composite is matted against white; undo that so colours aren't washed out at soft edges.
                    r = Unmatte(r, a); g = Unmatte(g, a); b = Unmatte(b, a);
                }
                int o = i * 4;
                px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = a;
            }

            if (IsBlankComposite(img))
                throw new InvalidDataException("This PSD has no flattened image (\"Maximize Compatibility\" was off when saving). Re-save with it on, or export a PNG.");

            return img;
        }

        static byte Unmatte(byte c, byte a)
        {
            // stored = c*a + 255*(1-a)  =>  c = (stored - 255*(1-a)) / a
            int v = (c * 255 - 255 * (255 - a) + a / 2) / a;
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        static bool IsBlankComposite(RgbaImage img)
        {
            // Photoshop writes an all-white (or all-zero) composite when compatibility is off.
            var px = img.Pixels;
            byte r0 = px[0], g0 = px[1], b0 = px[2], a0 = px[3];
            if (!((r0 == 255 && g0 == 255 && b0 == 255) || (r0 == 0 && g0 == 0 && b0 == 0 && a0 == 0))) return false;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i] != r0 || px[i + 1] != g0 || px[i + 2] != b0 || px[i + 3] != a0) return false;
            }
            return true;
        }

        static void UnpackBits(byte[] src, long pos, int len, byte[] dst, int dstPos, int dstLen)
        {
            long end = pos + len;
            int o = dstPos, oEnd = dstPos + dstLen;
            while (pos < end && o < oEnd)
            {
                int n = (sbyte)src[pos++];
                if (n >= 0)
                {
                    int count = n + 1;
                    for (int i = 0; i < count && o < oEnd && pos < end; i++) dst[o++] = src[pos++];
                }
                else if (n != -128)
                {
                    int count = 1 - n;
                    byte v = pos < end ? src[pos++] : (byte)0;
                    for (int i = 0; i < count && o < oEnd; i++) dst[o++] = v;
                }
            }
        }

        static int BE16(byte[] d, long p)
        {
            if (p + 2 > d.Length) throw new InvalidDataException("PSD file is truncated.");
            return (d[p] << 8) | d[p + 1];
        }

        static uint BE32(byte[] d, long p)
        {
            if (p + 4 > d.Length) throw new InvalidDataException("PSD file is truncated.");
            return (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
        }

        static ulong BE64(byte[] d, long p) => ((ulong)BE32(d, p) << 32) | BE32(d, p + 4);
    }
}
