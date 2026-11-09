using System;

namespace LiveryKit.Textures
{
    /// <summary>
    /// BC7 block encoder using the single-subset modes:
    ///   mode 6 - RGBA on one line, 16 steps, 7-bit + p-bit endpoints (best for most content)
    ///   mode 5 - RGB (4 steps, 7-bit) and alpha (4 steps, 8-bit) fitted separately
    ///   mode 4 - RGB and alpha separately with 8 steps for one of them (like DXT5's alpha)
    /// Each block keeps whichever decodes closest to the source. One instance per thread.
    /// </summary>
    internal sealed class Bc7Encoder
    {
        static readonly byte[] Nearest2 = NearestStepTable(Bc7Tables.Weights2);
        static readonly byte[] Nearest3 = NearestStepTable(Bc7Tables.Weights3);
        static readonly byte[] Nearest4 = NearestStepTable(Bc7Tables.Weights4);

        readonly int[] px = new int[64];
        readonly int[] idx = new int[16], bestIdx = new int[16], bestIdxA = new int[16], idxA = new int[16];
        readonly byte[] trial = new byte[16];
        readonly byte[] decoded = new byte[64];

        readonly double[] mean = new double[4], cov = new double[16], axis = new double[4], axisTmp = new double[4];
        readonly double[] e0 = new double[4], e1 = new double[4], lsX = new double[4], lsY = new double[4];
        readonly int[] q0 = new int[4], q1 = new int[4], d0 = new int[4], d1 = new int[4];
        readonly int[] bq0 = new int[4], bq1 = new int[4], dir = new int[4];

        public void Encode(byte[] rgba, byte[] dst, int o)
        {
            bool opaque = true;
            for (int i = 0; i < 64; i++)
            {
                px[i] = rgba[i];
                if ((i & 3) == 3 && rgba[i] != 255) opaque = false;
            }

            long best = EncodeMode6(opaque, dst, o);
            if (opaque || best == 0) return;

            // Transparency that doesn't follow the colour: try the modes that fit alpha on its own.
            for (int variant = 0; variant < 3 && best > 0; variant++)
            {
                if (variant == 0) EncodeSplit(5, 0, trial, 0);
                else EncodeSplit(4, variant - 1, trial, 0);
                long err = BlockError(trial);
                if (err < best)
                {
                    best = err;
                    Buffer.BlockCopy(trial, 0, dst, o, 16);
                }
            }
        }

        long BlockError(byte[] block)
        {
            Bc7Decoder.DecodeBlock(block, 0, decoded);
            long e = 0;
            for (int i = 0; i < 64; i++) { int d = decoded[i] - px[i]; e += d * d; }
            return e;
        }

        // ------------------------------------------------------------------ mode 6

        long EncodeMode6(bool opaque, byte[] dst, int o)
        {
            FitLine(4);

            long bestErr = long.MaxValue;
            int bp0 = 1, bp1 = 1;
            for (int iter = 0; iter < 4; iter++)
            {
                bool improved = false;
                for (int p0 = opaque ? 1 : 0; p0 < 2; p0++)
                {
                    for (int p1 = opaque ? 1 : 0; p1 < 2; p1++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            q0[c] = QuantP(e0[c], p0); d0[c] = (q0[c] << 1) | p0;
                            q1[c] = QuantP(e1[c], p1); d1[c] = (q1[c] << 1) | p1;
                        }
                        long err = AssignIndices(4, Bc7Tables.Weights4, Nearest4, idx);
                        if (err < bestErr)
                        {
                            bestErr = err; bp0 = p0; bp1 = p1;
                            Array.Copy(q0, bq0, 4); Array.Copy(q1, bq1, 4);
                            Array.Copy(idx, bestIdx, 16);
                            improved = true;
                        }
                    }
                }
                if (bestErr == 0 || (!improved && iter > 0)) break;
                if (!Refit(bestIdx, Bc7Tables.Weights4, 4)) break;
            }

            // The anchor (pixel 0) index must have its top bit clear.
            if (bestIdx[0] >= 8)
            {
                for (int c = 0; c < 4; c++) { int t = bq0[c]; bq0[c] = bq1[c]; bq1[c] = t; }
                int tp = bp0; bp0 = bp1; bp1 = tp;
                for (int i = 0; i < 16; i++) bestIdx[i] = 15 - bestIdx[i];
            }

