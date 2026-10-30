using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// DISCLAIMER.md (embedded in the exe) as the consent dialog shows it, and THIRD-PARTY-NOTICES.md
    /// for the Licenses window. The Markdown is written for GitHub, hard-wrapped at 100 columns; a
    /// dialog needs plain paragraphs it can wrap itself.
    /// </summary>
    public static class Disclaimer
    {
        /// <summary>Bump when the text changes in substance: everyone is asked again (settings.disclaimerAccepted).</summary>
        public const int Version = 5;

        private const string NewLine = "\r\n";   // what a WinForms TextBox needs

        private static readonly Regex Heading = new Regex(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$");
        private static readonly Regex ListItem = new Regex(@"^\s{0,3}(?:[*+-]|\d{1,3}[.)])\s+(.*)$");
        private static readonly Regex Link = new Regex(@"\[([^\]]+)\]\(([^)\s]+)\)");
        private static readonly Regex Strong = new Regex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1");
        private static readonly Regex Emphasis = new Regex(@"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])");
        private static readonly Regex Code = new Regex(@"`([^`]+)`");
        private static readonly Regex Comment = new Regex(@"<!--.*?-->", RegexOptions.Singleline);
        private static readonly Regex TableRow = new Regex(@"^\s{0,3}\|(.*)\|\s*$");
        private static readonly Regex TableRule = new Regex(@"^\s{0,3}\|[\s:|-]+\|\s*$");

        /// <summary>
        /// Plain text: headings without '#', hard-wrapped lines joined into one line per paragraph,
        /// list items as "• item", table rows as one line each ("cell | cell", no rule line),
        /// emphasis and code marks removed, links as "text (url)". Paragraphs are separated by a
        /// blank line; lines end with CRLF.
        /// </summary>
        public static string ToPlainText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";
            var text = Comment.Replace(markdown.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF'), "");

            var blocks = new List<string>();
            var paragraph = new StringBuilder();
            var list = new List<StringBuilder>();
            var table = new List<string>();
            bool inFence = false;
            var fence = new StringBuilder();

            void FlushParagraph()
            {
                if (paragraph.Length > 0) blocks.Add(Inline(paragraph.ToString()));
                paragraph.Clear();
            }
            void FlushList()
            {
                if (list.Count > 0)
                {
                    var items = new List<string>();
                    foreach (var item in list) items.Add("• " + Inline(item.ToString()));
                    blocks.Add(string.Join(NewLine, items));
                }
                list.Clear();
            }
            void FlushTable()
            {
                if (table.Count > 0) blocks.Add(string.Join(NewLine, table));
                table.Clear();
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (inFence) { blocks.Add(fence.ToString().TrimEnd('\r', '\n')); fence.Clear(); }
                    else { FlushParagraph(); FlushList(); FlushTable(); }
                    inFence = !inFence;
                    continue;
                }
                if (inFence) { fence.Append(line).Append(NewLine); continue; }

                if (line.Trim().Length == 0) { FlushParagraph(); FlushList(); FlushTable(); continue; }

                var row = TableRow.Match(line);
                if (row.Success)
                {
                    FlushParagraph();
                    FlushList();
                    if (!TableRule.IsMatch(line))
                    {
                        var cells = new List<string>();
                        foreach (var cell in row.Groups[1].Value.Split('|')) cells.Add(Inline(cell));
                        table.Add(string.Join(" | ", cells));
                    }
                    continue;
                }
                FlushTable();   // a table ends at the first line that is not a row

                var h = Heading.Match(line);
                if (h.Success)
                {
                    FlushParagraph(); FlushList();
                    if (h.Groups[1].Value.Length > 0) blocks.Add(Inline(h.Groups[1].Value));
                    continue;
                }
                var li = ListItem.Match(line);
                if (li.Success)
                {
                    FlushParagraph();
                    list.Add(new StringBuilder(li.Groups[1].Value.Trim()));
                    continue;
                }
                if (list.Count > 0) { list[list.Count - 1].Append(' ').Append(line.Trim()); continue; }   // continuation
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line.Trim());
            }
            if (inFence && fence.Length > 0) blocks.Add(fence.ToString().TrimEnd('\r', '\n'));
            FlushParagraph();
            FlushList();
            FlushTable();
            return string.Join(NewLine + NewLine, blocks);
        }

        private static string Inline(string s)
        {
            s = Code.Replace(s, "$1");
            s = Link.Replace(s, m =>
            {
                var url = m.Groups[2].Value;
                bool external = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                return external && url != m.Groups[1].Value ? m.Groups[1].Value + " (" + url + ")" : m.Groups[1].Value;
            });
            s = Strong.Replace(s, "$2");
            s = Emphasis.Replace(s, "$1");
            s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!])", "$1");   // Markdown escapes
            s = Regex.Replace(s, @" {2,}", " ");
            return s.Trim();
        }
    }
}
