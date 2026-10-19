using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using PtcglAutoBattleLog.Core;
using _Rainier.Scripts.BattleLog;

static partial class Program
{
    static int failures, checks;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        checks++;
        if (!ok) failures++;
    }

    static int Main()
    {
        Scenario();
        WatcherEdgeCases();
        LogStoreEdgeCases();
        LegacyMigration();
        StatusWriter();
        SealFormat();
        SealChain();
        SealModes();
        SealFailures();
        SealIdentity();
        SealInMatches();
        OutputFolderSetting();
        SealChainRepair();
        SealSamples();

        Console.WriteLine(failures == 0 ? "\nALL PASSED (" + checks + " checks)" : "\n" + failures + " FAILED (of " + checks + " checks)");
        return failures == 0 ? 0 : 1;
    }

    // ================================================================== fixtures

    sealed class Guard : IClipboardGuard
    {
        public int Begun, Ended;
        public IDisposable Begin()
        {
            Begun++;
            var before = BattleLogExporter.Clipboard;
            return new Scope(() => { Ended++; BattleLogExporter.Clipboard = before; });
        }
        sealed class Scope : IDisposable { readonly Action a; public Scope(Action a) { this.a = a; } public void Dispose() => a(); }
    }

    /// <summary>A LogStore that can be told to fail, like a full disk or a vanished drive.</summary>
    sealed class FlakySink : ILogSink
    {
        public LogStore Inner;
        public bool Fail;
        public string Save(MatchRecord m, string text, string existingPath)
        {
            if (Fail) throw new IOException("disk full");
            return Inner.Save(m, text, existingPath);
        }
    }

    /// <summary>One watcher on the fake game, writing to its own temp folder.</summary>
    sealed class Rig : IDisposable
    {
        public readonly string Dir = TempDir();
        public readonly Guard Guard = new Guard();
        public readonly ReflectionGameApi Api;
        public readonly LogStore Store;
        public readonly MatchWatcher Watcher;
        public DateTime T = new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);

        public Rig(Func<LogStore, ILogSink> sink = null, LogSealer sealer = null)
        {
            Api = new ReflectionGameApi(m => Console.WriteLine("    [api] " + m), Guard)
            {
                Assemblies = () => new[] { typeof(Program).Assembly },
            };
            Store = new LogStore(Dir) { Sealer = sealer };
            Watcher = new MatchWatcher(Api, sink == null ? Store : sink(Store), m => Console.WriteLine("    [watch] " + m));
        }

        public void Tick(double seconds) { T = T.AddSeconds(seconds); Watcher.Tick(T); }
        public string[] Txts() => Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.txt").OrderBy(f => f).ToArray() : new string[0];
        public string[] Names() => Txts().Select(Path.GetFileName).ToArray();
        public string IndexPath => Path.Combine(Dir, LogStore.IndexFile);
        public string[] Index() => File.Exists(IndexPath) ? File.ReadAllLines(IndexPath) : new string[0];

        public void Dispose()
        {
            Api.Unhook();       // OnExport is static: a stale rig would keep saving manual exports
            NetworkMatchController.currentMatchInfo = null;
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    static string TempDir() => Path.Combine(Path.GetTempPath(), "ptcgl-tests-" + Guid.NewGuid().ToString("N"));

    static MatchInfo Info => NetworkMatchController.currentMatchInfo;

    static void StartMatch(string id, bool iAmP1, string opp)
    {
        MatchManager.instance = new MatchManager();
        NetworkMatchController.currentMatchInfo = new MatchInfo { isPlayer1 = iAmP1, board = new MatchBoard { matchID = id } };
        NetworkMatchController.playerDetails = new() {
            new PlayerDetail { playerId = "me-123", playerName = "Ash", deckInfo = new DeckInfo { deckName = "Charizard ex" } },
            new PlayerDetail { playerId = "opp-9", playerName = opp },
        };
    }

    static void Log(string line) => MatchManager.instance.Entries.Add(new Entry { Line = line });

    static void GameOver(int winner)
    {
        Info.gameOver = true;
        Info.winner = winner;
    }

    static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    // ================================================================== the whole flow

    static void Scenario()
    {
        using var rig = new Rig();
        var t = rig;

        Console.WriteLine("1. no match loaded");
        t.Tick(1);
        Check(t.Txts().Length == 0, "nothing written");
        Check(t.Api.Resolved, "fake game types resolved by reflection");

        Console.WriteLine("2. a match that ends with a win; final entry lands late");
        StartMatch("m-1", iAmP1: false, opp: "Gary<>");
        Log("Setup"); Log("Turn 1 - Ash played Charmander");
        t.Tick(0.5); t.Tick(0.5);
        Check(t.Txts().Length == 0, "nothing written mid-match");
        Check(BattleLogExporter.Exports == 0, "exporter not called mid-match");
        Log("Gary's Pikachu was Knocked Out!");
        GameOver(2);     // Ash is player 2
        t.Tick(0.5); t.Tick(0.5);
        Check(t.Txts().Length == 1, "one log file after game over");
        var f1 = t.Txts().FirstOrDefault() ?? "";
        Check(Path.GetFileName(f1).Contains(" - VICTORY vs Gary__.txt"), "file named with result and sanitized opponent: " + Path.GetFileName(f1));
        Check(Regex.IsMatch(Path.GetFileName(f1), @"^\d{4}-\d\d-\d\d \d\d\.\d\d\.\d\d - "), "file name starts with yyyy-MM-dd HH.mm.ss");
        Log("Ash took the last Prize card. Ash wins.");
        t.Tick(3);
        Check(File.ReadAllText(f1).EndsWith("Ash wins."), "later, longer read overwrote the same file");
        Check(t.Txts().Length == 1, "still one file");
        Check(HasBom(File.ReadAllBytes(f1)), "log file is UTF-8 with BOM");
        Check(BattleLogExporter.Clipboard == "player clipboard", "player's clipboard untouched");
        Check(t.Guard.Begun > 0 && t.Guard.Begun == t.Guard.Ended, "clipboard guard wrapped every export");
        Check(t.Watcher.SavedCount == 1 && t.Watcher.LastSavedPath == f1, "SavedCount and LastSavedPath track the save");
        Check(t.Watcher.LastSavedUtc == t.T, "LastSavedUtc is the time of the last write");

        Console.WriteLine("3. player presses Export on the results screen");
        new BattleLogExporter().ExportBattleLog(MatchManager.instance.BattleLogExportData);
        Check(t.Txts().Length == 1, "manual export of the same log adds no file");

        Console.WriteLine("4. back to menu, next match starts with the old log still in memory");
        var oldEntries = MatchManager.instance.Entries;
        NetworkMatchController.currentMatchInfo = null;
        t.Tick(10);
        StartMatch("m-2", iAmP1: true, opp: "Misty");
        MatchManager.instance.Entries = oldEntries;               // stale data from match 1
        t.Tick(0.5);
        MatchManager.instance.Entries = new();
        Log("Setup m2"); Log("Misty conceded.");
        GameOver(2);       // Ash is player 1 -> loss
        t.Tick(1); t.Tick(3); t.Tick(8);
        Check(t.Txts().Length == 2, "second match saved as its own file");
        Check(t.Names().Any(f => f.EndsWith(" - DEFEAT vs Misty.txt")), "loss detected from seat + winner");

        Console.WriteLine("5. stale log in a new match that disconnects before game over");
        NetworkMatchController.currentMatchInfo = null; t.Tick(1);
        var m2 = MatchManager.instance.Entries;
        StartMatch("m-3", iAmP1: true, opp: "Brock");
        MatchManager.instance.Entries = m2;
        t.Tick(1);
        NetworkMatchController.currentMatchInfo = null; t.Tick(1);
        Check(t.Txts().Length == 2, "stale log of the previous match not saved twice");

        Console.WriteLine("6. disconnect after some play, no game over");
        StartMatch("m-4", iAmP1: true, opp: "Erika");
        Log("Setup m4"); Log("Connection lost");
        t.Tick(1);
        NetworkMatchController.currentMatchInfo = null; t.Tick(1);
        Check(t.Names().Any(f => f.EndsWith(" - NO-RESULT vs Erika.txt")), "teardown read saved it without a result");

        Console.WriteLine("7. back-to-back matches with no menu in between");
        StartMatch("m-5", true, "Sabrina"); Log("m5"); t.Tick(1);
        GameOver(1);
        t.Tick(0.1);  // game over seen, but the first read is not due yet
        var keep = MatchManager.instance;
        StartMatch("m-6", true, "Koga");
        MatchManager.instance = keep;  // log of m-5 still there when m-6 is first seen
        t.Tick(0.5);
        Check(t.Names().Any(f => f.EndsWith(" - VICTORY vs Sabrina.txt")), "pending read flushed when the match id changed");

        Console.WriteLine("8. an unrelated log exported with no match tracked");
        NetworkMatchController.currentMatchInfo = null; t.Tick(1);
        t.Tick(400);  // well past the results screen of the last match
        new BattleLogExporter().ExportBattleLog(new IBattleLogData[] { new Entry { Line = "an old replay" } });
        Check(t.Names().Any(f => f.EndsWith(" - EXPORTED.txt")), "saved as its own EXPORTED file");

        var index = t.Index();
        Check(index.Length > 0 && index[0] == "Date,Result,Opponent,Player,Deck,Duration (min),MatchId,File", "index.csv header is the contract's");
        Check(index.Length == 1 + t.Txts().Length, "index.csv has a header and one row per file (" + (index.Length - 1) + ")");
        Check(index.Skip(1).All(r => t.Names().Contains(r.Substring(r.LastIndexOf(',') + 1))), "every row names an existing file");
        Check(index.Any(r => Regex.IsMatch(r, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,VICTORY,Gary<>,Ash,Charizard ex,[\d.]+,m-1,")), "row holds date, result, opponent, player, deck, minutes, match id");
        var bytes = File.ReadAllBytes(t.IndexPath);
        Check(HasBom(bytes), "index.csv starts with a UTF-8 BOM for Excel");
        int boms = Enumerable.Range(0, Math.Max(0, bytes.Length - 2)).Count(i => bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF);
        Check(boms == 1, "BOM written once, not per append (" + boms + ")");
        Check(Enumerable.Range(0, bytes.Length).All(i => bytes[i] != '\n' || (i > 0 && bytes[i - 1] == '\r')), "index.csv uses CRLF");
        Check(!Directory.GetFiles(t.Dir, "*.tmp").Any(), "no temp files left behind");

        Console.WriteLine();
        foreach (var l in index) Console.WriteLine("    " + l);
    }

    // ================================================================== MatchWatcher / GameApi

    static void WatcherEdgeCases()
    {
        Console.WriteLine("\n9. the winner lands after the first read");
        using (var t = new Rig())
        {
            StartMatch("w-1", iAmP1: false, opp: "Blaine");
            Log("Setup"); t.Tick(1);
            GameOver(0);                    // gameOver first, winner a moment later
            t.Tick(0.1); t.Tick(0.5);       // seen, then the first read
            Check(t.Names().Any(f => f.Contains("NO-RESULT vs Blaine")), "first read saved before the winner was known");
            Info.winner = 2;                // Ash is player 2
            t.Tick(0.5);
            Check(t.Names().Length == 1 && t.Names()[0].EndsWith(" - VICTORY vs Blaine.txt"), "file renamed once the winner is known: " + string.Join(" | ", t.Names()));
            var idx = t.Index();
            Check(idx.Length == 2 && idx[1].Contains(",VICTORY,") && idx[1].EndsWith("," + t.Names()[0]), "its index row was rewritten, not duplicated");
            Check(t.Watcher.SavedCount == 1, "still one match saved");
            Check(t.Watcher.LastSavedPath == t.Txts()[0], "LastSavedPath follows the rename");
        }

        Console.WriteLine("10. the match vanishes for a moment (reconnect) and comes back");
        using (var t = new Rig())
        {
            StartMatch("r-1", iAmP1: true, opp: "Lt. Surge");
            Log("Setup r1"); Log("Turn 1"); t.Tick(1);
            var info = Info;
            NetworkMatchController.currentMatchInfo = null;
            t.Tick(1);
            Check(t.Names().Length == 1 && t.Names()[0].Contains("NO-RESULT"), "teardown read kept what there was");
            NetworkMatchController.currentMatchInfo = info;      // same match id again
            t.Tick(1);
            Log("Turn 2"); Log("Lt. Surge conceded.");
            GameOver(1);
            t.Tick(1); t.Tick(3); t.Tick(8);
            Check(t.Names().Length == 1, "one file for the whole match: " + string.Join(" | ", t.Names()));
            Check(t.Names().Length == 1 && t.Names()[0].EndsWith(" - VICTORY vs Lt. Surge.txt"), "named with the final result");
            Check(t.Txts().Length == 1 && File.ReadAllText(t.Txts()[0]).EndsWith("conceded."), "holds the full log");
            Check(t.Index().Length == 2, "one index row");
            Check(t.Watcher.SavedCount == 1, "counted once");
        }

        Console.WriteLine("11. a resumed match that is torn down again without a game over");
        using (var t = new Rig())
        {
            StartMatch("r-2", iAmP1: true, opp: "Sabrina");
            Log("Setup r2"); t.Tick(1);
            var info = Info;
            NetworkMatchController.currentMatchInfo = null; t.Tick(1);
            NetworkMatchController.currentMatchInfo = info; t.Tick(1);
            Log("more play after the reconnect");
            t.Tick(1);
            NetworkMatchController.currentMatchInfo = null; t.Tick(1);
            Check(t.Txts().Length == 1 && File.ReadAllText(t.Txts()[0]).EndsWith("after the reconnect"), "second teardown read the longer log into the same file");
        }

        Console.WriteLine("12. details of the next match show up before its id does");
        using (var t = new Rig())
        {
            StartMatch("n-1", iAmP1: true, opp: "Giovanni");
            Log("Setup n1"); t.Tick(1);
            GameOver(1);
            t.Tick(1); t.Tick(3);
            NetworkMatchController.playerDetails[1].playerName = "Someone Else";
            t.Tick(1); t.Tick(8);
            Check(t.Names().Length == 1 && t.Names()[0].EndsWith("vs Giovanni.txt"), "a finished match keeps its opponent: " + string.Join(" | ", t.Names()));
        }

        Console.WriteLine("13. our account is not among the players");
        using (var t = new Rig())
        {
            StartMatch("a-1", iAmP1: true, opp: "Lance");
            Info.accountID = null;
            Log("Setup a1"); t.Tick(1);
            GameOver(1);
            t.Tick(0.1); t.Tick(0.5);
            Check(t.Names().Length == 1 && t.Names()[0].EndsWith(" - VICTORY.txt"), "no opponent rather than maybe our own name: " + string.Join(" | ", t.Names()));
        }

        Console.WriteLine("14. the board has an empty match id; MatchInfo has it");
        using (var t = new Rig())
        {
            StartMatch("", iAmP1: true, opp: "Karen");
            Info.matchID = "info-7";
            Log("Setup i7"); t.Tick(1);
            GameOver(1);
            t.Tick(0.1); t.Tick(0.5);
            Check(t.Index().Length == 2 && t.Index()[1].Contains(",info-7,"), "match id taken from MatchInfo");
        }

        Console.WriteLine("15. blank opponent name");
        using (var t = new Rig())
        {
            StartMatch("b-1", iAmP1: true, opp: "   ");
            Log("Setup b1"); t.Tick(1);
            GameOver(2);
            t.Tick(0.1); t.Tick(0.5);
            Check(t.Names().Length == 1 && t.Names()[0].EndsWith(" - DEFEAT.txt"), "no dangling \" vs \": " + string.Join(" | ", t.Names()));
        }

        Console.WriteLine("16. a write fails, then works again");
        FlakySink flaky = null;
        using (var t = new Rig(store => flaky = new FlakySink { Inner = store, Fail = true }))
        {
            StartMatch("f-1", iAmP1: true, opp: "Bruno");
            Log("Setup f1"); t.Tick(1);
            GameOver(1);
            t.Tick(0.1); t.Tick(0.5);
            Check(t.Watcher.LastError != null && t.Watcher.LastError.Contains("disk full"), "LastError says why (" + t.Watcher.LastError + ")");
            Check(t.Watcher.SavedCount == 0, "nothing counted");
            flaky.Fail = false;
            t.Tick(3);
            Check(t.Watcher.LastError == null, "LastError cleared by the next successful write");
            Check(t.Names().Length == 1 && t.Watcher.SavedCount == 1, "the retry saved it once");
        }
    }

    // ================================================================== LogStore

    static MatchRecord Record(string opponent, bool? won = true, string deck = "Lugia VSTAR")
    {
        var start = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc);
        return new MatchRecord
        {
            MatchId = "x-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            StartUtc = start, EndUtc = start.AddMinutes(12.34), Won = won,
            MyName = "Ash", OpponentName = opponent, MyDeck = deck,
        };
    }

    static void LogStoreEdgeCases()
    {
        Console.WriteLine("\n17. a culture with another calendar and time separator");
        var dir = TempDir();
        var store = new LogStore(dir);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            var thai = (CultureInfo)new CultureInfo("th-TH").Clone();      // Buddhist calendar: 2026 is 2569
            thai.DateTimeFormat.TimeSeparator = ".";
            CultureInfo.CurrentCulture = thai;
            var r = Record("Gary");
            var path = store.Save(r, "log", null);
            var local = r.EndUtc.Value.ToLocalTime();
            Check(Path.GetFileName(path) == local.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + " - VICTORY vs Gary.txt",
                  "file name uses the Gregorian year: " + Path.GetFileName(path));
            var row = File.ReadAllLines(Path.Combine(dir, LogStore.IndexFile))[1];
            Check(row.StartsWith(local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ","), "index date is yyyy-MM-dd HH:mm:ss: " + row);
            Check(row.Contains(",12.3,"), "minutes with a dot");
        }
        finally { CultureInfo.CurrentCulture = saved; }

        Console.WriteLine("18. CSV quoting");
        {
            var path = store.Save(Record("O'Brien, \"The\" Best", deck: "Line1\nLine2"), "log", null);
            var text = File.ReadAllText(Path.Combine(dir, LogStore.IndexFile));
            Check(text.Contains(",\"O'Brien, \"\"The\"\" Best\",Ash,\"Line1\nLine2\","), "commas, quotes and line breaks are quoted");
            Check(Path.GetFileName(path).Contains("vs O'Brien, _The_ Best"), "quotes replaced in the file name: " + Path.GetFileName(path));
        }

        Console.WriteLine("19. long names, duplicates, surrogates");
        {
            var emoji = char.ConvertFromUtf32(0x1F600);
            var cut = LogStore.Sanitize(new string('x', LogStore.MaxStemLength - 1) + emoji + "yyy");
            Check(cut.Length == LogStore.MaxStemLength - 1 && !cut.Any(char.IsSurrogate), "truncation does not split a surrogate pair");
            Check(LogStore.Sanitize("a" + emoji + "b") == "a" + emoji + "b", "emoji kept when it fits");
            Check(LogStore.Sanitize("A" + (char)0xD800 + "B" + (char)0xDC00) == "A_B_", "lone surrogates replaced");
            Check(LogStore.Sanitize("C:\\x/y*z?<a>|b\"" + (char)1) == "C__x_y_z__a__b__", "Windows-invalid characters replaced");

            var longOpp = new string('L', 300);
            var r = Record(longOpp);
            var a = store.Save(r, "first", null);
            var b = store.Save(r, "second", null);
            Check(Path.GetFileName(a).Length <= 150 && Path.GetFileName(b).Length <= 150, "whole names within 150 chars (" + Path.GetFileName(b).Length + ")");
            Check(Path.GetFileName(b).EndsWith(" (2).txt"), "duplicate gets (2)");
            Check(File.ReadAllText(a) == "first" && File.ReadAllText(b) == "second", "both kept");
        }
        try { Directory.Delete(dir, true); } catch { }

        Console.WriteLine("20. index.csv emptied or edited by hand");
        dir = TempDir();
        store = new LogStore(dir);
        {
            Directory.CreateDirectory(dir);
            var index = Path.Combine(dir, LogStore.IndexFile);
            File.WriteAllBytes(index, new byte[0]);
            store.Save(Record("Gary"), "log", null);
            var lines = File.ReadAllLines(index);
            Check(lines.Length == 2 && lines[0] == LogStore.IndexHeader, "empty file gets the header back");
            Check(HasBom(File.ReadAllBytes(index)), "and the BOM");

            File.WriteAllText(index, LogStore.IndexHeader + "\r\nmy own row without a line break", new UTF8Encoding(true));
            store.Save(Record("Misty"), "log", null);
            lines = File.ReadAllLines(index);
            Check(lines.Length == 3 && lines[1] == "my own row without a line break" && lines[2].Contains(",Misty,"), "new row does not join a hand-edited last line");
        }
        try { Directory.Delete(dir, true); } catch { }

        Console.WriteLine("21. index.csv cannot be opened at all");
        dir = TempDir();
        store = new LogStore(dir);
        {
            Directory.CreateDirectory(Path.Combine(dir, LogStore.IndexFile));     // a folder in its place
            string first = null, second = null;
            try { first = store.Save(Record("Gary"), "log", null); } catch (Exception e) { Console.WriteLine("    " + e.Message); }
            Check(first != null && File.Exists(first), "the log is still saved and its path returned");
            try { second = store.Save(Record("Gary"), "longer log", first); } catch { }
            Check(second == first && Directory.GetFiles(dir, "*.txt").Length == 1, "the next read overwrites it instead of a second file");
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    // ================================================================== 1.0 leftovers

    static void LegacyMigration()
    {
        Console.WriteLine("\n22. logs folder left by version 1.0");
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var index = Path.Combine(dir, LogStore.IndexFile);
        File.WriteAllText(Path.Combine(dir, "estado.txt"), "Estado: OK\r\n");
        File.WriteAllText(index,
            "Fecha,Resultado,Oponente,Jugador,Mazo,Duracion (min),MatchId,Archivo\r\n" +
            "2026-10-01 20:00:00,VICTORIA,Gary,Ash,Charizard ex,12.5,m-1,2026-10-01 20.00.00 - VICTORIA vs Gary.txt\r\n" +
            "2026-10-01 21:00:00,DERROTA,Misty,Ash,\"multi\r\n2026-10-01 22:00:00,VICTORIA,not a row\",8,m-2,x.txt\r\n" +
            "2026-10-01 23:00:00,SIN-RESULTADO,,Ash,,,m-3,c.txt\r\n" +
            "2026-10-02 00:00:00,EXPORTADO,,,,,,d.txt\r\n", new UTF8Encoding(true));
        var store = new LogStore(dir);
        var log = new List<string>();
        store.MigrateLegacy(log.Add);
        var text = File.ReadAllText(index);
        var lines = text.Split("\r\n");
        Check(!File.Exists(Path.Combine(dir, "estado.txt")), "estado.txt removed");
        Check(lines[0] == LogStore.IndexHeader, "header translated");
        Check(lines[1].StartsWith("2026-10-01 20:00:00,VICTORY,Gary,"), "VICTORIA -> VICTORY");
        Check(lines[2].StartsWith("2026-10-01 21:00:00,DEFEAT,Misty,"), "DERROTA -> DEFEAT");
        Check(lines[3] == "2026-10-01 22:00:00,VICTORIA,not a row\",8,m-2,x.txt", "text inside a quoted field left alone");
        Check(lines[4].StartsWith("2026-10-01 23:00:00,NO-RESULT,"), "SIN-RESULTADO -> NO-RESULT");
        Check(lines[5].StartsWith("2026-10-02 00:00:00,EXPORTED,"), "EXPORTADO -> EXPORTED");
        Check(lines[1].EndsWith("VICTORIA vs Gary.txt"), "old file names in the File column kept (the files keep them)");
        Check(HasBom(File.ReadAllBytes(index)), "BOM kept");
        Check(log.Count == 2, "both steps logged");

        var before = File.ReadAllText(index);
        log.Clear();
        store.MigrateLegacy(log.Add);
        Check(File.ReadAllText(index) == before && log.Count == 0, "second run is a no-op");

        store.Save(Record("Brock"), "log", null);
        lines = File.ReadAllLines(index);
        Check(lines.Length == 7 && lines[6].Contains(",VICTORY,Brock,"), "new rows append under the English header");
        Check(!Directory.GetFiles(dir, "*.tmp").Any(), "no temp files left behind");

        var empty = new LogStore(Path.Combine(dir, "does-not-exist"));
        log.Clear();
        empty.MigrateLegacy(log.Add);
        Check(log.Count == 0, "a folder that does not exist yet is fine");
        try { Directory.Delete(dir, true); } catch { }
    }

    // ================================================================== plugin-status.json

    static readonly string[] ContractFields =
        { "pluginVersion", "state", "message", "logsFolder", "savedThisSession", "lastFile", "lastSavedUtc", "heartbeatUtc", "processId" };

    static void StatusWriter()
    {
        Console.WriteLine("\n23. plugin-status.json");
        var root = TempDir();
        var path = PluginStatusWriter.PathFor(root);
        Check(path == Path.Combine(root, "PtcglAutoBattleLog", "plugin-status.json"), "lives in <LocalAppData>\\PtcglAutoBattleLog");

        // The example from docs/ARCHITECTURE.md, byte for byte (line breaks aside).
        var folder = @"C:\Users\Ash\Documents\PTCGL Battle Logs";
        var w = new PluginStatusWriter(path, "1.1.0", folder, 12345);
        var t = new DateTime(2026, 10, 19, 21, 35, 0, DateTimeKind.Utc);
        Check(w.Update(t, PluginStatusWriter.Ok, "Watching for matches.", 2,
                       folder + @"\2026-10-19 21.34.05 - VICTORY vs Gary.txt", new DateTime(2026, 10, 19, 21, 34, 5, DateTimeKind.Utc)),
              "first update writes (directory created)");
        var expected = string.Join("\n",
            "{",
            "  \"pluginVersion\": \"1.1.0\",",
            "  \"state\": \"ok\",",
            "  \"message\": \"Watching for matches.\",",
            "  \"logsFolder\": \"C:\\\\Users\\\\Ash\\\\Documents\\\\PTCGL Battle Logs\",",
            "  \"savedThisSession\": 2,",
            "  \"lastFile\": \"C:\\\\Users\\\\Ash\\\\Documents\\\\PTCGL Battle Logs\\\\2026-10-19 21.34.05 - VICTORY vs Gary.txt\",",
            "  \"lastSavedUtc\": \"2026-10-19T21:34:05Z\",",
            "  \"heartbeatUtc\": \"2026-10-19T21:35:00Z\",",
            "  \"processId\": 12345",
            "}", "");
        var raw = File.ReadAllBytes(path);
        Check(Encoding.UTF8.GetString(raw).Replace("\r\n", "\n") == expected, "matches the contract's example exactly");
        Check(raw[0] == (byte)'{', "UTF-8 without BOM");

        using (var doc = JsonDocument.Parse(raw))
        {
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            Check(names.SequenceEqual(ContractFields), "field names and order as in the contract: " + string.Join(",", names));
            Check(doc.RootElement.GetProperty("savedThisSession").ValueKind == JsonValueKind.Number &&
                  doc.RootElement.GetProperty("processId").GetInt32() == 12345, "numbers are JSON numbers");
            Check(doc.RootElement.GetProperty("logsFolder").GetString() == folder, "backslashes round-trip");
        }
        Check(!File.Exists(path + ".tmp"), "atomic write leaves no .tmp");

        Console.WriteLine("24. nulls, starting state, escaping");
        var nasty = "C:\\Users\\Zoë \"Q\" 日本語 " + char.ConvertFromUtf32(0x1F525) + "\tTab\nLine\r" + (char)1 + (char)0x1F + (char)0x2028 + "/end";
        var w2 = new PluginStatusWriter(path, "1.2.0-beta", nasty, 7);
        w2.Update(t, PluginStatusWriter.Starting, "Waiting for the game to load.", 0, null, null);
        var json = File.ReadAllText(path);
        using (var doc = JsonDocument.Parse(json))
        {
            var e = doc.RootElement;
            Check(e.GetProperty("state").GetString() == "starting", "state starting");
            Check(e.GetProperty("lastFile").ValueKind == JsonValueKind.Null, "lastFile null");
            Check(e.GetProperty("lastSavedUtc").ValueKind == JsonValueKind.Null, "lastSavedUtc null");
            Check(e.GetProperty("logsFolder").GetString() == nasty, "quotes, backslashes, control chars and non-ASCII round-trip");
        }
        Check(!json.Any(c => c < ' ' && c != '\r' && c != '\n'), "no raw control characters in the file");
        Check(json.Contains("\\u0001") && json.Contains("\\u001f") && json.Contains("\\t") && json.Contains("\\n") && json.Contains("\\r"), "control characters escaped");
        Check(json.Contains("Zoë") && json.Contains("日本語"), "non-ASCII written as UTF-8");

        Check(PluginStatusWriter.Quote(null) == "null", "null string -> null");
        Check(PluginStatusWriter.Quote("a" + (char)0xD800 + "b") == "\"a\\ud800b\"", "lone surrogate escaped, not mangled");
        Check(PluginStatusWriter.Date(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)) == "\"2026-01-02T03:04:05Z\"", "date format");
        var localTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        Check(PluginStatusWriter.Date(localTime) == "\"" + localTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z\"", "local times converted to UTC");
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Check(PluginStatusWriter.Date(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)) == "\"2026-01-02T03:04:05Z\"", "date format ignores the current culture");
        }
        finally { CultureInfo.CurrentCulture = saved; }

        Console.WriteLine("25. written on change, otherwise as a heartbeat");
        var w3 = new PluginStatusWriter(path, "1.1.0", folder, 1);
        Check(w3.Update(t, "ok", "Watching for matches.", 0, null, null), "first write");
        Check(!w3.Update(t.AddSeconds(1), "ok", "Watching for matches.", 0, null, null), "same content a second later: not rewritten");
        Check(!w3.Heartbeat(t.AddSeconds(10)), "heartbeat not due at 10 s");
        Check(w3.Update(t.AddSeconds(11), "ok", "Watching for matches.", 1, "f.txt", t.AddSeconds(11)), "a save is written at once");
        Check(!w3.Heartbeat(t.AddSeconds(30)), "heartbeat counts from the last write");
        Check(PluginStatusWriter.HeartbeatInterval <= TimeSpan.FromSeconds(30), "heartbeat interval within the contract's 30 s");
        Check(w3.Heartbeat(t.AddSeconds(11) + PluginStatusWriter.HeartbeatInterval), "heartbeat due after the interval");
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(path)))
            Check(doc.RootElement.GetProperty("heartbeatUtc").GetString() == "2026-10-19T21:35:31Z", "heartbeat time updated");
        Check(w3.Heartbeat(t.AddSeconds(-60)), "clock set back: beats at once instead of going quiet");
        Check(w3.Update(t, "error", "boom", 1, "f.txt", t.AddSeconds(11)), "state change written at once");

        Console.WriteLine("26. concurrent writers, reader never sees half a file");
        var stop = false;
        int reads = 0, bad = 0;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try { using (JsonDocument.Parse(File.ReadAllBytes(path))) reads++; }
                catch (JsonException) { bad++; }
                catch (IOException) { }
            }
        });
        reader.Start();
        var w4 = new PluginStatusWriter(path, "1.1.0", folder, 1);
        var beat = new Thread(() => { for (int i = 0; i < 300; i++) w4.Heartbeat(t.AddSeconds(i * 30)); });
        beat.Start();
        for (int i = 0; i < 300; i++) w4.Update(t.AddSeconds(i), "ok", "msg " + i, i, null, null);
        beat.Join();
        Volatile.Write(ref stop, true);
        reader.Join();
        Check(bad == 0 && reads > 0, "every read parsed (" + reads + " reads)");
        Check(!File.Exists(path + ".tmp"), "no .tmp left");

        Console.WriteLine("27. the folder cannot be written");
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var logged = new List<string>();
        var w5 = new PluginStatusWriter(Path.Combine(blocker, "PtcglAutoBattleLog", PluginStatusWriter.FileName), "1.1.0", folder, 1, logged.Add);
        bool threw = false, wrote = true;
        try
        {
            wrote = w5.Update(t, "ok", "a", 0, null, null);
            w5.Update(t.AddSeconds(1), "ok", "b", 0, null, null);
            w5.Heartbeat(t.AddSeconds(60));
        }
        catch { threw = true; }
        Check(!threw && !wrote, "never throws, reports false");
        Check(logged.Count == 1, "logged once per failure streak (" + logged.Count + ")");

        try { Directory.Delete(root, true); } catch { }
    }
}
