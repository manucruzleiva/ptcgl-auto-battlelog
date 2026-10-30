using System;
using System.Threading;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;
using Timer = System.Windows.Forms.Timer;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>The "tcgkb account" submenu (docs/ARCHITECTURE.md, "tcgkb account").</summary>
    internal sealed partial class TrayApplicationContext
    {
        /// <summary>Uploads that failed for a passing reason (offline, tcgkb busy) are retried this often.</summary>
        private static readonly TimeSpan UploadRetryInterval = TimeSpan.FromMinutes(30);

        private TcgkbService _tcgkb;
        private LogUploader _uploader;
        private Timer _uploadTimer;
        private string _lastUploadLabel;
        private bool _tcgkbConsentLogged;
        private ToolStripMenuItem _tcgkbItem, _tcgkbInfoItem, _tcgkbConnectItem, _tcgkbAutoUploadItem,
                                  _tcgkbUploadNowItem, _tcgkbLastUploadItem, _tcgkbDisconnectItem;

        private ToolStripMenuItem BuildTcgkbMenu()
        {
            _tcgkbItem = new ToolStripMenuItem("tcgkb account");
            _tcgkbInfoItem = Info("Not connected");
            _tcgkbConnectItem = Item("Sign in to tcgkb...", ConnectTcgkb);
            _tcgkbAutoUploadItem = Toggle("Upload my matches automatically", ToggleAutoUpload);
            _tcgkbUploadNowItem = Item("Upload matches now", () => UploadMatches(true));
            _tcgkbLastUploadItem = Info("");
            _tcgkbDisconnectItem = Item("Disconnect", DisconnectTcgkb);
            _tcgkbItem.DropDownItems.AddRange(new ToolStripItem[]
            {
                _tcgkbInfoItem, _tcgkbConnectItem, new ToolStripSeparator(),
                _tcgkbAutoUploadItem, _tcgkbUploadNowItem, _tcgkbLastUploadItem, new ToolStripSeparator(), _tcgkbDisconnectItem,
            });
            return _tcgkbItem;
        }

        private void RefreshTcgkbItems()
        {
            var user = Tcgkb.Connection.UserName;
            _tcgkbInfoItem.Text = MenuText(user == null ? "Not connected" : Clip("Connected as " + user, 60));
            _tcgkbConnectItem.Visible = user == null;
            _tcgkbDisconnectItem.Visible = _tcgkbAutoUploadItem.Visible = user != null;
            var auto = AutoUpload;
            _tcgkbAutoUploadItem.Checked = auto;
            _tcgkbUploadNowItem.Visible = user != null && auto;
            var waiting = !HasConsent();
            _tcgkbLastUploadItem.Text = MenuText(waiting ? "Waiting: accept the updated disclaimer" : Clip("Last upload: " + _lastUploadLabel, 90));
            _tcgkbLastUploadItem.Visible = user != null && auto && (waiting || _lastUploadLabel != null);
        }

        /// <summary>Made on first use: a tray that is never connected sends nothing to tcgkb.</summary>
        private TcgkbService Tcgkb => _tcgkb ?? (_tcgkb = new TcgkbService(_session));

        /// <summary>Uploads are on unless the player turned them off (null: never chosen, which means on).</summary>
        private bool AutoUpload => _session.Read(s => s.TcgkbAutoUpload) != false;

        /// <summary>
        /// Sign-in is the only step: every PTCGL account in this computer's sealed logs is taken as the
        /// player's (tcgkb claims it with the first upload) and uploads are on by default.
        /// </summary>
        private void ConnectTcgkb()
        {
            using (Modal())
            {
                if (!Dialogs.Confirm("Sign in to tcgkb.app to upload your matches?\n\n" +
                                     "Your browser opens tcgkb.app, where you sign in and approve this computer. The app never sees your password.\n\n" +
                                     "From then on, after each match the app sends tcgkb the sealed battle log and your decklist; the matches already in " +
                                     "your logs folder go too. Every PTCGL account that plays on this computer is added to your tcgkb account. The logs " +
                                     "contain both players' names, your deck and every play of the match. You can turn uploads off or disconnect any time " +
                                     "in the tcgkb account menu.", MessageBoxIcon.Question)) return;
                var user = TcgkbConnectDialog.Run(Tcgkb.Connection);
                if (user == null) return;
                Log.Info("Signed in to tcgkb; automatic match upload " + (AutoUpload ? "on." : "off."));
            }
            UploadMatches(true);
        }

        /// <summary>
        /// Not signed in: right after a first install the sign-in opens by itself; a player who installed
        /// before sign-in was the only step (or never finished it) is offered it once, with a balloon.
        /// </summary>
        private void OfferSignIn(bool firstRun)
        {
            if (Tcgkb.Connection.IsConnected) return;
            if (firstRun) { Post(ConnectTcgkb); return; }
            if (_session.Read(s => s.TcgkbSignInOffered)) return;
            _session.Update(s => s.TcgkbSignInOffered = true);
            Balloon("Sign in to tcgkb", "Sign in once and your matches go to tcgkb.app by themselves: stats, meta and leaderboard. Click to sign in.",
                    ToolTipIcon.Info, ConnectTcgkb);
        }

        // ---- Match uploads (docs/TCGKB-API.md, "Match uploads") --------------------------------

        private LogUploader Uploader => _uploader ?? (_uploader = new LogUploader(Tcgkb.Connection, _session.Paths.Root));

        private void ToggleAutoUpload()
        {
            var on = !AutoUpload;
            _session.Update(s => s.TcgkbAutoUpload = on);
            Log.Info("Automatic match upload to tcgkb turned " + (on ? "on." : "off."));
            if (on) UploadMatches(true);
        }

        /// <summary>
        /// Starts the retry timer and uploads what is pending, when the player turned uploads on. Called
        /// at startup; never makes a request for a player who did not.
        /// </summary>
        private void StartUploads()
        {
            _uploadTimer = new Timer { Interval = (int)UploadRetryInterval.TotalMilliseconds };
            _uploadTimer.Tick += (s, e) => UploadMatches(false);
            _uploadTimer.Start();
            UploadMatches(false);
        }

        /// <summary>
        /// Sends the pending logs. announce: say what was sent (the player asked for it, or just turned
        /// uploads on); otherwise only the log file hears about it, so a saved match shows one notification.
        /// </summary>
        private async void UploadMatches(bool announce)
        {
            if (_exiting || !AutoUpload || !Tcgkb.Connection.IsConnected) return;
            // The uploads carry the decklist, which the current disclaimer covers: nothing goes before it is accepted.
            if (!HasConsent())
            {
                if (announce) AskConsent();   // the player asked for an upload: ask now
                if (!HasConsent())
                {
                    if (!_tcgkbConsentLogged) Log.Info("tcgkb uploads wait until the updated disclaimer is accepted.");
                    _tcgkbConsentLogged = true;
                    return;
                }
            }
            UploadRun run;
            try { run = await Uploader.SyncAsync(_session.LogsFolder(_session.Game), CancellationToken.None); }
            catch (TcgkbAuthException)
            {
                Log.Info("tcgkb no longer accepts this connection; match uploads stop until the player connects again.");
                Balloon("Sign in to tcgkb again", "tcgkb no longer accepts this computer's connection, so your matches are not uploaded. Click to sign in.",
                        ToolTipIcon.Warning, ConnectTcgkb);
                return;
            }
            catch (Exception e)
            {
                Log.Error("Uploading matches to tcgkb failed", e);
                return;
            }
            if (run == null) return;   // a run was already going

            var sent = run.Uploaded + run.AlreadyThere;
            if (sent > 0 || run.Rejected > 0 || run.StoppedBecause != null)
                Log.Info("tcgkb uploads: " + run.Uploaded + " new, " + run.AlreadyThere + " already there, " + run.Rejected + " refused, " +
                         run.Left + " left" + (run.HitLimit ? " (next run follows)" : "") + (run.StoppedBecause != null ? " (" + run.StoppedBecause + ")" : "") + ".");
            if (run.Newest != null)
            {
                var decks = run.Newest.PlayerDeck != null && run.Newest.OpponentDeck != null
                    ? run.Newest.PlayerDeck + " vs " + run.Newest.OpponentDeck + ", " : "";
                _lastUploadLabel = decks + DateTime.Now.ToString("t");
            }
            if (announce)
            {
                if (sent > 0)
                    Balloon("Matches uploaded to tcgkb",
                            run.Uploaded + (run.Uploaded == 1 ? " new match" : " new matches") + " sent" +
                            (run.Left > 0 ? "; " + run.Left + " more follow over the next runs." : "."),
                            ToolTipIcon.Info, null);
                else if (run.StoppedBecause != null && run.Left > 0)
                    Balloon("Matches not uploaded yet", run.StoppedBecause + " The app tries again later.", ToolTipIcon.Warning, null);
                else
                    Balloon("tcgkb is up to date", "Every match is on tcgkb.", ToolTipIcon.Info, null);
            }
            // The per-run limit: carry on right away rather than in 30 minutes.
            if (run.HitLimit) Post(() => UploadMatches(false));
        }

        /// <summary>The disclaimer was accepted: matches that were waiting for it go now (after the caller's own run, if any).</summary>
        private void OnConsentForTcgkb()
        {
            _tcgkbConsentLogged = false;
            Post(() => UploadMatches(false));
            OfferSignIn(false);
        }

        private async void DisconnectTcgkb()
        {
            bool confirmed;
            using (Modal()) confirmed = Dialogs.Confirm("Disconnect from tcgkb? Your PTCGL accounts and matches stay on tcgkb.", MessageBoxIcon.Question);
            if (!confirmed) return;
            try
            {
                var revoked = await Tcgkb.Connection.DisconnectAsync(CancellationToken.None);
                Log.Info("Disconnected from tcgkb" + (revoked ? "." : " (tcgkb not reached; the connection was forgotten here)."));
                Balloon("Disconnected from tcgkb",
                        revoked ? "This computer is no longer connected." : "Forgotten on this computer. tcgkb could not be reached: remove this computer in your tcgkb profile.",
                        revoked ? ToolTipIcon.Info : ToolTipIcon.Warning, null);
            }
            catch (Exception e) { Log.Error("Disconnect from tcgkb failed", e); }
        }
    }
}
