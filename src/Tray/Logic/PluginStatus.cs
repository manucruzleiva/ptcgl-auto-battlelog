using System;
using System.Globalization;
using System.Runtime.Serialization;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// plugin-status.json, written by the plugin inside the game. The only channel from the plugin
    /// to the tray: a fresh heartbeat means the game is running with the plugin loaded.
    /// </summary>
    [DataContract]
    public sealed class PluginStatus
    {
        public const string StateStarting = "starting";
        public const string StateOk = "ok";
        public const string StateError = "error";

        /// <summary>The plugin writes at least every 30 s; three missed beats means it is gone.</summary>
        public static readonly TimeSpan AliveWindow = TimeSpan.FromSeconds(90);

        [DataMember(Name = "pluginVersion", Order = 1)] public string PluginVersion;
        [DataMember(Name = "state", Order = 2)] public string State;
        [DataMember(Name = "message", Order = 3)] public string Message;
        [DataMember(Name = "logsFolder", Order = 4)] public string LogsFolder;
        [DataMember(Name = "savedThisSession", Order = 5)] private int? _savedThisSession;
        [DataMember(Name = "lastFile", Order = 6)] public string LastFile;
        [DataMember(Name = "lastSavedUtc", Order = 7)] public string LastSavedUtc;
        [DataMember(Name = "heartbeatUtc", Order = 8)] public string HeartbeatUtc;
        [DataMember(Name = "processId", Order = 9)] private int? _processId;

        // Numbers are nullable on the wire ("missing values are null") so a null does not make the
        // whole file unreadable; callers just see 0.
        public int SavedThisSession { get => _savedThisSession ?? 0; set => _savedThisSession = value; }
        public int ProcessId { get => _processId ?? 0; set => _processId = value; }

        /// <summary>Null if the file is absent, being replaced, or not valid JSON.</summary>
        public static PluginStatus TryRead(string path)
        {
            var text = FileOps.TryReadSmallText(path, 256 * 1024);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return Json.Read<PluginStatus>(text); }
            catch (Exception) { return null; }
        }

        public DateTime? HeartbeatUtcValue => ParseUtc(HeartbeatUtc);
        public DateTime? LastSavedUtcValue => ParseUtc(LastSavedUtc);

        /// <summary>Heartbeat within 90 s of <paramref name="nowUtc"/> (either way, to survive small clock changes).</summary>
        public bool IsAlive(DateTime nowUtc)
        {
            var hb = HeartbeatUtcValue;
            if (!hb.HasValue) return false;
            var now = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc;
            var age = now - hb.Value;
            return age.Duration() <= AliveWindow;
        }

        private static readonly string[] UtcFormats = { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" };

        internal static DateTime? ParseUtc(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), UtcFormats, CultureInfo.InvariantCulture,
                                          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                ? d : (DateTime?)null;
        }
    }
}
