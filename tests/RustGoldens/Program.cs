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

        // BepInEx cfg strings as the C# tray reads and writes them (its tests check this against the real BepInEx.dll).
        var cfgInputs = new[]
        {
            @"C:\Users\Ash\Documents\PTCGL Battle Logs", "C:\\Users\\Jos\u00e9\\Pok\u00e9mon logs", @"C:\trailing\", "relative\\dir", "C:/forward/slashes",
            @"C:\a = b\[x]", @"C:\#notacomment", @"C:\new\tab", @"Z:\", @"E:\Logs (2026) & more!",
            @"\\server\share\PTCGL Logs", @"C:\Users\O'Brien\Documents\PTCGL Battle Logs", @"D:\My ""quoted"" logs", @"\\?\C:\long", "relative\\new", " C:\\x", "C:\\x\ny",
            @"D:\""quoted""", @"\Logs", @"\server\share", "a\\b\"c'd\te\nf\r\0\a\b\v\f", @"\\srv\s", "plain", "",
            @"C:\\a\\b", @"""C:\x", @"\\\\srv\\s", @"a\qb\", @"C:\a\\b", @"D:\Ash\'s", "x\\ty", "\u00e9:\\x", "C:\\x\\\\y\nz",
        };
        var cfg = cfgInputs.Select(v =>
        {
            string serialized;
            try { serialized = BepInExConfig.Serialize(v); } catch (ArgumentException) { serialized = null; }
            return new Dictionary<string, string>
            {
                ["value"] = v, ["escape"] = BepInExConfig.Escape(v), ["convertToObject"] = BepInExConfig.ConvertToObject(v),
                ["unescape"] = BepInExConfig.Unescape(v), ["serialize"] = serialized, ["whyPluginRefuses"] = GameInstall.WhyPluginRefuses(v),
            };
        }).ToList();
        File.WriteAllText(Path.Combine(outDir, "bepinex-config.json"),
            System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        // plugin-status.json as the plugin's own writer makes it, with strings the tray must read back exactly.
        var emoji = char.ConvertFromUtf32(0x1F525);
        var nasty = "C:\\Users\\Zo\u00eb \"Q\" \u65e5\u672c\u8a9e " + emoji + "\\PTCGL\tBattle\nLogs\r" + (char)1 + (char)0x1F + "\u2028\u2029/end\\";
        var lone = "A" + (char)0xD800 + "B" + (char)0xDC00 + "C" + (char)0xDBFF;
        var beat = new DateTime(2026, 10, 19, 21, 35, 0, 123, DateTimeKind.Utc);
        foreach (var (name, folder, message, last) in new[]
        {
            ("plain", @"C:\Users\Ash\Documents\PTCGL Battle Logs", "Watching for matches.", @"C:\Users\Ash\Documents\PTCGL Battle Logs\2026-10-19 21.34.05 - VICTORY vs Gary.txt"),
            ("nasty", nasty, "line 1\nline \"2\"\t\\ end", nasty + "\\x.txt"),
            ("lone-surrogates", lone, lone, lone),
            ("nulls", null, null, null),
        })
        {
            var file = Path.Combine(outDir, "plugin-status-" + name + ".json");
            new PluginStatusWriter(file, "1.1.0", folder, 4242).Update(beat, PluginStatusWriter.Ok, message, 3, last, last == null ? (DateTime?)null : beat.AddSeconds(-55));
        }

        try { Directory.Delete(work, true); } catch { }
        Console.WriteLine("goldens written to " + outDir);
        return 0;
    }
}
