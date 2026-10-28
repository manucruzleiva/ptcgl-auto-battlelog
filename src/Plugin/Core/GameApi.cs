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
        private bool _deckShapeLogged;

        public event Action<string> ManualExport;

        /// <summary>Optional extra place to look for log data when MatchManager has none.</summary>
        public Func<object> FallbackExportData;

        /// <summary>
        /// Research aid (issue #14, which queue a match came from): log the members of the match
        /// objects once when a match starts and once at game over. Off by default.
        /// </summary>
        public bool DumpMatchFields;
        private readonly HashSet<string> _dumped = new HashSet<string>();

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
                    object myDeck = null, otherDeck = null;
                    foreach (var p in players)
                    {
                        if (p == null) continue;
                        var name = AsString(Get(p, "playerName"));
                        var id = AsString(Get(p, "playerId"));
                        if (!string.IsNullOrEmpty(me) && id == me)
                        {
                            foundMe = true;
                            snap.MyName = name;
                            var deck = myDeck = Get(p, "deckInfo");
                            if (deck != null) snap.MyDeck = AsString(Get(deck, "deckName"));
                        }
                        else if (other == null && !string.IsNullOrEmpty(name))
                        {
                            other = name;
                            otherId = NonEmpty(id);
                            otherDeck = Get(p, "deckInfo");
                        }
                    }
                    if (foundMe && myDeck != null) LogDeckShapeOnce(myDeck, otherDeck);
                    // Without our own seat, "the other player" could be us: no name beats our own
                    // name in the opponent's place.
                    if (foundMe)
                    {
                        snap.OpponentName = other;
                        snap.OpponentAccountId = otherId;
                    }
                    else if (other != null) WarnOnce("seat", "Our account is not among playerDetails; the opponent name is left out until it is.");
                }
                if (DumpMatchFields && _dumped.Add((snap.MatchId ?? "?") + (snap.GameOver ? "/over" : "/start")))
                    _log("Match fields (" + (snap.GameOver ? "game over" : "start") + "):\n" + DescribeMatch());
                return snap;
            }
            catch (Exception e)
            {
                WarnOnce("read", "Reading the match failed: " + Unwrap(e).Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ research dump

        /// <summary>Words that suggest a member or type says which queue or format a match is.</summary>
        private static readonly string[] QueueWords =
            { "queue", "mode", "ranked", "casual", "format", "matchtype", "battletype", "build", "friend", "event", "ladder", "tournament", "rule" };

        /// <summary>String values of members with these words are left out: names, account ids, tokens.</summary>
        private static readonly string[] PrivateWords =
            { "name", "account", "player", "user", "token", "email", "auth", "session", "password", "friend" };

        /// <summary>
        /// Every field and property of the match objects (MatchInfo, its board state, MatchManager,
        /// NetworkMatchController's statics) with simple values shown, members that look queue
        /// related marked "&lt;&lt;" and expanded one level, then the loaded enums and types whose names
        /// look queue related. Personal strings are redacted. For the BepInEx log only.
        /// </summary>
        public string DescribeMatch()
        {
            var sb = new System.Text.StringBuilder();
            if (!Resolve()) return "(game types not found)";
            object info = null, board = null, mm = null;
            try { info = GetStatic(_netController, "currentMatchInfo", "CurrentMatchInfo"); } catch { }
            try { board = info == null ? null : Call(info, "GetBoardState"); } catch { }
            try { mm = GetStatic(_matchManager, "instance", "Instance"); } catch { }

            DescribeObject(sb, "MatchInfo", info, 1);
            DescribeObject(sb, "BoardState", board, 1);
            DescribeObject(sb, "MatchManager", mm, 1);
            DescribeStatics(sb, _netController);

            sb.Append("Queue-like types:\n");
            var types = AllTypes()
                .Where(t => LooksLikeQueue(t.Name) && !t.IsGenericTypeDefinition &&
                            !(t.Namespace != null && (t.Namespace.StartsWith("System") || t.Namespace.StartsWith("Unity") || t.Namespace.StartsWith("Mono"))))
                .OrderBy(t => t.IsEnum ? 0 : 1)     // the enums say the most: their names are the queues
                .Take(201).ToList();
            int n = 0;
            foreach (var t in types)
            {
                if (++n > 200) { sb.Append("  ...\n"); break; }
                sb.Append("  ").Append(t.FullName).Append(" [").Append(t.Assembly.GetName().Name).Append(']');
                if (t.IsEnum)
                {
                    try { sb.Append(" enum { ").Append(string.Join(", ", Enum.GetNames(t))).Append(" }"); } catch { }
                }
                sb.Append('\n');
            }
            if (n == 0) sb.Append("  (none)\n");
            return sb.ToString();
        }

        private static void DescribeObject(System.Text.StringBuilder sb, string label, object obj, int depth, string indent = "")
        {
            if (obj == null) { sb.Append(indent).Append(label).Append(": null\n"); return; }
            var t = obj.GetType();
            sb.Append(indent).Append(label).Append(" (").Append(t.FullName).Append("):\n");
            foreach (var m in Members(t))
            {
                object value = null;
                string error = null;
                try { value = m.Value(obj); } catch (Exception e) { error = Unwrap(e).GetType().Name; }
                DescribeMember(sb, m.Name, m.Type, value, error, depth, indent + "  ");
            }
        }

        private static void DescribeStatics(System.Text.StringBuilder sb, Type type)
        {
            sb.Append("NetworkMatchController statics (").Append(type.FullName).Append("):\n");
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(AnyStatic))
                {
                    if (f.Name.Contains("<")) continue;
                    object v = null; string error = null;
                    try { v = f.GetValue(null); } catch (Exception e) { error = Unwrap(e).GetType().Name; }
                    DescribeMember(sb, f.Name, f.FieldType, v, error, 1, "  ");
                }
                foreach (var p in t.GetProperties(AnyStatic))
                {
                    if (p.GetIndexParameters().Length != 0 || p.GetGetMethod(true) == null) continue;
                    object v = null; string error = null;
                    try { v = p.GetValue(null, null); } catch (Exception e) { error = Unwrap(e).GetType().Name; }
                    DescribeMember(sb, p.Name, p.PropertyType, v, error, 1, "  ");
                }
            }
        }

        private static void DescribeMember(System.Text.StringBuilder sb, string name, Type type, object value, string error, int depth, string indent)
        {
            bool queueLike = LooksLikeQueue(name) || LooksLikeQueue(type.Name);
            sb.Append(indent).Append(name).Append(": ").Append(type.Name);
            if (error != null) sb.Append(" (threw ").Append(error).Append(')');
            else if (value == null) sb.Append(" = null");
            else if (IsSimple(value.GetType()))
            {
                var text = value.ToString();
                if (value is string && HasWord(name, PrivateWords)) text = "<" + text.Length + " chars, hidden>";
                else if (text.Length > 80) text = text.Substring(0, 80) + "...";
                sb.Append(" = ").Append(text);
            }
            else if (value is ICollection c) sb.Append(" (count ").Append(c.Count).Append(')');
            if (queueLike) sb.Append("   <<");
            sb.Append('\n');
            if (queueLike && depth > 0 && value != null && error == null && !IsSimple(value.GetType()) && !(value is IEnumerable))
                DescribeObject(sb, name, value, depth - 1, indent + "  ");
        }

        private struct MemberRead { public string Name; public Type Type; public Func<object, object> Value; }

        private static IEnumerable<MemberRead> Members(Type type)
        {
            var seen = new HashSet<string>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) break;   // MonoBehaviour plumbing
                foreach (var f in t.GetFields(AnyInstance | BindingFlags.DeclaredOnly))
                {
                    if (f.Name.Contains("<") || !seen.Add(f.Name)) continue;   // backing fields show up as properties
                    var field = f;
                    yield return new MemberRead { Name = f.Name, Type = f.FieldType, Value = o => field.GetValue(o) };
                }
                foreach (var p in t.GetProperties(AnyInstance | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length != 0 || p.GetGetMethod(true) == null || !seen.Add(p.Name)) continue;
                    var prop = p;
                    yield return new MemberRead { Name = p.Name, Type = p.PropertyType, Value = o => prop.GetValue(o, null) };
                }
            }
        }

        private static bool IsSimple(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) ||
                   t == typeof(DateTime) || t == typeof(TimeSpan);
        }

        private static bool LooksLikeQueue(string name) { return HasWord(name, QueueWords); }

        private static bool HasWord(string name, string[] words)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var w in words)
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Research for issue #15 (can the player's decklist travel with the log?): once per game
        /// session, write what deckInfo holds to the BepInEx log. Our own deck is shown with values,
        /// since it is the player's own data in their own log; the opponent's only as member names
        /// and types, to learn whether the client gives us their cards without recording them.
        /// </summary>
        private void LogDeckShapeOnce(object mine, object theirs)
        {
            if (_deckShapeLogged) return;
            _deckShapeLogged = true;
            try
            {
                _log("Deck info (research, issue #15) - ours: " + DescribeShape(mine, true));
                if (theirs != null) _log("Deck info (research, issue #15) - opponent, names and types only: " + DescribeShape(theirs, false));
            }
            catch (Exception e) { _log("Describing deckInfo failed: " + Unwrap(e).Message); }
        }

        /// <summary>
        /// The fields and properties of an object, three levels deep, as one line. Collections give
        /// their count and their first few items. Values are shown only when withValues is set.
        /// </summary>
        public static string DescribeShape(object obj, bool withValues)
        {
            var sb = new System.Text.StringBuilder();
            DescribeInto(sb, obj, withValues, 0);
            return sb.ToString();
        }

        private static void DescribeInto(System.Text.StringBuilder sb, object obj, bool withValues, int depth)
        {
            if (sb.Length > ShapeBudget) { sb.Append("..."); return; }
            if (obj == null) { sb.Append("null"); return; }
            var t = obj.GetType();
            if (IsLeaf(t))
            {
                sb.Append(withValues ? Clip(obj.ToString()) : t.Name);
                return;
            }
            if (obj is IEnumerable list)
            {
                int n = 0;
                var items = new List<object>();
                foreach (var item in list) { if (n < 3) items.Add(item); if (++n >= 1000) break; }
                sb.Append(t.Name).Append("(count ").Append(n >= 1000 ? "1000+" : n.ToString()).Append(")");
                if (depth >= 3 || items.Count == 0) return;
                sb.Append(" [");
                // Without values one item shows the element's shape; the rest would repeat it.
                var shown = withValues ? items : items.Take(1).ToList();
                for (int i = 0; i < shown.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    DescribeInto(sb, shown[i], withValues, depth + 1);
                }
                if (n > shown.Count) sb.Append(", ...");
                sb.Append("]");
                return;
            }
            sb.Append(t.Name);
            if (depth >= 3) return;
            sb.Append(" {");
            bool first = true;
            for (var c = t; c != null && c != typeof(object); c = c.BaseType)
            {
                // Unity's own base classes hold engine state, not game data.
                if (c.Namespace != null && c.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)) break;
                foreach (var m in c.GetMembers(AnyInstance | BindingFlags.DeclaredOnly))
                {
                    object value;
                    string error = null;
                    var f = m as FieldInfo;
                    var p = m as PropertyInfo;
                    if (f != null)
                    {
                        if (f.Name.EndsWith("k__BackingField", StringComparison.Ordinal)) continue;
                        try { value = f.GetValue(obj); } catch (Exception e) { value = null; error = Unwrap(e).GetType().Name; }
                    }
                    else if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null)
                    {
                        try { value = p.GetValue(obj, null); } catch (Exception e) { value = null; error = Unwrap(e).GetType().Name; }
                    }
                    else continue;
                    if (!first) sb.Append("; ");
                    first = false;
                    sb.Append(m.Name).Append(": ");
                    if (error != null)
                        sb.Append('<').Append(error).Append('>');
                    else if (!withValues && value != null && IsLeaf(value.GetType()))
                        sb.Append(value.GetType().Name);
                    else
                        DescribeInto(sb, value, withValues, depth + 1);
                    if (sb.Length > ShapeBudget) { sb.Append("..."); break; }
                }
            }
            sb.Append("}");
        }

        private const int ShapeBudget = 4000;   // characters; a BepInEx log line, not a dump

        private static bool IsLeaf(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) ||
                   t == typeof(DateTime) || t == typeof(Guid) || t == typeof(TimeSpan);
        }

        private static string Clip(string s)
        {
            if (s == null) return "null";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 60 ? s.Substring(0, 60) + "..." : s;
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
