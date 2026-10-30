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
}