            var w = new BitWriter();
            w.Write(1 << 6, 7);
            for (int c = 0; c < 4; c++) { w.Write(bq0[c], 7); w.Write(bq1[c], 7); }
            w.Write(bp0, 1); w.Write(bp1, 1);
            w.Write(bestIdx[0], 3);
            for (int i = 1; i < 16; i++) w.Write(bestIdx[i], 4);
            w.CopyTo(dst, o);
            return bestErr;
        }

        static int QuantP(double v, int p)
        {
            int q = (int)Math.Round((v - p) / 2.0);
            return q < 0 ? 0 : q > 127 ? 127 : q;
        }

        // ------------------------------------------------------------------ modes 4 and 5 (alpha fitted on its own)

        /// <summary>
        /// Mode 5: colour 7-bit / 4 steps, alpha 8-bit / 4 steps.
        /// Mode 4: colour 5-bit, alpha 6-bit; idxMode 0 = colour 4 steps + alpha 8 steps,
        ///         idxMode 1 = colour 8 steps + alpha 4 steps.
        /// </summary>
        void EncodeSplit(int mode, int idxMode, byte[] dst, int o)
        {
            int colorBits = mode == 4 ? 5 : 7;
            int alphaBits = mode == 4 ? 6 : 8;
            bool colorWide = mode == 4 && idxMode == 1;
            bool alphaWide = mode == 4 && idxMode == 0;
            int[] cw = colorWide ? Bc7Tables.Weights3 : Bc7Tables.Weights2;
            byte[] cn = colorWide ? Nearest3 : Nearest2;
            int[] aw = alphaWide ? Bc7Tables.Weights3 : Bc7Tables.Weights2;

            FitLine(3);
            long bestErr = long.MaxValue;
            for (int iter = 0; iter < 4; iter++)
            {
                for (int c = 0; c < 3; c++)
                {
                    q0[c] = Quant(e0[c], colorBits); d0[c] = Expand(q0[c], colorBits);
                    q1[c] = Quant(e1[c], colorBits); d1[c] = Expand(q1[c], colorBits);
                }
                long err = AssignIndices(3, cw, cn, idx);
                if (err >= bestErr) break;
                bestErr = err;
                Array.Copy(q0, bq0, 3); Array.Copy(q1, bq1, 3);
                Array.Copy(idx, bestIdx, 16);
                if (bestErr == 0 || !Refit(bestIdx, cw, 3)) break;
            }

            int amin = 255, amax = 0;
            for (int i = 0; i < 16; i++) { int a = px[i * 4 + 3]; if (a < amin) amin = a; if (a > amax) amax = a; }
            double fa0 = amin, fa1 = amax;
            long bestA = long.MaxValue;
            int qa0 = 0, qa1 = 0;
            for (int iter = 0; iter < 3; iter++)
            {
                int t0 = Quant(fa0, alphaBits), t1 = Quant(fa1, alphaBits);
                long err = AssignAlpha(Expand(t0, alphaBits), Expand(t1, alphaBits), aw, idxA);
                if (err >= bestA) break;
                bestA = err; qa0 = t0; qa1 = t1;
                Array.Copy(idxA, bestIdxA, 16);
                if (bestA == 0 || !RefitAlpha(bestIdxA, aw, out fa0, out fa1)) break;
            }

            // Pixel 0 of each index set is stored with one bit less, so its top bit must be clear.
            if (bestIdx[0] >= (colorWide ? 4 : 2))
            {
                for (int c = 0; c < 3; c++) { int t = bq0[c]; bq0[c] = bq1[c]; bq1[c] = t; }
                for (int i = 0; i < 16; i++) bestIdx[i] = cw.Length - 1 - bestIdx[i];
            }
            if (bestIdxA[0] >= (alphaWide ? 4 : 2))
            {
                int t = qa0; qa0 = qa1; qa1 = t;
                for (int i = 0; i < 16; i++) bestIdxA[i] = aw.Length - 1 - bestIdxA[i];
            }

            var w = new BitWriter();
            w.Write(1 << mode, mode + 1);
            w.Write(0, 2); // no channel rotation
            if (mode == 4) w.Write(idxMode, 1);
            for (int c = 0; c < 3; c++) { w.Write(bq0[c], colorBits); w.Write(bq1[c], colorBits); }
            w.Write(qa0, alphaBits); w.Write(qa1, alphaBits);

            // The 2-bit index set always comes first; in mode 4 with idxMode 1 that's the alpha set.
            int[] primary = colorWide ? bestIdxA : bestIdx;
            int[] secondary = colorWide ? bestIdx : bestIdxA;
            int sBits = mode == 4 ? 3 : 2;
            w.Write(primary[0], 1);
            for (int i = 1; i < 16; i++) w.Write(primary[i], 2);
            w.Write(secondary[0], sBits - 1);
            for (int i = 1; i < 16; i++) w.Write(secondary[i], sBits);
            w.CopyTo(dst, o);
        }

