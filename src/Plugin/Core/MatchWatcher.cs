using System;
using System.Collections.Generic;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>Everything known about one match, for naming, indexing and sealing its log file.</summary>
    public sealed class MatchRecord
    {
        public string MatchId;
        public DateTime StartUtc;
        public DateTime? EndUtc;
        public bool? Won;           // null when the match ended without a result
        public int? Winner;         // seat of the winner, 1 or 2, when known
        public int? MySeat;         // our seat, 1 or 2, when known
        public string MyName;
        public string OpponentName;
        public string MyDeck;
        /// <summary>Our deck's cards, saved as the .deck file; null when the client gave none.</summary>
        public DeckList MyDeckList;
        public string MyAccountId;
        public string OpponentAccountId;
        /// <summary>The client's queue name (Ranked, Casual, BuildAndBattle, Friend, ...); null when unknown.</summary>
        public string Queue;
        /// <summary>The client's format name (Standard, Expanded, BuildAndBattle, ...); null when unknown.</summary>
        public string Format;
        public bool ManualOnly;     // an Export click with no match we were tracking
        /// <summary>How the text being saved was obtained: "auto" (our own read) or "export" (the player's Export click).</summary>
        public string Capture;
    }

    public interface ILogSink
    {
        /// <summary>
        /// Write (or overwrite) the log for a match. <paramref name="existingPath"/> is the file
        /// already written for this match, if any; the sink may move it when the record now names
        /// it differently (e.g. the result arrived after the first save). Returns the path written.
        /// </summary>
        string Save(MatchRecord match, string text, string existingPath);
    }

    /// <summary>
    /// Decides when to read the battle log and when to write it. No Unity, no clock of its own:
    /// call <see cref="Tick"/> a couple of times a second with the current time.
    ///
    /// The log is read from the client at the moment the match reports game over, and again a
    /// few seconds later in case the last entries (prize taken, knock out, concede) land a frame
    /// late. A longer read replaces a shorter one. If the match goes away before any of that
    /// happens (disconnect, leaving early) one last read is tried on teardown. If the same match
    /// comes back (reconnect), it continues in the same file.
    /// </summary>
    public sealed class MatchWatcher
    {
        /// <summary>Seconds after game over at which the log is (re)read.</summary>
        public static readonly double[] CaptureDelays = { 0.5, 3, 8 };

        /// <summary>How long after a match closes an Export click is still taken to be its log.</summary>
        public static readonly TimeSpan ResultsScreenWindow = TimeSpan.FromMinutes(5);

        private readonly IGameApi _api;
        private readonly ILogSink _sink;
        private readonly Action<string> _log;

        private Session _current;
        private Session _previous;
        private string _lastSavedText;
        private DateTime _lastTickUtc;

        public bool SaveManualExports = true;

        /// <summary>Matches saved since the game started.</summary>
        public int SavedCount { get; private set; }
        public string LastSavedPath { get; private set; }
        public DateTime? LastSavedUtc { get; private set; }
        /// <summary>Why the last write failed; null again after a successful one.</summary>
        public string LastError { get; private set; }

        private sealed class Session
        {
            public MatchRecord Record = new MatchRecord();
            public bool ResultKnown;
            public readonly Queue<DateTime> Due = new Queue<DateTime>();
            public string Text;
            public string Capture;  // how Text was obtained (seal metadata)
            public string Path;
            public bool Dirty;      // the record changed since it was last written
        }

        public MatchWatcher(IGameApi api, ILogSink sink, Action<string> log)
        {
            _api = api;
            _sink = sink;
            _log = log ?? (_ => { });
            _api.ManualExport += OnManualExport;
        }

        public void Tick(DateTime nowUtc)
        {
            _lastTickUtc = nowUtc;
            var snap = _api.ReadMatch();

            if (snap == null)
            {
                if (_current != null) EndSession(nowUtc);
                return;
            }

            if (_current != null && !string.IsNullOrEmpty(snap.MatchId) &&
                !string.IsNullOrEmpty(_current.Record.MatchId) && snap.MatchId != _current.Record.MatchId)
            {
                EndSession(nowUtc);     // back-to-back matches with no gap between them
            }

            if (_current == null)
            {
                if (_previous != null && !string.IsNullOrEmpty(snap.MatchId) && snap.MatchId == _previous.Record.MatchId)
                {
                    // The match we just closed is back (reconnect, a scene reload): same session,
                    // same file, instead of a second file holding part of the same game.
                    _current = _previous;
                    _previous = null;
                    _log("Match resumed: " + snap.MatchId);
                }
                else
                {
                    _current = new Session();
                    _current.Record.StartUtc = nowUtc;
                    _log("Match started" + (string.IsNullOrEmpty(snap.MatchId) ? "." : ": " + snap.MatchId));
                }
            }

            var s = _current;
            var r = s.Record;
            if (!string.IsNullOrEmpty(snap.MatchId)) r.MatchId = snap.MatchId;
            // Until game over the latest read wins (details fill in while the match loads); after
            // it only gaps are filled, so the next match's details showing up before its id does
            // cannot relabel this one.
            bool open = !s.ResultKnown;
            s.Dirty |= Take(ref r.MyName, snap.MyName, open);
            s.Dirty |= Take(ref r.OpponentName, snap.OpponentName, open);
            s.Dirty |= Take(ref r.MyDeck, snap.MyDeck, open);
            if (snap.MyDeckList != null && !DeckList.Same(r.MyDeckList, snap.MyDeckList) && (open || r.MyDeckList == null))
            {
                r.MyDeckList = snap.MyDeckList;
                s.Dirty = true;
            }
            s.Dirty |= Take(ref r.MyAccountId, snap.MyAccountId, open);
            s.Dirty |= Take(ref r.OpponentAccountId, snap.OpponentAccountId, open);
            s.Dirty |= Take(ref r.Queue, snap.Queue, open);
            s.Dirty |= Take(ref r.Format, snap.Format, open);
            if (snap.MySeat.HasValue && r.MySeat != snap.MySeat && (open || r.MySeat == null))
            {
                r.MySeat = snap.MySeat;
                s.Dirty = true;
            }

            if (snap.GameOver)
            {
                // The winner can land a few frames after gameOver: take it whenever it shows up.
                if (r.Won == null && (snap.Winner == 1 || snap.Winner == 2))
                {
                    r.Won = (snap.Winner == 1) == snap.IAmPlayer1;
                    r.Winner = snap.Winner;
                    s.Dirty = true;
                }
                if (!s.ResultKnown)
                {
                    s.ResultKnown = true;
                    r.EndUtc = nowUtc;
                    foreach (var d in CaptureDelays) s.Due.Enqueue(nowUtc.AddSeconds(d));
                    _log("Game over (" + (r.Won == true ? "won" : r.Won == false ? "lost" : "no result yet") + "); saving battle log.");
                }
            }

            while (s.Due.Count > 0 && s.Due.Peek() <= nowUtc)
            {
                s.Due.Dequeue();
                Capture(s, nowUtc);
            }

            // Already saved under what we knew then: rewrite it under what we know now.
            if (s.Dirty && s.Text != null) Write(s, s.Text, s.Capture, nowUtc);
        }

        private void EndSession(DateTime nowUtc)
        {
            var s = _current;
            _current = null;
            // Without a game over, the end is now (for a resumed match: the latest teardown).
            if (!s.ResultKnown && s.Record.EndUtc != nowUtc) { s.Record.EndUtc = nowUtc; s.Dirty = true; }
            // Pending reads never ran, or the match ended without a game over: last chance.
            if (s.Due.Count > 0 || s.Text == null || !s.ResultKnown) Capture(s, nowUtc);
            if (s.Dirty && s.Text != null) Write(s, s.Text, s.Capture, nowUtc);
            s.Due.Clear();
            _previous = s;
            _log("Match closed" + (s.Path == null ? " - no battle log was available." : "."));
        }

        private void Capture(Session s, DateTime nowUtc)
        {
            string text;
            try { text = _api.ExportBattleLog(); }
            catch (Exception e) { _log("Battle log read failed: " + e.Message); return; }
            Offer(s, text, LogSealer.CaptureAuto, nowUtc);
        }

        /// <summary>Keep the text if it is the longest seen for this match.</summary>
        private void Offer(Session s, string text, string capture, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (s.Text != null && text.Length <= s.Text.Length) return;
            // A brand-new session being handed the previous match's log (left in memory by the
            // client) would save the same game twice.
            if (s.Text == null && text == _lastSavedText) return;
            Write(s, text, capture, nowUtc);
        }

        private void Write(Session s, string text, string capture, DateTime nowUtc)
        {
            // Cleared up front: a record that cannot be written is retried with the next read,
            // not on every tick.
            s.Dirty = false;
            try
            {
                s.Record.Capture = capture;
                var path = _sink.Save(s.Record, text, s.Path);
                if (s.Text == null) SavedCount++;
                else if (path != s.Path) _log("Battle log renamed: " + s.Path + " -> " + path);
                s.Path = path;
                s.Text = text;
                s.Capture = capture;
                _lastSavedText = text;
                LastSavedPath = path;
                LastSavedUtc = nowUtc;
                LastError = null;
                _log("Battle log saved (" + text.Length + " chars): " + path);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                _log("Could not write the battle log: " + e.Message);
            }
        }

        /// <summary>The player pressed Export: same text, so treat it as one more read.</summary>
        private void OnManualExport(string text)
        {
            if (!SaveManualExports || string.IsNullOrEmpty(text)) return;
            var now = _lastTickUtc == default(DateTime) ? DateTime.UtcNow : _lastTickUtc;
            if (_current != null) { Offer(_current, text, LogSealer.CaptureExport, now); return; }
            var p = _previous;
            bool recent = p != null && p.Record.EndUtc.HasValue && now - p.Record.EndUtc.Value <= ResultsScreenWindow;
            if (recent && (p.Text == null || text.StartsWith(Head(p.Text), StringComparison.Ordinal)))
            {
                Offer(p, text, LogSealer.CaptureExport, now);    // the results screen, after the match itself was torn down
                return;
            }
            if (text == _lastSavedText) return;
            // Opened from somewhere we did not see as a match (e.g. an old log): its own file.
            var orphan = new Session();
            orphan.Record.StartUtc = now;
            orphan.Record.ManualOnly = true;
            Offer(orphan, text, LogSealer.CaptureExport, now);
        }

        /// <summary>The first lines of a log identify its match well enough.</summary>
        private static string Head(string text)
        {
            return text.Length <= 200 ? text : text.Substring(0, 200);
        }

        /// <summary>Copies a non-empty value into <paramref name="field"/>; true when it changed.</summary>
        private static bool Take(ref string field, string value, bool overwrite)
        {
            if (string.IsNullOrEmpty(value) || value == field) return false;
            if (!overwrite && !string.IsNullOrEmpty(field)) return false;
            field = value;
            return true;
        }
    }
}
