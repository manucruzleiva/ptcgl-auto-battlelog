using System;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// What the tray installs into the game: the plugin DLL and the BepInEx zip. In the shipped exe
    /// both are embedded resources; tests pass files. Openers are called on demand, so nothing is
    /// held in memory between repairs.
    /// </summary>
    public sealed class Payload
    {
        private readonly Func<Stream> _openPlugin;
        private readonly Func<Stream> _openZip;
        private readonly object _gate = new object();
        private byte[] _pluginSha256;

        public Payload(Func<Stream> openPluginDll, Func<Stream> openBepInExZip)
        {
            _openPlugin = openPluginDll ?? throw new ArgumentNullException(nameof(openPluginDll));
            _openZip = openBepInExZip ?? throw new ArgumentNullException(nameof(openBepInExZip));
        }

        public static Payload FromFiles(string pluginDllPath, string bepInExZipPath)
        {
            return new Payload(() => File.OpenRead(pluginDllPath), () => File.OpenRead(bepInExZipPath));
        }

        /// <summary>
        /// SHA-256 of the plugin, computed on first use and then cached (a failed read is retried
        /// next time instead of being cached). Returns a copy, so callers cannot alter the cache.
        /// </summary>
        public byte[] PluginSha256
        {
            get
            {
                lock (_gate)
                {
                    if (_pluginSha256 == null)
                        using (var s = OpenPluginDll()) _pluginSha256 = Hashing.Sha256(s);
                    return (byte[])_pluginSha256.Clone();
                }
            }
        }

        public Stream OpenPluginDll()
        {
            return _openPlugin() ?? throw new InvalidOperationException("The plugin payload is not available.");
        }

        public Stream OpenBepInExZip()
        {
            return _openZip() ?? throw new InvalidOperationException("The BepInEx payload is not available.");
        }
    }
}
