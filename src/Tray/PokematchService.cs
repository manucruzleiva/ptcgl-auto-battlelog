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
    /// The tray's Pokematch uploads (docs/ARCHITECTURE.md, "Pokematch"): HttpClient for PokematchApi
    /// and DPAPI for the saved token. Made on first use; a tray without a token makes no request to
    /// Pokematch.
    /// </summary>
    internal sealed class PokematchService : IDisposable
    {
        // Pokematch runs on a host that sleeps when idle and can take most of a minute to wake up.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
        private const int MaxResponseBytes = 1024 * 1024;
        // Ties the encrypted file to this app as well as to the Windows user.
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("PtcglAutoBattleLog/pokematch-token/v1");

        private readonly HttpClient _http;

        public PokematchService(Session session)
        {
            _http = CreateHttpClient();
            Tokens = new PokematchTokenStore(session.Paths.Root,
                data => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser),
                data => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
            Uploader = new PokematchUploader(new PokematchApi(SendAsync), Path.Combine(session.Paths.Root, PokematchUploadState.FileName));
        }

        public PokematchTokenStore Tokens { get; }
        public PokematchUploader Uploader { get; }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,   // the token goes to Pokematch and nowhere else
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri(PokematchApi.BaseUrl) };
            if (!http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppPaths.AppId + "/" + AppInfo.Version))
                http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppPaths.AppId);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return http;
        }

        private async Task<PokematchResponse> SendAsync(PokematchRequest r, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(new HttpMethod(r.Method), r.Path))
            {
                timeout.CancelAfter(RequestTimeout);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", r.Token);
                if (r.Body != null) request.Content = new StringContent(r.Body, new UTF8Encoding(false), "application/json");
                try
                {
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (timeout.Token.Register(() => { try { response.Dispose(); } catch (Exception) { } }))
                        return new PokematchResponse { Status = (int)response.StatusCode, Body = await ReadCappedAsync(body, timeout.Token).ConfigureAwait(false) };
                }
                catch (Exception) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new PokematchException("Pokematch did not answer within " + (int)RequestTimeout.TotalSeconds + " seconds.");
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
                    if (ms.Length + n > MaxResponseBytes) throw new PokematchException("Pokematch answered with more than " + MaxResponseBytes + " bytes.");
                    ms.Write(buffer, 0, n);
                }
                return new UTF8Encoding(false).GetString(ms.ToArray());
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
