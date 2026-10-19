using System;
using System.Collections.Generic;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// Finds the Pokémon TCG Live folder. The caller gathers the inputs that need Windows (registry,
    /// running process); this class only decides, so the order of preference is testable.
    /// </summary>
    public static class GameLocator
    {
        public const string GameExe = "Pokemon TCG Live.exe";
        public const string ProcessName = "Pokemon TCG Live";

        // "Pokémon" spelled with an escape so the source reads the same in any editor encoding.
        internal const string CompanyFolder = "The Pokémon Company International";
        internal const string GameFolder = "Pokémon Trading Card Game Live";

        public static bool IsGameDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try { return File.Exists(Path.Combine(dir, GameExe)); }
            catch (Exception) { return false; }   // .NET Framework throws on invalid path characters
        }

        /// <summary>The launcher's default install places, most likely first. Empty bases are skipped.</summary>
        public static IReadOnlyList<string> DefaultCandidates(string userProfile, string localAppData,
                                                              string programFiles, string programFilesX86)
        {
            var list = new List<string>();
            foreach (var root in new[] { userProfile, localAppData, programFiles, programFilesX86 })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string dir;
                try { dir = Path.Combine(root, CompanyFolder, GameFolder); }
                catch (Exception) { continue; }
                if (!list.Exists(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))) list.Add(dir);
            }
            return list;
        }

        /// <summary>
        /// First valid of: saved, candidates, registry install locations, running process folder.
        /// Registry values may be an InstallLocation or a DisplayIcon ("...\Pokemon TCG Live.exe",0);
        /// both are accepted. Returns null when nothing matches.
        /// </summary>
        public static string Find(string savedDir, IEnumerable<string> candidates,
                                  IEnumerable<string> registryLocations, string runningProcessDir)
        {
            foreach (var raw in Inputs(savedDir, candidates, registryLocations, runningProcessDir))
            {
                var dir = Clean(raw);
                if (IsGameDir(dir)) return dir;
            }
            return null;
        }

        private static IEnumerable<string> Inputs(string saved, IEnumerable<string> candidates,
                                                  IEnumerable<string> registry, string running)
        {
            yield return saved;
            if (candidates != null) foreach (var c in candidates) yield return c;
            if (registry != null) foreach (var r in registry) yield return r;
            yield return running;
        }

        /// <summary>Strips quotes and an icon index, and turns a path to the game exe into its folder.</summary>
        internal static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            int comma = s.LastIndexOf(',');
            if (comma > 0 && IsDigits(s, comma + 1)) s = s.Substring(0, comma);
            s = s.Trim().Trim('"').Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try { s = Path.GetDirectoryName(s); }
                catch (Exception) { return null; }
            }
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static bool IsDigits(string s, int start)
        {
            var t = s.Substring(start).Trim();
            if (t.Length == 0) return false;
            if (t[0] == '-') t = t.Substring(1);
            foreach (var c in t) if (c < '0' || c > '9') return false;
            return t.Length > 0;
        }
    }
}
