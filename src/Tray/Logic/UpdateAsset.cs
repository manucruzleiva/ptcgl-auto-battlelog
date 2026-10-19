using System.Runtime.Serialization;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>One file of a release, as listed in update.json. Size and Sha256 are what the download must match.</summary>
    [DataContract]
    public sealed class UpdateAsset
    {
        [DataMember(Name = "name", Order = 1)] public string Name;
        [DataMember(Name = "url", Order = 2)] public string Url;
        [DataMember(Name = "size", Order = 3)] public long Size;
        [DataMember(Name = "sha256", Order = 4)] public string Sha256;
    }
}
