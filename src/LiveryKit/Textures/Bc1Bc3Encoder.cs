using System;

namespace LiveryKit.Textures
{
    /// <summary>
    /// High quality BC1 (DXT1) / BC3 (DXT5) block encoder.
    /// Colour: PCA line fit, least-squares refinement, greedy endpoint search, and
    /// optimal tables for flat-colour blocks (so solid livery colours come out exact).
    /// Alpha: best of the 8-step and 6-step+0/255 modes.
    /// One instance per thread (it reuses scratch buffers).
    /// </summary>
    internal sealed class Bc1Bc3Encoder
    {
        // [value * 2] = endpoint 0, [value * 2 + 1] = endpoint 1, for a palette entry that lands on 'value'.
        static readonly byte[] Third5 = BuildSingleColorTable(5, true);
        static readonly byte[] Third6 = BuildSingleColorTable(6, true);
        static readonly byte[] Half5 = BuildSingleColorTable(5, false);
        static readonly byte[] Half6 = BuildSingleColorTable(6, false);

        readonly int[] pr = new int[16], pg = new int[16], pb = new int[16];
        readonly bool[] act = new bool[16];
        readonly int[] idx = new int[16], bestIdx = new int[16];
        readonly int[] alphaPal = new int[8];
        readonly int[] alphaIdx = new int[16], alphaBest = new int[16];

        public void EncodeBC3(byte[] rgba, byte[] dst, int o)
        {
            EncodeAlpha(rgba, dst, o);
            EncodeColor(rgba, dst, o + 8, false);
        }

        public void EncodeBC1(byte[] rgba, byte[] dst, int o)
        {
            bool punch = false;
            for (int i = 0; i < 16; i++) if (rgba[i * 4 + 3] < 128) { punch = true; break; }
            EncodeColor(rgba, dst, o, punch);
        }

        // ---------------------------------------------------------------- colour

        void EncodeColor(byte[] px, byte[] dst, int o, bool punch)
        {
            int count = 0;
            for (int i = 0; i < 16; i++)
            {
                pr[i] = px[i * 4]; pg[i] = px[i * 4 + 1]; pb[i] = px[i * 4 + 2];
                act[i] = !punch || px[i * 4 + 3] >= 128;
                if (act[i]) count++;
            }

            if (count == 0)
            {
                WriteColor(dst, o, 0, 0, 0xFFFFFFFFu);
                return;
            }

            int first = Array.IndexOf(act, true);
            bool solid = true;
            for (int i = 0; i < 16 && solid; i++)
            {
                if (act[i] && (pr[i] != pr[first] || pg[i] != pg[first] || pb[i] != pb[first])) solid = false;
            }

            int c0, c1;
            if (solid)
            {
                SolidFit(pr[first], pg[first], pb[first], punch, out c0, out c1);
                int value = punch ? 2 : 2; // the interpolated palette entry the tables target
                for (int i = 0; i < 16; i++) bestIdx[i] = act[i] ? value : 3;
                if (c0 == c1) for (int i = 0; i < 16; i++) if (act[i]) bestIdx[i] = 0;
            }
            else
            {
                LineFit(punch, out c0, out c1);
            }

            // Order endpoints for the mode we encoded in: 4-colour needs c0 > c1, 3-colour needs c0 <= c1.
            if (!punch)
            {
                if (c0 < c1)
                {
                    Swap(ref c0, ref c1);
                    for (int i = 0; i < 16; i++) bestIdx[i] ^= 1; // 0<->1, 2<->3
                }
                else if (c0 == c1)
                {
                    for (int i = 0; i < 16; i++) bestIdx[i] = 0;
                }
            }
            else if (c0 > c1)
            {
                Swap(ref c0, ref c1);
                for (int i = 0; i < 16; i++) if (bestIdx[i] < 2) bestIdx[i] ^= 1; // 0<->1, 2 and 3 stay
            }

            uint bits = 0;
            for (int i = 0; i < 16; i++) bits |= (uint)bestIdx[i] << (2 * i);
            WriteColor(dst, o, c0, c1, bits);
        }

