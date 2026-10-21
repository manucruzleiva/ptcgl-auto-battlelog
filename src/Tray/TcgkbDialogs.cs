using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// "Connect to tcgkb...": shows the device code, opens tcgkb.app/link in the browser and waits
    /// for the player to approve it there. The app never sees the tcgkb password, and Google
    /// sign-in works the same way. Closes by itself once connected; Cancel stops waiting.
    /// </summary>
    internal sealed class TcgkbConnectDialog : Form
    {
        private readonly TcgkbConnection _connection;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly Label _status, _code;
        private readonly Button _open;
        private string _page;
        private bool _done;

        private TcgkbConnectDialog(TcgkbConnection connection)
        {
            _connection = connection;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.ProductName + " - Connect to tcgkb";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16), Dock = DockStyle.Fill };
            _status = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Text = "Asking tcgkb for a code...", Margin = new Padding(0, 0, 0, 12) };
            _code = new Label
            {
                AutoSize = true,
                Font = new Font(FontFamily.GenericMonospace, 20F, FontStyle.Bold),
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 0, 0, 12),
                Visible = false,
            };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var cancel = Button("Cancel", (s, e) => Close());
            _open = Button("Open tcgkb.app", (s, e) => OpenPage());
            _open.Enabled = false;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_open);
            CancelButton = cancel;

            layout.Controls.Add(_status);
            layout.Controls.Add(_code);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            ResumeLayout(false);
            PerformLayout();

            Shown += async (s, e) => await RunAsync();
            FormClosing += (s, e) => { if (!_done) _cancel.Cancel(); };
        }

        /// <summary>The connected tcgkb user's name, or null when cancelled (or failed: already shown and logged).</summary>
        public static string Run(TcgkbConnection connection)
        {
            using (var dlg = new TcgkbConnectDialog(connection))
            {
                dlg.ShowDialog();
                return dlg.UserName;
            }
        }

        public string UserName { get; private set; }

        private async Task RunAsync()
        {
            try
            {
                UserName = await _connection.ConnectAsync(Environment.MachineName, ShowCodeLater,
                                                          (d, c) => Task.Delay(d, c), _cancel.Token);
                Log.Info("Connected to tcgkb as " + UserName + ".");
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            catch (Exception e)
            {
                Log.Error("Connecting to tcgkb failed", e);
                if (!_cancel.IsCancellationRequested) Dialogs.Warning("Could not connect to tcgkb.\n\n" + e.Message);
            }
            _done = true;
            if (!IsDisposed) Close();
        }

        /// <summary>Called on a pool thread; the dialog may have been closed meanwhile.</summary>
        private void ShowCodeLater(DeviceAuthorization code)
        {
            try { BeginInvoke(new MethodInvoker(() => ShowCode(code))); }
            catch (InvalidOperationException) { }   // closed: the wait is being cancelled
        }

        private void ShowCode(DeviceAuthorization code)
        {
            if (IsDisposed) return;
            _page = code.VerificationUriComplete ?? code.VerificationUri;
            _code.Text = code.UserCode;
            _code.Visible = true;
            _status.Text = "Sign in to tcgkb in your browser and approve this code. It must match the code on the page:";
            _open.Enabled = true;
            OpenPage();
        }

        private void OpenPage()
        {
            if (!TcgkbApi.IsOwnPage(_page)) return;   // checked by TcgkbApi already; never opens anything else
            try { Shell.Open(_page); }
            catch (Exception e) { Log.Error("Could not open " + _page, e); }
        }

        private static Button Button(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 0), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(8, 0, 0, 0) };
            b.Click += click;
            return b;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cancel.Dispose();
                _code.Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// "My PTCGL accounts...": the PTCGL accounts whose sealed logs are in the logs folder, next to the
    /// ones the tcgkb user owns, with Claim and Release. Claiming sends that account's newest
    /// battle logs (at most 3) to tcgkb, which checks their seals.
    /// </summary>
    internal sealed class TcgkbAccountsDialog : Form
    {
        private readonly TcgkbConnection _connection;
        private readonly string _logsFolder;
        private readonly ListView _list;
        private readonly Label _status;
        private readonly Button _claim, _release;
        private List<DetectedAccount> _detected = new List<DetectedAccount>();
        private Dictionary<string, TcgkbAccount> _owned = new Dictionary<string, TcgkbAccount>();
        private bool _busy;

        private TcgkbAccountsDialog(TcgkbConnection connection, string logsFolder)
        {
            _connection = connection;
            _logsFolder = logsFolder;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.ProductName + " - My PTCGL accounts";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(560, 340);
            MinimumSize = new Size(420, 280);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _status = new Label { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 8), Text = "Loading..." };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            _list.Columns.Add("PTCGL account", 200);
            _list.Columns.Add("Matches on this PC", 130);
            _list.Columns.Add("On tcgkb", 180);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            var close = MakeButton("Close", (s, e) => Close());
            _release = MakeButton("Release", async (s, e) => await ReleaseAsync());
            _claim = MakeButton("Claim", async (s, e) => await ClaimAsync());
            buttons.Controls.Add(close);
            buttons.Controls.Add(_release);
            buttons.Controls.Add(_claim);
            CancelButton = close;

            layout.Controls.Add(_status, 0, 0);
            layout.Controls.Add(_list, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout);
            ResumeLayout(false);
            PerformLayout();
            UpdateButtons();

            Shown += async (s, e) => await LoadAsync();
        }

        public static void Show(TcgkbConnection connection, string logsFolder)
        {
            using (var dlg = new TcgkbAccountsDialog(connection, logsFolder)) dlg.ShowDialog();
        }

        private async Task LoadAsync()
        {
            if (await Busy("Loading...", async () =>
            {
                var scan = Task.Run(() => PtcglAccountScanner.Scan(_logsFolder));
                var mine = await _connection.GetAccountsAsync(CancellationToken.None);
                _detected = await scan;
                _owned = mine.Accounts.GroupBy(a => a.AccountHash).ToDictionary(g => g.Key, g => g.First());
                _status.Text = "Signed in to tcgkb as " + mine.User.Name + ". Claim the PTCGL accounts you play with; each one keeps its own stats.";
            })) Fill();
        }

        private void Fill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var d in _detected)
            {
                _owned.TryGetValue(d.AccountHash, out var owned);
                _list.Items.Add(new ListViewItem(new[] { d.Name ?? "(no name)", d.Matches.ToString(), Describe(owned) }) { Tag = d.AccountHash });
            }
            foreach (var o in _owned.Values.Where(o => _detected.All(d => d.AccountHash != o.AccountHash)))
                _list.Items.Add(new ListViewItem(new[] { o.Name ?? "(no name)", "0", Describe(o) }) { Tag = o.AccountHash });
            _list.EndUpdate();
            if (_list.Items.Count == 0 && !_busy)
                _status.Text = "No sealed battle logs with a PTCGL account were found in " + PathUtil.Friendly(_logsFolder) + ". Play a match with the mod first.";
            UpdateButtons();
        }

        private static string Describe(TcgkbAccount a)
        {
            switch (a?.Status)
            {
                case null: return "Not claimed";
                case TcgkbAccount.Verified: return "Yours, verified";
                case TcgkbAccount.Disputed: return "Yours, disputed";
                default: return "Yours, not verified yet";
            }
        }

        private string Selected => _list.SelectedItems.Count == 1 ? (string)_list.SelectedItems[0].Tag : null;

        private void UpdateButtons()
        {
            var hash = Selected;
            _claim.Enabled = !_busy && hash != null && !_owned.ContainsKey(hash) && _detected.Any(d => d.AccountHash == hash && d.Evidence.Count > 0);
            _release.Enabled = !_busy && hash != null && _owned.ContainsKey(hash);
        }

        private async Task ClaimAsync()
        {
            var account = _detected.FirstOrDefault(d => d.AccountHash == Selected);
            if (account == null) return;
            if (!Dialogs.Confirm("Claim the PTCGL account " + account.Name + "?\n\nYour " + account.Evidence.Count + " newest battle logs of this account are " +
                                 "sent to tcgkb.app as proof. They contain your opponents' display names, as the game's own Export does.",
                                 MessageBoxIcon.Question)) return;
            if (await Busy("Claiming " + account.Name + "...", async () =>
            {
                var claim = await Task.Run(() => TcgkbConnection.BuildClaim(account));
                var result = await _connection.ClaimAsync(claim, CancellationToken.None);
                _owned[result.AccountHash] = result;
                Log.Info("Claimed the PTCGL account " + account.Name + " on tcgkb (" + result.Status + ").");
                _status.Text = account.Name + " is now yours on tcgkb. It shows as verified once an opponent's log confirms one of your matches.";
            })) Fill();
        }

        private async Task ReleaseAsync()
        {
            var hash = Selected;
            if (hash == null || !_owned.TryGetValue(hash, out var owned)) return;
            if (!Dialogs.Confirm("Release the PTCGL account " + owned.Name + " from your tcgkb account?", MessageBoxIcon.Question)) return;
            if (await Busy("Releasing " + owned.Name + "...", async () =>
            {
                await _connection.ReleaseAsync(hash, CancellationToken.None);
                _owned.Remove(hash);
                Log.Info("Released the PTCGL account " + owned.Name + " on tcgkb.");
                _status.Text = owned.Name + " no longer belongs to your tcgkb account.";
            })) Fill();
        }

        /// <summary>
        /// Runs one action at a time with the buttons off. False when it failed: the error is shown in
        /// the dialog, and a lost connection closes it.
        /// </summary>
        private async Task<bool> Busy(string what, Func<Task> action)
        {
            _busy = true;
            UpdateButtons();
            UseWaitCursor = true;
            _status.Text = what;
            try
            {
                await action();
                return true;
            }
            catch (TcgkbAuthException e)
            {
                Log.Error("tcgkb connection lost", e);
                Dialogs.Warning(e.Message);
                Close();
                return false;
            }
            catch (Exception e)
            {
                Log.Error(what.TrimEnd('.') + " failed", e);
                _status.Text = e.Message;
                return false;
            }
            finally
            {
                _busy = false;
                if (!IsDisposed)
                {
                    UseWaitCursor = false;
                    UpdateButtons();
                }
            }
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 0), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(8, 0, 0, 0) };
            b.Click += click;
            return b;
        }
    }
}
