using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>
    /// Writes %LOCALAPPDATA%\PtcglAutoBattleLog\plugin-status.json, the only thing the tray app
    /// learns from the plugin: that it is running, whether it found the game, and what it saved.
    ///
    /// Rewritten when the content changes and, as a heartbeat, every <see cref="HeartbeatInterval"/>;
    /// the tray takes a heartbeat older than 90 s as "plugin not running". Always written whole
    /// (.tmp + replace), so the tray never reads half a file. The JSON is built by hand: the game's
    /// Mono profile has no JSON library we can count on, and a DLL of our own is all we ship.
    ///
    /// Thread-safe. The Unity side reports from the main thread and beats from a timer thread, so
    /// the heartbeat keeps going while the game is minimized and Unity stops running its loop.
    /// </summary>
    public sealed class PluginStatusWriter
    {
        public const string FileName = "plugin-status.json";
        public const string Starting = "starting", Ok = "ok", Error = "error";

        /// <summary>Well under the contract's 30 s, so a slow frame or a missed timer tick never matters.</summary>
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly object _lock = new object();
        private readonly string _version, _logsFolder;
        private readonly int _processId;
        private readonly Action<string> _log;

        private string _state = Starting, _message, _lastFile;
        private int _saved;
        private DateTime? _lastSavedUtc;
        private string _written;        // last content written, heartbeat left out
        private DateTime _writtenUtc;
        private bool _failing;

        public PluginStatusWriter(string path, string pluginVersion, string logsFolder, int processId, Action<string> log = null)
        {
            Path = path;
            _version = pluginVersion;
            _logsFolder = logsFolder;
            _processId = processId;
            _log = log ?? (_ => { });
        }

        public string Path { get; }

        /// <summary>The contract's location: &lt;localAppData&gt;\PtcglAutoBattleLog\plugin-status.json.</summary>
        public static string PathFor(string localAppData)
        {
            return System.IO.Path.Combine(localAppData, "PtcglAutoBattleLog", FileName);
        }

        /// <summary>
        /// Sets what the file says. Writes at once when that differs from the last write, otherwise
        /// only when the heartbeat is due. Never throws; false when nothing was written.
        /// </summary>
        public bool Update(DateTime nowUtc, string state, string message, int savedThisSession,
                           string lastFile, DateTime? lastSavedUtc)
        {
            lock (_lock)
            {
                _state = state;
                _message = message;
                _saved = savedThisSession;
                _lastFile = lastFile;
                _lastSavedUtc = lastSavedUtc;
                return WriteIfDue(nowUtc);
            }
        }

        /// <summary>Rewrites the file with a fresh heartbeat if the last write is getting old.</summary>
        public bool Heartbeat(DateTime nowUtc)
        {
            lock (_lock) return WriteIfDue(nowUtc);
        }

        private bool WriteIfDue(DateTime nowUtc)
        {
            var content = Render(null);
            var age = nowUtc - _writtenUtc;
            // A negative age is a clock set back: beat now rather than wait for it to catch up.
            if (content == _written && age >= TimeSpan.Zero && age < HeartbeatInterval) return false;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                LogStore.WriteAtomic(Path, Render(nowUtc), Utf8NoBom);
                _written = content;
                _writtenUtc = nowUtc;
                _failing = false;
                return true;
            }
            catch (Exception e)
            {
                // Typically the tray reading it at that very moment; the next tick tries again.
                if (!_failing) _log("Could not write " + Path + ": " + e.Message);
                _failing = true;
                return false;
            }
        }

        /// <summary>The file's JSON; field names, order and formats are the contract in docs/ARCHITECTURE.md.</summary>
        internal string Render(DateTime? heartbeatUtc)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\r\n");
            Field(sb, "pluginVersion", Quote(_version));
            Field(sb, "state", Quote(_state));
            Field(sb, "message", Quote(_message));
            Field(sb, "logsFolder", Quote(_logsFolder));
            Field(sb, "savedThisSession", _saved.ToString(CultureInfo.InvariantCulture));
            Field(sb, "lastFile", Quote(_lastFile));
            Field(sb, "lastSavedUtc", Date(_lastSavedUtc));
            Field(sb, "heartbeatUtc", Date(heartbeatUtc));
            sb.Append("  \"processId\": ").Append(_processId.ToString(CultureInfo.InvariantCulture)).Append("\r\n}\r\n");
            return sb.ToString();
        }

        private static void Field(StringBuilder sb, string name, string json)
        {
            sb.Append("  \"").Append(name).Append("\": ").Append(json).Append(",\r\n");
        }

        /// <summary>yyyy-MM-ddTHH:mm:ssZ in UTC, or null.</summary>
        internal static string Date(DateTime? d)
        {
            if (d == null) return "null";
            var utc = d.Value.Kind == DateTimeKind.Local ? d.Value.ToUniversalTime() : d.Value;
            return "\"" + utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture) + "\"";
        }

        /// <summary>A JSON string literal, or null.</summary>
        internal static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8).Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // Half a surrogate pair cannot be written as UTF-8 (it would become U+FFFD);
                        // escaped, a path that contains one still reads back exactly.
                        bool lone = char.IsHighSurrogate(c) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])
                                  : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));
                        if (c < ' ' || c == '\u2028' || c == '\u2029' || lone)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
