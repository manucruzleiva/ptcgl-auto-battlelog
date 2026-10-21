using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>A PTCGL account that sealed logs in the logs folder name as the player (the seal's "player").</summary>
    public sealed class DetectedAccount
    {
        public string AccountHash;
        /// <summary>The display name in the newest seal: PTCGL names can change, the account hash does not.</summary>
        public string Name;
        public int Matches;
        public DateTime LastSealedUtc;
        /// <summary>The newest logs that can go to tcgkb as claim evidence, newest first (at most MaxEvidence).</summary>
        public List<SealSummary> Evidence = new List<SealSummary>();
    }

    /// <summary>
    /// Groups the sealed battle logs of a folder by the player's accountHash, so the tray can offer each
    /// PTCGL account that played on this computer for a tcgkb claim (docs/TCGKB-API.md). Only the
    /// battle log names the plugin writes are read; a log without a seal or without the player's
    /// account hash is skipped.
    /// </summary>
    public static class PtcglAccountScanner
    {
        public const int MaxEvidence = 3;
        /// <summary>A battle log is a few dozen KB; a bigger file is never sent as evidence.</summary>
        public const long MaxEvidenceBytes = 1024 * 1024;

        /// <summary>Accounts, most matches first. Empty when the folder is missing or unreadable. Never throws.</summary>
        public static List<DetectedAccount> Scan(string logsFolder)
        {
            var seals = new List<SealSummary>();
            try
            {
                if (string.IsNullOrEmpty(logsFolder) || !Directory.Exists(logsFolder)) return new List<DetectedAccount>();
                foreach (var file in Directory.EnumerateFiles(logsFolder, "*.txt"))
                {
                    if (!LogFileName.TryParse(file, out _, out _)) continue;
                    var seal = SealSummary.TryRead(file);
                    if (seal?.PlayerAccountHash != null) seals.Add(seal);
                }
            }
            catch (Exception) { }

            return seals
                .GroupBy(s => s.PlayerAccountHash, StringComparer.Ordinal)
                .Select(g =>
                {
                    var newest = g.OrderByDescending(s => s.SealedUtc).ThenByDescending(s => s.LogFile, StringComparer.Ordinal).ToList();
                    return new DetectedAccount
                    {
                        AccountHash = g.Key,
                        Name = newest.Select(s => s.PlayerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                        Matches = newest.Count,
                        LastSealedUtc = newest[0].SealedUtc,
                        Evidence = newest.Where(IsEvidence).Take(MaxEvidence).ToList(),
                    };
                })
                .OrderByDescending(a => a.Matches)
                .ThenByDescending(a => a.LastSealedUtc)
                .ToList();
        }

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
