using System;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// The logs folder as it was when a FileSystemWatcher started on it. A watcher holds the folder
    /// itself open, not its path: renamed in Explorer, or deleted to the Recycle Bin (a move), the
    /// folder is still watched where it went, with no error event, while the plugin saves new logs
    /// into a folder it makes again at the old path. <see cref="StillThere"/> notices, so the tray
    /// can start a watcher on the folder that is there now.
    /// </summary>
    public sealed class WatchedFolder
    {
        private readonly DateTime? _createdUtc;

        public WatchedFolder(string path)
        {
            Path = path;
            _createdUtc = CreatedUtc(path);
        }

        public string Path { get; }

        /// <summary>
        /// The folder at <see cref="Path"/> is the one first seen: it exists and has the same
        /// creation time (a folder made again has a new one, unless the file system cannot tell).
        /// </summary>
        public bool StillThere()
        {
            try { return Directory.Exists(Path) && CreatedUtc(Path) == _createdUtc; }
            catch (Exception) { return false; }
        }

        private static DateTime? CreatedUtc(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : (DateTime?)null; }
            catch (Exception) { return null; }
        }
    }
}
