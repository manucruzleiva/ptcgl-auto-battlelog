using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// Reads and edits BepInEx 5 .cfg text the way BepInEx itself parses it (ConfigFile.Reload and
    /// TomlTypeConverter; the tests compare against the real BepInEx.dll): "[Section]" headers,
    /// "Key = Value" lines, '#' comments, values trimmed, the last duplicate wins. String values
    /// are unescaped (\\, \", \n...) except one that looks like a Windows path ("C:\dir\file"),
    /// which BepInEx keeps verbatim. That is also how BepInEx writes paths: C:\Users\Ash, as is.
    /// </summary>
    internal static class BepInExConfig
    {
        // BepInEx keeps a value that looks like an unescaped Windows path ("C:\dir\file") as is.
        private static readonly Regex RawPath = new Regex(@"^""?\w:\\(?!\\)(?!.+\\\\)");

        internal static string ReadValue(string text, string section, string key)
        {
            string current = "", value = null;
            foreach (var raw in SplitLines(text, out _, out _))
            {
                var line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (IsHeader(line)) { current = line.Substring(1, line.Length - 2); continue; }
                var kv = line.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && current == section && kv[0].Trim() == key) value = kv[1].Trim();
            }
            return value == null ? null : ConvertToObject(value);
        }

        /// <summary>
        /// Sets every occurrence of the key in the section, or adds it right under the first
        /// header of the section, or appends the section. Other lines, comments, line endings and
        /// the trailing newline are kept exactly.
        /// </summary>
        internal static string WriteValue(string text, string section, string key, string value)
        {
            var lines = SplitLines(text, out var nl, out var trailingNewline);
            var entry = key + " = " + Serialize(value);
            string current = "";
            int header = -1;
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (IsHeader(line))
                {
                    current = line.Substring(1, line.Length - 2);
                    if (current == section && header < 0) header = i;
                    continue;
                }
                var kv = line.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && current == section && kv[0].Trim() == key) { lines[i] = entry; replaced = true; }
            }
            if (!replaced)
            {
                if (header >= 0) lines.Insert(header + 1, entry);
                else
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                    lines.Add("[" + section + "]");
                    lines.Add(entry);
                }
            }
            return string.Join(nl, lines) + (trailingNewline || text.Length == 0 ? nl : "");
        }

        private static bool IsHeader(string trimmed)
        {
            return trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal);
        }

        private static List<string> SplitLines(string text, out string newline, out bool trailingNewline)
        {
            text = text ?? "";
            newline = text.Contains("\r\n") || !text.Contains("\n") ? "\r\n" : "\n";
            var lines = new List<string>(text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            trailingNewline = text.Length > 0 && lines[lines.Count - 1].Length == 0;
            if (trailingNewline || text.Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        /// <summary>
        /// The text after "Key = ": exactly what BepInEx itself writes. BepInEx rewrites the cfg
        /// every time the game starts, so a value only lasts if it survives BepInEx's own write and
        /// read; one that does not (BepInEx 5.4.23 garbles ' and " in a "C:\..." path, and network
        /// paths like \\server\share) is refused with an ArgumentException rather than working
        /// for one session and then silently pointing somewhere else.
        /// </summary>
        internal static string Serialize(string value)
        {
            value = value ?? "";
            var text = Escape(value);
            if (ConvertToObject(text.Trim()) != value)
                throw new ArgumentException("BepInEx 5 cannot keep this folder in its config: " + value +
                                            ". Avoid ' and \" in the path, and network paths (\\\\server\\share); a mapped drive letter works.");
            return text;
        }

        internal static string ConvertToObject(string str)
        {
            return RawPath.IsMatch(str) ? str : Unescape(str);
        }

        /// <summary>BepInEx 5.4.23's own escaping (TomlTypeConverter.Escape): note that '\' is left alone.</summary>
        internal static string Escape(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return string.Empty;
            var sb = new StringBuilder(txt.Length + 2);
            foreach (var c in txt)
                switch (c)
                {
                    case '\0': sb.Append(@"\0"); break;
                    case '\a': sb.Append(@"\a"); break;
                    case '\b': sb.Append(@"\b"); break;
                    case '\t': sb.Append(@"\t"); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\v': sb.Append(@"\v"); break;
                    case '\f': sb.Append(@"\f"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\'': sb.Append(@"\'"); break;
                    case '"': sb.Append("\\\""); break;
                    default: sb.Append(c); break;
                }
            return sb.ToString();
        }

        internal static string Unescape(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return txt;
            var sb = new StringBuilder(txt.Length);
            for (int i = 0; i < txt.Length;)
            {
                int bs = txt.IndexOf('\\', i);
                if (bs < 0 || bs == txt.Length - 1) bs = txt.Length;   // a lone trailing '\' stays
                sb.Append(txt, i, bs - i);
                if (bs >= txt.Length) break;
                var c = txt[bs + 1];
                switch (c)
                {
                    case '0': sb.Append('\0'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'v': sb.Append('\v'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\'': sb.Append('\''); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(c); break;
                }
                i = bs + 2;
            }
            return sb.ToString();
        }
    }
}
