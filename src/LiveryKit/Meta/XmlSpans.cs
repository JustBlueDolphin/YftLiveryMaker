using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace LiveryKit.Meta
{
    /// <summary>An element's location in the original text.</summary>
    public sealed class XmlSpan
    {
        public string Name;
        /// <summary>Index of the '&lt;' that opens the start tag.</summary>
        public int Start;
        /// <summary>Index just after the start tag's '&gt;'.</summary>
        public int ContentStart;
        /// <summary>Index of the '&lt;' of the end tag (== ContentStart when self-closing).</summary>
        public int ContentEnd;
        /// <summary>Index just after the end tag's '&gt;'.</summary>
        public int End;
        public bool SelfClosing;
        public XmlSpan Parent;
        public readonly List<XmlSpan> Children = new List<XmlSpan>();

        public XmlSpan Child(string name) => Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        public IEnumerable<XmlSpan> ChildrenNamed(string name) => Children.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        public string Text(string doc) => SelfClosing ? "" : WebUtility.HtmlDecode(doc.Substring(ContentStart, ContentEnd - ContentStart)).Trim();
        public string ChildText(string doc, string name) => Child(name)?.Text(doc);

        public override string ToString() => $"<{Name}> @{Start}";
    }

    /// <summary>
    /// Forgiving XML scanner that records where each element sits in the text, so files can be
    /// edited by inserting text instead of re-serialising (which would reformat the whole file and
    /// trip over the small mistakes the game happily tolerates in .meta files).
    /// </summary>
    public static class XmlSpans
    {
        public static XmlSpan Parse(string doc)
        {
            var root = new XmlSpan { Name = "#document", Start = 0, ContentStart = 0, ContentEnd = doc.Length, End = doc.Length };
            var stack = new List<XmlSpan> { root };
            int i = 0, n = doc.Length;

            while (i < n)
            {
                int lt = doc.IndexOf('<', i);
                if (lt < 0) break;

                if (Match(doc, lt, "<!--"))
                {
                    int e = doc.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 3;
                    continue;
                }
                if (Match(doc, lt, "<![CDATA["))
                {
                    int e = doc.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 3;
                    continue;
                }
                if (Match(doc, lt, "<?"))
                {
                    int e = doc.IndexOf("?>", lt + 2, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 2;
                    continue;
                }
                if (Match(doc, lt, "<!"))
                {
                    int e = doc.IndexOf('>', lt + 2);
                    i = e < 0 ? n : e + 1;
                    continue;
                }

                int gt = FindTagEnd(doc, lt + 1);
                if (gt < 0) break;

                if (doc[lt + 1] == '/')
                {
                    string closeName = ReadName(doc, lt + 2);
                    int idx = stack.FindLastIndex(s => string.Equals(s.Name, closeName, StringComparison.Ordinal));
                    if (idx > 0)
                    {
                        // Anything still open above the match was never closed: end it here.
                        for (int k = stack.Count - 1; k > idx; k--)
                        {
                            stack[k].ContentEnd = lt;
                            stack[k].End = lt;
                        }
                        var el = stack[idx];
                        el.ContentEnd = lt;
                        el.End = gt + 1;
                        stack.RemoveRange(idx, stack.Count - idx);
                    }
                    i = gt + 1;
                    continue;
                }

                var span = new XmlSpan
                {
                    Name = ReadName(doc, lt + 1),
                    Start = lt,
                    ContentStart = gt + 1,
                    Parent = stack[stack.Count - 1],
                };
                span.Parent.Children.Add(span);
                if (doc[gt - 1] == '/')
                {
                    span.SelfClosing = true;
                    span.ContentStart = span.ContentEnd = span.End = gt + 1;
                }
                else
                {
                    stack.Add(span);
                }
                i = gt + 1;
            }

            for (int k = stack.Count - 1; k > 0; k--)
            {
                stack[k].ContentEnd = n;
                stack[k].End = n;
            }
            return root;
        }

        public static IEnumerable<XmlSpan> Descendants(XmlSpan s)
        {
            foreach (var c in s.Children)
            {
                yield return c;
                foreach (var d in Descendants(c)) yield return d;
            }
        }

        public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        static bool Match(string doc, int at, string what) => string.CompareOrdinal(doc, at, what, 0, what.Length) == 0;

        static int FindTagEnd(string doc, int i)
        {
            char quote = '\0';
            for (; i < doc.Length; i++)
            {
                char c = doc[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '>') return i;
            }
            return -1;
        }

        static string ReadName(string doc, int i)
        {
            int s = i;
            while (i < doc.Length && !char.IsWhiteSpace(doc[i]) && doc[i] != '>' && doc[i] != '/') i++;
            return doc.Substring(s, i - s);
        }
    }
}
