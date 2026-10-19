using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// update.json from the latest GitHub release. Only describes the release: whether it is
    /// newer, and whether a download may be trusted, is decided by UpdateChecker.
    /// </summary>
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember(Name = "version", Order = 1)] public string Version;
        [DataMember(Name = "tag", Order = 2)] public string Tag;
        [DataMember(Name = "releasedUtc", Order = 3)] public string ReleasedUtc;
        [DataMember(Name = "notesUrl", Order = 4)] public string NotesUrl;
        [DataMember(Name = "assets", Order = 5)] public List<UpdateAsset> Assets;

        /// <summary>Throws FormatException when the text is not a manifest (bad JSON or no version).</summary>
        public static UpdateManifest Parse(string json)
        {
            UpdateManifest m;
            try { m = Json.Read<UpdateManifest>(json); }
            catch (Exception e) { throw new FormatException("update.json is not valid JSON: " + e.Message, e); }
            if (m == null || string.IsNullOrWhiteSpace(m.Version))
                throw new FormatException("update.json has no version.");
            if (m.Assets == null) m.Assets = new List<UpdateAsset>();
            m.Assets.RemoveAll(a => a == null);
            return m;
        }

        /// <summary>The asset with exactly this name, or null.</summary>
        public UpdateAsset Find(string name)
        {
            if (Assets == null || name == null) return null;
            return Assets.Find(a => a != null && string.Equals(a.Name, name, StringComparison.Ordinal));
        }
    }
}
