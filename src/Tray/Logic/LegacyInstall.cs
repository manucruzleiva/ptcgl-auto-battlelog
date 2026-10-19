using System;
using System.Collections.Generic;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// What the 1.0 PowerShell installer left in %LOCALAPPDATA%\PtcglAutoBattleLog: its payload,
    /// scripts, game-dir.txt and the "we installed BepInEx" flag. The tray reads the last two before
    /// removing everything, so an upgrade keeps the game folder and the BepInEx ownership.
    /// Stopping the watchdog and deleting its Startup shortcut need Windows and are the app's job.
    /// </summary>
    public sealed class LegacyInstall
    {
        public const string GameDirFileName = "game-dir.txt";
        public const string BepInExFlagFileName = "bepinex-installed-by-us.flag";
        public const string WatchdogScriptName = "watchdog.ps1";
        /// <summary>In the user's Startup folder; started the watchdog at logon.</summary>
        public const string StartupShortcutName = "PTCGL Auto Battle Log.lnk";

        internal const string PayloadDirName = "payload";
        internal static readonly string[] FileNames =
        {
            WatchdogScriptName, "common.ps1", "uninstall.ps1", "install.ps1", GameDirFileName, BepInExFlagFileName,
            "watchdog.log", "watchdog.log.old",
        };

        public LegacyInstall(string root)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("Folder is required.", nameof(root));
            Root = root;
        }

        public string Root { get; }

        /// <summary>Any 1.0 leftover is present.</summary>
        public bool Exists
        {
            get
            {
                try
                {
                    if (Directory.Exists(Path.Combine(Root, PayloadDirName))) return true;
                    foreach (var f in FileNames) if (File.Exists(Path.Combine(Root, f))) return true;
                }
                catch (Exception) { }
                return false;
            }
        }

        /// <summary>The game folder 1.0 used (Set-Content -Encoding UTF8 wrote a BOM); null when absent or blank.</summary>
        public string ReadGameDir() => ReadLine(GameDirFileName);

        /// <summary>
        /// 1.0 added BepInEx to this game folder. Its flag holds the folder it was written for;
        /// a flag for another folder does not count, an unreadable or empty one does.
        /// </summary>
        public bool BepInExInstalledByUs(string gameDir)
        {
            var flag = Path.Combine(Root, BepInExFlagFileName);
            try { if (!File.Exists(flag)) return false; }
            catch (Exception) { return false; }
            var owner = ReadLine(BepInExFlagFileName);
            return owner == null || SameFolder(owner, gameDir);
        }

        /// <summary>Deletes every leftover. Best effort: returns the paths that could not be removed.</summary>
        public IReadOnlyList<string> RemoveLeftovers()
        {
            var failed = new List<string>();
            foreach (var f in FileNames)
            {
                var path = Path.Combine(Root, f);
                if (!FileOps.TryDelete(path)) failed.Add(path);
            }
            var payload = Path.Combine(Root, PayloadDirName);
            try { if (Directory.Exists(payload)) Directory.Delete(payload, true); }
            catch (Exception) { failed.Add(payload); }
            return failed;
        }

        /// <summary>The command line of 1.0's hidden PowerShell watchdog (from WMI Win32_Process).</summary>
        public static bool IsWatchdogCommandLine(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return false;
            int app = commandLine.IndexOf(AppPaths.AppId, StringComparison.OrdinalIgnoreCase);
            return app >= 0 && commandLine.IndexOf(WatchdogScriptName, app, StringComparison.OrdinalIgnoreCase) > app;
        }

        private string ReadLine(string name)
        {
            var text = FileOps.TryReadSmallText(Path.Combine(Root, name), 64 * 1024);
            if (text == null) return null;
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim().Trim('\uFEFF').Trim();
                if (t.Length > 0) return t;
            }
            return null;
        }

        internal static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
