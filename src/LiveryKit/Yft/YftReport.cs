using System.IO;
using System.Linq;
using System.Text;
using CodeWalker.GameFiles;

namespace LiveryKit.Yft
{
    /// <summary>Human-readable dump of a YFT: drawables, models, shaders, texture params, embedded textures.</summary>
    public static class YftReport
    {
        public static string Describe(string path)
        {
            var data = File.ReadAllBytes(path);
            var yft = YftIO.Load(data, Path.GetFileName(path));
            return Describe(yft, YftIO.ReadResourceVersion(data), data.Length);
        }

        public static string Describe(YftFile yft, int version, long fileSize)
        {
            var sb = new StringBuilder();
            var f = yft.Fragment;
            sb.AppendLine($"Fragment '{f.Name}'  version {version}{(version == YftIO.Gen9Version ? " (gen9 / Enhanced)" : " (legacy / FiveM)")}  file {fileSize:N0} bytes");
            sb.AppendLine($"  bones: {f.Drawable?.Skeleton?.BonesCount ?? 0}  glass windows: {f.GlassWindowsCount}  physics LOD group: {(f.PhysicsLODGroup != null ? "yes" : "no")}  drawable array: {f.DrawableArrayCount}");

            foreach (var slot in YftWalker.AllDrawables(f))
            {
                var d = slot.Drawable;
                var models = d.AllModels ?? new DrawableModel[0];
                int geoms = models.Sum(m => m?.Geometries?.Length ?? 0);
                sb.AppendLine($"  [{slot.Label}] models: {models.Length}  geometries: {geoms}");

                var shaders = d.ShaderGroup?.Shaders?.data_items;
                if (shaders != null)
                {
                    for (int i = 0; i < shaders.Length; i++)
                    {
                        var s = shaders[i];
                        sb.AppendLine($"    shader {i}: {s.Name} ({s.FileName})");
                        var plist = s.ParametersList;
                        if (plist?.Parameters == null) continue;
                        for (int p = 0; p < plist.Parameters.Length; p++)
                        {
                            var prm = plist.Parameters[p];
                            if (prm.Data is TextureBase t)
                            {
                                string kind = t is Texture ? "EMBEDDED" : "external";
                                sb.AppendLine($"      {plist.Hashes[p]} -> '{t.Name}' ({kind})");
                            }
                        }
                    }
                }

                var texs = d.ShaderGroup?.TextureDictionary?.Textures?.data_items;
                if (texs != null)
                {
                    foreach (var t in texs)
                    {
                        sb.AppendLine($"    embedded texture '{t.Name}' {t.Width}x{t.Height} {t.Format} mips:{t.Levels} stride:{t.Stride} usage:{t.Usage} flags:{t.UsageFlags} extra:{t.ExtraFlags} data:{t.Data?.FullData?.Length ?? 0:N0}");
                        sb.AppendLine($"      raw: vft:{t.VFT:X8} u4:{t.Unknown_4h} u30:{t.Unknown_30h} u32:{t.Unknown_32h} depth:{t.Depth} u5C:{t.Unknown_5Ch} u5E:{t.Unknown_5Eh} u60:{t.Unknown_60h} u64:{t.Unknown_64h} u68:{t.Unknown_68h} u6C:{t.Unknown_6Ch} u78:{t.Unknown_78h} u7C:{t.Unknown_7Ch} u80:{t.Unknown_80h} u84:{t.Unknown_84h} u88:{t.Unknown_88h} u8C:{t.Unknown_8Ch}");
                    }
                }
            }
            return sb.ToString();
        }
    }
}
