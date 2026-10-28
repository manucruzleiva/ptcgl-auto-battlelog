using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The status and settings window: the tray menu's items laid out as a window. It holds no state
    /// and no actions of its own: each control mirrors one menu item (its text, whether it shows,
    /// is enabled or ticked) and clicking it clicks that item, so the menu and the window cannot
    /// disagree. The tray refreshes the menu items and then calls Sync.
    /// Look: a white header with the app's icon and a status dot in the tray icon's colours, a
    /// navigation column on the left, and each page's items in white cards on a light grey surface
    /// (Windows 11 settings style). In high contrast mode it keeps the system colours and buttons.
    /// </summary>
    internal sealed class StatusWindow : Form
    {
        /// <summary>Tags the menu items that show a line of text (TrayApplicationContext.Info).</summary>
        public static readonly object InfoTag = new object();
        /// <summary>Tags the menu items that are ticked on and off (TrayApplicationContext.Toggle).</summary>
        public static readonly object ToggleTag = new object();

        // At 96 DPI. TextWidth is a card's text column: long status lines and folders wrap.
        private const int TextWidth = 460;
        private const int NavWidth = 184;

        private readonly List<Action> _syncs = new List<Action>();
        private readonly List<Font> _fonts = new List<Font>();
        private readonly List<(NavButton Button, Control Page)> _pages = new List<(NavButton, Control)>();
        private readonly Action<ToolStripMenuItem> _click;
        private readonly Func<TrayHealth> _health;
        private readonly System.Drawing.Icon _icon;   // ours to dispose: a Form does not dispose the Icon it is given
        private readonly Bitmap _logo;
        private readonly Font _boldFont, _titleFont, _pageFont, _sectionFont, _statusFont, _navFont;

        /// <param name="title">The header's big line (the product name).</param>
        /// <param name="subtitle">The line under it (version, portable).</param>
        /// <param name="status">The status line, shown with a dot in the tray icon's colour.</param>
        /// <param name="health">The tray icon's state, for the dot.</param>
        /// <param name="header">The other lines and actions shown under the status.</param>
        /// <param name="tabs">Each page's name and the menu items it shows.</param>
        /// <param name="footer">The buttons at the bottom, left of Close.</param>
        /// <param name="click">Runs a menu item's action (and refreshes).</param>
        public StatusWindow(string title, string subtitle, ToolStripMenuItem status, Func<TrayHealth> health,
                            IEnumerable<ToolStripItem> header, IEnumerable<(string Name, ToolStripItem[] Items)> tabs,
                            IEnumerable<ToolStripMenuItem> footer, Action<ToolStripMenuItem> click)
        {
            _click = click;
            _health = health;
            SuspendLayout();
            // Sizes below are at 96 DPI; Dpi mode scales them to the system DPI (the manifest makes us system DPI aware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.ProductName;
            Font = SystemFonts.MessageBoxFont;
            ForeColor = Palette.Text;
            BackColor = Palette.Surface;
            _boldFont = Track(new Font(Font, FontStyle.Bold));
            _titleFont = Semibold(14F);
            _pageFont = Semibold(13F);
            _sectionFont = Semibold(10.5F);
            _statusFont = Semibold(10F);
            _navFont = Track(new Font(Font.FontFamily, 10F));
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(784, 600);
            MinimumSize = new Size(800, 480);
            try
            {
                Icon = _icon = Embedded.AppIcon();
                using (var big = new System.Drawing.Icon(_icon, 48, 48)) _logo = big.ToBitmap();
            }
            catch (Exception) { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(BuildHeader(title, subtitle, status, header), 0, 0);
            root.Controls.Add(Line(), 0, 1);
            root.Controls.Add(BuildBody(tabs), 0, 2);
            root.Controls.Add(Line(), 0, 3);
            root.Controls.Add(BuildFooter(footer), 0, 4);
            Controls.Add(root);
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
                _logo?.Dispose();
                foreach (var font in _fonts) font.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- Layout ----------------------------------------------------------------------------

        /// <summary>Icon, product name and version, the status with its dot, then the other header items.</summary>
        private Control BuildHeader(string title, string subtitle, ToolStripMenuItem status, IEnumerable<ToolStripItem> items)
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Palette.Card,
                Margin = Padding.Empty,
                Padding = new Padding(24, 18, 24, 14),
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var logo = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Image = _logo, Margin = new Padding(0, 2, 16, 0), Visible = _logo != null };
            header.Controls.Add(logo, 0, 0);

            var text = Column();
            text.Controls.Add(new Label { Text = title, AutoSize = true, Font = _titleFont, UseMnemonic = false, Margin = Padding.Empty });
            text.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = Palette.Secondary, UseMnemonic = false, Margin = new Padding(0, 0, 0, 8) });

            var statusRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4) };
            var dot = new Dot { Size = new Size(10, 10), Margin = new Padding(0, 6, 8, 0), BackColor = Palette.Card };
            var statusLabel = new Label { AutoSize = true, Font = _statusFont, MaximumSize = new Size(TextWidth + 120, 0), Margin = Padding.Empty };
            Sync(status, statusLabel, () => statusLabel.Text = status.Text, enable: false);   // the item is disabled so that it cannot be clicked
            Sync(status, dot, () => dot.Fill = HealthColor(_health()), enable: false);
            statusRow.Controls.Add(dot);
            statusRow.Controls.Add(statusLabel);
            text.Controls.Add(statusRow);
            AddItems(text.Controls, items, Palette.Secondary);
            header.Controls.Add(text, 1, 0);
            return header;
        }

        /// <summary>The navigation column and one scrolling page per tab; the first page shows.</summary>
        private Control BuildBody(IEnumerable<(string Name, ToolStripItem[] Items)> tabs)
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NavWidth));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(12, 16, 8, 8) };
            var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            foreach (var (name, items) in tabs)
            {
                var page = BuildPage(name, items);
                page.Visible = _pages.Count == 0;
                host.Controls.Add(page);

                var button = new NavButton { Text = name, Font = _navFont, Size = new Size(NavWidth - 20, 38), Margin = new Padding(0, 0, 0, 4), Checked = _pages.Count == 0 };
                button.CheckedChanged += (s, e) => { if (button.Checked) ShowPage(page); };
                nav.Controls.Add(button);
                _pages.Add((button, page));
            }
            body.Controls.Add(nav, 0, 0);
            body.Controls.Add(host, 1, 0);
            return body;
        }

        private void ShowPage(Control page)
        {
            SuspendLayout();
            foreach (var (_, other) in _pages) other.Visible = ReferenceEquals(other, page);
            ResumeLayout(true);
        }

        /// <summary>
        /// A page: its name as a heading, then its items in cards. A separator starts a new card, and
        /// a submenu gets a card of its own titled with its name.
        /// </summary>
        private Control BuildPage(string name, IEnumerable<ToolStripItem> items)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 16, 24, 8), Margin = Padding.Empty };
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stack.Controls.Add(new Label { Text = name, AutoSize = true, Font = _pageFont, UseMnemonic = false, Margin = new Padding(0, 0, 0, 12) });

            var pending = new List<ToolStripItem>();
            void Flush()
            {
                if (pending.Count > 0) stack.Controls.Add(BuildCard(null, pending.ToArray()));
                pending.Clear();
            }
            foreach (var item in items)
            {
                if (!(item is ToolStripMenuItem m)) Flush();
                else if (m.HasDropDownItems) { Flush(); stack.Controls.Add(BuildCard(m, m.DropDownItems.Cast<ToolStripItem>().ToArray())); }
                else pending.Add(m);
            }
            Flush();
            page.Controls.Add(stack);
            return page;
        }

        /// <summary>A white card: the submenu's name as its title (if any), then the items.</summary>
        private Control BuildCard(ToolStripMenuItem section, ToolStripItem[] items)
        {
            var card = new Card { Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 12) };
            if (section != null)
            {
                var heading = new Label { AutoSize = true, Font = _sectionFont, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 0, 0, 6) };
                Sync(section, heading, () => heading.Text = section.Text, enable: false);
                card.Controls.Add(heading);
            }
            var options = items.OfType<ToolStripMenuItem>().ToList();
            if (section != null && options.Count > 0 && options.All(o => o.Tag is string))
            {
                // One choice out of several (Battle log seal, Update channel): radio buttons.
                foreach (var option in options)
                {
                    var radio = new RadioButton { AutoSize = true, AutoCheck = false, Margin = new Padding(0, 3, 0, 3) };
                    radio.Click += (s, e) => { if (!option.Checked) _click(option); };
                    Sync(option, radio, () => { radio.Text = option.Text; radio.Checked = option.Checked; });
                    card.Controls.Add(radio);
                }
            }
            else AddItems(card.Controls, items, Palette.Text);

            // A card shows while any of its items does; a submenu's card follows the submenu (shown, enabled).
            void Apply()
            {
                card.Visible = section != null ? section.Available : options.Any(o => o.Available);
                if (section != null) card.Enabled = section.Enabled;
            }
            Apply();
            _syncs.Add(Apply);
            return card;
        }

        /// <summary>Open logs folder, Open last log, Close.</summary>
        private Control BuildFooter(IEnumerable<ToolStripMenuItem> footer)
        {
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(20, 12, 20, 12),
                WrapContents = false,
            };
            var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(96, 32), Margin = Padding.Empty };
            Style(close, Look.Neutral, Color.Empty);
            close.Click += (s, e) => Close();
            buttons.Controls.Add(close);
            CancelButton = close;
            foreach (var item in footer.Reverse()) buttons.Controls.Add(MirrorButton(item, new Padding(0, 0, 8, 0)));
            return buttons;
        }

        // ---- Mirrors ---------------------------------------------------------------------------

        /// <summary>
        /// Adds a control per item: info lines become labels, toggles switches, a submenu a sub-heading
        /// with its items, the rest buttons. Buttons in a row share a line and wrap.
        /// </summary>
        private void AddItems(Control.ControlCollection target, IEnumerable<ToolStripItem> items, Color infoColor)
        {
            FlowLayoutPanel row = null;
            foreach (var item in items)
            {
                if (!(item is ToolStripMenuItem m))
                {
                    row = null;
                    target.Add(new Panel { Height = 1, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Palette.Border, Margin = new Padding(0, 8, 0, 8) });
                    continue;
                }
                if (m.Tag == InfoTag)
                {
                    row = null;
                    var label = new Label { AutoSize = true, ForeColor = infoColor, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 2, 0, 4) };
                    Sync(m, label, () => label.Text = m.Text, enable: false);   // the item is disabled so that it cannot be clicked: no grey text here
                    target.Add(label);
                }
                else if (m.Tag == ToggleTag)
                {
                    row = null;
                    var toggle = new ToggleSwitch { AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
                    toggle.Click += (s, e) => _click(m);
                    Sync(m, toggle, () => { toggle.Text = m.Text; toggle.Checked = m.Checked; });
                    target.Add(toggle);
                }
                else if (m.HasDropDownItems)
                {
                    row = null;
                    var group = Column();
                    group.Margin = new Padding(0, 6, 0, 6);
                    var heading = new Label { AutoSize = true, Font = _statusFont, Margin = new Padding(0, 0, 0, 4) };
                    group.Controls.Add(heading);
                    AddItems(group.Controls, m.DropDownItems.Cast<ToolStripItem>(), infoColor);
                    Sync(m, group, () => heading.Text = m.Text);
                    target.Add(group);
                }
                else
                {
                    if (row == null)
                    {
                        // MaximumSize makes the auto-sized row wrap its buttons instead of growing past the card.
                        row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 6, 0, 0) };
                        target.Add(row);
                    }
                    row.Controls.Add(MirrorButton(m, new Padding(0, 0, 8, 8)));
                }
            }
        }

        private Button MirrorButton(ToolStripMenuItem m, Padding margin)
        {
            var button = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 32), Padding = new Padding(8, 0, 8, 0), Margin = margin };
            // An item that needs the player (red in the menu) is filled red; the menu's bold item (the double-click action) is the accent.
            var look = !m.ForeColor.IsSystemColor ? Look.Attention : m.Font.Bold ? Look.Accent : Look.Neutral;
            Style(button, look, m.ForeColor);
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

        // ---- Look ------------------------------------------------------------------------------

        private enum Look { Neutral, Accent, Attention }

        private static void StyleFlat(Button button, Color face, Color text, Color border, Color hover, Color down)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = face;
            button.ForeColor = text;
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.MouseOverBackColor = hover;
            button.FlatAppearance.MouseDownBackColor = down;
        }

        private void Style(Button button, Look look, Color attention)
        {
            if (Palette.Plain)
            {
                // High contrast: system buttons, the attention item in bold red as in the menu.
                if (look == Look.Attention) { button.ForeColor = attention; button.Font = _boldFont; }
                return;
            }
            switch (look)
            {
                case Look.Accent:
                    StyleFlat(button, Palette.Accent, Palette.OnAccent, Palette.Accent, Blend(Palette.Accent, Color.White, 0.12), Blend(Palette.Accent, Color.Black, 0.15));
                    break;
                case Look.Attention:
                    StyleFlat(button, attention, Palette.OnAccent, attention, Blend(attention, Color.White, 0.12), Blend(attention, Color.Black, 0.15));
                    break;
                default:
                    StyleFlat(button, Palette.ButtonFace, Palette.Text, Palette.ButtonBorder, Palette.Hover, Blend(Palette.Hover, Color.Black, 0.06));
                    break;
            }
        }

        /// <summary>The tray icon's dot colour; grey when the tray shows none (healthy, game closed).</summary>
        private static Color HealthColor(TrayHealth health)
        {
            var color = StatusIcons.DotColor(health);
            return color.IsEmpty ? Color.FromArgb(0x9E, 0x9E, 0x9E) : color;
        }

        private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

        private Font Track(Font font)
        {
            _fonts.Add(font);
            return font;
        }

        /// <summary>Segoe UI Semibold (Windows 10/11); bold where it is missing.</summary>
        private Font Semibold(float size)
        {
            var font = new Font("Segoe UI Semibold", size);
            if (font.Name != "Segoe UI Semibold") { font.Dispose(); font = new Font(Font.FontFamily, size, FontStyle.Bold); }
            return Track(font);
        }

        private static FlowLayoutPanel Column() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };

        private static Control Line() => new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Palette.Border };

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Windows 11 light colours; the system colours in high contrast mode.</summary>
        private static class Palette
        {
            public static readonly bool Plain = SystemInformation.HighContrast;
            public static readonly Color Surface = Plain ? SystemColors.Control : Color.FromArgb(0xF3, 0xF3, 0xF3);
            public static readonly Color Card = Plain ? SystemColors.Control : Color.White;
            public static readonly Color Border = Plain ? SystemColors.ControlText : Color.FromArgb(0xE3, 0xE3, 0xE3);
            public static readonly Color Text = Plain ? SystemColors.ControlText : Color.FromArgb(0x1B, 0x1B, 0x1B);
            public static readonly Color Secondary = Plain ? SystemColors.ControlText : Color.FromArgb(0x5C, 0x5C, 0x5C);
            public static readonly Color Accent = Plain ? SystemColors.Highlight : Color.FromArgb(0x00, 0x5F, 0xB8);
            public static readonly Color OnAccent = Plain ? SystemColors.HighlightText : Color.White;
            public static readonly Color Hover = Plain ? SystemColors.Control : Color.FromArgb(0xEA, 0xEA, 0xEA);
            public static readonly Color Selected = Plain ? SystemColors.Highlight : Color.FromArgb(0xE2, 0xE2, 0xE2);
            public static readonly Color SelectedText = Plain ? SystemColors.HighlightText : Text;
            public static readonly Color ButtonFace = Plain ? SystemColors.Control : Color.FromArgb(0xFD, 0xFD, 0xFD);
            public static readonly Color ButtonBorder = Plain ? SystemColors.ControlText : Color.FromArgb(0xD0, 0xD0, 0xD0);
            public static readonly Color SwitchOff = Plain ? SystemColors.ControlText : Color.FromArgb(0x86, 0x86, 0x86);
            public static readonly Color Disabled = Plain ? SystemColors.GrayText : Color.FromArgb(0xA8, 0xA8, 0xA8);
        }

        // ---- Controls --------------------------------------------------------------------------

        /// <summary>A white, rounded card on the grey surface: a one-column table whose rows size to their content.</summary>
        private sealed class Card : TableLayoutPanel
        {
            public Card()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                ColumnCount = 1;
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                Padding = new Padding(18, 14, 18, 12);
                BackColor = Palette.Surface;   // the corners; the card itself is painted below
            }

            protected override void OnControlAdded(ControlEventArgs e)
            {
                base.OnControlAdded(e);
                // Children sit inside the padding, on the white; one with its own colour (a divider) keeps it.
                if (e.Control.BackColor == Palette.Surface) e.Control.BackColor = Palette.Card;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var surface = new SolidBrush(Palette.Surface)) g.FillRectangle(surface, ClientRectangle);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(4, Font.Height / 3)))
                {
                    using (var fill = new SolidBrush(Palette.Card)) g.FillPath(fill, path);
                    using (var pen = new Pen(Palette.Border)) g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>The status dot.</summary>
        private sealed class Dot : Control
        {
            private Color _fill = Color.Gray;

            public Dot()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }

            public Color Fill
            {
                get => _fill;
                set { if (_fill != value) { _fill = value; Invalidate(); } }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var back = new SolidBrush(BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(_fill)) e.Graphics.FillEllipse(fill, 0, 0, Width - 1, Height - 1);
            }
        }

        /// <summary>A check box drawn as an on/off switch, the text on its right. Still a check box to keyboards and screen readers.</summary>
        private sealed class ToggleSwitch : CheckBox
        {
            private bool _hover;

            public ToggleSwitch()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AutoCheck = false;
            }

            // From the font, so the switch scales with the text at any DPI.
            private int TrackHeight => (int)Math.Round(Font.Height * 1.25);
            private int TrackWidth => TrackHeight * 2;
            private int Gap => TrackHeight / 2;

            public override Size GetPreferredSize(Size proposedSize)
            {
                var text = TextRenderer.MeasureText(Text, Font);
                return new Size(TrackWidth + Gap + text.Width + 4, Math.Max(TrackHeight, text.Height) + 6);
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);

                int h = TrackHeight, w = TrackWidth;
                var track = new Rectangle(1, (Height - h) / 2, w - 1, h - 1);
                bool on = Checked;
                Color fill = !Enabled ? (on ? Palette.Disabled : BackColor)
                           : on ? (_hover ? Blend(Palette.Accent, Color.White, 0.12) : Palette.Accent)
                           : (_hover ? Palette.Hover : BackColor);
                Color edge = !Enabled ? Palette.Disabled : on ? fill : Palette.SwitchOff;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundRect(track, h / 2))
                {
                    using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                    using (var pen = new Pen(edge)) g.DrawPath(pen, path);
                }
                int d = h * 3 / 5;
                int x = on ? track.Left + w - (h + d) / 2 : track.Left + (h - d) / 2;
                int y = track.Top + (h - d) / 2;
                Color knob = !Enabled ? (on ? BackColor : Palette.Disabled) : on ? Palette.OnAccent : Palette.SwitchOff;
                using (var brush = new SolidBrush(knob)) g.FillEllipse(brush, x, y, d, d);
                g.SmoothingMode = SmoothingMode.Default;

                var textBox = new Rectangle(track.Left + w + Gap, 0, Math.Max(0, Width - w - Gap - 1), Height);
                TextRenderer.DrawText(g, Text, Font, textBox, Enabled ? ForeColor : Palette.Disabled,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.HidePrefix);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
            }
        }

        /// <summary>A navigation entry: a radio button drawn flat, rounded when selected or hovered, with an accent bar.</summary>
        private sealed class NavButton : RadioButton
        {
            private bool _hover;

            public NavButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AutoSize = false;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (Checked || _hover)
                {
                    using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(3, Height / 8)))
                    using (var brush = new SolidBrush(Checked ? Palette.Selected : Palette.Hover))
                        g.FillPath(brush, path);
                }
                int bar = Math.Max(3, Height / 12);
                if (Checked && !Palette.Plain)
                {
                    int barHeight = Height * 2 / 5;
                    using (var path = RoundRect(new Rectangle(1, (Height - barHeight) / 2, bar, barHeight), bar / 2 + 1))
                    using (var brush = new SolidBrush(Palette.Accent))
                        g.FillPath(brush, path);
                }
                g.SmoothingMode = SmoothingMode.Default;
                int indent = bar + Height / 3;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(indent, 0, Math.Max(0, Width - indent), Height),
                                      Checked ? Palette.SelectedText : Palette.Text,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.HidePrefix);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2));
            }
        }
    }
}
