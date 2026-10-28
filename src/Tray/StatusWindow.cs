using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The status and settings window: the tray menu's items laid out as a window. It holds no state
    /// and no actions of its own: each control mirrors one menu item (its text, whether it shows,
    /// is enabled or ticked) and clicking it clicks that item, so the menu and the window cannot
    /// disagree. The tray refreshes the menu items and then calls Sync.
    /// </summary>
    internal sealed class StatusWindow : Form
    {
        /// <summary>Tags the menu items that show a line of text (TrayApplicationContext.Info).</summary>
        public static readonly object InfoTag = new object();
        /// <summary>Tags the menu items that are ticked on and off (TrayApplicationContext.Toggle).</summary>
        public static readonly object ToggleTag = new object();

        private const int TextWidth = 470;   // at 96 DPI: long status lines and folders wrap

        private readonly List<Action> _syncs = new List<Action>();
        private readonly Action<ToolStripMenuItem> _click;
        private readonly System.Drawing.Icon _icon;   // ours to dispose: a Form does not dispose the Icon it is given
        private readonly Font _boldFont;

        /// <param name="header">The status lines and the actions shown above the tabs.</param>
        /// <param name="tabs">Each tab's name and the menu items it shows.</param>
        /// <param name="footer">The buttons at the bottom, left of Close.</param>
        /// <param name="click">Runs a menu item's action (and refreshes).</param>
        public StatusWindow(string title, IEnumerable<ToolStripItem> header, IEnumerable<(string Name, ToolStripItem[] Items)> tabs,
                            IEnumerable<ToolStripMenuItem> footer, Action<ToolStripMenuItem> click)
        {
            _click = click;
            SuspendLayout();
            // Sizes below are at 96 DPI; Dpi mode scales them to the system DPI (the manifest makes us system DPI aware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.ProductName;
            Font = SystemFonts.MessageBoxFont;
            _boldFont = new Font(Font, FontStyle.Bold);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(540, 560);
            MinimumSize = new Size(440, 420);
            try { Icon = _icon = Embedded.AppIcon(); } catch (Exception) { }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var top = Column();
            top.Margin = new Padding(0, 0, 0, 8);
            top.Controls.Add(new Label { Text = title, AutoSize = true, Font = _boldFont, UseMnemonic = false, Margin = new Padding(3, 0, 3, 4) });
            foreach (var item in header) top.Controls.Add(Mirror(item));

            var tabControl = new TabControl { Dock = DockStyle.Fill };
            foreach (var (name, items) in tabs)
            {
                var page = new TabPage(name) { Padding = new Padding(8), UseVisualStyleBackColor = true };
                var column = Column();
                column.Dock = DockStyle.Fill;
                column.AutoSize = false;
                column.AutoScroll = true;
                foreach (var item in items) column.Controls.Add(Mirror(item));
                page.Controls.Add(column);
                tabControl.TabPages.Add(page);
            }

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 10, 0, 0),
                WrapContents = false,
            };
            var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 0), Margin = new Padding(8, 0, 0, 0) };
            close.Click += (s, e) => Close();
            buttons.Controls.Add(close);
            CancelButton = close;
            foreach (var item in footer.Reverse()) buttons.Controls.Add(MirrorButton(item, new Padding(8, 0, 0, 0)));

            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(tabControl, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout);
            ResumeLayout(false);
            PerformLayout();

            Activated += (s, e) => Sync();
        }

        /// <summary>Copies the menu items' current state to their controls. UI thread.</summary>
        public void Sync()
        {
            if (IsDisposed) return;
            SuspendLayout();
            foreach (var sync in _syncs) sync();
            ResumeLayout(true);
        }

        /// <summary>Brings the window up, from the tray or from another copy of the app.</summary>
        public void Raise()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            // Started from Explorer or another process, a window may open behind the active one: pull it forward once.
            TopMost = true;
            Activate();
            TopMost = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon?.Dispose();
                _boldFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- Mirrors ---------------------------------------------------------------------------

        private static FlowLayoutPanel Column() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };

        private Control Mirror(ToolStripItem item)
        {
            if (!(item is ToolStripMenuItem m)) return new Label { AutoSize = false, Size = new Size(1, 6) };   // a separator: a gap
            if (m.Tag == InfoTag)
            {
                var label = new Label { AutoSize = true, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(3, 2, 3, 4) };
                Sync(m, label, () => label.Text = m.Text, enable: false);   // the item is disabled so that it cannot be clicked: no grey text here
                return label;
            }
            if (m.Tag == ToggleTag)
            {
                var check = new CheckBox { AutoSize = true, AutoCheck = false, Margin = new Padding(3, 2, 3, 2) };
                check.Click += (s, e) => _click(m);
                Sync(m, check, () => { check.Text = m.Text; check.Checked = m.Checked; });
                return check;
            }
            if (!m.HasDropDownItems) return MirrorButton(m, new Padding(3, 2, 3, 2));

            var group = new GroupBox { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 6), Margin = new Padding(3, 6, 3, 6) };
            var inner = Column();
            inner.Dock = DockStyle.Fill;
            var options = m.DropDownItems.OfType<ToolStripMenuItem>().ToList();
            if (options.Count > 0 && options.All(o => o.Tag is string))
            {
                // One choice out of several (Battle log seal, Update channel): radio buttons.
                foreach (var option in options)
                {
                    var radio = new RadioButton { AutoSize = true, AutoCheck = false, Margin = new Padding(3, 2, 3, 2) };
                    radio.Click += (s, e) => { if (!option.Checked) _click(option); };
                    Sync(option, radio, () => { radio.Text = option.Text; radio.Checked = option.Checked; });
                    inner.Controls.Add(radio);
                }
            }
            else
            {
                foreach (ToolStripItem child in m.DropDownItems) inner.Controls.Add(Mirror(child));
            }
            group.Controls.Add(inner);
            Sync(m, group, () => group.Text = m.Text);
            return group;
        }

        private Button MirrorButton(ToolStripMenuItem m, Padding margin)
        {
            var button = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 0), Padding = new Padding(6, 1, 6, 1), Margin = margin };
            if (!m.ForeColor.IsSystemColor) { button.ForeColor = m.ForeColor; button.Font = _boldFont; }   // an item that needs the player (red in the menu)
            button.Click += (s, e) => _click(m);
            Sync(m, button, () => button.Text = m.Text);
            return button;
        }

        /// <summary>
        /// Keeps a control in step with its item. Available, not Visible: Visible is false for every
        /// item of a menu that is closed, Available is what the tray set.
        /// </summary>
        private void Sync(ToolStripMenuItem item, Control control, Action text, bool enable = true)
        {
            void Apply()
            {
                text();
                control.Visible = item.Available;
                if (enable) control.Enabled = item.Enabled;
            }
            Apply();
            _syncs.Add(Apply);
        }
    }
}
