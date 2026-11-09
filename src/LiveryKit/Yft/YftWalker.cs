using System.Collections.Generic;
using CodeWalker.GameFiles;

namespace LiveryKit.Yft
{
    /// <summary>A drawable found somewhere inside a fragment, with a readable label of where it lives.</summary>
    public sealed class DrawableSlot
    {
        public DrawableSlot(string label, DrawableBase drawable)
        {
            Label = label;
            Drawable = drawable;
        }

        public string Label { get; }
        public DrawableBase Drawable { get; }
    }

    /// <summary>Enumerates every drawable a YFT can carry its own shaders and embedded textures in.</summary>
    public static class YftWalker
    {
        public static List<DrawableSlot> AllDrawables(FragType frag)
        {
            var list = new List<DrawableSlot>();
            if (frag == null) return list;

            var seen = new HashSet<DrawableBase>();
            void Add(string label, DrawableBase d)
            {
                if (d != null && seen.Add(d)) list.Add(new DrawableSlot(label, d));
            }

            Add("Drawable", frag.Drawable);
            Add("DrawableCloth", frag.DrawableCloth);

            var array = frag.DrawableArray?.data_items;
            if (array != null)
            {
                for (int i = 0; i < array.Length; i++) Add($"DrawableArray[{i}]", array[i]);
            }

            var lods = new[] { frag.PhysicsLODGroup?.PhysicsLOD1, frag.PhysicsLODGroup?.PhysicsLOD2, frag.PhysicsLODGroup?.PhysicsLOD3 };
            for (int l = 0; l < lods.Length; l++)
            {
                var children = lods[l]?.Children?.data_items;
                if (children == null) continue;
                for (int c = 0; c < children.Length; c++)
                {
                    Add($"PhysicsLOD{l + 1}.Child[{c}].Drawable1", children[c]?.Drawable1);
                    Add($"PhysicsLOD{l + 1}.Child[{c}].Drawable2", children[c]?.Drawable2);
                }
            }

            return list;
        }

        /// <summary>Every shader texture parameter in a drawable: (shader, parameter name, texture reference).</summary>
        public static IEnumerable<(ShaderFX Shader, string ParamName, ShaderParameter Param, TextureBase Texture)> TextureParams(DrawableBase d)
        {
            var shaders = d?.ShaderGroup?.Shaders?.data_items;
            if (shaders == null) yield break;

            foreach (var s in shaders)
            {
                var plist = s?.ParametersList;
                if (plist?.Parameters == null) continue;
                for (int i = 0; i < plist.Parameters.Length; i++)
                {
                    var p = plist.Parameters[i];
                    if (p?.Data is TextureBase tex)
                    {
                        string pname = (plist.Hashes != null && i < plist.Hashes.Length) ? plist.Hashes[i].ToString() : $"param{i}";
                        yield return (s, pname, p, tex);
                    }
                }
            }
        }
    }
}
