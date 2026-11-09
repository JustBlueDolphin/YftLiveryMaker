using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiveryKit.Resources
{
    /// <summary>How new liveries are named so they line up with the car's existing ones.</summary>
    public sealed class NamingScheme
    {
        public string ModelPrefix { get; internal set; }
        public int FirstNumber { get; internal set; }
        /// <summary>The car's mod shop labels are the display text itself (e.g. "White Stripes") rather than text keys.</summary>
        public bool LiteralLabels { get; internal set; }
        public string LabelPrefix { get; internal set; }
        public int FirstLabelNumber { get; internal set; }
        /// <summary>The existing label the new ones are modelled on (used to find the names script line style).</summary>
        public string TemplateLabel { get; internal set; }

        public string ModelName(int index) => ModelPrefix + (FirstNumber + index);
        public string Label(int index, string displayText) => LiteralLabels ? displayText : LabelPrefix + (FirstLabelNumber + index);
    }

    public static class Naming
    {
        static readonly Regex Valid = new Regex(@"^[A-Za-z0-9_\-]+$", RegexOptions.Compiled);

        public static bool IsValidModelName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 63 && Valid.IsMatch(name);

        public static int? TrailingNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var m = Regex.Match(s, @"(\d+)$");
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : (int?)null;
        }

        static string PrefixOf(string s)
        {
            var m = Regex.Match(s ?? "", @"^(.*?)\d+$");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static NamingScheme For(CarInfo car, LiveryFile template, ICollection<string> takenYftNames, ICollection<string> takenLabels = null)
        {
            var s = new NamingScheme();
            string tname = template?.ModelName ?? car?.Liveries.FirstOrDefault()?.ModelName ?? "livery";

            s.ModelPrefix = PrefixOf(tname) ?? tname + "_";
            int max = 0;
            foreach (var l in car?.Liveries ?? new List<LiveryFile>())
            {
                if (l.ModelName.StartsWith(s.ModelPrefix, StringComparison.OrdinalIgnoreCase) && TrailingNumber(l.ModelName) is int n && l.ModelName.Length - s.ModelPrefix.Length == n.ToString().Length)
                    max = Math.Max(max, n);
            }
            if (max == 0 && TrailingNumber(tname) is int tn) max = tn;
            s.FirstNumber = max + 1;
            while (takenYftNames != null && takenYftNames.Contains(s.ModelPrefix + s.FirstNumber)) s.FirstNumber++;

            string label = template?.Label;
            if (string.IsNullOrEmpty(label)) label = car?.Liveries.Select(l => l.Label).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            s.TemplateLabel = label;

            if (!string.IsNullOrEmpty(label) && label.Any(char.IsWhiteSpace))
            {
                s.LiteralLabels = true;
                return s;
            }

            if (string.IsNullOrEmpty(label))
            {
                // Nothing to copy: make keys from the model names, e.g. 811S2_LIVERY_24.
                s.LabelPrefix = s.ModelPrefix.ToUpperInvariant();
                s.FirstLabelNumber = s.FirstNumber;
                return s;
            }

            s.LabelPrefix = PrefixOf(label) ?? label + "_";
            int lmax = 0;
            foreach (var l in car?.Liveries ?? new List<LiveryFile>())
            {
                if (l.Label != null && l.Label.StartsWith(s.LabelPrefix, StringComparison.OrdinalIgnoreCase) && TrailingNumber(l.Label) is int n)
                    lmax = Math.Max(lmax, n);
            }
            if (lmax == 0 && TrailingNumber(label) is int ln) lmax = ln;
            s.FirstLabelNumber = lmax + 1;

            // Keep model and label numbers in step when the car already does (811s2_livery_7 <-> 811s2_LIV7).
            if (s.FirstLabelNumber < s.FirstNumber && car?.Liveries.All(l => TrailingNumber(l.ModelName) == TrailingNumber(l.Label)) == true)
                s.FirstLabelNumber = s.FirstNumber;
            while (takenLabels != null && takenLabels.Contains(s.LabelPrefix + s.FirstLabelNumber)) s.FirstLabelNumber++;
            return s;
        }
    }
}
