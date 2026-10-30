using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The status and settings window: the tray menu's items laid out as a window, plus a home page
    /// with the player's matches and their place on the tcgkb ladder. Each control that mirrors a menu
    /// item copies its text, whether it shows, is enabled or ticked, and clicking it clicks that item,
    /// so the menu and the window cannot disagree. The rest (local matches, tcgkb accounts and
    /// ladder) comes from the data the tray hands over; the window keeps none of its own. The tray
    /// refreshes the menu items and then calls Sync.
    /// Look: dark, with tcgkb's yellow as the accent; a navigation column with icons on the left and
    /// each page's items in rounded cards. In high contrast mode it keeps the system colours and buttons.
    /// </summary>
    internal sealed class StatusWindow : Form
    {
        /// <summary>Tags the menu items that show a line of text (TrayApplicationContext.Info).</summary>
        public static readonly object InfoTag = new object();
        /// <summary>Tags the menu items that are ticked on and off (TrayApplicationContext.Toggle).</summary>
        public static readonly object ToggleTag = new object();

        /// <summary>The menu items the window mirrors.</summary>
        public sealed class Items
        {
            public string Version;
            public ToolStripMenuItem Status, Update;
            /// <summary>Under the status: mod version, last saved, data folder.</summary>
            public ToolStripItem[] StatusLines;
            /// <summary>The home page's buttons: disclaimer, game folder, open logs folder, open last log.</summary>
            public ToolStripMenuItem[] Actions;
            /// <summary>The one of them in the accent colour.</summary>
            public ToolStripMenuItem Primary;
            /// <summary>The tcgkb submenu, and the items of it the home page shows.</summary>
            public ToolStripMenuItem Tcgkb, TcgkbConnect, TcgkbAutoUpload, TcgkbLastUpload;
            public ToolStripItem[] Settings, Updates, Help;
        }

        /// <summary>A battle log in the logs folder, from its file name.</summary>
        public sealed class Match
        {
            public string Result, Opponent, Path;
            public DateTime LocalTime;
        }

        /// <summary>What the window shows besides the menu items. Read on every Sync.</summary>
        public sealed class Data
        {
            /// <summary>Null while not signed in to tcgkb.</summary>
            public string UserName;
            /// <summary>The PTCGL accounts and ladder from tcgkb; null until it answered.</summary>
            public TcgkbAccounts Me;
            /// <summary>Newest first.</summary>
            public IReadOnlyList<Match> Matches = new Match[0];
        }

        // At 96 DPI. TextWidth is a card's text column: long status lines and folders wrap.
        private const int TextWidth = 460;
        // The home page's two cards side by side: their text wraps here, narrower than the cards even at
        // the window's minimum size, so a card's measured height is the height it gets.
        private const int HalfWidth = 280;
        private const int NavWidth = 190;
        private const int MaxListedMatches = 100;
        private const string LadderUrl = "https://tcgkb.app/leaderboard";

        private readonly List<Action> _syncs = new List<Action>();
        private readonly List<Font> _fonts = new List<Font>();
        private readonly List<(NavButton Button, Control Page)> _pages = new List<(NavButton, Control)>();
        private readonly Action<ToolStripMenuItem> _click;
        private readonly Func<TrayHealth> _health;
        private readonly Func<Data> _data;
        private readonly Action<string> _openLog, _openUrl;
        private readonly System.Drawing.Icon _icon;   // ours to dispose: a Form does not dispose the Icon it is given
        private readonly Font _boldFont, _heroFont, _pageFont, _sectionFont, _strongFont, _navFont, _iconFont, _statFont, _smallFont;
        private Data _d = new Data();
        private string _filter = "all";
        private NavButton _matchesNav, _tcgkbNav, _updatesNav;

        /// <param name="items">The menu items the window mirrors.</param>
        /// <param name="health">The tray icon's state, for the status dot.</param>
        /// <param name="data">The matches and tcgkb data, read on every Sync.</param>
        /// <param name="click">Runs a menu item's action (and refreshes).</param>
        /// <param name="openLog">Opens a battle log.</param>
        /// <param name="openUrl">Opens a web page.</param>
        public StatusWindow(Items items, Func<TrayHealth> health, Func<Data> data, Action<ToolStripMenuItem> click, Action<string> openLog, Action<string> openUrl)
        {
            _click = click;
            _health = health;
            _data = data;
            _openLog = openLog;
            _openUrl = openUrl;
            _d = _data() ?? new Data();
            SuspendLayout();
            // Sizes below are at 96 DPI; Dpi mode scales them to the system DPI (the manifest makes us system DPI aware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.ProductName;
            Font = SystemFonts.MessageBoxFont;
            ForeColor = Palette.Text;
            BackColor = Palette.Surface;
            _boldFont = Track(new Font(Font, FontStyle.Bold));
            _heroFont = Semibold(14F);
            _pageFont = Semibold(14F);
            _statFont = Semibold(14F);
            _sectionFont = Semibold(8.5F);
            _strongFont = Semibold(10F);
            _smallFont = Track(new Font(Font.FontFamily, 8.5F));
            _navFont = Track(new Font(Font.FontFamily, 10F));
            _iconFont = IconFont(11F);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ShowInTaskbar = true;
            KeyPreview = true;
            ClientSize = new Size(880, 600);
            MinimumSize = new Size(820, 500);
            try { Icon = _icon = Embedded.AppIcon(); }
            catch (Exception) { }

            Controls.Add(BuildBody(items));
            ResumeLayout(false);
            PerformLayout();

            Activated += (s, e) => Sync();
        }

        /// <summary>Copies the menu items' current state and the tray's data to their controls. UI thread.</summary>
        public void Sync()
        {
            if (IsDisposed) return;
            _d = _data() ?? new Data();
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Palette.Plain) DarkTitleBar(Handle);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon?.Dispose();
                foreach (var font in _fonts) font.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- Layout ----------------------------------------------------------------------------

        /// <summary>The navigation column and one scrolling page per section; Home shows first.</summary>
        private Control BuildBody(Items items)
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NavWidth));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(10, 12, 8, 8) };
            nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };

            NavButton Add(string name, string glyph, Control page)
            {
                page.Visible = _pages.Count == 0;
                host.Controls.Add(page);
                var button = new NavButton { Text = name, Glyph = glyph, GlyphFont = _iconFont, Font = _navFont, Size = new Size(NavWidth - 18, 38), Margin = new Padding(0, 0, 0, 2), Checked = _pages.Count == 0 };
                button.CheckedChanged += (s, e) => { if (button.Checked) ShowPage(page); };
                buttons.Controls.Add(button);
                _pages.Add((button, page));
                return button;
            }
            // Segoe MDL2 Assets / Segoe Fluent Icons code points.
            Add("Home", "", BuildHome(items));
            _matchesNav = Add("Matches", "", BuildMatches());
            _tcgkbNav = Add("tcgkb", "", BuildTcgkb(items));
            Add("Settings", "", BuildPage("Settings", items.Settings));
            _updatesNav = Add("Updates", "", BuildPage("Updates", items.Updates));
            Add("Help", "", BuildPage("Help", items.Help));
            nav.Controls.Add(buttons, 0, 0);

            var foot = Column();
            foot.Margin = new Padding(12, 0, 0, 4);
            foot.Controls.Add(new Label { Text = items.Version, AutoSize = true, ForeColor = Palette.Secondary, Font = _smallFont, UseMnemonic = false, Margin = Padding.Empty });
            var update = Link("", () => _updatesNav.Checked = true);
            update.Font = _smallFont;
            Sync(items.Update, update, () => update.Text = items.Update.Text, enable: false);
            foot.Controls.Add(update);
            nav.Controls.Add(foot, 0, 1);

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

        /// <summary>A scrolling page and the stack its cards go in, under the page's name (none for Home).</summary>
        private (Panel Page, TableLayoutPanel Stack) Page(string name)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 12, 20, 8), Margin = Padding.Empty };
            page.HandleCreated += (s, e) => { if (!Palette.Plain) DarkScrollBars(page.Handle); };
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            if (name != null) stack.Controls.Add(new Label { Text = name, AutoSize = true, Font = _pageFont, UseMnemonic = false, Margin = new Padding(0, 4, 0, 12) });
            page.Controls.Add(stack);
            return (page, stack);
        }

        /// <summary>
        /// A page of menu items: its name as a heading, then its items in cards. A separator starts a new
        /// card, and a submenu gets a card of its own titled with its name.
        /// </summary>
        private Control BuildPage(string name, IEnumerable<ToolStripItem> items)
        {
            var (page, stack) = Page(name);
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
            return page;
        }

        /// <summary>A card: the submenu's name as its title (if any), then the items.</summary>
        private Card BuildCard(ToolStripMenuItem section, ToolStripItem[] items)
        {
            var card = NewCard();
            if (section != null)
            {
                var heading = new Label { AutoSize = true, Font = _strongFont, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 0, 0, 6) };
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
            else AddItems(card.Controls, items, Palette.Secondary);

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

        // ---- Home ------------------------------------------------------------------------------

        /// <summary>The status with its actions, the tcgkb card and the week's numbers, then the last matches.</summary>
        private Control BuildHome(Items items)
        {
            var (page, stack) = Page(null);
            stack.Controls.Add(BuildHero(items));

            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12), Padding = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            var tcgkb = BuildTcgkbSummary(items);
            tcgkb.Margin = new Padding(0, 0, 6, 0);
            var week = BuildWeek();
            week.Margin = new Padding(6, 0, 0, 0);
            row.Controls.Add(tcgkb, 0, 0);
            row.Controls.Add(week, 1, 0);
            stack.Controls.Add(row);

            var last = NewCard();
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(Section("Last matches", Padding.Empty), 0, 0);
            var all = new Button { Text = "See all", AutoSize = true, MinimumSize = new Size(0, 28), Margin = Padding.Empty };
            Style(all, Look.Neutral, Color.Empty);
            all.Click += (s, e) => _matchesNav.Checked = true;
            top.Controls.Add(all, 1, 0);
            last.Controls.Add(top);
            last.Controls.Add(MatchList(() => _d.Matches.Take(5), "No battle log yet. Play a match and it shows up here."));
            stack.Controls.Add(last);
            return page;
        }

        /// <summary>The status in big letters with the tray icon's colour, the lines under it, and the main buttons.</summary>
        private Control BuildHero(Items items)
        {
            var card = NewCard();
            card.Padding = new Padding(18, 16, 18, 14);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var halo = new Halo { Size = new Size(44, 44), Margin = new Padding(0, 2, 14, 0) };
            Sync(items.Status, halo, () => halo.Fill = HealthColor(_health()), enable: false);
            grid.Controls.Add(halo, 0, 0);

            var text = Column();
            var status = new Label { AutoSize = true, Font = _heroFont, MaximumSize = new Size(TextWidth + 120, 0), Margin = new Padding(0, 0, 0, 2) };
            Sync(items.Status, status, () => status.Text = items.Status.Text, enable: false);   // the item is disabled so that it cannot be clicked
            text.Controls.Add(status);
            AddItems(text.Controls, items.StatusLines, Palette.Secondary);
            var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(TextWidth + 120, 0), Margin = new Padding(0, 8, 0, 0) };
            foreach (var item in items.Actions) actions.Controls.Add(MirrorButton(item, new Padding(0, 0, 8, 4), item == items.Primary ? Look.Accent : (Look?)null));
            text.Controls.Add(actions);
            grid.Controls.Add(text, 1, 0);
            card.Controls.Add(grid);
            return card;
        }

        /// <summary>Signed in: the accounts with their verified mark, the ladder progress and the upload switch. Otherwise: sign in.</summary>
        private Card BuildTcgkbSummary(Items items)
        {
            var card = NewCard();
            card.Controls.Add(Section("tcgkb", new Padding(0, 0, 0, 8)));

            // Straight in the card's rows (nested auto-sized panels lose their last rows when their visibility flips).
            var who = new Label { AutoSize = true, Font = _strongFont, UseMnemonic = false, MaximumSize = new Size(HalfWidth, 0), Margin = Padding.Empty };
            var upload = new Label { AutoSize = true, ForeColor = Palette.Secondary, UseMnemonic = false, MaximumSize = new Size(HalfWidth, 0), Margin = new Padding(0, 2, 0, 0) };
            var chips = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(HalfWidth, 0), Margin = new Padding(0, 10, 0, 0) };
            var explain = Link("What does verified mean?", () => _tcgkbNav.Checked = true);
            var ladderTop = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 12, 0, 0) };
            ladderTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ladderTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var ladderTitle = new Label { AutoSize = true, Margin = Padding.Empty };
            var ladderCount = new Label { AutoSize = true, ForeColor = Palette.Secondary, Margin = Padding.Empty, Anchor = AnchorStyles.Right };
            ladderTop.Controls.Add(ladderTitle, 0, 0);
            ladderTop.Controls.Add(ladderCount, 1, 0);
            var bar = new Bar { Height = 6, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            var toggle = MirrorToggle(items.TcgkbAutoUpload);
            toggle.Margin = new Padding(0, 10, 0, 0);
            var signedIn = new Control[] { who, upload, chips, explain, ladderTop, bar, toggle };
            Sync(items.TcgkbLastUpload, upload, () => upload.Text = items.TcgkbLastUpload.Text, enable: false);
            card.Controls.AddRange(signedIn);

            var outTitle = new Label { Text = "Your matches stay on this PC", AutoSize = true, Font = _strongFont, Margin = Padding.Empty };
            var outText = new Label
            {
                Text = "Sign in once and every match goes to tcgkb.app by itself: your stats, the meta and the ladder.",
                AutoSize = true, ForeColor = Palette.Secondary, MaximumSize = new Size(HalfWidth, 0), Margin = new Padding(0, 4, 0, 10),
            };
            var outButton = MirrorButton(items.TcgkbConnect, Padding.Empty, Look.Accent);
            var signedOut = new Control[] { outTitle, outText };
            card.Controls.AddRange(signedOut);
            card.Controls.Add(outButton);   // follows the sign-in item

            string chipKey = null;
            void Apply()
            {
                bool on = _d.UserName != null;
                // The items' own syncs ran first: the toggle and the upload line also follow their items.
                foreach (var c in new Control[] { who, chips, explain, ladderTop, bar }) c.Visible = on;
                if (!on) upload.Visible = toggle.Visible = false;
                foreach (var c in signedOut) c.Visible = !on;
                if (!on) return;
                who.Text = "Signed in as " + _d.UserName;
                var accounts = _d.Me?.Accounts ?? new List<TcgkbAccount>();
                var key = string.Join("|", accounts.Select(a => a.Name + ":" + a.Status));
                if (key != chipKey)
                {
                    chipKey = key;
                    chips.Controls.Clear();
                    foreach (var a in accounts) chips.Controls.Add(new Chip { Text = (a.Name ?? "PTCGL account") + " · " + StatusWord(a.Status), Highlight = a.Status == TcgkbAccount.Verified, Margin = new Padding(0, 0, 6, 6) });
                }
                chips.Visible = explain.Visible = accounts.Count > 0;
                var l = LadderStatus.From(_d.Me);
                ladderTop.Visible = bar.Visible = l != null;
                if (l != null)
                {
                    ladderTitle.Text = l.Title;
                    ladderCount.Text = l.Count;
                    bar.Value = l.Progress;
                }
            }
            Apply();
            _syncs.Add(Apply);
            return card;
        }

        /// <summary>The last seven days from the logs folder: matches, win rate, victories.</summary>
        private Card BuildWeek()
        {
            var card = NewCard();
            card.Controls.Add(Section("This week", new Padding(0, 0, 0, 8)));
            var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            Label Tile(string caption, int column, Color color)
            {
                var tile = new Tile { Dock = DockStyle.Fill, Margin = new Padding(column == 0 ? 0 : 4, 0, column == 2 ? 0 : 4, 0) };
                var number = new Label { AutoSize = true, Font = _statFont, ForeColor = color, Margin = Padding.Empty, BackColor = Palette.Raise };
                tile.Controls.Add(number);
                tile.Controls.Add(new Label { Text = caption, AutoSize = true, Font = _smallFont, ForeColor = Palette.Secondary, Margin = Padding.Empty, BackColor = Palette.Raise });
                tiles.Controls.Add(tile, column, 0);
                return number;
            }
            var count = Tile("matches", 0, Palette.Text);
            var rate = Tile("win rate", 1, Palette.Good);
            var wins = Tile("victories", 2, Palette.Text);
            card.Controls.Add(tiles);
            var note = new Label { AutoSize = true, ForeColor = Palette.Secondary, MaximumSize = new Size(HalfWidth - 60, 0), Margin = new Padding(0, 10, 0, 0) };
            card.Controls.Add(note);
            void Apply()
            {
                var since = DateTime.Now.AddDays(-7);
                var week = _d.Matches.Where(m => m.LocalTime >= since).ToList();
                int won = week.Count(m => m.Result == "VICTORY"), lost = week.Count(m => m.Result == "DEFEAT");
                count.Text = week.Count.ToString(CultureInfo.CurrentCulture);
                rate.Text = won + lost == 0 ? "–" : Math.Round(100.0 * won / (won + lost)).ToString(CultureInfo.CurrentCulture) + "%";
                wins.Text = won.ToString(CultureInfo.CurrentCulture);
                note.Text = _d.Matches.Count == 0 ? "From the battle logs on this PC." : _d.Matches.Count + " battle logs on this PC in all.";
            }
            Apply();
            _syncs.Add(Apply);
            return card;
        }

        // ---- Matches ---------------------------------------------------------------------------

        /// <summary>Every battle log in the folder, newest first, filtered by result; clicking one opens it.</summary>
        private Control BuildMatches()
        {
            var (page, stack) = Page(null);
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 4, 0, 12) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(new Label { Text = "Matches", AutoSize = true, Font = _pageFont, Margin = Padding.Empty }, 0, 0);
            var filters = new List<(Button Button, string Key)>();
            void Restyle() { foreach (var (b, k) in filters) Style(b, k == _filter ? Look.Selected : Look.Neutral, Color.Empty); }
            foreach (var (label, key) in new[] { ("All", "all"), ("Victories", "VICTORY"), ("Defeats", "DEFEAT") })
            {
                var b = new Button { Text = label, AutoSize = true, MinimumSize = new Size(0, 28), Margin = new Padding(6, 0, 0, 0) };
                b.Click += (s, e) => { _filter = key; Restyle(); Sync(); };
                filters.Add((b, key));
                top.Controls.Add(b, filters.Count, 0);
            }
            Restyle();
            stack.Controls.Add(top);

            var card = NewCard();
            card.Controls.Add(MatchList(() => _d.Matches.Where(m => _filter == "all" || m.Result == _filter).Take(MaxListedMatches),
                                        "No battle log here yet."));
            var more = new Label { AutoSize = true, ForeColor = Palette.Secondary, Margin = new Padding(0, 8, 0, 0), Text = "Older matches are in your logs folder." };
            card.Controls.Add(more);
            void Apply() => more.Visible = _d.Matches.Count(m => _filter == "all" || m.Result == _filter) > MaxListedMatches;
            Apply();
            _syncs.Add(Apply);
            stack.Controls.Add(card);
            return page;
        }

        /// <summary>A list of match rows, rebuilt when the matches change; the text when there are none.</summary>
        private Control MatchList(Func<IEnumerable<Match>> source, string empty)
        {
            // A one-column table: its rows stretch to the card's width.
            var list = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0) };
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var none = new Label { Text = empty, AutoSize = true, ForeColor = Palette.Secondary, Margin = new Padding(0, 6, 0, 2) };
            string key = null;
            void Apply()
            {
                // Built only while shown (a hidden page catches up when it shows), rebuilt only when the
                // logs change: each row reads its time when it paints.
                if (!list.Visible) return;
                var matches = source().ToList();
                var k = string.Join("|", matches.Select(m => m.Path));
                if (k == key) { foreach (Control c in list.Controls) c.Invalidate(); return; }
                key = k;
                list.SuspendLayout();
                foreach (Control c in list.Controls.Cast<Control>().ToList()) if (c != none) c.Dispose();
                list.Controls.Clear();
                if (matches.Count == 0) list.Controls.Add(none);
                foreach (var m in matches)
                {
                    var row = new MatchRow { Result = m.Result, Opponent = m.Opponent, LocalTime = m.LocalTime, Height = 34, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
                    row.Click += (s, e) => _openLog(m.Path);
                    list.Controls.Add(row);
                }
                list.ResumeLayout(true);
            }
            list.VisibleChanged += (s, e) => Apply();
            _syncs.Add(Apply);
            return list;
        }

        // ---- tcgkb -----------------------------------------------------------------------------

        /// <summary>Sign in; or the steps to a row on the ladder, the accounts with what "verified" means, and the connection.</summary>
        private Control BuildTcgkb(Items items)
        {
            var (page, stack) = Page("tcgkb");

            var intro = NewCard();
            intro.Padding = new Padding(20, 18, 20, 18);
            intro.Controls.Add(new Label { Text = "Sign in once. That's it.", AutoSize = true, Font = _strongFont, Margin = Padding.Empty });
            intro.Controls.Add(new Label
            {
                Text = "Your browser opens tcgkb.app. Approve the code and every match goes up by itself, with every PTCGL account you play on this PC. " +
                       "The app never sees your password.",
                AutoSize = true, ForeColor = Palette.Secondary, MaximumSize = new Size(TextWidth + 80, 0), Margin = new Padding(0, 6, 0, 12),
            });
            intro.Controls.Add(MirrorButton(items.TcgkbConnect, Padding.Empty, Look.Accent));
            stack.Controls.Add(intro);

            var account = BuildCard(items.Tcgkb, items.Tcgkb.DropDownItems.Cast<ToolStripItem>().ToArray());

            // The ladder: three steps, the last with the matches still to play.
            var ladder = NewCard();
            ladder.Controls.Add(Section("Your place on the ladder", new Padding(0, 0, 0, 4)));
            var step1 = new Step { Title = "Signed in to tcgkb", Detail = "Only players with a tcgkb account get a row." };
            var step2 = new Step();
            var step3 = new Step { WithBar = true };
            foreach (var s in new[] { step1, step2, step3 }) { s.Build(this); ladder.Controls.Add(s.Root); }
            var open = Link("Open the ladder on tcgkb.app", () => _openUrl(LadderUrl));
            open.Margin = new Padding(30, 6, 0, 0);
            ladder.Controls.Add(open);
            stack.Controls.Add(ladder);

            // The accounts and what verified means.
            var accounts = NewCard();
            accounts.Controls.Add(Section("Your PTCGL accounts", new Padding(0, 0, 0, 4)));
            var rows = Column();
            accounts.Controls.Add(rows);
            var box = new Tile { Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0), Padding = new Padding(14, 12, 14, 12) };
            box.Controls.Add(new Label { Text = "What does verified mean?", AutoSize = true, Font = _strongFont, BackColor = Palette.Raise, Margin = new Padding(0, 0, 0, 4) });
            box.Controls.Add(new Label
            {
                Text = "An account is verified once someone you played, who also uses tcgkb, uploads the same match from their side. It proves the account is really yours.\n\n" +
                       "You don't need it to be on the ladder: verified only adds the check next to your name. If another tcgkb user ever claims the same PTCGL " +
                       "account, the one with a verified match keeps it.",
                AutoSize = true, ForeColor = Palette.Muted, BackColor = Palette.Raise, MaximumSize = new Size(TextWidth + 80, 0), Margin = Padding.Empty,
            });
            accounts.Controls.Add(box);
            stack.Controls.Add(accounts);
            stack.Controls.Add(account);   // the connection itself last: the ladder is what players come here for

            string rowKey = null;
            void Apply()
            {
                bool on = _d.UserName != null;
                intro.Visible = !on;
                account.Visible = on;
                ladder.Visible = accounts.Visible = on;
                if (!on) return;

                var list = _d.Me?.Accounts;
                var l = LadderStatus.From(_d.Me);
                step1.Set(true, null, null);
                bool hasAccount = l?.HasAccount ?? list?.Any(a => a.Status != TcgkbAccount.Disputed) ?? false;
                step2.Set(hasAccount, "PTCGL account added",
                          hasAccount ? "Every account you play on this PC counts as yours, merged into one row."
                                     : "Added by itself with your first uploaded match.");
                if (l != null) step3.Set(l.Ranked, l.Step, l.Why, l.Progress);
                else step3.Set(false, "Play 5 matches", "Until then you show up at the bottom of the ladder as \"Not ranked yet\", so a few lucky wins can't top the board.", 0);

                var key = list == null ? null : string.Join("|", list.Select(a => a.Name + ":" + a.Status));
                if (key == rowKey) return;
                rowKey = key;
                rows.SuspendLayout();
                rows.Controls.Clear();
                if (list == null || list.Count == 0)
                    rows.Controls.Add(new Label
                    {
                        Text = list == null ? "Asking tcgkb..." : "None yet: your account is added with your first uploaded match.",
                        AutoSize = true, ForeColor = Palette.Secondary, Margin = new Padding(0, 4, 0, 4),
                    });
                else foreach (var a in list) rows.Controls.Add(AccountRow(a));
                rows.ResumeLayout(true);
            }
            Apply();
            _syncs.Add(Apply);
            return page;
        }

        private Control AccountRow(TcgkbAccount a)
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 6), Width = TextWidth + 80 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = Column();
            text.Controls.Add(new Label { Text = a.Name ?? "PTCGL account", AutoSize = true, UseMnemonic = false, Margin = Padding.Empty });
            text.Controls.Add(new Label
            {
                Text = a.Status == TcgkbAccount.Verified ? "Confirmed by an opponent"
                     : a.Status == TcgkbAccount.Disputed ? "Another tcgkb user claims it too: it counts for no one until one of you has a verified match"
                     : "Verified once an opponent on tcgkb uploads the same match",
                AutoSize = true, ForeColor = Palette.Secondary, MaximumSize = new Size(TextWidth - 40, 0), Margin = new Padding(0, 2, 0, 0),
            });
            row.Controls.Add(text, 0, 0);
            row.Controls.Add(new Chip { Text = StatusWord(a.Status), Highlight = a.Status == TcgkbAccount.Verified, Warn = a.Status == TcgkbAccount.Disputed, Anchor = AnchorStyles.Right, Margin = new Padding(8, 2, 0, 0) }, 1, 0);
            return row;
        }

        private static string StatusWord(string status) =>
            status == TcgkbAccount.Verified ? "verified" : status == TcgkbAccount.Disputed ? "disputed" : "not verified";

        /// <summary>A step towards the ladder: a check or a dot, a line, its explanation and, for the last, a progress bar.</summary>
        private sealed class Step
        {
            public string Title, Detail;
            public bool WithBar;
            public Control Root;
            private Mark _mark;
            private Label _title, _detail;
            private Bar _bar;

            public void Build(StatusWindow w)
            {
                var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 6), Padding = Padding.Empty };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                _mark = new Mark { Size = new Size(20, 20), Margin = new Padding(0, 0, 10, 0) };
                row.Controls.Add(_mark, 0, 0);
                var text = Column();
                _title = new Label { AutoSize = true, Text = Title, Margin = Padding.Empty };
                _detail = new Label { AutoSize = true, Text = Detail, ForeColor = Palette.Secondary, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 2, 0, 0) };
                text.Controls.Add(_title);
                text.Controls.Add(_detail);
                if (WithBar)
                {
                    _bar = new Bar { Size = new Size(260, 6), Margin = new Padding(0, 8, 0, 0) };
                    text.Controls.Add(_bar);
                }
                row.Controls.Add(text, 1, 0);
                Root = row;
            }

            public void Set(bool done, string title, string detail, double progress = 0)
            {
                _mark.Done = done;
                if (title != null) _title.Text = title;
                if (detail != null) _detail.Text = detail;
                if (_bar != null) { _bar.Value = progress; _bar.Visible = !done; }
            }
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
                    target.Add(MirrorToggle(m));
                }
                else if (m.HasDropDownItems)
                {
                    row = null;
                    var group = Column();
                    group.Margin = new Padding(0, 6, 0, 6);
                    var heading = new Label { AutoSize = true, Font = _strongFont, Margin = new Padding(0, 0, 0, 4) };
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

        private ToggleSwitch MirrorToggle(ToolStripMenuItem m)
        {
            var toggle = new ToggleSwitch { AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
            toggle.Click += (s, e) => _click(m);
            Sync(m, toggle, () => { toggle.Text = m.Text; toggle.Checked = m.Checked; });
            return toggle;
        }

        /// <param name="look">The button's look; by default the item's: red (needs the player) is filled red, the menu's bold item is the accent.</param>
        private Button MirrorButton(ToolStripMenuItem m, Padding margin, Look? look = null)
        {
            var button = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 32), Padding = new Padding(8, 0, 8, 0), Margin = margin };
            Style(button, look ?? (!m.ForeColor.IsSystemColor ? Look.Attention : m.Font.Bold ? Look.Accent : Look.Neutral), m.ForeColor);
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

        private enum Look { Neutral, Accent, Attention, Selected }

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
            button.Cursor = Cursors.Hand;
            if (Palette.Plain)
            {
                // High contrast: system buttons, the attention item in bold red as in the menu, the selected filter in bold.
                if (look == Look.Attention) { button.ForeColor = attention; button.Font = _boldFont; }
                button.Font = look == Look.Selected || look == Look.Attention ? _boldFont : Font;
                return;
            }
            switch (look)
            {
                case Look.Accent:
                    StyleFlat(button, Palette.Accent, Palette.OnAccent, Palette.Accent, Blend(Palette.Accent, Color.White, 0.2), Blend(Palette.Accent, Color.Black, 0.15));
                    button.Font = _boldFont;
                    break;
                case Look.Attention:
                    StyleFlat(button, attention, Color.White, attention, Blend(attention, Color.White, 0.12), Blend(attention, Color.Black, 0.15));
                    break;
                case Look.Selected:
                    StyleFlat(button, Palette.Selected, Palette.Text, Palette.Edge, Palette.Selected, Palette.Selected);
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

        private static Card NewCard() => new Card { Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 12) };

        /// <summary>A card's small upper-case heading.</summary>
        private Label Section(string text, Padding margin) => new Label
        {
            Text = text.ToUpperInvariant(), AutoSize = true, Font = _sectionFont, ForeColor = Palette.Secondary, UseMnemonic = false, Margin = margin,
        };

        private static LinkLabel Link(string text, Action onClick)
        {
            var link = new LinkLabel
            {
                Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 0), UseMnemonic = false,
                LinkColor = Palette.Accent, ActiveLinkColor = Palette.Accent, VisitedLinkColor = Palette.Accent, LinkBehavior = LinkBehavior.HoverUnderline,
            };
            link.LinkClicked += (s, e) => onClick();
            return link;
        }

        /// <summary>"2 min ago", "3 h ago", "Yesterday", "Monday", "12 Oct".</summary>
        internal static string When(DateTime local)
        {
            var now = DateTime.Now;
            var ago = now - local;
            if (ago < TimeSpan.FromMinutes(1)) return "Just now";
            if (ago < TimeSpan.FromHours(1)) return (int)ago.TotalMinutes + " min ago";
            if (local.Date == now.Date) return (int)ago.TotalHours + " h ago";
            if (local.Date == now.Date.AddDays(-1)) return "Yesterday";
            if (ago < TimeSpan.FromDays(6)) return local.ToString("dddd", CultureInfo.CurrentCulture);
            return local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);
        }

        /// <summary>Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10); null without either: the navigation shows no icons.</summary>
        private Font IconFont(float size)
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                var font = new Font(name, size);
                if (font.Name == name) return Track(font);
                font.Dispose();
            }
            return null;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        /// <summary>A dark title bar to match (Windows 10 1809 and later; attribute 19 before 20H1). Nothing elsewhere.</summary>
        private static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch (Exception) { }
        }

        /// <summary>Dark scroll bars on a scrolling page (Windows 10 1809 and later); the light ones elsewhere.</summary>
        private static void DarkScrollBars(IntPtr hwnd)
        {
            try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); }
            catch (Exception) { }
        }

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

        /// <summary>Dark, with tcgkb's yellow as the accent; the system colours in high contrast mode.</summary>
        private static class Palette
        {
            public static readonly bool Plain = SystemInformation.HighContrast;
            public static readonly Color Surface = Plain ? SystemColors.Control : Color.FromArgb(0x12, 0x16, 0x1C);
            public static readonly Color Card = Plain ? SystemColors.Control : Color.FromArgb(0x17, 0x1C, 0x23);
            public static readonly Color Raise = Plain ? SystemColors.Control : Color.FromArgb(0x1E, 0x24, 0x2D);
            public static readonly Color Border = Plain ? SystemColors.ControlText : Color.FromArgb(0x23, 0x2A, 0x34);
            public static readonly Color Edge = Plain ? SystemColors.ControlText : Color.FromArgb(0x2E, 0x36, 0x44);
            public static readonly Color Text = Plain ? SystemColors.ControlText : Color.FromArgb(0xE8, 0xEC, 0xF2);
            public static readonly Color Muted = Plain ? SystemColors.ControlText : Color.FromArgb(0xC9, 0xD1, 0xDB);
            public static readonly Color Secondary = Plain ? SystemColors.ControlText : Color.FromArgb(0x9A, 0xA4, 0xB2);
            public static readonly Color Accent = Plain ? SystemColors.Highlight : Color.FromArgb(0xF5, 0xC5, 0x18);
            public static readonly Color OnAccent = Plain ? SystemColors.HighlightText : Color.FromArgb(0x14, 0x17, 0x1C);
            public static readonly Color Hover = Plain ? SystemColors.Control : Color.FromArgb(0x1A, 0x20, 0x28);
            public static readonly Color Selected = Plain ? SystemColors.Highlight : Color.FromArgb(0x2C, 0x34, 0x40);
            public static readonly Color SelectedText = Plain ? SystemColors.HighlightText : Text;
            public static readonly Color ButtonFace = Plain ? SystemColors.Control : Color.FromArgb(0x17, 0x1C, 0x23);
            public static readonly Color ButtonBorder = Plain ? SystemColors.ControlText : Edge;
            public static readonly Color SwitchOff = Plain ? SystemColors.ControlText : Color.FromArgb(0x8A, 0x94, 0xA3);
            public static readonly Color Disabled = Plain ? SystemColors.GrayText : Color.FromArgb(0x4A, 0x55, 0x66);
            public static readonly Color Track = Plain ? SystemColors.ControlDark : Color.FromArgb(0x2C, 0x34, 0x40);
            public static readonly Color Good = Plain ? SystemColors.ControlText : Color.FromArgb(0x56, 0xD3, 0x64);
            public static readonly Color Bad = Plain ? SystemColors.ControlText : Color.FromArgb(0xF4, 0x70, 0x67);
        }

        // ---- Controls --------------------------------------------------------------------------

        /// <summary>A rounded card on the window's surface: a one-column table whose rows size to their content.</summary>
        private class Card : TableLayoutPanel
        {
            private readonly Color _outer, _fill;
            private readonly bool _border;

            public Card() : this(Palette.Surface, Palette.Card, true) { }

            protected Card(Color outer, Color fill, bool border)
            {
                _outer = outer;
                _fill = fill;
                _border = border;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                ColumnCount = 1;
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                Padding = new Padding(16, 14, 16, 12);
                BackColor = outer;   // the corners; the card itself is painted below
            }

            protected override void OnControlAdded(ControlEventArgs e)
            {
                base.OnControlAdded(e);
                // Children sit inside the padding, on the card; one with its own colour (a divider, a tile) keeps it.
                if (e.Control.BackColor == _outer) e.Control.BackColor = _fill;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var outer = new SolidBrush(_outer)) g.FillRectangle(outer, ClientRectangle);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(6, Font.Height * 2 / 3)))
                {
                    using (var fill = new SolidBrush(_fill)) g.FillPath(fill, path);
                    if (_border) using (var pen = new Pen(Palette.Border)) g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>A raised block inside a card: a number tile, the "verified" explanation.</summary>
        private sealed class Tile : Card
        {
            public Tile() : base(Palette.Card, Palette.Raise, Palette.Plain) { Padding = new Padding(10, 8, 10, 8); }
        }

        /// <summary>Paints itself; never takes the focus.</summary>
        private abstract class Painted : Control
        {
            protected Painted()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }

            protected Graphics Begin(PaintEventArgs e)
            {
                using (var back = new SolidBrush(BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                return e.Graphics;
            }
        }

        /// <summary>The status dot in the tray icon's colour, in a faint ring of the same colour.</summary>
        private sealed class Halo : Painted
        {
            private Color _fill = Color.Gray;

            public Color Fill
            {
                get => _fill;
                set { if (_fill != value) { _fill = value; Invalidate(); } }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = Begin(e);
                int d = Math.Min(Width, Height) - 1;
                using (var ring = new SolidBrush(Palette.Plain ? BackColor : Color.FromArgb(40, _fill))) g.FillEllipse(ring, 0, 0, d, d);
                int dot = d / 3;
                using (var fill = new SolidBrush(_fill)) g.FillEllipse(fill, (d - dot) / 2f, (d - dot) / 2f, dot, dot);
            }
        }

        /// <summary>A thin progress bar, Value from 0 to 1.</summary>
        private sealed class Bar : Painted
        {
            private double _value;

            public double Value
            {
                get => _value;
                set { value = Math.Max(0, Math.Min(1, value)); if (_value != value) { _value = value; Invalidate(); } }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = Begin(e);
                var track = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = RoundRect(track, Height / 2))
                using (var brush = new SolidBrush(Palette.Track)) g.FillPath(brush, path);
                int w = (int)Math.Round((Width - 1) * _value);
                if (w <= 0) return;
                using (var path = RoundRect(new Rectangle(0, 0, Math.Max(w, Height), Height - 1), Height / 2))
                using (var brush = new SolidBrush(Palette.Accent)) g.FillPath(brush, path);
            }
        }

        /// <summary>A step's mark: a green check when done, a yellow dot to do.</summary>
        private sealed class Mark : Painted
        {
            private bool _done;

            public bool Done
            {
                get => _done;
                set { if (_done != value) { _done = value; Invalidate(); } }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = Begin(e);
                int d = Math.Min(Width, Height) - 1;
                var color = _done ? Palette.Good : Palette.Accent;
                using (var ring = new SolidBrush(Palette.Plain ? BackColor : Color.FromArgb(45, color))) g.FillEllipse(ring, 0, 0, d, d);
                using (var pen = new Pen(color, Math.Max(1.6f, d / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (_done) g.DrawLines(pen, new[] { new PointF(d * 0.3f, d * 0.52f), new PointF(d * 0.45f, d * 0.66f), new PointF(d * 0.72f, d * 0.36f) });
                    else using (var dot = new SolidBrush(color)) g.FillEllipse(dot, d * 0.38f, d * 0.38f, d * 0.24f, d * 0.24f);
                }
            }
        }

        /// <summary>A pill with a short text: an account and its verified state.</summary>
        private sealed class Chip : Label
        {
            public bool Highlight, Warn;

            public Chip()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AutoSize = true;
                UseMnemonic = false;
                Padding = new Padding(9, 3, 9, 4);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
                var color = Highlight ? Palette.Accent : Warn ? Palette.Bad : Palette.Secondary;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
                {
                    using (var fill = new SolidBrush(Palette.Raise)) g.FillPath(fill, path);
                    using (var pen = new Pen(Highlight || Warn ? Color.FromArgb(Palette.Plain ? 255 : 130, color) : Palette.Edge)) g.DrawPath(pen, path);
                }
                g.SmoothingMode = SmoothingMode.Default;
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, Highlight || Warn ? color : Palette.Muted,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }

        /// <summary>A match in a list: the result in a coloured pill, the opponent, when. A button to keyboards and screen readers.</summary>
        private sealed class MatchRow : Button
        {
            private bool _hover;
            public string Result, Opponent;
            public DateTime LocalTime;
            private string When => StatusWindow.When(LocalTime);

            public MatchRow()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                Text = Result + " vs " + Opponent + ", " + When;   // what a screen reader says
                AccessibleDescription = "Opens the battle log";
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var back = new SolidBrush(_hover ? Palette.Hover : BackColor)) g.FillRectangle(back, ClientRectangle);
                using (var line = new Pen(Palette.Border)) g.DrawLine(line, 0, 0, Width, 0);

                bool won = Result == "VICTORY", lost = Result == "DEFEAT";
                var color = won ? Palette.Good : lost ? Palette.Bad : Palette.Secondary;
                int pillW = Font.Height * 5, pillH = Font.Height + 6;
                var pill = new Rectangle(6, (Height - pillH) / 2, pillW, pillH);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundRect(pill, 6))
                using (var fill = new SolidBrush(Palette.Plain ? BackColor : Color.FromArgb(36, color))) g.FillPath(fill, path);
                g.SmoothingMode = SmoothingMode.Default;
                using (var bold = new Font(Font.FontFamily, Font.Size * 0.85f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Result, bold, pill, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                int whenW = TextRenderer.MeasureText(When ?? "", Font).Width + 8;
                var whenBox = new Rectangle(Width - whenW - 6, 0, whenW, Height);
                TextRenderer.DrawText(g, When, Font, whenBox, Palette.Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                int x = pill.Right + 12;
                int vsW = TextRenderer.MeasureText(g, "vs", Font, Size.Empty, TextFormatFlags.NoPadding).Width + Font.Height / 3;
                TextRenderer.DrawText(g, "vs", Font, new Point(x, (Height - Font.Height) / 2), Palette.Secondary, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Opponent, Font, new Rectangle(x + vsW, 0, Math.Max(0, whenBox.Left - x - vsW - 8), Height), Palette.Text,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2));
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
            /// <summary>An icon font character drawn left of the text; none without GlyphFont.</summary>
            public string Glyph;
            public Font GlyphFont;

            public NavButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AutoSize = false;
                Cursor = Cursors.Hand;
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
                    using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(4, Height / 5)))
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
                var color = Checked ? Palette.SelectedText : Palette.Muted;
                if (GlyphFont != null && !string.IsNullOrEmpty(Glyph))
                {
                    TextRenderer.DrawText(g, Glyph, GlyphFont, new Rectangle(indent, 0, Height * 2 / 3, Height), color,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    indent += Height * 2 / 3 + 2;
                }
                using (var font = Checked && !Palette.Plain ? new Font(Font, FontStyle.Bold) : null)
                    TextRenderer.DrawText(g, Text, font ?? Font, new Rectangle(indent, 0, Math.Max(0, Width - indent), Height),
                                      color,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.HidePrefix);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2));
            }
        }
    }
}
