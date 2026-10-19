using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// tray.log, the file to ask for in a bug report. Diagnostics must never take the tray down,
    /// so nothing here throws. One line per call, opened and closed each time so another process
    /// (the installer, an uninstall) can append too; rotated to tray.log.1 past maxBytes.
    /// </summary>
    public sealed class TrayLog
    {
        // Instances writing the same file share one lock, so rotation cannot race an append.
        private static readonly ConcurrentDictionary<string, object> Locks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private readonly long _maxBytes;
        private readonly object _gate;

        public TrayLog(string path, long maxBytes = 512 * 1024)
        {
            FilePath = path;
            _maxBytes = maxBytes > 0 ? maxBytes : 512 * 1024;
            _gate = Locks.GetOrAdd(LockKey(path), _ => new object());
        }

        public string FilePath { get; }

        public void Info(string message) => Write("INFO ", message);

        public void Error(string message, Exception e = null) => Write("ERROR", e == null ? message : message + ": " + e);

        private void Write(string level, string message)
        {
            try
            {
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                           " " + level + " " + message + Environment.NewLine;
                var bytes = FileOps.Utf8NoBom.GetBytes(line);
                lock (_gate)
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    RotateIfNeeded(bytes.Length);
                    using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception) { }
        }

        private void RotateIfNeeded(int incoming)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (!fi.Exists || fi.Length + incoming <= _maxBytes) return;
                var rotated = FilePath + ".1";
                if (File.Exists(rotated)) File.Delete(rotated);
                File.Move(FilePath, rotated);
            }
            catch (Exception) { }   // keep appending to the big file rather than lose the line
        }

        private static string LockKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }
    }
}