        long AssignAlpha(int a0, int a1, int[] weights, int[] outIdx)
        {
            long total = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = px[i * 4 + 3];
                int best = 0, bd = int.MaxValue;
                for (int k = 0; k < weights.Length; k++)
                {
                    int wk = weights[k];
                    int v = ((64 - wk) * a0 + wk * a1 + 32) >> 6;
                    int d = (v - a) * (v - a);
                    if (d < bd) { bd = d; best = k; }
                }
                outIdx[i] = best;
                total += bd;
            }
            return total;
        }

        bool RefitAlpha(int[] ix, int[] weights, out double a0, out double a1)
        {
            double a = 0, b = 0, c = 0, x = 0, y = 0;
            for (int i = 0; i < 16; i++)
            {
                double w1 = weights[ix[i]] / 64.0, w0 = 1 - w1;
                a += w0 * w0; b += w0 * w1; c += w1 * w1;
                x += w0 * px[i * 4 + 3]; y += w1 * px[i * 4 + 3];
            }
            double det = a * c - b * b;
            a0 = a1 = 0;
            if (Math.Abs(det) < 1e-9) return false;
            a0 = Clamp255((c * x - b * y) / det);
            a1 = Clamp255((a * y - b * x) / det);
            return true;
        }

        static int Quant(double v, int bits)
        {
            int max = (1 << bits) - 1;
            int q = (int)Math.Round(v * max / 255.0);
            return q < 0 ? 0 : q > max ? max : q;
        }

        static int Expand(int q, int bits) => bits >= 8 ? q : (q << (8 - bits)) | (q >> (2 * bits - 8));

        // ------------------------------------------------------------------ shared

        /// <summary>Puts e0/e1 at the ends of the block's principal axis over the first n channels.</summary>
        void FitLine(int n)
        {
            for (int c = 0; c < 4; c++) mean[c] = 0;
            for (int i = 0; i < 16; i++) for (int c = 0; c < n; c++) mean[c] += px[i * 4 + c];
            for (int c = 0; c < n; c++) mean[c] /= 16.0;

            Array.Clear(cov, 0, cov.Length);
            for (int i = 0; i < 16; i++)
            {
                for (int a = 0; a < n; a++)
                {
                    double da = px[i * 4 + a] - mean[a];
                    for (int b = a; b < n; b++) cov[a * 4 + b] += da * (px[i * 4 + b] - mean[b]);
                }
            }
            for (int a = 0; a < n; a++) for (int b = 0; b < a; b++) cov[a * 4 + b] = cov[b * 4 + a];

            int widest = 0;
            for (int i = 1; i < n; i++) if (cov[i * 4 + i] > cov[widest * 4 + widest]) widest = i;
            for (int i = 0; i < n; i++) axis[i] = cov[widest * 4 + i];
            for (int it = 0; it < 8; it++)
            {
                double len = 0;
                for (int r = 0; r < n; r++)
                {
                    double s = 0;
                    for (int k = 0; k < n; k++) s += cov[r * 4 + k] * axis[k];
                    axisTmp[r] = s;
                    len += s * s;
                }
                len = Math.Sqrt(len);
                if (len < 1e-12) break;
                for (int r = 0; r < n; r++) axis[r] = axisTmp[r] / len;
            }
            double l2 = 0;
            for (int i = 0; i < n; i++) l2 += axis[i] * axis[i];
            if (l2 < 1e-12) for (int i = 0; i < n; i++) axis[i] = 1.0 / Math.Sqrt(n);
            else { l2 = Math.Sqrt(l2); for (int i = 0; i < n; i++) axis[i] /= l2; }

            double tmin = double.MaxValue, tmax = double.MinValue;
            for (int i = 0; i < 16; i++)
            {
                double t = 0;
                for (int c = 0; c < n; c++) t += (px[i * 4 + c] - mean[c]) * axis[c];
                if (t < tmin) tmin = t;
                if (t > tmax) tmax = t;
            }
            for (int c = 0; c < n; c++)
            {
                e0[c] = Clamp255(mean[c] + axis[c] * tmin);
                e1[c] = Clamp255(mean[c] + axis[c] * tmax);
            }
        }

        /// <summary>Nearest palette step (between decoded endpoints d0/d1) for each pixel; returns total squared error.</summary>
        long AssignIndices(int channels, int[] weights, byte[] nearest, int[] outIdx)
        {
            int steps = weights.Length;
            long dd = 0;
            for (int c = 0; c < channels; c++) { dir[c] = d1[c] - d0[c]; dd += dir[c] * dir[c]; }

            long total = 0;
            for (int i = 0; i < 16; i++)
            {
                int guess = 0;
                if (dd > 0)
                {
                    long dot = 0;
                    for (int c = 0; c < channels; c++) dot += (px[i * 4 + c] - d0[c]) * dir[c];
                    long t64 = (dot * 64 + dd / 2) / dd;
                    guess = nearest[t64 < 0 ? 0 : t64 > 64 ? 64 : t64];
                }

                int best = guess;
                long bd = long.MaxValue;
                int lo = Math.Max(0, guess - 1), hi = Math.Min(steps - 1, guess + 1);
                for (int k = lo; k <= hi; k++)
                {
                    int wk = weights[k];
                    long d = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int v = ((64 - wk) * d0[c] + wk * d1[c] + 32) >> 6;
                        int diff = v - px[i * 4 + c];
                        d += diff * diff;
                    }
                    if (d < bd) { bd = d; best = k; }
                }
                outIdx[i] = best;
                total += bd;
            }
            return total;
        }

        /// <summary>Least-squares endpoints for fixed indices. False when the indices can't separate them.</summary>
        bool Refit(int[] ix, int[] weights, int channels)
        {
            double a = 0, b = 0, c = 0;
            for (int ch = 0; ch < 4; ch++) { lsX[ch] = 0; lsY[ch] = 0; }
            for (int i = 0; i < 16; i++)
            {
                double w1 = weights[ix[i]] / 64.0, w0 = 1 - w1;
                a += w0 * w0; b += w0 * w1; c += w1 * w1;
                for (int ch = 0; ch < channels; ch++)
                {
                    lsX[ch] += w0 * px[i * 4 + ch];
                    lsY[ch] += w1 * px[i * 4 + ch];
                }
            }
            double det = a * c - b * b;
            if (Math.Abs(det) < 1e-9) return false;
            for (int ch = 0; ch < channels; ch++)
            {
                e0[ch] = Clamp255((c * lsX[ch] - b * lsY[ch]) / det);
                e1[ch] = Clamp255((a * lsY[ch] - b * lsX[ch]) / det);
            }
            return true;
        }

        static double Clamp255(double v) => v < 0 ? 0 : v > 255 ? 255 : v;

        static byte[] NearestStepTable(int[] weights)
        {
            var t = new byte[65];
            for (int v = 0; v <= 64; v++)
            {
                int best = 0;
                for (int k = 1; k < weights.Length; k++)
                    if (Math.Abs(weights[k] - v) < Math.Abs(weights[best] - v)) best = k;
                t[v] = (byte)best;
            }
            return t;
        }

        struct BitWriter
        {
            ulong lo, hi;
            int pos;

            public void Write(int value, int bits)
            {
                for (int i = 0; i < bits; i++, pos++)
                {
                    ulong bit = (ulong)((value >> i) & 1);
                    if (pos < 64) lo |= bit << pos;
                    else hi |= bit << (pos - 64);
                }
            }

            public void CopyTo(byte[] d, int o)
            {
                if (pos != 128) throw new InvalidOperationException($"BC7 block has {pos} bits, expected 128.");
                for (int i = 0; i < 8; i++) { d[o + i] = (byte)(lo >> (8 * i)); d[o + 8 + i] = (byte)(hi >> (8 * i)); }
            }
        }
    }
}
