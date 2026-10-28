using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using PtcglAutoBattleLog.Core;
using UnityEngine;

namespace PtcglAutoBattleLog
{
    /// <summary>
    /// Saves the battle log of every Pokémon TCG Live match to a folder, with no clicks.
    ///
    /// Runs inside the game (BepInEx 5). It does not touch the UI, does not simulate input and
    /// sends nothing anywhere: at the end of each match it asks the client's own exporter for the
    /// same text the Export button would copy, and writes it to disk, sealed with a signature
    /// (docs/LOG-SEAL.md) unless [Metadata] Mode is Off.
    /// </summary>
    [BepInPlugin(Guid, Name, BuildInfo.VersionCore)]   // BepInEx parses X.Y.Z only; Version may be 1.3.2-beta.1
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = PluginConfig.Guid;
        public const string Name = "PTCGL Auto Battle Log";
        public const string Version = BuildInfo.Version;

        internal static ManualLogSource Log;

        private ConfigEntry<string> _folder;
        private ConfigEntry<bool> _manual;
        private ConfigEntry<SealMode> _sealMode;
        private ConfigEntry<bool> _dumpFields;
        private Harmony _harmony;
        private GameObject _host;
        private static bool _quitting;

        private void Awake()
        {
            Log = Logger;

            // The default is empty, not the path: BepInEx cannot keep every path in its cfg (see LogsFolder).
            var defaultFolder = LogsFolder.DefaultFor(DocumentsFolder());
            _folder = Config.Bind(PluginConfig.GeneralSection, PluginConfig.OutputFolderKey, PluginConfig.OutputFolderDefault,
                "Folder where every battle log is saved, one .txt per match plus index.csv. Empty: Documents\\" + LogsFolder.DefaultFolderName + ".");
            _manual = Config.Bind(PluginConfig.GeneralSection, PluginConfig.SaveManualExportsKey, PluginConfig.SaveManualExportsDefault,
                "Also save the log when you press Export in game (merged into the same match file).");
            _sealMode = Config.Bind(PluginConfig.MetadataSection, PluginConfig.SealModeKey, PluginConfig.SealModeDefault,
                "Signed metadata (a \"seal\") added to every battle log, so a leaderboard can tell whether the log was edited after it was saved. " +
                "Footer: at the end of the .txt. Sidecar: in a .seal file next to it. Off: no seal.");
            _dumpFields = Config.Bind(PluginConfig.DiagnosticsSection, PluginConfig.DumpMatchFieldsKey, PluginConfig.DumpMatchFieldsDefault,
                "Research aid: write the fields of the game's match objects to LogOutput.log at the start and end of every match " +
                "(to find which queue a match came from). Player names and account ids are hidden. Leave off otherwise.");
            var folder = ResolveFolder(defaultFolder);

            // Unity's own value; read here, on the main thread.
            string gameVersion = null;
            try { gameVersion = Application.version; } catch { }

            _harmony = new Harmony(Guid);
            ClipboardGuard.Install(_harmony);
            Application.quitting += () => _quitting = true;

            // Own object: some games destroy BepInEx's manager object (which this component lives on)
            // after the first scene. The runner, its patches and its host outlive it; see OnDestroy.
            _host = new GameObject("PtcglAutoBattleLog");
            _host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(_host);
            var runner = _host.AddComponent<Runner>();
            var mode = _sealMode;
            runner.Init(folder, _manual.Value, () => mode.Value, gameVersion, _dumpFields.Value);

            Log.LogInfo(Name + " " + Version + " loaded. Logs go to: " + folder + " (seal: " + _sealMode.Value + ")");
        }

        /// <summary>The folder logs go to this session; a garbled or unusable setting falls back to the default.</summary>
        private string ResolveFolder(string defaultFolder)
        {
            string gameFolder = null;
            try { gameFolder = Paths.GameRootPath; } catch { }
            var choice = LogsFolder.Choose(_folder.Value, defaultFolder, BepInExRoundTrip, TryCreateFolder, gameFolder);
            if (choice.Warning != null) Log.LogWarning(choice.Warning);
            if (choice.Note != null) Log.LogInfo(choice.Note);
            if (choice.ClearSetting)
            {
                // Saved by BepInEx at once: the next start reads "" (the default folder).
                try { _folder.Value = ""; }
                catch (Exception e) { Log.LogWarning("Could not clear OutputFolder: " + e.Message); }
            }
            return choice.Folder;
        }

