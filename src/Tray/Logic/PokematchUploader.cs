using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>What happened to one battle log on Pokematch (pokematch-uploads.json).</summary>
    [DataContract]
    public sealed class PokematchUpload
    {
        public const string Uploaded = "uploaded", Rejected = "rejected", Deleted = "deleted";

        /// <summary>"match:" + the seal's match id, else "file:" + the file name.</summary>
        [DataMember(Name = "key", Order = 1)] public string Key;
        [DataMember(Name = "status", Order = 2)] public string Status;
        /// <summary>Pokematch's id for the match; null when its answer named none (then it is never updated).</summary>
        [DataMember(Name = "id", Order = 3)] public string Id;
        /// <summary>SHA-256 of the JSON last sent: when what a log would send changes (a corrected result), it is sent again, once.</summary>
        [DataMember(Name = "sha256", Order = 4)] public string Sha256;
        [DataMember(Name = "file", Order = 5)] public string File;
        [DataMember(Name = "utc", Order = 6)] public DateTime Utc;
        [DataMember(Name = "error", Order = 7, EmitDefaultValue = false)] public string Error;
    }

    [DataContract]
    public sealed class PokematchUploadState
    {
        public const string FileName = "pokematch-uploads.json";

        [DataMember(Name = "uploads", Order = 1)] public List<PokematchUpload> Uploads = new List<PokematchUpload>();

        /// <summary>Never throws: a missing or corrupt file is an empty state (Pokematch's own ids are lost, nothing else).</summary>
        public static PokematchUploadState Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = Json.Read<PokematchUploadState>(File.ReadAllText(path, FileOps.Utf8NoBom));
                    if (s != null)
                    {
                        s.Uploads = (s.Uploads ?? new List<PokematchUpload>()).Where(u => u != null && !string.IsNullOrEmpty(u.Key)).ToList();
                        return s;
                    }
                }
            }
            catch (Exception) { }
            return new PokematchUploadState();
        }

        public void Save(string path) => FileOps.WriteAllTextAtomic(path, Json.Write(this), FileOps.Utf8NoBom);

        public PokematchUpload Find(string key) => Uploads.FirstOrDefault(u => u.Key == key);
    }

    /// <summary>A battle log ready to go to Pokematch: the match as Pokematch takes it, and how it is known.</summary>
    public sealed class PokematchCandidate
    {
        public string LogFile, Key, Sha256;
        public PokematchMatch Match;
    }

    public sealed class PokematchSyncResult
    {
        public int Uploaded, Updated, Rejected, Waiting;
        /// <summary>Pokematch refused the token: nothing more is sent until the player sets a new one.</summary>
        public bool TokenRejected;
        /// <summary>The newest error worth telling the player about, or null.</summary>
        public string Error;
        /// <summary>Logs left for a later run (more than one run's worth, or Pokematch not reachable).</summary>
        public bool More;
    }

    /// <summary>
    /// Sends the plugin's battle logs to Pokematch (docs/ARCHITECTURE.md, "Pokematch"). Each run reads the
    /// logs folder, picks the finished matches (VICTORY or DEFEAT) of at least SinceUtc that the plugin
    /// has stopped writing, and adds each one Pokematch does not have yet. A match whose result the plugin
    /// corrected later updates the log it added before. pokematch-uploads.json
    /// remembers what was sent, so nothing is sent twice, and a match the player deleted on Pokematch is
    /// not sent again.
    /// </summary>
    public sealed class PokematchUploader
    {
        /// <summary>The plugin may rewrite a log shortly after the match (docs/ARCHITECTURE.md, "Logs folder").</summary>
        public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(2);
        public const long MaxLogBytes = 1024 * 1024;
        public const int MaxPerRun = 25;

        private static readonly string[] Results = { "VICTORY", "DEFEAT" };

        private readonly PokematchApi _api;
        private readonly string _stateFile;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public PokematchUploader(PokematchApi api, string stateFile, Func<DateTime> utcNow = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            if (string.IsNullOrEmpty(stateFile)) throw new ArgumentException("State file is required.", nameof(stateFile));
            _stateFile = stateFile;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public string StateFile => _stateFile;

        /// <summary>The battle logs of the folder that a run would look at: finished matches of at least sinceUtc, oldest first.</summary>
        public List<string> Eligible(string logsFolder, DateTime sinceUtc)
        {
            var found = new List<(string File, DateTime Utc)>();
            try
            {
                if (string.IsNullOrEmpty(logsFolder) || !Directory.Exists(logsFolder)) return new List<string>();
                var settled = _utcNow() - SettleTime;
                foreach (var file in Directory.EnumerateFiles(logsFolder, "*.txt"))
                {
                    if (!LogFileName.TryParse(file, out var result, out _, out var local) || Array.IndexOf(Results, result) < 0) continue;
                    var utc = local.ToUniversalTime();
                    if (utc < sinceUtc) continue;
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > settled || info.Length > MaxLogBytes) continue;
                    found.Add((file, utc));
                }
            }
            catch (Exception) { }
            return found.OrderBy(f => f.Utc).ThenBy(f => f.File, StringComparer.Ordinal).Select(f => f.File).ToList();
        }

        /// <summary>How many logs of at least sinceUtc Pokematch does not have yet (for "Upload past logs...").</summary>
        public int CountNew(string logsFolder, DateTime sinceUtc)
        {
            var state = PokematchUploadState.Load(_stateFile);
            int n = 0;
            foreach (var file in Eligible(logsFolder, sinceUtc))
            {
                var c = Read(file);
                if (c != null && state.Find(c.Key) == null) n++;
            }
            return n;
        }

        /// <summary>
        /// Sends what Pokematch is missing, at most MaxPerRun logs. One run at a time: a second caller
        /// waits. Never throws for Pokematch's answers; cancellation throws.
        /// </summary>
        public async Task<PokematchSyncResult> SyncAsync(string token, string logsFolder, DateTime sinceUtc, CancellationToken ct)
        {
            var result = new PokematchSyncResult();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var state = PokematchUploadState.Load(_stateFile);
                int sent = 0;
                foreach (var file in Eligible(logsFolder, sinceUtc))
                {
                    ct.ThrowIfCancellationRequested();
                    var c = Read(file);
                    if (c == null) continue;
                    var known = state.Find(c.Key);
                    if (known != null && (known.Status == PokematchUpload.Deleted || known.Sha256 == c.Sha256)) continue;
                    if (known != null && known.Status == PokematchUpload.Uploaded && known.Id == null) continue;   // cannot be replaced
                    if (sent >= MaxPerRun) { result.More = true; break; }
                    sent++;

                    var entry = known ?? new PokematchUpload { Key = c.Key };
                    try
                    {
                        if (known != null && known.Status == PokematchUpload.Uploaded)
                        {
                            if (await _api.UpdateAsync(token, known.Id, c.Match, ct).ConfigureAwait(false)) result.Updated++;
                            else entry.Status = PokematchUpload.Deleted;
                        }
                        else
                        {
                            entry.Id = await _api.CreateAsync(token, c.Match, ct).ConfigureAwait(false);
                            entry.Status = PokematchUpload.Uploaded;
                            result.Uploaded++;
                        }
                        entry.Error = null;
                    }
                    catch (PokematchAuthException e)
                    {
                        result.TokenRejected = true;
                        result.Error = e.Message;
                        break;
                    }
                    catch (PokematchException e) when (e.Transient)
                    {
                        result.Waiting++;
                        result.More = true;
                        result.Error = e.Message;
                        break;   // Pokematch is down or busy: the next run tries again
                    }
                    catch (PokematchException e)
                    {
                        // Refused for this log: not sent again until what it would send changes.
                        entry.Status = entry.Status ?? PokematchUpload.Rejected;
                        entry.Error = e.Message;
                        result.Rejected++;
                        result.Error = e.Message;
                    }
                    entry.Sha256 = c.Sha256;
                    entry.File = Path.GetFileName(c.LogFile);
                    entry.Utc = _utcNow();
                    if (known == null) state.Uploads.Add(entry);
                    state.Save(_stateFile);
                }
            }
            finally { _gate.Release(); }
            return result;
        }

        /// <summary>
        /// Our tag, then the queue and the format the seal names, lowercase as Pokematch keeps tags
        /// ("ranked", "standard"). Logs sealed without them send just our tag, as before.
        /// </summary>
        internal static List<string> Tags(SealSummary seal)
        {
            var tags = new List<string> { PokematchMatch.Tag };
            foreach (var t in new[] { seal?.Queue, seal?.Format })
            {
                var tag = t?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(tag) && tag.Length <= PokematchMatch.MaxTagLength && !tags.Contains(tag)) tags.Add(tag);
            }
            return tags;
        }

        /// <summary>A battle log as Pokematch takes it; null when it is not one of the plugin's logs. Never throws.</summary>
        public static PokematchCandidate Read(string logFile)
        {
            try
            {
                if (!LogFileName.TryParse(logFile, out var result, out _, out var local) || !File.Exists(logFile)) return null;
                var seal = SealSummary.TryRead(logFile);
                var ended = seal?.EndedUtc?.ToLocalTime() ?? local;
                var match = new PokematchMatch
                {
                    ActiveDeck = new PokematchDeck { Name = string.IsNullOrWhiteSpace(seal?.Deck) ? PokematchMatch.Unknown : seal.Deck.Trim() },
                    // The client does not tell the player the opponent's archetype; the player can fill it in on Pokematch.
                    OpponentDeck = PokematchMatch.Unknown,
                    Format = "Bo1",
                    Games = new List<PokematchGame> { new PokematchGame { Result = (seal?.Result ?? result) == "VICTORY" ? "win" : "loss" } },
                    Date = ended.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Notes = "Added by " + PokematchApi.Source + ".",
                    Tags = Tags(seal),
                };
                return new PokematchCandidate
                {
                    LogFile = logFile,
                    Key = string.IsNullOrEmpty(seal?.MatchId) ? "file:" + Path.GetFileName(logFile) : "match:" + seal.MatchId,
                    Match = match,
                    Sha256 = Sha256Hex(Json.Write(match)),
                };
            }
            catch (Exception) { return null; }
        }

        private static string Sha256Hex(string s)
        {
            using (var ms = new MemoryStream(FileOps.Utf8NoBom.GetBytes(s)))
                return Hashing.ToHex(Hashing.Sha256(ms));
        }
    }
}
