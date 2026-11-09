using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveryKit.Meta
{
    public sealed class NewLiveryEntry
    {
        public NewLiveryEntry(string modelName, string label)
        {
            ModelName = modelName;
            Label = label;
        }

        public string ModelName { get; }
        public string Label { get; }
    }

    public sealed class PatchResult
    {
        public string Text { get; internal set; }
        public List<string> Added { get; } = new List<string>();
        public List<string> AlreadyThere { get; } = new List<string>();
    }

    /// <summary>Adds VMT_LIVERY_MOD entries to a kit in carcols.meta by inserting text, keeping the file's own formatting.</summary>
    public static class CarcolsPatcher
    {
        static XmlSpan FindKit(string text, string kitName)
        {
            var root = XmlSpans.Parse(text);
            var kit = XmlSpans.Descendants(root)
                .Where(s => s.Name.Equals("Kits", StringComparison.OrdinalIgnoreCase))
                .SelectMany(k => k.ChildrenNamed("Item"))
                .FirstOrDefault(i => string.Equals(i.ChildText(text, "kitName"), kitName, StringComparison.OrdinalIgnoreCase));
            if (kit == null) throw new LiveryException($"The mod kit '{kitName}' isn't in this carcols.meta any more.");
            return kit;
        }

        /// <summary>Changes the modShopLabel of existing entries (used when a replaced livery is renamed).</summary>
        public static PatchResult SetLabels(string text, string kitName, IEnumerable<(string ModelName, string Label)> changes)
        {
            var mods = FindKit(text, kitName).Child("visibleMods");
            var result = new PatchResult();
            var edits = new List<(int Start, int End, string With)>();
            foreach (var (model, label) in changes)
            {
                var item = mods?.ChildrenNamed("Item").FirstOrDefault(m => string.Equals(m.ChildText(text, "modelName"), model, StringComparison.OrdinalIgnoreCase));
                var lb = item?.Child("modShopLabel");
                if (lb == null || lb.Text(text) == label) { result.AlreadyThere.Add(model); continue; }
                edits.Add(lb.SelfClosing
                    ? (lb.Start, lb.End, "<modShopLabel>" + XmlSpans.Escape(label) + "</modShopLabel>")
                    : (lb.ContentStart, lb.ContentEnd, XmlSpans.Escape(label)));
                result.Added.Add(model);
            }
            var sb = new StringBuilder(text);
            foreach (var e in edits.OrderByDescending(x => x.Start))
            {
                sb.Remove(e.Start, e.End - e.Start);
                sb.Insert(e.Start, e.With);
            }
            result.Text = sb.ToString();
            return result;
        }

        public static PatchResult AddLiveries(string text, string kitName, string templateModelName, IEnumerable<NewLiveryEntry> entries)
        {
            var kit = FindKit(text, kitName);

            var mods = kit.Child("visibleMods");
            if (mods == null) throw new LiveryException($"The mod kit '{kitName}' has no <visibleMods> section.");

            var all = mods.ChildrenNamed("Item").ToList();
            var existing = new HashSet<string>(all.Select(m => m.ChildText(text, "modelName") ?? ""), StringComparer.OrdinalIgnoreCase);
            var liveryItems = all.Where(m => string.Equals(m.ChildText(text, "type"), "VMT_LIVERY_MOD", StringComparison.OrdinalIgnoreCase)).ToList();
            var template = liveryItems.FirstOrDefault(m => string.Equals(m.ChildText(text, "modelName"), templateModelName, StringComparison.OrdinalIgnoreCase))
                           ?? liveryItems.FirstOrDefault();

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var result = new PatchResult();
            var sb = new StringBuilder();

            if (template != null)
            {
                string indent = LineIndent(text, template.Start);
                string block = text.Substring(template.Start, template.End - template.Start);
                foreach (var e in entries)
                {
                    if (!existing.Add(e.ModelName)) { result.AlreadyThere.Add(e.ModelName); continue; }
                    sb.Append(nl).Append(indent).Append(FillBlock(text, template, block, e));
                    result.Added.Add(e.ModelName);
                }
                int at = liveryItems.Last().End;
                result.Text = text.Insert(at, sb.ToString());
                return result;
            }

            // The kit has no liveries yet: use the standard entry layout.
            string itemIndent = LineIndent(text, mods.Start) + "  ";
            foreach (var e in entries)
            {
                if (!existing.Add(e.ModelName)) { result.AlreadyThere.Add(e.ModelName); continue; }
                sb.Append(nl).Append(StandardItem(e, itemIndent, nl));
                result.Added.Add(e.ModelName);
            }
            if (mods.SelfClosing)
            {
                string open = LineIndent(text, mods.Start);
                result.Text = text.Substring(0, mods.Start) + "<visibleMods>" + sb + nl + open + "</visibleMods>" + text.Substring(mods.End);
            }
            else
            {
                result.Text = text.Insert(LastLineBreakBefore(text, mods.ContentEnd), sb.ToString());
            }
            return result;
        }

        /// <summary>The template's text with modelName and modShopLabel swapped for the new values.</summary>
        static string FillBlock(string doc, XmlSpan item, string block, NewLiveryEntry e)
        {
            var edits = new List<(int start, int end, string with)>();
            AddEdit(item.Child("modelName"), "modelName", e.ModelName);
            AddEdit(item.Child("modShopLabel"), "modShopLabel", e.Label);

            var sb = new StringBuilder(block);
            foreach (var ed in edits.OrderByDescending(x => x.start))
            {
                sb.Remove(ed.start, ed.end - ed.start);
                sb.Insert(ed.start, ed.with);
            }
            return sb.ToString();

            void AddEdit(XmlSpan el, string tag, string value)
            {
                if (el == null) return;
                if (el.SelfClosing)
                    edits.Add((el.Start - item.Start, el.End - item.Start, $"<{tag}>{XmlSpans.Escape(value)}</{tag}>"));
                else
                    edits.Add((el.ContentStart - item.Start, el.ContentEnd - item.Start, XmlSpans.Escape(value)));
            }
        }

        internal static string StandardItem(NewLiveryEntry e, string indent, string nl)
        {
            string i2 = indent + "  ";
            return indent + "<Item>" + nl +
                   i2 + "<modelName>" + XmlSpans.Escape(e.ModelName) + "</modelName>" + nl +
                   i2 + "<modShopLabel>" + XmlSpans.Escape(e.Label) + "</modShopLabel>" + nl +
                   i2 + "<linkedModels />" + nl +
                   i2 + "<turnOffBones />" + nl +
                   i2 + "<type>VMT_LIVERY_MOD</type>" + nl +
                   i2 + "<bone>chassis</bone>" + nl +
                   i2 + "<collisionBone>chassis</collisionBone>" + nl +
                   i2 + "<cameraPos>VMCP_DEFAULT</cameraPos>" + nl +
                   i2 + "<audioApply value=\"1.000000\" />" + nl +
                   i2 + "<weight value=\"0\" />" + nl +
                   i2 + "<turnOffExtra value=\"false\" />" + nl +
                   i2 + "<disableBonnetCamera value=\"false\" />" + nl +
                   i2 + "<allowBonnetSlide value=\"true\" />" + nl +
                   indent + "</Item>";
        }

        /// <summary>A complete carcols.meta with one new mod kit holding the given liveries.</summary>
        public static string NewKitFile(string kitName, string kitId, IEnumerable<NewLiveryEntry> entries)
        {
            const string nl = "\r\n";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(nl);
            sb.Append("<CVehicleModelInfoVarGlobal>").Append(nl);
            sb.Append("  <Kits>").Append(nl);
            sb.Append("    <Item>").Append(nl);
            sb.Append("      <kitName>").Append(XmlSpans.Escape(kitName)).Append("</kitName>").Append(nl);
            sb.Append("      <id value=\"").Append(kitId).Append("\" />").Append(nl);
            sb.Append("      <kitType>MKT_SPECIAL</kitType>").Append(nl);
            sb.Append("      <visibleMods>").Append(nl);
            foreach (var e in entries) sb.Append(StandardItem(e, "        ", nl)).Append(nl);
            sb.Append("      </visibleMods>").Append(nl);
            sb.Append("      <linkMods />").Append(nl);
            sb.Append("      <statMods />").Append(nl);
            sb.Append("      <slotNames />").Append(nl);
            sb.Append("      <liveryNames />").Append(nl);
            sb.Append("      <livery2Names />").Append(nl);
            sb.Append("    </Item>").Append(nl);
            sb.Append("  </Kits>").Append(nl);
            sb.Append("  <Lights />").Append(nl);
            sb.Append("</CVehicleModelInfoVarGlobal>").Append(nl);
            return sb.ToString();
        }

        static string LineIndent(string text, int pos)
        {
            int ls = text.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
            string before = text.Substring(ls, pos - ls);
            return before.All(char.IsWhiteSpace) ? before : "";
        }

        static int LastLineBreakBefore(string text, int pos)
        {
            int i = pos;
            while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
            if (i > 0 && text[i - 1] == '\n') i--;
            if (i > 0 && text[i - 1] == '\r') i--;
            return i;
        }
    }

    /// <summary>Adds AddTextEntry lines to a FiveM names script, copying the style of the lines already there.</summary>
    public static class NamesLuaPatcher
    {
        public static PatchResult AddEntries(string text, string templateKey, IEnumerable<(string Key, string Value)> entries)
        {
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { nl }, StringSplitOptions.None).ToList();

            var defined = new HashSet<string>(TextEntries.Read(text, null).Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
            int styleLine = -1, insertAfter = -1;
            string prefix = KeyPrefix(templateKey);
            for (int i = 0; i < lines.Count; i++)
            {
                var m = TextEntries.Call.Match(lines[i]);
                if (!m.Success) continue;
                string key = m.Groups["key"].Value;
                if (styleLine < 0 || string.Equals(key, templateKey, StringComparison.OrdinalIgnoreCase)) styleLine = i;
                if (prefix.Length > 0 && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(key.Substring(prefix.Length), @"^\d+$"))
                    insertAfter = i;
            }

            var result = new PatchResult();
            var newLines = new List<string>();
            foreach (var (key, value) in entries)
            {
                if (!defined.Add(key)) { result.AlreadyThere.Add(key); continue; }
                newLines.Add(styleLine >= 0 ? Restyle(lines[styleLine], key, value) : $"AddTextEntry(\"{key}\", \"{TextEntries.LuaEscape(value, '"')}\")");
                result.Added.Add(key);
            }

            if (newLines.Count == 0) { result.Text = text; return result; }
            if (insertAfter < 0) insertAfter = styleLine;
            if (insertAfter < 0)
            {
                // No entries in the file yet: append at the end.
                int last = lines.Count;
                while (last > 0 && lines[last - 1].Trim().Length == 0) last--;
                lines.InsertRange(last, newLines);
            }
            else
            {
                lines.InsertRange(insertAfter + 1, newLines);
            }
            result.Text = string.Join(nl, lines);
            return result;
        }

        /// <summary>Changes the text of AddTextEntry lines that already exist (keys not in the file are ignored).</summary>
        public static PatchResult SetValues(string text, IEnumerable<(string Key, string Value)> changes)
        {
            var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in changes) want[key] = value;
            var result = new PatchResult();
            var sb = new StringBuilder(text);
            foreach (var m in TextEntries.Call.Matches(text).Cast<Match>().Reverse())
            {
                string key = m.Groups["key"].Value;
                if (!want.TryGetValue(key, out var value)) continue;
                var val = m.Groups["val"];
                if (TextEntries.LuaUnescape(val.Value) == value) { result.AlreadyThere.Add(key); continue; }
                sb.Remove(val.Index, val.Length).Insert(val.Index, TextEntries.LuaEscape(value, m.Groups["q2"].Value[0]));
                result.Added.Add(key);
            }
            result.Text = sb.ToString();
            return result;
        }

        public static string NewFile(IEnumerable<(string Key, string Value)> entries)
        {
            var sb = new StringBuilder();
            sb.Append("-- Mod shop names for the liveries made with YFT Livery Maker\r\n");
            foreach (var (key, value) in entries)
                sb.Append("AddTextEntry('").Append(TextEntries.LuaEscape(key, '\'')).Append("', '").Append(TextEntries.LuaEscape(value, '\'')).Append("')\r\n");
            return sb.ToString();
        }

        static string Restyle(string line, string key, string value)
        {
            var m = TextEntries.Call.Match(line);
            char q2 = m.Groups["q2"].Value[0];
            var sb = new StringBuilder(line);
            var v = m.Groups["val"];
            var k = m.Groups["key"];
            sb.Remove(v.Index, v.Length).Insert(v.Index, TextEntries.LuaEscape(value, q2));
            sb.Remove(k.Index, k.Length).Insert(k.Index, key);
            return sb.ToString();
        }

        static string KeyPrefix(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var m = Regex.Match(key, @"^(.*?)\d+$");
            return m.Success ? m.Groups[1].Value : "";
        }
    }

    public static class FxManifestPatcher
    {
        /// <summary>Adds a client_script line for <paramref name="script"/> unless the manifest already mentions it.</summary>
        public static string EnsureClientScript(string text, string script)
        {
            if (text.IndexOf(script, StringComparison.OrdinalIgnoreCase) >= 0) return text;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string trimmed = text.TrimEnd();
            return trimmed + nl + nl + "client_script '" + script + "'" + nl;
        }
    }
}