        static void WriteColor(byte[] d, int o, int c0, int c1, uint bits)
        {
            d[o] = (byte)c0; d[o + 1] = (byte)(c0 >> 8);
            d[o + 2] = (byte)c1; d[o + 3] = (byte)(c1 >> 8);
            d[o + 4] = (byte)bits; d[o + 5] = (byte)(bits >> 8); d[o + 6] = (byte)(bits >> 16); d[o + 7] = (byte)(bits >> 24);
        }

        static void SolidFit(int r, int g, int b, bool half, out int c0, out int c1)
        {
            var t5 = half ? Half5 : Third5;
            var t6 = half ? Half6 : Third6;
            c0 = (t5[r * 2] << 11) | (t6[g * 2] << 5) | t5[b * 2];
            c1 = (t5[r * 2 + 1] << 11) | (t6[g * 2 + 1] << 5) | t5[b * 2 + 1];
        }

        void LineFit(bool punch, out int c0Out, out int c1Out)
        {
            // Mean and covariance of the colours that count.
            double n = 0, mr = 0, mg = 0, mb = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!act[i]) continue;
                n++; mr += pr[i]; mg += pg[i]; mb += pb[i];
            }
            mr /= n; mg /= n; mb /= n;

            double crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!act[i]) continue;
                double r = pr[i] - mr, g = pg[i] - mg, b = pb[i] - mb;
                crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
            }

            // Principal axis by power iteration, seeded with the widest channel.
            double vr, vg, vb;
            if (crr >= cgg && crr >= cbb) { vr = crr; vg = crg; vb = crb; }
            else if (cgg >= cbb) { vr = crg; vg = cgg; vb = cgb; }
            else { vr = crb; vg = cgb; vb = cbb; }
            for (int it = 0; it < 8; it++)
            {
                double nr = crr * vr + crg * vg + crb * vb;
                double ng = crg * vr + cgg * vg + cgb * vb;
                double nb = crb * vr + cgb * vg + cbb * vb;
                double len = Math.Sqrt(nr * nr + ng * ng + nb * nb);
                if (len < 1e-12) break;
                vr = nr / len; vg = ng / len; vb = nb / len;
            }
            double vlen = Math.Sqrt(vr * vr + vg * vg + vb * vb);
            if (vlen < 1e-12) { vr = vg = vb = 0.57735; } else { vr /= vlen; vg /= vlen; vb /= vlen; }

            double tmin = double.MaxValue, tmax = double.MinValue;
            for (int i = 0; i < 16; i++)
            {
                if (!act[i]) continue;
                double t = (pr[i] - mr) * vr + (pg[i] - mg) * vg + (pb[i] - mb) * vb;
                if (t < tmin) tmin = t;
                if (t > tmax) tmax = t;
            }

            double e0r = mr + vr * tmax, e0g = mg + vg * tmax, e0b = mb + vb * tmax;
            double e1r = mr + vr * tmin, e1g = mg + vg * tmin, e1b = mb + vb * tmin;

            long bestErr = long.MaxValue;
            int best0 = 0, best1 = 0;
            for (int iter = 0; iter < 4; iter++)
            {
                int q0 = Quant565(e0r, e0g, e0b), q1 = Quant565(e1r, e1g, e1b);
                long err = Evaluate(q0, q1, punch, idx);
                if (err < bestErr)
                {
                    bestErr = err; best0 = q0; best1 = q1;
                    Array.Copy(idx, bestIdx, 16);
                }
                else if (iter > 0) break;
                if (bestErr == 0) break;
                if (!LeastSquares(punch, idx, out e0r, out e0g, out e0b, out e1r, out e1g, out e1b)) break;
            }

            // Greedy +/-1 search on the quantised endpoint channels.
            for (int pass = 0; pass < 2 && bestErr > 0; pass++)
            {
                bool improved = false;
                for (int ch = 0; ch < 6; ch++)
                {
                    for (int dir = -1; dir <= 1; dir += 2)
                    {
                        int q0 = best0, q1 = best1;
                        if (!Nudge(ref q0, ref q1, ch, dir)) continue;
                        long err = Evaluate(q0, q1, punch, idx);
                        if (err < bestErr)
                        {
                            bestErr = err; best0 = q0; best1 = q1;
                            Array.Copy(idx, bestIdx, 16);
                            improved = true;
                        }
                    }
                }
                if (!improved) break;
            }

            c0Out = best0;
            c1Out = best1;
        }

        static bool Nudge(ref int q0, ref int q1, int ch, int dir)
        {
            ref int q = ref (ch < 3 ? ref q0 : ref q1);
            int c = ch % 3;
            int shift = c == 0 ? 11 : c == 1 ? 5 : 0;
            int max = c == 1 ? 63 : 31;
            int v = ((q >> shift) & max) + dir;
            if (v < 0 || v > max) return false;
            q = (q & ~(max << shift)) | (v << shift);
            return true;
        }

        long Evaluate(int q0, int q1, bool punch, int[] outIdx)
        {
            BcDecoder.Expand565(q0, out int r0, out int g0, out int b0);
            BcDecoder.Expand565(q1, out int r1, out int g1, out int b1);

            // Palette in a scaled integer domain to avoid fractions: x3 for 4-colour, x2 for 3-colour.
            int s, n;
            int pr0, pg0, pb0, pr1, pg1, pb1, pr2, pg2, pb2, pr3 = 0, pg3 = 0, pb3 = 0;
            if (!punch)
            {
                s = 3; n = 4;
                pr0 = 3 * r0; pg0 = 3 * g0; pb0 = 3 * b0;
                pr1 = 3 * r1; pg1 = 3 * g1; pb1 = 3 * b1;
                pr2 = 2 * r0 + r1; pg2 = 2 * g0 + g1; pb2 = 2 * b0 + b1;
                pr3 = r0 + 2 * r1; pg3 = g0 + 2 * g1; pb3 = b0 + 2 * b1;
            }
            else
            {
                s = 2; n = 3;
                pr0 = 2 * r0; pg0 = 2 * g0; pb0 = 2 * b0;
                pr1 = 2 * r1; pg1 = 2 * g1; pb1 = 2 * b1;
                pr2 = r0 + r1; pg2 = g0 + g1; pb2 = b0 + b1;
            }

            long total = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!act[i]) { outIdx[i] = 3; continue; }
                int r = pr[i] * s, g = pg[i] * s, b = pb[i] * s;
                int d0 = Sq(r - pr0) + Sq(g - pg0) + Sq(b - pb0);
                int d1 = Sq(r - pr1) + Sq(g - pg1) + Sq(b - pb1);
                int d2 = Sq(r - pr2) + Sq(g - pg2) + Sq(b - pb2);
                int best = 0, bd = d0;
                if (d1 < bd) { bd = d1; best = 1; }
                if (d2 < bd) { bd = d2; best = 2; }
                if (n == 4)
                {
                    int d3 = Sq(r - pr3) + Sq(g - pg3) + Sq(b - pb3);
                    if (d3 < bd) { bd = d3; best = 3; }
                }
                outIdx[i] = best;
                total += bd;
            }
            // Normalise so 3- and 4-colour errors compare on the same scale.
            return punch ? total * 9 / 4 : total;
        }

        bool LeastSquares(bool punch, int[] ix, out double e0r, out double e0g, out double e0b, out double e1r, out double e1g, out double e1b)
        {
            // Weight of endpoint 0 per palette index, scaled by S.
            int S = punch ? 2 : 3;
            double a = 0, b = 0, c = 0, xr = 0, xg = 0, xb = 0, yr = 0, yg = 0, yb = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!act[i]) continue;
                int w;
                switch (ix[i])
                {
                    case 0: w = S; break;
                    case 1: w = 0; break;
                    case 2: w = punch ? 1 : 2; break;
                    default: w = 1; break;
                }
                int v = S - w;
                a += w * w; b += w * v; c += v * v;
                xr += w * pr[i]; xg += w * pg[i]; xb += w * pb[i];
                yr += v * pr[i]; yg += v * pg[i]; yb += v * pb[i];
            }
            double det = a * c - b * b;
            e0r = e0g = e0b = e1r = e1g = e1b = 0;
            if (Math.Abs(det) < 1e-9) return false;
            double inv = S / det;
            e0r = Clamp255((c * xr - b * yr) * inv); e0g = Clamp255((c * xg - b * yg) * inv); e0b = Clamp255((c * xb - b * yb) * inv);
            e1r = Clamp255((a * yr - b * xr) * inv); e1g = Clamp255((a * yg - b * xg) * inv); e1b = Clamp255((a * yb - b * xb) * inv);
            return true;
        }

        static double Clamp255(double v) => v < 0 ? 0 : v > 255 ? 255 : v;
        static int Sq(int v) => v * v;
        static void Swap(ref int a, ref int b) { int t = a; a = b; b = t; }

        static int Quant565(double r, double g, double b)
        {
            int r5 = Clamp((int)(r * 31.0 / 255.0 + 0.5), 31);
            int g6 = Clamp((int)(g * 63.0 / 255.0 + 0.5), 63);
            int b5 = Clamp((int)(b * 31.0 / 255.0 + 0.5), 31);
            return (r5 << 11) | (g6 << 5) | b5;
        }

        static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;

        static byte[] BuildSingleColorTable(int bits, bool third)
        {
            int max = (1 << bits) - 1;
            var table = new byte[512];
            for (int v = 0; v < 256; v++)
            {
                double bestErr = double.MaxValue;
                for (int e0 = 0; e0 <= max; e0++)
                {
                    int x0 = Expand(e0, bits);
                    for (int e1 = 0; e1 <= max; e1++)
                    {
                        int x1 = Expand(e1, bits);
                        double interp = third ? (2.0 * x0 + x1) / 3.0 : (x0 + x1) / 2.0;
                        // Small penalty on endpoint spread: decoders may round the blend differently.
                        double err = Math.Abs(interp - v) + Math.Abs(x0 - x1) * 0.03;
                        if (err < bestErr)
                        {
                            bestErr = err;
                            table[v * 2] = (byte)e0;
                            table[v * 2 + 1] = (byte)e1;
                        }
                    }
                }
            }
            return table;
        }

        static int Expand(int v, int bits) => bits == 5 ? (v << 3) | (v >> 2) : (v << 2) | (v >> 4);

        // ---------------------------------------------------------------- alpha (BC3)

        void EncodeAlpha(byte[] px, byte[] dst, int o)
        {
            int min = 255, max = 0, min6 = 255, max6 = 0;
            bool hasExtremes = false;
            for (int i = 0; i < 16; i++)
            {
                int a = px[i * 4 + 3];
                if (a < min) min = a;
                if (a > max) max = a;
                if (a == 0 || a == 255) hasExtremes = true;
                else
                {
                    if (a < min6) min6 = a;
                    if (a > max6) max6 = a;
                }
            }

            if (min == max)
            {
                WriteAlpha(dst, o, min, min, null);
                return;
            }

            // 8-step mode: a0 > a1.
            BcDecoder.AlphaPalette(max, min, alphaPal);
            long err8 = AssignAlpha(px, alphaPal, alphaBest);
            int bestA0 = max, bestA1 = min;

            if (hasExtremes)
            {
                // 6-step mode with explicit 0 and 255: a0 <= a1.
                int lo = min6 <= max6 ? min6 : 0;
                int hi = min6 <= max6 ? max6 : 255;
                if (lo == hi) { if (hi < 255) hi++; else lo--; }
                BcDecoder.AlphaPalette(lo, hi, alphaPal);
                long err6 = AssignAlpha(px, alphaPal, alphaIdx);
                if (err6 < err8)
                {
                    bestA0 = lo; bestA1 = hi;
                    Array.Copy(alphaIdx, alphaBest, 16);
                }
            }

            WriteAlpha(dst, o, bestA0, bestA1, alphaBest);
        }

        static long AssignAlpha(byte[] px, int[] pal, int[] outIdx)
        {
            long total = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = px[i * 4 + 3];
                int best = 0, bd = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int d = a - pal[k];
                    d *= d;
                    if (d < bd) { bd = d; best = k; }
                }
                outIdx[i] = best;
                total += bd;
            }
            return total;
        }

        static void WriteAlpha(byte[] d, int o, int a0, int a1, int[] ix)
        {
            d[o] = (byte)a0;
            d[o + 1] = (byte)a1;
            ulong bits = 0;
            if (ix != null) for (int i = 0; i < 16; i++) bits |= (ulong)ix[i] << (3 * i);
            for (int i = 0; i < 6; i++) d[o + 2 + i] = (byte)(bits >> (8 * i));
        }
    }
}
