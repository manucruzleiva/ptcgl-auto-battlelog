using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// settings.json. Loading never fails: a missing field takes its default and a corrupt file is
    /// set aside (settings.json.corrupt) and replaced by defaults, so a bad edit cannot stop the
    /// tray from starting.
    /// </summary>
    [DataContract]
    public sealed class Settings
    {
        [DataMember(Name = "gameDir", Order = 1)] public string GameDir;
        [DataMember(Name = "bepInExInstalledByUs", Order = 2)] public bool BepInExInstalledByUs;
        [DataMember(Name = "autoUpdate", Order = 3)] public bool AutoUpdate = true;
        [DataMember(Name = "notifyOnSave", Order = 4)] public bool NotifyOnSave = true;
        [DataMember(Name = "lastUpdateCheckUtc", Order = 5)] public string LastUpdateCheckUtc;
        [DataMember(Name = "skippedVersion", Order = 6)] public string SkippedVersion;
        /// <summary>The Disclaimer.Version the user accepted; 0 = never. Asked again when it is lower.</summary>
        [DataMember(Name = "disclaimerAccepted", Order = 7)] public int DisclaimerAccepted;
        /// <summary>Send new battle logs to Pokematch (only with a token set; off by default).</summary>
        [DataMember(Name = "pokematchAutoUpload", Order = 8)] public bool PokematchAutoUpload;
        /// <summary>When automatic Pokematch uploads were turned on: older logs go only with "Upload past logs...". UTC, "o".</summary>
        [DataMember(Name = "pokematchSinceUtc", Order = 9)] public string PokematchSinceUtc;

        // The serializer skips constructors and field initializers; without this, a field missing
        // from the file would read as false instead of its default.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            AutoUpdate = true;
            NotifyOnSave = true;
        }

        public static Settings Load(string path, Action<string> log)
        {
            string text = null;
            for (int attempt = 1; text == null; attempt++)
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new Settings();
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs, FileOps.Utf8NoBom, true))
                        text = r.ReadToEnd();
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(100);   // another process is swapping the file in; the next try sees it
                }
                catch (Exception e)
                {
                    Log(log, "Could not read settings (" + e.Message + "); using defaults.");
                    return new Settings();
                }
            }

            try
            {
                var s = Json.Read<Settings>(text);
                if (s != null) return s;
            }
            catch (Exception) { }

            Log(log, "settings.json is corrupt; replaced by defaults (old copy kept as settings.json.corrupt).");
            var defaults = new Settings();
            try
            {
                File.Copy(path, path + ".corrupt", true);
                defaults.Save(path);
            }
            catch (Exception e) { Log(log, "Could not replace the corrupt settings file: " + e.Message); }
            return defaults;
        }

        /// <summary>Atomic: written beside, then swapped in.</summary>
        public void Save(string path)
        {
            FileOps.WriteAllTextAtomic(path, Json.Write(this), FileOps.Utf8NoBom);
        }

        private static void Log(Action<string> log, string message)
        {
            try { log?.Invoke(message); } catch (Exception) { }
        }
    }
}
