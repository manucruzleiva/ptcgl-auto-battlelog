namespace PtcglAutoBattleLog.Core
{
    /// <summary>
    /// Names in the plugin's BepInEx cfg (BepInEx\config\ptcgl.autobattlelog.cfg, named after the
    /// GUID). The tray app edits the same file (src/Tray/Logic/GameInstall.cs: "Change logs folder...",
    /// "Battle log seal"), so these are a contract: tests/TrayTests checks that both sides agree, with
    /// the real BepInEx ConfigFile reading what the tray wrote.
    /// </summary>
    public static class PluginConfig
    {
        public const string Guid = "ptcgl.autobattlelog";
        public const string FileName = Guid + ".cfg";

        public const string GeneralSection = "General";
        /// <summary>Empty by default: the default folder (Documents\PTCGL Battle Logs), resolved at every start (LogsFolder).</summary>
        public const string OutputFolderKey = "OutputFolder";
        public const string OutputFolderDefault = "";
        public const string SaveManualExportsKey = "SaveManualExports";
        public const bool SaveManualExportsDefault = true;

        public const string MetadataSection = "Metadata";
        /// <summary>A <see cref="SealMode"/> name; BepInEx parses it case-insensitively.</summary>
        public const string SealModeKey = "Mode";
        public const SealMode SealModeDefault = SealMode.Footer;
    }
}
