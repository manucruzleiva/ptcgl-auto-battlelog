using System;
using System.Drawing;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The disclaimer (DISCLAIMER.md, embedded) before the first install or the first portable run,
    /// and from the menu later. Accepting is the only way past it; closing the window is Cancel.
    /// The same window, read-only, shows the licenses and third-party notices the exe carries.
    /// </summary>
    internal sealed class DisclaimerDialog : Form
    {
        public enum Purpose { Install, Continue, View, Licenses }

        private readonly System.Drawing.Icon _icon;   // ours to dispose: a Form does not dispose the Icon it is given

        private DisclaimerDialog(Purpose purpose, string text)
        {
            SuspendLayout();
            // Sizes below are at 96 DPI; Dpi mode scales them to the system DPI (the manifest makes us system DPI aware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.ProductName + (purpose == Purpose.Licenses ? " - Licenses" : " - Disclaimer");
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;   // the first window of the app: it must be findable
            ClientSize = new Size(620, 520);
            MinimumSize = new Size(420, 360);
            try { Icon = _icon = Embedded.AppIcon(); } catch (Exception) { }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,   // wraps to the column width
                Margin = new Padding(0, 0, 0, 8),
                Text = purpose == Purpose.Licenses
                    ? AppInfo.ProductName + " is free software under the MIT License. It carries BepInEx and the other components below, " +
                      "which keep their own licenses."
                    : purpose == Purpose.View
                    ? "The terms you accepted when you installed " + AppInfo.ProductName + "."
                    : "Please read this before " + (purpose == Purpose.Install ? "installing" : "using") + " " + AppInfo.ProductName + ".",
            };

            var body = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Window,
                TabStop = false,
                MaxLength = 0,   // no 32K limit: the license texts are longer
                Text = text,
            };

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 10, 0, 0),
                WrapContents = false,
            };

            if (purpose == Purpose.View || purpose == Purpose.Licenses)
            {
                var close = MakeButton("Close", DialogResult.OK);
                buttons.Controls.Add(close);
                AcceptButton = close;
                CancelButton = close;
            }
            else
            {
                var cancel = MakeButton("Cancel", DialogResult.Cancel);
                var accept = MakeButton(purpose == Purpose.Install ? "I understand, install" : "I understand, continue", DialogResult.OK);
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(accept);
                CancelButton = cancel;   // Esc and the close box both mean Cancel
                Shown += (s, e) => accept.Focus();
            }

            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(body, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout);
            ResumeLayout(false);
            PerformLayout();

            Shown += (s, e) =>
            {
                body.SelectionLength = 0;
                // Started from Explorer, a new process's first window may open behind it: pull it forward once.
                TopMost = true;
                Activate();
                TopMost = false;
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _icon?.Dispose();
            base.Dispose(disposing);
        }

        private static Button MakeButton(string text, DialogResult result)
        {
            return new Button
            {
                Text = text,
                DialogResult = result,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(90, 0),
                Padding = new Padding(8, 2, 8, 2),
                Margin = new Padding(8, 0, 0, 0),
            };
        }

        /// <summary>Shows the dialog; true when the player accepted (always true for View and Licenses).</summary>
        public static bool Ask(Purpose purpose)
        {
            string text;
            try { text = purpose == Purpose.Licenses ? Embedded.LicensesText() : Embedded.DisclaimerText(); }
            catch (Exception e)
            {
                Log.Error("Embedded text unreadable", e);
                text = purpose == Purpose.Licenses
                    ? "The licenses could not be loaded. Read them at " + AppInfo.ProjectUrl + "/blob/main/THIRD-PARTY-NOTICES.md."
                    : "The disclaimer could not be loaded. Read it at " + AppInfo.ProjectUrl + "/blob/main/DISCLAIMER.md before continuing.";
            }
            using (var dlg = new DisclaimerDialog(purpose, text))
                return dlg.ShowDialog() == DialogResult.OK;
        }

        /// <summary>
        /// Asks for consent unless this version was accepted already; stores it on acceptance.
        /// False = the player cancelled: the caller exits without changing anything.
        /// </summary>
        public static bool EnsureAccepted(Session session, Purpose purpose)
        {
            if (session.Read(s => s.DisclaimerAccepted) >= Disclaimer.Version) return true;
            if (!Ask(purpose))
            {
                Log.Info("Disclaimer declined.");
                return false;
            }
            if (session.Portable) session.PrepareDataFolder();   // first, so the held log lines land in the right folder
            Log.Commit();
            session.Update(s => s.DisclaimerAccepted = Disclaimer.Version);
            Log.Info("Disclaimer v" + Disclaimer.Version + " accepted.");
            return true;
        }
    }
}
