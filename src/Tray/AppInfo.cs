using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>Names and links shown to the player, and the names other processes find us by.</summary>
    internal static class AppInfo
    {
        public const string ProductName = "PTCGL Auto Battle Log";
        public const string Version = BuildInfo.Version;
        public const string Publisher = "manucruzleiva";
        public const string ProjectUrl = "https://github.com/" + BuildInfo.RepoOwner + "/" + BuildInfo.RepoName;
        public const string ReleasesUrl = ProjectUrl + "/releases";
        public const string GameName = "Pokémon TCG Live";

        // docs/ARCHITECTURE.md, "Command line": shared by the installed and the portable copy.
        public const string MutexName = @"Local\" + AppPaths.AppId + "-tray";
        public const string ShowEventName = @"Local\" + AppPaths.AppId + "-show";
        public const string ExitEventName = @"Local\" + AppPaths.AppId + "-exit";
    }
}
