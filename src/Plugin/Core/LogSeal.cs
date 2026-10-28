using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>Where the seal of a log goes. BepInEx cfg [Metadata] Mode.</summary>
    public enum SealMode
    {
        /// <summary>Appended to the .txt, after the exact Export text.</summary>
        Footer,
        /// <summary>In a .seal file next to the .txt, which then holds only the Export text.</summary>
        Sidecar,
        /// <summary>No seal; the chain does not advance.</summary>
        Off,
    }

    /// <summary>A seal's place in the chain: what re-sealing the same file needs to know.</summary>
    public sealed class SealRef
    {
        public long Seq;
        /// <summary>Chain hash of the seal before it; null for seq 1.</summary>
        public string Prev;
        /// <summary>Chain hash of this seal.</summary>
        public string Hash;
    }

    /// <summary>A signed seal, ready to be written with its log.</summary>
    public sealed class PreparedSeal
    {
        public string Json;
        public string Signature;
        public SealRef Ref;

        /// <summary>From "-----BEGIN" to the final line break; ASCII.</summary>
        public string Block
        {
            get { return LogSealer.Begin + "\n" + Json + "\n" + Signature + "\n" + LogSealer.End + "\n"; }
        }
    }

    /// <summary>
    /// Seals battle logs: one line of metadata JSON about the match and the log, signed with the
    /// per-user key, chained to the previous seal. A leaderboard can then tell a log that was
    /// edited after it was saved, and see gaps and forks in a player's history. What it cannot
    /// prove is in docs/LOG-SEAL.md; the format is the contract in docs/ARCHITECTURE.md.
    ///
    /// Sealing never costs a log: whatever fails here, the caller saves the log without a seal and
    /// <see cref="Warning"/> says why.
    /// </summary>
    public sealed class LogSealer
    {
        /// <summary>The seal format the plugin writes (docs/LOG-SEAL.md); v1 seals from earlier builds are still read.</summary>
        public const int Version = 2;
        public const string Begin = "-----BEGIN PTCGL-ABL SEAL v2-----";
        public const string End = "-----END PTCGL-ABL SEAL v2-----";
        public const string BeginV1 = "-----BEGIN PTCGL-ABL SEAL v1-----";
        public const string EndV1 = "-----END PTCGL-ABL SEAL v1-----";
        /// <summary>
        /// Starts a footer seal of any version. The log is everything before its last occurrence;
        /// the BEGIN line then says the version.
        /// </summary>
        public const string FooterPrefix = "\n\n-----BEGIN PTCGL-ABL SEAL v";
        public const string SidecarExtension = ".seal";
        public const string AppName = "PTCGL Auto Battle Log";
        public const string CaptureAuto = "auto", CaptureExport = "export";
        /// <summary>accountHash = SHA-256 of this prefix + the account id: the same in both players' seals, never the raw id.</summary>
        public const string AccountHashPrefix = "ptcgl-abl:v1:";

        private readonly SigningIdentity _identity;
        private readonly string _pluginVersion, _gameVersion;
        private readonly Action<string> _warn;
        private string _failure, _lastWarned;

        /// <summary>Read at every save, so a change made while the game runs applies to the next log.</summary>
        public Func<SealMode> Mode = () => SealMode.Footer;
        public Func<DateTime> UtcNow = () => DateTime.UtcNow;
        /// <summary>How long a save waits for a key that is still being created before it goes unsealed.</summary>
        public TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);
        /// <summary>Where the identity looks for seals if it has to rebuild chain.json (the logs folder).</summary>
        public string RecoveryFolder;

        public LogSealer(SigningIdentity identity, string pluginVersion, string gameVersion, Action<string> warn = null)
        {
            _identity = identity;
            _pluginVersion = pluginVersion;
            _gameVersion = string.IsNullOrEmpty(gameVersion) ? null : gameVersion;
            _warn = warn ?? (_ => { });
        }

        public SigningIdentity Identity { get { return _identity; } }

        public SealMode CurrentMode()
        {
            try
            {
                var m = Mode == null ? SealMode.Footer : Mode();
                return m == SealMode.Sidecar || m == SealMode.Off ? m : SealMode.Footer;
            }
            catch { return SealMode.Footer; }
        }

        /// <summary>
        /// Why logs are being saved without a seal, for plugin-status.json; null while sealing works,
        /// before anything was tried, and when sealing is off.
        /// </summary>
        public string Warning
        {
            get
            {
                if (CurrentMode() == SealMode.Off) return null;
                if (_failure != null) return _failure;
                if (_identity == null) return "Battle logs are saved without a seal: there is no folder for the signing key.";
                var error = _identity.Error;
                return error == null ? null : "Battle logs are saved without a seal: " + error + ".";
            }
        }

        /// <summary>
        /// Builds and signs the seal of one write of a log. <paramref name="previous"/> is the seal
        /// the same file carried until now, if any: when it is still the latest seal of the chain,
        /// the new one takes its place (same seq and prev); otherwise it takes the next seq.
        /// <paramref name="deck"/> is the .deck file written with the log, or null.
        /// Returns null when the log has to be saved without a seal. Never throws.
        /// </summary>
        public PreparedSeal TryPrepare(MatchRecord m, string text, byte[] logUtf8, byte[] deck, SealRef previous)
        {
            try
            {
                if (_identity == null) return Fail("there is no folder for the signing key");
                if (!_identity.Started) _identity.BeginLoad(RecoveryFolder);
                if (!_identity.WaitReady(ReadyTimeout)) return Fail(_identity.Error ?? "the signing key is still being created");

                long chainSeq; string chainLast;
                _identity.GetChain(out chainSeq, out chainLast);
                long seq; string prev;
                if (previous != null && previous.Hash != null && previous.Hash == chainLast) { seq = previous.Seq; prev = previous.Prev; }
                else { seq = chainSeq + 1; prev = chainLast; }

                var json = BuildJson(m, text, logUtf8, deck, seq, prev, UtcNow());
                var signature = Convert.ToBase64String(_identity.Sign(Encoding.ASCII.GetBytes(json)));
                return new PreparedSeal
                {
                    Json = json,
                    Signature = signature,
                    Ref = new SealRef { Seq = seq, Prev = prev, Hash = ChainHash(json, signature) },
                };
            }
            catch (Exception e)
            {
                return Fail("signing failed (" + e.Message + ")");
            }
        }

        /// <summary>The seal is on disk with its log: the chain moves on. Never throws.</summary>
        public void Commit(PreparedSeal seal)
        {
            _failure = null;
            _lastWarned = null;
            _identity.Commit(seal.Ref.Seq, seal.Ref.Hash);
        }

        /// <summary>A prepared seal could not be written (e.g. its sidecar): the log stays unsealed.</summary>
        public void Abandon(string why)
        {
            Fail(why);
        }

        private PreparedSeal Fail(string why)
        {
            _failure = "The last battle log was saved without a seal: " + why + ".";
            if (_failure != _lastWarned) _warn(_failure);
            _lastWarned = _failure;
            return null;
        }

        // ------------------------------------------------------------------ format

        /// <summary>
        /// The metadata line. Keys, their order and the formats are the contract (docs/ARCHITECTURE.md,
        /// "Sealed logs"); verifiers check the signature over these exact bytes, so nothing here may
        /// depend on the culture. ASCII only: everything else is \u-escaped.
        /// </summary>
        internal string BuildJson(MatchRecord m, string text, byte[] logUtf8, byte[] deck, long seq, string prev, DateTime sealedUtc)
        {
            var sb = new StringBuilder(1300);
            sb.Append("{\"v\":").Append(Version.ToString(CultureInfo.InvariantCulture));
            Field(sb, "app", Quote(AppName));
            Field(sb, "pluginVersion", Quote(_pluginVersion));
            Field(sb, "gameVersion", Quote(_gameVersion));
            Field(sb, "capture", Quote(m.ManualOnly || m.Capture == CaptureExport ? CaptureExport : CaptureAuto));
            Field(sb, "matchId", Quote(NullIfEmpty(m.MatchId)));
            Field(sb, "startedUtc", PluginStatusWriter.Date(m.StartUtc));
            Field(sb, "endedUtc", PluginStatusWriter.Date(m.EndUtc));
            Field(sb, "result", Quote(LogStore.ResultLabel(m)));
            Field(sb, "winner", Seat(m.Winner));
            Field(sb, "mySeat", Seat(m.MySeat));
            Field(sb, "player", Party(m.MyName, m.MyAccountId));
            Field(sb, "opponent", Party(m.OpponentName, m.OpponentAccountId));
            Field(sb, "deck", Quote(NullIfEmpty(m.MyDeck)));
            Field(sb, "deckSha256", Quote(deck == null ? null : SigningIdentity.Hex(SigningIdentity.Sha256(deck))));
            Field(sb, "queue", Quote(NullIfEmpty(m.Queue)));
            Field(sb, "format", Quote(NullIfEmpty(m.Format)));
            Field(sb, "logSha256", Quote(SigningIdentity.Hex(SigningIdentity.Sha256(logUtf8))));
            Field(sb, "logChars", text.Length.ToString(CultureInfo.InvariantCulture));
            Field(sb, "sealedUtc", PluginStatusWriter.Date(sealedUtc));
            Field(sb, "seq", seq.ToString(CultureInfo.InvariantCulture));
            Field(sb, "prev", Quote(prev));
            Field(sb, "keyId", Quote(_identity.KeyId));
            Field(sb, "publicKey", "{\"kty\":\"RSA\",\"n\":" + Quote(_identity.Modulus) + ",\"e\":" + Quote(_identity.Exponent) + "}");
            return sb.Append('}').ToString();
        }

        private static void Field(StringBuilder sb, string name, string json)
        {
            sb.Append(",\"").Append(name).Append("\":").Append(json);
        }

        private static string Party(string name, string accountId)
        {
            return "{\"name\":" + Quote(NullIfEmpty(name)) + ",\"accountHash\":" + Quote(AccountHash(accountId)) + "}";
        }

        private static string Seat(int? seat)
        {
            return seat == 1 || seat == 2 ? seat.Value.ToString(CultureInfo.InvariantCulture) : "null";
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>Lowercase hex SHA-256 of "ptcgl-abl:v1:" + id (UTF-8); null when the id is unknown.</summary>
        public static string AccountHash(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return null;
            return SigningIdentity.Hex(SigningIdentity.Sha256(new UTF8Encoding(false).GetBytes(AccountHashPrefix + accountId)));
        }

        /// <summary>Lowercase hex SHA-256 of the JSON line + "\n" + the signature line.</summary>
        public static string ChainHash(string json, string signature)
        {
            return SigningIdentity.Hex(SigningIdentity.Sha256(Encoding.ASCII.GetBytes(json + "\n" + signature)));
        }

        /// <summary>
        /// A JSON string literal in pure ASCII, or null. Escapes '"', '\', control characters and
        /// everything from U+007F up (a pair of surrogates becomes two \u escapes, as JSON wants).
        /// </summary>
        internal static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ' || c >= '\u007f') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }

    /// <summary>
    /// Reads the seal of a saved file: enough to find a chain's position again (seq, prev, keyId,
    /// public key, chain hash). It does not verify anything: SigningIdentity checks the signature
    /// of a seal before it trusts its position, and tools/verify_log.py checks everything.
    /// </summary>
    public sealed class SealReader
    {
        /// <summary>
        /// A seal block is at most 16 KiB (docs/LOG-SEAL.md §2; a plugin's is about 1.5 KB), so the
        /// footer's separator lies within this tail. Reading only the tail keeps a folder scan cheap.
        /// </summary>
        private const int TailBytes = 20 * 1024;

        private static readonly Regex SeqField = new Regex(@"[{,]""seq"":(\d{1,18})[,}]");
        private static readonly Regex PrevField = new Regex(@"[{,]""prev"":(?:null|""([0-9a-f]{64})"")[,}]");
        private static readonly Regex KeyIdField = new Regex(@"[{,]""keyId"":""([A-Za-z0-9_-]+)""[,}]");
        private static readonly Regex PublicKeyField = new Regex(@",""publicKey"":\{""kty"":""RSA"",""n"":""([A-Za-z0-9_-]+)"",""e"":""([A-Za-z0-9_-]+)""\}\}$");

        public string Json;
        public string Signature;
        public long Seq;
        public string Prev;
        public string KeyId;
        /// <summary>The seal's publicKey n and e (base64url); null when the line has none in the v1/v2 form.</summary>
        public string Modulus, Exponent;
        public string Hash;

        /// <summary>The seal at the end of a .txt (footer), or in a .seal file. Null when none or unreadable.</summary>
        public static SealReader TryReadFile(string path)
        {
            try
            {
                byte[] tail;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var length = (int)Math.Min(fs.Length, TailBytes);
                    fs.Seek(-length, SeekOrigin.End);
                    tail = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = fs.Read(tail, read, length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                if (string.Equals(Path.GetExtension(path), LogSealer.SidecarExtension, StringComparison.OrdinalIgnoreCase))
                {
                    int start = tail.Length >= 3 && tail[0] == 0xEF && tail[1] == 0xBB && tail[2] == 0xBF ? 3 : 0;
                    return ParseBlock(Encoding.ASCII.GetString(tail, start, tail.Length - start));
                }
                var at = LastIndexOf(tail, Encoding.ASCII.GetBytes(LogSealer.FooterPrefix));
                if (at < 0) return null;
                return ParseBlock(Encoding.ASCII.GetString(tail, at + 2, tail.Length - at - 2));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>"-----BEGIN…-----\n{json}\n{signature}\n-----END…-----\n" exactly, v1 or v2; null otherwise.</summary>
        public static SealReader ParseBlock(string block)
        {
            if (block == null) return null;
            var lines = block.Split('\n');
            if (lines.Length != 5 || lines[4].Length != 0) return null;
            string version;
            if (lines[0] == LogSealer.Begin && lines[3] == LogSealer.End) version = "{\"v\":2,";
            else if (lines[0] == LogSealer.BeginV1 && lines[3] == LogSealer.EndV1) version = "{\"v\":1,";
            else return null;
            var json = lines[1];
            if (!json.StartsWith(version, StringComparison.Ordinal)) return null;
            var seq = SeqField.Match(json);
            var prev = PrevField.Match(json);
            var keyId = KeyIdField.Match(json);
            var publicKey = PublicKeyField.Match(json);
            long n;
            if (!seq.Success || !prev.Success || !keyId.Success || lines[2].Length == 0 ||
                !long.TryParse(seq.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null;
            return new SealReader
            {
                Json = json,
                Signature = lines[2],
                Seq = n,
                Prev = prev.Groups[1].Success ? prev.Groups[1].Value : null,
                KeyId = keyId.Groups[1].Value,
                Modulus = publicKey.Success ? publicKey.Groups[1].Value : null,
                Exponent = publicKey.Success ? publicKey.Groups[2].Value : null,
                Hash = LogSealer.ChainHash(json, lines[2]),
            };
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
