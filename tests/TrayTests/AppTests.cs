using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtcglAutoBattleLog.Tray.Logic;

// Logic behind the Windows app: portable paths, consent text, command line, 1.0 leftovers,
// the daemon's decisions, save notifications, icon frames and the extra cfg keys.
static partial class Program
{
    static void PortablePathsTests()
    {
        Console.WriteLine("16. AppPaths: portable mode");
        var dir = TempDir();
        var exe = Path.Combine(dir, "Tools", "My ABL.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        File.WriteAllText(exe, "MZ");
        var local = Path.Combine(dir, "Local");
        var docs = Path.Combine(dir, "Docs");

        var installed = new AppPaths(local, docs);
        Check(!installed.IsPortable && !installed.UsesFallbackData && installed.LocalRoot == installed.Root, "installed: not portable, Root = LocalRoot");

        Check(!AppPaths.HasPortableMarker(exe), "no marker yet");
        File.WriteAllText(Path.Combine(dir, "Tools", AppPaths.PortableMarkerFileName), "portable");
        Check(AppPaths.HasPortableMarker(exe), "portable.txt next to the exe");
        Check(!AppPaths.HasPortableMarker(Path.Combine(dir, "other.exe")), "marker elsewhere does not count");
        Check(!AppPaths.HasPortableMarker(null) && !AppPaths.HasPortableMarker("") && !AppPaths.HasPortableMarker("bad\0path") &&
              !AppPaths.HasPortableMarker("noFolder.exe"), "bad input -> false, no throw");

        var p = AppPaths.ForPortable(exe, local, docs);
        Check(p.IsPortable && !p.UsesFallbackData, "portable");
        Check(p.Root == Path.Combine(dir, "Tools", "data"), "Root = <exe folder>\\data: " + p.Root);
        Check(p.InstalledExe == exe && p.OldExe == exe + ".old", "the (renamed) exe itself is updated in place");
        Check(p.SettingsFile == Path.Combine(p.Root, "settings.json") && p.TrayLogFile == Path.Combine(p.Root, "tray.log") &&
              p.UpdatesDir == Path.Combine(p.Root, "updates"), "settings, log and updates in data");
        Check(p.PluginStatusFile == Path.Combine(local, "PtcglAutoBattleLog", "plugin-status.json"), "plugin-status.json stays in %LOCALAPPDATA%");
        Check(p.LocalRoot == installed.Root && p.DefaultLogsFolder == installed.DefaultLogsFolder, "same LocalRoot and logs folder as installed");

        var f = AppPaths.ForPortableFallback(exe, local, docs);
        Check(f.IsPortable && f.UsesFallbackData && f.Root == Path.Combine(local, "PtcglAutoBattleLog", "portable-data"), "fallback data: " + f.Root);
        Check(f.InstalledExe == exe && f.PluginStatusFile == p.PluginStatusFile, "fallback keeps the exe and status file");

        Check(Throws<ArgumentException>(() => AppPaths.ForPortable(null, local, docs)) &&
              Throws<ArgumentException>(() => AppPaths.ForPortable("bare.exe", local, docs)) &&
              Throws<ArgumentException>(() => AppPaths.ForPortable(exe, null, docs)), "bad arguments rejected");
        Check(NoThrow(() => AppPaths.ForPortable(exe)) && NoThrow(() => AppPaths.ForPortableFallback(exe)), "current-user overloads work");

        Check(p.TryPrepareRoot() && Directory.Exists(p.Root) && Directory.GetFiles(p.Root).Length == 0, "TryPrepareRoot creates the folder, leaves no probe file");
        var blocker = Path.Combine(dir, "ro");
        File.WriteAllText(blocker, "a file where the folder should be");
        var bad = AppPaths.ForPortable(Path.Combine(blocker, "x.exe"), local, docs);
        Check(NoThrow(() => bad.TryPrepareRoot()) && !bad.TryPrepareRoot(), "unwritable data folder -> false");
    }

    static void DisclaimerTests()
    {
        Console.WriteLine("17. Disclaimer");
        Check(Disclaimer.Version == 4, "version 4");
        var md = File.ReadAllText(Path.Combine(RepoRoot(), "DISCLAIMER.md"));
        var plain = Disclaimer.ToPlainText(md);
        Check(md.Contains("Pokematch") && md.Contains("tcgkb.app"), "it names every place the app can send logs to");
        var paragraphs = plain.Split(new[] { "\r\n\r\n" }, StringSplitOptions.None);
        Check(paragraphs[0] == "Disclaimer", "heading without '#': " + paragraphs[0]);
        Check(!plain.Contains("#") && !plain.Contains("**") && !plain.Contains("`"), "no Markdown marks left");
        Check(paragraphs.Skip(1).All(par => !par.Contains("\n") && par.Length > 40), "each paragraph is one long line the dialog can wrap");
        Check(plain.Contains("Terms of Service") && plain.Contains("not affiliated with"), "content kept");
        var mdWords = md.Replace("#", " ").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var plainWords = plain.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        Check(mdWords.SequenceEqual(plainWords), "same words in the same order (" + plainWords.Length + ")");
        Check(!plain.Replace("\r\n", "").Contains("\n") && !plain.Contains("\r\n\r\n\r\n"), "CRLF only, single blank lines");

        Check(Disclaimer.ToPlainText(null) == "" && Disclaimer.ToPlainText("") == "", "empty in, empty out");
        var sample = "\uFEFF# Title\r\n\r\nSome **bold** and *em* and `code`,\nwrapped line.\n\n* one\n  continued\n- two\n\n" +
                     "See [the site](https://example.com) and [LICENSE](LICENSE).\n<!-- hidden -->\n\n## Sub ##\n```\nkeep   this\n  as is\n```\nsnake_case and 2 * 3 * 4\n";
        var expected = string.Join("\r\n\r\n", new[]
        {
            "Title",
            "Some bold and em and code, wrapped line.",
            "\u2022 one continued\r\n\u2022 two",
            "See the site (https://example.com) and LICENSE.",
            "Sub",
            "keep   this\r\n  as is",
            "snake_case and 2 * 3 * 4",
        });
        var got = Disclaimer.ToPlainText(sample);
        Check(got == expected, "Markdown subset rendered" + (got == expected ? "" : ":\n" + got.Replace("\r\n", "⏎\n")));
        var table = "Before\n\n| Files | Component | License |\n|---|:---:|---|\n| `a.dll`, `b.dll` | [BepInEx](https://x.example) | **MIT** |\n| `c.dll` | Doorstop | LGPL-2.1 |\nAfter\n";
        got = Disclaimer.ToPlainText(table);
        expected = "Before\r\n\r\nFiles | Component | License\r\na.dll, b.dll | BepInEx (https://x.example) | MIT\r\nc.dll | Doorstop | LGPL-2.1\r\n\r\nAfter";
        Check(got == expected, "tables (THIRD-PARTY-NOTICES.md): one line per row, no rule line" + (got == expected ? "" : ":\n" + got.Replace("\r\n", "⏎\n")));
    }

    static void CommandLineTests()
    {
        Console.WriteLine("18. CommandLine");
        var none = CommandLine.Parse(new string[0]);
        Check(!none.Background && !none.FirstRun && !none.Portable && !none.Uninstall && !none.Quiet && !none.Updated && none.Unknown.Count == 0, "no arguments");
        Check(CommandLine.Parse(null).Unknown.Count == 0, "null args");
        var all = CommandLine.Parse(new[] { "--BACKGROUND", " --first-run ", "--Portable", "--uninstall", "--quiet", "--updated-from", "1.0.0", "/x", "" });
        Check(all.Background && all.FirstRun && all.Portable && all.Uninstall && all.Quiet && all.UpdatedFrom == "1.0.0", "every flag, any case, trimmed");
        Check(all.Unknown.SequenceEqual(new[] { "/x" }), "unknown kept for the log");
        var noValue = CommandLine.Parse(new[] { "--updated-from", "--portable" });
        Check(noValue.Updated && noValue.UpdatedFrom == "" && noValue.Portable, "--updated-from without a value, next flag still read");
        Check(CommandLine.Parse(new[] { "--updated-from" }).UpdatedFrom == "", "--updated-from last");
        Check(CommandLine.Parse(new[] { "--updated-from=1.2.3" }).UpdatedFrom == "1.2.3", "--updated-from=X form");
        Check(CommandLine.Parse(new string[] { null, "--uninstall" }).Uninstall, "null entries skipped");

        Check(CommandLine.Quote("1.1.0") == "1.1.0", "plain arg unquoted");
        Check(CommandLine.Quote("") == "\"\"", "empty quoted");
        Check(CommandLine.Quote(@"C:\Program Files\x") == "\"C:\\Program Files\\x\"", "spaces quoted, backslashes kept");
        Check(CommandLine.Quote(@"C:\dir with space\") == "\"C:\\dir with space\\\\\"", "trailing backslash doubled before the closing quote");
        Check(CommandLine.Quote("say \"hi\"") == "\"say \\\"hi\\\"\"", "inner quotes escaped");
        Check(CommandLine.Quote("a\\\"b") == "\"a\\\\\\\"b\"", "backslashes before a quote doubled");
        Check(CommandLine.Join(new[] { "--updated-from", "1.1.0", "--portable" }) == "--updated-from 1.1.0 --portable", "Join");
        Check(CommandLine.RunCommand(@"C:\Users\Ash\AppData\Local\PtcglAutoBattleLog\PtcglAutoBattleLog.exe", false) ==
              "\"C:\\Users\\Ash\\AppData\\Local\\PtcglAutoBattleLog\\PtcglAutoBattleLog.exe\" --background", "Run value (contract)");
        Check(CommandLine.RunCommand(@"D:\Tools\abl.exe", true) == "\"D:\\Tools\\abl.exe\" --background --portable", "portable Run value without marker");

        // What the Run value parses back to, by Windows' rules (CommandLineToArgvW) re-implemented here.
        foreach (var arg in new[] { "plain", "with space", "trail\\", "trail space\\", "q\"uote", "\\\\server\\share", "a\\\\\"b", "" })
            Check(ParseWindowsArgs(CommandLine.Quote(arg)).SequenceEqual(new[] { arg }), "Quote round-trips by Windows rules: " + arg);
    }

    /// <summary>The MSVCRT / CommandLineToArgvW rules for arguments after the program name.</summary>
    static List<string> ParseWindowsArgs(string s)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < s.Length && s[i] == '\\') { n++; i++; }
                if (i < s.Length && s[i] == '"')
                {
                    cur.Append('\\', n / 2);
                    if (n % 2 == 1) cur.Append('"');
                    else { inQuotes = !inQuotes; }
                    any = true;
                }
                else { cur.Append('\\', n); i--; any = true; }
                continue;
            }
            if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
            if ((c == ' ' || c == '\t') && !inQuotes)
            {
                if (any) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
            any = true;
        }
        if (any) args.Add(cur.ToString());
        return args;
    }

    static void LegacyInstallTests()
    {
        Console.WriteLine("19. LegacyInstall (1.0 leftovers)");
        var root = Path.Combine(TempDir(), "PtcglAutoBattleLog");
        Directory.CreateDirectory(root);
        var legacy = new LegacyInstall(root);
        Check(!legacy.Exists && legacy.ReadGameDir() == null && !legacy.BepInExInstalledByUs(@"C:\Game"), "nothing there");

        // As 1.0 wrote them: Set-Content -Encoding UTF8 (BOM + CRLF).
        var game = @"C:\Users\Ash\The Pokémon Company International\Pokémon Trading Card Game Live";
        File.WriteAllText(Path.Combine(root, "game-dir.txt"), game + "\r\n", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root, "bepinex-installed-by-us.flag"), game + "\r\n", new UTF8Encoding(true));
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        File.WriteAllText(Path.Combine(root, "payload", "PtcglAutoBattleLog.dll"), "MZ");
        foreach (var f in new[] { "watchdog.ps1", "common.ps1", "uninstall.ps1", "watchdog.log", "watchdog.log.old" })
            File.WriteAllText(Path.Combine(root, f), "x");
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");   // ours: must survive
        Check(legacy.Exists, "leftovers detected");
        Check(legacy.ReadGameDir() == game, "game-dir.txt read without BOM or newline");
        Check(legacy.BepInExInstalledByUs(game) && legacy.BepInExInstalledByUs(game.ToUpperInvariant() + "\\"), "flag for this game folder (case, trailing slash)");
        Check(!legacy.BepInExInstalledByUs(@"D:\Other"), "flag for another folder does not count");
        File.WriteAllText(Path.Combine(root, "bepinex-installed-by-us.flag"), "");
        Check(legacy.BepInExInstalledByUs(@"D:\Other"), "an empty flag counts for any folder");

        var failed = legacy.RemoveLeftovers();
        Check(failed.Count == 0 && !legacy.Exists, "all leftovers removed");
        Check(FilesUnder(root).SequenceEqual(new[] { "settings.json" }), "the 1.1 files are kept: " + string.Join(",", FilesUnder(root)));
        Check(NoThrow(() => legacy.RemoveLeftovers()) && legacy.RemoveLeftovers().Count == 0, "removing twice is fine");

        File.WriteAllText(Path.Combine(root, "game-dir.txt"), "\r\n   \r\n");
        Check(legacy.ReadGameDir() == null, "blank game-dir.txt -> null");
        Check(Throws<ArgumentException>(() => new LegacyInstall(null)), "root required");

        var cmd = "\"C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
                  "\"C:\\Users\\Ash\\AppData\\Local\\PtcglAutoBattleLog\\watchdog.ps1\"";
        Check(LegacyInstall.IsWatchdogCommandLine(cmd), "1.0 watchdog command line recognized");
        Check(LegacyInstall.IsWatchdogCommandLine(cmd.ToLowerInvariant()), "case-insensitive");
        Check(!LegacyInstall.IsWatchdogCommandLine("powershell.exe -File C:\\other\\watchdog.ps1") &&
              !LegacyInstall.IsWatchdogCommandLine("powershell.exe -File C:\\x\\PtcglAutoBattleLog\\install.ps1") &&
              !LegacyInstall.IsWatchdogCommandLine("watchdog.ps1 PtcglAutoBattleLog") &&
              !LegacyInstall.IsWatchdogCommandLine(null), "other PowerShell processes are left alone");
    }

    static void DaemonMonitorTests()
    {
        Console.WriteLine("20. DaemonMonitor");
        var t0 = new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
        var all = new[] { InstallPiece.WinHttp, InstallPiece.DoorstopConfig, InstallPiece.BepInExPreloader, InstallPiece.BepInExCore, InstallPiece.Plugin };
        var none = new InstallPiece[0];
        PluginStatus Beat(DateTime at, string state = "ok", int saved = 0, int pid = 100, string message = null) =>
            new PluginStatus { State = state, HeartbeatUtc = at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), SavedThisSession = saved, ProcessId = pid, Message = message };
        DaemonObservation Obs(double sec, bool running, InstallPiece[] missing = null, bool pluginFile = true, PluginStatus status = null, string game = @"C:\Game") =>
            new DaemonObservation { NowUtc = t0.AddSeconds(sec), GameDir = game, GameRunning = running, Missing = missing ?? none, PluginFilePresent = pluginFile, Status = status };

        // Healthy, game closed.
        var m = new DaemonMonitor();
        var d = m.Evaluate(Obs(0, false));
        Check(d.Health == TrayHealth.Idle && !d.Repair && d.Notice == DaemonNotice.None && d.Summary.StartsWith("Ready"), "idle: " + d.Summary);
        Check(m.Evaluate(Obs(0, false, game: null)).Health == TrayHealth.Attention, "game not found -> attention");

        // A game update wiped the loader while the game was closed: wait one tick, then repair.
        m = new DaemonMonitor();
        d = m.Evaluate(Obs(0, false, all, pluginFile: false));
        Check(!d.Repair && d.Health == TrayHealth.Attention && d.Notice == DaemonNotice.None, "first sighting: wait (" + d.Summary + ")");
        d = m.Evaluate(Obs(3, false, all, pluginFile: false));
        Check(!d.Repair, "3 s later: still waiting");
        d = m.Evaluate(Obs(5, false, all, pluginFile: false));
        Check(d.Repair, "5 s later, still closed: repair");
        m.RepairFailed("Access denied", t0.AddSeconds(5));
        d = m.Evaluate(Obs(10, false, all, pluginFile: false));
        Check(!d.Repair && d.Health == TrayHealth.Error && d.Summary.Contains("Access denied"), "failed repair: red, no retry yet (" + d.Summary + ")");
        d = m.Evaluate(Obs(35, false, all, pluginFile: false));
        Check(d.Repair, "retried 30 s after the failure");
        m.RepairSucceeded();
        d = m.Evaluate(Obs(40, false));
        Check(!d.Repair && d.Health == TrayHealth.Idle && m.LastRepairError == null, "repaired: idle again");

        // The game starts in between: never repair while it runs, and the wait restarts after it closes.
        m = new DaemonMonitor();
        m.Evaluate(Obs(0, false, all, pluginFile: false));
        d = m.Evaluate(Obs(5, true, all, pluginFile: false));
        Check(!d.Repair && d.Notice == DaemonNotice.GameNeedsRestart && d.Health == TrayHealth.Attention, "missing while running: notice, no repair");
        d = m.Evaluate(Obs(10, true, all, pluginFile: false));
        Check(!d.Repair && d.Notice == DaemonNotice.None, "notice shown once");
        d = m.Evaluate(Obs(15, false, all, pluginFile: false));
        Check(!d.Repair, "game closed: wait again first");
        d = m.Evaluate(Obs(20, false, all, pluginFile: false));
        Check(d.Repair, "then repair");
        m.RepairSucceeded();
        m.Evaluate(Obs(25, false));
        d = m.Evaluate(Obs(30, true, all, pluginFile: false));
        Check(d.Notice == DaemonNotice.GameNeedsRestart, "a new episode notifies again");

        // A newer plugin waiting for the game to close is not a problem to report.
        m = new DaemonMonitor();
        d = m.Evaluate(Obs(0, true, new[] { InstallPiece.Plugin }, pluginFile: true, status: Beat(t0)));
        Check(d.Notice == DaemonNotice.None && d.Health == TrayHealth.Active && !d.Repair, "outdated plugin while playing: green, quiet");
        d = m.Evaluate(Obs(60, false, new[] { InstallPiece.Plugin }, pluginFile: true));
        Check(!d.Repair && d.Summary.StartsWith("Updating"), "game closed: updating (" + d.Summary + ")");
        Check(m.Evaluate(Obs(65, false, new[] { InstallPiece.Plugin }, pluginFile: true)).Repair, "and replaced after the delay");
        m = new DaemonMonitor();
        d = m.Evaluate(Obs(0, true, new[] { InstallPiece.Plugin }, pluginFile: false));
        Check(d.Notice == DaemonNotice.GameNeedsRestart, "plugin file gone while playing: notice");

        // Plugin heartbeat: green while fresh, with the count.
        m = new DaemonMonitor();
        d = m.Evaluate(Obs(10, true, status: Beat(t0, saved: 2)));
        Check(d.Health == TrayHealth.Active && d.PluginAlive && d.Summary.Contains("2 logs this session"), "active: " + d.Summary);
        Check(m.Evaluate(Obs(10, true, status: Beat(t0, saved: 1))).Summary.Contains("1 log this session"), "singular");
        Check(m.Evaluate(Obs(10, true, status: Beat(t0, state: "starting"))).Summary.StartsWith("Mod starting"), "starting");
        d = m.Evaluate(Obs(200, false, status: Beat(t0)));
        Check(d.Health == TrayHealth.Idle && !d.PluginAlive, "stale heartbeat, game closed: idle");

        // Game running, the plugin never reports: amber + one notice after 90 s.
        m = new DaemonMonitor();
        d = m.Evaluate(Obs(0, true));
        Check(d.Health == TrayHealth.Idle && d.Notice == DaemonNotice.None && d.Summary.StartsWith("Game starting"), "just started: give it time");
        d = m.Evaluate(Obs(60, true));
        Check(d.Notice == DaemonNotice.None, "60 s: still waiting");
        d = m.Evaluate(Obs(90, true));
        Check(d.Notice == DaemonNotice.RunningWithoutMod && d.Health == TrayHealth.Attention, "90 s without heartbeat: notice + amber");
        d = m.Evaluate(Obs(95, true));
        Check(d.Notice == DaemonNotice.None && d.Health == TrayHealth.Attention, "notice once per game session");
        m.Evaluate(Obs(100, false));
        m.Evaluate(Obs(105, true));
        d = m.Evaluate(Obs(200, true));
        Check(d.Notice == DaemonNotice.RunningWithoutMod, "next game session notifies again");
        d = m.Evaluate(Obs(205, true, status: Beat(t0.AddSeconds(204))));
        Check(d.Health == TrayHealth.Active && d.Notice == DaemonNotice.None, "late heartbeat: green");

        // Game folder unknown: no "restart the game" advice (the daemon finds the running game within a minute).
        m = new DaemonMonitor();
        m.Evaluate(Obs(0, true, game: null));
        d = m.Evaluate(Obs(120, true, game: null));
        Check(d.Notice == DaemonNotice.None && d.Health == TrayHealth.Attention, "game not found while running: amber, no restart notice");

        // Plugin error: red, one notice per game process.
        m = new DaemonMonitor();
        d = m.Evaluate(Obs(10, true, status: Beat(t0, state: "error", message: "Game classes not found.")));
        Check(d.Health == TrayHealth.Error && d.Notice == DaemonNotice.PluginError && d.Summary.Contains("Game classes not found."), "error: " + d.Summary);
        d = m.Evaluate(Obs(15, true, status: Beat(t0.AddSeconds(15), state: "error")));
        Check(d.Notice == DaemonNotice.None && d.Summary.Contains("BepInEx log"), "same process: no second notice, fallback text");
        d = m.Evaluate(Obs(300, true, status: Beat(t0.AddSeconds(300), state: "error", pid: 200)));
        Check(d.Notice == DaemonNotice.PluginError, "new game process: notice again");
        Check(Throws<ArgumentNullException>(() => m.Evaluate(null)), "null observation rejected");

        // The disclaimer was updated and the player has not accepted it yet: nothing is written into the game.
        m = new DaemonMonitor();
        DaemonObservation Pending(double sec, bool running, InstallPiece[] missing, bool pluginFile)
        {
            var o = Obs(sec, running, missing, pluginFile);
            o.ConsentPending = true;
            return o;
        }
        d = m.Evaluate(Pending(0, false, new[] { InstallPiece.Plugin }, true));
        d = m.Evaluate(Pending(60, false, new[] { InstallPiece.Plugin }, true));
        Check(!d.Repair && d.Health == TrayHealth.Attention && d.Summary.Contains("disclaimer"), "consent pending: the new plugin is not installed (" + d.Summary + ")");
        d = m.Evaluate(Pending(65, true, all, false));
        Check(!d.Repair && d.Notice == DaemonNotice.None, "nor promised back with a notice while the game runs");
        m.Evaluate(Pending(70, false, all, false));
        d = m.Evaluate(Obs(80, false, all, pluginFile: false));
        Check(d.Repair, "accepted: repaired at the next tick");

        // Nothing to repair, but the disclaimer still waits: the status and icon say so, even while logs are saved.
        m = new DaemonMonitor();
        var idle = Obs(0, false);
        idle.ConsentPending = true;
        d = m.Evaluate(idle);
        Check(d.Health == TrayHealth.Attention && d.Summary == DaemonMonitor.ConsentPendingSummary, "consent pending, game closed: " + d.Summary);
        var saving = Obs(10, true, status: Beat(t0.AddSeconds(10), saved: 2));
        saving.ConsentPending = true;
        d = m.Evaluate(saving);
        Check(d.Health == TrayHealth.Attention && d.Summary == DaemonMonitor.ConsentPendingSummary && d.PluginAlive, "consent pending, saving: " + d.Summary);
        d = m.Evaluate(Obs(20, true, status: Beat(t0.AddSeconds(20), saved: 2)));
        Check(d.Health == TrayHealth.Active, "accepted: back to the normal status");

        // A first install while the game runs: no "a game update removed it" notice, the status says what to do.
        m = new DaemonMonitor();
        m.SetupPending();
        d = m.Evaluate(Obs(0, true, all, pluginFile: false));
        Check(d.Notice == DaemonNotice.None && d.Health == TrayHealth.Attention && d.Summary.Contains("finish installing"), "setup pending: " + d.Summary);
        d = m.Evaluate(Obs(10, false, all, pluginFile: false));
        Check(d.Summary.StartsWith("Installing") && !d.Repair, "game closed: installing (" + d.Summary + ")");
        Check(m.Evaluate(Obs(15, false, all, pluginFile: false)).Repair, "and installed after the delay");
        m.RepairSucceeded();
        m.Evaluate(Obs(20, false));
        d = m.Evaluate(Obs(30, true, all, pluginFile: false));
        Check(d.Notice == DaemonNotice.GameNeedsRestart && d.Summary.StartsWith("Mod missing"), "later episodes are game updates again");
    }

    sealed class FakeUninstall : IUninstallHost
    {
        public readonly List<string> Steps = new List<string>();
        public bool Quiet { get; set; }
        public bool Portable { get; set; }
        public bool Confirms = true, GameRunning, GivesUpOnGame, TrayStays;
        public string Left;
        public UninstallResult? Result;
        public string Detail;
        public bool Confirm() { Steps.Add("confirm"); return Confirms; }
        public bool IsGameRunning() { Steps.Add("game?"); return GameRunning; }
        public bool WaitForGameToClose() { Steps.Add("wait game"); return !GivesUpOnGame; }
        public IDisposable StopTray(out bool stopped)
        {
            Steps.Add("stop tray");
            stopped = !TrayStays;
            return new Release(() => Steps.Add("release mutex"));
        }
        public void StopLegacy() => Steps.Add("legacy");
        public string RemoveFromGame() { Steps.Add("game files"); return Left; }
        public void RemoveRegistration() => Steps.Add("registration");
        public void DeleteInstallFolder() => Steps.Add("install folder");
        public void Report(UninstallResult result, string detail) { Steps.Add("report " + result); Result = result; Detail = detail; }
        sealed class Release : IDisposable { readonly Action a; public Release(Action a) { this.a = a; } public void Dispose() => a(); }
    }

    static void UninstallFlowTests()
    {
        Console.WriteLine("30. UninstallFlow: what an uninstall removes, and when it stops");
        string Run(FakeUninstall h, out int code) { code = UninstallFlow.Run(h); return string.Join(" > ", h.Steps); }

        var h = new FakeUninstall();
        var steps = Run(h, out var code);
        Check(code == 0 && steps == "confirm > wait game > stop tray > legacy > game files > registration > release mutex > report Done > install folder",
              "interactive: everything, in order, the folder last: " + steps);
        h = new FakeUninstall { Portable = true };
        steps = Run(h, out code);
        Check(code == 0 && !steps.Contains("install folder"), "portable: the folder stays (the player deletes it)");
        h = new FakeUninstall { Confirms = false };
        steps = Run(h, out code);
        Check(code == 1 && steps == "confirm > report Cancelled", "No at the confirmation: nothing changed");
        h = new FakeUninstall { GivesUpOnGame = true };
        steps = Run(h, out code);
        Check(code == 1 && steps == "confirm > wait game > report Cancelled", "Cancel while the game runs: nothing changed");

        h = new FakeUninstall { Quiet = true };
        steps = Run(h, out code);
        Check(code == 0 && steps == "game? > stop tray > legacy > game files > registration > release mutex > report Done > install folder", "--quiet: no dialogs: " + steps);
        h = new FakeUninstall { Quiet = true, GameRunning = true };
        steps = Run(h, out code);
        Check(code == 1 && steps == "game? > report GameRunning", "--quiet with the game running: exit 1, nothing changed (no half-removed mod)");

        h = new FakeUninstall { TrayStays = true };
        steps = Run(h, out code);
        Check(code == 1 && steps == "confirm > wait game > stop tray > report TrayStillRunning > release mutex",
              "a tray that did not exit: stop, so it cannot put the mod back: " + steps);
        h = new FakeUninstall { Quiet = true, TrayStays = true };
        Run(h, out code);
        Check(code == 1 && h.Result == UninstallResult.TrayStillRunning, "same with --quiet: exit 1");

        h = new FakeUninstall { Left = "winhttp.dll (in use)" };
        steps = Run(h, out code);
        Check(code == 1 && h.Result == UninstallResult.GameFilesLeft && h.Detail == "winhttp.dll (in use)" &&
              !steps.Contains("registration") && !steps.Contains("install folder"),
              "files left in the game: registration and settings kept so it can be run again, exit 1: " + steps);
        Check(Throws<ArgumentNullException>(() => UninstallFlow.Run(null)), "null host rejected");
    }

    static void WatchedFolderTests()
    {
        Console.WriteLine("31. WatchedFolder: a logs folder renamed or recycled under the watcher");
        var root = TempDir();
        var logs = Path.Combine(root, "PTCGL Battle Logs");
        Directory.CreateDirectory(logs);
        var created = Directory.GetCreationTimeUtc(logs);
        var w = new WatchedFolder(logs);
        Check(w.Path == logs && w.StillThere(), "the folder it started on");
        var renamed = Path.Combine(root, "renamed in Explorer");
        Directory.Move(logs, renamed);
        Check(!w.StillThere(), "renamed away (or moved to the Recycle Bin): not there any more");
        Directory.Move(renamed, logs);
        Check(w.StillThere(), "renamed back: the same folder again");
        Directory.Delete(logs);
        System.Threading.Thread.Sleep(20);
        Directory.CreateDirectory(logs);
        if (Directory.GetCreationTimeUtc(logs) != created)
            Check(!w.StillThere(), "deleted and made again at the same path: another folder");
        else
            Console.WriteLine("    (this file system gave the new folder the old creation time; only the rename case can be told apart here)");
        Check(!new WatchedFolder(Path.Combine(root, "missing")).StillThere(), "a folder that does not exist is never there");
        Check(NoThrow(() => new WatchedFolder("bad\0path").StillThere()) && NoThrow(() => new WatchedFolder(null).StillThere()), "bad paths do not throw");
    }

    static void SaveNotificationTests()
    {
        Console.WriteLine("21. SaveNotificationQueue");
        var dir = TempDir();
        var t0 = new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Func<string, bool> exists = present.Contains;
        var q = new SaveNotificationQueue(TimeSpan.FromSeconds(1.5), 3);
        string P(string name) => Path.Combine(dir, name);

        var noResult = P("2026-10-19 21.34.05 - NO-RESULT vs Gary.txt");
        var victory = P("2026-10-19 21.34.05 - VICTORY vs Gary.txt");
        Check(!q.Add(P("2026-10-19 21.34.05 - VICTORY vs Gary.txt.tmp"), t0) && !q.Add(P("index.csv"), t0) &&
              !q.Add(P("2026-10-19 21.34.05 - VICTORY vs Gary.seal"), t0) && !q.Add(P("notes.txt"), t0) && !q.Add(null, t0),
              "temp, seal, index and other files ignored");
        Check(q.Add(victory, t0) && q.PendingCount == 1, "a log name is queued");
        present.Add(victory);
        Check(q.TakeDue(t0.AddSeconds(1), exists).Count == 0, "not before the delay");
        q.Add(victory, t0.AddSeconds(1));   // overwritten with a longer read: delay restarts
        Check(q.TakeDue(t0.AddSeconds(2), exists).Count == 0, "a new event restarts the delay");
        var due = q.TakeDue(t0.AddSeconds(2.5), exists);
        Check(due.SequenceEqual(new[] { victory }), "announced once quiet");
        Check(q.TakeDue(t0.AddSeconds(10), exists).Count == 0, "and only once");
        Check(!q.Add(victory, t0.AddSeconds(20)) && q.PendingCount == 0, "later overwrites of the same file never notify again");

        // NO-RESULT saved, then moved to VICTORY before the delay: only the final name is announced.
        var defeat0 = P("2026-10-19 22.00.00 - NO-RESULT vs Misty.txt");
        var defeat1 = P("2026-10-19 22.00.00 - DEFEAT vs Misty.txt");
        q.Add(defeat0, t0.AddSeconds(30));
        q.Add(defeat1, t0.AddSeconds(30.5));
        present.Add(defeat1);
        due = q.TakeDue(t0.AddSeconds(32.5), exists);
        Check(due.SequenceEqual(new[] { defeat1 }), "renamed file: only the name that still exists");

        // Two matches at once (sorted), and the remembered set is bounded.
        var a = P("2026-10-19 23.00.00 - VICTORY vs A.txt");
        var b = P("2026-10-19 23.00.01 - EXPORTED.txt");
        present.Add(a); present.Add(b);
        q.Add(b, t0.AddSeconds(40)); q.Add(a, t0.AddSeconds(40));
        Check(q.TakeDue(t0.AddSeconds(45), exists).SequenceEqual(new[] { a, b }), "several due files, in name order");
        Check(q.Add(victory, t0.AddSeconds(50)), "oldest announced name forgotten past the limit (3)");
        q.ClearPending();
        Check(q.PendingCount == 0, "ClearPending");
        Check(q.TakeDue(t0.AddSeconds(60), p => throw new IOException("x")).Count == 0, "a throwing exists check is contained");
        Check(NoThrow(() => new SaveNotificationQueue().TakeDue(t0, null)), "default queue, real File.Exists");
    }

    static void IcoFileTests()
    {
        Console.WriteLine("22. IcoFile");
        var ico = File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "Tray", "app.ico"));
        bool IsPng(byte[] b) => b != null && b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
        foreach (var (want, expect) in new[] { (16, 16), (17, 20), (20, 20), (24, 24), (30, 32), (32, 32), (100, 128), (256, 256), (512, 256) })
        {
            var ok = IcoFile.TryGetPngFrame(ico, want, out var png, out var size);
            Check(ok && size == expect && IsPng(png), "app.ico at " + want + " px -> " + size + " px PNG frame");
        }
        using (var s = new MemoryStream(ico))
            Check(IcoFile.TryGetPngFrame(s, 16, out var p16, out _) && IsPng(p16), "from a stream");
        Check(!IcoFile.TryGetPngFrame((byte[])null, 16, out _, out _) && !IcoFile.TryGetPngFrame(new byte[5], 16, out _, out _) &&
              !IcoFile.TryGetPngFrame(Encoding.ASCII.GetBytes("not an icon at all"), 16, out _, out _) && !IcoFile.TryGetPngFrame(ico, 0, out _, out _),
              "garbage rejected");
        var truncated = ico.Take(200).ToArray();
        Check(!IcoFile.TryGetPngFrame(truncated, 16, out _, out _), "frame outside the file rejected");
        var bmp = (byte[])ico.Clone();
        int off = BitConverter.ToInt32(bmp, 6 + 12);   // first frame (16 px)
        bmp[off] = 40;                                   // BITMAPINFOHEADER instead of PNG
        Check(!IcoFile.TryGetPngFrame(bmp, 16, out _, out _), "non-PNG frame -> false (caller falls back to System.Drawing)");
    }

    static void ConfigExtrasTests()
    {
        Console.WriteLine("23. BepInEx cfg: reset folder, seal mode");
        var g = FakeGame();
        var gi = new GameInstall(g);
        Check(NoThrow(() => gi.ResetOutputFolder()) && !File.Exists(gi.ConfigPath), "reset without a cfg: nothing to do, no file created");
        Check(gi.ReadSealMode() == null, "no cfg -> seal mode null (plugin default)");
        Check(GameInstall.SealModes.SequenceEqual(new[] { "Footer", "Sidecar", "Off" }) && GameInstall.DefaultSealMode == "Footer", "modes per contract");

        var generated = "## Settings file was created by plugin PTCGL Auto Battle Log v1.1.0\r\n\r\n[General]\r\n\r\n# Setting type: String\r\n# Default value: \r\n" +
                        "OutputFolder = E:\\Logs\r\n\r\nSaveManualExports = true\r\n\r\n[Metadata]\r\n\r\n# Acceptable values: Footer, Sidecar, Off\r\nMode = Footer\r\n";
        Directory.CreateDirectory(Path.GetDirectoryName(gi.ConfigPath));
        File.WriteAllText(gi.ConfigPath, generated);
        Check(gi.ReadOutputFolder() == @"E:\Logs" && gi.ReadSealMode() == "Footer", "reads both");
        gi.ResetOutputFolder();
        Check(gi.ReadOutputFolder() == null && File.ReadAllText(gi.ConfigPath) == generated.Replace("OutputFolder = E:\\Logs", "OutputFolder = "),
              "reset writes an empty value, everything else kept");
        Check(gi.ReadConfigValue("General", "OutputFolder") == "", "raw value is empty, not absent");

        gi.WriteSealMode("sidecar");
        Check(gi.ReadSealMode() == "Sidecar" && File.ReadAllText(gi.ConfigPath).Contains("\r\nMode = Sidecar\r\n"), "mode written in canonical case");
        gi.WriteSealMode("Off");
        Check(gi.ReadSealMode() == "Off", "Off");
        Check(Throws<ArgumentException>(() => gi.WriteSealMode("Signed")) && Throws<ArgumentException>(() => gi.WriteSealMode(null)), "unknown mode refused");
        File.WriteAllText(gi.ConfigPath, "[Metadata]\nMode = fOOTER\n");
        Check(gi.ReadSealMode() == "Footer", "case-insensitive like BepInEx");
        File.WriteAllText(gi.ConfigPath, "[Metadata]\nMode = 7\n");
        Check(gi.ReadSealMode() == null, "unknown value -> null");

        File.WriteAllText(gi.ConfigPath, "[General]\nOutputFolder = D:\\L\n");
        gi.WriteSealMode("Sidecar");
        Check(File.ReadAllText(gi.ConfigPath) == "[General]\nOutputFolder = D:\\L\n\n[Metadata]\nMode = Sidecar\n", "missing [Metadata] appended: " + File.ReadAllText(gi.ConfigPath).Replace("\n", "⏎"));
        Check(gi.ReadOutputFolder() == @"D:\L", "folder untouched");
        Check(Throws<ArgumentException>(() => gi.WriteConfigValue("", "k", "v")) && Throws<ArgumentException>(() => gi.WriteConfigValue("s", " ", "v")), "section and key required");
        gi.WriteConfigValue("General", "SaveManualExports", "false");
        Check(gi.ReadConfigValue("General", "SaveManualExports") == "false", "generic write/read");
    }
}
