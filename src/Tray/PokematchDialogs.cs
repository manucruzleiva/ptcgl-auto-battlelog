using System;
using System.Drawing;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// "Set API token...": the player pastes the API token they made on Pokematch's API Tokens page.
    /// The box hides what is typed; the token is kept encrypted for this Windows user (PokematchService).
    /// </summary>
    internal sealed class PokematchTokenDialog : Form
    {
        private readonly TextBox _token;
        private readonly Label _error;

        private PokematchTokenDialog()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.ProductName + " - Pokematch API token";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16), Dock = DockStyle.Fill };
            var help = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(440, 0),
                Margin = new Padding(0, 0, 0, 10),
                Text = "Make a token on Pokematch (Profile Settings > Account > API Tokens, for example named \"PTCGL uploader\") and paste it here. " +
                       "It is kept encrypted on this computer for your Windows user, and sent only to Pokematch.\n\n" +
                       "Changing your Pokematch password revokes the token: then set a new one here.",
            };
            _token = new TextBox { Width = 440, UseSystemPasswordChar = true, Margin = new Padding(0, 0, 0, 4) };
            var show = new CheckBox { Text = "Show the token", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            show.CheckedChanged += (s, e) => _token.UseSystemPasswordChar = !show.Checked;
            _error = new Label { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.Firebrick, Visible = false, Margin = new Padding(0, 0, 0, 4) };

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            var cancel = MakeButton("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            var save = MakeButton("Save", (s, e) => Save());
            var open = MakeButton("Open Pokematch", (s, e) => Shell.Open(PokematchApi.TokensPage));
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            buttons.Controls.Add(open);
            AcceptButton = save;
            CancelButton = cancel;

            layout.Controls.Add(help);
            layout.Controls.Add(_token);
            layout.Controls.Add(show);
            layout.Controls.Add(_error);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>The token the player entered, cleaned (PokematchApi.CleanToken), or null when cancelled.</summary>
        public static string Ask()
        {
            using (var dlg = new PokematchTokenDialog())
                return dlg.ShowDialog() == DialogResult.OK ? dlg.Token : null;
        }

        private string Token { get; set; }

        private void Save()
        {
            var token = PokematchApi.CleanToken(_token.Text);
            if (token == null)
            {
                _error.Text = "That does not look like a token: paste only the token itself, without spaces.";
                _error.Visible = true;
                return;
            }
            Token = token;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 0), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(8, 0, 0, 0) };
            b.Click += click;
            return b;
        }
    }
}
