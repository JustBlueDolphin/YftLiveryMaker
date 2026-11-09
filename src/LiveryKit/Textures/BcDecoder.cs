using System;

namespace LiveryKit.Textures
{
    /// <summary>Block decoders for BC1 (DXT1), BC2 (DXT3), BC3 (DXT5) and BC7. Output is RGBA8.</summary>
    public static class BcDecoder
    {
        /// <summary>Decodes a whole BC surface of the given pixel size into RGBA8.</summary>
        public static byte[] DecodeSurface(byte[] data, int offset, int width, int height, BcKind kind)
        {
            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
            int blockBytes = kind == BcKind.BC1 ? 8 : 16;
            long need = (long)bw * bh * blockBytes;
            if (offset + need > data.Length) throw new ArgumentException("Texture data is shorter than its size says.");

            var outPx = new byte[width * height * 4];
            var block = new byte[64];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int src = offset + (by * bw + bx) * blockBytes;
                    DecodeBlock(data, src, kind, block);
                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) break;
                            Buffer.BlockCopy(block, (py * 4 + px) * 4, outPx, (y * width + x) * 4, 4);
                        }
                    }
                }
            }
            return outPx;
        }

        public static void DecodeBlock(byte[] src, int p, BcKind kind, byte[] rgba64)
        {
            switch (kind)
            {
                case BcKind.BC1: DecodeColor(src, p, rgba64, true); break;
                case BcKind.BC2:
                    DecodeColor(src, p + 8, rgba64, false);
                    for (int i = 0; i < 16; i++)
                    {
                        int nib = (src[p + i / 2] >> ((i & 1) * 4)) & 0xF;
                        rgba64[i * 4 + 3] = (byte)(nib * 17);
                    }
                    break;
                case BcKind.BC3:
                    DecodeColor(src, p + 8, rgba64, false);
                    DecodeAlpha(src, p, rgba64);
                    break;
                case BcKind.BC7: Bc7Decoder.DecodeBlock(src, p, rgba64); break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        static void DecodeColor(byte[] s, int p, byte[] o, bool bc1)
        {
            int c0 = s[p] | (s[p + 1] << 8);
            int c1 = s[p + 2] | (s[p + 3] << 8);
            uint idx = (uint)(s[p + 4] | (s[p + 5] << 8) | (s[p + 6] << 16) | (s[p + 7] << 24));

            Span4 pal = default;
            Expand565(c0, out pal.R0, out pal.G0, out pal.B0);
            Expand565(c1, out pal.R1, out pal.G1, out pal.B1);
            bool four = !bc1 || c0 > c1;
            int r2, g2, b2, r3, g3, b3, a3 = 255;
            if (four)
            {
                r2 = (2 * pal.R0 + pal.R1) / 3; g2 = (2 * pal.G0 + pal.G1) / 3; b2 = (2 * pal.B0 + pal.B1) / 3;
                r3 = (pal.R0 + 2 * pal.R1) / 3; g3 = (pal.G0 + 2 * pal.G1) / 3; b3 = (pal.B0 + 2 * pal.B1) / 3;
            }
            else
            {
                r2 = (pal.R0 + pal.R1) / 2; g2 = (pal.G0 + pal.G1) / 2; b2 = (pal.B0 + pal.B1) / 2;
                r3 = g3 = b3 = 0; a3 = 0;
            }

            for (int i = 0; i < 16; i++)
            {
                int k = (int)((idx >> (2 * i)) & 3);
                int o4 = i * 4;
                switch (k)
                {
                    case 0: o[o4] = (byte)pal.R0; o[o4 + 1] = (byte)pal.G0; o[o4 + 2] = (byte)pal.B0; o[o4 + 3] = 255; break;
                    case 1: o[o4] = (byte)pal.R1; o[o4 + 1] = (byte)pal.G1; o[o4 + 2] = (byte)pal.B1; o[o4 + 3] = 255; break;
                    case 2: o[o4] = (byte)r2; o[o4 + 1] = (byte)g2; o[o4 + 2] = (byte)b2; o[o4 + 3] = 255; break;
                    default: o[o4] = (byte)r3; o[o4 + 1] = (byte)g3; o[o4 + 2] = (byte)b3; o[o4 + 3] = (byte)a3; break;
                }
            }
        }

        struct Span4 { public int R0, G0, B0, R1, G1, B1; }

        internal static void Expand565(int c, out int r, out int g, out int b)
        {
            int r5 = (c >> 11) & 31, g6 = (c >> 5) & 63, b5 = c & 31;
            r = (r5 << 3) | (r5 >> 2);
            g = (g6 << 2) | (g6 >> 4);
            b = (b5 << 3) | (b5 >> 2);
        }

        static void DecodeAlpha(byte[] s, int p, byte[] o)
        {
            int a0 = s[p], a1 = s[p + 1];
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)s[p + 2 + i] << (8 * i);
            var pal = new int[8];
            AlphaPalette(a0, a1, pal);
            for (int i = 0; i < 16; i++)
            {
                o[i * 4 + 3] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
            }
        }

        internal static void AlphaPalette(int a0, int a1, int[] pal)
        {
            pal[0] = a0; pal[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
            }
            else
            {
                for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
                pal[6] = 0; pal[7] = 255;
            }
        }
    }

    public enum BcKind { BC1, BC2, BC3, BC7 }
}
