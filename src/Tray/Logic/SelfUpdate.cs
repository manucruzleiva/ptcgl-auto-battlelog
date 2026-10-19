using System;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// Replaces the installed exe with a verified download. Windows lets a running exe be renamed
    /// but not overwritten, hence the .old dance. Starting the new exe is the caller's job.
    /// </summary>
    public static class SelfUpdate
    {
        /// <summary>
        /// installed → installed.old (a stale .old is deleted first), new → installed. If the second
        /// step fails, the old exe is put back before the error is rethrown, so there is always an
        /// exe to start.
        /// </summary>
        public static void Swap(string newExe, string installedExe) => Swap(newExe, installedExe, File.Move);

        /// <summary>Same, with the rename injectable so tests can make it fail.</summary>
        internal static void Swap(string newExe, string installedExe, Action<string, string> move)
        {
            if (string.IsNullOrEmpty(newExe)) throw new ArgumentException("New exe path is required.", nameof(newExe));
            if (string.IsNullOrEmpty(installedExe)) throw new ArgumentException("Installed exe path is required.", nameof(installedExe));
            if (string.Equals(Path.GetFullPath(newExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The new exe is the installed exe.", nameof(newExe));
            if (!File.Exists(newExe)) throw new FileNotFoundException("The downloaded update is missing.", newExe);

            var old = installedExe + ".old";
            if (File.Exists(old)) File.Delete(old);   // fails before anything moved if it is still in use

            bool movedAside = false;
            if (File.Exists(installedExe))
            {
                move(installedExe, old);
                movedAside = true;
            }

            try
            {
                move(newExe, installedExe);
            }
            catch (Exception e)
            {
                if (movedAside) Restore(old, installedExe, e, move);
                throw;
            }
        }

        private static void Restore(string old, string installedExe, Exception cause, Action<string, string> move)
        {
            try
            {
                if (File.Exists(installedExe)) File.Delete(installedExe);   // a partial copy across volumes
                move(old, installedExe);
            }
            catch (Exception)
            {
                // A copy works where a rename does not: the user keeps a working exe either way.
                try { File.Copy(old, installedExe, true); }
                catch (Exception e2)
                {
                    throw new IOException("Update failed (" + cause.Message + ") and the previous version could not be restored from " +
                                          old + " (" + e2.Message + ").", cause);
                }
            }
        }

        /// <summary>Deletes installed.old left by the previous update. Best effort, never throws.</summary>
        public static void CleanupOld(string installedExe)
        {
            if (string.IsNullOrEmpty(installedExe)) return;
            FileOps.TryDelete(installedExe + ".old");
        }
    }
}
