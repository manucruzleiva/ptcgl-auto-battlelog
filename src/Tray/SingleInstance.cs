using System;
using System.Threading;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// One tray per Windows session, installed or portable (named mutex), plus the two signals
    /// other launches send it: "show yourself" and "exit" (named auto-reset events, TraySignals).
    /// The mutex is owned by the thread that acquired it: create and dispose on the same thread.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private static readonly TimeSpan SignalEvery = TimeSpan.FromMilliseconds(500);

        private readonly Mutex _mutex;

        private SingleInstance(Mutex mutex, bool owned, bool accessDenied)
        {
            _mutex = mutex;
            Owned = owned;
            AccessDenied = accessDenied;
        }

        public bool Owned { get; private set; }

        /// <summary>
        /// The mutex exists but this process may not open it: a copy started with "Run as
        /// administrator" made it with an administrators-only ACL. That copy can only be stopped from
        /// its own icon (or by signing out).
        /// </summary>
        public bool AccessDenied { get; }

        /// <summary>Takes the tray mutex, waiting up to <paramref name="wait"/> for a previous instance to let go.</summary>
        public static SingleInstance Acquire(TimeSpan wait)
        {
            Mutex mutex;
            try { mutex = new Mutex(false, AppInfo.MutexName); }
            catch (UnauthorizedAccessException)
            {
                Log.Info("The running tray's mutex cannot be opened: that copy runs as administrator.");
                return new SingleInstance(null, false, true);
            }
            var instance = new SingleInstance(mutex, false, false);
            instance.TryTake(wait);
            return instance;
        }

        private bool TryTake(TimeSpan wait)
        {
            if (Owned || _mutex == null) return Owned;
            try { Owned = _mutex.WaitOne(wait); }
            catch (AbandonedMutexException) { Owned = true; }   // the previous owner crashed: ours now
            return Owned;
        }

        /// <summary>
        /// Asks a running tray to exit and takes the mutex once it has (up to <paramref name="wait"/>),
        /// so no other tray can start while an install or uninstall works. Check Owned. The request is
        /// repeated every half second: a tray that is still starting (its consent dialog open, an
        /// older version that creates its events late) may not be listening yet.
        /// </summary>
        public static SingleInstance StopRunningAndAcquire(TimeSpan wait)
        {
            var instance = Acquire(TimeSpan.Zero);
            if (instance.Owned || instance.AccessDenied) return instance;
            Log.Info("Asking the running tray to exit.");
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                Signal(AppInfo.ExitEventName);
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return instance;
                if (instance.TryTake(left < SignalEvery ? left : SignalEvery)) return instance;
            }
        }

        /// <summary>Sets a named event if some process created it. False when nobody listens.</summary>
        public static bool Signal(string eventName)
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(eventName, out var handle)) return false;
                using (handle) return handle.Set();
            }
            catch (Exception e)
            {
                Log.Error("Could not signal " + eventName, e);
                return false;
            }
        }

        public void Dispose()
        {
            if (Owned)
            {
                Owned = false;
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }   // released already, or a different thread: Windows frees it at exit
            }
            _mutex?.Dispose();
        }
    }

    /// <summary>
    /// The named events a running tray listens on. Created as soon as the tray owns the mutex,
    /// before its first dialog: an exit or show request sent while it is still starting stays set
    /// (auto-reset events keep a signal until someone waits), so it is honoured instead of lost.
    /// </summary>
    internal sealed class TraySignals : IDisposable
    {
        private TraySignals(EventWaitHandle exit, EventWaitHandle show)
        {
            Exit = exit;
            Show = show;
        }

        /// <summary>Null when the event could not be created (the tray then runs without it).</summary>
        public EventWaitHandle Exit { get; }
        public EventWaitHandle Show { get; }

        public static TraySignals Create()
        {
            return new TraySignals(Open(AppInfo.ExitEventName), Open(AppInfo.ShowEventName));
        }

        private static EventWaitHandle Open(string name)
        {
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, name); }
            catch (Exception e)
            {
                Log.Error("Could not create " + name, e);
                return null;
            }
        }

        /// <summary>Takes an exit request that arrived meanwhile: startup stops there.</summary>
        public bool ExitRequested()
        {
            try { return Exit != null && Exit.WaitOne(0); }
            catch (Exception) { return false; }
        }

        public void Dispose()
        {
            Exit?.Dispose();
            Show?.Dispose();
        }
    }

    /// <summary>
    /// Listens for one named event (owned by TraySignals) on the thread pool and runs a callback
    /// per signal. The callback must marshal to the UI thread itself.
    /// </summary>
    internal sealed class EventListener : IDisposable
    {
        private readonly RegisteredWaitHandle _registration;

        public EventListener(EventWaitHandle handle, string name, Action onSignal)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(handle, (state, timedOut) =>
            {
                try { onSignal(); }
                catch (Exception e) { Log.Error("Handling " + name + " failed", e); }
            }, null, Timeout.Infinite, false);
        }

        public void Dispose()
        {
            using (var done = new ManualResetEvent(false))
            {
                // Waits for a callback in flight, so nothing runs against a disposed tray.
                if (_registration.Unregister(done)) done.WaitOne(TimeSpan.FromSeconds(2));
            }
        }
    }
}