        private static string DocumentsFolder()
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs)) return docs;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        }

        /// <summary>What a string setting becomes after BepInEx writes it to the cfg and reads it back.</summary>
        private static string BepInExRoundTrip(string value)
        {
            var written = TomlTypeConverter.ConvertToString(value, typeof(string));
            return (string)TomlTypeConverter.ConvertToValue(written.Trim(), typeof(string));
        }

        private static string TryCreateFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); return null; }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>
        /// Runs when BepInEx's manager object goes away: at quit, or because the game destroyed it.
        /// The runner's own object, and the clipboard patch it relies on, must survive the latter,
        /// which is the reason that object exists; at quit Unity destroys it anyway. So nothing is
        /// torn down here.
        /// </summary>
        private void OnDestroy()
        {
            if (!_quitting) Log?.LogInfo("BepInEx's manager object was destroyed; " + Name + " keeps running on its own object.");
        }
    }

    /// <summary>
    /// Polls the match twice a second on Unity's main thread, and keeps plugin-status.json
    /// current for the tray app.
    /// </summary>
    internal class Runner : MonoBehaviour
    {
        private const float Interval = 0.5f;
        /// <summary>The game loads its assemblies late; only after this long is "not found" an error.</summary>
        private const float MissingAfterSeconds = 90f;

        private ReflectionGameApi _api;
        private MatchWatcher _watcher;
        private LogStore _store;
        private PluginStatusWriter _status;
        private Timer _heartbeat;
        private float _startedAt;
        private bool _reportedMissing;
        private string _lastTickError;

        public void Init(string folder, bool saveManual, Func<SealMode> sealMode, string gameVersion, bool dumpMatchFields)
        {
            _store = new LogStore(folder);
            _store.MigrateLegacy(m => Plugin.Log.LogInfo(m));
            _store.Sealer = CreateSealer(folder, sealMode, gameVersion);
            _api = new ReflectionGameApi(m => Plugin.Log.LogInfo(m), new ClipboardGuard());
            _api.FallbackExportData = FindMenuExportData;
            _api.DumpMatchFields = dumpMatchFields;
            _watcher = new MatchWatcher(_api, _store, m => Plugin.Log.LogInfo(m)) { SaveManualExports = saveManual };
            _startedAt = Time.unscaledTime;

            _status = CreateStatusWriter(folder);
            var identity = _store.Sealer?.Identity;
            if (_status != null || identity != null)
            {
                if (_status != null) ReportStatus(DateTime.UtcNow);
                // Off the main thread: Unity may stop running coroutines while the game is minimized.
                // Also writes chain.json again if its last write failed, so it is not left behind its logs.
                var status = _status;
                _heartbeat = new Timer(_ =>
                {
                    status?.Heartbeat(DateTime.UtcNow);
                    identity?.RetryChain();
                }, null, 5000, 5000);
            }
            StartCoroutine(Loop());
        }

        /// <summary>
        /// The key is loaded (or created: seconds on Mono) on a background thread right away, so it
        /// is ready long before the first match ends. With Mode Off it is not touched until a seal
        /// is wanted.
        /// </summary>
        private static LogSealer CreateSealer(string folder, Func<SealMode> sealMode, string gameVersion)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var identity = string.IsNullOrEmpty(localAppData) ? null
                : new SigningIdentity(SigningIdentity.FolderFor(localAppData), RsaBackend.Default,
                                      m => Plugin.Log.LogInfo(m), m => Plugin.Log.LogWarning(m));
            var sealer = new LogSealer(identity, Plugin.Version, gameVersion, m => Plugin.Log.LogWarning(m))
            {
                Mode = sealMode,
                RecoveryFolder = folder,
            };
            if (identity != null && sealer.CurrentMode() != SealMode.Off) identity.BeginLoad(folder);
            return sealer;
        }

        private static PluginStatusWriter CreateStatusWriter(string folder)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData)) return null;
            int pid = 0;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            return new PluginStatusWriter(PluginStatusWriter.PathFor(localAppData), Plugin.Version, folder, pid,
                                          m => Plugin.Log.LogWarning(m));
        }

        private IEnumerator Loop()
        {
            var wait = new WaitForSecondsRealtime(Interval);
            while (true)
            {
                yield return wait;
                var now = DateTime.UtcNow;
                try
                {
                    if (_api.Resolve())
                    {
                        _watcher.Tick(now);
                        _lastTickError = null;
                    }
                    else if (!_reportedMissing && Time.unscaledTime - _startedAt > MissingAfterSeconds)
                    {
                        _reportedMissing = true;
                        Plugin.Log.LogWarning("Game classes not found yet - the game may have changed. Candidates: " + _api.DescribeCandidates());
                    }
                }
                catch (Exception e)
                {
                    // Once per distinct failure, not twice a second.
                    if (e.Message != _lastTickError) Plugin.Log.LogWarning("tick failed: " + e);
                    _lastTickError = e.Message;
                }
                ReportStatus(now);
            }
        }

        private void ReportStatus(DateTime nowUtc)
        {
            if (_status == null) return;
            string state, message;
            if (!_api.Resolved)
            {
                state = _reportedMissing ? PluginStatusWriter.Error : PluginStatusWriter.Starting;
                message = _reportedMissing
                    ? "The game's classes were not found after " + (int)MissingAfterSeconds + " s; the game may have changed. " +
                      "See " + Path.Combine("BepInEx", "LogOutput.log") + " in the game folder."
                    : "Waiting for the game to load.";
            }
            else if (_watcher.LastError != null)
            {
                state = PluginStatusWriter.Error;
                message = "Could not save the last battle log to " + _store.Folder + ": " + _watcher.LastError;
            }
            else
            {
                state = PluginStatusWriter.Ok;
                message = "Watching for matches.";
                // Still ok: the logs are saved. But a leaderboard will not accept them unsealed.
                var sealWarning = _store.Sealer == null ? null : _store.Sealer.Warning;
                if (sealWarning != null) message += " " + sealWarning;
            }
            _status.Update(nowUtc, state, message, _watcher.SavedCount, _watcher.LastSavedPath, _watcher.LastSavedUtc);
        }

        /// <summary>
        /// Fallback when MatchManager has no data: the Battle Log screen keeps its own copy once
        /// it has been opened.
        /// </summary>
        private object FindMenuExportData()
        {
            var menuType = _api.FindType("_Rainier.Scripts.BattleLog.BattleLogMenu", "BattleLogMenu");
            if (menuType == null) return null;
            foreach (var o in Resources.FindObjectsOfTypeAll(menuType))
            {
                var menu = o as Component;
                if (menu == null || !menu.gameObject.scene.IsValid()) continue;
                for (var t = menu.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                  BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(f.FieldType)) continue;
                        if (f.FieldType.FullName == null || f.FieldType.FullName.IndexOf("BattleLog", StringComparison.Ordinal) < 0) continue;
                        var v = f.GetValue(menu);
                        if (v != null) return v;
                    }
                }
            }
            return null;
        }

        private void OnDestroy()
        {
            _heartbeat?.Dispose();
            _api?.Unhook();
            _store?.Sealer?.Identity?.RetryChain();   // the last chance for a chain.json write that failed
        }
    }

    /// <summary>
    /// The game's exporter puts the log on the system clipboard. While we run it ourselves the
    /// clipboard write is skipped (Harmony prefix on GUIUtility.systemCopyBuffer's setter), so the
    /// player's clipboard and Windows clipboard history never see it. If the patch could not be
    /// applied (or the JIT inlined the setter) the previous clipboard text is put back right after.
    /// </summary>
    internal sealed class ClipboardGuard : IClipboardGuard
    {
        private static bool _suppress;

        public static void Install(Harmony harmony)
        {
            try
            {
                var setter = AccessTools.PropertySetter(typeof(GUIUtility), nameof(GUIUtility.systemCopyBuffer));
                harmony.Patch(setter, prefix: new HarmonyMethod(typeof(ClipboardGuard), nameof(Prefix)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("clipboard patch failed, falling back to restore: " + e.Message);
            }
        }

        private static bool Prefix()
        {
            return !_suppress;
        }

        public IDisposable Begin()
        {
            string before = null;
            try { before = GUIUtility.systemCopyBuffer; } catch { }
            _suppress = true;
            return new Scope(before);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _before;
            public Scope(string before) { _before = before; }

            public void Dispose()
            {
                _suppress = false;
                // Checked even when patched: the JIT may have inlined the setter into the exporter.
                try
                {
                    if (GUIUtility.systemCopyBuffer != _before) GUIUtility.systemCopyBuffer = _before ?? "";
                }
                catch { }
            }
        }
    }
}
