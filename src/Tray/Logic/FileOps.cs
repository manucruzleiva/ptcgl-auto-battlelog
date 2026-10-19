using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>Small file helpers shared by the Logic classes.</summary>
    internal static class FileOps
    {
        internal static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Moves <paramref name="tmp"/> over <paramref name="dest"/>. File.Replace is a single
        /// rename on both Windows and Linux, so a reader never sees a missing or half-written file.
        /// </summary>
        internal static void ReplaceWith(string tmp, string dest)
        {
            if (!File.Exists(dest)) { File.Move(tmp, dest); return; }
            try { File.Replace(tmp, dest, null); }
            catch (IOException) when (File.Exists(tmp))
            {
                // ReplaceFile refuses some cases a delete + rename allows (antivirus, network drives).
                File.Delete(dest);
                File.Move(tmp, dest);
            }
        }

        /// <summary>Writes beside, then swaps in: a crash mid-write never leaves a truncated file.</summary>
        internal static void WriteAllTextAtomic(string path, string text, Encoding encoding)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, encoding);
                ReplaceWith(tmp, path);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>
        /// Reads a small text file another process may be replacing at the same moment. Detects a
        /// BOM. Returns null when the file is absent, unreadable or larger than <paramref name="maxBytes"/>.
        /// </summary>
        internal static string TryReadSmallText(string path, int maxBytes)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > maxBytes) return null;
                    using (var r = new StreamReader(fs, Utf8NoBom, true)) return r.ReadToEnd();
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// <paramref name="path"/> is <paramref name="folder"/> or inside it, after resolving both to
        /// full paths; case-insensitive, as on Windows. "C:\BepInExtra" is not inside "C:\BepInEx".
        /// </summary>
        internal static bool IsSameOrUnder(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                var p = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var f = Path.GetFullPath(folder.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(p, f, StringComparison.OrdinalIgnoreCase) ||
                       p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Deletes <paramref name="dir"/> and everything in it, except <paramref name="keep"/> when it
        /// is at or under <paramref name="dir"/>: that folder, its contents and the folders leading to
        /// it stay. Battle logs are never deleted, even from a logs folder someone put inside a folder
        /// the uninstaller removes. True when something was kept. Throws on the first failure.
        /// </summary>
        internal static bool DeleteTreeExcept(string dir, string keep)
        {
            if (!Directory.Exists(dir)) return false;
            if (!IsSameOrUnder(keep, dir))
            {
                Directory.Delete(dir, true);
                return false;
            }
            if (IsSameOrUnder(dir, keep)) return true;   // dir is the logs folder itself
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).ToList())
            {
                if (Directory.Exists(entry)) DeleteTreeExcept(entry, keep);
                else File.Delete(entry);
            }
            return true;
        }

        internal static bool TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
