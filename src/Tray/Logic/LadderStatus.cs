using System;
using System.Linq;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// What the status window says about the tcgkb ranking: the three steps to a row (signed in, a PTCGL
    /// account, enough matches) and why a player is not ranked yet. The rules are tcgkb's
    /// (docs/TCGKB-API.md, "PTCGL accounts"): only accounts a user owns count, merged into one row, and
    /// a player needs MinGames matches before Elo ranks them; until then they are listed as "Not ranked yet".
    /// </summary>
    public sealed class LadderStatus
    {
        public bool HasAccount;
        public bool Ranked;
        /// <summary>A short line for the home page, e.g. "Ladder: 3 of 5 matches".</summary>
        public string Title;
        /// <summary>Right of the title: "2 more to go", "#4 of 37".</summary>
        public string Count;
        /// <summary>The third step's line and its explanation.</summary>
        public string Step, Why;
        /// <summary>The progress bar, 0 to 1.</summary>
        public double Progress;

        /// <summary>Null until tcgkb answered (or when it is too old to send the ladder).</summary>
        public static LadderStatus From(TcgkbAccounts me)
        {
            var ladder = me?.Ladder;
            if (ladder == null) return null;
            int min = Math.Max(1, ladder.MinGames), games = Math.Max(0, ladder.Games);
            var accounts = me.Accounts ?? Enumerable.Empty<TcgkbAccount>();
            var s = new LadderStatus { HasAccount = accounts.Any(a => a.Status != TcgkbAccount.Disputed) };
            if (ladder.Rank is int rank)
            {
                s.Ranked = true;
                s.Title = "On the ladder";
                s.Count = "#" + rank + " of " + ladder.Players;
                s.Step = games + " matches played";
                s.Why = "You're ranked by Elo, over every match of all your accounts.";
                s.Progress = 1;
                return s;
            }
            int left = Math.Max(1, min - games);
            s.Title = "Ladder: " + games + " of " + min + " matches";
            s.Count = left + " more to go";
            s.Step = "Play " + min + " matches (" + games + " of " + min + ")";
            s.Why = !s.HasAccount && accounts.Any()
                ? "Another tcgkb user claims your PTCGL account too, so its matches count for no one until one of you has a verified match."
                : !s.HasAccount
                ? "Play a match with the app running: its upload adds your PTCGL account. You need " + min + " matches before the ladder ranks you."
                : "You have " + games + " of " + min + ". Until then you show up at the bottom of the ladder as \"Not ranked yet\", so a few lucky wins can't top the board.";
            s.Progress = Math.Min(1, games / (double)min);
            return s;
        }
    }
}
