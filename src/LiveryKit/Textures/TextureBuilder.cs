using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveryKit.Imaging;

namespace LiveryKit.Textures
{
    public enum LiveryTextureFormat
    {
        /// <summary>BC3: full smooth alpha, what nearly every GTA livery uses.</summary>
        DXT5,
        /// <summary>BC1: half the size, alpha is on/off only.</summary>
        DXT1,
        /// <summary>BC7: sharpest, DirectX 11 only.</summary>
        BC7,
        /// <summary>Uncompressed 32-bit. Very large.</summary>
        A8R8G8B8,
    }

    public sealed class TextureSettings
    {
        public LiveryTextureFormat Format { get; set; } = LiveryTextureFormat.DXT5;

        /// <summary>Mip levels including the full-size one. GTA liveries normally have 10.</summary>
        public int MipLevels { get; set; } = 10;

        /// <summary>Colour fully transparent pixels from the nearest artwork to avoid dark/white fringes.</summary>
        public bool FixTransparentEdges { get; set; } = true;
    }

    public sealed class EncodedTexture
    {
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int MipLevels { get; internal set; }
        public LiveryTextureFormat Format { get; internal set; }
        public DdsFormat DdsFormat { get; internal set; }
        public byte[] Dds { get; internal set; }
        public long PixelDataSize { get; internal set; }
        public List<string> Notes { get; } = new List<string>();
    }

    public static class TextureBuilder
    {
        public static DdsFormat ToDds(LiveryTextureFormat f)
        {
            switch (f)
            {
                case LiveryTextureFormat.DXT1: return DdsFormat.DXT1;
                case LiveryTextureFormat.BC7: return DdsFormat.BC7;
                case LiveryTextureFormat.A8R8G8B8: return DdsFormat.A8R8G8B8;
                default: return DdsFormat.DXT5;
            }
        }

        public static bool IsCompressed(LiveryTextureFormat f) => f != LiveryTextureFormat.A8R8G8B8;

        /// <summary>
        /// Most mip levels GTA can store for this size. GTA sizes each level as a quarter of the one
        /// above, so compressed mips must stay at least 4x4 (2048x2048 => 10 levels, 1024 => 9).
        /// </summary>
        public static int MaxMipLevels(int width, int height, LiveryTextureFormat f)
        {
            int min = Math.Min(width, height);
            int levels = 1;
            int floor = IsCompressed(f) ? 4 : 1;
            while (min / 2 >= floor && (width / 2 > 0) && (height / 2 > 0))
            {
                min /= 2; width /= 2; height /= 2;
                levels++;
            }
            return levels;
        }

        public static long EstimateSize(int width, int height, int levels, LiveryTextureFormat f)
        {
            long total = 0;
            for (int i = 0; i < levels; i++)
            {
                total += DdsFile.LevelSize(ToDds(f), Math.Max(1, width >> i), Math.Max(1, height >> i));
            }
            return total;
        }

        public static EncodedTexture Encode(RgbaImage source, TextureSettings settings, Action<double> progress = null, CancellationToken cancel = default)
        {
            var fmt = settings.Format;
            var dds = ToDds(fmt);
            int w = source.Width, h = source.Height;
            if (IsCompressed(fmt) && (w % 4 != 0 || h % 4 != 0))
                throw new ArgumentException($"Compressed textures need sizes that are multiples of 4 (got {w}x{h}).");

            var result = new EncodedTexture { Width = w, Height = h, Format = fmt, DdsFormat = dds };

            int maxLevels = MaxMipLevels(w, h, fmt);
            int levels = Math.Max(1, Math.Min(settings.MipLevels, maxLevels));
            if (levels < settings.MipLevels)
            {
                result.Notes.Add($"{w}x{h} can hold at most {maxLevels} mipmaps (the smallest must be at least {(IsCompressed(fmt) ? "4x4" : "1x1")}), so it has {levels} instead of {settings.MipLevels}.");
            }
            result.MipLevels = levels;

            var top = source;
            if (settings.FixTransparentEdges)
            {
                top = source.Clone();
                TransparentFill.Apply(top);
            }

            var encodedLevels = new List<byte[]>(levels);
            long totalBlocks = 0;
            for (int i = 0; i < levels; i++) totalBlocks += Math.Max(1, (w >> i) / 4) * (long)Math.Max(1, (h >> i) / 4);
            long doneBlocks = 0;

            byte[] levelBytes = top.Pixels;
            float[] levelFloats = null;
            int lw = w, lh = h;
            for (int level = 0; level < levels; level++)
            {
                cancel.ThrowIfCancellationRequested();
                if (level > 0)
                {
                    levelFloats = levelFloats == null
                        ? MipChain.DownFromBytes(levelBytes, lw, lh, out int nw, out int nh)
                        : MipChain.DownFromFloats(levelFloats, lw, lh, out nw, out nh);
                    lw = nw; lh = nh;
                    levelBytes = MipChain.ToBytes(levelFloats);
                }

                long blocksInLevel = Math.Max(1, lw / 4) * (long)Math.Max(1, lh / 4);
                encodedLevels.Add(EncodeLevel(levelBytes, lw, lh, fmt, cancel));
                doneBlocks += blocksInLevel;
                progress?.Invoke((double)doneBlocks / totalBlocks);
            }

            foreach (var l in encodedLevels) result.PixelDataSize += l.Length;
            result.Dds = DdsFile.Write(w, h, dds, encodedLevels);
            return result;
        }

