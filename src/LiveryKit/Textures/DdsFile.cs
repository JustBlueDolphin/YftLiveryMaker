using System;
using System.Collections.Generic;
using System.IO;
using LiveryKit.Imaging;

namespace LiveryKit.Textures
{
    public enum DdsFormat
    {
        DXT1,
        DXT3,
        DXT5,
        BC7,
        /// <summary>32-bit uncompressed, B,G,R,A bytes in memory (D3DFMT_A8R8G8B8).</summary>
        A8R8G8B8,
        /// <summary>32-bit uncompressed, R,G,B,A bytes in memory (D3DFMT_A8B8G8R8).</summary>
        A8B8G8R8,
        /// <summary>32-bit uncompressed, B,G,R,x (alpha ignored).</summary>
        X8R8G8B8,
        /// <summary>24-bit uncompressed, B,G,R.</summary>
        R8G8B8,
    }

    public sealed class DdsInfo
    {
        public int Width, Height, MipCount, DataOffset;
        public DdsFormat Format;
    }

    /// <summary>Minimal DDS reader/writer for 2D textures.</summary>
    public static class DdsFile
    {
        const uint Magic = 0x20534444; // "DDS "
        const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PITCH = 0x8, DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000, DDSD_LINEARSIZE = 0x80000;
        const uint DDPF_ALPHAPIXELS = 0x1, DDPF_FOURCC = 0x4, DDPF_RGB = 0x40;
        const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;
        const uint DXGI_BC1 = 71, DXGI_BC2 = 74, DXGI_BC3 = 77, DXGI_BC7 = 98, DXGI_BC7_SRGB = 99, DXGI_BC1_SRGB = 72, DXGI_BC3_SRGB = 78, DXGI_BC2_SRGB = 75;
        const uint DXGI_R8G8B8A8 = 28, DXGI_R8G8B8A8_SRGB = 29, DXGI_B8G8R8A8 = 87, DXGI_B8G8R8A8_SRGB = 91, DXGI_B8G8R8X8 = 88;

        public static bool IsDds(byte[] d) => d.Length >= 128 && BitConverter.ToUInt32(d, 0) == Magic;

        public static bool IsBlockCompressed(DdsFormat f) => f == DdsFormat.DXT1 || f == DdsFormat.DXT3 || f == DdsFormat.DXT5 || f == DdsFormat.BC7;

        public static int BytesPerBlock(DdsFormat f) => f == DdsFormat.DXT1 ? 8 : 16;

        /// <summary>Byte size of one mip level as stored in DDS (blocks are rounded up to 4x4).</summary>
        public static int LevelSize(DdsFormat f, int w, int h)
        {
            if (IsBlockCompressed(f)) return Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BytesPerBlock(f);
            return w * h * BytesPerPixel(f);
        }

        static int BytesPerPixel(DdsFormat f) => f == DdsFormat.R8G8B8 ? 3 : 4;

        public static byte[] Write(int width, int height, DdsFormat format, IReadOnlyList<byte[]> levels)
        {
            if (format == DdsFormat.X8R8G8B8 || format == DdsFormat.R8G8B8 || format == DdsFormat.DXT3)
                throw new NotSupportedException($"Writing {format} isn't supported.");

            bool bc = IsBlockCompressed(format);
            bool dx10 = format == DdsFormat.BC7;
            long total = 4 + 124 + (dx10 ? 20 : 0);
            foreach (var l in levels) total += l.Length;

            using (var ms = new MemoryStream((int)total))
            using (var w = new BinaryWriter(ms))
            {
                uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT;
                if (levels.Count > 1) flags |= DDSD_MIPMAPCOUNT;
                flags |= bc ? DDSD_LINEARSIZE : DDSD_PITCH;

                w.Write(Magic);
                w.Write(124u);
                w.Write(flags);
                w.Write((uint)height);
                w.Write((uint)width);
                w.Write((uint)(bc ? LevelSize(format, width, height) : width * 4));
                w.Write(0u);                     // depth
                w.Write((uint)levels.Count);     // mip count (includes the top level)
                for (int i = 0; i < 11; i++) w.Write(0u);

                // DDS_PIXELFORMAT
                w.Write(32u);
                switch (format)
                {
                    case DdsFormat.DXT1: w.Write(DDPF_FOURCC); w.Write(FourCC("DXT1")); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); break;
                    case DdsFormat.DXT5: w.Write(DDPF_FOURCC); w.Write(FourCC("DXT5")); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); break;
                    case DdsFormat.BC7: w.Write(DDPF_FOURCC); w.Write(FourCC("DX10")); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); break;
                    case DdsFormat.A8R8G8B8: w.Write(DDPF_RGB | DDPF_ALPHAPIXELS); w.Write(0u); w.Write(32u); w.Write(0x00FF0000u); w.Write(0x0000FF00u); w.Write(0x000000FFu); w.Write(0xFF000000u); break;
                    case DdsFormat.A8B8G8R8: w.Write(DDPF_RGB | DDPF_ALPHAPIXELS); w.Write(0u); w.Write(32u); w.Write(0x000000FFu); w.Write(0x0000FF00u); w.Write(0x00FF0000u); w.Write(0xFF000000u); break;
                }

