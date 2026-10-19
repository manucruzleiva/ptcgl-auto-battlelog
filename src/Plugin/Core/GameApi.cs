using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>What the watcher needs to know about the match on screen.</summary>
    public sealed class MatchSnapshot
    {
        public string MatchId;
        public bool GameOver;
        public int Winner;          // 1 = player 1, 2 = player 2, 0 = not decided
        public bool IAmPlayer1 = true;
        public int? MySeat;         // 1 or 2; null when the client does not say (IAmPlayer1 is then a guess)
        public string MyName;
        public string OpponentName;
        public string MyDeck;
        public string MyAccountId;
        public string OpponentAccountId;
    }

    public interface IGameApi
    {
        /// <summary>The live match, or null when no match is loaded.</summary>
        MatchSnapshot ReadMatch();

        /// <summary>The battle log text, exactly as the in-game Export button builds it, or null.</summary>
        string ExportBattleLog();

        /// <summary>Raised when the player presses Export in game (not for our own exports).</summary>
        event Action<string> ManualExport;
    }

    /// <summary>
    /// Lets the Unity side keep the system clipboard untouched while the game's exporter runs:
    /// the exporter writes to the clipboard as a side effect, and we only want its text.
    /// </summary>
    public interface IClipboardGuard
    {
        IDisposable Begin();
    }

    /// <summary>
    /// Reads the client's own match state and battle log by reflection.
    ///
    /// Nothing here is compiled against the game's DLLs. Every type and member is looked up by
    /// name at runtime, so a game update that moves a class to another assembly or namespace does
    /// not break the plugin, and one that renames a member degrades to "no log for this match"
    /// with a warning in the BepInEx log instead of a crash.
    ///
    /// The names come from the client itself (decompiled, see README):
    ///   NetworkMatchController.currentMatchInfo   -> MatchInfo (gameOver, winner, isPlayer1, ...)
    ///   NetworkMatchController.playerDetails      -> names, account ids and decks of both seats
    ///   MatchManager.instance.BattleLogExportData -> the data behind the Battle Log screen
    ///   _Rainier.Scripts.BattleLog.BattleLogExporter.ExportBattleLog(data) + static OnExport
    ///                                              -> the exact text the Export button copies
    /// </summary>
    public sealed class ReflectionGameApi : IGameApi
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private readonly Action<string> _log;
        private readonly IClipboardGuard _clipboard;

        private Type _matchManager, _netController, _exporter;
        private EventInfo _onExport;
        private Delegate _manualHandler;
        private bool _selfExporting;
        private string _selfCaptured;
        private DateTime _nextResolve = DateTime.MinValue;
        private readonly HashSet<string> _warned = new HashSet<string>();

        public event Action<string> ManualExport;

        /// <summary>Optional extra place to look for log data when MatchManager has none.</summary>
        public Func<object> FallbackExportData;

        /// <summary>Overridable for tests; defaults to every loaded assembly.</summary>
        public Func<IEnumerable<Assembly>> Assemblies = () => AppDomain.CurrentDomain.GetAssemblies();

        public ReflectionGameApi(Action<string> log, IClipboardGuard clipboard)
        {
            _log = log ?? (_ => { });
            _clipboard = clipboard;
        }

        public bool Resolved => _matchManager != null && _netController != null && _exporter != null;

        /// <summary>Find the game's types. Cheap to call repeatedly; retries at most every 5 s.</summary>
        public bool Resolve()
        {
            if (Resolved) return true;
            var now = DateTime.UtcNow;
            if (now < _nextResolve) return false;
            _nextResolve = now.AddSeconds(5);

            if (_matchManager == null) _matchManager = FindType("MatchManager", "MatchManager");
            if (_netController == null) _netController = FindType("NetworkMatchController", "NetworkMatchController");
            if (_exporter == null)
            {
                _exporter = FindType("_Rainier.Scripts.BattleLog.BattleLogExporter", "BattleLogExporter");
                if (_exporter != null) HookManualExport();
            }
            if (Resolved) _log("Game types found: " + _matchManager.FullName + ", " + _netController.FullName + ", " + _exporter.FullName);
            return Resolved;
        }

        /// <summary>Names of loaded types that look related, for the log when Resolve keeps failing.</summary>
        public string DescribeCandidates()
        {
            var hits = new List<string>();
            foreach (var t in AllTypes())
            {
                var n = t.Name;
                if (n.IndexOf("BattleLog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n == "MatchManager" || n == "NetworkMatchController")
                    hits.Add(t.FullName + " [" + t.Assembly.GetName().Name + "]");
                if (hits.Count >= 40) break;
            }
            return hits.Count == 0 ? "(none)" : string.Join(", ", hits.ToArray());
        }

        // ------------------------------------------------------------------ match state

        public MatchSnapshot ReadMatch()
        {
            if (!Resolve()) return null;
            try
            {
                var info = GetStatic(_netController, "currentMatchInfo", "CurrentMatchInfo");
                if (info == null) return null;
                var mm = GetStatic(_matchManager, "instance", "Instance");
                if (mm == null) return null;
                var board = Call(info, "GetBoardState");
                if (board == null) return null;

                var seat = Get(info, "isPlayer1");
                var snap = new MatchSnapshot
                {
                    MatchId = NonEmpty(AsString(Get(board, "matchID"))) ?? NonEmpty(AsString(Get(info, "matchID"))),
                    GameOver = AsBool(Get(info, "gameOver"), false),
                    Winner = AsInt(Get(info, "winner"), 0),
                    IAmPlayer1 = AsBool(seat, true),
                    MySeat = seat is bool ? ((bool)seat ? 1 : 2) : (int?)null,
                };

                var me = AsString(Get(info, "accountID"));
                snap.MyAccountId = NonEmpty(me);
                var players = GetStatic(_netController, "playerDetails", "PlayerDetails") as IEnumerable;
                if (players != null)
                {
                    bool foundMe = false;
                    string other = null, otherId = null;
                    foreach (var p in players)
                    {
                        if (p == null) continue;
                        var name = AsString(Get(p, "playerName"));
                        var id = AsString(Get(p, "playerId"));
                        if (!string.IsNullOrEmpty(me) && id == me)
                        {
                            foundMe = true;
                            snap.MyName = name;
                            var deck = Get(p, "deckInfo");
                            if (deck != null) snap.MyDeck = AsString(Get(deck, "deckName"));
                        }
                        else if (other == null && !string.IsNullOrEmpty(name))
                        {
                            other = name;
                            otherId = NonEmpty(id);
                        }
                    }
                    // Without our own seat, "the other player" could be us: no name beats our own
                    // name in the opponent's place.
                    if (foundMe)
                    {
                        snap.OpponentName = other;
                        snap.OpponentAccountId = otherId;
                    }
                    else if (other != null) WarnOnce("seat", "Our account is not among playerDetails; the opponent name is left out until it is.");
                }
                return snap;
            }
            catch (Exception e)
            {
                WarnOnce("read", "Reading the match failed: " + Unwrap(e).Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ battle log

        public string ExportBattleLog()
        {
            if (!Resolve()) return null;
            object data = null;
            try
            {
                var mm = GetStatic(_matchManager, "instance", "Instance");
                if (mm != null) data = Get(mm, "BattleLogExportData");
            }
            catch (Exception e) { WarnOnce("data", "MatchManager.BattleLogExportData failed: " + Unwrap(e).Message); }

            if (data == null && FallbackExportData != null)
            {
                try { data = FallbackExportData(); } catch { }
            }
            if (data == null) return null;

            var method = _exporter.GetMethods(AnyInstance | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ExportBattleLog" && m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType.IsInstanceOfType(data));
            if (method == null)
            {
                WarnOnce("method", "BattleLogExporter.ExportBattleLog(data) not found.");
                return null;
            }

            object exporter = null;
            if (!method.IsStatic)
            {
                try { exporter = Activator.CreateInstance(_exporter, true); }
                catch (Exception e) { WarnOnce("ctor", "Could not create BattleLogExporter: " + Unwrap(e).Message); return null; }
            }

            _selfCaptured = null;
            _selfExporting = true;
            object returned = null;
            IDisposable guard = null;
            try
            {
                guard = _clipboard != null ? _clipboard.Begin() : null;
                returned = method.Invoke(exporter, new[] { data });
            }
            catch (Exception e)
            {
                WarnOnce("export", "ExportBattleLog threw: " + Unwrap(e).Message);
            }
            finally
            {
                _selfExporting = false;
                if (guard != null) { try { guard.Dispose(); } catch { } }
            }

            var text = _selfCaptured;
            if (string.IsNullOrEmpty(text)) text = returned as string;
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// One permanent subscription to the static OnExport event. It serves both our own exports
        /// (the text arrives here, not through the clipboard) and the player's Export clicks.
        /// </summary>
        private void HookManualExport()
        {
            _onExport = _exporter.GetEvent("OnExport", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (_onExport == null)
            {
                _log("BattleLogExporter.OnExport not found; relying on ExportBattleLog's return value.");
                return;
            }
            try
            {
                var target = typeof(ReflectionGameApi).GetMethod(nameof(OnExportText), BindingFlags.Instance | BindingFlags.NonPublic);
                _manualHandler = Delegate.CreateDelegate(_onExport.EventHandlerType, this, target);
                var add = _onExport.GetAddMethod(true);
                add.Invoke(null, new object[] { _manualHandler });
            }
            catch (Exception e)
            {
                _manualHandler = null;
                _log("Could not subscribe to OnExport (" + _onExport.EventHandlerType + "): " + Unwrap(e).Message);
            }
        }

        public void Unhook()
        {
            if (_onExport == null || _manualHandler == null) return;
            try { _onExport.GetRemoveMethod(true).Invoke(null, new object[] { _manualHandler }); } catch { }
            _manualHandler = null;
        }

        private void OnExportText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_selfExporting) { _selfCaptured = text; return; }
            var h = ManualExport;
            if (h != null) { try { h(text); } catch { } }
        }

        // ------------------------------------------------------------------ reflection helpers

        public Type FindType(string fullName, string simpleName)
        {
            foreach (var a in Assemblies())
            {
                Type t = null;
                try { t = a.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return AllTypes().FirstOrDefault(t => t.Name == simpleName && !t.IsGenericTypeDefinition);
        }

        private IEnumerable<Type> AllTypes()
        {
            foreach (var a in Assemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                foreach (var t in types) if (t != null) yield return t;
            }
        }

        /// <summary>A static property or field, looked up through base classes (e.g. MonoSingleton&lt;T&gt;).</summary>
        public static object GetStatic(Type type, params string[] names)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var name in names)
                {
                    var p = t.GetProperty(name, AnyStatic);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(null, null);
                    var f = t.GetField(name, AnyStatic);
                    if (f != null) return f.GetValue(null);
                }
            }
            return null;
        }

        /// <summary>An instance property or field by name; null when absent.</summary>
        public static object Get(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, AnyInstance);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj, null);
            for (var c = t; c != null; c = c.BaseType)
            {
                var f = c.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(obj);
            }
            return null;
        }

        private static object Call(object obj, string method)
        {
            var m = obj.GetType().GetMethod(method, AnyInstance, null, Type.EmptyTypes, null);
            return m == null ? null : m.Invoke(obj, null);
        }

        private static string AsString(object o) { return o == null ? null : o.ToString(); }
        private static string NonEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }
        private static bool AsBool(object o, bool def) { return o is bool b ? b : def; }
        private static int AsInt(object o, int def)
        {
            if (o == null) return def;
            try { return Convert.ToInt32(o); } catch { return def; }
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private void WarnOnce(string key, string message)
        {
            if (_warned.Add(key)) _log(message);
        }
    }
}
