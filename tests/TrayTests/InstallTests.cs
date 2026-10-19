using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PtcglAutoBattleLog;
using PtcglAutoBattleLog.Tray.Logic;

static partial class Program
{
    static readonly byte[] PluginV2 = Encoding.ASCII.GetBytes("MZ fake plugin build 2 " + new string('x', 5000));
    static readonly byte[] PluginV1 = Encoding.ASCII.GetBytes("MZ fake plugin build 1");

    static string BepInExZipPath => Path.Combine(RepoRoot(), "cache", BuildInfo.BepInExZip);

    static int zipOpens;
    static Payload MakePayload(byte[] plugin, string zipPath = null)
    {
        zipPath = zipPath ?? BepInExZipPath;
        return new Payload(() => new MemoryStream(plugin, false), () => { zipOpens++; return File.OpenRead(zipPath); });
    }

    /// <summary>A folder that looks like the game: just the exe.</summary>
    static string FakeGame()
    {
        var g = Path.Combine(TempDir(), "Pokemon TCG Live");
        Directory.CreateDirectory(g);
        File.WriteAllText(Path.Combine(g, GameLocator.GameExe), "game");
        return g;
    }

    static string ZipEntryPath(string root, string entry) =>
        entry.Split('/').Aggregate(root, Path.Combine);

    static void AppPathsTests()
    {
        Console.WriteLine("1. AppPaths");
        var p = new AppPaths(Path.Combine("L", "AppData", "Local"), Path.Combine("U", "Documents"));
        Check(p.Root == Path.Combine("L", "AppData", "Local", "PtcglAutoBattleLog"), "Root under local app data: " + p.Root);
        Check(p.InstalledExe == Path.Combine(p.Root, "PtcglAutoBattleLog.exe"), "installed exe");
        Check(p.OldExe == p.InstalledExe + ".old", "old exe");
        Check(p.SettingsFile == Path.Combine(p.Root, "settings.json") && p.PluginStatusFile == Path.Combine(p.Root, "plugin-status.json"), "settings + status files");
        Check(p.TrayLogFile == Path.Combine(p.Root, "tray.log") && p.UpdatesDir == Path.Combine(p.Root, "updates"), "log + updates");
        Check(p.DefaultLogsFolder == Path.Combine("U", "Documents", "PTCGL Battle Logs"), "default logs folder");
        Check(NoThrow(() => AppPaths.ForCurrentUser()), "ForCurrentUser works");
        Check(Throws<ArgumentException>(() => new AppPaths(null, "d")), "null local app data rejected");
    }

    static void GameLocatorTests()
    {
        Console.WriteLine("2. GameLocator");
        var game = FakeGame();
        var notGame = TempDir();
        Check(GameLocator.IsGameDir(game), "folder with the exe is the game");
        Check(!GameLocator.IsGameDir(notGame) && !GameLocator.IsGameDir(null) && !GameLocator.IsGameDir("") &&
              !GameLocator.IsGameDir(Path.Combine(notGame, "nope")), "other folders are not");
        Check(NoThrow(() => GameLocator.IsGameDir("bad\"<>|\0path")), "invalid characters do not throw");

        var c = GameLocator.DefaultCandidates("/u", null, "/pf", "");
        Check(c.Count == 2 && c[0] == Path.Combine("/u", "The Pok\u00e9mon Company International", "Pok\u00e9mon Trading Card Game Live"),
              "candidates built from the non-empty bases: " + string.Join(" | ", c));
        Check(GameLocator.DefaultCandidates("/x", "/x", "/x", "/x").Count == 1, "duplicates removed");

        Check(GameLocator.Find(game, new[] { notGame }, null, null) == game, "saved folder first");
        Check(GameLocator.Find(notGame, new[] { notGame, game }, null, null) == game, "invalid saved -> first valid candidate");
        var icon = "\"" + Path.Combine(game, GameLocator.GameExe) + "\",0";
        Check(GameLocator.Find(null, null, new[] { "", null, icon }, null) == game, "registry DisplayIcon with quotes and index -> folder");
        Check(GameLocator.Find(null, new string[0], new[] { "  " + game + "  " }, null) == game, "registry InstallLocation trimmed");
        Check(GameLocator.Find(null, null, null, game) == game, "running process folder last");
        Check(GameLocator.Find(null, null, null, Path.Combine(game, GameLocator.GameExe)) == game, "an exe path is turned into its folder");
        Check(GameLocator.Find(notGame, new[] { notGame }, new[] { "garbage\"" }, null) == null, "nothing valid -> null");
    }

