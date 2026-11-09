using System;
using System.Threading.Tasks;

namespace LiveryKit.Imaging
{
    /// <summary>
    /// Separable Catmull-Rom resize on premultiplied alpha, so transparent pixels don't
    /// bleed their (invisible) colour into visible edges.
    /// </summary>
    public static class Resizer
    {
        public static RgbaImage Resize(RgbaImage src, int width, int height)
        {
            if (src.Width == width && src.Height == height) return src;

            var hx = Contributors(src.Width, width);
            var hy = Contributors(src.Height, height);

            int sw = src.Width, sh = src.Height;
            var s = src.Pixels;

            // Horizontal pass: sw x sh bytes -> width x sh premultiplied floats.
            var tmp = new float[(long)width * sh * 4];
            Parallel.For(0, sh, y =>
            {
                int row = y * sw * 4;
                for (int x = 0; x < width; x++)
                {
                    var c = hx[x];
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int k = 0; k < c.Count; k++)
                    {
                        int p = row + (c.Start + k) * 4;
                        float wa = c.Weights[k] * s[p + 3];
                        r += wa * s[p]; g += wa * s[p + 1]; b += wa * s[p + 2]; a += wa;
                    }
                    long o = ((long)y * width + x) * 4;
                    tmp[o] = r; tmp[o + 1] = g; tmp[o + 2] = b; tmp[o + 3] = a;
                }
            });

            // Vertical pass, then un-premultiply.
            var dst = new RgbaImage(width, height);
            var d = dst.Pixels;
            Parallel.For(0, height, y =>
            {
                var c = hy[y];
                for (int x = 0; x < width; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int k = 0; k < c.Count; k++)
                    {
                        long p = ((long)(c.Start + k) * width + x) * 4;
                        float w = c.Weights[k];
                        r += w * tmp[p]; g += w * tmp[p + 1]; b += w * tmp[p + 2]; a += w * tmp[p + 3];
                    }
                    int o = (y * width + x) * 4;
                    if (a <= 0.5f)
                    {
                        d[o] = d[o + 1] = d[o + 2] = 0; d[o + 3] = 0;
                        continue;
                    }
                    float inv = 1f / a;
                    d[o] = ToByte(r * inv); d[o + 1] = ToByte(g * inv); d[o + 2] = ToByte(b * inv);
                    d[o + 3] = ToByte(a);
                }
            });
            return dst;
        }

        static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

        sealed class Contrib
        {
            public int Start, Count;
            public float[] Weights;
        }

        static Contrib[] Contributors(int srcSize, int dstSize)
        {
            double scale = (double)dstSize / srcSize;
            double support = 2.0;                      // Catmull-Rom radius
            double filterScale = scale < 1 ? 1 / scale : 1;
            double radius = support * filterScale;

            var list = new Contrib[dstSize];
            for (int i = 0; i < dstSize; i++)
            {
                double center = (i + 0.5) / scale - 0.5;
                int start = (int)Math.Floor(center - radius) + 1;
                int end = (int)Math.Ceiling(center + radius) - 1;
                int count = end - start + 1;
                var raw = new double[count];
                double sum = 0;
                for (int k = 0; k < count; k++)
                {
                    double w = CatmullRom((start + k - center) / filterScale);
                    raw[k] = w;
                    sum += w;
                }

                // Clamp taps to the image (edge pixels repeat), merging weights that fall outside.
                int cs = Math.Max(0, start), ce = Math.Min(srcSize - 1, end);
                var weights = new float[ce - cs + 1];
                for (int k = 0; k < count; k++)
                {
                    int sx = Math.Min(srcSize - 1, Math.Max(0, start + k));
                    weights[sx - cs] += (float)(raw[k] / (sum == 0 ? 1 : sum));
                }
                list[i] = new Contrib { Start = cs, Count = weights.Length, Weights = weights };
            }
            return list;
        }

        static double CatmullRom(double x)
        {
            x = Math.Abs(x);
            if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
            if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
            return 0;
        }
    }
}
