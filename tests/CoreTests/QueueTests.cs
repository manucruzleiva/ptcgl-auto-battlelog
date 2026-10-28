using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtcglAutoBattleLog.Core;

// The match's queue and format with each log (issue #26): read from NetworkMatchController's
// gameplayType and gameMode, kept as the client names them, written to index.csv and the seal.
static partial class Program
{
    static void QueueAndFormat()
    {
        Console.WriteLine("\n61. ReadMatch gives the queue and the format, as the client names them");
        var lines = new List<string>();
        var api = new ReflectionGameApi(lines.Add, null) { Assemblies = () => new[] { typeof(Program).Assembly } };
        StartMatch("q-1", true, "Gary");
        var snap = api.ReadMatch();
        Check(snap.Queue == "Ranked" && snap.Format == "Standard", "Standard ranked: Ranked, Standard");
        NetworkMatchController.gameplayType = GameplayType.Casual;
        NetworkMatchController.gameMode = MatchLogic.GameMode.Expanded;
        snap = api.ReadMatch();
        Check(snap.Queue == "Casual" && snap.Format == "Expanded", "casual Expanded: Casual, Expanded");
        NetworkMatchController.gameplayType = GameplayType.BuildAndBattle;
        NetworkMatchController.gameMode = MatchLogic.GameMode.BuildAndBattle;
        snap = api.ReadMatch();
        Check(snap.Queue == "BuildAndBattle" && snap.Format == "BuildAndBattle", "Build & Battle: BuildAndBattle, BuildAndBattle");
        NetworkMatchController.gameMode = MatchLogic.GameMode.NONE;
        Check(api.ReadMatch().Format == null, "gameMode NONE: no format");

        NetworkMatchController.gameplayType = null;
        Check(api.ReadMatch().Queue == null, "no gameplayType and no direct match: no queue");
        NetworkMatchController.directMatchOpponent = new SignedAccountId { id = "opp-9" };
        snap = api.ReadMatch();
        Check(snap.Queue == ReflectionGameApi.FriendQueue, "no gameplayType, a direct opponent: Friend");
        NetworkMatchController.directMatchOpponent = null;
        NetworkMatchController.lobbyCode = "ABC123";
        snap = api.ReadMatch();
        Check(snap.Queue == ReflectionGameApi.FriendQueue, "no gameplayType, a lobby code: Friend");
        Check(typeof(MatchSnapshot).GetFields().All(f => !(f.GetValue(snap) is string v) || !v.Contains("ABC123")),
              "the lobby code itself is kept nowhere");
        NetworkMatchController.gameplayType = GameplayType.Casual;
        Check(api.ReadMatch().Queue == "Casual", "a gameplayType of its own wins over the friend signals");
        Check(!lines.Any(l => l.Contains("queue")), "no warning");
        api.Unhook();
        NetworkMatchController.currentMatchInfo = null;

        Console.WriteLine("62. the watcher keeps the match's queue; index.csv and the seal carry it");
        var id = NewIdentity(out var idDir);
        var dir = TempDir();
        var store = new LogStore(dir) { Sealer = NewSealer(id) };
        var api2 = new ReflectionGameApi(_ => { }, null) { Assemblies = () => new[] { typeof(Program).Assembly } };
        var watcher = new MatchWatcher(api2, store, _ => { });
        var t = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        StartMatch("q-2", true, "Gary");
        NetworkMatchController.gameplayType = GameplayType.Casual;
        NetworkMatchController.gameMode = MatchLogic.GameMode.Expanded;
        Log("Setup");
        watcher.Tick(t);
        GameOver(1);
        Log("Ash wins.");
        for (int i = 1; i <= 20; i++) watcher.Tick(t.AddSeconds(i));
        NetworkMatchController.gameplayType = GameplayType.Ranked;     // the next match's queue shows up early
        NetworkMatchController.gameMode = MatchLogic.GameMode.Standard;
        for (int i = 21; i <= 25; i++) watcher.Tick(t.AddSeconds(i));
        var saved = watcher.LastSavedPath;
        var s = ReadSealed(saved);
        Check(s.Str("queue") == "Casual" && s.Str("format") == "Expanded" && Problem(s) == null, "the seal names the queue and format the match was played in");
        var rows = File.ReadAllLines(Path.Combine(dir, LogStore.IndexFile));
        Check(rows[0].TrimStart('﻿') == "Date,Result,Opponent,Player,Deck,Duration (min),MatchId,File,Queue,Format", "index.csv header ends with Queue,Format");
        Check(rows.Length == 2 && rows[1].EndsWith("," + Path.GetFileName(saved) + ",Casual,Expanded"), "the row ends with them: " + rows.Last());
        Check(!Path.GetFileName(saved).Contains("Casual"), "the file name is unchanged");
        api2.Unhook();
        NetworkMatchController.currentMatchInfo = null;

        var none = Match("q-3", "Brock");
        none.Queue = none.Format = null;
        var sn = ReadSealed(store.Save(none, "no queue known", null));
        Check(sn.Str("queue") == null && sn.Str("format") == null && Problem(sn) == null, "unknown: null in the seal");
        Check(File.ReadAllLines(Path.Combine(dir, LogStore.IndexFile)).Last().EndsWith(",,"), "and empty in index.csv");

        Console.WriteLine("63. an index.csv from before Queue and Format");
        var oldDir = TempDir();
        Directory.CreateDirectory(oldDir);
        var index = Path.Combine(oldDir, LogStore.IndexFile);
        var oldRow = "2026-10-19 21:34:05,VICTORY,\"Gary, Jr.\",Ash,Charizard ex,12,m-0,\"2026-10-19 21.34.05 - VICTORY vs Gary, Jr..txt\"";
        File.WriteAllText(index, LogStore.IndexHeaderV1 + "\r\n" + oldRow + "\r\n", new UTF8Encoding(true));
        var oldStore = new LogStore(oldDir);
        var log = new List<string>();
        oldStore.MigrateLegacy(log.Add);
        var after = File.ReadAllLines(index);
        Check(after.Length == 2 && after[0].TrimStart('﻿') == LogStore.IndexHeader && after[1] == oldRow, "the header gets the two columns, old rows stay as they are");
        Check(log.Count == 1 && log[0].Contains("Queue"), "logged once: " + string.Join(" | ", log));
        log.Clear();
        oldStore.MigrateLegacy(log.Add);
        Check(log.Count == 0 && File.ReadAllLines(index).SequenceEqual(after), "and only once");

        var late = Match("q-4", "Misty", won: null);
        var p1 = oldStore.Save(late, "no result yet", null);
        late.Won = true;
        late.Winner = 1;
        var p2 = oldStore.Save(late, "no result yet, then a win", p1);
        var final = File.ReadAllLines(index);
        Check(p1 != p2 && final.Length == 3 && final[1] == oldRow && final[2].EndsWith("," + Path.GetFileName(p2) + ",Ranked,Standard"),
              "a renamed log's row is found by its File column and rewritten in place");
        Check(LogStore.SplitCsv(oldRow).Count == 8 && LogStore.SplitCsv(oldRow)[7] == "2026-10-19 21.34.05 - VICTORY vs Gary, Jr..txt" &&
              LogStore.SplitCsv("a,\"b \"\"c\"\"\",").SequenceEqual(new[] { "a", "b \"c\"", "" }), "SplitCsv unquotes fields");
        Cleanup(dir, idDir, oldDir);
    }
}
