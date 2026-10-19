using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The files baked into the exe (see the .csproj): what gets installed into the game, the
    /// disclaimer, and the icon the tray draws its status icons from. One download, nothing to lose
    /// next to it.
    /// </summary>
    internal static class Embedded
    {
        public const string PluginResource = "payload/PtcglAutoBattleLog.Plugin.dll";
        public const string BepInExResource = "payload/BepInEx.zip";
        public const string IconResource = "app.ico";
        public const string DisclaimerResource = "DISCLAIMER.md";
        // The exe carries BepInEx (MIT) and Unity Doorstop (LGPL-2.1): their notices and license texts travel with it.
        public const string NoticesResource = "THIRD-PARTY-NOTICES.md";
        public const string LicenseResource = "LICENSE";
        public const string LgplResource = "licenses/LGPL-2.1.txt";

        public static Payload CreatePayload()
        {
            return new Payload(() => Open(PluginResource), () => Open(BepInExResource));
        }

        public static Stream Open(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                   ?? throw new InvalidOperationException("This copy of the app is incomplete (missing " + name + "). Download it again.");
        }

        /// <summary>DISCLAIMER.md as plain paragraphs for the consent dialog.</summary>
        public static string DisclaimerText()
        {
            return Disclaimer.ToPlainText(ReadText(DisclaimerResource));
        }

        /// <summary>The "Licenses" window: THIRD-PARTY-NOTICES.md, then this app's LICENSE and the full LGPL-2.1.</summary>
        public static string LicensesText()
        {
            const string nl = "\r\n";
            return Disclaimer.ToPlainText(ReadText(NoticesResource)) + nl + nl +
                   "LICENSE of " + AppInfo.ProductName + nl + nl + Crlf(ReadText(LicenseResource)) + nl + nl +
                   "GNU Lesser General Public License, version 2.1 (Unity Doorstop)" + nl + nl + Crlf(ReadText(LgplResource));
        }

        private static string ReadText(string name)
        {
            using (var r = new StreamReader(Open(name), Encoding.UTF8, true)) return r.ReadToEnd();
        }

        private static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n").TrimEnd();

        /// <summary>The app icon at about this size (for windows); the caller disposes it.</summary>
        public static Icon AppIcon(int size = 32)
        {
            using (var s = Open(IconResource)) return new Icon(s, size, size);
        }
    }
}