        static byte[] EncodeLevel(byte[] rgba, int w, int h, LiveryTextureFormat fmt, CancellationToken cancel)
        {
            if (fmt == LiveryTextureFormat.A8R8G8B8)
            {
                var o = new byte[rgba.Length];
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    o[i] = rgba[i + 2]; o[i + 1] = rgba[i + 1]; o[i + 2] = rgba[i]; o[i + 3] = rgba[i + 3];
                }
                return o;
            }

            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            int bpb = fmt == LiveryTextureFormat.DXT1 ? 8 : 16;
            var output = new byte[bw * bh * bpb];

            var po = new ParallelOptions { CancellationToken = cancel };
            Parallel.For(0, bh, po,
                () => new Worker(),
                (by, state, worker) =>
                {
                    var block = worker.Block;
                    for (int bx = 0; bx < bw; bx++)
                    {
                        for (int py = 0; py < 4; py++)
                        {
                            int y = Math.Min(h - 1, by * 4 + py);
                            for (int pxi = 0; pxi < 4; pxi++)
                            {
                                int x = Math.Min(w - 1, bx * 4 + pxi);
                                Buffer.BlockCopy(rgba, (y * w + x) * 4, block, (py * 4 + pxi) * 4, 4);
                            }
                        }
                        int dst = (by * bw + bx) * bpb;
                        switch (fmt)
                        {
                            case LiveryTextureFormat.DXT1: worker.Dxt.EncodeBC1(block, output, dst); break;
                            case LiveryTextureFormat.BC7: worker.Bc7.Encode(block, output, dst); break;
                            default: worker.Dxt.EncodeBC3(block, output, dst); break;
                        }
                    }
                    return worker;
                },
                _ => { });
            return output;
        }

        sealed class Worker
        {
            public readonly byte[] Block = new byte[64];
            public readonly Bc1Bc3Encoder Dxt = new Bc1Bc3Encoder();
            Bc7Encoder bc7;
            public Bc7Encoder Bc7 => bc7 ?? (bc7 = new Bc7Encoder());
        }
    }

    /// <summary>2x2 box mip filter, weighted by alpha so invisible pixels don't tint visible ones.</summary>
    internal static class MipChain
    {
        public static float[] DownFromBytes(byte[] s, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w / 2); nh = Math.Max(1, h / 2);
            var d = new float[(long)nw * nh * 4];
            int dw = nw, dh = nh;
            Parallel.For(0, dh, y =>
            {
                int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                    Combine(
                        s, (y0 * w + x0) * 4, (y0 * w + x1) * 4, (y1 * w + x0) * 4, (y1 * w + x1) * 4,
                        d, ((long)y * dw + x) * 4);
                }
            });
            return d;
        }

        public static float[] DownFromFloats(float[] s, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w / 2); nh = Math.Max(1, h / 2);
            var d = new float[(long)nw * nh * 4];
            int dw = nw, dh = nh;
            Parallel.For(0, dh, y =>
            {
                int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                    long a = ((long)y0 * w + x0) * 4, b = ((long)y0 * w + x1) * 4, c = ((long)y1 * w + x0) * 4, e = ((long)y1 * w + x1) * 4;
                    float aa = s[a + 3], ab = s[b + 3], ac = s[c + 3], ae = s[e + 3];
                    float sum = aa + ab + ac + ae;
                    long o = ((long)y * dw + x) * 4;
                    if (sum > 0)
                    {
                        float inv = 1f / sum;
                        d[o] = (s[a] * aa + s[b] * ab + s[c] * ac + s[e] * ae) * inv;
                        d[o + 1] = (s[a + 1] * aa + s[b + 1] * ab + s[c + 1] * ac + s[e + 1] * ae) * inv;
                        d[o + 2] = (s[a + 2] * aa + s[b + 2] * ab + s[c + 2] * ac + s[e + 2] * ae) * inv;
                    }
                    else
                    {
                        d[o] = (s[a] + s[b] + s[c] + s[e]) * 0.25f;
                        d[o + 1] = (s[a + 1] + s[b + 1] + s[c + 1] + s[e + 1]) * 0.25f;
                        d[o + 2] = (s[a + 2] + s[b + 2] + s[c + 2] + s[e + 2]) * 0.25f;
                    }
                    d[o + 3] = sum * 0.25f;
                }
            });
            return d;
        }

        static void Combine(byte[] s, int a, int b, int c, int e, float[] d, long o)
        {
            float aa = s[a + 3], ab = s[b + 3], ac = s[c + 3], ae = s[e + 3];
            float sum = aa + ab + ac + ae;
            if (sum > 0)
            {
                float inv = 1f / sum;
                d[o] = (s[a] * aa + s[b] * ab + s[c] * ac + s[e] * ae) * inv;
                d[o + 1] = (s[a + 1] * aa + s[b + 1] * ab + s[c + 1] * ac + s[e + 1] * ae) * inv;
                d[o + 2] = (s[a + 2] * aa + s[b + 2] * ab + s[c + 2] * ac + s[e + 2] * ae) * inv;
            }
            else
            {
                d[o] = (s[a] + s[b] + s[c] + s[e]) * 0.25f;
                d[o + 1] = (s[a + 1] + s[b + 1] + s[c + 1] + s[e + 1]) * 0.25f;
                d[o + 2] = (s[a + 2] + s[b + 2] + s[c + 2] + s[e + 2]) * 0.25f;
            }
            d[o + 3] = sum * 0.25f;
        }

        public static byte[] ToBytes(float[] f)
        {
            var b = new byte[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                float v = f[i];
                b[i] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
            }
            return b;
        }
    }
}
