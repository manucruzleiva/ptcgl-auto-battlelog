using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Message boxes and folder pickers for an app whose only UI is a tray icon. Without a visible
    /// owner they open behind whatever window is active, so each gets a hidden topmost owner.
    /// </summary>
    internal static class Dialogs
    {
        public static void Info(string text) => Show(text, MessageBoxButtons.OK, MessageBoxIcon.Information);

        public static void Warning(string text) => Show(text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        public static void Error(string text) => Show(text, MessageBoxButtons.OK, MessageBoxIcon.Error);

        public static bool Confirm(string text, MessageBoxIcon icon = MessageBoxIcon.Question) =>
            Show(text, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        /// <summary>Retry or Cancel; true for Retry.</summary>
        public static bool RetryCancel(string text) =>
            Show(text, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) == DialogResult.Retry;

        public static DialogResult Show(string text, MessageBoxButtons buttons, MessageBoxIcon icon,
                                        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        {
            using (var owner = new DialogOwner())
                return MessageBox.Show(owner, text, AppInfo.ProductName, buttons, icon, defaultButton);
        }

        /// <summary>A folder chosen by the player, or null when cancelled.</summary>
        public static string PickFolder(string description, string initial, bool allowNewFolder)
        {
            using (var owner = new DialogOwner())
            using (var dlg = new FolderBrowserDialog { Description = description, ShowNewFolderButton = allowNewFolder })
            {
                try { if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dlg.SelectedPath = initial; }
                catch (Exception) { }
                return dlg.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath) ? dlg.SelectedPath : null;
            }
        }
    }

    /// <summary>
    /// An invisible, topmost, taskbar-less window that brings a dialog to the front. It sits in the
    /// middle of the screen the mouse is on, because dialogs position themselves around their owner.
    /// </summary>
    internal sealed class DialogOwner : Form
    {
        public DialogOwner()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + area.Width / 2, area.Top + area.Height / 3);
            Size = new Size(1, 1);
            Opacity = 0;
            TopMost = true;
            Show();
            Activate();
        }
    }
}
