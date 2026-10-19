using System;
using System.Collections.Generic;
using System.Linq;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>Tray icon state (docs/ARCHITECTURE.md, "Daemon loop").</summary>
    public enum TrayHealth
    {
        Idle,       // no dot: healthy, game closed
        Active,     // green: the plugin's heartbeat is fresh
        Attention,  // amber: repair pending, game running without the plugin, game not found
        Error,      // red: the plugin reports an error, or a repair keeps failing
    }

    /// <summary>One-off notifications the daemon asks the UI to show.</summary>
    public enum DaemonNotice
    {
        None,
        /// <summary>Pieces are missing while the game runs: restart it once it has finished updating.</summary>
        GameNeedsRestart,
        /// <summary>The game has run for a while and the plugin never reported in.</summary>
        RunningWithoutMod,
        /// <summary>The plugin is loaded but says state "error".</summary>
        PluginError,
    }

    /// <summary>What the daemon saw on one tick. Gathering it needs Windows; judging it does not.</summary>
    public sealed class DaemonObservation
    {
        public DateTime NowUtc;
        /// <summary>Null when the game was not found.</summary>
        public string GameDir;
        public bool GameRunning;
        public IReadOnlyList<InstallPiece> Missing = new InstallPiece[0];
        /// <summary>The plugin DLL exists (so a reported Plugin piece only means "outdated").</summary>
        public bool PluginFilePresent;
        public PluginStatus Status;
        /// <summary>
        /// The player has not accepted the current disclaimer yet (a tray started at logon or after an
        /// update, after Disclaimer.Version went up): nothing is written into the game until they do.
        /// </summary>
        public bool ConsentPending;
    }

    public sealed class DaemonDecision
    {
        public TrayHealth Health;
        public string Summary;
        /// <summary>Call GameInstall.Repair now (after checking once more that the game is closed).</summary>
        public bool Repair;
        public DaemonNotice Notice;
        public bool PluginAlive;
    }

    /// <summary>
    /// The daemon's judgement, fed every 5 s: when to repair, what to say, which icon. Repairs wait
    /// one more tick after pieces go missing (a game updater may still be writing) and happen only
    /// while the game is closed and the player's consent is current; each notice is shown once per
    /// episode, not every tick.
    /// </summary>
    public sealed class DaemonMonitor
    {
        public static readonly TimeSpan RepairDelay = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan RepairRetry = TimeSpan.FromSeconds(30);

        private DateTime? _missingSince;
        private DateTime? _retryAfter;
        private DateTime? _runningSince;
        private bool _restartNoticeShown;
        private bool _noModNoticeShown;
        private int? _errorNoticePid;
        private bool _setupPending;

        public string LastRepairError { get; private set; }

        public DaemonDecision Evaluate(DaemonObservation o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            var now = o.NowUtc;
            var missing = o.GameDir != null && o.Missing != null && o.Missing.Count > 0;
            var onlyOutdatedPlugin = missing && o.PluginFilePresent && o.Missing.All(p => p == InstallPiece.Plugin);
            var alive = o.Status != null && o.Status.IsAlive(now);
            var pluginError = alive && string.Equals(o.Status.State, PluginStatus.StateError, StringComparison.OrdinalIgnoreCase);

            if (o.GameRunning) { if (_runningSince == null) _runningSince = now; }
            else { _runningSince = null; _noModNoticeShown = false; }
            var runningFor = _runningSince.HasValue ? now - _runningSince.Value : TimeSpan.Zero;

            var d = new DaemonDecision { PluginAlive = alive };

            // Repair: missing, game closed, still so one delay later, not right after a failure, and
            // never before the player has accepted the current disclaimer.
            if (missing && !o.GameRunning)
            {
                if (_missingSince == null) _missingSince = now;
                d.Repair = !o.ConsentPending && now - _missingSince.Value >= RepairDelay && (_retryAfter == null || now >= _retryAfter.Value);
            }
            else _missingSince = null;
            if (!missing) { LastRepairError = null; _retryAfter = null; _restartNoticeShown = false; _setupPending = false; }

            // Notices, most important first; one per tick.
            if (pluginError && _errorNoticePid != o.Status.ProcessId)
            {
                d.Notice = DaemonNotice.PluginError;
                _errorNoticePid = o.Status.ProcessId;
            }
            else if (missing && o.GameRunning && !onlyOutdatedPlugin && !_restartNoticeShown && !_setupPending && !o.ConsentPending)
            {
                d.Notice = DaemonNotice.GameNeedsRestart;
                _restartNoticeShown = true;
            }
            else if (o.GameRunning && !alive && !missing && o.GameDir != null && runningFor >= PluginStatus.AliveWindow && !_noModNoticeShown)
            {
                d.Notice = DaemonNotice.RunningWithoutMod;
                _noModNoticeShown = true;
            }

            // Icon and status line.
            if (o.GameDir == null) Set(d, TrayHealth.Attention, "Pokémon TCG Live not found");
            else if (missing && o.ConsentPending) Set(d, TrayHealth.Attention, "Accept the updated disclaimer to keep the mod updated");
            else if (missing && LastRepairError != null) Set(d, TrayHealth.Error, "Could not repair the mod: " + LastRepairError);
            else if (missing && !o.GameRunning)
                Set(d, TrayHealth.Attention, onlyOutdatedPlugin ? "Updating the mod..." : _setupPending ? "Installing the mod..." : "Repairing the mod...");
            else if (missing && !onlyOutdatedPlugin && _setupPending) Set(d, TrayHealth.Attention, "Close Pokémon TCG Live once to finish installing the mod");
            else if (missing && !onlyOutdatedPlugin) Set(d, TrayHealth.Attention, "Mod missing: restart the game when it finishes updating");
            else if (pluginError) Set(d, TrayHealth.Error, "Mod error: " + (string.IsNullOrWhiteSpace(o.Status.Message) ? "see the BepInEx log" : o.Status.Message.Trim()));
            else if (alive)
            {
                var starting = string.Equals(o.Status.State, PluginStatus.StateStarting, StringComparison.OrdinalIgnoreCase);
                var saved = o.Status.SavedThisSession;
                Set(d, TrayHealth.Active, (starting ? "Mod starting..." : "Saving your matches") +
                                          (saved > 0 ? " · " + saved + (saved == 1 ? " log" : " logs") + " this session" : ""));
            }
            else if (o.GameRunning && runningFor >= PluginStatus.AliveWindow) Set(d, TrayHealth.Attention, "Game running without the mod: restart it");
            else if (o.GameRunning) Set(d, TrayHealth.Idle, "Game starting...");
            else Set(d, TrayHealth.Idle, "Ready · waiting for the game");
            return d;
        }

        /// <summary>
        /// A first install (or an upgrade) that could not be finished because the game was running:
        /// until the mod is complete, the status says the game must be closed once to finish
        /// installing, and the "a game update removed it" notice is not shown (the welcome says it).
        /// </summary>
        public void SetupPending()
        {
            _setupPending = true;
        }

        /// <summary>The repair the last decision asked for threw. Retried after RepairRetry.</summary>
        public void RepairFailed(string message, DateTime nowUtc)
        {
            LastRepairError = string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Trim();
            _retryAfter = nowUtc + RepairRetry;
        }

        public void RepairSucceeded()
        {
            LastRepairError = null;
            _retryAfter = null;
            _missingSince = null;
        }

        private static void Set(DaemonDecision d, TrayHealth health, string summary)
        {
            d.Health = health;
            d.Summary = summary;
        }
    }
}
