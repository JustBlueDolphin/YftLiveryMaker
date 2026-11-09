using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWalker.GameFiles;
using LiveryKit.Imaging;
using LiveryKit.Textures;

namespace LiveryKit.Yft
{
    /// <summary>A texture embedded in a template livery YFT.</summary>
    public sealed class TemplateTexture
    {
        public string Name { get; internal set; }
        public uint NameHash { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int MipLevels { get; internal set; }
        public TextureFormat Format { get; internal set; }
        public string DrawableLabel { get; internal set; }
        /// <summary>Where shaders use it, e.g. "vehicle_paint4 › diffusesampler2".</summary>
        public List<string> UsedBy { get; } = new List<string>();
        public bool InLiverySlot { get; internal set; }
        internal int Score { get; set; }

        public string FormatName => FriendlyFormat(Format);

        public static string FriendlyFormat(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.D3DFMT_DXT1: return "DXT1";
                case TextureFormat.D3DFMT_DXT3: return "DXT3";
                case TextureFormat.D3DFMT_DXT5: return "DXT5";
                case TextureFormat.D3DFMT_BC7: return "BC7";
                case TextureFormat.D3DFMT_A8R8G8B8: return "A8R8G8B8";
                case TextureFormat.D3DFMT_A8B8G8R8: return "A8B8G8R8";
                default: return f.ToString().Replace("D3DFMT_", "");
            }
        }

        public override string ToString() => $"{Name}  ({Width}x{Height} {FormatName}, {MipLevels} mip{(MipLevels == 1 ? "" : "s")})";
    }

    /// <summary>
    /// An existing livery .yft of the car. New liveries are copies of it with only the
    /// livery texture swapped, so the model, bones, shaders and flags stay exactly as the car expects.
    /// </summary>
    public sealed class LiveryTemplate
    {
        LiveryTemplate() { }

        public string FilePath { get; private set; }
        public string ModelName => Path.GetFileNameWithoutExtension(FilePath);
        public byte[] FileBytes { get; private set; }
        public int ResourceVersion { get; private set; }
        public bool IsGen9 => ResourceVersion == YftIO.Gen9Version;
        public string FragmentName { get; private set; }
        public IReadOnlyList<TemplateTexture> Textures { get; private set; }

        /// <summary>The texture that is most likely the livery (null if the YFT embeds none).</summary>
        public TemplateTexture Suggested { get; private set; }

        public static LiveryTemplate Load(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { throw new LiveryException($"Couldn't read {Path.GetFileName(path)}: {ex.Message}", ex); }
            return Load(path, data);
        }

        public static LiveryTemplate Load(string path, byte[] data)
        {
            string file = Path.GetFileName(path);
            if (data.Length >= 4 && data[0] == 'F' && data[1] == 'X' && data[2] == 'A' && data[3] == 'P')
                throw new LiveryException($"{file} is locked by FiveM Asset Escrow (encrypted), so no tool can open it. Pick one of the car's other livery .yft files that isn't escrowed.");

            YftFile yft;
            try { yft = YftIO.Load(data, file); }
            catch (Exception ex) { throw new LiveryException($"{file} couldn't be opened as a GTA V .yft: {ex.Message}", ex); }

            var t = new LiveryTemplate
            {
                FilePath = Path.GetFullPath(path),
                FileBytes = data,
                ResourceVersion = YftIO.ReadResourceVersion(data),
                FragmentName = yft.Fragment.Name,
            };

            var list = new List<TemplateTexture>();
            foreach (var slot in YftWalker.AllDrawables(yft.Fragment))
            {
                var texs = slot.Drawable.ShaderGroup?.TextureDictionary?.Textures?.data_items;
                if (texs == null) continue;
                foreach (var tex in texs)
                {
                    if (tex == null || list.Any(x => x.NameHash == tex.NameHash)) continue;
                    var info = new TemplateTexture
                    {
                        Name = tex.Name,
                        NameHash = tex.NameHash,
                        Width = tex.Width,
                        Height = tex.Height,
                        MipLevels = tex.Levels,
                        Format = tex.Format,
                        DrawableLabel = slot.Label,
                    };
                    foreach (var s in YftWalker.AllDrawables(yft.Fragment))
                    {
                        foreach (var p in YftWalker.TextureParams(s.Drawable))
                        {
                            if (p.Texture.NameHash != tex.NameHash) continue;
                            string use = $"{p.Shader.Name} › {p.ParamName}";
                            if (!info.UsedBy.Contains(use)) info.UsedBy.Add(use);
                            if (p.ParamName.Equals("diffusesampler2", StringComparison.OrdinalIgnoreCase)) info.InLiverySlot = true;
                        }
                    }
                    info.Score = ScoreTexture(info);
                    list.Add(info);
                }
            }

            t.Textures = list;
            t.Suggested = list.OrderByDescending(x => x.Score).FirstOrDefault();
            return t;
        }

        static int ScoreTexture(TemplateTexture t)
        {
            int score = 0;
            if (t.InLiverySlot) score += 1000;
            if (t.UsedBy.Count > 0) score += 300;
            string n = (t.Name ?? "").ToLowerInvariant();
            if (n.Contains("liv") || n.Contains("sign")) score += 200;
            if (n.EndsWith("_n") || n.EndsWith("_nrm") || n.EndsWith("_normal") || n.EndsWith("_s") || n.EndsWith("_spec") || n.Contains("dirt"))
                score -= 500;
            score += (int)Math.Log(Math.Max(1, t.Width * t.Height), 2);
            return score;
        }

        /// <summary>Fresh, independent copy of the template's object graph.</summary>
        internal YftFile LoadCopy() => YftIO.Load(FileBytes, Path.GetFileName(FilePath));

        /// <summary>Decodes one embedded texture for previews, picking the mip closest to maxSize.</summary>
        public RgbaImage DecodePreview(TemplateTexture which, int maxSize = 256)
        {
            var yft = LoadCopy();
            foreach (var slot in YftWalker.AllDrawables(yft.Fragment))
            {
                var tex = slot.Drawable.ShaderGroup?.TextureDictionary?.Lookup(which.NameHash);
                if (tex?.Data?.FullData == null) continue;
                return DecodeTexture(tex, maxSize);
            }
            return null;
        }

        internal static RgbaImage DecodeTexture(Texture tex, int maxSize)
        {
            DdsFormat fmt;
            switch (tex.Format)
            {
                case TextureFormat.D3DFMT_DXT1: fmt = DdsFormat.DXT1; break;
                case TextureFormat.D3DFMT_DXT3: fmt = DdsFormat.DXT3; break;
                case TextureFormat.D3DFMT_DXT5: fmt = DdsFormat.DXT5; break;
                case TextureFormat.D3DFMT_BC7: fmt = DdsFormat.BC7; break;
                case TextureFormat.D3DFMT_A8R8G8B8: fmt = DdsFormat.A8R8G8B8; break;
                case TextureFormat.D3DFMT_A8B8G8R8: fmt = DdsFormat.A8B8G8R8; break;
                default: return null;
            }

            // Walk the mip chain the way GTA stores it (each level a quarter of the last).
            int w = tex.Width, h = tex.Height, offset = 0, level = 0;
            var data = tex.Data.FullData;
            while (level < tex.Levels - 1 && Math.Max(w, h) > maxSize)
            {
                int size = DdsFile.LevelSize(fmt, w, h);
                if (offset + size >= data.Length) break;
                offset += size;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                level++;
            }
            return DdsFile.DecodeLevel(data, offset, w, h, fmt);
        }
    }
}
