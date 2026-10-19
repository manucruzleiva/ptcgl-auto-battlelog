using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>What the UI needs after each daemon tick.</summary>
    internal sealed class DaemonReport
    {
        public DaemonDecision Decision;
        public string GameDir;
        public bool GameRunning;
        public IReadOnlyList<InstallPiece> Missing;
        public PluginStatus Status;
        /// <summary>Where new logs will appear: the running plugin's folder, else the configured one.</summary>
        public string WatchFolder;
        /// <summary>Pieces the last repair put back on this tick (empty if none).</summary>
        public IReadOnlyList<InstallPiece> Repaired = new InstallPiece[0];
    }

    /// <summary>
    /// The repair daemon (docs/ARCHITECTURE.md, "Daemon loop"): every 5 s on a background thread,
    /// looks at the game folder, the game process and the plugin's heartbeat, repairs while the game
    /// is closed, and reports to the UI. The decisions themselves are DaemonMonitor's.
    /// </summary>
    internal sealed class Daemon : IDisposable
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FindInterval = TimeSpan.FromSeconds(60);

        private readonly Session _session;
        private readonly DaemonMonitor _monitor = new DaemonMonitor();
        private readonly object _repairGate = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Task _loop;
        private DateTime _lastFindUtc = DateTime.MinValue;
        private string _lastError;

        public Daemon(Session session)
        {
            _session = session;
        }

        /// <summary>Raised on the daemon thread; handlers marshal to the UI themselves.</summary>
        public event Action<DaemonReport> Report;

        public void Start()
        {
            // Its own thread: the loop blocks for good, which a thread-pool thread should not.
            if (_loop == null) _loop = Task.Factory.StartNew(() => Run(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Runs a tick now instead of in up to 5 s (after a setting changed).</summary>
        public void Poke()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void Run(CancellationToken ct)
        {
            Log.Info("Daemon started.");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Tick();
                    _lastError = null;
                }
                catch (Exception e)
                {
                    // Never let one bad tick stop the daemon; log each distinct failure once.
                    if (e.Message != _lastError) Log.Error("Daemon tick failed", e);
                    _lastError = e.Message;
                }
                try { if (WaitHandle.WaitAny(new[] { ct.WaitHandle, _wake }, Interval) == 0) break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            var game = _session.Game;
            if (game == null && now - _lastFindUtc >= FindInterval)
            {
                // The registry walk is not free: when the game is missing, look again once a minute.
                _lastFindUtc = now;
                var found = GameFinder.Find(_session.GameDir);
                if (found != null)
                {
                    GameSetup.Adopt(_session, found);
                    game = _session.Game;
                }
            }

            var report = new DaemonReport { GameDir = game?.GameDir, GameRunning = GameProcess.IsRunning() };
            var observation = new DaemonObservation
            {
                NowUtc = now,
                GameDir = report.GameDir,
                GameRunning = report.GameRunning,
                // After a disclaimer update the player has not accepted yet: the mod is not repaired or updated.
                ConsentPending = _session.Read(s => s.DisclaimerAccepted) < Disclaimer.Version,
            };
            if (game != null)
            {
                observation.Missing = game.Missing(_session.Payload);
                observation.PluginFilePresent = File.Exists(game.PluginPath);
            }
            observation.Status = report.Status = PluginStatus.TryRead(_session.Paths.PluginStatusFile);

            DaemonDecision decision;
            lock (_repairGate)   // the monitor is shared with RepairNow
            {
                decision = _monitor.Evaluate(observation);
                if (decision.Repair && game != null && !GameProcess.IsRunning())   // once more, right before touching the folder
                {
                    try
                    {
                        report.Repaired = game.Repair(_session.Payload);
                        _monitor.RepairSucceeded();
                        Log.Info("Repaired: " + string.Join(", ", report.Repaired));
                    }
                    catch (Exception e)
                    {
                        if (e.Message != _monitor.LastRepairError) Log.Error("Repair failed", e);
                        _monitor.RepairFailed(e.Message, now);
                    }
                    observation.Missing = game.Missing(_session.Payload);
                    observation.PluginFilePresent = File.Exists(game.PluginPath);
                    var after = _monitor.Evaluate(observation);
                    if (after.Notice == DaemonNotice.None) after.Notice = decision.Notice;   // keep what the first look decided to say
                    decision = after;
                }
            }

            report.Decision = decision;
            report.Missing = observation.Missing;
            report.WatchFolder = decision.PluginAlive && IsAbsolute(report.Status?.LogsFolder)
                ? report.Status.LogsFolder
                : _session.LogsFolder(game);
            Report?.Invoke(report);
        }

        private static bool IsAbsolute(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Path.GetFullPath(path) != null; }
            catch (Exception) { return false; }
        }

        /// <summary>The first install waits for the game to close (DaemonMonitor.SetupPending). Call before Start.</summary>
        public void SetupPending()
        {
            lock (_repairGate) _monitor.SetupPending();
        }

        /// <summary>"Repair now" from the menu: same rules (only with the game closed), run on the caller's thread.</summary>
        public GameSetup.RepairOutcome RepairNow(out IReadOnlyList<InstallPiece> repaired, out string error)
        {
            lock (_repairGate)
            {
                var outcome = GameSetup.TryRepair(_session, out repaired, out error);
                if (outcome == GameSetup.RepairOutcome.Done) _monitor.RepairSucceeded();
                Poke();
                return outcome;
            }
        }

        /// <summary>Stops the loop and waits briefly for a tick in progress (a repair finishes its file).</summary>
        public void Stop()
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(10)); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
            _wake.Dispose();
        }
    }
}
