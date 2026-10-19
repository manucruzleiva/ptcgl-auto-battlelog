using System;
using System.Collections.Generic;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// Turns the logs folder watcher's noisy events into one "battle log saved" per file. The
    /// plugin writes name.tmp, renames it, may overwrite it with a longer read and may move it to
    /// a corrected name (NO-RESULT → VICTORY): a file is announced once it has been quiet for the
    /// delay, still exists, and was not announced before. Not thread-safe: use from one thread.
    /// </summary>
    public sealed class SaveNotificationQueue
    {
        public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1.5);

        private readonly TimeSpan _delay;
        private readonly int _remember;
        private readonly Dictionary<string, DateTime> _pending = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _announced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _announcedOrder = new Queue<string>();

        public SaveNotificationQueue() : this(DefaultDelay, 1000) { }

        public SaveNotificationQueue(TimeSpan delay, int remember)
        {
            _delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            _remember = Math.Max(1, remember);
        }

        public int PendingCount => _pending.Count;

        /// <summary>
        /// A file was created or renamed into place. Ignored (false) unless its name is a battle log
        /// name (so .tmp, .seal, index.csv and anything else never notify). Restarts its delay.
        /// </summary>
        public bool Add(string path, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(path) || !LogFileName.TryParse(path, out _, out _)) return false;
            if (_announced.Contains(path)) return false;
            _pending[path] = nowUtc;
            return true;
        }

        /// <summary>Files to announce now; each is returned once, ever (up to the last 1000 remembered).</summary>
        public IReadOnlyList<string> TakeDue(DateTime nowUtc, Func<string, bool> exists)
        {
            var due = new List<string>();
            if (_pending.Count == 0) return due;
            foreach (var kv in new List<KeyValuePair<string, DateTime>>(_pending))
            {
                if (nowUtc - kv.Value < _delay) continue;
                _pending.Remove(kv.Key);
                bool present;
                try { present = exists == null ? File.Exists(kv.Key) : exists(kv.Key); }
                catch (Exception) { present = false; }
                if (!present) continue;   // moved on (renamed to its final name) or deleted
                Remember(kv.Key);
                due.Add(kv.Key);
            }
            due.Sort(StringComparer.OrdinalIgnoreCase);
            return due;
        }

        /// <summary>Forget pending events (the watched folder changed). Announced names are kept.</summary>
        public void ClearPending() => _pending.Clear();

        private void Remember(string path)
        {
            if (!_announced.Add(path)) return;
            _announcedOrder.Enqueue(path);
            while (_announcedOrder.Count > _remember) _announced.Remove(_announcedOrder.Dequeue());
        }
    }
}
