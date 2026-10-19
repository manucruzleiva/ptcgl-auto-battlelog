using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PtcglAutoBattleLog.Tray.Logic;
using Timer = System.Windows.Forms.Timer;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// OTA updates (docs/ARCHITECTURE.md, "OTA updates"): checks GitHub Releases 1 minute after start
    /// and every 6 hours; with autoUpdate on, downloads, verifies (UpdateChecker), swaps the exe and
    /// restarts into it; otherwise offers the update. Lives on the UI thread: the timer ticks there,
    /// and awaits come back there, while the network and file work run on the thread pool.
    /// </summary>
    internal sealed class UpdateService : IDisposable
    {
        private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Every = TimeSpan.FromHours(6);
        private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        private readonly Session _session;
        private readonly HttpClient _http;
        private readonly UpdateChecker _checker;
        private readonly Timer _timer = new Timer { Interval = 15 * 1000 };
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private DateTime _nextCheckUtc;
        private bool _busy;
        private bool _installing;
        private bool _oldCleaned;
        private string _announced;
        private string _failureShown;

        public UpdateService(Session session)
        {
            _session = session;
            _http = CreateHttpClient();
            _checker = new UpdateChecker(BuildInfo.RepoOwner, BuildInfo.RepoName, AppInfo.Version, OpenUrlAsync);
            _nextCheckUtc = DateTime.UtcNow + FirstCheck;
            _timer.Tick += async (s, e) => await CheckIfDueAsync();
            CleanUpdatesFolder();
        }

        /// <summary>A newer release waiting for the player (autoUpdate off, or applying it failed).</summary>
        public UpdateManifest Available { get; private set; }

        /// <summary>The menu changes when Available changes.</summary>
        public event Action AvailableChanged;
        /// <summary>Balloon: title, text, what a click does (may be null).</summary>
        public event Action<string, string, Action> Notify;
        /// <summary>The new exe was started: the tray must exit now so it can take the mutex.</summary>
        public event Action RestartRequested;
        /// <summary>False while something must not be interrupted (a dialog, a removal): the update waits.</summary>
        public Func<bool> CanRestart = () => true;

        public void Start() => _timer.Start();

        /// <summary>Sets SecurityProtocol so GitHub (TLS 1.2+) works on every .NET 4.8 setup.</summary>
        public static void ConfigureTls()
        {
            try
            {
                // SystemDefault (what a 4.8 app gets unless a machine policy says otherwise) lets Windows
                // pick, TLS 1.3 included; only an explicit legacy list needs TLS 1.2 added.
                var current = ServicePointManager.SecurityProtocol;
                if (current != SecurityProtocolType.SystemDefault)
                    ServicePointManager.SecurityProtocol = (current | SecurityProtocolType.Tls12) &
                                                           ~(SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls | SecurityProtocolType.Tls11);
            }
            catch (Exception e) { Log.Error("Could not configure TLS", e); }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,   // release downloads redirect to GitHub's asset CDN
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // per request, below
            if (!http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppPaths.AppId + "/" + AppInfo.Version))
                http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppPaths.AppId);
            return http;
        }

        /// <summary>
        /// GET, following redirects; the stream owns the response. Redirects must stay on https (the
        /// SHA-256 check would catch a swapped file anyway; this refuses to even read one).
        /// </summary>
        private async Task<Stream> OpenUrlAsync(string url, CancellationToken ct)
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                var final = response.RequestMessage?.RequestUri;
                if (final != null && final.Scheme != Uri.UriSchemeHttps)
                    throw new UpdateVerificationException("The download was redirected to a non-https address: " + final);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                // .NET Framework's network stream ignores the token while reading: abort it by disposing.
                var abort = ct.Register(() => { try { response.Dispose(); } catch (Exception) { } });
                return new ResponseStream(body, response, abort);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        private async Task CheckIfDueAsync()
        {
            if (!_oldCleaned)
            {
                // Again after start: right after an update the previous process may still have held the .old exe.
                _oldCleaned = true;
                SelfUpdate.CleanupOld(_session.Paths.InstalledExe);
            }
            if (DateTime.UtcNow < _nextCheckUtc) return;
            await CheckAsync(manual: false);
        }

        /// <summary>
        /// Looks for a newer release. Automatic checks are silent on failure (logged, retried in 30
        /// minutes); a manual check always says what happened and ignores a skipped version.
        /// </summary>
        public async Task CheckAsync(bool manual)
        {
            if (_busy)
            {
                if (manual) Notify?.Invoke(AppInfo.ProductName, "Already checking for updates...", null);
                return;
            }
            _busy = true;
            try
            {
                UpdateManifest manifest;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    timeout.CancelAfter(ManifestTimeout);
                    manifest = await _checker.FetchManifestAsync(timeout.Token);
                }
                _nextCheckUtc = DateTime.UtcNow + Every;
                _session.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));

                if (!_checker.IsNewer(manifest))
                {
                    Log.Info("Update check: up to date (latest " + manifest.Version + ").");
                    SetAvailable(null);
                    if (manual) Notify?.Invoke("You're up to date", AppInfo.ProductName + " v" + AppInfo.Version + " is the latest version.", null);
                    return;
                }

                var skipped = _session.Read(s => s.SkippedVersion);
                if (!manual && string.Equals(skipped, manifest.Version, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info("Update check: v" + manifest.Version + " available, skipped by the player.");
                    return;
                }

                Log.Info("Update check: v" + manifest.Version + " available.");
                SetAvailable(manifest);
                if (_session.Read(s => s.AutoUpdate)) await InstallAsync(manifest, manual);
                else if (manual || _announced != manifest.Version)
                {
                    _announced = manifest.Version;
                    Notify?.Invoke("Update available", AppInfo.ProductName + " v" + manifest.Version + " is available. Click to install it.",
                                   () => { var _ = InstallAsync(manifest, true); });
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException) || !_cts.IsCancellationRequested)
            {
                _nextCheckUtc = DateTime.UtcNow + AfterFailure;
                Log.Error("Update check failed", e);
                if (manual) Notify?.Invoke("Could not check for updates", Friendly(e), null);
            }
            catch (OperationCanceledException) { }   // shutting down
            finally { _busy = false; }
        }

        /// <summary>Downloads, verifies and applies the update; on success this process is about to exit.</summary>
        public async Task InstallAsync(UpdateManifest manifest, bool manual)
        {
            if (manifest == null) return;
            var asset = manifest.Find(UpdateChecker.ExeAssetName);
            if (asset == null)
            {
                Log.Error("Release v" + manifest.Version + " has no " + UpdateChecker.ExeAssetName);
                if (manual) Notify?.Invoke("Update failed", "The release has no " + UpdateChecker.ExeAssetName + ".", OpenReleases);
                return;
            }

            if (_installing) return;
            _installing = true;
            try
            {
                string downloaded;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    timeout.CancelAfter(DownloadTimeout);
                    downloaded = await _checker.DownloadAsync(asset, _session.Paths.UpdatesDir, timeout.Token);
                }
                Log.Info("Downloaded and verified v" + manifest.Version + ": " + downloaded);

                if (!CanRestart())
                {
                    // A dialog is open or a removal is running: try again at the next check.
                    Log.Info("Update postponed: the app is busy.");
                    _nextCheckUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                    SetAvailable(manifest);
                    return;
                }
                Apply(downloaded);
            }
            catch (Exception e) when (!(e is OperationCanceledException) || !_cts.IsCancellationRequested)
            {
                _nextCheckUtc = DateTime.UtcNow + AfterFailure;
                Log.Error("Update to v" + manifest.Version + " failed", e);
                SetAvailable(manifest);
                // A bad release would fail every retry: say so once per version, unless asked.
                if (manual || (e is UpdateVerificationException && _failureShown != manifest.Version))
                {
                    _failureShown = manifest.Version;
                    Notify?.Invoke("Update failed", Friendly(e) + " Click to download it from the project page.", OpenReleases);
                }
            }
            catch (OperationCanceledException) { }
            finally { _installing = false; }
        }

        /// <summary>Swap the exe, start the new one with --updated-from, and ask the tray to exit. No awaits in here.</summary>
        private void Apply(string downloaded)
        {
            var exe = _session.Paths.InstalledExe;
            SelfUpdate.Swap(downloaded, exe);
            var args = new[] { CommandLine.UpdatedFromFlag, AppInfo.Version };
            if (_session.Portable && _session.PortableForced) args = new[] { CommandLine.UpdatedFromFlag, AppInfo.Version, CommandLine.PortableFlag };
            try
            {
                using (Process.Start(new ProcessStartInfo(exe, CommandLine.Join(args))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                })) { }
            }
            catch (Exception)
            {
                // The new exe did not start: put the running one back so the next logon still works.
                try
                {
                    File.Delete(exe);
                    File.Move(_session.Paths.OldExe, exe);
                }
                catch (Exception e2) { Log.Error("Could not restore the previous exe after a failed restart", e2); }
                throw;
            }
            Log.Info("Started the new version; exiting.");
            RestartRequested?.Invoke();
        }

        public void SkipAvailable()
        {
            var m = Available;
            if (m == null) return;
            _session.Update(s => s.SkippedVersion = m.Version);
            Log.Info("Update v" + m.Version + " skipped.");
            SetAvailable(null);
        }

        private void SetAvailable(UpdateManifest m)
        {
            if (ReferenceEquals(m, Available) || (m != null && Available != null && m.Version == Available.Version)) return;
            Available = m;
            AvailableChanged?.Invoke();
        }

        private static void OpenReleases()
        {
            try { Shell.Open(AppInfo.ReleasesUrl); }
            catch (Exception e) { Log.Error("Could not open " + AppInfo.ReleasesUrl, e); }
        }

        private static string Friendly(Exception e)
        {
            if (e is HttpRequestException || e is WebException) return "No connection to GitHub.";
            if (e is OperationCanceledException) return "GitHub did not answer in time.";
            if (e is UpdateVerificationException) return "The download could not be verified, so it was not installed.";
            if (e is UnauthorizedAccessException || e is IOException) return "The new version could not be put in place (" + e.Message + ").";
            return e.Message;
        }

        /// <summary>Downloads left by an update that went through, failed or was interrupted.</summary>
        private void CleanUpdatesFolder()
        {
            try
            {
                var dir = _session.Paths.UpdatesDir;
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir)) PathUtil.TryDeleteFile(f);
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _cts.Cancel();
            _cts.Dispose();
            _http.Dispose();
        }

        /// <summary>The response body; disposing it also disposes the response.</summary>
        private sealed class ResponseStream : Stream
        {
            private readonly Stream _inner;
            private readonly HttpResponseMessage _response;
            private readonly CancellationTokenRegistration _abort;

            public ResponseStream(Stream inner, HttpResponseMessage response, CancellationTokenRegistration abort)
            {
                _inner = inner;
                _response = response;
                _abort = abort;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _abort.Dispose();
                    _inner.Dispose();
                    _response.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
