using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PtcglAutoBattleLog.Tray.Logic;

static partial class Program
{
    // The example in docs/ARCHITECTURE.md, verbatim.
    const string ContractStatusJson = @"{
  ""pluginVersion"": ""1.1.0"",
  ""state"": ""ok"",
  ""message"": ""Watching for matches."",
  ""logsFolder"": ""C:\\Users\\Ash\\Documents\\PTCGL Battle Logs"",
  ""savedThisSession"": 2,
  ""lastFile"": ""C:\\Users\\Ash\\Documents\\PTCGL Battle Logs\\2026-10-19 21.34.05 - VICTORY vs Gary.txt"",
  ""lastSavedUtc"": ""2026-10-19T21:34:05Z"",
  ""heartbeatUtc"": ""2026-10-19T21:35:00Z"",
  ""processId"": 12345
}";

    const string ContractSettingsJson = @"{
  ""gameDir"": null,
  ""bepInExInstalledByUs"": false,
  ""autoUpdate"": true,
  ""notifyOnSave"": true,
  ""lastUpdateCheckUtc"": null,
  ""skippedVersion"": null,
  ""disclaimerAccepted"": 0
}";

    static bool IsDefault(Settings s) =>
        s != null && s.GameDir == null && !s.BepInExInstalledByUs && s.AutoUpdate && s.NotifyOnSave && s.LastUpdateCheckUtc == null && s.SkippedVersion == null && s.DisclaimerAccepted == 0 &&
        s.UpdateChannel == Settings.StableChannel && !s.BetaUpdates;

    static void SettingsTests()
    {
        Console.WriteLine("6. Settings");
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        var logged = new List<string>();
        Action<string> log = logged.Add;

        Check(IsDefault(new Settings()), "new Settings has the contract defaults");
        Check(IsDefault(Settings.Load(path, log)) && logged.Count == 0, "missing file -> defaults, nothing logged");
        Check(IsDefault(Settings.Load(Path.Combine(dir, "no", "such", "dir.json"), null)), "missing folder, null log -> defaults");

        File.WriteAllText(path, ContractSettingsJson);
        Check(IsDefault(Settings.Load(path, log)), "contract example parses to defaults");
        File.WriteAllText(path, "{}");
        Check(IsDefault(Settings.Load(path, log)), "{} -> defaults (true fields stay true)");
        File.WriteAllText(path, "{\"gameDir\":\"G:\\\\PTCGL\",\"autoUpdate\":false,\"someFutureField\":[1,2,{\"x\":1}]}");
        var partial = Settings.Load(path, log);
        Check(partial.GameDir == @"G:\PTCGL" && !partial.AutoUpdate && partial.NotifyOnSave, "given fields read, missing ones default, unknown ignored");
        Check(logged.Count == 0, "valid files log nothing");

        foreach (var corrupt in new[] { "{not json", "", "null", "[]", "\"text\"", "{\"autoUpdate\":\"yes\"}", "\0\0\0" })
        {
            logged.Clear();
            File.WriteAllText(path, corrupt);
            var s = Settings.Load(path, log);
            Check(IsDefault(s) && logged.Count == 1, "corrupt \"" + corrupt.Replace("\0", "\\0") + "\" -> defaults, logged once");
        }
        Check(File.Exists(path + ".corrupt") && IsDefault(Settings.Load(path, null)), "corrupt file set aside and replaced by defaults");
        Check(NoThrow(() => Settings.Load(path, m => throw new Exception("bad logger"))), "a throwing logger is contained");
        Check(IsDefault(Settings.Load(dir, log)), "a folder instead of a file -> defaults");

        var full = new Settings { GameDir = "C:\\Game \"x\" é", BepInExInstalledByUs = true, AutoUpdate = false, NotifyOnSave = false,
                                  LastUpdateCheckUtc = "2026-10-19T21:35:00Z", SkippedVersion = "1.2.0", DisclaimerAccepted = Disclaimer.Version };
        full.Save(path);
        var back = Settings.Load(path, log);
        Check(back.GameDir == full.GameDir && back.BepInExInstalledByUs && !back.AutoUpdate && !back.NotifyOnSave &&
              back.LastUpdateCheckUtc == full.LastUpdateCheckUtc && back.SkippedVersion == "1.2.0" && back.DisclaimerAccepted == Disclaimer.Version, "save/load round trip");
        var raw = File.ReadAllBytes(path);
        var json = Encoding.UTF8.GetString(raw);
        Check(raw[0] == (byte)'{', "UTF-8 without BOM");
        Check(json.IndexOf("\"gameDir\"") < json.IndexOf("\"bepInExInstalledByUs\"") && json.IndexOf("\"lastUpdateCheckUtc\"") < json.IndexOf("\"skippedVersion\"") &&
              json.IndexOf("\"skippedVersion\"") < json.IndexOf("\"disclaimerAccepted\": " + Disclaimer.Version), "fields in contract order");
        File.WriteAllText(path, "{\"disclaimerAccepted\":7}");
        Check(Settings.Load(path, log).DisclaimerAccepted == 7, "disclaimerAccepted read as given (a newer version's acceptance counts)");
        logged.Clear();
        File.WriteAllText(path, "{\"disclaimerAccepted\":\"yes\"}");
        Check(Settings.Load(path, log).DisclaimerAccepted == 0 && logged.Count == 1, "a non-number disclaimerAccepted -> corrupt -> asked again");
        File.WriteAllText(path, "{\"updateChannel\":\"beta\"}");
        Check(Settings.Load(path, log).BetaUpdates, "updateChannel beta read");
        File.WriteAllText(path, "{\"updateChannel\":\"BETA\"}");
        Check(Settings.Load(path, log).BetaUpdates, "updateChannel compared case-insensitively");
        foreach (var other in new[] { "{\"updateChannel\":\"nightly\"}", "{\"updateChannel\":null}", "{\"updateChannel\":\"\"}" })
        {
            File.WriteAllText(path, other);
            Check(!Settings.Load(path, log).BetaUpdates, "unknown or empty updateChannel reads as stable: " + other);
        }
        new Settings { UpdateChannel = Settings.BetaChannel }.Save(path);
        Check(Settings.Load(path, log).BetaUpdates && File.ReadAllText(path).Contains("\"updateChannel\": \"beta\""), "beta channel round trip");
        Check(json.Contains("\n"), "indented for humans");
        Check(!File.Exists(path + ".tmp"), "atomic save left no temp file");
        new Settings().Save(Path.Combine(dir, "new", "settings.json"));
        Check(File.Exists(Path.Combine(dir, "new", "settings.json")), "save creates the folder");
    }

    static void PluginStatusTests()
    {
        Console.WriteLine("7. PluginStatus");
        var dir = TempDir();
        var path = Path.Combine(dir, "plugin-status.json");
        Check(PluginStatus.TryRead(path) == null, "absent -> null");
        File.WriteAllText(path, ContractStatusJson, new UTF8Encoding(false));
        var s = PluginStatus.TryRead(path);
        // The status window's mod line (#42): the contract example is v1.1.0 with a heartbeat at 21:35:00Z.
        var beat = new DateTime(2026, 10, 19, 21, 35, 30, DateTimeKind.Utc);
        Check(PluginStatus.ModVersionLine(null, "1.1.0", beat) == "Mod in the game: not loaded yet", "mod line: no status file -> not loaded yet");
        Check(PluginStatus.ModVersionLine(new PluginStatus { PluginVersion = " " }, "1.1.0", beat) == "Mod in the game: not loaded yet", "mod line: blank version -> not loaded yet");
        Check(PluginStatus.ModVersionLine(s, "1.1.0", beat) == "Mod in the game: v1.1.0 (same as the app)", "mod line: alive, same version");
        Check(PluginStatus.ModVersionLine(s, "1.2.0", beat) == "Mod in the game: v1.1.0 · not the app's v1.2.0: restart the game to update it", "mod line: alive, older than the app");
        Check(PluginStatus.ModVersionLine(s, "1.1.0", beat.AddHours(1)) == "Mod last loaded by the game: v1.1.0", "mod line: game closed, same version");
        Check(PluginStatus.ModVersionLine(s, "1.2.0", beat.AddHours(1)) == "Mod last loaded by the game: v1.1.0 · the game loads v1.2.0 the next time it starts", "mod line: game closed, older");

        Check(s != null, "contract example parses");
        if (s == null) return;
        Check(s.PluginVersion == "1.1.0" && s.State == PluginStatus.StateOk && s.Message == "Watching for matches.", "version, state, message");
        Check(s.LogsFolder == @"C:\Users\Ash\Documents\PTCGL Battle Logs" && s.SavedThisSession == 2 && s.ProcessId == 12345, "folder, count, pid");
        Check(s.LastFile.EndsWith(@"\2026-10-19 21.34.05 - VICTORY vs Gary.txt"), "last file");
        var hb = new DateTime(2026, 10, 19, 21, 35, 0, DateTimeKind.Utc);
        Check(s.HeartbeatUtcValue == hb && s.HeartbeatUtcValue.Value.Kind == DateTimeKind.Utc, "heartbeat parsed as UTC");
        Check(s.LastSavedUtcValue == new DateTime(2026, 10, 19, 21, 34, 5, DateTimeKind.Utc), "last saved parsed");
        Check(s.IsAlive(hb) && s.IsAlive(hb.AddSeconds(60)) && s.IsAlive(hb.AddSeconds(90)), "alive within 90 s");
        Check(!s.IsAlive(hb.AddSeconds(91)) && !s.IsAlive(hb.AddHours(5)), "not alive after 90 s");
        Check(s.IsAlive(hb.AddSeconds(-20)) && !s.IsAlive(hb.AddMinutes(-10)), "small clock skew tolerated, large not");
        Check(s.IsAlive(DateTime.SpecifyKind(hb.AddSeconds(30), DateTimeKind.Unspecified)), "unspecified kind treated as UTC");

        File.WriteAllText(path, "\uFEFF" + ContractStatusJson.Replace("\"2026-10-19T21:34:05Z\"", "null").Replace("\"C:\\\\Users\\\\Ash\\\\Documents\\\\PTCGL Battle Logs\\\\2026-10-19 21.34.05 - VICTORY vs Gary.txt\"", "null"), new UTF8Encoding(false));
        var n = PluginStatus.TryRead(path);
        Check(n != null && n.LastFile == null && n.LastSavedUtcValue == null && n.IsAlive(hb), "BOM tolerated, null values read as null");

        File.WriteAllText(path, "{\"state\":\"starting\",\"heartbeatUtc\":\"yesterday\"}");
        var st = PluginStatus.TryRead(path);
        Check(st != null && st.State == "starting" && st.HeartbeatUtcValue == null && !st.IsAlive(hb), "missing fields ok, bad date -> not alive");
        File.WriteAllText(path, "{\"heartbeatUtc\":\"2026-10-19T21:35:00.123Z\"}");
        Check(PluginStatus.TryRead(path)?.IsAlive(hb) == true, "fractional seconds accepted");
        foreach (var bad in new[] { "", "{", "{\"pluginVersion\":", "[1,2]", "garbage", "{\"savedThisSession\":\"two\"}" })
        {
            File.WriteAllText(path, bad);
            Check(NoThrow(() => PluginStatus.TryRead(path)) && PluginStatus.TryRead(path) == null, "corrupt -> null: " + bad);
        }
        File.WriteAllText(path, "{\"processId\":null,\"savedThisSession\":null,\"heartbeatUtc\":\"2026-10-19T21:35:00Z\"}");
        var nulls = PluginStatus.TryRead(path);
        Check(nulls != null && nulls.ProcessId == 0 && nulls.SavedThisSession == 0 && nulls.IsAlive(hb), "null numbers read as 0, not as a corrupt file");
        Check(PluginStatus.TryRead(dir) == null && PluginStatus.TryRead(null) == null, "folder or null path -> null");

        File.WriteAllText(path, ContractStatusJson, new UTF8Encoding(false));
        var good = PluginStatus.TryReadKeeping(path, null);
        Check(good != null && good.ProcessId == 12345, "keeping read: fresh value when readable");
        File.WriteAllText(path, "{");
        Check(ReferenceEquals(PluginStatus.TryReadKeeping(path, good), good), "keeping read: unreadable but present -> previous kept");
        File.Delete(path);
        Check(PluginStatus.TryReadKeeping(path, good) == null, "keeping read: file gone -> null");
    }

    static void LogFileNameTests()
    {
        Console.WriteLine("14. LogFileName");
        void Parses(string name, string result, string opponent)
        {
            var ok = LogFileName.TryParse(name, out var r, out var o);
            Check(ok && r == result && o == opponent, name + " -> " + r + " / " + (o ?? "(none)"));
        }
        void Fails(string name)
        {
            var ok = LogFileName.TryParse(name, out var r, out var o);
            Check(!ok && r == null && o == null, "rejected: " + (name ?? "(null)"));
        }
        Parses("2026-10-19 21.34.05 - VICTORY vs Gary.txt", "VICTORY", "Gary");
        Parses("2026-10-19 21.34.05 - DEFEAT vs Ash vs Gary.txt", "DEFEAT", "Ash vs Gary");
        Parses("2026-10-19 21.34.05 - VICTORY vs vs.txt", "VICTORY", "vs");
        Parses("2026-10-19 21.34.05 - VICTORY vs  vs Gary .txt", "VICTORY", " vs Gary ");
        Parses("2026-10-19 21.34.05 - NO-RESULT vs Misty.txt", "NO-RESULT", "Misty");
        Parses("2026-10-19 21.34.05 - NO-RESULT.txt", "NO-RESULT", null);
        Parses("2026-10-19 21.34.05 - EXPORTED.txt", "EXPORTED", null);
        Parses("2026-10-19 21.34.05 - EXPORTED (2).txt", "EXPORTED", null);
        Parses("2026-10-19 21.34.05 - VICTORY vs Gary (2).txt", "VICTORY", "Gary");
        Parses("2026-10-19 21.34.05 - VICTORY vs Gary (13).txt", "VICTORY", "Gary");
        Parses("2026-10-19 21.34.05 - VICTORY vs Gary (2) (3).txt", "VICTORY", "Gary (2)");
        Parses("2026-10-19 21.34.05 - VICTORY vs Gary (1).txt", "VICTORY", "Gary (1)");
        Parses("2026-10-19 21.34.05 - VICTORY vs Team (Rocket).txt", "VICTORY", "Team (Rocket)");
        Parses("2026-10-19 21.34.05 - DEFEAT vs Gary__.TXT", "DEFEAT", "Gary__");
        Parses(Path.Combine("C:", "Logs", "2026-10-19 21.34.05 - DEFEAT vs Gary.txt"), "DEFEAT", "Gary");
        Fails(null);
        Fails("");
        Fails(".txt");
        Fails("index.csv");
        Fails("notes.txt");
        Fails("2026-10-19 21.34.05 - VICTORY vs Gary.txt.tmp");
        Fails("2026-10-19 21.34.05 - VICTORY vs Gary");
        Fails("2026-10-19 21.34.05 - VICTORY vs .txt");
        Fails("2026-10-19 21.34.05 - VICTORYvs Gary.txt");
        Fails("2026-10-19 21.34.05 - WIN vs Gary.txt");
        Fails("2026-10-19 21.34.05 - victory vs Gary.txt");
        Fails("2026-10-19 21.34.05 - VICTORIA vs Gary.txt");
        Fails("2026-10-19 21.34.05 -VICTORY.txt");
        Fails("2026-10-19 21:34:05 - VICTORY.txt");
        Fails("2026-13-45 21.34.05 - VICTORY.txt");
        Fails("2026-10-19 21.34.05 - .txt");
        Fails("2026-10-19 21.34.05 - EXPORTED (0).txt");
    }

    static void TrayLogTests()
    {
        Console.WriteLine("15. TrayLog");
        var dir = TempDir();
        var path = Path.Combine(dir, "sub", "tray.log");
        var log = new TrayLog(path);
        log.Info("hello");
        Exception boom;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { boom = e; }
        log.Error("it failed", boom);
        log.Error("no exception");
        var lines = File.ReadAllLines(path);
        var lineRx = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} (INFO |ERROR) ");
        Check(lines.Length >= 3 && lineRx.IsMatch(lines[0]) && lines[0].EndsWith("INFO  hello"), "info line: " + lines[0]);
        Check(lines[1].Contains("ERROR it failed: System.InvalidOperationException: boom"), "error line carries the exception");
        Check(lines.Any(l => l.EndsWith("ERROR no exception")), "error without exception");

        var small = Path.Combine(dir, "small.log");
        var sl = new TrayLog(small, 4096);
        for (int i = 0; i < 500; i++) sl.Info("line " + i.ToString("D4") + " " + new string('.', 40));
        Check(File.Exists(small + ".1"), "rotated to .1");
        Check(new FileInfo(small).Length <= 4096 && new FileInfo(small + ".1").Length <= 4096, "both files within the limit");
        Check(File.ReadAllLines(small).Last().Contains("line 0499"), "newest line in the current file");
        Check(!File.Exists(small + ".2"), "only one old file kept");

        // Concurrency: many threads, two instances on one file, no lost or torn lines.
        var conc = Path.Combine(dir, "conc.log");
        var a = new TrayLog(conc);
        var b = new TrayLog(conc);
        int threads = 8, per = 400;
        var errors = 0;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            try { for (int i = 0; i < per; i++) (i % 2 == 0 ? a : b).Info("t" + t + " i" + i + " " + new string('x', t * 10)); }
            catch { Interlocked.Increment(ref errors); }
        });
        var cl = File.ReadAllLines(conc);
        var wellFormed = new Regex(@"^\S+ \S+ INFO  t(\d) i(\d+) (x*)$");
        Check(errors == 0, "no exceptions from concurrent writers");
        Check(cl.Length == threads * per, "every line written once (" + cl.Length + "/" + threads * per + ")");
        Check(cl.All(l => { var m = wellFormed.Match(l); return m.Success && m.Groups[3].Length == int.Parse(m.Groups[1].Value) * 10; }), "no torn or interleaved lines");
        Check(cl.Select(l => wellFormed.Match(l)).Select(m => m.Groups[1].Value + "/" + m.Groups[2].Value).Distinct().Count() == threads * per, "no duplicates");

        // Concurrency across rotation.
        var rot = Path.Combine(dir, "rot.log");
        var r1 = new TrayLog(rot, 16 * 1024);
        Parallel.For(0, threads, t => { for (int i = 0; i < 300; i++) r1.Info("t" + t + " i" + i + " " + new string('y', 50)); });
        var both = File.ReadAllLines(rot).Concat(File.ReadAllLines(rot + ".1")).ToArray();
        Check(both.All(l => Regex.IsMatch(l, @"^\S+ \S+ INFO  t\d i\d+ y{50}$")), "lines intact across rotations (" + both.Length + " kept)");
        Check(new FileInfo(rot).Length <= 16 * 1024, "current file within the limit");

        // Never throws.
        var blocker = Path.Combine(dir, "a-file");
        File.WriteAllText(blocker, "x");
        Check(NoThrow(() => new TrayLog(Path.Combine(blocker, "tray.log")).Info("x")), "unwritable path does not throw");
        Check(NoThrow(() => { var n = new TrayLog(null); n.Info("x"); n.Error("y", new Exception()); }), "null path does not throw");
        Check(NoThrow(() => new TrayLog("bad\0name").Error(null, null)), "invalid path and null message do not throw");
    }
}
