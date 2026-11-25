using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PtcglAutoBattleLog.Core;
using PtcglAutoBattleLog.Tray.Logic;

// Deterministic apart from the RSA key and signatures: rerunning changes only those bytes.
sealed class TestRsa : RsaBackend
{
    public override RSA Create(int bits) => RSA.Create(bits);
    public override RSA FromXml(string xml) { var r = RSA.Create(); r.FromXmlString(xml); return r; }
    public override byte[] SignHash(RSA key, byte[] h) => key.SignHash(h, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    public override bool VerifyHash(RSA key, byte[] h, byte[] s) => key.VerifyHash(h, s, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}

static class Program
{
    static int Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "golden");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var logs = Path.Combine(outDir, "logs");
        var work = Path.Combine(Path.GetTempPath(), "pab-goldens-" + Guid.NewGuid().ToString("N"));
        var identity = new SigningIdentity(Path.Combine(work, "identity"), new TestRsa());
        identity.Load(null);
        var mode = SealMode.Footer;
        var store = new LogStore(logs) { Sealer = new LogSealer(identity, "1.6.3", "1.43.0") { Mode = () => mode } };
        var t = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        int n = 0;
        string Save(string me, string myId, bool? won, bool manual = false, DeckList deck = null, string queue = null, string format = null, string opponent = "Gary")
        {
            n++;
            var start = t.AddHours(n);
            return store.Save(new MatchRecord
            {
                MatchId = manual ? null : "m-" + n, StartUtc = start, EndUtc = manual ? (DateTime?)null : start.AddMinutes(10), Won = won,
                Winner = won == null ? (int?)null : won == true ? 1 : 2, MySeat = 1, MyName = me, MyAccountId = myId, OpponentName = opponent,
                OpponentAccountId = "gary-id", ManualOnly = manual, Capture = LogSealer.CaptureAuto, MyDeckList = deck, MyDeck = deck?.Name,
                Queue = queue, Format = format,
            }, "Setup\nTurn " + n + " - " + me + " played", null);
        }
        var deck = new DeckList { Name = "Dragapult ex", DeckDefinitionId = "d-1", Size = 2 };
        deck.Cards["sv8_174"] = 2;
        Save("Ash", "ash-id", true, deck: deck, queue: "Ranked", format: "Standard");
        Save("Ash \"the\" Kétchum ★", "ash-id", false, queue: "Casual", format: "Standard", opponent: "Gary / Oak");
        Save("Ash", "ash-id", true, queue: "BuildAndBattle", format: "BuildAndBattle");
        Save("Ash", null, null);
        Save("Ash", "ash-id", null, manual: true);
        mode = SealMode.Sidecar;
        Save("Misty", "misty-id", true, deck: deck, queue: "Friend", format: "Expanded");
        mode = SealMode.Off;
        Save("Brock", "brock-id", false);

        // What the tray derives from each log: the seal fields, Pokematch's JSON and its hash, tcgkb's key.
        var rows = new List<string>();
        foreach (var file in Directory.GetFiles(logs, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
        {
            var s = SealSummary.TryRead(file);
            var c = PokematchUploader.Read(file);
            rows.Add(string.Join("\t", Path.GetFileName(file),
                s == null ? "-" : LogUploader.Key(s),
                s == null ? "-" : (PtcglAccountScanner.IsEvidence(s) ? "evidence" : "not-evidence"),
                s == null ? "-" : (LogUploader.IsBuildAndBattle(s) ? "bnb" : "-"),
                c?.Key ?? "-", c?.Sha256 ?? "-"));
            if (c != null) File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".pokematch.json"), Json.Write(c.Match), FileOps.Utf8NoBom);
        }
        File.WriteAllLines(Path.Combine(outDir, "derived.tsv"), rows);

        // The upload state files as the C# tray writes them.
        var tcgkb = new LogUploadState();
        tcgkb.Uploaded.Add("m-1|" + LogSealer.AccountHash("ash-id") + "|" + identity.KeyId);
        tcgkb.Decks.Add(new string('a', 64));
        File.WriteAllText(Path.Combine(outDir, "tcgkb-uploads.json"), Json.Write(tcgkb), FileOps.Utf8NoBom);
        var pm = new PokematchUploadState();
        pm.Uploads.Add(new PokematchUpload { Key = "match:m-1", Status = PokematchUpload.Uploaded, Id = "65f0", Sha256 = new string('b', 64), File = "x.txt", Utc = t });
        pm.Uploads.Add(new PokematchUpload { Key = "file:y.txt", Status = PokematchUpload.Rejected, Sha256 = new string('c', 64), File = "y.txt", Utc = t, Error = "Pokematch refused" });
        pm.Save(Path.Combine(outDir, "pokematch-uploads.json"));
        new Settings { GameDir = "C:\\Games\\PTCGL", DisclaimerAccepted = Disclaimer.Version, TcgkbAutoUpload = true }.Save(Path.Combine(outDir, "settings.json"));

        // How the Json writer spells strings and empty lists (the hash of Pokematch's JSON depends on it).
        var tricky = new PokematchMatch
        {
            ActiveDeck = new PokematchDeck { Name = "Kétchum ★ \"quoted\" back\\slash / slash <tag> & 😀" },
            OpponentDeck = "ctl\u0001\u001f\t\n\r\b\f\u007f\u0085\u2028\u2029end",
            Format = "Bo1", Games = new List<PokematchGame>(), Date = "2026-10-20", Tags = new List<string>(),
        };
        File.WriteAllText(Path.Combine(outDir, "escape.json"), Json.Write(tricky), FileOps.Utf8NoBom);

        try { Directory.Delete(work, true); } catch { }
        Console.WriteLine("goldens written to " + outDir);
        return 0;
    }
}
