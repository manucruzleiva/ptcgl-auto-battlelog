using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtcglAutoBattleLog.Core;

// The player's deck next to each log (issue #22): read from deckInfo, saved as <name>.deck, named
// by the seal (v2 deckSha256). Format: docs/LOG-SEAL.md, "Deck file".
static partial class Program
{
    /// <summary>The deckInfo of the Build &amp; Battle match in issue #15, shortened.</summary>
    static DeckInfo SampleDeckInfo() => new DeckInfo
    {
        size = 40, DeckGuid = "fa550ad9-e5d6-4472-af21-abf178eb5016", IterationCount = 4,
        deckName = "<i></i>", deckDefinitionID = "bnb-me3",
        cards = new Dictionary<string, int> { ["sv8_174"] = 2, ["me1_131"] = 2, ["sv5_144"] = 2, ["sv1_1"] = 34 },
    };

    static DeckList SampleDeck() => DeckList.FromDeckInfo(SampleDeckInfo());

    /// <summary>A client that keeps the guid as a Guid and the counts as longs.</summary>
    class OtherDeckInfo
    {
        public Guid DeckGuid = new Guid("fa550ad9-e5d6-4472-af21-abf178eb5016");
        public long IterationCount = 7;
        public SortedList<string, long> cards = new SortedList<string, long> { ["sv3_125"] = 3 };
        public string deckName = "Zoë \"Q\"";
    }