    static void PayloadTests()
    {
        Console.WriteLine("3. Payload");
        int opens = 0;
        var p = new Payload(() => { opens++; return new MemoryStream(PluginV2); }, () => Stream.Null);
        var expected = SHA256.HashData(PluginV2);
        var h1 = p.PluginSha256;
        h1[0] ^= 0xFF;
        var h2 = p.PluginSha256;
        Check(h2.SequenceEqual(expected), "PluginSha256 is the plugin's SHA-256");
        Check(opens == 1, "computed once (" + opens + " opens)");
        Check(!h1.SequenceEqual(h2), "callers get a copy");
        var dir = TempDir();
        File.WriteAllBytes(Path.Combine(dir, "p.dll"), PluginV1);
        Check(Payload.FromFiles(Path.Combine(dir, "p.dll"), BepInExZipPath).PluginSha256.SequenceEqual(SHA256.HashData(PluginV1)), "FromFiles");
    }

    static void GameInstallTests()
    {
        Console.WriteLine("4. GameInstall against the real BepInEx zip");
        Check(File.Exists(BepInExZipPath), "BepInEx zip present: " + BepInExZipPath);
        string[] zipEntries;
        using (var z = ZipFile.OpenRead(BepInExZipPath)) zipEntries = z.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName).ToArray();
        var all = new[] { InstallPiece.WinHttp, InstallPiece.DoorstopConfig, InstallPiece.BepInExPreloader, InstallPiece.BepInExCore, InstallPiece.Plugin };

