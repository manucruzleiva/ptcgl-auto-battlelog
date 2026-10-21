using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;
using Timer = System.Windows.Forms.Timer;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>The "Pokematch" submenu and the automatic uploads (docs/ARCHITECTURE.md, "Pokematch").</summary>
    internal sealed partial class TrayApplicationContext
    {
        private static readonly TimeSpan PokematchFirstRun = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan PokematchInterval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PokematchRetry = TimeSpan.FromMinutes(30);

        private PokematchService _pokematch;
        private Timer _pokematchTimer;
        private bool _pokematchBusy, _pokematchTokenRejected;
        private string _pokematchLast, _pokematchWarned;
        private ToolStripMenuItem _pokematchItem, _pokematchInfoItem, _pokematchTokenItem, _pokematchAutoItem, _pokematchPastItem, _pokematchRemoveItem;

        /// <summary>Made on first use: a tray without a token sends nothing to Pokematch.</summary>
        private PokematchService Pokematch => _pokematch ?? (_pokematch = new PokematchService(_session));

        private ToolStripMenuItem BuildPokematchMenu()
        {
            _pokematchItem = new ToolStripMenuItem("Pokematch");
            _pokematchInfoItem = Info("Not set up");
            _pokematchTokenItem = Item("Set API token...", SetPokematchToken);
            _pokematchAutoItem = Item("Upload new battle logs automatically", TogglePokematchAuto);
            _pokematchPastItem = Item("Upload past battle logs...", UploadPastPokematch);
            _pokematchRemoveItem = Item("Remove API token", RemovePokematchToken);
            _pokematchItem.DropDownItems.AddRange(new ToolStripItem[] { _pokematchInfoItem, _pokematchTokenItem, _pokematchAutoItem, _pokematchPastItem, _pokematchRemoveItem });
            return _pokematchItem;
        }

        /// <summary>Called once the tray runs: the timer only ticks while automatic uploads are on.</summary>
        private void StartPokematch()
        {
            _pokematchTimer = new Timer();
            _pokematchTimer.Tick += (s, e) => { _pokematchTimer.Stop(); RunPokematch(manual: false); };
            if (PokematchAutoOn()) SchedulePokematch(PokematchFirstRun);
        }

        private bool HasPokematchToken() => Pokematch.Tokens.Load() != null;

        private bool PokematchAutoOn() =>
            _session.Read(s => s.PokematchAutoUpload) && System.IO.File.Exists(System.IO.Path.Combine(_session.Paths.Root, PokematchTokenStore.FileName));

        private void RefreshPokematchItems()
        {
            bool token = HasPokematchToken();
            bool auto = _session.Read(s => s.PokematchAutoUpload);
            string info;
            if (!token) info = "Not set up";
            else if (_pokematchTokenRejected) info = "Pokematch refused the token: set a new one";
            else if (_pokematchBusy) info = "Uploading...";
            else info = _pokematchLast ?? (auto ? "Uploads new battle logs" : "Automatic upload is off");
            _pokematchInfoItem.Text = MenuText(Clip(info, 80));
            _pokematchTokenItem.Text = token ? "Change API token..." : "Set API token...";
            _pokematchAutoItem.Visible = _pokematchPastItem.Visible = _pokematchRemoveItem.Visible = token;
            _pokematchAutoItem.Checked = auto;
            _pokematchPastItem.Enabled = !_pokematchBusy;
        }

        /// <summary>A log was saved: upload it once the plugin is done writing it.</summary>
        private void OnSavedForPokematch()
        {
            if (PokematchAutoOn() && !_pokematchTokenRejected) SchedulePokematch(PokematchUploader.SettleTime + TimeSpan.FromSeconds(15));
        }

        private void SchedulePokematch(TimeSpan after)
        {
            if (_pokematchTimer == null || _exiting) return;
            _pokematchTimer.Stop();
            _pokematchTimer.Interval = (int)Math.Max(1000, after.TotalMilliseconds);
            _pokematchTimer.Start();
        }

        private void SetPokematchToken()
        {
            string token;
            using (Modal()) token = PokematchTokenDialog.Ask();
            if (token == null) return;
            Pokematch.Tokens.Save(new PokematchToken { Token = token, SetUtc = DateTime.UtcNow });
            _pokematchTokenRejected = false;
            _pokematchWarned = null;
            Log.Info("Pokematch API token set.");
            if (_session.Read(s => s.PokematchAutoUpload)) { SchedulePokematch(TimeSpan.FromSeconds(1)); return; }
            TogglePokematchAuto();   // offers to turn automatic uploads on
        }

        private void TogglePokematchAuto()
        {
            if (_session.Read(s => s.PokematchAutoUpload))
            {
                _session.Update(s => s.PokematchAutoUpload = false);
                _pokematchTimer?.Stop();
                Log.Info("Pokematch automatic upload: off");
                return;
            }
            if (!HasPokematchToken()) { SetPokematchToken(); return; }
            bool yes;
            using (Modal())
                yes = Dialogs.Confirm("Upload your new battle logs to Pokematch automatically?\n\n" +
                                      "After each finished match (victory or defeat), the app sends Pokematch the battle log text, the result, " +
                                      "your opponent's display name, your deck name and when the match ended. The log text contains both players' " +
                                      "names and every play of the match.\n\n" +
                                      "Only matches played from now on are sent; \"Upload past battle logs...\" sends older ones. " +
                                      "Turn this off at any time in the Pokematch menu.", MessageBoxIcon.Question);
            if (!yes) return;
            var since = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _session.Update(s =>
            {
                s.PokematchAutoUpload = true;
                if (string.IsNullOrEmpty(s.PokematchSinceUtc)) s.PokematchSinceUtc = since;
            });
            Log.Info("Pokematch automatic upload: on");
            SchedulePokematch(TimeSpan.FromSeconds(5));
        }

        private async void UploadPastPokematch()
        {
            if (!HasPokematchToken()) { SetPokematchToken(); return; }
            if (_pokematchBusy) return;
            var folder = _session.LogsFolder(_session.Game);
            int count = await Task.Run(() => Pokematch.Uploader.CountNew(folder, DateTime.MinValue));
            bool yes;
            using (Modal())
            {
                if (count == 0)
                {
                    Dialogs.Info("Pokematch already has every finished match in your logs folder.\n\n" + PathUtil.Friendly(folder));
                    return;
                }
                yes = Dialogs.Confirm("Upload " + count + " battle log" + (count == 1 ? "" : "s") + " from your logs folder to Pokematch?\n\n" +
                                      "Each one sends the battle log text (both players' names and every play), the result, your opponent's " +
                                      "name, your deck name and when the match ended. Matches already on Pokematch are not sent again.",
                                      MessageBoxIcon.Question);
            }
            if (yes) RunPokematch(manual: true);
        }

        private void RemovePokematchToken()
        {
            bool yes;
            using (Modal())
                yes = Dialogs.Confirm("Remove the Pokematch API token from this computer? Nothing more is uploaded. Battle logs already on Pokematch stay there; " +
                                      "delete the token in Pokematch too if you no longer use it.", MessageBoxIcon.Question);
            if (!yes) return;
            Pokematch.Tokens.Delete();
            _session.Update(s => s.PokematchAutoUpload = false);
            _pokematchTimer?.Stop();
            _pokematchLast = null;
            _pokematchTokenRejected = false;
            Log.Info("Pokematch API token removed.");
        }

        /// <summary>
        /// One upload run: automatic (new logs since uploads were turned on) or manual (every log in
        /// the folder). Runs until nothing is left or Pokematch stops answering.
        /// </summary>
        private async void RunPokematch(bool manual)
        {
            if (_pokematchBusy || _exiting) return;
            if (!HasConsent())
            {
                if (!manual && PokematchAutoOn()) SchedulePokematch(PokematchInterval);   // until the updated disclaimer is accepted
                return;
            }
            if (!manual && (!PokematchAutoOn() || _pokematchTokenRejected)) return;
            var token = Pokematch.Tokens.Load()?.Token;
            if (token == null) return;

            var since = DateTime.MinValue;
            if (!manual && DateTime.TryParse(_session.Read(s => s.PokematchSinceUtc), CultureInfo.InvariantCulture,
                                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                since = parsed;
            var folder = _session.LogsFolder(_session.Game);

            _pokematchBusy = true;
            int uploaded = 0, updated = 0, rejected = 0;
            PokematchSyncResult r = null;
            try
            {
                do
                {
                    r = await Task.Run(() => Pokematch.Uploader.SyncAsync(token, folder, since, CancellationToken.None));
                    uploaded += r.Uploaded;
                    updated += r.Updated;
                    rejected += r.Rejected;
                } while (r.More && r.Waiting == 0 && !r.TokenRejected && !_exiting);
            }
            catch (Exception e)
            {
                Log.Error("Pokematch upload failed", e);
            }
            finally { _pokematchBusy = false; }
            if (_exiting || r == null) return;

            if (uploaded + updated + rejected > 0 || r.Error != null)
                Log.Info("Pokematch: " + uploaded + " uploaded, " + updated + " updated, " + rejected + " refused" + (r.Error != null ? " (" + r.Error + ")" : "") + ".");
            if (uploaded + updated > 0)
                _pokematchLast = "Last upload: " + (uploaded + updated) + " log" + (uploaded + updated == 1 ? "" : "s") + ", " + DateTime.Now.ToString("t");

            if (r.TokenRejected)
            {
                _pokematchTokenRejected = true;
                Balloon("Pokematch refused the API token", r.Error + " Click to set a new one.", ToolTipIcon.Warning, SetPokematchToken);
                return;
            }
            if (manual)
            {
                var text = (uploaded + updated) + " battle log" + (uploaded + updated == 1 ? "" : "s") + " sent to Pokematch." +
                           (rejected > 0 ? " " + rejected + " refused: " + r.Error : "") +
                           (r.Waiting > 0 ? " Pokematch did not answer; the rest " + (PokematchAutoOn() ? "is sent later." : "can be sent later.") : "");
                Balloon("Pokematch upload", text, rejected + r.Waiting > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info, null);
            }
            else if (rejected > 0 && r.Error != _pokematchWarned)
            {
                _pokematchWarned = r.Error;   // once per kind of refusal, not after every match
                Balloon("Pokematch refused a battle log", r.Error + " Details are in the app log.", ToolTipIcon.Warning, () => Shell.OpenText(Log.FilePath));
            }

            if (PokematchAutoOn()) SchedulePokematch(r.Waiting > 0 ? PokematchRetry : PokematchInterval);
        }
    }
}