    static void DeckLists()
    {
        Console.WriteLine("\n56. the player's deck: read from deckInfo, one line of JSON");
        var deck = SampleDeck();
        const string expected = "{\"v\":1,\"deckGuid\":\"fa550ad9-e5d6-4472-af21-abf178eb5016\",\"iterationCount\":4,\"name\":\"<i></i>\"," +
                                "\"deckDefinitionId\":\"bnb-me3\",\"size\":40,\"cards\":{\"me1_131\":2,\"sv1_1\":34,\"sv5_144\":2,\"sv8_174\":2}}";
        Check(deck != null && deck.ToJson() == expected, "keys in order, cards sorted by id: " + deck?.ToJson());
        Check(deck.ToBytes().SequenceEqual(Encoding.ASCII.GetBytes(expected)), "the file is those bytes: ASCII, no BOM, no line break");
        using (var doc = JsonDocument.Parse(deck.ToJson()))
            Check(doc.RootElement.GetProperty("cards").EnumerateObject().Sum(p => p.Value.GetInt32()) == 40, "valid JSON; the counts add up to size");

        var other = DeckList.FromDeckInfo(new OtherDeckInfo());
        Check(other != null && other.ToJson() == "{\"v\":1,\"deckGuid\":\"fa550ad9-e5d6-4472-af21-abf178eb5016\",\"iterationCount\":7," +
              "\"name\":\"Zo\\u00eb \\\"Q\\\"\",\"deckDefinitionId\":null,\"size\":3,\"cards\":{\"sv3_125\":3}}",
              "a Guid, long counts, no size or definition id, non-ASCII name: " + other?.ToJson());

        Check(DeckList.FromDeckInfo(new DeckInfo()) == null, "no cards (the opponent's deckInfo): no deck");
        Check(DeckList.FromDeckInfo(null) == null && DeckList.FromDeckInfo("not a deck") == null, "no deckInfo, or something else: no deck");
        var odd = SampleDeckInfo();
        odd.cards = new Dictionary<string, int> { ["sv8_174"] = 0, [""] = 4, ["sv5_144"] = -1, [new string('x', 65)] = 1, ["me1_131"] = 100 };
        Check(DeckList.FromDeckInfo(odd) == null, "zero, negative, empty, too long or absurd entries are skipped (none left: no deck)");
        var huge = SampleDeckInfo();
        huge.cards = Enumerable.Range(0, DeckList.MaxCards + 1).ToDictionary(i => "sv1_" + i, i => 1);
        Check(DeckList.FromDeckInfo(huge) == null, "more than " + DeckList.MaxCards + " ids is not a deck");
        Check(DeckList.Same(SampleDeck(), SampleDeck()) && !DeckList.Same(SampleDeck(), other) && DeckList.Same(null, null) && !DeckList.Same(deck, null),
              "Same compares the file content");

        Console.WriteLine("57. ReadMatch gives our deck, never the opponent's");
        var lines = new List<string>();
        var api = new ReflectionGameApi(lines.Add, null) { Assemblies = () => new[] { typeof(Program).Assembly } };
        StartMatch("deck-1", true, "Gary");
        NetworkMatchController.playerDetails[0].deckInfo = SampleDeckInfo();
        NetworkMatchController.playerDetails[1].deckInfo = new DeckInfo { deckName = "Their deck", cards = new Dictionary<string, int> { ["sv2_1"] = 60 } };
        var snap = api.ReadMatch();
        Check(snap?.MyDeckList != null && snap.MyDeckList.ToJson() == expected && snap.MyDeck == "<i></i>", "our deck's cards are read");
        NetworkMatchController.playerDetails[0].deckInfo = new DeckInfo { deckName = "Charizard ex" };
        snap = api.ReadMatch();
        Check(snap.MyDeckList == null && snap.MyDeck == "Charizard ex", "no cards: no deck list, the name still");
        Check(!lines.Any(l => l.Contains("issue #15")), "the research line of issue #15 is gone");
        api.Unhook();
        NetworkMatchController.currentMatchInfo = null;

        Console.WriteLine("58. each distinct deck saved once, in decks\\<sha256>.deck; the seal names it");
        var id = NewIdentity(out var idDir);
        var dir = TempDir();
        var mode = SealMode.Footer;
        var store = new LogStore(dir) { Sealer = NewSealer(id) };
        store.Sealer.Mode = () => mode;
        var sha = Hex(SHA256.HashData(SampleDeck().ToBytes()));
        var deckPath = LogStore.DeckPathFor(dir, sha);
        var m = Match("d-1", "Gary", null);
        m.MyDeckList = SampleDeck();
        var p1 = store.Save(m, "Setup\nTurn 1", null);
        var s1 = ReadSealed(p1);
        Check(deckPath == Path.Combine(dir, "decks", sha + ".deck") && File.ReadAllBytes(deckPath).SequenceEqual(SampleDeck().ToBytes()),
              "decks\\<sha256>.deck holds the deck's bytes");
        Check(s1.Str("deckSha256") == sha && Problem(s1) == null, "the seal names that SHA-256 and verifies (" + Problem(s1) + ")");
        Check(s1.Json.StartsWith("{\"v\":2,") && s1.Json.Contains(",\"deck\":\"Charizard ex\",\"deckSha256\":\""), "v 2; deckSha256 right after deck");
        Check(!Directory.GetFiles(dir).Any(f => f.EndsWith(".deck")), "nothing next to the .txt");

        var written = File.GetLastWriteTimeUtc(deckPath);
        var m2 = Match("d-2", "Misty");
        m2.MyDeckList = SampleDeck();
        var p2 = store.Save(m2, "another match, same deck", null);
        Check(ReadSealed(p2).Str("deckSha256") == sha && Directory.GetFiles(Path.Combine(dir, "decks")).Length == 1 && File.GetLastWriteTimeUtc(deckPath) == written,
              "a second match with the same deck reuses the file (not written again)");

        m.Won = true; m.Winner = 1;                                   // the result arrives: the file moves
        var p3 = store.Save(m, "Setup\nTurn 1\nAsh wins.", p1);
        Check(p3 != p1 && File.Exists(deckPath) && Problem(ReadSealed(p3)) == null, "a log that moves keeps naming the same deck");

        var changedDeck = SampleDeck();
        changedDeck.Cards["sv1_1"] = 33;
        changedDeck.Cards["sv9_1"] = 1;
        var m4 = Match("d-4", "Brock");
        m4.MyDeckList = changedDeck;
        var s4 = ReadSealed(store.Save(m4, "edited deck", null));
        Check(s4.Str("deckSha256") != sha && Directory.GetFiles(Path.Combine(dir, "decks")).Length == 2 && Problem(s4) == null, "an edited deck is a second file");

        File.WriteAllText(deckPath, "damaged");
        var m5 = Match("d-5", "Erika");
        m5.MyDeckList = SampleDeck();
        var p5 = store.Save(m5, "after damage", null);
        Check(File.ReadAllBytes(deckPath).SequenceEqual(SampleDeck().ToBytes()) && Problem(ReadSealed(p5)) == null, "a damaged deck file is written again");

        mode = SealMode.Sidecar;
        var ms = Match("d-6", "Misty");
        ms.MyDeckList = SampleDeck();
        var ps = store.Save(ms, "sidecar log", null);
        Check(File.Exists(LogStore.SidecarPathFor(ps)) && ReadSealed(ps).Str("deckSha256") == sha && Problem(ReadSealed(ps)) == null, "Sidecar mode names the deck too");

        mode = SealMode.Off;
        var offDir = TempDir();
        var offStore = new LogStore(offDir) { Sealer = NewSealer(id) };
        offStore.Sealer.Mode = () => SealMode.Off;
        var mo = Match("d-7", "Brock");
        mo.MyDeckList = SampleDeck();
        var po = offStore.Save(mo, "unsealed log", null);
        Check(ReadSealed(po) == null && !Directory.Exists(Path.Combine(offDir, "decks")), "Off: no seal, so no deck (nothing would name it)");
        mode = SealMode.Footer;

        var blockedDir = TempDir();
        Directory.CreateDirectory(blockedDir);
        File.WriteAllText(Path.Combine(blockedDir, "decks"), "a file where the folder goes");
        var blockedStore = new LogStore(blockedDir) { Sealer = NewSealer(id) };
        var mb = Match("d-8", "Erika");
        mb.MyDeckList = SampleDeck();
        var sb = ReadSealed(blockedStore.Save(mb, "blocked deck", null));
        Check(sb.Str("deckSha256") == null && Problem(sb) == null, "a deck that cannot be saved: the log is saved and sealed without it (deckSha256 null)");

        var idx = File.ReadAllText(Path.Combine(dir, LogStore.IndexFile));
        Check(idx.TrimStart('\uFEFF').StartsWith(LogStore.IndexHeader), "index.csv unchanged in shape");

        Console.WriteLine("59. the watcher takes the deck at match start and keeps it");
        var sink = new LogStore(TempDir()) { Sealer = NewSealer(id) };
        var api2 = new ReflectionGameApi(_ => { }, null) { Assemblies = () => new[] { typeof(Program).Assembly } };
        var watcher = new MatchWatcher(api2, sink, _ => { });
        var t = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        StartMatch("deck-2", true, "Gary");
        NetworkMatchController.playerDetails[0].deckInfo = SampleDeckInfo();
        Log("Setup");
        watcher.Tick(t);
        GameOver(1);
        Log("Ash wins.");
        for (int i = 1; i <= 20; i++) watcher.Tick(t.AddSeconds(i));
        NetworkMatchController.playerDetails[0].deckInfo = new DeckInfo { cards = new Dictionary<string, int> { ["sv9_9"] = 60 } };   // the next deck shows up early
        for (int i = 21; i <= 25; i++) watcher.Tick(t.AddSeconds(i));
        var saved = watcher.LastSavedPath;
        Check(saved != null && ReadSealed(saved).Deck != null && ReadSealed(saved).Deck.SequenceEqual(SampleDeck().ToBytes()),
              "the match's deck is the deck it was played with");
        Check(Problem(ReadSealed(saved)) == null, "and the seal names it");
        api2.Unhook();
        NetworkMatchController.currentMatchInfo = null;

        Console.WriteLine("60. seals of earlier builds (v1) are still read");
        var v2 = ReadSealed(p3);
        var v1Json = System.Text.RegularExpressions.Regex.Replace(v2.Json.Replace("{\"v\":2,", "{\"v\":1,"), ",\"deckSha256\":(null|\"[0-9a-f]{64}\"),\"queue\":(null|\"[A-Za-z]+\"),\"format\":(null|\"[A-Za-z]+\")", "")
                         .Replace("\"seq\":" + v2.Seq + ",", "\"seq\":40,");
        var v1Sig = Convert.ToBase64String(id.Sign(Encoding.ASCII.GetBytes(v1Json)));
        var v1Block = "-----BEGIN PTCGL-ABL SEAL v1-----\n" + v1Json + "\n" + v1Sig + "\n-----END PTCGL-ABL SEAL v1-----\n";
        var r1 = SealReader.ParseBlock(v1Block);
        Check(r1 != null && r1.Seq == 40 && r1.KeyId == id.KeyId && r1.Modulus != null, "SealReader parses a v1 block");
        Check(SealReader.ParseBlock(v1Block.Replace("SEAL v1", "SEAL v2")) == null && SealReader.ParseBlock(v1Block.Replace("SEAL v1", "SEAL v3")) == null,
              "a BEGIN line that does not match \"v\", or an unknown version, is not a seal");
        var oldDir = TempDir();
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "old.txt"), "﻿old log\n\n" + v1Block, new UTF8Encoding(false));
        File.Delete(Path.Combine(idDir, SigningIdentity.ChainFileName));
        var again = new SigningIdentity(idDir, new PortableRsa());
        again.Load(oldDir);
        again.GetChain(out var seq, out var last);
        Check(seq == 40 && last == LogSealer.ChainHash(v1Json, v1Sig), "chain.json is rebuilt from a v1 seal in the logs folder");
        var next = new LogStore(oldDir) { Sealer = NewSealer(again) };
        var pn = next.Save(Match("d-5"), "after the update", null);
        Check(ReadSealed(pn).Seq == 41 && ReadSealed(pn).Prev == last, "and the first v2 seal follows it");
        Cleanup(dir, idDir, oldDir, sink.Folder, offDir, blockedDir);
    }
}
