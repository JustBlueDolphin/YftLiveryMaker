namespace LiveryKit.Textures
{
    /// <summary>Full BC7 block decoder (all 8 modes), per the D3D11 BC7 specification.</summary>
    internal static class Bc7Decoder
    {
        //                          mode:  0  1  2  3  4  5  6  7
        static readonly int[] NS  = {      3, 2, 3, 2, 1, 1, 1, 2 };
        static readonly int[] PB  = {      4, 6, 6, 6, 0, 0, 0, 6 };
        static readonly int[] RB  = {      0, 0, 0, 0, 2, 2, 0, 0 };
        static readonly int[] ISB = {      0, 0, 0, 0, 1, 0, 0, 0 };
        static readonly int[] CB  = {      4, 6, 5, 7, 5, 7, 7, 5 };
        static readonly int[] AB  = {      0, 0, 0, 0, 6, 8, 7, 5 };
        static readonly int[] EPB = {      1, 0, 0, 1, 0, 0, 1, 1 };
        static readonly int[] SPB = {      0, 1, 0, 0, 0, 0, 0, 0 };
        static readonly int[] IB  = {      3, 3, 2, 2, 2, 2, 4, 2 };
        static readonly int[] IB2 = {      0, 0, 0, 0, 3, 2, 0, 0 };

        struct Bits
        {
            ulong lo, hi;
            int pos;

            public Bits(byte[] s, int p)
            {
                lo = 0; hi = 0; pos = 0;
                for (int i = 0; i < 8; i++)
                {
                    lo |= (ulong)s[p + i] << (8 * i);
                    hi |= (ulong)s[p + 8 + i] << (8 * i);
                }
            }

            public int Read(int n)
            {
                if (n == 0) return 0;
                int v;
                if (pos + n <= 64) v = (int)((lo >> pos) & ((1UL << n) - 1));
                else if (pos >= 64) v = (int)((hi >> (pos - 64)) & ((1UL << n) - 1));
                else
                {
                    int lowBits = 64 - pos;
                    ulong part = lo >> pos;
                    part |= hi << lowBits;
                    v = (int)(part & ((1UL << n) - 1));
                }
                pos += n;
                return v;
            }
        }

        public static void DecodeBlock(byte[] s, int p, byte[] o)
        {
            int mode = 0;
            byte first = s[p];
            while (mode < 8 && (first & (1 << mode)) == 0) mode++;
            if (mode == 8)
            {
                for (int i = 0; i < 64; i++) o[i] = 0;
                return;
            }

            var bits = new Bits(s, p);
            bits.Read(mode + 1);

            int ns = NS[mode];
            int partition = bits.Read(PB[mode]);
            int rotation = bits.Read(RB[mode]);
            int indexSel = bits.Read(ISB[mode]);

            int ne = ns * 2;
            var ep = new int[6 * 4];   // endpoint e, channel c => ep[e * 4 + c]
            for (int c = 0; c < 3; c++)
                for (int e = 0; e < ne; e++) ep[e * 4 + c] = bits.Read(CB[mode]);
            if (AB[mode] > 0)
                for (int e = 0; e < ne; e++) ep[e * 4 + 3] = bits.Read(AB[mode]);

            var pbit = new int[6];
            bool hasP = EPB[mode] != 0 || SPB[mode] != 0;
            if (EPB[mode] != 0)
                for (int e = 0; e < ne; e++) pbit[e] = bits.Read(1);
            else if (SPB[mode] != 0)
                for (int sub = 0; sub < ns; sub++) { int b = bits.Read(1); pbit[sub * 2] = b; pbit[sub * 2 + 1] = b; }

            for (int e = 0; e < ne; e++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int nb = c < 3 ? CB[mode] : AB[mode];
                    if (nb == 0) { ep[e * 4 + c] = 255; continue; }
                    int v = ep[e * 4 + c];
                    if (hasP) { v = (v << 1) | pbit[e]; nb++; }
                    v <<= 8 - nb;
                    v |= v >> nb;
                    ep[e * 4 + c] = v;
                }
            }

            int a1 = 0, a2 = 0;
            if (ns == 2) a1 = Bc7Tables.Anchor2[partition];
            else if (ns == 3) { a1 = Bc7Tables.Anchor3a[partition]; a2 = Bc7Tables.Anchor3b[partition]; }

            var idx = new int[16];
            int ib = IB[mode];
            for (int i = 0; i < 16; i++)
            {
                bool anchor = i == 0 || (ns >= 2 && i == a1) || (ns == 3 && i == a2);
                idx[i] = bits.Read(anchor ? ib - 1 : ib);
            }

            int ib2 = IB2[mode];
            int[] idx2 = null;
            if (ib2 > 0)
            {
                idx2 = new int[16];
                for (int i = 0; i < 16; i++) idx2[i] = bits.Read(i == 0 ? ib2 - 1 : ib2);
            }

            for (int i = 0; i < 16; i++)
            {
                int sub = ns == 1 ? 0 : ns == 2 ? Bc7Tables.Partition2[partition * 16 + i] : Bc7Tables.Partition3[partition * 16 + i];
                int e0 = sub * 2 * 4, e1 = e0 + 4;

                int wc, wa;
                if (idx2 == null)
                {
                    wc = wa = Weight(ib, idx[i]);
                }
                else if (indexSel == 0)
                {
                    wc = Weight(ib, idx[i]);
                    wa = Weight(ib2, idx2[i]);
                }
                else
                {
                    wc = Weight(ib2, idx2[i]);
                    wa = Weight(ib, idx[i]);
                }

                int r = Lerp(ep[e0], ep[e1], wc);
                int g = Lerp(ep[e0 + 1], ep[e1 + 1], wc);
                int b = Lerp(ep[e0 + 2], ep[e1 + 2], wc);
                int a = Lerp(ep[e0 + 3], ep[e1 + 3], wa);

                switch (rotation)
                {
                    case 1: { int t = a; a = r; r = t; break; }
                    case 2: { int t = a; a = g; g = t; break; }
                    case 3: { int t = a; a = b; b = t; break; }
                }

                o[i * 4] = (byte)r; o[i * 4 + 1] = (byte)g; o[i * 4 + 2] = (byte)b; o[i * 4 + 3] = (byte)a;
            }
        }

        static int Weight(int bitsCount, int index)
        {
            switch (bitsCount)
            {
                case 2: return Bc7Tables.Weights2[index];
                case 3: return Bc7Tables.Weights3[index];
                default: return Bc7Tables.Weights4[index];
            }
        }

        static int Lerp(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;
    }
}
