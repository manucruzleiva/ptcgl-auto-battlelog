using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>tcgkb-uploads.json: the sealed logs already sent to tcgkb, by Key, so each is sent once.</summary>
    [DataContract]
    public sealed class LogUploadState
    {
        /// <summary>Accepted by tcgkb (new or already there).</summary>
        [DataMember(Name = "uploaded", Order = 1)] public List<string> Uploaded = new List<string>();
        /// <summary>Refused as invalid (invalid_log): sending it again would be refused again.</summary>
        [DataMember(Name = "rejected", Order = 2)] public List<string> Rejected = new List<string>();
        /// <summary>deckSha256 of the decks tcgkb has (linked to a match): later matches with one send only the seal's hash.</summary>
        [DataMember(Name = "decks", Order = 3)] public List<string> Decks = new List<string>();
    }

    /// <summary>What one LogUploader.SyncAsync did.</summary>
    public sealed class UploadRun
    {
        public int Uploaded, AlreadyThere, Rejected;
        /// <summary>Eligible logs left for a later run (the per-run limit, or a stop).</summary>
        public int Left;
        /// <summary>The run stopped at MaxPerRun with logs left: the caller may run again right away.</summary>
        public bool HitLimit;
        /// <summary>Why the run stopped early (tcgkb unreachable, busy...), for the log; null when it did not.</summary>
        public string StoppedBecause;
        /// <summary>tcgkb's answer to the newest log sent in this run, or null.</summary>
        public UploadResponse Newest;
        /// <summary>The newest tracked match of a PTCGL account that is not the player's on tcgkb, or null: an account to offer claiming.</summary>
        public SealSummary Unclaimed;
    }

    /// <summary>
    /// Sends tcgkb every tracked match of the player's claimed PTCGL accounts (docs/TCGKB-API.md,
    /// "Match uploads"): the sealed logs in the logs folder that tcgkb has not accepted yet, newest
    /// first, at most MaxPerRun per run. What was sent is remembered in tcgkb-uploads.json; a log
    /// whose upload failed for a passing reason (no network, tcgkb busy) is sent on a later run.
    /// Only accounts tcgkb lists as the player's are sent: a log of anyone else stays on this computer.
    /// </summary>
    public sealed class LogUploader
    {
        public const string StateFileName = "tcgkb-uploads.json";
        public const int MaxPerRun = 50;
        /// <summary>Keys kept in the state file: far more than a player's logs folder holds.</summary>
        private const int MaxKeys = 50_000;

        private readonly TcgkbConnection _connection;
        private readonly string _statePath;
        private readonly SemaphoreSlim _running = new SemaphoreSlim(1, 1);

        public LogUploader(TcgkbConnection connection, string stateFolder)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrEmpty(stateFolder)) throw new ArgumentException("Folder is required.", nameof(stateFolder));
            _statePath = Path.Combine(stateFolder, StateFileName);
        }

        public string StatePath => _statePath;

        /// <summary>One sealed log as tcgkb stores it: match, seat and sealing key.</summary>
        public static string Key(SealSummary s) => s.MatchId + "|" + s.PlayerAccountHash + "|" + s.KeyId;

        /// <summary>
        /// Uploads what is pending. Null when a run is already going. Throws TcgkbAuthException when
        /// the connection is gone; other tcgkb errors end the run early (UploadRun.StoppedBecause).
        /// </summary>
        public async Task<UploadRun> SyncAsync(string logsFolder, CancellationToken ct)
        {
            if (!await _running.WaitAsync(0, ct).ConfigureAwait(false)) return null;
            try
            {
                var run = new UploadRun();
                var state = LoadState();
                var done = new HashSet<string>(state.Uploaded.Concat(state.Rejected), StringComparer.Ordinal);

                TcgkbAccounts me;
                try { me = await _connection.GetAccountsAsync(ct).ConfigureAwait(false); }
                catch (TcgkbAuthException) { throw; }
                catch (TcgkbException e)
                {
                    run.StoppedBecause = e.Message;
                    return run;
                }
                var mine = new HashSet<string>(me.Accounts.Select(a => a.AccountHash), StringComparer.Ordinal);
                run.Unclaimed = NewestUnclaimed(logsFolder, mine);
                var pending = Pending(logsFolder, mine, done);

                int i = 0;
                for (; i < pending.Count; i++)
                {
                    if (i >= MaxPerRun)
                    {
                        run.HitLimit = true;
                        break;
                    }
                    var s = pending[i];
                    ClaimEvidence log;
                    bool known = s.DeckSha256 != null && state.Decks.Contains(s.DeckSha256);
                    try { log = TcgkbConnection.BuildEvidence(s, withDeck: !known); }
                    catch (IOException) { continue; }                    // moved or deleted since the scan
                    catch (UnauthorizedAccessException) { continue; }

                    try
                    {
                        var r = await _connection.UploadLogAsync(log, ct).ConfigureAwait(false);
                        if (r.Added) run.Uploaded++; else run.AlreadyThere++;
                        if (run.Newest == null) run.Newest = r;
                        state.Uploaded.Add(Key(s));
                        // Each deck is sent once: tcgkb links later matches to it by deckSha256. If it
                        // turns out not to have it any more, the next match with it sends it again.
                        if (s.DeckSha256 != null)
                        {
                            state.Decks.Remove(s.DeckSha256);
                            if (r.DeckRef != null) state.Decks.Add(s.DeckSha256);
                        }
                    }
                    catch (TcgkbAuthException) { throw; }
                    catch (TcgkbException e) when (e.Code == "invalid_log")
                    {
                        run.Rejected++;
                        state.Rejected.Add(Key(s));
                    }
                    catch (TcgkbException e) when (e.Code == "not_claimed") { }   // released since the list: not sent, kept for later
                    catch (TcgkbException e)
                    {
                        run.StoppedBecause = e.Message;
                        break;
                    }
                    finally { SaveState(state); }
                }
                run.Left = pending.Count - i;
                return run;
            }
            finally { _running.Release(); }
        }

        /// <summary>The folder's sealed logs of a tracked match of one of these accounts, not in done, newest first. Never throws.</summary>
        public static List<SealSummary> Pending(string logsFolder, ISet<string> accounts, ISet<string> done)
        {
            var found = new List<SealSummary>();
            if (accounts.Count == 0) return found;
            try
            {
                if (string.IsNullOrEmpty(logsFolder) || !Directory.Exists(logsFolder)) return found;
                foreach (var file in Directory.EnumerateFiles(logsFolder, "*.txt"))
                {
                    if (!LogFileName.TryParse(file, out _, out _)) continue;
                    var s = SealSummary.TryRead(file);
                    if (s?.PlayerAccountHash == null || !accounts.Contains(s.PlayerAccountHash)) continue;
                    if (!PtcglAccountScanner.IsEvidence(s) || done.Contains(Key(s))) continue;
                    found.Add(s);
                }
            }
            catch (Exception) { }
            return found.OrderByDescending(s => s.SealedUtc).ThenByDescending(s => s.LogFile, StringComparer.Ordinal).ToList();
        }

        /// <summary>The folder's newest sealed log of a tracked match of an account not in accounts, or null. Never throws.</summary>
        public static SealSummary NewestUnclaimed(string logsFolder, ISet<string> accounts)
        {
            SealSummary newest = null;
            try
            {
                if (string.IsNullOrEmpty(logsFolder) || !Directory.Exists(logsFolder)) return null;
                foreach (var file in Directory.EnumerateFiles(logsFolder, "*.txt"))
                {
                    if (!LogFileName.TryParse(file, out _, out _)) continue;
                    var s = SealSummary.TryRead(file);
                    if (s?.PlayerAccountHash == null || accounts.Contains(s.PlayerAccountHash) || !PtcglAccountScanner.IsEvidence(s)) continue;
                    if (newest == null || s.SealedUtc > newest.SealedUtc) newest = s;
                }
            }
            catch (Exception) { }
            return newest;
        }

        /// <summary>The saved state, or an empty one when the file is missing or unreadable (logs are then offered again: harmless).</summary>
        public LogUploadState LoadState()
        {
            try
            {
                var text = FileOps.TryReadSmallText(_statePath, 16 * 1024 * 1024);
                var state = text == null ? null : Json.Read<LogUploadState>(text);
                if (state != null)
                {
                    state.Uploaded = state.Uploaded ?? new List<string>();
                    state.Rejected = state.Rejected ?? new List<string>();
                    state.Decks = state.Decks ?? new List<string>();
                    return state;
                }
            }
            catch (Exception) { }
            return new LogUploadState();
        }

        private void SaveState(LogUploadState state)
        {
            if (state.Uploaded.Count > MaxKeys) state.Uploaded.RemoveRange(0, state.Uploaded.Count - MaxKeys);
            if (state.Rejected.Count > MaxKeys) state.Rejected.RemoveRange(0, state.Rejected.Count - MaxKeys);
            if (state.Decks.Count > MaxKeys) state.Decks.RemoveRange(0, state.Decks.Count - MaxKeys);
            try { FileOps.WriteAllTextAtomic(_statePath, Json.Write(state), FileOps.Utf8NoBom); }
            catch (Exception) { }   // not saved: the same logs are offered again next run, and tcgkb takes them again
        }
    }
}
