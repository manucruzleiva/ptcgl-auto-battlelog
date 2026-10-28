using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>
    /// One .txt per match plus an index.csv with one row per match for spreadsheets. The .txt
    /// starts with the exact Export text. With a <see cref="Sealer"/>, every write of a log is
    /// sealed (docs/LOG-SEAL.md): in Footer mode (the default) a seal block follows the Export
    /// text in the .txt, so a tool that expects the bare Export text must cut it off; in Sidecar
    /// mode it goes to a .seal file next to the .txt, which then holds only the Export text.
    /// The player's deck, when the client gives it, goes to decks\&lt;sha256&gt;.deck, once per distinct
    /// deck: the seal names it by that hash, so matches played with the same deck share one file.
    /// </summary>
    public sealed class LogStore : ILogSink
    {
        public const string IndexFile = "index.csv";
        /// <summary>Subfolder of the logs folder holding each distinct deck once (docs/LOG-SEAL.md §2a).</summary>
        public const string DecksFolder = "decks";
        public const string IndexHeader = "Date,Result,Opponent,Player,Deck,Duration (min),MatchId,File";

        /// <summary>
        /// Longest file name stem. With ".txt" and a " (999)" duplicate suffix the whole name stays
        /// within 150 characters, far from MAX_PATH even in a deep Documents folder.
        /// </summary>
        public const int MaxStemLength = 140;

        // 1.0 (Spanish) wrote these; they are migrated once at startup, see MigrateLegacy.
        internal const string LegacyStatusFile = "estado.txt";
        internal const string LegacyIndexHeader = "Fecha,Resultado,Oponente,Jugador,Mazo,Duracion (min),MatchId,Archivo";
        private static readonly string[] LegacyLabels = { "VICTORIA", "VICTORY", "DERROTA", "DEFEAT", "SIN-RESULTADO", "NO-RESULT", "EXPORTADO", "EXPORTED" };

        private static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        /// <summary>The seal each file written this session carries, for re-sealing it in place.</summary>
        private readonly Dictionary<string, SealRef> _seals = new Dictionary<string, SealRef>(StringComparer.OrdinalIgnoreCase);

        public string Folder { get; }

        /// <summary>Seals every write; null never seals.</summary>
        public LogSealer Sealer;

        /// <summary>File.Move; replaceable so the tests can play a file that is locked.</summary>
        internal Action<string, string> MoveFile = File.Move;

        /// <summary>
        /// The replace step of <see cref="WriteAtomic(string, byte[])"/>: ReplaceFile ignoring merge
        /// errors (ACLs a network share cannot merge). Replaceable so the tests can play a file
        /// system that refuses it.
        /// </summary>
        internal static Action<string, string> ReplaceFile = (tmp, path) => File.Replace(tmp, path, null, true);

        public LogStore(string folder)
        {
            Folder = folder;
        }

        public string Save(MatchRecord m, string text, string existingPath)
        {
            Directory.CreateDirectory(Folder);
            var wanted = FileNameFor(m);
            bool isNew = string.IsNullOrEmpty(existingPath);
            // Same file unless what we know about the match changed its name (the result or the
            // opponent arrived after the first save): then it moves, so the name never lies.
            bool rename = !isNew && !IsNamed(existingPath, wanted);
            var path = isNew || rename ? UniquePath(wanted) : existingPath;

            SealRef previous = null;
            if (!isNew) _seals.TryGetValue(existingPath, out previous);
            bool moved = false;
            if (rename)
            {
                // Moved, then overwritten: had the old file stayed behind next to the new one, both
                // would carry a seal for the same place in the chain. If it cannot be moved (locked),
                // its seal is still on disk, so the new seal takes the next place instead.
                try { MoveFile(existingPath, path); moved = true; }
                catch (Exception) { if (File.Exists(existingPath)) previous = null; }
            }

            var mode = Sealer == null ? SealMode.Off : Sealer.CurrentMode();
            // A verifier takes a footer in the .txt before a .seal file. A log that itself contains
            // the footer's separator would hide its sidecar behind that: seal it with a footer, which
            // the "last separator" rule finds.
            if (mode == SealMode.Sidecar && text.IndexOf(LogSealer.FooterPrefix, StringComparison.Ordinal) >= 0) mode = SealMode.Footer;

            // The deck goes first, so the seal never names a deck that is not on disk: one that cannot
            // be written is left out of the seal (deckSha256 null) instead. Without a seal nothing
            // would name it, so none is saved then.
            var deck = m.MyDeckList == null || mode == SealMode.Off ? null : SaveDeck(m.MyDeckList.ToBytes());

            var log = Utf8NoBom.GetBytes(text);
            var seal = mode == SealMode.Off ? null : Sealer.TryPrepare(m, text, log, deck, previous);
            var footer = seal != null && mode == SealMode.Footer ? Encoding.ASCII.GetBytes("\n\n" + seal.Block) : new byte[0];
            try
            {
                WriteAtomic(path, Concat(Bom, log, footer));
            }
            catch
            {
                if (moved) { try { File.Move(path, existingPath); } catch { } }
                throw;
            }

            // The log is safe on disk from here on. Nothing below may throw, or the watcher would
            // not learn the path and the next read would write a second file for the same match.
            var sidecar = SidecarPathFor(path);
            if (seal != null && mode == SealMode.Sidecar)
            {
                try { WriteAtomic(sidecar, Encoding.ASCII.GetBytes(seal.Block)); }
                catch (Exception e)
                {
                    TryDelete(sidecar);
                    Sealer.Abandon("could not write " + Path.GetFileName(sidecar) + " (" + e.Message + ")");
                    seal = null;
                }
            }
            else
            {
                TryDelete(sidecar);     // it described what this file held before
            }
            if (seal != null)
            {
                Sealer.Commit(seal);
                _seals[path] = seal.Ref;
            }
            else
            {
                _seals.Remove(path);
            }

            if (isNew)
            {
                AppendIndex(m, path);
            }
            else if (rename)
            {
                _seals.Remove(existingPath);
                if (!moved)
                {
                    try { File.Delete(existingPath); } catch { }
                }
                if (!File.Exists(existingPath)) TryDelete(SidecarPathFor(existingPath));
                ReplaceIndexRow(Path.GetFileName(existingPath), m, path);
            }
            return path;
        }

        /// <summary>Where the seal of <paramref name="logPath"/> goes in Sidecar mode: same name, ".seal".</summary>
        public static string SidecarPathFor(string logPath)
        {
            return Path.ChangeExtension(logPath, LogSealer.SidecarExtension);
        }

        /// <summary>Where a deck goes: decks\&lt;its SHA-256, lowercase hex&gt;.deck in the logs folder.</summary>
        public static string DeckPathFor(string logsFolder, string deckSha256)
        {
            return Path.Combine(logsFolder, DecksFolder, deckSha256 + DeckList.Extension);
        }

        /// <summary>
        /// The deck's file, written unless the same deck is already there (decks are never deleted:
        /// other logs may name them). Returns the bytes the seal names, or null when it cannot be saved.
        /// </summary>
        private byte[] SaveDeck(byte[] deck)
        {
            try
            {
                var path = DeckPathFor(Folder, SigningIdentity.Hex(SigningIdentity.Sha256(deck)));
                if (File.Exists(path))
                {
                    var on = File.ReadAllBytes(path);
                    if (on.Length == deck.Length && SigningIdentity.Hex(SigningIdentity.Sha256(on)) == Path.GetFileNameWithoutExtension(path)) return deck;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                WriteAtomic(path, deck);     // new, or a damaged copy replaced
                return deck;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string ResultLabel(MatchRecord m)
        {
            if (m.ManualOnly) return "EXPORTED";
            return m.Won == true ? "VICTORY" : m.Won == false ? "DEFEAT" : "NO-RESULT";
        }

        /// <summary>"2026-10-19 21.34.05 - VICTORY vs Gary.txt", local time of the match end.</summary>
        public static string FileNameFor(MatchRecord m)
        {
            // Invariant culture: a Thai or Saudi Windows would otherwise write its own calendar's year.
            var prefix = LocalTime(m).ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + " - " + ResultLabel(m);
            var opponent = m.OpponentName == null ? "" : m.OpponentName.Trim();
            var stem = Sanitize(opponent.Length > 0 ? prefix + " vs " + opponent : prefix);
            // The tray app reads the name back (LogFileName) for its notification. An opponent that
            // sanitizes to nothing ("...", or only dots and spaces where a long name is cut) must not
            // leave a dangling " vs".
            if (stem.Length <= prefix.Length + 4) stem = prefix;
            return stem + ".txt";
        }

        /// <summary>
        /// Removes what 1.0 left in the logs folder: the Spanish estado.txt (replaced by
        /// plugin-status.json) and the Spanish index.csv header and result labels, so old and new
        /// rows read as one table. Best effort; anything locked is retried on the next start.
        /// </summary>
        public void MigrateLegacy(Action<string> log)
        {
            log = log ?? (_ => { });
            try
            {
                var status = Path.Combine(Folder, LegacyStatusFile);
                if (File.Exists(status))
                {
                    File.Delete(status);
                    log("Removed " + LegacyStatusFile + " left by version 1.0.");
                }
            }
            catch (Exception e) { log("Could not remove " + LegacyStatusFile + ": " + e.Message); }

            try
            {
                var index = Path.Combine(Folder, IndexFile);
                if (!File.Exists(index)) return;
                bool legacy;
                using (var r = new StreamReader(index, Encoding.UTF8, true)) legacy = r.ReadLine() == LegacyIndexHeader;
                if (!legacy) return;
                RewriteIndex(record => record == LegacyIndexHeader ? IndexHeader : TranslateLegacyLabel(record));
                log(IndexFile + " converted from version 1.0 (Spanish) to English.");
            }
            catch (Exception e) { log("Could not convert " + IndexFile + " from version 1.0: " + e.Message); }
        }

        /// <summary>1.0 rows start with an unquoted date and label: "2026-10-19 21:34:05,VICTORIA,...".</summary>
        private static string TranslateLegacyLabel(string record)
        {
            int a = record.IndexOf(',');
            int b = a < 0 ? -1 : record.IndexOf(',', a + 1);
            if (b < 0) return record;
            var label = record.Substring(a + 1, b - a - 1);
            for (int i = 0; i < LegacyLabels.Length; i += 2)
                if (label == LegacyLabels[i])
                    return record.Substring(0, a + 1) + LegacyLabels[i + 1] + record.Substring(b);
            return record;
        }

        private string UniquePath(string fileName)
        {
            var path = Path.Combine(Folder, fileName);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(Folder, stem + " (" + i + ").txt");
            return path;
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="fileName"/> or one of its " (n)" duplicates.</summary>
        private static bool IsNamed(string path, string fileName)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            var stem = Path.GetFileNameWithoutExtension(fileName) + " (";
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(").txt", StringComparison.OrdinalIgnoreCase)) return false;
            var n = name.Substring(stem.Length, name.Length - stem.Length - 5);
            int unused;
            return n.Length > 0 && int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out unused);
        }

        // ------------------------------------------------------------------ index.csv

        private void AppendIndex(MatchRecord m, string path)
        {
            // Excel may hold index.csv open; a missed row is better than a lost log.
            try
            {
                using (var fs = new FileStream(Path.Combine(Folder, IndexFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    string prefix;
                    if (fs.Length <= 3)
                    {
                        // New, emptied, or only a BOM: start over with BOM + header.
                        fs.SetLength(0);
                        prefix = "\uFEFF" + IndexHeader + "\r\n";
                    }
                    else
                    {
                        // Edited by hand and saved without a final line break: do not glue our row onto it.
                        fs.Seek(-1, SeekOrigin.End);
                        prefix = fs.ReadByte() == '\n' ? "" : "\r\n";
                    }
                    var bytes = Utf8NoBom.GetBytes(prefix + Row(m, path) + "\r\n");
                    fs.Seek(0, SeekOrigin.End);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception) { }
        }

        /// <summary>Points the row of a renamed log file at its new name and details.</summary>
        private void ReplaceIndexRow(string oldFileName, MatchRecord m, string newPath)
        {
            var suffix = "," + Csv(oldFileName);
            bool found = false;
            try
            {
                RewriteIndex(record =>
                {
                    if (found || !record.EndsWith(suffix, StringComparison.Ordinal)) return record;
                    found = true;
                    return Row(m, newPath);
                });
            }
            catch (Exception) { }
            if (!found) AppendIndex(m, newPath);
        }

        /// <summary>
        /// Rewrites index.csv record by record. A record is not always a line: a quoted field
        /// may hold a line break, so line breaks only count outside quotes.
        /// </summary>
        private void RewriteIndex(Func<string, string> map)
        {
            var index = Path.Combine(Folder, IndexFile);
            if (!File.Exists(index)) return;
            var text = File.ReadAllText(index, Encoding.UTF8);     // drops the BOM
            var sb = new StringBuilder(text.Length + 256);
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"') quoted = !quoted;
                else if (text[i] == '\n' && !quoted)
                {
                    sb.Append(map(text.Substring(start, i - start).TrimEnd('\r'))).Append("\r\n");
                    start = i + 1;
                }
            }
            if (start < text.Length) sb.Append(map(text.Substring(start).TrimEnd('\r'))).Append("\r\n");
            WriteAtomic(index, sb.ToString(), Utf8Bom);
        }

        private static string Row(MatchRecord m, string path)
        {
            var minutes = m.EndUtc.HasValue && !m.ManualOnly
                ? Math.Round((m.EndUtc.Value - m.StartUtc).TotalMinutes, 1).ToString(CultureInfo.InvariantCulture)
                : "";
            return new StringBuilder()
                .Append(Csv(LocalTime(m).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(ResultLabel(m))).Append(',')
                .Append(Csv(m.OpponentName)).Append(',')
                .Append(Csv(m.MyName)).Append(',')
                .Append(Csv(m.MyDeck)).Append(',')
                .Append(Csv(minutes)).Append(',')
                .Append(Csv(m.MatchId)).Append(',')
                .Append(Csv(Path.GetFileName(path))).ToString();
        }

        private static string Csv(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        // ------------------------------------------------------------------ helpers

        private static DateTime LocalTime(MatchRecord m)
        {
            var t = m.EndUtc ?? m.StartUtc;
            return t.Kind == DateTimeKind.Local ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
        }

        public static string Sanitize(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                // Half a surrogate pair is legal in NTFS names but not in UTF-8, so index.csv
                // could not name the file.
                bool lone = char.IsHighSurrogate(c) ? i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1])
                          : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(name[i - 1]));
                sb.Append(lone || Array.IndexOf(bad, c) >= 0 || c == ':' || c == '\\' || c == '/' || c == '*' ||
                          c == '?' || c == '"' || c == '<' || c == '>' || c == '|' || c < 32 ? '_' : c);
            }
            var s = sb.ToString().Trim();
            if (s.Length > MaxStemLength)
            {
                int cut = MaxStemLength;
                if (char.IsHighSurrogate(s[cut - 1])) cut--;     // never split an emoji in two
                s = s.Substring(0, cut);
            }
            return s.TrimEnd(' ', '.');
        }

        /// <summary>
        /// Write beside, then swap in: a crash mid-write never leaves a truncated file, and readers
        /// (the tray app, a text editor) never see a half-written one.
        /// </summary>
        internal static void WriteAtomic(string path, string text, Encoding encoding)
        {
            var bytes = encoding.GetBytes(text);
            WriteAtomic(path, Concat(encoding.GetPreamble(), bytes, new byte[0]));
        }

        internal static void WriteAtomic(string path, byte[] bytes)
        {
            var tmp = path + ".tmp";
            try { File.WriteAllBytes(tmp, bytes); }
            catch
            {
                TryDelete(tmp);
                throw;
            }
            bool oldDeleted = false;
            try
            {
                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    return;
                }
                try
                {
                    ReplaceFile(tmp, path);
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException || e is PlatformNotSupportedException) && File.Exists(tmp))
                {
                    // ReplaceFile refuses some cases a delete + rename allows (network drives, antivirus),
                    // like the tray's FileOps.ReplaceWith. Falls through.
                }
                File.Delete(path);
                oldDeleted = true;
                File.Move(tmp, path);
            }
            catch
            {
                // Once the old file is deleted and the rename failed, the .tmp holds the only copy of
                // the text: kept (the next write of the same file picks it up). Otherwise a leftover.
                if (!oldDeleted) TryDelete(tmp);
                throw;
            }
        }

        private static byte[] Concat(byte[] a, byte[] b, byte[] c)
        {
            var all = new byte[a.Length + b.Length + c.Length];
            Buffer.BlockCopy(a, 0, all, 0, a.Length);
            Buffer.BlockCopy(b, 0, all, a.Length, b.Length);
            Buffer.BlockCopy(c, 0, all, a.Length + b.Length, c.Length);
            return all;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
