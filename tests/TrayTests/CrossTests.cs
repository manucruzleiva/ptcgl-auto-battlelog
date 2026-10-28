using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using PtcglAutoBattleLog;
using PtcglAutoBattleLog.Core;
using PtcglAutoBattleLog.Tray.Logic;

// Cross-part checks: the plugin and the tray never call each other, they share files. Here the
// plugin's own Unity-free code (src/Plugin/Core, linked into this project) writes them and the
// tray's Logic reads them; the real BepInEx (lib/BepInEx.dll) reads what the tray writes into the
// cfg; and the release tools' output goes through the updater's checks.
static partial class Program
{
    /// <summary>RSA.Create(): works on every OS the tests run on (the plugin's RSACryptoServiceProvider is for Mono).</summary>
    sealed class TestRsa : RsaBackend
    {
        public override RSA Create(int bits) => RSA.Create(bits);
        public override RSA FromXml(string xml) { var r = RSA.Create(); r.FromXmlString(xml); return r; }
        public override byte[] SignHash(RSA key, byte[] h) => key.SignHash(h, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        public override bool VerifyHash(RSA key, byte[] h, byte[] s) => key.VerifyHash(h, s, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // ================================================================== plugin-status.json

    static void StatusFileCrossTests()
    {
        Console.WriteLine("24. plugin-status.json: written by the plugin's PluginStatusWriter, read by the tray");
        var local = TempDir();
        var paths = new AppPaths(local, Path.Combine(local, "Docs"));
        var exe = Path.Combine(TempDir(), "Portable", "PtcglAutoBattleLog.exe");
        Check(PluginStatusWriter.PathFor(local) == paths.PluginStatusFile &&
              PluginStatusWriter.PathFor(local) == AppPaths.ForPortable(exe, local, Path.Combine(local, "Docs")).PluginStatusFile &&
              PluginStatusWriter.PathFor(local) == AppPaths.ForPortableFallback(exe, local, Path.Combine(local, "Docs")).PluginStatusFile,
              "same path on both sides, installed and portable");
        Check(PluginStatusWriter.Starting == PluginStatus.StateStarting && PluginStatusWriter.Ok == PluginStatus.StateOk &&
              PluginStatusWriter.Error == PluginStatus.StateError, "same state names");
        Check(PluginStatusWriter.HeartbeatInterval < TimeSpan.FromSeconds(30) && PluginStatus.AliveWindow == TimeSpan.FromSeconds(90) &&
              PluginStatusWriter.HeartbeatInterval.Ticks * 3 < PluginStatus.AliveWindow.Ticks, "heartbeat well inside the tray's 90 s window");

        var emoji = char.ConvertFromUtf32(0x1F525);
        var nastyFolder = "C:\\Users\\Zo\u00eb \"Q\" \u65e5\u672c\u8a9e " + emoji + "\\PTCGL\tBattle\nLogs\r" + (char)1 + (char)0x1F + "\u2028\u2029/end\\";
        var loneSurrogates = "A" + (char)0xD800 + "B" + (char)0xDC00 + "C" + (char)0xDBFF;
        var cases = new[]
        {
            new { Name = "plain", Folder = @"C:\Users\Ash\Documents\PTCGL Battle Logs", Message = "Watching for matches.", Last = @"C:\Users\Ash\Documents\PTCGL Battle Logs\2026-10-19 21.34.05 - VICTORY vs Gary.txt" },
            new { Name = "quotes, backslashes, control and non-ASCII characters", Folder = nastyFolder, Message = "line 1\nline \"2\"\t\\ end", Last = nastyFolder + "\\x.txt" },
            new { Name = "lone surrogates", Folder = loneSurrogates, Message = loneSurrogates, Last = loneSurrogates },
            new { Name = "nulls", Folder = (string)null, Message = (string)null, Last = (string)null },
        };
        var now = new DateTime(2026, 10, 19, 21, 35, 0, 123, DateTimeKind.Utc);
        foreach (var c in cases)
        {
            var file = PluginStatusWriter.PathFor(local);
            if (File.Exists(file)) File.Delete(file);
            var writer = new PluginStatusWriter(file, "1.1.0", c.Folder, 4242);
            var saved = c.Last == null ? (DateTime?)null : now.AddSeconds(-55).ToLocalTime();   // a Local time is written as UTC
            Check(writer.Update(now, PluginStatusWriter.Ok, c.Message, 3, c.Last, saved), c.Name + ": written");
            Check(!File.ReadAllBytes(file).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), c.Name + ": no BOM");
            var s = PluginStatus.TryRead(file);
            Check(s != null, c.Name + ": the tray can read it");
            if (s == null) { Console.WriteLine(File.ReadAllText(file)); continue; }
            Check(s.PluginVersion == "1.1.0" && s.State == PluginStatus.StateOk && s.SavedThisSession == 3 && s.ProcessId == 4242, c.Name + ": version, state, count, pid");
            Check(s.LogsFolder == c.Folder && s.Message == c.Message && s.LastFile == c.Last, c.Name + ": strings read back exactly");
            Check(s.HeartbeatUtcValue == new DateTime(2026, 10, 19, 21, 35, 0, DateTimeKind.Utc) && s.IsAlive(now) && s.IsAlive(now.AddSeconds(89)) && !s.IsAlive(now.AddSeconds(92)),
                  c.Name + ": heartbeat is alive for the tray, then stale");
            Check(c.Last == null ? s.LastSavedUtcValue == null : s.LastSavedUtcValue == new DateTime(2026, 10, 19, 21, 34, 5, DateTimeKind.Utc), c.Name + ": last saved (UTC)");
        }

        // The other states, and a heartbeat after the file went quiet.
        var f2 = PluginStatusWriter.PathFor(local);
        var w2 = new PluginStatusWriter(f2, "1.2.0-beta", @"D:\Logs", 1);
        w2.Update(now, PluginStatusWriter.Starting, "Waiting for the game to load.", 0, null, null);
        Check(PluginStatus.TryRead(f2)?.State == PluginStatus.StateStarting, "starting");
        w2.Update(now.AddSeconds(1), PluginStatusWriter.Error, "The game's classes were not found after 90 s.", 0, null, null);
        var e2 = PluginStatus.TryRead(f2);
        Check(e2?.State == PluginStatus.StateError && e2.Message.StartsWith("The game's classes"), "error with its message");
        Check(w2.Heartbeat(now.AddSeconds(30)) && PluginStatus.TryRead(f2)?.IsAlive(now.AddSeconds(115)) == true, "a heartbeat keeps it alive");
        Check(!Directory.GetFiles(Path.GetDirectoryName(f2)).Any(x => x.EndsWith(".tmp")), "no temp file left beside it");
    }

    // ================================================================== log file names

    static void LogNameCrossTests()
    {
        Console.WriteLine("25. log file names: the plugin's LogStore.FileNameFor, parsed by the tray's LogFileName");
        var emoji = char.ConvertFromUtf32(0x1F525);
        var opponents = new[]
        {
            null, "", "   ", "Gary", "  Gary  ", "Ash vs Gary", "vs", " vs ", "VICTORY", "Gary (2)", "Gary (1)", "(2)", "Gary (2) (3)", "Team (Rocket)",
            ".", "...", " . . ", "Mr.", "Mr. ", "a/b\\c:d*e?f\"g<h>i|j", "tab\there", "new\nline", "\u0001\u001f", "Zo\u00eb", "\u65e5\u672c\u8a9e",
            emoji, emoji + emoji, "A" + (char)0xD800 + "B", (char)0xDC00 + "x", "con", "NUL", "x - VICTORY vs y", "2026-10-19 21.34.05 - DEFEAT",
            new string('x', 200), new string('x', 105) + emoji + "yyyyyy", "." + new string(' ', 150) + "z", new string('.', 200), "a" + new string('.', 150),
        };
        var records = new List<MatchRecord>();
        foreach (var o in opponents)
        {
            foreach (var won in new bool?[] { true, false, null })
                records.Add(new MatchRecord { MatchId = "m", StartUtc = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 10, 19, 21, 34, 5, DateTimeKind.Utc), Won = won, OpponentName = o });
            records.Add(new MatchRecord { StartUtc = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc), ManualOnly = true, OpponentName = o });
        }
        var invalid = "<>:\"/\\|?*".ToCharArray();
        int ok = 0;
        var bad = new List<string>();
        foreach (var m in records)
        {
            var name = LogStore.FileNameFor(m);
            var label = LogStore.ResultLabel(m);
            var parsed = LogFileName.TryParse(name, out var result, out var opponent);
            // What the tray should show: the opponent part of the name as written, minus a " (n)" the
            // tray cannot tell from a duplicate's number (an opponent really called "Gary (2)").
            var stem = name.Substring(0, name.Length - 4);
            var prefixLength = "yyyy-MM-dd HH.mm.ss - ".Length + label.Length;
            var written = stem.Length > prefixLength ? stem.Substring(prefixLength + " vs ".Length) : null;
            bool opponentOk = opponent == written ||
                              (written != null && opponent != null && Regex.IsMatch(written, @" \([1-9][0-9]{0,5}\)$") && written.StartsWith(opponent + " (", StringComparison.Ordinal));
            bool windowsOk = name.Length <= 150 && name.IndexOfAny(invalid) < 0 && name.All(ch => ch >= ' ') && !stem.EndsWith(" ") && !stem.EndsWith(".") && WholeSurrogates(stem);
            if (parsed && result == label && opponentOk && windowsOk && (written == null || written.Length > 0)) ok++;
            else bad.Add(name + " -> " + (parsed ? result + " / " + (opponent ?? "(none)") : "not parsed") + (windowsOk ? "" : " [not a valid Windows name]"));
        }
        Check(bad.Count == 0, ok + " of " + records.Count + " names (4 results x " + opponents.Length + " opponents) are valid Windows names the tray parses back");
        foreach (var b in bad.Take(10)) Console.WriteLine("    " + b);
        var gary = new MatchRecord { StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow, Won = true, OpponentName = "Gary (2)" };
        Check(LogFileName.TryParse(LogStore.FileNameFor(gary), out _, out var g2) && g2 == "Gary",
              "known ambiguity: an opponent called \"Gary (2)\" is shown as Gary (a duplicate's number looks the same)");
        var dots = new MatchRecord { StartUtc = DateTime.UtcNow, Won = false, OpponentName = "..." };
        Check(!LogStore.FileNameFor(dots).Contains(" vs"), "an opponent that sanitizes to nothing leaves no dangling \" vs\": " + LogStore.FileNameFor(dots));
    }

