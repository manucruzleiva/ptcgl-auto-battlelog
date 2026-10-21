using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The tray's tcgkb connection (docs/ARCHITECTURE.md, "tcgkb account"): HttpClient for
    /// TcgkbApi and DPAPI for the saved tokens. Nothing here runs until the player connects; a tray
    /// that was never connected makes no request to tcgkb.
    /// </summary>
    internal sealed class TcgkbService : IDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private const int MaxResponseBytes = 1024 * 1024;
        // Ties the encrypted file to this app as well as to the Windows user.
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("PtcglAutoBattleLog/tcgkb-connection/v1");

        private readonly HttpClient _http;

        public TcgkbService(Session session)
        {
            _http = CreateHttpClient();
            var store = new TcgkbTokenStore(session.Paths.Root,
                data => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser),
                data => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
            Connection = new TcgkbConnection(new TcgkbApi(SendAsync), store);
        }

        public TcgkbConnection Connection { get; }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,   // the bearer token goes to tcgkb.app and nowhere else
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri(TcgkbApi.BaseUrl) };
            if (!http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppPaths.AppId + "/" + AppInfo.Version))
                http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppPaths.AppId);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return http;
        }

        private async Task<TcgkbResponse> SendAsync(TcgkbRequest r, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(new HttpMethod(r.Method), r.Path))
            {
                timeout.CancelAfter(RequestTimeout);
                if (r.Bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", r.Bearer);
                if (r.Body != null) request.Content = new StringContent(r.Body, new UTF8Encoding(false), r.ContentType);
                try
                {
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (timeout.Token.Register(() => { try { response.Dispose(); } catch (Exception) { } }))
                        return new TcgkbResponse { Status = (int)response.StatusCode, Body = await ReadCappedAsync(body, timeout.Token).ConfigureAwait(false) };
                }
                catch (Exception) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TcgkbException("network", "tcgkb did not answer within " + (int)RequestTimeout.TotalSeconds + " seconds.");
                }
            }
        }

        private static async Task<string> ReadCappedAsync(Stream s, CancellationToken ct)
        {
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[16384];
                int n;
                while ((n = await s.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > MaxResponseBytes) throw new TcgkbException("bad_response", "tcgkb answered with more than " + MaxResponseBytes + " bytes.");
                    ms.Write(buffer, 0, n);
                }
                return new UTF8Encoding(false).GetString(ms.ToArray());
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
