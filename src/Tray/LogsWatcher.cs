using System;
using System.ComponentModel;
using System.IO;
using PtcglAutoBattleLog.Tray.Logic;
using Timer = System.Windows.Forms.Timer;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Watches the logs folder for new battle logs (a .txt created or renamed into place) and
    /// raises Saved once per file, after it has been quiet for a moment (SaveNotificationQueue).
    /// UI thread only: the watcher's events are marshalled through the synchronizing object.
    /// </summary>
    internal sealed class LogsWatcher : IDisposable
    {
        private readonly ISynchronizeInvoke _ui;
        private readonly SaveNotificationQueue _queue = new SaveNotificationQueue();
        private readonly Timer _timer = new Timer { Interval = 500 };
        private FileSystemWatcher _fsw;
        private WatchedFolder _watched;
        private string _failedFolder;
        private bool _disposed;

        public LogsWatcher(ISynchronizeInvoke ui)
        {
            _ui = ui;
            _timer.Tick += (s, e) => Flush();
        }

        /// <summary>A battle log was saved: full path, result ("VICTORY"...), opponent (or null).</summary>
        public event Action<string, string, string> Saved;

        public string Folder { get; private set; }

        /// <summary>
        /// Watches this folder, (re)creating the watcher when the folder changed, the watcher broke,
        /// or the folder it watches is no longer the one at that path (renamed in Explorer, or deleted
        /// to the Recycle Bin: the watcher would follow it there, silently). Cheap when nothing
        /// changed: called on every daemon report.
        /// </summary>
        public void Watch(string folder)
        {
            if (_disposed || string.IsNullOrWhiteSpace(folder)) return;
            if (_fsw != null && PathUtil.Same(folder, Folder) && _watched != null && _watched.StillThere()) return;
            if (_fsw != null && PathUtil.Same(folder, Folder)) Log.Info("The logs folder was renamed, moved or made again; watching " + folder + " afresh.");

            Stop();
            Folder = folder;
            try
            {
                Directory.CreateDirectory(folder);
                _watched = new WatchedFolder(folder);
                _fsw = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    SynchronizingObject = _ui,
                    InternalBufferSize = 32 * 1024,
                };
                _fsw.Created += (s, e) => OnFile(e.FullPath);
                _fsw.Renamed += (s, e) => OnFile(e.FullPath);
                _fsw.Error += (s, e) =>
                {
                    Log.Error("Logs folder watcher failed; restarting it", e.GetException());
                    Stop();   // the next daemon report creates a new one
                };
                _fsw.EnableRaisingEvents = true;
                Log.Info("Watching " + folder);
            }
            catch (Exception e)
            {
                // Retried on every report (the drive may come back); logged once per folder.
                if (!PathUtil.Same(_failedFolder, folder)) Log.Error("Cannot watch " + folder, e);
                _failedFolder = folder;
                Stop();
                return;
            }
            _failedFolder = null;
        }

        private void OnFile(string path)
        {
            if (_disposed) return;
            if (_queue.Add(path, DateTime.UtcNow) && !_timer.Enabled) _timer.Start();
        }

        private void Flush()
        {
            if (_disposed) return;
            foreach (var path in _queue.TakeDue(DateTime.UtcNow, File.Exists))
            {
                if (!LogFileName.TryParse(path, out var result, out var opponent)) continue;
                try { Saved?.Invoke(path, result, opponent); }
                catch (Exception e) { Log.Error("Save notification failed", e); }
            }
            if (_queue.PendingCount == 0) _timer.Stop();
        }

        private void Stop()
        {
            var fsw = _fsw;
            _fsw = null;
            _watched = null;
            if (fsw != null)
            {
                try { fsw.EnableRaisingEvents = false; } catch (Exception) { }
                fsw.Dispose();
            }
            _queue.ClearPending();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