    /// <summary>The plugin's LogStore writing into a folder, as the tray's watcher sees it.</summary>
    static void LogsFolderWatcherCrossTests()
    {
        Console.WriteLine("26. the logs folder: what the plugin writes (logs, .seal sidecars, .tmp, index.csv) vs the tray's save notifications");
        var folder = Path.Combine(TempDir(), "PTCGL Battle Logs");
        var identity = new SigningIdentity(Path.Combine(TempDir(), "identity"), new TestRsa());
        identity.Load(null);
        var mode = SealMode.Sidecar;
        var store = new LogStore(folder) { Sealer = new LogSealer(identity, "1.1.0", "1.24.0") { Mode = () => mode } };
        var queue = new SaveNotificationQueue(TimeSpan.FromSeconds(1.5), 100);
        var t = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc);

        // A FileSystemWatcher reports every name the plugin's writes produce: name.tmp created, then
        // renamed (or replaced) into place; the same for the .seal sidecar and index.csv.
        List<string> Events(params string[] files) => files.SelectMany(f => new[] { f + ".tmp", f }).ToList();
        IReadOnlyList<string> Feed(IEnumerable<string> events, DateTime at)
        {
            foreach (var e in events) queue.Add(e, at);
            return queue.TakeDue(at.AddSeconds(2), File.Exists);
        }
        var index = Path.Combine(folder, LogStore.IndexFile);

