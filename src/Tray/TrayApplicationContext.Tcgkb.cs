using System;
using System.Threading;
using System.Windows.Forms;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>The "tcgkb account" submenu (docs/ARCHITECTURE.md, "tcgkb account").</summary>
    internal sealed partial class TrayApplicationContext
    {
        private TcgkbService _tcgkb;
        private ToolStripMenuItem _tcgkbItem, _tcgkbInfoItem, _tcgkbConnectItem, _tcgkbAccountsItem, _tcgkbDisconnectItem;

        private ToolStripMenuItem BuildTcgkbMenu()
        {
            _tcgkbItem = new ToolStripMenuItem("tcgkb account");
            _tcgkbInfoItem = Info("Not connected");
            _tcgkbConnectItem = Item("Connect to tcgkb...", ConnectTcgkb);
            _tcgkbAccountsItem = Item("My PTCGL accounts...", ShowTcgkbAccounts);
            _tcgkbDisconnectItem = Item("Disconnect", DisconnectTcgkb);
            _tcgkbItem.DropDownItems.AddRange(new ToolStripItem[] { _tcgkbInfoItem, _tcgkbConnectItem, _tcgkbAccountsItem, _tcgkbDisconnectItem });
            return _tcgkbItem;
        }

        private void RefreshTcgkbItems()
        {
            var user = Tcgkb.Connection.UserName;
            _tcgkbInfoItem.Text = MenuText(user == null ? "Not connected" : Clip("Connected as " + user, 60));
            _tcgkbConnectItem.Visible = user == null;
            _tcgkbAccountsItem.Visible = _tcgkbDisconnectItem.Visible = user != null;
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
