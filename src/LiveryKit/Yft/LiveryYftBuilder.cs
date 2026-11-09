using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace LiveryKit.Yft
{
    public sealed class LiveryYftResult
    {
        public byte[] YftBytes { get; internal set; }
        public string TextureName { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int MipLevels { get; internal set; }
        public string Format { get; internal set; }
        /// <summary>What the self-check confirmed about the saved file.</summary>
        public List<string> Checks { get; } = new List<string>();
    }

    /// <summary>Makes a new livery .yft from a template by swapping its embedded livery texture.</summary>
    public static class LiveryYftBuilder
    {
        public static LiveryYftResult Build(LiveryTemplate template, uint textureHash, byte[] dds, string newModelName, string newTextureName = null)
        {
            if (string.IsNullOrWhiteSpace(newModelName)) throw new ArgumentException("A model name is needed.", nameof(newModelName));
            newTextureName = string.IsNullOrWhiteSpace(newTextureName) ? newModelName : newTextureName.Trim();

            var yft = template.LoadCopy();
            var frag = yft.Fragment;
            var slots = YftWalker.AllDrawables(frag);

            Texture old = null;
            DrawableSlot owner = null;
            foreach (var s in slots)
            {
                var t = s.Drawable.ShaderGroup?.TextureDictionary?.Lookup(textureHash);
                if (t != null) { old = t; owner = s; break; }
            }
            if (old == null) throw new LiveryException($"The chosen texture isn't embedded in {template.ModelName}.yft.");

            // References to the old texture, counted before we touch anything (for the self-check).
            int refsBefore = slots.Sum(s => YftWalker.TextureParams(s.Drawable).Count(p => p.Texture.NameHash == old.NameHash));

            Texture fresh;
            try { fresh = DDSIO.GetTexture(dds); }
            catch (Exception ex) { throw new LiveryException("The new texture couldn't be read back as DDS: " + ex.Message, ex); }
            if (fresh?.Data?.FullData == null) throw new LiveryException("The new texture couldn't be read back as DDS.");

            // Same identity and flags as the texture it replaces; only size, format, mips, data and name change.
            fresh.Name = newTextureName;
            fresh.NameHash = JenkHash.GenHash(newTextureName.ToLowerInvariant());
            fresh.VFT = old.VFT;
            fresh.Unknown_4h = old.Unknown_4h;
            fresh.Unknown_30h = old.Unknown_30h;
            fresh.Unknown_32h = old.Unknown_32h;
            fresh.UsageData = old.UsageData;
            fresh.ExtraFlags = old.ExtraFlags;

            var dict = owner.Drawable.ShaderGroup.TextureDictionary;
            if (fresh.NameHash != old.NameHash && dict.Lookup(fresh.NameHash) != null)
                throw new LiveryException($"{template.ModelName}.yft already has a texture called '{newTextureName}'.");

            var textures = dict.Textures.data_items.Where(t => !ReferenceEquals(t, old)).ToList();
            textures.Add(fresh);
            dict.BuildFromTextureList(textures);

            foreach (var s in slots)
            {
                foreach (var p in YftWalker.TextureParams(s.Drawable).ToList())
                {
                    if (!ReferenceEquals(p.Texture, old) && p.Texture.NameHash != old.NameHash) continue;
                    if (ReferenceEquals(s, owner) || ReferenceEquals(p.Texture, old))
                    {
                        p.Param.Data = fresh;
                    }
                    else
                    {
                        // Another drawable refers to it by name only; keep that name in step.
                        p.Texture.Name = fresh.Name;
                        p.Texture.NameHash = fresh.NameHash;
                    }
                }
            }

            string oldStem = template.ModelName;
            if (frag.Name != null && frag.Name.EndsWith(oldStem, StringComparison.OrdinalIgnoreCase))
                frag.Name = frag.Name.Substring(0, frag.Name.Length - oldStem.Length) + newModelName;
            yft.Name = newModelName + ".yft";

            byte[] bytes;
            try { bytes = YftIO.Save(yft, template.ResourceVersion); }
            catch (Exception ex) { throw new LiveryException("Saving the new .yft failed: " + ex.Message, ex); }

            var result = new LiveryYftResult
            {
                YftBytes = bytes,
                TextureName = fresh.Name,
                Width = fresh.Width,
                Height = fresh.Height,
                MipLevels = fresh.Levels,
                Format = TemplateTexture.FriendlyFormat(fresh.Format),
            };
            Verify(template, old.NameHash, fresh, refsBefore, bytes, result);
            return result;
        }

        /// <summary>Re-opens the saved bytes and proves the file is the template with only the texture swapped.</summary>
        static void Verify(LiveryTemplate template, uint oldHash, Texture fresh, int refsBefore, byte[] bytes, LiveryYftResult result)
        {
            YftFile back;
            try { back = YftIO.Load(bytes); }
            catch (Exception ex) { throw new LiveryException("Self-check failed: the new .yft doesn't open again: " + ex.Message, ex); }

            if (YftIO.ReadResourceVersion(bytes) != template.ResourceVersion)
                throw new LiveryException("Self-check failed: the resource version changed.");
            result.Checks.Add($"re-opened the saved .yft (resource version {template.ResourceVersion})");

            var slots = YftWalker.AllDrawables(back.Fragment);
            var found = slots.Select(s => s.Drawable.ShaderGroup?.TextureDictionary?.Lookup(fresh.NameHash)).Where(t => t != null).ToList();
            if (found.Count != 1) throw new LiveryException($"Self-check failed: found {found.Count} copies of the new texture.");
            var tex = found[0];
            if (tex.Width != fresh.Width || tex.Height != fresh.Height || tex.Levels != fresh.Levels || tex.Format != fresh.Format)
                throw new LiveryException("Self-check failed: the saved texture's size, format or mip count differs from what was encoded.");
            if (tex.Data?.FullData == null || !tex.Data.FullData.SequenceEqual(fresh.Data.FullData))
                throw new LiveryException("Self-check failed: the saved texture data differs from what was encoded.");
            result.Checks.Add($"embedded texture '{tex.Name}' is {tex.Width}x{tex.Height} {TemplateTexture.FriendlyFormat(tex.Format)} with {tex.Levels} mipmaps, data identical ({tex.Data.FullData.Length:N0} bytes)");

            if (fresh.NameHash != oldHash && slots.Any(s => s.Drawable.ShaderGroup?.TextureDictionary?.Lookup(oldHash) != null))
                throw new LiveryException("Self-check failed: the old livery texture is still in the file.");

            int refsAfter = slots.Sum(s => YftWalker.TextureParams(s.Drawable).Count(p => p.Texture.NameHash == fresh.NameHash));
            if (refsAfter != refsBefore)
                throw new LiveryException($"Self-check failed: {refsBefore} shader slot(s) used the old livery but {refsAfter} use the new one.");
            // Legacy files link the shader straight to the embedded texture; Enhanced (gen9) files refer to it by name,
            // which the count above already confirmed.
            bool embeddedRef = slots.Any(s => YftWalker.TextureParams(s.Drawable).Any(p => p.Texture.NameHash == fresh.NameHash && p.Texture is Texture));
            if (refsBefore > 0 && !embeddedRef && template.ResourceVersion != YftIO.Gen9Version)
                throw new LiveryException("Self-check failed: the shader doesn't point at the embedded texture.");
            result.Checks.Add($"{refsAfter} shader slot(s) now use the new texture");

            string before = Signature(template.LoadCopy().Fragment, oldHash, oldHash);
            string after = Signature(back.Fragment, fresh.NameHash, oldHash);
            if (before != after)
                throw new LiveryException("Self-check failed: something other than the livery texture changed:\n" + FirstDifference(before, after));
            result.Checks.Add("model, bones, shaders and every other texture are identical to the original");
        }

        /// <summary>Everything about the fragment except the swapped livery texture.</summary>
        static string Signature(FragType f, uint liveryHash, uint oldHash)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"glass:{f.GlassWindowsCount} physics:{f.PhysicsLODGroup != null} array:{f.DrawableArrayCount} sphere:{f.BoundingSphereRadius:R}");
            foreach (var slot in YftWalker.AllDrawables(f))
            {
                var d = slot.Drawable;
                sb.AppendLine($"[{slot.Label}] bones:{d.Skeleton?.BonesCount ?? 0} bbox:{d.BoundingBoxMin}{d.BoundingBoxMax}");
                foreach (var m in d.AllModels ?? new DrawableModel[0])
                {
                    sb.Append(" model");
                    foreach (var g in m?.Geometries ?? new DrawableGeometry[0])
                        sb.Append($" g(v{g.VerticesCount} i{g.IndicesCount} s{g.ShaderID} stride{g.VertexStride} vd{Hash(g.VertexData?.VertexBytes)} id{Hash(g.IndexBuffer?.Indices)})");
                    sb.AppendLine();
                }
                var shaders = d.ShaderGroup?.Shaders?.data_items ?? new ShaderFX[0];
                foreach (var s in shaders)
                {
                    sb.Append($" shader {s.Name} bucket:{s.RenderBucket}");
                    var pl = s.ParametersList;
                    if (pl?.Parameters != null)
                    {
                        for (int i = 0; i < pl.Parameters.Length; i++)
                        {
                            var p = pl.Parameters[i];
                            sb.Append($" {pl.Hashes[i]}=");
                            if (p.Data is TextureBase tb) sb.Append(tb.NameHash == liveryHash || tb.NameHash == oldHash ? "<livery>" : tb.Name);
                            else if (p.Data is SharpDX.Vector4 v) sb.Append(v.ToString());
                            else if (p.Data is SharpDX.Vector4[] arr) sb.Append(string.Join(";", arr.Select(x => x.ToString())));
                            else sb.Append(p.Data?.ToString() ?? "null");
                        }
                    }
                    sb.AppendLine();
                }
                var texs = d.ShaderGroup?.TextureDictionary?.Textures?.data_items ?? new Texture[0];
                foreach (var t in texs.Where(t => t.NameHash != liveryHash && t.NameHash != oldHash).OrderBy(t => t.NameHash))
                    sb.AppendLine($" tex {t.Name} {t.Width}x{t.Height} {t.Format} {t.Levels} {Hash(t.Data?.FullData)}");
            }
            return sb.ToString();
        }

        static string Hash(byte[] data)
        {
            if (data == null) return "-";
            ulong h = 14695981039346656037UL;
            foreach (var b in data) { h ^= b; h *= 1099511628211UL; }
            return h.ToString("X16");
        }

        static string Hash(ushort[] data)
        {
            if (data == null) return "-";
            ulong h = 14695981039346656037UL;
            foreach (var v in data) { h ^= v; h *= 1099511628211UL; }
            return h.ToString("X16");
        }

        static string FirstDifference(string a, string b)
        {
            var la = a.Split('\n');
            var lb = b.Split('\n');
            for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            {
                string x = i < la.Length ? la[i] : "", y = i < lb.Length ? lb[i] : "";
                if (x != y) return $"before: {x.Trim()}\nafter:  {y.Trim()}";
            }
            return "(no line differs)";
        }
    }
}