        var m = new MatchRecord { MatchId = "m-1", StartUtc = t, EndUtc = t.AddMinutes(11), Won = null, OpponentName = "Gary", MyName = "Ash", Capture = LogSealer.CaptureAuto };
        var p1 = store.Save(m, "Setup\nTurn 1", null);
        var sidecar1 = LogStore.SidecarPathFor(p1);
        Check(File.Exists(p1) && File.Exists(sidecar1) && File.Exists(index), "Sidecar mode: .txt, .seal and index.csv written");
        Check(Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal)
                  .SequenceEqual(new[] { Path.GetFileName(p1), Path.GetFileName(sidecar1), LogStore.IndexFile }.OrderBy(x => x, StringComparer.Ordinal)),
              "nothing else left in the folder (no .tmp)");
        foreach (var other in new[] { sidecar1, sidecar1 + ".tmp", p1 + ".tmp", index, index + ".tmp" })
            Check(!queue.Add(other, t), "ignored by the tray: " + Path.GetFileName(other));
        var due = Feed(Events(p1, sidecar1, index), t);
        Check(due.SequenceEqual(new[] { p1 }), "one notification, for the .txt: " + string.Join(", ", due.Select(Path.GetFileName)));

        // A longer read of the same match, now in Footer mode: same file, no second notification, sidecar gone.
        mode = SealMode.Footer;
        var again = store.Save(m, "Setup\nTurn 1\nTurn 2", p1);
        Check(again == p1 && !File.Exists(sidecar1), "re-sealed in place as a footer, stale .seal deleted");
        Check(Feed(Events(p1, index), t.AddSeconds(10)).Count == 0, "an overwrite does not notify again");

        // The result arrives late: the file moves to its VICTORY name, which notifies once.
        m.Won = true;
        m.Winner = 1;
        m.MySeat = 1;
        var p2 = store.Save(m, "Setup\nTurn 1\nTurn 2\nGary conceded.", p1);
        Check(p2 != p1 && !File.Exists(p1) && File.Exists(p2) && LogFileName.TryParse(p2, out var r2, out var o2) && r2 == "VICTORY" && o2 == "Gary",
              "moved to its corrected name, which the tray reads as VICTORY vs Gary");
        Check(Feed(new[] { p2, index + ".tmp", index }, t.AddSeconds(20)).SequenceEqual(new[] { p2 }), "the corrected name notifies once");

        // A second match with the same name gets " (2)", in Sidecar mode again.
        mode = SealMode.Sidecar;
        var p3 = store.Save(new MatchRecord { MatchId = "m-2", StartUtc = t, EndUtc = t.AddMinutes(11), Won = true, OpponentName = "Gary", Capture = LogSealer.CaptureAuto }, "Setup\nAnother", null);
        Check(Path.GetFileName(p3) == Path.GetFileNameWithoutExtension(p2) + " (2).txt" && File.Exists(LogStore.SidecarPathFor(p3)), "duplicate name numbered, with its own .seal");
        Check(LogFileName.TryParse(p3, out var r3, out var o3) && r3 == "VICTORY" && o3 == "Gary", "the tray reads the duplicate as VICTORY vs Gary");
        Check(Feed(Events(p3, LogStore.SidecarPathFor(p3), index), t.AddSeconds(30)).SequenceEqual(new[] { p3 }), "and notifies for it once");

        // The same files, checked by the reference verifier (python3 tools/verify_log.py).
        var python = FindPython();
        if (python == null) { Console.WriteLine("    (Python 3.8+ not found: verify_log.py check skipped)"); return; }
        var code = RunProcess(python, out var output, "-I", "-X", "utf8", Path.Combine(RepoRoot(), "tools", "verify_log.py"), "--chain", folder);
        Check(code == 0 && output.Contains("SUMMARY"), "tools/verify_log.py --chain accepts what the plugin wrote here (exit " + code + ")");
        if (code != 0) Console.WriteLine(output);
    }

    // ================================================================== the plugin's cfg

    static Assembly bepInEx;

    static Assembly BepInEx()
    {
        if (bepInEx != null) return bepInEx;
        var dll = Path.Combine(RepoRoot(), "lib", "BepInEx.dll");
        return bepInEx = File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
    }

    /// <summary>One game start as BepInEx runs it: ConfigFile reads the cfg, the plugin binds its entries (Plugin.Awake), BepInEx saves.</summary>
    sealed class BepInExSession
    {
        public object Folder, Manual, Mode;
        public string FolderValue => (string)Folder.GetType().GetProperty("Value").GetValue(Folder);
        public SealMode ModeValue => (SealMode)Mode.GetType().GetProperty("Value").GetValue(Mode);
        public bool ManualValue => (bool)Manual.GetType().GetProperty("Value").GetValue(Manual);
        public void SetFolder(string v) => Folder.GetType().GetProperty("Value").SetValue(Folder, v);
    }

    static BepInExSession GameStart(string cfgPath, string folderDefault = PluginConfig.OutputFolderDefault)
    {
        var cfgType = BepInEx().GetType("BepInEx.Configuration.ConfigFile", true);
        var bind = cfgType.GetMethods().First(x => x.Name == "Bind" && x.IsGenericMethodDefinition &&
            x.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[] { "String", "String", "T", "String" }));
        var cfg = Activator.CreateInstance(cfgType, cfgPath, true);
        object Bind<T>(string section, string key, T def) => bind.MakeGenericMethod(typeof(T)).Invoke(cfg, new object[] { section, key, def, "(test)" });
        return new BepInExSession
        {
            Folder = Bind(PluginConfig.GeneralSection, PluginConfig.OutputFolderKey, folderDefault),
            Manual = Bind(PluginConfig.GeneralSection, PluginConfig.SaveManualExportsKey, PluginConfig.SaveManualExportsDefault),
            Mode = Bind(PluginConfig.MetadataSection, PluginConfig.SealModeKey, PluginConfig.SealModeDefault),
        };
    }

    static void ConfigCrossTests()
    {
        Console.WriteLine("27. the plugin's cfg: names, defaults and folder rules agree between the plugin, the tray and the real BepInEx");
        Check(GameInstall.ConfigFileName == PluginConfig.FileName && PluginConfig.FileName == PluginConfig.Guid + ".cfg", "cfg file name = plugin GUID + .cfg (BepInEx names it so)");
        Check(GameInstall.ConfigSection == PluginConfig.GeneralSection && GameInstall.OutputFolderKey == PluginConfig.OutputFolderKey &&
              GameInstall.MetadataSection == PluginConfig.MetadataSection && GameInstall.SealModeKey == PluginConfig.SealModeKey, "same section and key names");
        Check(GameInstall.SealModes.SequenceEqual(Enum.GetNames(typeof(SealMode))) && GameInstall.DefaultSealMode == PluginConfig.SealModeDefault.ToString(),
              "seal modes = the plugin's enum names, in order, same default");
        Check(PluginConfig.OutputFolderDefault == "" && LogsFolder.DefaultFolderName == AppPaths.LogsFolderName, "empty OutputFolder = Documents\\" + AppPaths.LogsFolderName + " on both sides");
        var pluginSource = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Plugin", "Plugin.cs"));
        var binds = Regex.Matches(pluginSource, @"Config\.Bind\(\s*([^,]+),\s*([^,]+),\s*([^,]+),").Cast<Match>().ToList();
        Check(binds.Count == 4 && binds.All(b => b.Groups[1].Value.StartsWith("PluginConfig.") && b.Groups[2].Value.StartsWith("PluginConfig.") && b.Groups[3].Value.StartsWith("PluginConfig.")),
              "Plugin.cs binds its 4 entries with PluginConfig's names and defaults");
        Check(Regex.IsMatch(pluginSource, @"public const string Guid = PluginConfig\.Guid;"), "Plugin.cs takes its GUID from PluginConfig");

        if (BepInEx() == null) { Check(false, "lib/BepInEx.dll present for the BepInEx checks"); return; }
        var conv = BepInEx().GetType("BepInEx.Configuration.TomlTypeConverter", true);
        var toText = conv.GetMethod("ConvertToString", new[] { typeof(object), typeof(Type) });
        var toValue = conv.GetMethod("ConvertToValue", new[] { typeof(string), typeof(Type) });
        string TheirRead(string raw) => (string)toValue.Invoke(null, new object[] { raw, typeof(string) });
        string RoundTrip(string v) => TheirRead(((string)toText.Invoke(null, new object[] { v, typeof(string) })).Trim());   // as Plugin.BepInExRoundTrip

        var game = FakeGame();
        var gi = new GameInstall(game);
        var def = Path.Combine(TempDir(), "Documents", "PTCGL Battle Logs");
        Directory.CreateDirectory(Path.GetDirectoryName(gi.ConfigPath));

        // 1. First game start: BepInEx writes the cfg with the plugin's defaults.
        var s = GameStart(gi.ConfigPath);
        Check(s.FolderValue == "" && s.ModeValue == SealMode.Footer && s.ManualValue, "BepInEx: defaults");
        Check(gi.ReadOutputFolder() == null && gi.ReadSealMode() == "Footer" && gi.ReadConfigValue(PluginConfig.GeneralSection, PluginConfig.SaveManualExportsKey) == "true",
              "tray reads BepInEx's cfg: no folder (default), Footer");
        Check(gi.ResolveLogsFolder(def) == def && LogsFolder.Choose(s.FolderValue, def, RoundTrip, null).Folder == def, "both resolve the default folder");

        // 2. The tray changes the folder and the seal mode; the next game start reads them.
        var mine = Path.Combine(TempDir(), "My Battle Logs (2026) & more!");
        gi.WriteOutputFolder(mine);
        gi.WriteSealMode("Sidecar");
        s = GameStart(gi.ConfigPath);
        var choice = LogsFolder.Choose(s.FolderValue, def, RoundTrip, null);
        Check(s.FolderValue == mine && choice.Folder == Path.GetFullPath(mine) && !choice.ClearSetting && choice.Warning == null, "BepInEx reads the tray's folder; the plugin uses it");
        Check(gi.ResolveLogsFolder(def) == choice.Folder, "the tray resolves the same folder");
        Check(s.ModeValue == SealMode.Sidecar && s.ManualValue, "BepInEx reads the tray's seal mode; other entries kept");
        Check(gi.ReadOutputFolder() == mine && gi.ReadSealMode() == "Sidecar", "still readable by the tray after BepInEx rewrote the cfg");
        foreach (var path in new[] { @"E:\Battle Logs", "C:\\Users\\Jos\u00e9\\Pok\u00e9mon logs", @"C:\new\tab", @"Z:\", "relative\\dir" })
        {
            gi.WriteOutputFolder(path);
            Check(GameStart(gi.ConfigPath).FolderValue == path && gi.ReadOutputFolder() == path, "tray-written folder survives BepInEx: " + path);
        }
        var relative = Path.Combine("Battle Logs", "Ranked");
        gi.WriteOutputFolder(relative);
        choice = LogsFolder.Choose(GameStart(gi.ConfigPath).FolderValue, def, RoundTrip, null, game);
        Check(choice.Folder == Path.Combine(game, "Battle Logs", "Ranked") && gi.ResolveLogsFolder(def) == choice.Folder,
              "a relative folder is relative to the game folder on both sides: " + choice.Folder);

        // 3. Every mode the tray offers is the plugin's enum value of that name, also when spelt otherwise.
        foreach (var modeName in GameInstall.SealModes)
        {
            gi.WriteSealMode(modeName.ToLowerInvariant());
            Check(GameStart(gi.ConfigPath).ModeValue.ToString() == modeName && gi.ReadSealMode() == modeName, "mode " + modeName);
        }
        foreach (var raw in new[] { "sidecar", "OFF", " Footer ", "0", "1", "2" })
        {
            File.WriteAllText(gi.ConfigPath, "[Metadata]\nMode = " + raw + "\n");
            var theirs = GameStart(gi.ConfigPath).ModeValue.ToString();
            Check(gi.ReadSealMode() == theirs, "raw \"" + raw + "\": BepInEx reads " + theirs + ", the tray " + gi.ReadSealMode());
        }
        File.WriteAllText(gi.ConfigPath, "[Metadata]\nMode = Signed\n");
        Check(gi.ReadSealMode() == null && GameStart(gi.ConfigPath).ModeValue == SealMode.Footer, "unknown mode: BepInEx falls back to Footer, the tray shows the default (Footer)");

        // 4. Back to the default: an empty value that both read as the default folder.
        gi.WriteOutputFolder(mine);
        gi.ResetOutputFolder();
        s = GameStart(gi.ConfigPath);
        choice = LogsFolder.Choose(s.FolderValue, def, RoundTrip, null);
        Check(s.FolderValue == "" && choice.Folder == def && !choice.ClearSetting && choice.Warning == null && gi.ResolveLogsFolder(def) == def, "reset: empty, the default on both sides");

        // 5. Folder values: the tray's copy of the plugin's rules (GameInstall.WhyPluginRefuses) gives
        // the plugin's verdict (LogsFolder.WhyUnkeepable with the real BepInEx round trip).
        var values = new[]
        {
            @"C:\Users\Ash\Documents\PTCGL Battle Logs", @"C:\Users\O\'Brien\Documents\PTCGL Battle Logs", @"C:\Users\O'Brien\Documents\PTCGL Battle Logs",
            @"D:\Ash's logs", @"D:\""quoted""", @"\fileserver\users\ash", @"\\server\share", @"\\?\C:\long", @"\Logs", @"E:\Logs (2026) & more!",
            "relative\\dir", "relative\\new", "C:/forward/slashes", @"C:\new\tab", "D:\\x\ty", "D:\\x" + (char)7, @"Z:\", "/home/ash/logs", "a'b", "C:\\Users\\Jos\u00e9\\Pok\u00e9mon logs",
            TheirRead(@"\\fileserver\users\ash\Documents\PTCGL Battle Logs"), TheirRead(@"C:\Users\O\'Brien\x"), TheirRead(@"""C:\x"""), @"C:\a\\b", @"x\", @"C:\#notacomment",
        };
        var disagree = values.Where(v => (LogsFolder.WhyUnkeepable(v, RoundTrip) == null) != (GameInstall.WhyPluginRefuses(v) == null)).ToList();
        Check(disagree.Count == 0, values.Length + " folder values: the tray and the plugin agree on which ones the plugin refuses" +
                                   (disagree.Count == 0 ? "" : ": not on " + string.Join(" | ", disagree.Select(Printable))));
        foreach (var v in values.Where(v => GameInstall.WhyPluginRefuses(v) != null))
            Check(Throws<ArgumentException>(() => gi.WriteOutputFolder(v)), "the tray never writes a value the plugin refuses: " + Printable(v));

        // 6. A 1.0 install whose profile path has an apostrophe: 1.0 put the default folder into the
        // cfg as the default value, and BepInEx garbled it. Before the 1.1 plugin first runs, the tray
        // must not use (create, link, watch) the garbled folder; then the plugin clears it.
        foreach (var oldDefault in new[] { @"C:\Users\O'Brien\Documents\PTCGL Battle Logs", @"\\fileserver\users\ash\Documents\PTCGL Battle Logs" })
        {
            File.Delete(gi.ConfigPath);
            GameStart(gi.ConfigPath, oldDefault);                          // 1.0
            var garbled = gi.ReadOutputFolder();
            Check(garbled != null && garbled != oldDefault, "1.0's cfg holds a garbled folder: " + Printable(garbled));
            Check(gi.ResolveLogsFolder(def) == def, "the tray uses the default folder, as the plugin will");
            s = GameStart(gi.ConfigPath);                                  // 1.1, default empty
            choice = LogsFolder.Choose(s.FolderValue, def, RoundTrip, null);
            Check(choice.Folder == def && choice.ClearSetting, "the plugin falls back and clears it");
            s.SetFolder("");                                               // Plugin.ResolveFolder
            Check(gi.ReadOutputFolder() == null && gi.ResolveLogsFolder(def) == def, "the tray then reads an empty value: the default folder");
        }
    }

    /// <summary>No half surrogate pair: UTF-8 (index.csv, the seal) could not name the file.</summary>
    static bool WholeSurrogates(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }

    static string Printable(string s) => s == null ? "(null)" : new string(s.Select(c => c < ' ' ? '?' : c).ToArray());

    // ================================================================== names that tie the build together

    static void BuildNamesCrossTests()
    {
        Console.WriteLine("28. names: plugin assembly, installed file, embedded resources, product name");
        var root = RepoRoot();
        var pluginProj = XDocument.Load(Path.Combine(root, "src", "Plugin", "PtcglAutoBattleLog.Plugin.csproj"));
        var assemblyName = pluginProj.Descendants("AssemblyName").Select(e => e.Value.Trim()).FirstOrDefault();
        Check(assemblyName + ".dll" == GameInstall.PluginFileName, "plugin AssemblyName + .dll = GameInstall.PluginFileName (" + GameInstall.PluginFileName + ")");
        Check(GameInstall.PluginFolderName == AppPaths.AppId, "plugin folder BepInEx\\plugins\\" + AppPaths.AppId);

        var trayProj = XDocument.Load(Path.Combine(root, "src", "Tray", "PtcglAutoBattleLog.Tray.csproj"));
        Check(trayProj.Descendants("AssemblyName").Select(e => e.Value.Trim()).FirstOrDefault() + ".exe" == AppPaths.ExeName &&
              AppPaths.ExeName == UpdateChecker.ExeAssetName, "tray AssemblyName + .exe = AppPaths.ExeName = the release asset name");
        var payload = trayProj.Descendants("PluginPayload").Select(e => e.Value.Trim()).FirstOrDefault() ?? "";
        Check(payload.EndsWith("/" + GameInstall.PluginFileName, StringComparison.Ordinal), "the tray embeds the plugin's build output: " + payload);
        var logical = trayProj.Descendants("EmbeddedResource").Select(e => (string)e.Attribute("LogicalName")).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expected = new[] { "payload/" + GameInstall.PluginFileName, "payload/BepInEx.zip", "app.ico", "DISCLAIMER.md",
                               "THIRD-PARTY-NOTICES.md", "LICENSE", "licenses/LGPL-2.1.txt" }.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(logical.SequenceEqual(expected), "csproj resources: " + string.Join(", ", logical));
        var embedded = Regex.Matches(File.ReadAllText(Path.Combine(root, "src", "Tray", "Embedded.cs")), @"const string \w+Resource = ""([^""]+)""")
                            .Cast<Match>().Select(x => x.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(embedded.SequenceEqual(expected), "names the tray loads (Embedded.cs): " + string.Join(", ", embedded));

        var appInfo = File.ReadAllText(Path.Combine(root, "src", "Tray", "AppInfo.cs"));
        var pluginSource = File.ReadAllText(Path.Combine(root, "src", "Plugin", "Plugin.cs"));
        var product = Regex.Match(appInfo, @"ProductName = ""([^""]+)""").Groups[1].Value;
        var pluginName = Regex.Match(pluginSource, @"const string Name = ""([^""]+)""").Groups[1].Value;
        Check(product == pluginName && product == LogSealer.AppName && product == "PTCGL Auto Battle Log", "one product name: tray, plugin (BepInEx log tag), seal \"app\"");
        Check(pluginProj.Root.ToString().Contains(@"lib\BepInEx.dll") && File.Exists(Path.Combine(root, "lib", "BepInEx.dll")), "the plugin compiles against lib/BepInEx.dll");

        // The plugin's signing key outlives an uninstall: the uninstaller keeps the folder the plugin uses.
        var local = TempDir();
        var kept = Regex.Match(File.ReadAllText(Path.Combine(root, "src", "Tray", "Uninstaller.cs")), @"KeptFolderName = ""([^""]+)""").Groups[1].Value;
        Check(kept == SigningIdentity.FolderName && SigningIdentity.FolderFor(local) == Path.Combine(new AppPaths(local, local).LocalRoot, kept),
              "uninstall keeps " + kept + "\\, where the plugin keeps its key");

        // The release scripts package what the tray looks for, and the license texts of what the exe carries.
        foreach (var script in new[] { "build.sh", "build.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(root, script));
            Check(text.Contains(AppPaths.PortableMarkerFileName) && text.Contains("PtcglAutoBattleLog-portable.zip") && text.Contains(AppPaths.ExeName) &&
                  text.Contains("make-manifest.py"), script + " packages " + AppPaths.ExeName + " and " + AppPaths.PortableMarkerFileName + " into the portable zip");
            Check(text.Contains("LICENSE.txt") && text.Contains("THIRD-PARTY-NOTICES.txt") && text.Contains("LGPL-2.1.txt") && text.Contains("global.json"),
                  script + " also packages LICENSE.txt, THIRD-PARTY-NOTICES.txt and licenses/LGPL-2.1.txt, and checks the SDK global.json pins");
        }
        foreach (var file in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md", Path.Combine("licenses", "LGPL-2.1.txt") })
            Check(File.Exists(Path.Combine(root, file)) && new FileInfo(Path.Combine(root, file)).Length > 500, file + " exists (embedded in the exe, packaged in the zip)");
        var lgpl = File.ReadAllText(Path.Combine(root, "licenses", "LGPL-2.1.txt"));
        Check(lgpl.Contains("GNU LESSER GENERAL PUBLIC LICENSE") && lgpl.Contains("Version 2.1, February 1999") && lgpl.Contains("END OF TERMS AND CONDITIONS"),
              "licenses/LGPL-2.1.txt is the complete LGPL-2.1");
        var notices = Disclaimer.ToPlainText(File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md")));
        Check(notices.Contains("BepInEx (https://github.com/BepInEx/BepInEx) | 5.4.23.3 | MIT") && notices.Contains("Copyright (c) 2018 Bepis"),
              "the Licenses window lists BepInEx 5.4.23.3 as MIT, with its copyright line");
        var global = File.ReadAllText(Path.Combine(root, "global.json"));
        Check(Regex.IsMatch(global, @"""version""\s*:\s*""8\.0\.\d+""") && Regex.IsMatch(global, @"""rollForward""\s*:\s*""disable"""),
              "global.json pins an exact .NET 8 SDK (rollForward disable)");
        foreach (var wf in new[] { "ci.yml", "release.yml" })
        {
            var yml = File.ReadAllText(Path.Combine(root, ".github", "workflows", wf));
            Check(yml.Contains("global-json-file: global.json") && !yml.Contains("dotnet-version:"), wf + " installs the SDK global.json pins");
        }

        // The tray's own files: per-monitor DPI needs an app.config a single exe cannot ship.
        var manifest = File.ReadAllText(Path.Combine(root, "src", "Tray", "app.manifest"));
        Check(Regex.IsMatch(manifest, @"<dpiAware[^>]*>true</dpiAware>") && !manifest.Contains("<dpiAwareness") && !manifest.Contains("true/pm"),
              "app.manifest: system DPI aware only (WinForms 4.8 per-monitor scaling needs app.config)");
        var menu = File.ReadAllText(Path.Combine(root, "src", "Tray", "TrayApplicationContext.Menu.cs"));
        foreach (var mode in GameInstall.SealModes)
            Check(Regex.IsMatch(menu, @"SealOption\(""" + mode + @""", """ + mode + @"[ ""]"), "the seal menu item of " + mode + " is labelled " + mode + ", as the README says");

        // When the plugin has been built (build.sh builds it after these tests), its file agrees too.
        var built = Path.Combine(root, "src", "Plugin", "bin", "Release", GameInstall.PluginFileName);
        if (File.Exists(built))
            Check(AssemblyName.GetAssemblyName(built).Name + ".dll" == GameInstall.PluginFileName, "built plugin's assembly name");
    }

    // ================================================================== release manifest

    static async Task ManifestCrossTests()
    {
        Console.WriteLine("29. tools/make-manifest.py output, read by the updater");
        var python = FindPython();
        if (python == null) { Console.WriteLine("    (Python 3.8+ not found: skipped; build.sh always runs it)"); return; }
        var dir = TempDir();
        var exe = Path.Combine(dir, UpdateChecker.ExeAssetName);
        var zip = Path.Combine(dir, "PtcglAutoBattleLog-portable.zip");
        var exeBytes = Encoding.ASCII.GetBytes("MZ" + string.Join(",", Enumerable.Range(0, 20000)));
        File.WriteAllBytes(exe, exeBytes);
        File.WriteAllBytes(zip, new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(Encoding.ASCII.GetBytes("not really a zip")).ToArray());
        var script = Path.Combine(RepoRoot(), "tools", "make-manifest.py");
        int Make(string version, string tag, string owner, string repo, string outDir, out string output) =>
            RunProcess(python, out output, "-I", "-X", "utf8", script, "--version", version, "--tag", tag, "--owner", owner, "--repo", repo,
                       "--exe", exe, "--portable-zip", zip, "--out", outDir, "--released-utc", "2026-11-01T12:00:00Z");

        foreach (var (owner, repo) in new[] { (BuildInfo.RepoOwner, BuildInfo.RepoName), ("Some-Fork.x", "my_repo.2") })
        {
            var outDir = Path.Combine(dir, owner);
            var code = Make("1.2.3", "v1.2.3", owner, repo, outDir, out var output);
            Check(code == 0, owner + "/" + repo + ": make-manifest.py exit 0");
            if (code != 0) { Console.WriteLine(output); continue; }
            var m = UpdateManifest.Parse(File.ReadAllText(Path.Combine(outDir, UpdateChecker.ManifestAssetName)));
            var asset = m.Find(UpdateChecker.ExeAssetName);
            Check(m.Version == "1.2.3" && m.Tag == "v1.2.3" && m.ReleasedUtc == "2026-11-01T12:00:00Z" && m.Assets.Count == 1 && asset != null,
                  owner + ": UpdateManifest.Parse reads it (version, tag, date, the exe only)");
            if (asset == null) continue;
            var checker = new UpdateChecker(owner, repo, "1.2.2", (u, ct) => Task.FromResult<Stream>(new MemoryStream(exeBytes)));
            Check(checker.IsNewer(m) && checker.IsTrustedAssetUrl(asset.Url) && m.NotesUrl == "https://github.com/" + owner + "/" + repo + "/releases/tag/v1.2.3",
                  owner + ": newer, trusted URL " + asset.Url);
            Check(asset.Size == exeBytes.Length && asset.Sha256 == Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant(), owner + ": size and sha256 of the exe");
            var got = await checker.DownloadAsync(asset, Path.Combine(outDir, "updates"), CancellationToken.None);
            Check(File.ReadAllBytes(got).SequenceEqual(exeBytes), owner + ": DownloadAsync verifies it");
            var other = new UpdateChecker(owner == BuildInfo.RepoOwner ? "Some-Fork.x" : BuildInfo.RepoOwner, repo, "1.2.2", (u, ct) => throw new InvalidOperationException());
            Check(!other.IsTrustedAssetUrl(asset.Url), owner + ": another repository's build does not trust it");
            var sums = File.ReadAllLines(Path.Combine(outDir, "SHA256SUMS.txt"));
            Check(sums.Length == 2 && sums[0] == asset.Sha256 + "  " + UpdateChecker.ExeAssetName &&
                  sums[1] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant() + "  PtcglAutoBattleLog-portable.zip", owner + ": SHA256SUMS.txt");
        }

        // A pre-release (published as a GitHub pre-release, never the latest): written, ignored by the
        // stable tray, and its testers' builds take the final version.
        {
            var outDir = Path.Combine(dir, "beta");
            var code = Make("1.2.3-beta.1", "v1.2.3-beta.1", "o", "r", outDir, out var output);
            Check(code == 0, "make-manifest.py writes a pre-release (exit 0)");
            if (code != 0) Console.WriteLine(output);
            else
            {
                var m = UpdateManifest.Parse(File.ReadAllText(Path.Combine(outDir, UpdateChecker.ManifestAssetName)));
                var asset = m.Find(UpdateChecker.ExeAssetName);
                var stable = new UpdateChecker("o", "r", "1.2.2", (u, ct) => throw new InvalidOperationException());
                Check(m.Version == "1.2.3-beta.1" && m.Tag == "v1.2.3-beta.1" && asset != null &&
                      asset.Url == "https://github.com/o/r/releases/download/v1.2.3-beta.1/" + UpdateChecker.ExeAssetName && stable.IsTrustedAssetUrl(asset.Url),
                      "pre-release manifest: version, tag and a trusted URL");
                Check(!stable.IsNewer(m), "the stable tray ignores a pre-release manifest");
                var final = new UpdateManifest { Version = "1.2.3" };
                Check(new UpdateChecker("o", "r", m.Version, (u, ct) => throw new InvalidOperationException()).IsNewer(final),
                      "a pre-release build updates to its final version");
            }
        }

        // What the tray would refuse (a version it ignores, a name its URL check rejects) is never written.
        foreach (var (version, tag, owner, repo, why) in new[]
        {
            ("1.2.3-", "v1.2.3-", "o", "r", "an empty pre-release suffix"), ("1.2.3+build", "v1.2.3+build", "o", "r", "build metadata"),
            ("1.2.3-be/ta", "v1.2.3-be/ta", "o", "r", "a '/' in the pre-release suffix"), ("01.2.3", "v01.2.3", "o", "r", "a leading zero"),
            ("1.2.3", "v1.2.3", ".hidden", "r", "an owner starting with '.'"), ("1.2.3", "v1.2.3", "o", "r/x", "a '/' in the repository"),
            ("1.2.3", "v1.2.3", new string('o', 101), "r", "an owner longer than 100 characters"), ("1.2.3", "v1.2.3", "o", "r\u00e9", "a non-ASCII repository"),
        })
        {
            var code = Make(version, tag, owner, repo, Path.Combine(dir, "refused"), out _);
            Check(code == 2 && !File.Exists(Path.Combine(dir, "refused", "update.json")), "refused with exit 2: " + why);
            var trayAccepts = UpdateChecker.TryParseVersion(version, out var v) && v.ToString(3) == version &&
                              UpdateChecker.IsSafeName(owner) && UpdateChecker.IsSafeName(repo);
            Check(!trayAccepts, "the tray would refuse it too: " + why);
        }
        Check(Make("1.2.3", "1.2.3", "o", "r", Path.Combine(dir, "refused"), out _) == 2, "refused with exit 2: a tag that is not 'v' + version");
    }

    // ================================================================== helpers

    static string[] pythonFound;
    static bool pythonSearched;

    /// <summary>
    /// Python 3.8+ as the command that starts it, or null: $PYTHON (build.sh passes its own), then
    /// py -3, python3, python (build.ps1's order; a Microsoft Store stub fails the version probe).
    /// </summary>
    static string[] FindPython()
    {
        if (pythonSearched) return pythonFound;
        pythonSearched = true;
        var candidates = new List<string[]>();
        var env = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(new[] { env });
        candidates.Add(new[] { "py", "-3" });
        candidates.Add(new[] { "python3" });
        candidates.Add(new[] { "python" });
        foreach (var c in candidates)
            if (RunProcess(c, out _, "-c", "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)") == 0)
                return pythonFound = c;
        return null;
    }

    static int RunProcess(string[] command, out string output, params string[] args) =>
        RunProcess(command[0], out output, command.Skip(1).Concat(args).ToArray());

    static int RunProcess(string exe, out string output, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    output = "timed out";
                    return -1;
                }
                output = stdout.Result + stderr.Result;
                return p.ExitCode;
            }
        }
        catch (Exception e)
        {
            output = e.Message;
            return -1;
        }
    }
}
