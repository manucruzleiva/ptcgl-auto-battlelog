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
        private ToolStripMenuItem _tcgkbItem, _tcgkbInfoItem, _tcgkbConnectItem, _tcgkbAccountsItem, _tcgkbAutoUploadItem,
                                  _tcgkbUploadNowItem, _tcgkbLastUploadItem, _tcgkbDisconnectItem;

        private ToolStripMenuItem BuildTcgkbMenu()
        {
            _tcgkbItem = new ToolStripMenuItem("tcgkb account");
            _tcgkbInfoItem = Info("Not connected");
            _tcgkbConnectItem = Item("Connect to tcgkb...", ConnectTcgkb);
            _tcgkbAccountsItem = Item("My PTCGL accounts...", ShowTcgkbAccounts);
            _tcgkbAutoUploadItem = Item("Upload my matches automatically", ToggleAutoUpload);
            _tcgkbUploadNowItem = Item("Upload matches now", () => UploadMatches(true));
            _tcgkbLastUploadItem = Info("");
            _tcgkbDisconnectItem = Item("Disconnect", DisconnectTcgkb);
            _tcgkbItem.DropDownItems.AddRange(new ToolStripItem[]
            {
                _tcgkbInfoItem, _tcgkbConnectItem, _tcgkbAccountsItem, new ToolStripSeparator(),
                _tcgkbAutoUploadItem, _tcgkbUploadNowItem, _tcgkbLastUploadItem, new ToolStripSeparator(), _tcgkbDisconnectItem,
            });
            return _tcgkbItem;
        }

        private void RefreshTcgkbItems()
        {
            var user = Tcgkb.Connection.UserName;
            _tcgkbInfoItem.Text = MenuText(user == null ? "Not connected" : Clip("Connected as " + user, 60));
            _tcgkbConnectItem.Visible = user == null;
            _tcgkbAccountsItem.Visible = _tcgkbDisconnectItem.Visible = _tcgkbAutoUploadItem.Visible = user != null;
            var auto = _session.Read(s => s.TcgkbAutoUpload) == true;
            _tcgkbAutoUploadItem.Checked = auto;
            _tcgkbUploadNowItem.Visible = user != null && auto;
            _tcgkbLastUploadItem.Text = MenuText(Clip("Last upload: " + _lastUploadLabel, 90));
            _tcgkbLastUploadItem.Visible = user != null && auto && _lastUploadLabel != null;
        }

        /// <summary>Made on first use: a tray that is never connected sends nothing to tcgkb.</summary>
        private TcgkbService Tcgkb => _tcgkb ?? (_tcgkb = new TcgkbService(_session));

        private void ConnectTcgkb()
        {
            using (Modal())
            {
                if (!Dialogs.Confirm("Connect " + AppInfo.ProductName + " to your tcgkb.app account?\n\n" +
                                     "Your browser opens tcgkb.app, where you sign in and approve this computer. The app never sees your password.\n\n" +
                                     "Once connected, you can claim the PTCGL accounts you play with: the app sends tcgkb the account's hash and " +
                                     "its newest battle logs (which contain your opponents' names) as proof. Nothing is sent without you choosing it, " +
                                     "and Disconnect ends the connection.", MessageBoxIcon.Question)) return;
                var user = TcgkbConnectDialog.Run(Tcgkb.Connection);
                if (user == null) return;
                Balloon("Connected to tcgkb", "Signed in as " + user + ". Now claim your PTCGL accounts.", ToolTipIcon.Info, ShowTcgkbAccounts);
            }
            ShowTcgkbAccounts();
        }

        private void ShowTcgkbAccounts()
        {
            if (!Tcgkb.Connection.IsConnected) { ConnectTcgkb(); return; }
            using (Modal()) TcgkbAccountsDialog.Show(Tcgkb.Connection, _session.LogsFolder(_session.Game));
            if (_session.Read(s => s.TcgkbAutoUpload) == null) AskAutoUpload();
            else UploadMatches(true);
        }

        // ---- Match uploads (docs/TCGKB-API.md, "Match uploads") --------------------------------

        private LogUploader Uploader => _uploader ?? (_uploader = new LogUploader(Tcgkb.Connection, _session.Paths.Root));

        /// <summary>Asked once, after the accounts window: uploads are off until the player says yes.</summary>
        private void AskAutoUpload()
        {
            bool yes;
            using (Modal())
                yes = Dialogs.Confirm("Upload your matches to tcgkb automatically?\n\n" +
                                      "After each match, the app sends tcgkb.app the sealed battle log and your decklist, if the player is one of the PTCGL accounts " +
                                      "you claimed. The matches already in your logs folder are sent too. tcgkb uses them for your stats, the meta " +
                                      "and the leaderboard, and keeps your decks there. The logs contain both players' names, your deck and every play of the match.\n\n" +
                                      "You can turn this off any time in the tcgkb account menu.", MessageBoxIcon.Question);
            _session.Update(s => s.TcgkbAutoUpload = yes);
            Log.Info("Automatic match upload to tcgkb " + (yes ? "turned on." : "declined."));
            if (yes) UploadMatches(true);
        }

        private void ToggleAutoUpload()
        {
            if (_session.Read(s => s.TcgkbAutoUpload) != true) { AskAutoUpload(); return; }
            _session.Update(s => s.TcgkbAutoUpload = false);
            Log.Info("Automatic match upload to tcgkb turned off.");
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
            if (_exiting || _session.Read(s => s.TcgkbAutoUpload) != true || !Tcgkb.Connection.IsConnected) return;
            UploadRun run;
            try { run = await Uploader.SyncAsync(_session.LogsFolder(_session.Game), CancellationToken.None); }
            catch (TcgkbAuthException)
            {
                Log.Info("tcgkb no longer accepts this connection; match uploads stop until the player connects again.");
                Balloon("Connect to tcgkb again", "tcgkb no longer accepts this computer's connection, so your matches are not uploaded. Click to connect.",
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
                    Balloon("tcgkb is up to date", "Every match of your claimed accounts is on tcgkb.", ToolTipIcon.Info, null);
            }
            // The per-run limit: carry on right away rather than in 30 minutes.
            if (run.HitLimit) Post(() => UploadMatches(false));
        }

        private async void DisconnectTcgkb()
        {
            bool confirmed;
            using (Modal()) confirmed = Dialogs.Confirm("Disconnect from tcgkb? Your claimed PTCGL accounts stay yours on tcgkb.", MessageBoxIcon.Question);
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