                uint caps = DDSCAPS_TEXTURE;
                if (levels.Count > 1) caps |= DDSCAPS_COMPLEX | DDSCAPS_MIPMAP;
                w.Write(caps);
                w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);

                if (dx10)
                {
                    w.Write(DXGI_BC7);
                    w.Write(3u);   // D3D10_RESOURCE_DIMENSION_TEXTURE2D
                    w.Write(0u);   // misc flags
                    w.Write(1u);   // array size
                    w.Write(1u);   // alpha mode: straight
                }

                foreach (var l in levels) w.Write(l);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static DdsInfo ReadHeader(byte[] d)
        {
            if (!IsDds(d)) throw new InvalidDataException("Not a DDS file.");
            uint flags = BitConverter.ToUInt32(d, 8);
            int height = (int)BitConverter.ToUInt32(d, 12);
            int width = (int)BitConverter.ToUInt32(d, 16);
            int mips = (int)BitConverter.ToUInt32(d, 28);
            if ((flags & DDSD_MIPMAPCOUNT) == 0 || mips == 0) mips = 1;

            uint pfFlags = BitConverter.ToUInt32(d, 80);
            uint fourCC = BitConverter.ToUInt32(d, 84);
            uint bitCount = BitConverter.ToUInt32(d, 88);
            uint rMask = BitConverter.ToUInt32(d, 92), gMask = BitConverter.ToUInt32(d, 96), bMask = BitConverter.ToUInt32(d, 100), aMask = BitConverter.ToUInt32(d, 104);

            var info = new DdsInfo { Width = width, Height = height, MipCount = mips, DataOffset = 128 };

            if ((pfFlags & DDPF_FOURCC) != 0)
            {
                if (fourCC == FourCC("DXT1")) info.Format = DdsFormat.DXT1;
                else if (fourCC == FourCC("DXT2") || fourCC == FourCC("DXT3")) info.Format = DdsFormat.DXT3;
                else if (fourCC == FourCC("DXT4") || fourCC == FourCC("DXT5")) info.Format = DdsFormat.DXT5;
                else if (fourCC == FourCC("DX10"))
                {
                    if (d.Length < 148) throw new InvalidDataException("DDS DX10 header is truncated.");
                    uint dxgi = BitConverter.ToUInt32(d, 128);
                    info.DataOffset = 148;
                    switch (dxgi)
                    {
                        case DXGI_BC1: case DXGI_BC1_SRGB: info.Format = DdsFormat.DXT1; break;
                        case DXGI_BC2: case DXGI_BC2_SRGB: info.Format = DdsFormat.DXT3; break;
                        case DXGI_BC3: case DXGI_BC3_SRGB: info.Format = DdsFormat.DXT5; break;
                        case DXGI_BC7: case DXGI_BC7_SRGB: info.Format = DdsFormat.BC7; break;
                        case DXGI_R8G8B8A8: case DXGI_R8G8B8A8_SRGB: info.Format = DdsFormat.A8B8G8R8; break;
                        case DXGI_B8G8R8A8: case DXGI_B8G8R8A8_SRGB: info.Format = DdsFormat.A8R8G8B8; break;
                        case DXGI_B8G8R8X8: info.Format = DdsFormat.X8R8G8B8; break;
                        default: throw new InvalidDataException($"DDS DXGI format {dxgi} isn't supported. Save it as DXT5/BC3, BC7 or 32-bit RGBA, or use a PNG.");
                    }
                }
                else
                {
                    throw new InvalidDataException($"DDS format '{FourCCText(fourCC)}' isn't supported. Save it as DXT1, DXT5, BC7 or 32-bit RGBA, or use a PNG.");
                }
            }
            else if ((pfFlags & DDPF_RGB) != 0 && bitCount == 32)
            {
                bool hasAlpha = (pfFlags & DDPF_ALPHAPIXELS) != 0 && aMask == 0xFF000000u;
                if (rMask == 0x00FF0000u && gMask == 0x0000FF00u && bMask == 0x000000FFu) info.Format = hasAlpha ? DdsFormat.A8R8G8B8 : DdsFormat.X8R8G8B8;
                else if (rMask == 0x000000FFu && gMask == 0x0000FF00u && bMask == 0x00FF0000u && hasAlpha) info.Format = DdsFormat.A8B8G8R8;
                else throw new InvalidDataException("This 32-bit DDS channel layout isn't supported. Use a PNG instead.");
            }
            else if ((pfFlags & DDPF_RGB) != 0 && bitCount == 24 && rMask == 0x00FF0000u)
            {
                info.Format = DdsFormat.R8G8B8;
            }
            else
            {
                throw new InvalidDataException("This DDS pixel format isn't supported. Save it as DXT1, DXT5, BC7 or 32-bit RGBA, or use a PNG.");
            }
            return info;
        }

        /// <summary>Decodes the top mip level to RGBA.</summary>
        public static RgbaImage DecodeTopLevel(byte[] d)
        {
            var info = ReadHeader(d);
            return DecodeLevel(d, info.DataOffset, info.Width, info.Height, info.Format);
        }

        public static RgbaImage DecodeLevel(byte[] d, int offset, int width, int height, DdsFormat format)
        {
            if (width <= 0 || height <= 0) throw new InvalidDataException("DDS has no size.");
            int size = LevelSize(format, width, height);
            if (offset + (long)size > d.Length) throw new InvalidDataException("DDS pixel data is truncated.");

            switch (format)
            {
                case DdsFormat.DXT1: return new RgbaImage(width, height, BcDecoder.DecodeSurface(d, offset, width, height, BcKind.BC1));
                case DdsFormat.DXT3: return new RgbaImage(width, height, BcDecoder.DecodeSurface(d, offset, width, height, BcKind.BC2));
                case DdsFormat.DXT5: return new RgbaImage(width, height, BcDecoder.DecodeSurface(d, offset, width, height, BcKind.BC3));
                case DdsFormat.BC7: return new RgbaImage(width, height, BcDecoder.DecodeSurface(d, offset, width, height, BcKind.BC7));
            }

            var px = new byte[width * height * 4];
            int n = width * height;
            for (int i = 0; i < n; i++)
            {
                int s = offset + i * BytesPerPixel(format), o = i * 4;
                switch (format)
                {
                    case DdsFormat.A8R8G8B8: px[o] = d[s + 2]; px[o + 1] = d[s + 1]; px[o + 2] = d[s]; px[o + 3] = d[s + 3]; break;
                    case DdsFormat.X8R8G8B8: px[o] = d[s + 2]; px[o + 1] = d[s + 1]; px[o + 2] = d[s]; px[o + 3] = 255; break;
                    case DdsFormat.A8B8G8R8: px[o] = d[s]; px[o + 1] = d[s + 1]; px[o + 2] = d[s + 2]; px[o + 3] = d[s + 3]; break;
                    case DdsFormat.R8G8B8: px[o] = d[s + 2]; px[o + 1] = d[s + 1]; px[o + 2] = d[s]; px[o + 3] = 255; break;
                }
            }
            return new RgbaImage(width, height, px);
        }

        static uint FourCC(string s) => (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));

        static string FourCCText(uint v)
        {
            var c = new char[4];
            for (int i = 0; i < 4; i++)
            {
                char ch = (char)((v >> (8 * i)) & 0xFF);
                c[i] = ch >= 32 && ch < 127 ? ch : '?';
            }
            return new string(c);
        }
    }
}
