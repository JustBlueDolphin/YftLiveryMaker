using System;
using System.Collections.Generic;

namespace LiveryKit.Imaging
{
    /// <summary>
    /// Gives fully transparent pixels the colour of the nearest visible artwork ("push-pull" fill).
    /// They stay invisible, but texture filtering and mipmaps no longer drag black/white fringes
    /// into the edges of the livery, and DXT blocks along edges compress cleaner.
    /// </summary>
    public static class TransparentFill
    {
        public static void Apply(RgbaImage img)
        {
            var px = img.Pixels;
            bool anyHole = false, anyVisible = false;
            for (int i = 3; i < px.Length; i += 4)
            {
                if (px[i] == 0) anyHole = true; else anyVisible = true;
                if (anyHole && anyVisible) break;
            }
            if (!anyHole || !anyVisible) return;

            int w0 = img.Width, h0 = img.Height;

            // Push: the first level is built straight from the bytes (half size) to keep memory down.
            var levels = new List<Level>();
            var first = new Level(Math.Max(1, (w0 + 1) / 2), Math.Max(1, (h0 + 1) / 2));
            for (int y = 0; y < h0; y++)
            {
                int row = y * w0 * 4, nrow = (y / 2) * first.W;
                for (int x = 0; x < w0; x++)
                {
                    int p = row + x * 4;
                    float a = px[p + 3] / 255f;
                    if (a == 0) continue;
                    int ni = (nrow + x / 2) * 4;
                    first.D[ni] += px[p] * a; first.D[ni + 1] += px[p + 1] * a; first.D[ni + 2] += px[p + 2] * a; first.D[ni + 3] += a;
                }
            }
            levels.Add(first);

            while (levels[levels.Count - 1].W > 1 || levels[levels.Count - 1].H > 1)
            {
                var prev = levels[levels.Count - 1];
                var next = new Level(Math.Max(1, (prev.W + 1) / 2), Math.Max(1, (prev.H + 1) / 2));
                for (int y = 0; y < prev.H; y++)
                {
                    for (int x = 0; x < prev.W; x++)
                    {
                        int pi = (y * prev.W + x) * 4, ni = ((y / 2) * next.W + (x / 2)) * 4;
                        next.D[ni] += prev.D[pi]; next.D[ni + 1] += prev.D[pi + 1]; next.D[ni + 2] += prev.D[pi + 2]; next.D[ni + 3] += prev.D[pi + 3];
                    }
                }
                levels.Add(next);
            }

            // Pull: coarsest first; empty cells take their parent's (already resolved) colour.
            for (int li = levels.Count - 1; li >= 0; li--)
            {
                var lv = levels[li];
                var parent = li + 1 < levels.Count ? levels[li + 1] : null;
                for (int y = 0; y < lv.H; y++)
                {
                    for (int x = 0; x < lv.W; x++)
                    {
                        int i = (y * lv.W + x) * 4;
                        float w = lv.D[i + 3];
                        if (w > 1e-4f)
                        {
                            lv.D[i] /= w; lv.D[i + 1] /= w; lv.D[i + 2] /= w;
                        }
                        else if (parent != null)
                        {
                            int p = ((y / 2) * parent.W + (x / 2)) * 4;
                            lv.D[i] = parent.D[p]; lv.D[i + 1] = parent.D[p + 1]; lv.D[i + 2] = parent.D[p + 2];
                        }
                    }
                }
            }

            for (int y = 0; y < h0; y++)
            {
                for (int x = 0; x < w0; x++)
                {
                    int p = (y * w0 + x) * 4;
                    if (px[p + 3] != 0) continue;
                    int f = ((y / 2) * first.W + x / 2) * 4;
                    px[p] = ToByte(first.D[f]);
                    px[p + 1] = ToByte(first.D[f + 1]);
                    px[p + 2] = ToByte(first.D[f + 2]);
                }
            }
        }

        static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

        sealed class Level
        {
            public Level(int w, int h) { W = w; H = h; D = new float[(long)w * h * 4]; }
            public readonly int W, H;
            public readonly float[] D;
        }
    }
}
