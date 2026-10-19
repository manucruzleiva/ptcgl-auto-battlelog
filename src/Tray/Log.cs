using System;
using System.Collections.Generic;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The app-wide tray.log. Until the player has accepted the disclaimer nothing may be written
    /// ("Cancel exits without changing anything"), so lines are held in memory and only reach the
    /// file on Commit; a crash before that still writes them, a Cancel drops them.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static TrayLog _file;
        private static List<Action<TrayLog>> _held = new List<Action<TrayLog>>();

        /// <summary>Where lines go (once committed). Lines held so far follow to the new file.</summary>
        public static void SetFile(string path)
        {
            lock (Gate) _file = new TrayLog(path);
        }

        public static string FilePath
        {
            get { lock (Gate) return _file?.FilePath; }
        }

        public static bool Committed
        {
            get { lock (Gate) return _held == null; }
        }

        /// <summary>Writes the held lines and every line from now on.</summary>
        public static void Commit()
        {
            List<Action<TrayLog>> held;
            TrayLog file;
            lock (Gate)
            {
                if (_held == null || _file == null) return;
                held = _held;
                _held = null;
                file = _file;
            }
            foreach (var write in held) write(file);
        }

        public static void Info(string message) => Write(f => f.Info(message));

        public static void Error(string message, Exception e = null) => Write(f => f.Error(message, e));

        /// <summary>For crashes: whatever the consent state, the lines must not be lost.</summary>
        public static void Fatal(string message, Exception e)
        {
            Commit();
            Error(message, e);
        }

        private static void Write(Action<TrayLog> write)
        {
            TrayLog file;
            lock (Gate)
            {
                if (_held != null || _file == null)
                {
                    if (_held != null && _held.Count < 500) _held.Add(write);
                    return;
                }
                file = _file;
            }
            write(file);   // TrayLog never throws
        }
    }
}
