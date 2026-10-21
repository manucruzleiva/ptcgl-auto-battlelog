using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// The few seal fields the tray needs to offer a player's PTCGL accounts for a tcgkb claim: who
    /// played (player name and accountHash), the match, when it was sealed and by which key. It finds
    /// the seal as docs/LOG-SEAL.md §2 says (a footer in the .txt, else a .seal sidecar) and reads the
    /// metadata line in its canonical v1 form. It verifies nothing: tcgkb checks the signature of every
    /// log it is sent, so a forged or broken seal here only means a claim that tcgkb refuses.
    /// </summary>
    public sealed class SealSummary
    {
        public const string Begin = "-----BEGIN PTCGL-ABL SEAL v1-----";
        public const string End = "-----END PTCGL-ABL SEAL v1-----";
        public const string SidecarExtension = ".seal";
        private const string FooterSeparator = "\n\n" + Begin + "\n";

        /// <summary>A seal block is at most 16 KiB (docs/LOG-SEAL.md §2), so the footer lies within this tail.</summary>
        private const int TailBytes = 20 * 1024;

        private const string JsonString = @"""((?:[^""\\]|\\.)*)""";
        private static readonly Regex PlayerField = new Regex(@",""player"":\{""name"":(?:null|" + JsonString + @"),""accountHash"":(?:null|""([0-9a-f]{64})"")\}");
        private static readonly Regex MatchIdField = new Regex(@",""matchId"":(?:null|" + JsonString + ")");
        private static readonly Regex ResultField = new Regex(@",""result"":""(VICTORY|DEFEAT|NO-RESULT|EXPORTED)""");
        private static readonly Regex SealedField = new Regex(@",""sealedUtc"":""(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)""");
        private static readonly Regex KeyIdField = new Regex(@",""keyId"":""([A-Za-z0-9_-]{1,128})""");
        private static readonly Regex OpponentField = new Regex(@",""opponent"":\{""name"":(?:null|" + JsonString + @"),""accountHash"":");
        private static readonly Regex DeckField = new Regex(@",""deck"":(?:null|" + JsonString + ")");
        private static readonly Regex EndedField = new Regex(@",""endedUtc"":(?:null|""(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)"")");

        /// <summary>The .txt the seal belongs to, and its sidecar when the seal is in one.</summary>
        public string LogFile, SealFile;
        public string PlayerName, PlayerAccountHash;
        public string MatchId, Result, KeyId;
        /// <summary>The opponent's display name and the player's deck name; null when the seal has none.</summary>
        public string OpponentName, Deck;
        public DateTime SealedUtc;
        /// <summary>When the plugin saw the match end; null for EXPORTED.</summary>
        public DateTime? EndedUtc;

        /// <summary>
        /// The seal of a battle log .txt: its footer, else &lt;name&gt;.seal beside it. Null when the log is
        /// unsealed, the seal is not v1 or the file cannot be read. Never throws.
        /// </summary>
        public static SealSummary TryRead(string logFile)
        {
            try
            {
                var tail = ReadTail(logFile);
                var at = LastIndexOf(tail, Encoding.ASCII.GetBytes(FooterSeparator));
                if (at >= 0) return Parse(Encoding.ASCII.GetString(tail, at + 2, tail.Length - at - 2), logFile, null);

                var sidecar = Path.ChangeExtension(logFile, SidecarExtension);
                if (!File.Exists(sidecar)) return null;
                var block = ReadTail(sidecar);
                int start = block.Length >= 3 && block[0] == 0xEF && block[1] == 0xBB && block[2] == 0xBF ? 3 : 0;
                return Parse(Encoding.ASCII.GetString(block, start, block.Length - start), logFile, sidecar);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>"-----BEGIN…-----\n{json}\n{signature}\n-----END…-----\n" exactly, with the fields above; null otherwise.</summary>
        internal static SealSummary Parse(string block, string logFile, string sealFile)
        {
            var lines = block.Split('\n');
            if (lines.Length != 5 || lines[0] != Begin || lines[3] != End || lines[4].Length != 0 || lines[2].Length == 0) return null;
            var json = lines[1];
            if (!json.StartsWith("{\"v\":1,", StringComparison.Ordinal)) return null;

            var player = PlayerField.Match(json);
            var sealedUtc = SealedField.Match(json);
            var keyId = KeyIdField.Match(json);
            if (!player.Success || !sealedUtc.Success || !keyId.Success) return null;
            var matchId = MatchIdField.Match(json);
            var result = ResultField.Match(json);
            var opponent = OpponentField.Match(json);
            var deck = DeckField.Match(json);
            var ended = EndedField.Match(json);
            return new SealSummary
            {
                LogFile = logFile,
                SealFile = sealFile,
                PlayerName = player.Groups[1].Success ? Unescape(player.Groups[1].Value) : null,
                PlayerAccountHash = player.Groups[2].Success ? player.Groups[2].Value : null,
                MatchId = matchId.Success && matchId.Groups[1].Success ? Unescape(matchId.Groups[1].Value) : null,
                Result = result.Success ? result.Groups[1].Value : null,
                KeyId = keyId.Groups[1].Value,
                OpponentName = opponent.Success && opponent.Groups[1].Success ? Unescape(opponent.Groups[1].Value) : null,
                Deck = deck.Success && deck.Groups[1].Success ? Unescape(deck.Groups[1].Value) : null,
                SealedUtc = ParseUtc(sealedUtc.Groups[1].Value),
                EndedUtc = ended.Success && ended.Groups[1].Success ? ParseUtc(ended.Groups[1].Value) : (DateTime?)null,
            };
        }

        private static DateTime ParseUtc(string s) =>
            DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        /// <summary>The body of a JSON string literal as the seal writes it (ASCII, \u escapes) → its text. Null when malformed.</summary>
        internal static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= s.Length) return null;
                switch (s[i])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 >= s.Length ||
                            !int.TryParse(s.Substring(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) return null;
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: return null;
                }
            }
            return sb.ToString();
        }

        private static byte[] ReadTail(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var length = (int)Math.Min(fs.Length, TailBytes);
                fs.Seek(-length, SeekOrigin.End);
                var tail = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = fs.Read(tail, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < length) Array.Resize(ref tail, read);
                return tail;
            }
        }

        private static int LastIndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = haystack.Length - needle.Length; i >= 0; i--)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
