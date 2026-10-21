using System;
using System.IO;
using System.Runtime.Serialization;

namespace PtcglAutoBattleLog.Tray.Logic
{
    [DataContract]
    public sealed class PokematchToken
    {
        [DataMember(Name = "token", Order = 1)] public string Token;
        [DataMember(Name = "setUtc", Order = 2)] public DateTime SetUtc;
    }

    /// <summary>
    /// pokematch-token.dat: the player's Pokematch API token as JSON, encrypted by protect (DPAPI
    /// CurrentUser in the tray, so another Windows user or another computer cannot read it). A file
    /// that does not decrypt or parse is deleted: the player simply sets the token again.
    /// </summary>
    public sealed class PokematchTokenStore
    {
        public const string FileName = "pokematch-token.dat";

        private readonly string _path;
        private readonly Func<byte[], byte[]> _protect, _unprotect;

        public PokematchTokenStore(string folder, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("Folder is required.", nameof(folder));
            _path = Path.Combine(folder, FileName);
            _protect = protect ?? throw new ArgumentNullException(nameof(protect));
            _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
        }

        public string FilePath => _path;

        /// <summary>The saved token, or null. Never throws.</summary>
        public PokematchToken Load()
        {
            byte[] data;
            try
            {
                if (!File.Exists(_path)) return null;
                data = File.ReadAllBytes(_path);
            }
            catch (Exception) { return null; }
            try
            {
                var t = Json.Read<PokematchToken>(FileOps.Utf8NoBom.GetString(_unprotect(data)));
                if (t != null && PokematchApi.CleanToken(t.Token) == t.Token) return t;
            }
            catch (Exception) { }
            Delete();
            return null;
        }

        /// <summary>Atomic: written beside, then swapped in.</summary>
        public void Save(PokematchToken token)
        {
            if (token == null || PokematchApi.CleanToken(token.Token) != token.Token) throw new ArgumentException("Invalid token.", nameof(token));
            FileOps.WriteAllBytesAtomic(_path, _protect(FileOps.Utf8NoBom.GetBytes(Json.Write(token))));
        }

        public bool Delete() => FileOps.TryDelete(_path);
    }
}
