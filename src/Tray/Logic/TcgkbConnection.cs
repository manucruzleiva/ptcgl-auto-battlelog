using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>What the tray keeps of a tcgkb connection: tcgkb-connection.dat, encrypted for the Windows user.</summary>
    [DataContract]
    public sealed class TcgkbTokens
    {
        [DataMember(Name = "accessToken", Order = 1)] public string AccessToken;
        [DataMember(Name = "accessExpiresUtc", Order = 2)] public DateTime AccessExpiresUtc;
        [DataMember(Name = "refreshToken", Order = 3)] public string RefreshToken;
        [DataMember(Name = "userName", Order = 4)] public string UserName;
        [DataMember(Name = "connectedUtc", Order = 5)] public DateTime ConnectedUtc;
    }

    /// <summary>
    /// tcgkb-connection.dat: the tokens as JSON, encrypted by protect (DPAPI CurrentUser in the tray,
    /// so another Windows user or another computer cannot read them). A file that does not decrypt or
    /// parse is deleted: the player is simply not connected any more.
    /// </summary>
    public sealed class TcgkbTokenStore
    {
        public const string FileName = "tcgkb-connection.dat";

        private readonly string _path;
        private readonly Func<byte[], byte[]> _protect, _unprotect;

        public TcgkbTokenStore(string folder, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("Folder is required.", nameof(folder));
            _path = Path.Combine(folder, FileName);
            _protect = protect ?? throw new ArgumentNullException(nameof(protect));
            _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
        }

        public string FilePath => _path;

        /// <summary>The saved connection, or null. Never throws.</summary>
        public TcgkbTokens Load()
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
                var t = Json.Read<TcgkbTokens>(FileOps.Utf8NoBom.GetString(_unprotect(data)));
                if (t != null && !string.IsNullOrEmpty(t.RefreshToken)) return t;
            }
            catch (Exception) { }
            Delete();
            return null;
        }

        /// <summary>Atomic: written beside, then swapped in.</summary>
        public void Save(TcgkbTokens tokens)
        {
            FileOps.WriteAllBytesAtomic(_path, _protect(FileOps.Utf8NoBom.GetBytes(Json.Write(tokens))));
        }

        public bool Delete() => FileOps.TryDelete(_path);
    }

    /// <summary>
    /// A tcgkb connection over its lifetime: connect with a device code, call the API with a fresh
    /// access token (refreshed a minute before it expires, and once more if tcgkb says 401), and
    /// disconnect. When tcgkb no longer accepts the refresh token the saved connection is deleted and
    /// calls throw TcgkbAuthException, so the menu shows "Connect" again.
    /// </summary>
    public sealed class TcgkbConnection
    {
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

        private readonly TcgkbApi _api;
        private readonly TcgkbTokenStore _store;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public TcgkbConnection(TcgkbApi api, TcgkbTokenStore store, Func<DateTime> utcNow = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public TcgkbApi Api => _api;

        /// <summary>The connected tcgkb user's name, or null when not connected. Reads the file: cheap.</summary>
        public string UserName => _store.Load()?.UserName;
        public bool IsConnected => _store.Load() != null;

        /// <summary>
        /// The whole device flow: a code, show (opens the page and shows the code), waiting for the
        /// approval, then the tcgkb user's name. Saved only once everything worked.
        /// </summary>
        public async Task<string> ConnectAsync(string deviceName, Action<DeviceAuthorization> show,
                                               Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
        {
            var code = await _api.StartDeviceAuthorizationAsync(deviceName, ct).ConfigureAwait(false);
            show?.Invoke(code);
            var granted = await _api.WaitForApprovalAsync(code, delay, _utcNow, ct).ConfigureAwait(false);
            var tokens = ToTokens(granted, null);
            TcgkbAccounts me;
            try { me = await _api.GetAccountsAsync(tokens.AccessToken, ct).ConfigureAwait(false); }
            catch (Exception)
            {
                try { await _api.RevokeAsync(tokens.RefreshToken, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
                throw;
            }
            tokens.UserName = me.User.Name;
            tokens.ConnectedUtc = _utcNow();
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try { _store.Save(tokens); }
            finally { _lock.Release(); }
            return tokens.UserName;
        }

        /// <summary>Forgets the connection here and, best effort, on tcgkb. True when tcgkb confirmed it.</summary>
        public async Task<bool> DisconnectAsync(CancellationToken ct)
        {
            TcgkbTokens tokens;
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                tokens = _store.Load();
                _store.Delete();
            }
            finally { _lock.Release(); }
            if (tokens == null) return true;
            try
            {
                await _api.RevokeAsync(tokens.RefreshToken, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return false; }
        }

        /// <summary>Runs an API call with a valid access token; one refresh and retry when tcgkb answers 401.</summary>
        public async Task<T> CallAsync<T>(Func<string, CancellationToken, Task<T>> call, CancellationToken ct)
        {
            var access = await AccessTokenAsync(false, ct).ConfigureAwait(false);
            try { return await call(access, ct).ConfigureAwait(false); }
            catch (TcgkbAuthException) { }
            access = await AccessTokenAsync(true, ct).ConfigureAwait(false);
            try { return await call(access, ct).ConfigureAwait(false); }
            catch (TcgkbAuthException)
            {
                Forget();
                throw;
            }
        }

        public Task<TcgkbAccounts> GetAccountsAsync(CancellationToken ct) => CallAsync(_api.GetAccountsAsync, ct);

        public Task<UploadResponse> UploadLogAsync(ClaimEvidence log, CancellationToken ct) =>
            CallAsync((access, c) => _api.UploadLogAsync(access, log, c), ct);

        private async Task<string> AccessTokenAsync(bool forceRefresh, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var tokens = _store.Load() ?? throw new TcgkbAuthException("Not connected to tcgkb.");
                if (!forceRefresh && !string.IsNullOrEmpty(tokens.AccessToken) && _utcNow() + RefreshMargin < tokens.AccessExpiresUtc)
                    return tokens.AccessToken;
                TcgkbTokenResponse fresh;
                try { fresh = await _api.RefreshAsync(tokens.RefreshToken, ct).ConfigureAwait(false); }
                catch (TcgkbAuthException)
                {
                    _store.Delete();
                    throw;
                }
                var next = ToTokens(fresh, tokens);
                _store.Save(next);
                return next.AccessToken;
            }
            finally { _lock.Release(); }
        }

        private void Forget()
        {
            _lock.Wait();
            try { _store.Delete(); }
            finally { _lock.Release(); }
        }

        private TcgkbTokens ToTokens(TcgkbTokenResponse r, TcgkbTokens previous) => new TcgkbTokens
        {
            AccessToken = r.AccessToken,
            AccessExpiresUtc = _utcNow() + TimeSpan.FromSeconds(Math.Min(r.ExpiresIn, 24 * 3600)),
            RefreshToken = r.RefreshToken,
            UserName = previous?.UserName,
            ConnectedUtc = previous?.ConnectedUtc ?? _utcNow(),
        };

        /// <summary>
        /// A sealed log as tcgkb takes it: the .txt and the .seal sidecar (if any), exactly as on disk;
        /// with <paramref name="withDeck"/> (match uploads), also the deck file the seal names, when it matches.
        /// </summary>
        public static ClaimEvidence BuildEvidence(SealSummary s, bool withDeck = false)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var deck = withDeck ? s.ReadDeck() : null;
            return new ClaimEvidence
            {
                FileName = Path.GetFileName(s.LogFile),
                Log = Convert.ToBase64String(ReadCapped(s.LogFile)),
                Seal = s.SealFile == null ? null : Convert.ToBase64String(ReadCapped(s.SealFile)),
                Deck = deck == null ? null : Convert.ToBase64String(deck),
            };
        }

        private static byte[] ReadCapped(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length > PtcglAccountScanner.MaxEvidenceBytes) throw new IOException(path + " is too large to send.");
                var bytes = new byte[fs.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = fs.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) throw new IOException(path + " changed while it was read.");
                    read += n;
                }
                return bytes;
            }
        }
    }
}
