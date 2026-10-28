using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using PtcglAutoBattleLog.Core;
using _Rainier.Scripts.BattleLog;

// Sealed logs (docs/ARCHITECTURE.md "Sealed logs", docs/LOG-SEAL.md). Every seal written here is
// checked with code that shares nothing with the plugin's: System.Text.Json, RSA.Create() and the
// RFC 7638 thumbprint computed again below. tools/verify_log.py is the third, independent check
// (run on the samples written when PTCGL_SEAL_SAMPLES is set).
static partial class Program
{
    static readonly string[] SealKeys =
    {
        "v", "app", "pluginVersion", "gameVersion", "capture", "matchId", "startedUtc", "endedUtc", "result", "winner",
        "mySeat", "player", "opponent", "deck", "deckSha256", "logSha256", "logChars", "sealedUtc", "seq", "prev", "keyId", "publicKey",
    };

    const string Separator = "\n\n-----BEGIN PTCGL-ABL SEAL v2-----\n";

    // ================================================================== fixtures

    /// <summary>The tests' RSA: .NET 8's own, through the seam the plugin fills with RSACryptoServiceProvider.</summary>
    class PortableRsa : RsaBackend
    {
        public int Created;
        public int CreatedOnThread;
        public override RSA Create(int bits)
        {
            Created++;
            CreatedOnThread = Environment.CurrentManagedThreadId;
            return RSA.Create(bits);
        }
        public override RSA FromXml(string xml) { var r = RSA.Create(); r.FromXmlString(xml); return r; }
        public override byte[] SignHash(RSA key, byte[] h) => key.SignHash(h, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        public override bool VerifyHash(RSA key, byte[] h, byte[] s) => key.VerifyHash(h, s, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>Breaks on demand: a signer that throws, or one that returns garbage.</summary>
    sealed class BrokenRsa : PortableRsa
    {
        public bool Throw, Garbage;
        public override byte[] SignHash(RSA key, byte[] h)
        {
            if (Throw) throw new CryptographicException("the key store went away");
            var sig = base.SignHash(key, h);
            if (Garbage) sig[10] ^= 0x55;
            return sig;
        }
    }

    /// <summary>Key creation that waits for the test to let it finish.</summary>
    sealed class GatedRsa : PortableRsa
    {
        public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
        public override RSA Create(int bits) { Gate.Wait(TimeSpan.FromSeconds(30)); return base.Create(bits); }
    }

    sealed class Sealed
    {
        public byte[] Log;
        public string Json, Signature, Source;
        /// <summary>The deck file the seal names (decks\&lt;deckSha256&gt;.deck); null when it names none or it is missing.</summary>
        public byte[] Deck;
        public string Hash => Hex(SHA256.HashData(Encoding.ASCII.GetBytes(Json + "\n" + Signature)));
        public JsonElement Meta => JsonDocument.Parse(Json).RootElement;
        public long Seq => Meta.GetProperty("seq").GetInt64();
        public string Prev => Meta.GetProperty("prev").ValueKind == JsonValueKind.Null ? null : Meta.GetProperty("prev").GetString();
        public string Str(string name) => Meta.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Meta.GetProperty(name).GetString();
    }

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    static string ToB64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A log and its seal as the contract lays them out (footer, else sidecar); null when unsealed.</summary>
    static Sealed ReadSealed(string txt)
    {
        var data = File.ReadAllBytes(txt);
        int start = HasBom(data) ? 3 : 0;
        var sep = Encoding.ASCII.GetBytes(Separator);
        int at = -1;
        for (int i = data.Length - sep.Length; i >= start && at < 0; i--)
            if (data.AsSpan(i, sep.Length).SequenceEqual(sep)) at = i;
        byte[] log; string block; string source;
        if (at >= 0)
        {
            log = data[start..at];
            block = Encoding.ASCII.GetString(data, at + 2, data.Length - at - 2);
            source = "footer";
        }
        else
        {
            var side = Path.ChangeExtension(txt, ".seal");
            if (!File.Exists(side)) return null;
            log = data[start..];
            block = Encoding.ASCII.GetString(File.ReadAllBytes(side));
            source = "sidecar";
        }
        var lines = block.Split('\n');
        if (lines.Length != 5 || lines[0] != "-----BEGIN PTCGL-ABL SEAL v2-----" || lines[3] != "-----END PTCGL-ABL SEAL v2-----" || lines[4] != "")
            throw new FormatException("malformed seal block in " + txt);
        var named = System.Text.RegularExpressions.Regex.Match(lines[1], "\"deckSha256\":\"([0-9a-f]{64})\"");
        var deck = named.Success ? LogStore.DeckPathFor(Path.GetDirectoryName(txt), named.Groups[1].Value) : null;
        return new Sealed { Log = log, Json = lines[1], Signature = lines[2], Source = source, Deck = deck != null && File.Exists(deck) ? File.ReadAllBytes(deck) : null };
    }

    /// <summary>What is wrong with a seal, checked independently of the plugin's code; null when valid.</summary>
    static string Problem(Sealed s)
    {
        if (s == null) return "no seal";
        using var doc = JsonDocument.Parse(s.Json);
        var root = doc.RootElement;
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (!names.SequenceEqual(SealKeys)) return "keys: " + string.Join(",", names);
        var pk = root.GetProperty("publicKey");
        var n = pk.GetProperty("n").GetString();
        var e = pk.GetProperty("e").GetString();
        var thumb = ToB64Url(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["e"] = e, ["kty"] = "RSA", ["n"] = n }))));
        if (root.GetProperty("keyId").GetString() != thumb) return "keyId is not the RFC 7638 thumbprint";
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(new RSAParameters { Modulus = B64Url(n), Exponent = B64Url(e) });
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(s.Json), Convert.FromBase64String(s.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return "signature";
        }
        if (Hex(SHA256.HashData(s.Log)) != root.GetProperty("logSha256").GetString()) return "logSha256";
        if (Encoding.UTF8.GetString(s.Log).Length != root.GetProperty("logChars").GetInt32()) return "logChars";
        if (root.GetProperty("v").GetInt32() != 2) return "v";
        var deckSha = root.GetProperty("deckSha256");
        if (deckSha.ValueKind != JsonValueKind.Null && (s.Deck == null || Hex(SHA256.HashData(s.Deck)) != deckSha.GetString()))
            return "deckSha256";
        return null;
    }

    static string ChainJson(string identityDir) => File.ReadAllText(Path.Combine(identityDir, SigningIdentity.ChainFileName));

    /// <summary>A loaded identity of its own, in a temp folder.</summary>
    static SigningIdentity NewIdentity(out string dir, RsaBackend backend = null, string logsFolder = null)
    {
        dir = TempDir();
        var id = new SigningIdentity(dir, backend ?? new PortableRsa(), m => Console.WriteLine("    [key] " + m));
        id.Load(logsFolder);
        return id;
    }

    static LogSealer NewSealer(SigningIdentity id, SealMode mode = SealMode.Footer)
    {
        return new LogSealer(id, "1.1.0", "1.24.0", m => Console.WriteLine("    [seal] " + m)) { Mode = () => mode, ReadyTimeout = TimeSpan.FromSeconds(30) };
    }

    static MatchRecord Match(string id, string opponent = "Gary", bool? won = true)
    {
        var start = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc);
        return new MatchRecord
        {
            MatchId = id, StartUtc = start, EndUtc = start.AddMinutes(12), Won = won, Winner = won == null ? null : won == true ? 1 : 2,
            MySeat = 1, MyName = "Ash", OpponentName = opponent, MyDeck = "Charizard ex",
            MyAccountId = "me-123", OpponentAccountId = "opp-9", Capture = LogSealer.CaptureAuto,
        };
    }

    static void Cleanup(params string[] dirs)
    {
        foreach (var d in dirs) try { Directory.Delete(d, true); } catch { }
    }

    // ================================================================== format

    static void SealFormat()
    {
        Console.WriteLine("\n28. a footer seal, byte for byte");
        var id = NewIdentity(out var idDir);
        var dir = TempDir();
        var store = new LogStore(dir) { Sealer = NewSealer(id) };
        store.Sealer.UtcNow = () => new DateTime(2026, 10, 19, 21, 12, 9, DateTimeKind.Utc);
        var text = "Setup\r\nTurn 1 - Ash played Charmander\r\nAsh wins.";
        var path = store.Save(Match("m-1"), text, null);
        var bytes = File.ReadAllBytes(path);
        var s = ReadSealed(path);
        Check(s != null && s.Source == "footer", "the .txt carries a footer");
        Check(HasBom(bytes) && Encoding.UTF8.GetString(s.Log) == text, "the log part is the exact Export text, after the BOM");
        var expected = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text + Separator + s.Json + "\n" + s.Signature + "\n-----END PTCGL-ABL SEAL v2-----\n")).ToArray();
        Check(bytes.SequenceEqual(expected), "file = BOM + log + \\n\\n + BEGIN + JSON + signature + END, each line ending in \\n");
        Check(Problem(s) == null, "signature, keyId, logSha256 and logChars verify independently (" + Problem(s) + ")");
        Check(!File.Exists(LogStore.SidecarPathFor(path)), "no sidecar in Footer mode");

        var m = s.Meta;
        Check(m.EnumerateObject().Select(p => p.Name).SequenceEqual(SealKeys), "keys in the contract's order");
        Check(m.GetProperty("player").EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "name", "accountHash" }) &&
              m.GetProperty("publicKey").EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "kty", "n", "e" }), "nested keys in order");
        Check(m.GetProperty("v").GetInt32() == 2 && s.Str("deckSha256") == null && s.Str("app") == "PTCGL Auto Battle Log" && s.Str("pluginVersion") == "1.1.0" &&
              s.Str("gameVersion") == "1.24.0" && s.Str("capture") == "auto" && s.Str("matchId") == "m-1", "v 2, no deck, app, versions, capture, matchId");
        Check(s.Str("startedUtc") == "2026-10-19T21:00:00Z" && s.Str("endedUtc") == "2026-10-19T21:12:00Z" && s.Str("sealedUtc") == "2026-10-19T21:12:09Z", "dates in UTC, yyyy-MM-ddTHH:mm:ssZ");
        Check(s.Str("result") == "VICTORY" && m.GetProperty("winner").GetInt32() == 1 && m.GetProperty("mySeat").GetInt32() == 1, "result, winner, mySeat");
        var meHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes("ptcgl-abl:v1:me-123")));
        Check(m.GetProperty("player").GetProperty("name").GetString() == "Ash" && m.GetProperty("player").GetProperty("accountHash").GetString() == meHash,
              "player name and accountHash = sha256(\"ptcgl-abl:v1:\" + id)");
        Check(m.GetProperty("opponent").GetProperty("accountHash").GetString() == Hex(SHA256.HashData(Encoding.UTF8.GetBytes("ptcgl-abl:v1:opp-9"))), "opponent accountHash");
        Check(s.Str("deck") == "Charizard ex" && m.GetProperty("logChars").GetInt32() == text.Length, "deck and logChars");
        Check(s.Seq == 1 && s.Prev == null, "first seal: seq 1, prev null");
        Check(s.Str("keyId") == id.KeyId && m.GetProperty("publicKey").GetProperty("e").GetString() == "AQAB" &&
              B64Url(m.GetProperty("publicKey").GetProperty("n").GetString()).Length == 256, "keyId and a 2048-bit public key, e = AQAB");
        Check(!m.GetProperty("publicKey").GetProperty("n").GetString().Contains('='), "base64url without padding");
        Check(Convert.FromBase64String(s.Signature).Length == 256 && !s.Signature.Contains('-'), "signature: standard base64 of 256 bytes");
        Check(ChainJson(idDir) == "{\"seq\":1,\"last\":\"" + s.Hash + "\",\"keyId\":\"" + id.KeyId + "\"}", "chain.json = {\"seq\":1,\"last\":sha256(json \\n signature),\"keyId\":...}");

        Console.WriteLine("29. tampering is detected");
        var tampered = ReadSealed(path);
        tampered.Log = Encoding.UTF8.GetBytes(text.Replace("Ash wins.", "Ash wins!"));
        Check(Problem(tampered) == "logSha256", "an edited log fails its logSha256");
        tampered = ReadSealed(path);
        tampered.Json = tampered.Json.Replace("\"VICTORY\"", "\"DEFEAT\"");
        Check(Problem(tampered) == "signature", "edited metadata fails the signature");
        tampered = ReadSealed(path);
        var otherId = NewIdentity(out var otherDir);
        tampered.Json = tampered.Json.Replace(id.KeyId, otherId.KeyId).Replace(id.Modulus, otherId.Modulus);
        Check(Problem(tampered) == "signature", "a seal re-labelled with another key fails");

        Console.WriteLine("30. JSON escaping, ASCII only, any culture");
        var nasty = "Zo\u00eb \"Q\" back\\slash/ \t\r\n" + (char)1 + (char)0x1F + (char)0x7F + " \u65e5\u672c\u8a9e " + char.ConvertFromUtf32(0x1F525) + " \u2028";
        var r = Match("m-\"2\"", opponent: nasty);
        r.MyDeck = "Deck \\ \"x\"";
        var p2 = store.Save(r, "log with " + nasty, null);
        var s2 = ReadSealed(p2);
        Check(s2.Json.All(c => c >= 0x20 && c < 0x7F), "the JSON line is printable ASCII");
        Check(s2.Meta.GetProperty("opponent").GetProperty("name").GetString() == nasty && s2.Str("matchId") == "m-\"2\"" && s2.Str("deck") == "Deck \\ \"x\"",
              "quotes, backslashes, control characters, non-ASCII and emoji round-trip");
        Check(s2.Json.Contains("\\u00eb") && s2.Json.Contains("\\ud83d\\udd25") && s2.Json.Contains("\\u007f") && s2.Json.Contains("\\u2028"), "escaped as \\uXXXX, lowercase");
        Check(Problem(s2) == null, "and it verifies (" + Problem(s2) + ")");
        Check(LogSealer.Quote("a" + (char)0xD800 + "b") == "\"a\\ud800b\"", "a lone surrogate is escaped, not mangled");
        Check(LogSealer.Quote(null) == "null", "null -> null");
        var plain = Match("m-3");
        var log = Encoding.UTF8.GetBytes("x");
        var sealer = store.Sealer;
        var saved = CultureInfo.CurrentCulture;
        string invariantJson, thaiJson;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        invariantJson = sealer.BuildJson(plain, "x", log, null, 12345, new string('a', 64), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        try
        {
            var thai = (CultureInfo)new CultureInfo("th-TH").Clone();
            thai.NumberFormat.NegativeSign = "~";
            thai.NumberFormat.NativeDigits = new[] { "๐", "๑", "๒", "๓", "๔", "๕", "๖", "๗", "๘", "๙" };
            CultureInfo.CurrentCulture = thai;
            thaiJson = sealer.BuildJson(plain, "x", log, null, 12345, new string('a', 64), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        }
        finally { CultureInfo.CurrentCulture = saved; }
        Check(thaiJson == invariantJson && invariantJson.Contains("\"sealedUtc\":\"2026-01-02T03:04:05Z\"") && invariantJson.Contains("\"seq\":12345"),
              "the JSON does not depend on the current culture");

        var manual = new MatchRecord { StartUtc = new DateTime(2026, 10, 19, 22, 0, 0, DateTimeKind.Utc), ManualOnly = true };
        var p3 = store.Save(manual, "an old replay", null);
        var s3 = ReadSealed(p3);
        Check(s3.Str("capture") == "export" && s3.Str("result") == "EXPORTED" && s3.Str("matchId") == null && s3.Str("endedUtc") == null &&
              s3.Meta.GetProperty("winner").ValueKind == JsonValueKind.Null && s3.Meta.GetProperty("mySeat").ValueKind == JsonValueKind.Null &&
              s3.Meta.GetProperty("player").GetProperty("name").ValueKind == JsonValueKind.Null &&
              s3.Meta.GetProperty("player").GetProperty("accountHash").ValueKind == JsonValueKind.Null && s3.Str("deck") == null,
              "an EXPORTED orphan: capture export, unknown values are null");
        Check(Problem(s3) == null, "and it verifies");
        Check(LogSealer.AccountHash(null) == null && LogSealer.AccountHash("") == null, "accountHash of an unknown id is null");
        Cleanup(dir, idDir, otherDir);
    }

    // ================================================================== chain

    static void SealChain()
    {
        Console.WriteLine("\n31. the chain: re-seal in place, next seal, out-of-order re-seal");
        var id = NewIdentity(out var idDir);
        var dir = TempDir();
        var store = new LogStore(dir) { Sealer = NewSealer(id) };
        var a = Match("m-a", "Gary");
        var pa = store.Save(a, "match a", null);
        var a1 = ReadSealed(pa);
        var pa2 = store.Save(a, "match a, longer read", pa);
        var a2 = ReadSealed(pa2);
        Check(pa2 == pa && a2.Seq == 1 && a2.Prev == null && a2.Hash != a1.Hash, "a longer read of the latest seal keeps seq 1 and prev");
        Check(Encoding.UTF8.GetString(a2.Log) == "match a, longer read" && Problem(a2) == null, "the file holds the new log and one seal, which verifies");
        Check(ChainJson(idDir) == SigningIdentity.ChainText(1, a2.Hash, id.KeyId), "chain.json points at the new seal");

        var b = Match("m-b", "Misty", won: false);
        var pb = store.Save(b, "match b", null);
        var b1 = ReadSealed(pb);
        Check(b1.Seq == 2 && b1.Prev == a2.Hash, "the next log: seq 2, prev = chain hash of seq 1");

        var pa3 = store.Save(a, "match a, even longer", pa2);
        var a3 = ReadSealed(pa3);
        Check(a3.Seq == 3 && a3.Prev == b1.Hash, "re-sealing a log that is no longer the latest takes the next seq (gap at 1, explained in LOG-SEAL.md)");
        Check(ChainJson(idDir) == SigningIdentity.ChainText(3, a3.Hash, id.KeyId) && Problem(a3) == null, "chain.json at seq 3");

        Console.WriteLine("32. a re-seal that moves the file (late winner)");
        var c = Match("m-c", "Brock", won: null);
        var pc = store.Save(c, "match c", null);
        var c1 = ReadSealed(pc);
        c.Won = true; c.Winner = 1;
        var pc2 = store.Save(c, "match c", pc);
        var c2 = ReadSealed(pc2);
        Check(pc2 != pc && !File.Exists(pc) && pc2.EndsWith(" - VICTORY vs Brock.txt"), "moved to its corrected name, old name gone");
        Check(c2.Seq == c1.Seq && c2.Prev == c1.Prev && c2.Str("result") == "VICTORY" && Problem(c2) == null, "same place in the chain, new result, verifies");
        var rows = File.ReadAllLines(Path.Combine(dir, LogStore.IndexFile));
        Check(rows.Length == 4 && rows.Count(x => x.EndsWith("," + Path.GetFileName(pc2))) == 1 && !rows.Any(x => x.EndsWith("," + Path.GetFileName(pc))), "index row follows the move");

        Console.WriteLine("33. the old file cannot be moved (locked): no fork");
        var d = Match("m-d", "Erika", won: null);
        var pd = store.Save(d, "match d", null);
        var d1 = ReadSealed(pd);
        d.Won = false; d.Winner = 2;
        store.MoveFile = (from, to) => throw new IOException("The process cannot access the file because it is being used by another process.");
        var pd2 = store.Save(d, "match d", pd);
        store.MoveFile = File.Move;
        var d2 = ReadSealed(pd2);
        Check(pd2 != pd && d2.Seq == d1.Seq + 1 && d2.Prev == d1.Hash && Problem(d2) == null,
              "the new file takes the next seq after the old seal, so the two never claim one place (seq " + d2.Seq + ")");
        Cleanup(dir, idDir);
    }

    // ================================================================== modes

    static void SealModes()
    {
        Console.WriteLine("\n34. Sidecar, then Footer, then Off");
        var id = NewIdentity(out var idDir);
        var dir = TempDir();
        var mode = SealMode.Sidecar;
        var store = new LogStore(dir) { Sealer = NewSealer(id) };
        store.Sealer.Mode = () => mode;
        var m = Match("m-s");
        var p = store.Save(m, "sidecar log", null);
        var side = LogStore.SidecarPathFor(p);
        Check(File.ReadAllBytes(p).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("sidecar log"))), "the .txt holds only the Export text");
        Check(side == p.Substring(0, p.Length - 4) + ".seal" && File.Exists(side), "the seal is in <same name>.seal");
        var sb = File.ReadAllBytes(side);
        Check(!HasBom(sb) && sb.All(x => x < 0x80) && Encoding.ASCII.GetString(sb).StartsWith("-----BEGIN PTCGL-ABL SEAL v2-----\n") &&
              Encoding.ASCII.GetString(sb).EndsWith("\n-----END PTCGL-ABL SEAL v2-----\n"), "the sidecar is the block, ASCII, no BOM");
        var s1 = ReadSealed(p);
        Check(s1.Source == "sidecar" && Problem(s1) == null && s1.Seq == 1, "it verifies");

        mode = SealMode.Footer;
        var p2 = store.Save(m, "sidecar log, longer", p);
        var s2 = ReadSealed(p2);
        Check(!File.Exists(side) && s2.Source == "footer" && s2.Seq == 1 && Problem(s2) == null, "Footer now: stale .seal removed, footer re-seals in place");

        m.Won = false; m.Winner = 2;
        mode = SealMode.Sidecar;
        var p3 = store.Save(m, "sidecar log, longer", p2);
        Check(p3 != p2 && File.Exists(LogStore.SidecarPathFor(p3)) && !File.Exists(LogStore.SidecarPathFor(p2)) && !File.Exists(p2), "a moved sidecar log moves its .seal too");
        Check(Problem(ReadSealed(p3)) == null && ReadSealed(p3).Seq == 1, "and it still verifies at seq 1");

        var quoting = store.Save(Match("m-quote"), "a log quoting a seal:" + Separator + "{}\nAAAA\n-----END PTCGL-ABL SEAL v2-----\nmore", null);
        Check(ReadSealed(quoting).Source == "footer" && !File.Exists(LogStore.SidecarPathFor(quoting)) && Problem(ReadSealed(quoting)) == null,
              "Sidecar mode, but the log quotes the separator: sealed with a footer, so it still verifies");

        mode = SealMode.Off;
        var before = ChainJson(idDir);
        var p4 = store.Save(m, "sidecar log, longest", p3);
        Check(ReadSealed(p4) == null && !File.Exists(LogStore.SidecarPathFor(p4)), "Off: no footer, and the stale .seal is gone");
        Check(File.ReadAllBytes(p4).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("sidecar log, longest"))), "Off: exactly the Export text");
        Check(ChainJson(idDir) == before, "Off: the chain does not advance");
        Check(store.Sealer.Warning == null, "Off: no warning");
        mode = SealMode.Footer;
        var p5 = store.Save(Match("m-t"), "next", null);
        Check(ReadSealed(p5).Seq == 3 && ReadSealed(p5).Prev == ReadSealed(quoting).Hash, "back on: the next seal follows the last one (seq 3)");

        Console.WriteLine("35. Mode Off from the start never creates a key");
        var offDir = TempDir();
        var lazy = new SigningIdentity(offDir, new PortableRsa());
        var offStore = new LogStore(TempDir()) { Sealer = new LogSealer(lazy, "1.1.0", null) { Mode = () => SealMode.Off } };
        var po = offStore.Save(Match("m-o"), "off", null);
        Check(ReadSealed(po) == null && !lazy.Started && !Directory.Exists(offDir), "no seal, no key, no identity folder");
        offStore.Sealer.Mode = () => SealMode.Footer;
        offStore.Sealer.ReadyTimeout = TimeSpan.FromSeconds(30);
        var pl = offStore.Save(Match("m-p"), "on", null);
        var sl = ReadSealed(pl);
        Check(lazy.Started && sl != null && Problem(sl) == null && sl.Str("gameVersion") == null, "turned on later: the key is made then; gameVersion null when unknown");
        Cleanup(dir, idDir, offDir, offStore.Folder);
    }

    // ================================================================== never lose a log

    static void SealFailures()
    {
        Console.WriteLine("\n36. signing fails: the log is saved without a seal");
        var broken = new BrokenRsa();
        var id = NewIdentity(out var idDir, broken);
        var dir = TempDir();
        var warnings = new List<string>();
        var store = new LogStore(dir) { Sealer = new LogSealer(id, "1.1.0", "1.24.0", warnings.Add) };
        var ok = store.Save(Match("m-1"), "sealed", null);
        var chainBefore = ChainJson(idDir);
        broken.Throw = true;
        var p = store.Save(Match("m-2"), "unsealed: the signer threw", null);
        Check(File.Exists(p) && ReadSealed(p) == null && File.ReadAllText(p) == "unsealed: the signer threw", "the log is saved, exactly, without a seal");
        Check(store.Sealer.Warning != null && store.Sealer.Warning.Contains("without a seal") && store.Sealer.Warning.Contains("key store went away"),
              "Warning says so and why: " + store.Sealer.Warning);
        Check(warnings.Count == 1, "logged once");
        store.Save(Match("m-3"), "again", null);
        Check(warnings.Count == 1, "the same failure is not logged twice in a row");
        Check(ChainJson(idDir) == chainBefore, "the chain does not advance");
        broken.Throw = false;
        broken.Garbage = true;
        var pg = store.Save(Match("m-4"), "unsealed: bad signature", null);
        Check(ReadSealed(pg) == null && store.Sealer.Warning.Contains("did not verify"), "a signature that does not verify is never written");
        broken.Garbage = false;
        var pok = store.Save(Match("m-5"), "sealed again", null);
        var sok = ReadSealed(pok);
        Check(sok != null && Problem(sok) == null && sok.Seq == 2 && sok.Prev == ReadSealed(ok).Hash, "works again: seq 2 follows seq 1");
        Check(store.Sealer.Warning == null, "Warning cleared by the next seal");

        Console.WriteLine("37. the key cannot be created");
        var blocker = Path.Combine(TempDir(), "file-in-the-way");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker));
        File.WriteAllText(blocker, "not a folder");
        var noKey = new SigningIdentity(Path.Combine(blocker, "identity"), new PortableRsa());
        var nkStore = new LogStore(TempDir()) { Sealer = new LogSealer(noKey, "1.1.0", null) { ReadyTimeout = TimeSpan.FromSeconds(30) } };
        noKey.BeginLoad(nkStore.Folder);
        var pn = nkStore.Save(Match("m-n"), "no key", null);
        Check(File.Exists(pn) && ReadSealed(pn) == null, "the log is saved without a seal");
        Check(noKey.Error != null && nkStore.Sealer.Warning != null && nkStore.Sealer.Warning.StartsWith("The last battle log was saved without a seal"),
              "Warning: " + nkStore.Sealer.Warning);
        var noFolder = new LogStore(TempDir()) { Sealer = new LogSealer(null, "1.1.0", null) };
        Check(noFolder.Sealer.Warning != null, "no LocalAppData: the warning is there before any log");
        var pnf = noFolder.Save(Match("m-x"), "no identity at all", null);
        Check(File.Exists(pnf) && ReadSealed(pnf) == null, "and logs are saved unsealed");

        Console.WriteLine("38. the sidecar cannot be written");
        var sideStore = new LogStore(TempDir()) { Sealer = NewSealer(id, SealMode.Sidecar) };
        var wanted = Path.Combine(sideStore.Folder, LogStore.FileNameFor(Match("m-q")));
        Directory.CreateDirectory(LogStore.SidecarPathFor(wanted));      // a folder where the .seal goes
        var chain2 = ChainJson(idDir);
        var ps = sideStore.Save(Match("m-q"), "sidecar blocked", null);
        Check(File.Exists(ps) && File.ReadAllText(ps) == "sidecar blocked", "the log is saved");
        Check(sideStore.Sealer.Warning != null && sideStore.Sealer.Warning.Contains(".seal"), "Warning: " + sideStore.Sealer.Warning);
        Check(ChainJson(idDir) == chain2, "the chain does not advance");

        Console.WriteLine("39. the log itself cannot be written: the chain does not move");
        var fs = new LogStore(TempDir()) { Sealer = NewSealer(id) };
        var pf = fs.Save(Match("m-f"), "first", null);
        var chain3 = ChainJson(idDir);
        File.Delete(pf);
        Directory.CreateDirectory(pf);                                     // the .txt cannot be replaced
        bool threw = false;
        try { fs.Save(Match("m-f"), "first, longer", pf); } catch (IOException) { threw = true; } catch (UnauthorizedAccessException) { threw = true; }
        Check(threw && ChainJson(idDir) == chain3, "Save throws (the watcher retries) and chain.json is unchanged");
        id.GetChain(out var seqNow, out _);
        Check(seqNow == SigningIdentityChainSeq(chain3), "in memory too");
        Cleanup(dir, idDir, Path.GetDirectoryName(blocker), nkStore.Folder, noFolder.Folder, sideStore.Folder, fs.Folder);
    }

    static long SigningIdentityChainSeq(string chainJson)
    {
        SigningIdentity.TryParseChain(chainJson, out var seq, out _);
        return seq;
    }

    // ================================================================== identity

    static void SealIdentity()
    {
        Console.WriteLine("\n40. the key: made off the calling thread, kept, reloaded");
        var gated = new GatedRsa();
        var dir = TempDir();
        var id = new SigningIdentity(dir, gated);
        var store = new LogStore(TempDir()) { Sealer = new LogSealer(id, "1.1.0", null) { ReadyTimeout = TimeSpan.FromMilliseconds(100) } };
        id.BeginLoad(store.Folder);
        Check(!id.WaitReady(TimeSpan.FromMilliseconds(50)), "BeginLoad returns at once; the key is not ready yet");
        var early = store.Save(Match("m-e"), "too early", null);
        Check(ReadSealed(early) == null && store.Sealer.Warning.Contains("still being created"), "a log saved before the key exists is saved unsealed: " + store.Sealer.Warning);
        gated.Gate.Set();
        Check(id.WaitReady(TimeSpan.FromSeconds(30)), "then the key is ready");
        Check(gated.CreatedOnThread != Environment.CurrentManagedThreadId, "it was created on another thread");
        var xml = File.ReadAllText(Path.Combine(dir, SigningIdentity.KeyFileName));
        Check(xml.StartsWith("<RSAKeyValue>") && xml.Contains("<D>") && xml.Contains("<Modulus>"), "signing-key.xml is RSA.ToXmlString(true)");
        Check(ChainJson(dir) == "{\"seq\":0,\"last\":null,\"keyId\":\"" + id.KeyId + "\"}", "a new key starts chain.json at seq 0, naming its key");
        store.Sealer.ReadyTimeout = TimeSpan.FromSeconds(30);
        var p1 = store.Save(Match("m-1"), "one", null);
        var p2 = store.Save(Match("m-2"), "two", null);
        var keyId = id.KeyId;

        var again = new SigningIdentity(dir, new PortableRsa());
        again.Load(store.Folder);
        Check(again.KeyId == keyId && again.Modulus == id.Modulus, "the next start loads the same key");
        again.GetChain(out var seq, out var last);
        Check(seq == 2 && last == ReadSealed(p2).Hash, "and continues the chain");

        Console.WriteLine("41. chain.json lost or corrupt: rebuilt from the logs folder");
        File.Delete(Path.Combine(dir, SigningIdentity.ChainFileName));
        var rebuilt = new SigningIdentity(dir, new PortableRsa());
        rebuilt.Load(store.Folder);
        rebuilt.GetChain(out seq, out last);
        Check(seq == 2 && last == ReadSealed(p2).Hash, "missing: continues after the newest seal of this key in the folder");
        var store2 = new LogStore(store.Folder) { Sealer = NewSealer(rebuilt) };
        var p3 = store2.Save(Match("m-3"), "three", null);
        Check(ReadSealed(p3).Seq == 3 && ReadSealed(p3).Prev == ReadSealed(p2).Hash, "so the next seal is seq 3, linked to seq 2");
        File.WriteAllText(Path.Combine(dir, SigningIdentity.ChainFileName), "{\"seq\":\"oops\"");
        var fixedUp = new SigningIdentity(dir, new PortableRsa());
        fixedUp.Load(store.Folder);
        fixedUp.GetChain(out seq, out _);
        Check(seq == 3 && File.Exists(Path.Combine(dir, SigningIdentity.ChainFileName + SigningIdentity.CorruptSuffix)), "corrupt: kept as .corrupt, rebuilt at seq 3");
        Check(SigningIdentity.TryParseChain(ChainJson(dir), out var s3, out _) && s3 == 3, "and chain.json written again");
        Check(!SigningIdentity.TryParseChain("{\"seq\":2,\"last\":null}", out _, out _) && !SigningIdentity.TryParseChain("{\"seq\":0,\"last\":\"" + new string('a', 64) + "\"}", out _, out _),
              "chain.json with seq and last disagreeing is rejected");

        Console.WriteLine("42. a corrupt key file is set aside, not lost");
        File.WriteAllText(Path.Combine(dir, SigningIdentity.KeyFileName), "<RSAKeyValue><Modulus>garbage");
        var fresh = new SigningIdentity(dir, new PortableRsa());
        fresh.Load(store.Folder);
        fresh.GetChain(out seq, out last);
        Check(fresh.IsReady && fresh.KeyId != keyId, "a new key replaces it");
        Check(File.ReadAllText(Path.Combine(dir, SigningIdentity.KeyFileName + SigningIdentity.CorruptSuffix)).Contains("garbage"), "the old file is kept as signing-key.xml.corrupt");
        Check(seq == 0 && last == null, "the new key starts its own chain");
        var publicOnly = new SigningIdentity(TempDir(), new PortableRsa());
        Directory.CreateDirectory(publicOnly.Folder);
        File.WriteAllText(publicOnly.KeyPath, xml.Substring(0, xml.IndexOf("<P>")) + "</RSAKeyValue>");
        publicOnly.Load(null);
        Check(publicOnly.IsReady && publicOnly.KeyId != keyId, "a key file without the private key is replaced too");
        foreach (var bits in new[] { 1024, 4160 })
        {
            // Verifiers accept 2048 to 4096 bits (docs/LOG-SEAL.md §1): a key outside that would only make invalid seals.
            var odd = new SigningIdentity(TempDir(), new PortableRsa());
            Directory.CreateDirectory(odd.Folder);
            using (var rsa = RSA.Create(bits)) File.WriteAllText(odd.KeyPath, rsa.ToXmlString(true));
            odd.Load(null);
            Check(odd.IsReady && File.Exists(odd.KeyPath + SigningIdentity.CorruptSuffix) && B64Url(odd.Modulus).Length * 8 == SigningIdentity.KeyBits,
                  "a " + bits + "-bit key file is set aside and replaced by a 2048-bit key");
            Cleanup(odd.Folder);
        }

        Console.WriteLine("43. the plugin's own RSA (RSACryptoServiceProvider) on this runtime");
        try
        {
            var cspReal = new SigningIdentity(TempDir(), RsaBackend.Default);
            cspReal.Load(null);
            var cs = new LogStore(TempDir()) { Sealer = NewSealer(cspReal) };
            var pc = cs.Save(Match("m-csp"), "signed by RSACryptoServiceProvider", null);
            Check(cspReal.Error == null && Problem(ReadSealed(pc)) == null, "creates, signs, and the seal verifies independently" + (cspReal.Error == null ? "" : ": " + cspReal.Error));
            var reload = new SigningIdentity(cspReal.Folder, RsaBackend.Default);
            reload.Load(null);
            Check(reload.KeyId == cspReal.KeyId, "reloads its own signing-key.xml");
            var cross = new SigningIdentity(cspReal.Folder, new PortableRsa());
            cross.Load(null);
            Check(cross.KeyId == cspReal.KeyId, "and RSA.Create() reads the same file");
            Cleanup(cspReal.Folder, cs.Folder);
        }
        catch (PlatformNotSupportedException e)
        {
            Console.WriteLine("    skipped: " + e.Message);
        }

        Console.WriteLine("44. RFC 7638 thumbprint, known answer");
        // RFC 7638 section 3.1's example key and thumbprint.
        const string rfcN = "0vx7agoebGcQSuuPiLJXZptN9nndrQmbXEps2aiAFbWhM78LhWx4cbbfAAtVT86zwu1RK7aPFFxuhDR1L6tSoc_BJECPebWKRXjBZCiFV4n3oknjhMstn64tZ_2W-5JsGY4Hc5n9yBXArwl93lqt7_RN5w6Cf0h4QyQ5v-65YGjQR0_FDW2QvzqY368QQMicAtaSqzs8KJZgnYb9c7d0zgdAZHzu6qMQvRL5hajrn1n91CbOpbISD08qNLyrdkt-bFTWhAI4vMQFh6WeZu0fM4lFd2NcRwr3XPksINHaQ-G_xBniIqbw0Ls1jF44-csFCur-kEgU8awapJzKnqDKgw";
        Check(SigningIdentity.Thumbprint(rfcN, "AQAB") == "NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs", "matches the RFC's example");
        Check(SigningIdentity.Base64Url(SigningIdentity.TrimLeadingZeros(new byte[] { 0, 0, 1, 0, 1 })) == "AQAB", "leading zero bytes are not part of a JWK integer");
        Cleanup(dir, store.Folder, publicOnly.Folder);
    }

    // ================================================================== through the watcher

    static void SealInMatches()
    {
        Console.WriteLine("\n45. matches end to end, sealed");
        var id = NewIdentity(out var idDir);
        using (var t = new Rig(sealer: NewSealer(id)))
        {
            StartMatch("w-1", iAmP1: false, opp: "Blaine");
            Log("Setup"); t.Tick(1);
            GameOver(0);
            t.Tick(0.1); t.Tick(0.5);
            var first = ReadSealed(t.Txts()[0]);
            Check(first != null && first.Seq == 1 && first.Str("result") == "NO-RESULT" && first.Meta.GetProperty("winner").ValueKind == JsonValueKind.Null,
                  "first read sealed before the winner is known");
            Info.winner = 2;
            t.Tick(0.5);
            var s = ReadSealed(t.Txts()[0]);
            Check(t.Txts().Length == 1 && s.Seq == 1 && s.Prev == null && Problem(s) == null, "renamed and re-sealed in place (seq 1)");
            Check(s.Str("result") == "VICTORY" && s.Meta.GetProperty("winner").GetInt32() == 2 && s.Meta.GetProperty("mySeat").GetInt32() == 2,
                  "result, winner and our seat from the client");
            Check(s.Str("capture") == "auto" && s.Str("matchId") == "w-1" && s.Meta.GetProperty("opponent").GetProperty("name").GetString() == "Blaine", "capture auto, matchId, opponent");
            Check(s.Meta.GetProperty("player").GetProperty("accountHash").GetString() == LogSealer.AccountHash("me-123") &&
                  s.Meta.GetProperty("opponent").GetProperty("accountHash").GetString() == LogSealer.AccountHash("opp-9"), "both accountHashes from the client's ids");
            Log("Ash took the last Prize card.");
            t.Tick(3);
            s = ReadSealed(t.Txts()[0]);
            Check(s.Seq == 1 && Encoding.UTF8.GetString(s.Log).EndsWith("Prize card.") && Problem(s) == null, "the longer read re-seals in place");
            new BattleLogExporter().ExportBattleLog(MatchManager.instance.BattleLogExportData);
            Log("Extra line seen only by a manual export.");
            new BattleLogExporter().ExportBattleLog(MatchManager.instance.BattleLogExportData);
            s = ReadSealed(t.Txts()[0]);
            Check(s.Str("capture") == "export" && s.Seq == 1 && Problem(s) == null, "a longer manual export of the same match: capture export, same seq");

            NetworkMatchController.currentMatchInfo = null; t.Tick(1);
            t.Tick(400);
            new BattleLogExporter().ExportBattleLog(new IBattleLogData[] { new Entry { Line = "an old replay" } });
            var orphan = t.Txts().Select(ReadSealed).FirstOrDefault(x => x.Str("result") == "EXPORTED");
            Check(orphan != null && orphan.Str("capture") == "export" && orphan.Seq == 2 && orphan.Prev == s.Hash && Problem(orphan) == null,
                  "an EXPORTED orphan is sealed too (capture export, seq 2)");
            Check(t.Store.Sealer.Warning == null, "no warning");
        }
        Cleanup(idDir);
    }

    // ================================================================== OutputFolder

    static void OutputFolderSetting()
    {
        Console.WriteLine("\n46. OutputFolder: empty by default, garbled values fall back");
        var root = TempDir();
        var def = Path.Combine(root, "Documents", "PTCGL Battle Logs");
        Func<string, string> create = f => { try { Directory.CreateDirectory(f); return null; } catch (Exception e) { return e.Message; } };
        Check(LogsFolder.DefaultFor(Path.Combine(root, "Documents")) == def, "default = Documents\\PTCGL Battle Logs");
        var c = LogsFolder.Choose("", def, null, create);
        Check(c.Folder == def && !c.ClearSetting && c.Warning == null, "empty -> the default folder, quietly");
        c = LogsFolder.Choose("   ", def, null, create);
        Check(c.Folder == def && c.Warning == null, "blank -> the default folder");
        var mine = Path.Combine(root, "My Logs");
        c = LogsFolder.Choose(mine, def, null, create);
        Check(c.Folder == mine && Directory.Exists(mine) && !c.ClearSetting && c.Warning == null, "a usable folder is used (and created)");
        c = LogsFolder.Choose(def, def, null, create);
        Check(c.Folder == def && c.ClearSetting && c.Warning == null && c.Note != null, "the default spelled out -> cleared to empty, quietly");
        c = LogsFolder.Choose(@"C:\Users\O\'Brien\Documents\PTCGL Battle Logs", def, null, create);
        Check(c.Folder == def && c.ClearSetting && c.Warning != null && c.Warning.Contains("garbled"), "an apostrophe path garbled by BepInEx -> default, cleared, warned");
        c = LogsFolder.Choose(@"\server\share\Documents\PTCGL Battle Logs", def, null, create);
        Check(c.Folder == def && c.ClearSetting && c.Warning != null && c.Warning.Contains("network"), "a \\\\server path garbled by BepInEx -> default, cleared, warned");
        c = LogsFolder.Choose("D:\\\"quoted\"", def, null, create);
        Check(c.Folder == def && c.ClearSetting, "a path with \" -> default, cleared");
        c = LogsFolder.Choose("D:\\new" + (char)7 + "logs", def, null, create);
        Check(c.Folder == def && c.ClearSetting, "a path with a control character -> default, cleared");
        var blocker = Path.Combine(root, "a-file");
        File.WriteAllText(blocker, "x");
        c = LogsFolder.Choose(Path.Combine(blocker, "logs"), def, null, create);
        Check(c.Folder == def && !c.ClearSetting && c.Warning != null && c.Warning.Contains("cannot be created"), "a folder that cannot be created -> default, warned, setting kept");

        var bep = FindBepInEx();
        Check(bep != null, "lib/BepInEx.dll found for the BepInEx checks");
        if (bep == null) { Cleanup(root); return; }
        var conv = bep.GetType("BepInEx.Configuration.TomlTypeConverter", true);
        var toText = conv.GetMethod("ConvertToString", new[] { typeof(object), typeof(Type) });
        var toValue = conv.GetMethod("ConvertToValue", new[] { typeof(string), typeof(Type) });
        Func<string, string> roundTrip = v => (string)toValue.Invoke(null, new object[] { ((string)toText.Invoke(null, new object[] { v, typeof(string) })).Trim(), typeof(string) });
        c = LogsFolder.Choose(@"D:\Ash's logs", def, roundTrip, create);
        Check(c.Folder == def && c.ClearSetting && c.Warning.Contains("cannot be kept"), "a value BepInEx would garble at its next save -> default, cleared: " + c.Warning);
        c = LogsFolder.Choose(@"\\server\share\logs", def, roundTrip, create);
        Check(c.Folder == def && c.ClearSetting, "an unescaped \\\\server path (BepInEx reads it back as \\server) -> default, cleared");
        Check(LogsFolder.WhyUnkeepable(@"C:\Users\Ash\Documents\PTCGL Battle Logs", roundTrip) == null && LogsFolder.WhyUnkeepable(@"E:\Logs (2026) & more!", roundTrip) == null,
              "ordinary paths are kept");

        Console.WriteLine("47. upgrading a cfg that BepInEx garbled, with the real BepInEx ConfigFile");
        var cfgType = bep.GetType("BepInEx.Configuration.ConfigFile", true);
        var bind = cfgType.GetMethods().First(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
            m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[] { "String", "String", "T", "String" })).MakeGenericMethod(typeof(string));
        var cfgPath = Path.Combine(root, "ptcgl.autobattlelog.cfg");
        object Session(string defaultValue, out PropertyInfo value)
        {
            var cfg = Activator.CreateInstance(cfgType, cfgPath, true);
            var entry = bind.Invoke(cfg, new object[] { "General", "OutputFolder", defaultValue, "Folder where every battle log is saved." });
            value = entry.GetType().GetProperty("Value");
            return entry;
        }
        foreach (var oldDefault in new[] { @"C:\Users\O'Brien\Documents\PTCGL Battle Logs", @"\\fileserver\users\ash\Documents\PTCGL Battle Logs" })
        {
            File.Delete(cfgPath);
            Session(oldDefault, out _);                                         // 1.0: the default folder as the default value
            var entry = Session("", out var value);                             // 1.1: default empty
            var read = (string)value.GetValue(entry);
            Check(read != oldDefault, "BepInEx garbled it: " + read);
            var choice = LogsFolder.Choose(read, def, roundTrip, create);
            Check(choice.Folder == def && choice.ClearSetting, "the plugin falls back to the default folder");
            value.SetValue(entry, "");                                          // what Plugin.ResolveFolder does
            var next = Session("", out var nextValue);
            var cfgText = File.ReadAllText(cfgPath);
            Check((string)nextValue.GetValue(next) == "" && (cfgText.Contains("OutputFolder = \r\n") || cfgText.Contains("OutputFolder = \n")),
                  "the cfg now holds an empty OutputFolder, which BepInEx keeps");
        }
        Cleanup(root);
    }

    static Assembly FindBepInEx()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            var dll = Path.Combine(dir, "lib", "BepInEx.dll");
            if (File.Exists(dll)) return Assembly.LoadFrom(dll);
        }
        return null;
    }

    // ================================================================== chain.json against the logs folder

    static void SealChainRepair()
    {
        Console.WriteLine("\n48. chain.json could not be written, then the game exited: the next start catches up");
        var id = NewIdentity(out var idDir);
        var dir = TempDir();
        var chainPath = Path.Combine(idDir, SigningIdentity.ChainFileName);
        var blocker = chainPath + ".tmp";                                  // a folder where chain.json's .tmp goes
        var store = new LogStore(dir) { Sealer = NewSealer(id) };
        store.Save(Match("m-a"), "match a", null);
        Directory.CreateDirectory(blocker);
        var pb = store.Save(Match("m-b", "Misty"), "match b", null);
        var b = ReadSealed(pb);
        Check(b.Seq == 2 && SigningIdentityChainSeq(ChainJson(idDir)) == 1, "seq 2 sealed while chain.json stays at seq 1");
        Directory.Delete(blocker);                                         // the game exits before a retry
        var restart = new SigningIdentity(idDir, new PortableRsa(), m => Console.WriteLine("    [key] " + m));
        restart.Load(dir);
        restart.GetChain(out var seq, out var last);
        Check(seq == 2 && last == b.Hash, "the next start continues after seq 2, found in the logs folder");
        Check(ChainJson(idDir) == SigningIdentity.ChainText(2, b.Hash, id.KeyId), "and writes chain.json");
        var store2 = new LogStore(dir) { Sealer = NewSealer(restart) };
        var pc = store2.Save(Match("m-c", "Brock"), "match c", null);
        Check(ReadSealed(pc).Seq == 3 && ReadSealed(pc).Prev == b.Hash, "so the next seal is seq 3, linked to seq 2 (no fork at seq 2)");

        Console.WriteLine("49. the same for a re-seal in place (a longer read)");
        Directory.CreateDirectory(blocker);
        var pc2 = store2.Save(Match("m-c", "Brock"), "match c, longer read", pc);
        var c2 = ReadSealed(pc2);
        Check(pc2 == pc && c2.Seq == 3 && !ChainJson(idDir).Contains(c2.Hash), "seq 3 re-sealed in place while chain.json still names the old seal");
        Directory.Delete(blocker);
        var restart2 = new SigningIdentity(idDir, new PortableRsa());
        restart2.Load(dir);
        restart2.GetChain(out seq, out last);
        Check(seq == 3 && last == c2.Hash, "the next start follows the re-sealed file (no broken link after seq 3)");
        var store3 = new LogStore(dir) { Sealer = NewSealer(restart2) };
        var pd = store3.Save(Match("m-d"), "match d", null);
        Check(ReadSealed(pd).Seq == 4 && ReadSealed(pd).Prev == c2.Hash, "next seal: seq 4 after the re-sealed seq 3");

        Console.WriteLine("50. a failed chain.json write is retried (heartbeat, shutdown)");
        Directory.CreateDirectory(blocker);
        var pe = store3.Save(Match("m-e"), "match e", null);
        var e = ReadSealed(pe);
        restart2.RetryChain();
        Check(SigningIdentityChainSeq(ChainJson(idDir)) == 4, "still blocked: chain.json unchanged, nothing thrown");
        Directory.Delete(blocker);
        restart2.RetryChain();
        Check(ChainJson(idDir) == SigningIdentity.ChainText(5, e.Hash, id.KeyId), "written as soon as it can be");
        var written = File.GetLastWriteTimeUtc(chainPath);
        restart2.RetryChain();
        Check(File.GetLastWriteTimeUtc(chainPath) == written, "nothing to do when the last write worked");

        Console.WriteLine("51. rebuilding chain.json trusts only seals this key signed");
        string Footer(string json, string signature) => "x" + Separator + json + "\n" + signature + "\n-----END PTCGL-ABL SEAL v2-----\n";
        // The keyId is public: anyone can drop a file that claims it, with any seq.
        File.WriteAllText(Path.Combine(dir, "planted.txt"),
            Footer("{\"seq\":999999999999999999,\"prev\":\"" + new string('a', 64) + "\",\"keyId\":\"" + id.KeyId + "\"}", "AAAA"));
        File.WriteAllText(Path.Combine(dir, "bumped.txt"), Footer(e.Json.Replace("\"seq\":5,", "\"seq\":50,"), e.Signature));
        File.WriteAllText(Path.Combine(dir, "respelled.txt"), Footer(e.Json, Respell(e.Signature)));
        Check(Convert.ToBase64String(Convert.FromBase64String(Respell(e.Signature))) != Respell(e.Signature), "(a non-canonical spelling of the real signature)");
        File.Delete(chainPath);
        var rebuilt = new SigningIdentity(idDir, new PortableRsa());
        rebuilt.Load(dir);
        rebuilt.GetChain(out seq, out last);
        Check(seq == 5 && last == e.Hash, "missing chain.json: rebuilt at the real seq 5, not a planted seq, an edited seq or a re-spelled signature");
        var again = new SigningIdentity(idDir, new PortableRsa());
        again.Load(dir);
        again.GetChain(out seq, out last);
        Check(seq == 5 && last == e.Hash, "chain.json present: the same files do not move it either");

        Console.WriteLine("52. chain.json of another key, or one that does not name its key");
        File.WriteAllText(chainPath, SigningIdentity.ChainText(77, new string('b', 64), "another-key"));
        var other = new SigningIdentity(idDir, new PortableRsa());
        other.Load(dir);
        other.GetChain(out seq, out last);
        Check(seq == 5 && last == e.Hash, "another key's chain.json (a crash right after a new key was written) is not followed: rebuilt from this key's seals");
        File.WriteAllText(chainPath, "{\"seq\":5,\"last\":\"" + e.Hash + "\"}");
        var older = new SigningIdentity(idDir, new PortableRsa());
        older.Load(dir);
        older.GetChain(out seq, out last);
        Check(seq == 5 && last == e.Hash && ChainJson(idDir) == "{\"seq\":5,\"last\":\"" + e.Hash + "\"}", "one without keyId is read as this key's (and left as it is)");
        Cleanup(dir, idDir);

        Console.WriteLine("53. ReplaceFile refused (a network share): delete + rename instead");
        var wdir = TempDir();
        Directory.CreateDirectory(wdir);
        var target = Path.Combine(wdir, "x.txt");
        File.WriteAllText(target, "old");
        var replace = LogStore.ReplaceFile;
        try
        {
            LogStore.ReplaceFile = (tmp, path) => throw new IOException("Unable to remove the file to be replaced.");
            LogStore.WriteAtomic(target, Encoding.UTF8.GetBytes("new"));
            Check(File.ReadAllText(target) == "new" && !File.Exists(target + ".tmp"), "IOException: written anyway, no .tmp left");
            LogStore.ReplaceFile = (tmp, path) => throw new UnauthorizedAccessException("Access to the path is denied.");
            LogStore.WriteAtomic(target, Encoding.UTF8.GetBytes("newer"));
            Check(File.ReadAllText(target) == "newer", "UnauthorizedAccessException (ACLs that cannot be merged): written anyway");
            var st = new LogStore(wdir);
            var late = Match("m-late", won: null);
            var p1 = st.Save(late, "log", null);
            late.Won = true; late.Winner = 1;
            var p2 = st.Save(late, "log, longer", p1);
            Check(p2.EndsWith(" - VICTORY vs Gary.txt") && !File.Exists(p1) && File.ReadAllText(p2) == "log, longer",
                  "a late result still renames NO-RESULT to VICTORY and overwrites it");
        }
        finally { LogStore.ReplaceFile = replace; }
        Cleanup(wdir);
    }

    /// <summary>The same signature bytes in another base64 spelling: one unused bit of the last character set.</summary>
    static string Respell(string signature)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var data = signature.TrimEnd('=');
        var last = alphabet[alphabet.IndexOf(data[data.Length - 1]) ^ 1];
        return data.Substring(0, data.Length - 1) + last + signature.Substring(data.Length);
    }

    // ================================================================== samples for tools/verify_log.py

    static void SealSamples()
    {
        var target = Environment.GetEnvironmentVariable("PTCGL_SEAL_SAMPLES");
        Console.WriteLine("\n54. sample sealed logs for the cross-language check" + (string.IsNullOrEmpty(target) ? ": PTCGL_SEAL_SAMPLES not set, skipped" : " -> " + target));
        if (string.IsNullOrEmpty(target)) return;
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(target))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".txt" || ext == ".seal" || Path.GetFileName(f) == LogStore.IndexFile) File.Delete(f);
        }
        var decks = Path.Combine(target, LogStore.DecksFolder);
        if (Directory.Exists(decks)) Directory.Delete(decks, true);
        // The plugin's own RSA where this runtime has it, so the samples come from the code that ships.
        RsaBackend backend = RsaBackend.Default;
        try { using (new RSACryptoServiceProvider(512)) { } } catch (PlatformNotSupportedException) { backend = new PortableRsa(); }
        var id = new SigningIdentity(TempDir(), backend);
        id.Load(null);
        var mode = SealMode.Footer;
        var store = new LogStore(target) { Sealer = new LogSealer(id, "1.1.0", "1.24.0") { Mode = () => mode } };
        var t0 = new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
        MatchRecord At(int minutes, string matchId, bool? won, string opponent)
        {
            var r = Match(matchId, opponent, won);
            r.StartUtc = t0.AddMinutes(minutes);
            r.EndUtc = r.StartUtc.AddMinutes(11);
            return r;
        }
        var first = At(0, "sample-1", true, "Gary");
        first.MyDeckList = SampleDeck();
        var paths = new List<string>
        {
            store.Save(first, "Setup\r\nTurn 1 - Ash played Charmander.\r\nGary conceded.\r\nAsh wins.", null),
            store.Save(At(20, "sample-2", false, "Zo\u00eb \"Q\" \\ " + char.ConvertFromUtf32(0x1F525) + " \u65e5\u672c\u8a9e"),
                       "Setup\nZo\u00eb's Pikachu \u2014 \u65e5\u672c\u8a9e " + char.ConvertFromUtf32(0x1F525) + "\nZo\u00eb wins.", null),
        };
        var third = At(40, "sample-3", null, "Brock");
        var p3 = store.Save(third, "Setup\nA line that quotes a seal:" + Separator + "{}\nAAAA\n-----END PTCGL-ABL SEAL v2-----\nConnection lost", null);
        third.Won = true; third.Winner = 1;
        paths.Add(store.Save(third, "Setup\nA line that quotes a seal:" + Separator + "{}\nAAAA\n-----END PTCGL-ABL SEAL v2-----\nConnection lost\nBrock conceded.", p3));
        mode = SealMode.Sidecar;
        paths.Add(store.Save(new MatchRecord { StartUtc = t0.AddMinutes(90), ManualOnly = true, Capture = LogSealer.CaptureExport }, "an exported replay", null));
        var seals = paths.Select(ReadSealed).ToList();
        Check(seals.Count(x => x.Source == "footer") >= 3 && seals.Count(x => x.Source == "sidecar") == 1, "3 footer seals and 1 sidecar written");
        Check(seals[0].Deck != null && seals.Skip(1).All(x => x.Deck == null), "the first log names a deck file");
        Check(seals.Select(x => x.Seq).SequenceEqual(new long[] { 1, 2, 3, 4 }) && seals.All(x => Problem(x) == null), "one continuous chain, seq 1-4, all valid here");
        Check(Enumerable.Range(1, 3).All(i => seals[i].Prev == seals[i - 1].Hash), "linked");
        Console.WriteLine("    check with: python3 tools/verify_log.py --chain \"" + target + "\"");
        Cleanup(id.Folder);
    }
}