        // Fresh game folder.
        var g = FakeGame();
        var gi = new GameInstall(g);
        var payload = MakePayload(PluginV2);
        Check(gi.PluginPath == Path.Combine(g, "BepInEx", "plugins", "PtcglAutoBattleLog", "PtcglAutoBattleLog.Plugin.dll"), "plugin path");
        Check(gi.ConfigPath == Path.Combine(g, "BepInEx", "config", "ptcgl.autobattlelog.cfg"), "cfg path");
        Check(gi.BepInExLogPath == Path.Combine(g, "BepInEx", "LogOutput.log"), "BepInEx log path");
        Check(!gi.HasBepInEx, "no BepInEx yet");
        Check(gi.Missing(payload).SequenceEqual(all), "everything missing");
        var repaired = gi.Repair(payload);
        Check(repaired.SequenceEqual(all), "Repair returns what was missing before");
        Check(gi.Missing(payload).Count == 0, "nothing missing after repair");
        Check(gi.HasBepInEx, "HasBepInEx after repair");
        Check(zipEntries.Length == 22 && zipEntries.All(e => File.Exists(ZipEntryPath(g, e))), "all " + zipEntries.Length + " zip files extracted with OS paths");
        using (var z = ZipFile.OpenRead(BepInExZipPath))
            Check(z.Entries.Where(e => e.Name.Length > 0).All(e =>
            {
                using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray().SequenceEqual(File.ReadAllBytes(ZipEntryPath(g, e.FullName))); }
            }), "extracted bytes identical to the zip");
        Check(File.ReadAllBytes(gi.PluginPath).SequenceEqual(PluginV2), "plugin written");
        Check(!FilesUnder(g).Any(f => f.EndsWith(".tmp")), "no .tmp files left");
        var before = FilesUnder(g).ToDictionary(f => f, f => File.GetLastWriteTimeUtc(Path.Combine(g, f)));
        zipOpens = 0;
        Check(gi.Repair(payload).Count == 0 && zipOpens == 0, "second repair does nothing");
        Check(FilesUnder(g).SequenceEqual(before.Keys) && before.All(kv => File.GetLastWriteTimeUtc(Path.Combine(g, kv.Key)) == kv.Value), "files untouched");

        // Plugin hash mismatch (an older plugin): only the plugin is replaced.
        File.WriteAllBytes(gi.PluginPath, PluginV1);
        Check(gi.Missing(payload).SequenceEqual(new[] { InstallPiece.Plugin }), "old plugin reported as Plugin");
        zipOpens = 0;
        Check(gi.Repair(payload).SequenceEqual(new[] { InstallPiece.Plugin }) && zipOpens == 0, "repair replaces only the plugin, zip not opened");
        Check(File.ReadAllBytes(gi.PluginPath).SequenceEqual(PluginV2), "plugin now matches the payload");

        // Only the plugin missing: BepInEx files that are not loader pieces are not re-extracted.
        File.Delete(Path.Combine(g, "changelog.txt"));
        File.Delete(gi.PluginPath);
        Check(gi.Missing(payload).SequenceEqual(new[] { InstallPiece.Plugin }), "only Plugin missing");
        gi.Repair(payload);
        Check(File.Exists(gi.PluginPath) && !File.Exists(Path.Combine(g, "changelog.txt")), "complete loader left alone");

        // 1.0 leftovers: PtcglAutoBattleLog.dll in our folder.
        var legacy = Path.Combine(g, "BepInEx", "plugins", "PtcglAutoBattleLog", "PtcglAutoBattleLog.dll");
        File.WriteAllBytes(legacy, PluginV1);
        Check(gi.Missing(payload).SequenceEqual(new[] { InstallPiece.Plugin }), "legacy dll beside the plugin -> Plugin");
        gi.Repair(payload);
        Check(!File.Exists(legacy) && gi.Missing(payload).Count == 0, "legacy dll deleted");
        File.Delete(gi.PluginPath);
        File.WriteAllBytes(legacy, PluginV1);
        gi.Repair(payload);
        Check(!File.Exists(legacy) && File.ReadAllBytes(gi.PluginPath).SequenceEqual(PluginV2), "1.0 install upgraded: legacy gone, new plugin in place");

        // A game update wiped the loader but left the player's own doorstop_config.ini and a modded core file.
        var g2 = FakeGame();
        var gi2 = new GameInstall(g2);
        File.WriteAllText(Path.Combine(g2, "doorstop_config.ini"), "[General]\nenabled = true\n# mine\n");
        Directory.CreateDirectory(Path.Combine(g2, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(g2, "BepInEx", "core", "BepInEx.dll"), "player's own build");
        var m2 = gi2.Missing(payload);
        Check(m2.SequenceEqual(new[] { InstallPiece.WinHttp, InstallPiece.BepInExPreloader, InstallPiece.Plugin }), "missing: " + string.Join(",", m2));
        gi2.Repair(payload);
        Check(File.ReadAllText(Path.Combine(g2, "doorstop_config.ini")) == "[General]\nenabled = true\n# mine\n", "existing doorstop_config.ini preserved");
        Check(File.ReadAllText(Path.Combine(g2, "BepInEx", "core", "BepInEx.dll")) == "player's own build", "existing BepInEx file never overwritten");
        Check(File.Exists(Path.Combine(g2, "winhttp.dll")) && File.Exists(Path.Combine(g2, "BepInEx", "core", "BepInEx.Preloader.dll")), "missing loader files extracted");
        Check(gi2.Missing(payload).Count == 0, "complete after repair");

        // Not the game folder (uninstalled, or mid game update): refuse instead of recreating it.
        var gone = Path.Combine(TempDir(), "uninstalled");
        Check(Throws<DirectoryNotFoundException>(() => new GameInstall(gone).Repair(payload)) && !Directory.Exists(gone), "repair refuses a folder without the game");

        // Zip entries that could escape the game folder.
        var root = TempDir();
        Check(GameInstall.MapEntry(root, "BepInEx/core/BepInEx.dll") == Path.Combine(root, "BepInEx", "core", "BepInEx.dll"), "zip '/' mapped to OS path");
        Check(GameInstall.MapEntry(root, "BepInEx\\core\\x.dll") == Path.Combine(root, "BepInEx", "core", "x.dll"), "zip '\\' tolerated");
        Check(GameInstall.MapEntry(root, "BepInEx/plugins/") == null, "directory entry skipped");
        foreach (var bad in new[] { "../evil.dll", "BepInEx/../../evil.dll", "/etc/evil", "\\evil.dll", "C:/Windows/evil.dll", "C:evil.dll",
                                    "BepInEx\\..\\..\\evil.dll", "a/./b.dll", "a//b.dll", "...", "a/.../b", "dir./x", "dir /x", "x.dll ", "a/b.dll:ads",
                                    "con?.dll", "a<b", "a|b", "a\"b", "a*b", "tab\tname", "nul\0" })
            Check(Throws<InvalidDataException>(() => GameInstall.MapEntry(root, bad)), "unsafe zip entry rejected: " + bad.Replace("\0", "\\0").Replace("\t", "\\t"));

        var evilZip = Path.Combine(TempDir(), "evil.zip");
        using (var z = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("winhttp.dll").Open())) w.Write("x");
            using (var w = new StreamWriter(z.CreateEntry("../../escaped.txt").Open())) w.Write("x");
        }
        var g3 = FakeGame();
        Check(Throws<InvalidDataException>(() => new GameInstall(g3).Repair(MakePayload(PluginV2, evilZip))), "repair with a traversal zip throws");
        Check(!File.Exists(Path.Combine(g3, "winhttp.dll")) && !File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(g3)), "escaped.txt")),
              "nothing extracted from a bad zip, inside or outside");

        // Other mods and uninstall.
        var g4 = FakeGame();
        var gi4 = new GameInstall(g4);
        gi4.Repair(payload);
        gi4.WriteOutputFolder(Path.Combine("D:", "Logs"));
        Check(!gi4.HasOtherPlugins(), "only our plugin -> no other plugins");
        var other = Path.Combine(g4, "BepInEx", "plugins", "OtherMod");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "readme.txt"), "x");
        Check(!gi4.HasOtherPlugins(), "a folder without dlls does not count");
        Directory.CreateDirectory(Path.Combine(other, "lib"));
        File.WriteAllText(Path.Combine(other, "lib", "OtherMod.dll"), "x");
        Check(gi4.HasOtherPlugins(), "a dll in another plugin folder (nested) counts");
        File.WriteAllText(Path.Combine(g4, "BepInEx", "plugins", "PtcglAutoBattleLog", "Extra.dll"), "x");
        Directory.Delete(other, true);
        Check(!gi4.HasOtherPlugins(), "dlls inside our own folder do not count");
        File.WriteAllText(Path.Combine(g4, "BepInEx", "plugins", "Loose.dll"), "x");
        Check(gi4.HasOtherPlugins(), "a dll directly in plugins counts");
        File.Delete(Path.Combine(g4, "BepInEx", "plugins", "Loose.dll"));
        Directory.CreateDirectory(Path.Combine(g4, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(g4, "BepInEx", "patchers", "Patcher.dll"), "x");
        Check(gi4.HasOtherPlugins(), "a preloader patcher counts");
        Check(!new GameInstall(Path.Combine(TempDir(), "none")).HasOtherPlugins(), "no BepInEx folder -> false");

        gi4.Uninstall(removeBepInEx: true);
        Check(!Directory.Exists(Path.Combine(g4, "BepInEx", "plugins", "PtcglAutoBattleLog")) && !File.Exists(gi4.ConfigPath), "uninstall removed our folder and cfg");
        Check(File.Exists(Path.Combine(g4, "winhttp.dll")) && File.Exists(Path.Combine(g4, "BepInEx", "core", "BepInEx.dll")) &&
              File.Exists(Path.Combine(g4, "BepInEx", "patchers", "Patcher.dll")), "BepInEx kept: another mod uses it");

        var g5 = FakeGame();
        var gi5 = new GameInstall(g5);
        gi5.Repair(payload);
        File.WriteAllText(Path.Combine(g5, "player-notes.txt"), "keep");
        gi5.Uninstall(removeBepInEx: false);
        Check(!File.Exists(gi5.PluginPath) && gi5.HasBepInEx, "removeBepInEx=false keeps BepInEx");
        gi5.Uninstall(removeBepInEx: true);
        Check(FilesUnder(g5).SequenceEqual(new[] { GameLocator.GameExe, "player-notes.txt" }),
              "BepInEx removed when we installed it and nothing else uses it; game files kept: " + string.Join(",", FilesUnder(g5)));
        Check(NoThrow(() => gi5.Uninstall(true)), "uninstalling twice is fine");
    }

    static void LogsFolderSafetyTests()
    {
        Console.WriteLine("32. battle logs are never deleted: logs folders an uninstall would remove");
        var g = FakeGame();
        var gi = new GameInstall(g);
        var payload = MakePayload(PluginV2);
        gi.Repair(payload);
        var app = Path.Combine(TempDir(), "Local", "PtcglAutoBattleLog");
        var bepLogs = Path.Combine(g, "BepInEx", "BattleLogs");
        Check(gi.WhyNotLogsFolder(bepLogs) != null && gi.WhyNotLogsFolder(Path.Combine(g, "BepInEx")) != null &&
              gi.WhyNotLogsFolder(Path.Combine(gi.PluginPath, "..", "logs")) != null && gi.WhyNotLogsFolder(Path.Combine("BepInEx", "logs")) != null,
              "at or under the game's BepInEx folder (absolute, with '..', or relative to the game): refused");
        Check(gi.WhyNotLogsFolder(Path.Combine(app, "logs"), app) != null && gi.WhyNotLogsFolder(app.ToUpperInvariant(), app) != null,
              "at or under the app's own folder (any case): refused");
        Check(gi.WhyNotLogsFolder(Path.Combine(g, "BepInExtra")) == null && gi.WhyNotLogsFolder(g) == null &&
              gi.WhyNotLogsFolder(Path.GetDirectoryName(app), app) == null && gi.WhyNotLogsFolder(Path.Combine(TempDir(), "Logs"), app) == null && gi.WhyNotLogsFolder(null) == null,
              "a folder beside them, the game folder itself, or one that contains the app folder: allowed");
        Check(Throws<ArgumentException>(() => gi.WriteOutputFolder(bepLogs)) && Throws<ArgumentException>(() => gi.WriteOutputFolder(Path.Combine(app, "x"), app)) &&
              gi.ReadOutputFolder() == null, "WriteOutputFolder refuses them and writes nothing");

        Check(FileOps.IsSameOrUnder(Path.Combine(g, "a", "b"), g) && FileOps.IsSameOrUnder(g + Path.DirectorySeparatorChar, g) &&
              !FileOps.IsSameOrUnder(g + "x", g) && !FileOps.IsSameOrUnder(null, g) && !FileOps.IsSameOrUnder(g, " "), "IsSameOrUnder");

        // A cfg edited by hand (or by an older build) still puts the logs there: uninstall keeps them.
        gi.WriteConfigValue("General", "OutputFolder", bepLogs);
        Directory.CreateDirectory(bepLogs);
        var log = Path.Combine(bepLogs, "2026-10-19 21.34.05 - VICTORY vs Gary.txt");
        File.WriteAllText(log, "a battle log");
        var kept = gi.Uninstall(removeBepInEx: true);
        Check(File.ReadAllText(log) == "a battle log", "uninstall with BepInEx removal: the logs in BepInEx\\BattleLogs survive");
        Check(FilesUnder(g).SequenceEqual(new[] { "BepInEx/BattleLogs/2026-10-19 21.34.05 - VICTORY vs Gary.txt", GameLocator.GameExe }),
              "everything else of BepInEx and the plugin is gone: " + string.Join(", ", FilesUnder(g)));
        Check(kept.Count == 1 && kept[0] == Path.Combine(g, "BepInEx"), "and the kept folder is reported: " + string.Join(", ", kept));

        var g2 = FakeGame();
        var gi2 = new GameInstall(g2);
        gi2.Repair(payload);
        var inPlugin = Path.Combine(g2, "BepInEx", "plugins", GameInstall.PluginFolderName, "logs");
        gi2.WriteConfigValue("General", "OutputFolder", inPlugin);
        Directory.CreateDirectory(Path.Combine(inPlugin, "sub"));
        File.WriteAllText(Path.Combine(inPlugin, "sub", "x.txt"), "kept");
        kept = gi2.Uninstall(removeBepInEx: false);
        Check(File.Exists(Path.Combine(inPlugin, "sub", "x.txt")) && !File.Exists(gi2.PluginPath) && gi2.HasBepInEx && kept.Count == 1,
              "logs inside our plugin folder: kept, with their subfolders; the plugin itself is gone");
        Check(new GameInstall(FakeGame()).Uninstall(true).Count == 0, "nothing to keep: empty list");
        var g3 = FakeGame();
        var gi3 = new GameInstall(g3);
        gi3.Repair(payload);
        gi3.WriteConfigValue("General", "OutputFolder", Path.GetDirectoryName(gi3.PluginPath));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(gi3.PluginPath), "log.txt"), "kept");
        gi3.Uninstall(removeBepInEx: false);
        Check(!File.Exists(gi3.PluginPath) && File.Exists(Path.Combine(Path.GetDirectoryName(gi3.PluginPath), "log.txt")),
              "logs in the plugin folder itself: the plugin DLL goes, the logs stay");

        var tree = TempDir();
        Directory.CreateDirectory(Path.Combine(tree, "a", "keep", "deep"));
        Directory.CreateDirectory(Path.Combine(tree, "b"));
        File.WriteAllText(Path.Combine(tree, "a", "keep", "deep", "log.txt"), "x");
        File.WriteAllText(Path.Combine(tree, "a", "other.txt"), "x");
        File.WriteAllText(Path.Combine(tree, "b", "y.txt"), "x");
        File.WriteAllText(Path.Combine(tree, "z.txt"), "x");
        Check(FileOps.DeleteTreeExcept(tree, Path.Combine(tree, "a", "keep")) && FilesUnder(tree).SequenceEqual(new[] { "a/keep/deep/log.txt" }),
              "DeleteTreeExcept keeps the folder, its contents and the way to it: " + string.Join(", ", FilesUnder(tree)));
        Check(FileOps.DeleteTreeExcept(Path.Combine(tree, "a", "keep"), Path.Combine(tree, "a", "keep")) && File.Exists(Path.Combine(tree, "a", "keep", "deep", "log.txt")),
              "the kept folder itself is never deleted");
        Check(!FileOps.DeleteTreeExcept(tree, null) && !Directory.Exists(tree) && !FileOps.DeleteTreeExcept(tree, null), "nothing to keep: all of it; a missing folder: nothing to do");
    }

    static void ConfigTests()
    {
        Console.WriteLine("5. BepInEx cfg: OutputFolder");
        var g = FakeGame();
        var gi = new GameInstall(g);
        Check(gi.ReadOutputFolder() == null, "no cfg -> null");

        const string win = @"C:\Users\Ash\Documents\PTCGL Battle Logs";
        gi.WriteOutputFolder(win);
        var text = File.ReadAllText(gi.ConfigPath);
        Check(text == "[General]\r\nOutputFolder = " + win + "\r\n", "new cfg, path written as is, like BepInEx: " + text.Replace("\r\n", "⏎"));
        Check(gi.ReadOutputFolder() == win, "round trip");
        Check(!File.ReadAllBytes(gi.ConfigPath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "new cfg has no BOM (as BepInEx)");

        // A cfg as BepInEx 5.4.23 generates it, with another section after ours.
        var generated = string.Join("\r\n", new[]
        {
            "## Settings file was created by plugin PTCGL Auto Battle Log v1.1.0",
            "## Plugin GUID: ptcgl.autobattlelog",
            "",
            "[General]",
            "",
            "## Folder where every battle log is saved, one .txt per match plus index.csv.",
            "# Setting type: String",
            "# Default value: " + win,
            "OutputFolder = " + win,
            "",
            "## Also save the log when you press Export in game.",
            "# Setting type: Boolean",
            "# Default value: true",
            "SaveManualExports = false",
            "",
            "[Debug]",
            "OutputFolder = not ours",
            "",
        });
        File.WriteAllBytes(gi.ConfigPath, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(generated)).ToArray());
        Check(gi.ReadOutputFolder() == win, "reads the value BepInEx wrote");
        gi.WriteOutputFolder(@"E:\Battle Logs");
        var bytes = File.ReadAllBytes(gi.ConfigPath);
        var after = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var expectedAfter = generated.Replace("OutputFolder = " + win, @"OutputFolder = E:\Battle Logs");
        Check(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM preserved");
        Check(after == expectedAfter, "only the OutputFolder line of [General] changed (comments, other keys, [Debug], CRLF kept)");
        Check(gi.ReadOutputFolder() == @"E:\Battle Logs", "new value read back");

        // No [General] section yet; LF line endings.
        File.WriteAllText(gi.ConfigPath, "# hand made\n[Other]\nKey = 1\n");
        gi.WriteOutputFolder("/home/ash/logs");
        Check(File.ReadAllText(gi.ConfigPath) == "# hand made\n[Other]\nKey = 1\n\n[General]\nOutputFolder = /home/ash/logs\n", "[General] appended, LF kept: " + File.ReadAllText(gi.ConfigPath).Replace("\n", "⏎"));
        Check(gi.ReadOutputFolder() == "/home/ash/logs", "read from the appended section");

        // [General] exists without the key: added under the header. Comments that mention the key are not entries.
        File.WriteAllText(gi.ConfigPath, "[General]\r\n# OutputFolder = commented\r\nSaveManualExports = true\r\n");
        gi.WriteOutputFolder(@"D:\L");
        Check(File.ReadAllText(gi.ConfigPath) == "[General]\r\nOutputFolder = D:\\L\r\n# OutputFolder = commented\r\nSaveManualExports = true\r\n", "key inserted under the header, comment untouched");

        // Duplicates: BepInEx applies lines in order, so the last one wins; writing sets them all.
        File.WriteAllText(gi.ConfigPath, "[General]\nOutputFolder = A:\\one\n  OutputFolder=A:\\\\two  \n");
        Check(gi.ReadOutputFolder() == @"A:\two", "last duplicate wins, spaces around '=' ignored, escaped form read");
        gi.WriteOutputFolder(@"B:\x");
        Check(File.ReadAllText(gi.ConfigPath) == "[General]\nOutputFolder = B:\\x\nOutputFolder = B:\\x\n", "every duplicate rewritten");

        // BepInEx's own reading rules.
        File.WriteAllText(gi.ConfigPath, "[General]\nOutputFolder = C:\\new\\tab\n");
        Check(gi.ReadOutputFolder() == @"C:\new\tab", "Windows path kept verbatim (no \\n or \\t unescaping)");
        File.WriteAllText(gi.ConfigPath, "[General]\nOutputFolder = \\\\\\\\server\\\\share\n");
        Check(gi.ReadOutputFolder() == @"\\server\share", "other values unescaped");
        File.WriteAllText(gi.ConfigPath, "[General]\nOutputFolder =   \n");
        Check(gi.ReadOutputFolder() == null, "empty value -> null");
        File.WriteAllText(gi.ConfigPath, "[general]\nOutputFolder = X:\\y\n");
        Check(gi.ReadOutputFolder() == null, "section names are case-sensitive in BepInEx");

        var roundTrips = new[] { win, "C:\\Users\\Jos\u00e9\\Pok\u00e9mon logs", @"C:\trailing\", "relative\\dir", "C:/forward/slashes",
                                 @"C:\a = b\[x]", @"C:\#notacomment", @"C:\new\tab", @"Z:\", @"E:\Logs (2026) & more!" };
        foreach (var path in roundTrips)
        {
            File.Delete(gi.ConfigPath);
            gi.WriteOutputFolder(path);
            Check(gi.ReadOutputFolder() == path, "round trip: " + path + "  (written: " + File.ReadAllLines(gi.ConfigPath)[1] + ")");
        }

        // Folders BepInEx itself cannot keep across its rewrite at every game start: refused, cfg untouched.
        var unkeepable = new[] { @"\\server\share\PTCGL Logs", @"C:\Users\O'Brien\Documents\PTCGL Battle Logs", @"D:\My ""quoted"" logs", @"\\?\C:\long",
                                 "relative\\new", " C:\\x", "C:\\x\ny" };
        File.WriteAllText(gi.ConfigPath, "[General]\nOutputFolder = C:\\kept\n");
        foreach (var path in unkeepable)
            Check(Throws<ArgumentException>(() => gi.WriteOutputFolder(path)) && File.ReadAllText(gi.ConfigPath) == "[General]\nOutputFolder = C:\\kept\n",
                  "refused, cfg unchanged: " + path.Replace("\n", "\\n"));
        // BepInEx keeps these, but the plugin falls back to the default folder for them (no Windows path
        // has '"'; a single leading '\' is what BepInEx leaves of \\server): refused too.
        var pluginRefuses = new[] { @"D:\""quoted""", @"\Logs", @"\server\share" };
        foreach (var path in pluginRefuses)
            Check(Throws<ArgumentException>(() => gi.WriteOutputFolder(path)) && File.ReadAllText(gi.ConfigPath) == "[General]\nOutputFolder = C:\\kept\n",
                  "refused (the plugin would not use it), cfg unchanged: " + path);
        Check(BepInExConfig.Serialize(win) == win, "a normal path is written as is");
        Check(BepInExConfig.Escape("a\\b\"c'd\te\nf") == "a\\b\\\"c\\'d\\te\\nf", "escaping leaves '\\' alone, like BepInEx 5.4.23");
        Check(BepInExConfig.Unescape("a\\qb\\") == "a\\qb\\", "unknown escapes and a trailing backslash kept");
        Check(Throws<ArgumentException>(() => gi.WriteOutputFolder(" ")) && Throws<ArgumentException>(() => gi.WriteOutputFolder(null)), "blank folder rejected");
        Check(!Directory.GetFiles(Path.GetDirectoryName(gi.ConfigPath)).Any(f => f.EndsWith(".tmp")), "no temp files left");

        // The reference: BepInEx's own TomlTypeConverter from lib/BepInEx.dll (the 5.4.23.3 build that ships).
        var bepDll = Path.Combine(RepoRoot(), "lib", "BepInEx.dll");
        Check(File.Exists(bepDll), "lib/BepInEx.dll present");
        if (!File.Exists(bepDll)) return;
        var conv = System.Reflection.Assembly.LoadFrom(bepDll).GetType("BepInEx.Configuration.TomlTypeConverter", true);
        var toValue = conv.GetMethod("ConvertToValue", new[] { typeof(string), typeof(Type) });
        var toText = conv.GetMethod("ConvertToString", new[] { typeof(object), typeof(Type) });
        string TheirRead(string s) => (string)toValue.Invoke(null, new object[] { s, typeof(string) });
        string TheirWrite(string s) => (string)toText.Invoke(null, new object[] { s, typeof(string) });
        foreach (var path in roundTrips)
            Check(BepInExConfig.Serialize(path) == TheirWrite(path) && TheirRead(TheirWrite(path)) == path, "written exactly as BepInEx writes it, and kept: " + path);
        foreach (var path in unkeepable)
            Check(TheirRead(TheirWrite(path).Trim()) != path, "BepInEx itself garbles it, so refusing is right: " + path.Replace("\n", "\\n"));
        foreach (var path in pluginRefuses)
            Check(TheirRead(TheirWrite(path).Trim()) == path, "BepInEx keeps it; only the plugin's rules refuse it: " + path);
        foreach (var s in new[] { "a\\b\"c'd\te\nf\r\0\a\b\v\f", @"\\srv\s", "plain", "" })
            Check(BepInExConfig.Escape(s) == TheirWrite(s), "same escaping as BepInEx: " + s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\0", "\\0").Replace("\t", "\\t"));
        foreach (var raw in new[] { win, @"C:\new\tab", @"C:\\a\\b", @"""C:\x", @"\\srv\s", @"\\\\srv\\s", @"a\qb\", @"C:\a\\b", @"D:\Ash\'s", "x\\ty", "plain", "" })
            Check(TheirRead(raw) == BepInExConfig.ConvertToObject(raw), "same reading as BepInEx: " + raw);
    }
}
