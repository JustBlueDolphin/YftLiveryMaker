using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiveryKit.Meta
{
    public sealed class CarcolsLivery
    {
        public string ModelName { get; internal set; }
        public string Label { get; internal set; }
    }

    public sealed class CarcolsKit
    {
        public string FilePath { get; internal set; }
        public string KitName { get; internal set; }
        public string KitId { get; internal set; }
        public List<CarcolsLivery> Liveries { get; } = new List<CarcolsLivery>();
        public override string ToString() => $"{KitName} (id {KitId})";
    }

    public static class CarcolsReader
    {
        public static bool IsCarcols(string text) => text.IndexOf("<CVehicleModelInfoVarGlobal", StringComparison.OrdinalIgnoreCase) >= 0;

        public static List<CarcolsKit> ReadKits(string text, string path)
        {
            var root = XmlSpans.Parse(text);
            var kits = new List<CarcolsKit>();
            foreach (var kitsEl in XmlSpans.Descendants(root).Where(s => s.Name.Equals("Kits", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var item in kitsEl.ChildrenNamed("Item"))
                {
                    var kit = new CarcolsKit
                    {
                        FilePath = path,
                        KitName = item.ChildText(text, "kitName") ?? "",
                        KitId = Attribute(text, item.Child("id"), "value"),
                    };
                    var mods = item.Child("visibleMods");
                    if (mods != null)
                    {
                        foreach (var mod in mods.ChildrenNamed("Item"))
                        {
                            if (!string.Equals(mod.ChildText(text, "type"), "VMT_LIVERY_MOD", StringComparison.OrdinalIgnoreCase)) continue;
                            kit.Liveries.Add(new CarcolsLivery
                            {
                                ModelName = mod.ChildText(text, "modelName") ?? "",
                                Label = mod.ChildText(text, "modShopLabel") ?? "",
                            });
                        }
                    }
                    kits.Add(kit);
                }
            }
            return kits;
        }

        internal static string Attribute(string doc, XmlSpan span, string name)
        {
            if (span == null) return null;
            int tagEnd = span.SelfClosing ? span.End : span.ContentStart;
            string tag = doc.Substring(span.Start, tagEnd - span.Start);
            var m = Regex.Match(tag, "\\b" + Regex.Escape(name) + "\\s*=\\s*(['\"])(.*?)\\1");
            return m.Success ? m.Groups[2].Value : null;
        }
    }

    public sealed class VariationEntry
    {
        public string ModelName { get; internal set; }
        public List<string> Kits { get; } = new List<string>();
        public string FilePath { get; internal set; }
    }

    public static class CarvariationsReader
    {
        public static bool IsCarvariations(string text) => text.IndexOf("<CVehicleModelInfoVariation", StringComparison.OrdinalIgnoreCase) >= 0;

        public static List<VariationEntry> Read(string text, string path)
        {
            var root = XmlSpans.Parse(text);
            var list = new List<VariationEntry>();
            foreach (var data in XmlSpans.Descendants(root).Where(s => s.Name.Equals("variationData", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var item in data.ChildrenNamed("Item"))
                {
                    var e = new VariationEntry { ModelName = item.ChildText(text, "modelName") ?? "", FilePath = path };
                    var kits = item.Child("kits");
                    if (kits != null) e.Kits.AddRange(kits.ChildrenNamed("Item").Select(k => k.Text(text)).Where(k => k.Length > 0));
                    list.Add(e);
                }
            }
            return list;
        }
    }

    public sealed class TextEntry
    {
        public string Key { get; internal set; }
        public string Value { get; internal set; }
        public string FilePath { get; internal set; }
    }

    /// <summary>AddTextEntry("KEY", "Text") calls in FiveM client scripts.</summary>
    public static class TextEntries
    {
        internal static readonly Regex Call = new Regex(
            @"AddTextEntry\s*\(\s*(?<q1>['""])(?<key>(?:\\.|(?!\k<q1>).)*)\k<q1>\s*,\s*(?<q2>['""])(?<val>(?:\\.|(?!\k<q2>).)*)\k<q2>\s*\)",
            RegexOptions.Compiled);

        public static bool HasEntries(string text) => text.IndexOf("AddTextEntry", StringComparison.Ordinal) >= 0;

        public static List<TextEntry> Read(string text, string path)
        {
            return Call.Matches(text).Cast<Match>()
                .Select(m => new TextEntry { Key = m.Groups["key"].Value, Value = LuaUnescape(m.Groups["val"].Value), FilePath = path })
                .ToList();
        }

        internal static string LuaUnescape(string s) => Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value);

        internal static string LuaEscape(string s, char quote) =>
            s.Replace("\\", "\\\\").Replace(quote.ToString(), "\\" + quote).Replace("\r", "").Replace("\n", "\\n");
    }
}
