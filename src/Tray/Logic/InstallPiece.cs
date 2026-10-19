namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>The parts that must be in the game folder for the plugin to load.</summary>
    public enum InstallPiece
    {
        WinHttp,            // winhttp.dll: the Doorstop proxy that starts BepInEx
        DoorstopConfig,     // doorstop_config.ini
        BepInExPreloader,   // BepInEx\core\BepInEx.Preloader.dll
        BepInExCore,        // BepInEx\core\BepInEx.dll
        Plugin,             // our plugin DLL, absent or not the one this tray ships
    }
}
