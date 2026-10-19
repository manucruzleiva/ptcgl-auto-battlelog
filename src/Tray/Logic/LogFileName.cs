using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// Reads back the names the plugin gives its logs, for the "battle log saved" notification:
    /// "yyyy-MM-dd HH.mm.ss - RESULT[ vs OPPONENT][ (N)].txt".
    /// </summary>
    public static class LogFileName
    {
        public static readonly IReadOnlyList<string> Results = new[] { "VICTORY", "DEFEAT", "NO-RESULT", "EXPORTED" };

        private const string DateFormat = "yyyy-MM-dd HH.mm.ss";
        private const string Separator = " - ";
        private const string Versus = " vs ";

        /// <summary>
        /// "2026-10-19 21.34.05 - VICTORY vs Gary.txt" → "VICTORY", "Gary". The opponent is null when
        /// the name has none; it may itself contain " vs ". A path is accepted too. Temp files
        /// (.txt.tmp) and anything else do not parse.
        /// </summary>
        public static bool TryParse(string fileName, out string result, out string opponent)
        {
            result = null;
            opponent = null;
            if (string.IsNullOrEmpty(fileName)) return false;

            string name;
            try { name = Path.GetFileName(fileName); }
            catch (ArgumentException) { return false; }
            if (name == null || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
            name = name.Substring(0, name.Length - 4);
            // An opponent may itself end in " (2)": when the name does not parse without it, it was part of the name.
            var stripped = StripDuplicateSuffix(name);
            return TryParseStem(stripped, out result, out opponent) ||
                   (!ReferenceEquals(stripped, name) && TryParseStem(name, out result, out opponent));
        }

        private static bool TryParseStem(string name, out string result, out string opponent)
        {
            result = null;
            opponent = null;
            if (name.Length < DateFormat.Length + Separator.Length) return false;
            if (!DateTime.TryParseExact(name.Substring(0, DateFormat.Length), DateFormat, CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out _)) return false;
            if (string.CompareOrdinal(name, DateFormat.Length, Separator, 0, Separator.Length) != 0) return false;

            var rest = name.Substring(DateFormat.Length + Separator.Length);
            foreach (var label in Results)
            {
                if (rest == label) { result = label; return true; }
                if (rest.StartsWith(label + Versus, StringComparison.Ordinal) && rest.Length > label.Length + Versus.Length)
                {
                    result = label;
                    opponent = rest.Substring(label.Length + Versus.Length);
                    return true;
                }
            }
            return false;
        }

        /// <summary>The plugin numbers duplicates " (2)", " (3)"...; one such suffix is removed.</summary>
        private static string StripDuplicateSuffix(string name)
        {
            if (!name.EndsWith(")", StringComparison.Ordinal)) return name;
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0) return name;
            var digits = name.Substring(open + 2, name.Length - open - 3);
            if (digits.Length == 0 || digits.Length > 6 || digits[0] == '0') return name;
            foreach (var c in digits) if (c < '0' || c > '9') return name;
            return digits == "1" ? name : name.Substring(0, open);
        }
    }
}
