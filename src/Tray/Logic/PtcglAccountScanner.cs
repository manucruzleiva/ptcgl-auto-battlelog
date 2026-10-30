using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>Which sealed battle logs can go to tcgkb (docs/TCGKB-API.md).</summary>
    public static class PtcglAccountScanner
    {
        /// <summary>A battle log is a few dozen KB; a bigger file is never sent as evidence.</summary>
        public const long MaxEvidenceBytes = 1024 * 1024;

        /// <summary>A tracked match (it has a match id and a result other than EXPORTED) whose files are small enough to send.</summary>
        public static bool IsEvidence(SealSummary s)
        {
            if (string.IsNullOrEmpty(s.MatchId) || s.Result == null || s.Result == "EXPORTED" || string.IsNullOrEmpty(s.PlayerName)) return false;
            try
            {
                if (new FileInfo(s.LogFile).Length > MaxEvidenceBytes) return false;
                return s.SealFile == null || new FileInfo(s.SealFile).Length <= MaxEvidenceBytes;
            }
            catch (Exception) { return false; }
        }
    }
}
