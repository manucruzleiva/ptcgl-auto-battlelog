using System;
using System.Collections.Generic;
using System.IO;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Finding the game and putting the mod into it the first time, shared by the installer, the
    /// first portable run and the tray's "Choose game folder..." item.
    /// </summary>
    internal static class GameSetup
    {
        /// <summary>
        /// The game folder: the saved one, the usual places, the registry, the running game, and
        /// finally (when <paramref name="askPlayer"/>) a folder picker. Adopts what it finds; null
        /// when nothing was found or the player gave up.
        /// </summary>
        public static string Locate(Session session, bool askPlayer)
        {
            string dir = null;
            try { dir = GameFinder.Find(session.GameDir); }
            catch (Exception e) { Log.Error("Looking for the game failed", e); }

            if (dir == null && askPlayer)
            {
                Dialogs.Info(AppInfo.GameName + " was not found in its usual place.\n\nPlease select the game's folder: the one that contains \"" +
                             GameLocator.GameExe + "\".");
                dir = AskForFolder(null);
            }
            if (dir != null) Adopt(session, dir);
            else Log.Info("Game folder not found.");
            return dir;
        }

        /// <summary>A folder picker that only accepts the game's folder; null when cancelled.</summary>
        public static string AskForFolder(string initial)
        {
            while (true)
            {
                var picked = Dialogs.PickFolder("Select the " + AppInfo.GameName + " folder (the one that contains \"" + GameLocator.GameExe + "\").",
                                                initial, false);
                if (picked == null) return null;
                var dir = GameLocator.Find(picked, null, null, null) ?? GameLocator.Find(Path.Combine(picked, "Pokemon TCG Live"), null, null, null);
                if (dir != null) return dir;
                if (!Dialogs.RetryCancel("\"" + GameLocator.GameExe + "\" is not in that folder.\n\n" + picked)) return null;
                initial = picked;
            }
        }

        /// <summary>
        /// Remembers the game folder. A folder seen for the first time decides whether uninstalling
        /// may remove BepInEx: only if it was not there before us (or 1.0 recorded that it added it).
        /// </summary>
        public static void Adopt(Session session, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            var known = session.GameDir;
            if (known != null && PathUtil.Same(known, dir)) return;

            var game = new GameInstall(dir);
            bool ours = !game.HasBepInEx || new LegacyInstall(session.Paths.LocalRoot).BepInExInstalledByUs(dir);
            session.Update(s =>
            {
                s.GameDir = dir;
                s.BepInExInstalledByUs = ours;
            });
            Log.Info("Game folder: " + dir + (ours ? " (BepInEx added by us)" : " (BepInEx already there)"));
        }

        public enum RepairOutcome { Done, NothingToDo, GameRunning, NoGame, Failed, ConsentPending }

        /// <summary>
        /// Repairs now if the game is closed (Windows locks the loaded plugin, and a running game
        /// may be mid update) and the player has accepted the current disclaimer (nothing is written
        /// into the game before that). Never throws; <paramref name="error"/> says what failed.
        /// </summary>
        public static RepairOutcome TryRepair(Session session, out IReadOnlyList<InstallPiece> repaired, out string error)
        {
            repaired = new InstallPiece[0];
            error = null;
            var game = session.Game;
            if (game == null) return RepairOutcome.NoGame;
            if (session.Read(s => s.DisclaimerAccepted) < Disclaimer.Version) return RepairOutcome.ConsentPending;
            try
            {
                if (game.Missing(session.Payload).Count == 0) return RepairOutcome.NothingToDo;
                if (GameProcess.IsRunning()) return RepairOutcome.GameRunning;
                repaired = game.Repair(session.Payload);
                Log.Info("Repaired: " + string.Join(", ", repaired));
                return RepairOutcome.Done;
            }
            catch (Exception e)
            {
                Log.Error("Repair failed", e);
                error = e.Message;
                return RepairOutcome.Failed;
            }
        }

        /// <summary>The logs folder exists (the plugin would create it too, but the shortcut and the watcher need it now).</summary>
        public static string EnsureLogsFolder(Session session)
        {
            var folder = session.LogsFolder(session.Game);
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) { Log.Error("Could not create the logs folder " + folder, e); }
            return folder;
        }
    }
}
