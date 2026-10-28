using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>One HTTP request to tcgkb: a path under TcgkbApi.BaseUrl, never a full URL.</summary>
    public sealed class TcgkbRequest
    {
        public string Method;
        public string Path;
        /// <summary>The access token for "Authorization: Bearer", or null.</summary>
        public string Bearer;
        public string ContentType;
        public string Body;
    }

    public sealed class TcgkbResponse
    {
        public int Status;
        public string Body;
    }

    /// <summary>tcgkb answered with an error, or with something that is not the API's answer. Message is for people.</summary>
    public class TcgkbException : Exception
    {
        public TcgkbException(string code, string message, int status = 0) : base(message)
        {
            Code = code;
            Status = status;
        }

        /// <summary>The API's "error" (docs/TCGKB-API.md), or "bad_response" / "network".</summary>
        public string Code { get; }
        public int Status { get; }
    }

    /// <summary>The connection is gone (revoked, expired, never made): the player has to connect again.</summary>
    public sealed class TcgkbAuthException : TcgkbException
    {
        public TcgkbAuthException(string message) : base("not_connected", message, 401) { }
    }

    [DataContract]
    public sealed class DeviceAuthorization
    {
        [DataMember(Name = "device_code")] public string DeviceCode;
        [DataMember(Name = "user_code")] public string UserCode;
        [DataMember(Name = "verification_uri")] public string VerificationUri;
        [DataMember(Name = "verification_uri_complete")] public string VerificationUriComplete;
        [DataMember(Name = "expires_in")] public int ExpiresIn;
        [DataMember(Name = "interval")] public int Interval;
    }

    [DataContract]
    public sealed class TcgkbTokenResponse
    {
        [DataMember(Name = "access_token")] public string AccessToken;
        [DataMember(Name = "token_type")] public string TokenType;
        [DataMember(Name = "expires_in")] public int ExpiresIn;
        [DataMember(Name = "refresh_token")] public string RefreshToken;
        [DataMember(Name = "scope")] public string Scope;
    }

    [DataContract]
    public sealed class TcgkbError
    {
        [DataMember(Name = "error")] public string Error;
        [DataMember(Name = "error_description")] public string Description;
        [DataMember(Name = "message")] public string Message;
    }

    [DataContract]
    public sealed class TcgkbUser
    {
        [DataMember(Name = "name")] public string Name;
    }

    /// <summary>A PTCGL account as tcgkb knows it. Status: "claimed", "verified" or "disputed".</summary>
    [DataContract]
    public sealed class TcgkbAccount
    {
        public const string Claimed = "claimed", Verified = "verified", Disputed = "disputed";

        [DataMember(Name = "accountHash")] public string AccountHash;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "status")] public string Status;
    }

    [DataContract]
    public sealed class TcgkbAccounts
    {
        [DataMember(Name = "user")] public TcgkbUser User;
        [DataMember(Name = "accounts")] public List<TcgkbAccount> Accounts;
    }

    [DataContract]
    public sealed class ClaimEvidence
    {
        [DataMember(Name = "fileName", Order = 1)] public string FileName;
        /// <summary>The .txt exactly as on disk (BOM, log, footer), base64.</summary>
        [DataMember(Name = "log", Order = 2)] public string Log;
        /// <summary>The .seal sidecar exactly as on disk, base64; null when the seal is a footer.</summary>
        [DataMember(Name = "seal", Order = 3)] public string Seal;
        /// <summary>
        /// Match uploads only: the deck file the seal (v2) names, exactly as on disk, base64; left out
        /// when there is none, it does not match, or tcgkb already has it (it then links it by deckSha256).
        /// </summary>
        [DataMember(Name = "deck", Order = 4, EmitDefaultValue = false)] public string Deck;
    }

    [DataContract]
    public sealed class ClaimRequest
    {
        [DataMember(Name = "accountHash", Order = 1)] public string AccountHash;
        [DataMember(Name = "evidence", Order = 2)] public List<ClaimEvidence> Evidence;
    }

    [DataContract]
    public sealed class ClaimResponse
    {
        [DataMember(Name = "account")] public TcgkbAccount Account;
    }

    /// <summary>tcgkb's answer to one uploaded battle log: whether it was new, and the decks it read from it.</summary>
    [DataContract]
    public sealed class UploadResponse
    {
        [DataMember(Name = "added")] public bool Added;
        [DataMember(Name = "account")] public TcgkbAccount Account;
        [DataMember(Name = "playerDeck")] public string PlayerDeck;
        [DataMember(Name = "opponentDeck")] public string OpponentDeck;
        /// <summary>The deck tcgkb linked to this match; null when none (no deck in the seal, or tcgkb does not have it).</summary>
        [DataMember(Name = "deckRef")] public TcgkbDeckRef DeckRef;
    }

    [DataContract]
    public sealed class TcgkbDeckRef
    {
        [DataMember(Name = "deckId")] public string DeckId;
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "changed")] public bool Changed;
    }

    public enum DevicePollState { Pending, SlowDown, Granted, Denied, Expired }

    /// <summary>
    /// The tcgkb API the tray uses (docs/TCGKB-API.md): the OAuth 2.0 device authorization grant
    /// (RFC 8628) to connect without the app ever seeing a password, the PTCGL account claims, and
    /// the upload of each match of a claimed account.
    /// HTTP is injected (send), as in UpdateChecker: the tray passes HttpClient, tests a fake server.
    /// </summary>
    public sealed class TcgkbApi
    {
        public const string BaseUrl = "https://tcgkb.app";
        public const string ClientId = "ptcgl-auto-battlelog";
        public const string Scopes = "claims:write logs:upload";
        public const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
        /// <summary>RFC 8628 §3.5: the default interval, and what slow_down adds to it.</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

        private readonly Func<TcgkbRequest, CancellationToken, Task<TcgkbResponse>> _send;

        public TcgkbApi(Func<TcgkbRequest, CancellationToken, Task<TcgkbResponse>> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        // ---- Device authorization (RFC 8628) ----------------------------------------------------

        /// <summary>Asks for a code. The page it names is always on BaseUrl: anything else is refused, never opened.</summary>
        public async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(string deviceName, CancellationToken ct)
        {
            var r = await PostFormAsync("/api/device/code", ct, "client_id", ClientId, "scope", Scopes, "device_name", Clip(deviceName, 64)).ConfigureAwait(false);
            var a = Ok<DeviceAuthorization>(r);
            if (string.IsNullOrEmpty(a.DeviceCode) || a.DeviceCode.Length > 512 || !IsUserCode(a.UserCode) || a.ExpiresIn <= 0)
                throw BadResponse("an incomplete device code");
            if (!IsOwnPage(a.VerificationUri)) throw BadResponse("a sign-in page outside " + BaseUrl);
            if (a.VerificationUriComplete != null && !IsOwnPage(a.VerificationUriComplete)) a.VerificationUriComplete = null;
            return a;
        }

        /// <summary>One look at whether the player approved the code. Granted comes with the tokens.</summary>
        public async Task<(DevicePollState State, TcgkbTokenResponse Tokens)> PollAsync(string deviceCode, CancellationToken ct)
        {
            var r = await PostFormAsync("/api/device/token", ct, "grant_type", DeviceCodeGrant, "device_code", deviceCode, "client_id", ClientId).ConfigureAwait(false);
            if (r.Status == 200) return (DevicePollState.Granted, Tokens(r));
            switch (ErrorOf(r)?.Error)
            {
                case "authorization_pending": return (DevicePollState.Pending, null);
                case "slow_down": return (DevicePollState.SlowDown, null);
                case "access_denied": return (DevicePollState.Denied, null);
                case "expired_token": return (DevicePollState.Expired, null);
                default: throw Failure(r);
            }
        }

        /// <summary>
        /// Polls at the server's interval until the code is approved, denied or expires. Denied and
        /// expired throw TcgkbException ("access_denied", "expired_token"). delay is Task.Delay in the tray.
        /// </summary>
        public async Task<TcgkbTokenResponse> WaitForApprovalAsync(DeviceAuthorization a, Func<TimeSpan, CancellationToken, Task> delay, Func<DateTime> utcNow, CancellationToken ct)
        {
            var interval = a.Interval > 0 ? TimeSpan.FromSeconds(Math.Min(a.Interval, 60)) : DefaultInterval;
            var deadline = utcNow() + TimeSpan.FromSeconds(Math.Min(a.ExpiresIn, 3600));
            while (true)
            {
                await delay(interval, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (utcNow() >= deadline) throw new TcgkbException("expired_token", "The code expired before it was approved. Try connecting again.");
                var (state, tokens) = await PollAsync(a.DeviceCode, ct).ConfigureAwait(false);
                switch (state)
                {
                    case DevicePollState.Granted: return tokens;
                    case DevicePollState.SlowDown: interval += DefaultInterval; break;
                    case DevicePollState.Denied: throw new TcgkbException("access_denied", "The connection was declined on tcgkb.");
                    case DevicePollState.Expired: throw new TcgkbException("expired_token", "The code expired before it was approved. Try connecting again.");
                }
            }
        }

        /// <summary>New tokens for a refresh token (rotated: the old one stops working). invalid_grant throws TcgkbAuthException.</summary>
        public async Task<TcgkbTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            var r = await PostFormAsync("/api/device/token", ct, "grant_type", "refresh_token", "refresh_token", refreshToken, "client_id", ClientId).ConfigureAwait(false);
            if (r.Status == 200) return Tokens(r);
            var error = ErrorOf(r)?.Error;
            if (error == "invalid_grant" || r.Status == 401) throw new TcgkbAuthException("tcgkb no longer accepts this connection. Connect again.");
            throw Failure(r);
        }

        /// <summary>Ends the connection on tcgkb's side (RFC 7009: a token it does not know is not an error).</summary>
        public async Task RevokeAsync(string refreshToken, CancellationToken ct)
        {
            var r = await PostFormAsync("/api/device/revoke", ct, "token", refreshToken, "client_id", ClientId).ConfigureAwait(false);
            if (r.Status != 200 && r.Status != 204) throw Failure(r);
        }

        // ---- PTCGL accounts -------------------------------------------------------------------

        /// <summary>The connected tcgkb user and the PTCGL accounts they own.</summary>
        public async Task<TcgkbAccounts> GetAccountsAsync(string accessToken, CancellationToken ct)
        {
            var r = await SendAsync(new TcgkbRequest { Method = "GET", Path = "/api/ptcgl/accounts", Bearer = accessToken }, ct).ConfigureAwait(false);
            var a = Ok<TcgkbAccounts>(r);
            if (a.User == null) throw BadResponse("no user");
            a.Accounts = (a.Accounts ?? new List<TcgkbAccount>()).Where(x => x != null && IsAccountHash(x.AccountHash)).ToList();
            return a;
        }

        /// <summary>Claims a PTCGL account with sealed logs that name it as the player. tcgkb checks each seal.</summary>
        public async Task<TcgkbAccount> ClaimAsync(string accessToken, ClaimRequest claim, CancellationToken ct)
        {
            if (claim == null || !IsAccountHash(claim.AccountHash)) throw new ArgumentException("Invalid account hash.", nameof(claim));
            if (claim.Evidence == null || claim.Evidence.Count == 0 || claim.Evidence.Count > PtcglAccountScanner.MaxEvidence)
                throw new ArgumentException("A claim needs 1 to " + PtcglAccountScanner.MaxEvidence + " logs.", nameof(claim));
            var r = await SendAsync(new TcgkbRequest
            {
                Method = "POST", Path = "/api/ptcgl/claims", Bearer = accessToken,
                ContentType = "application/json", Body = Json.Write(claim),
            }, ct).ConfigureAwait(false);
            var account = Ok<ClaimResponse>(r).Account;
            if (account == null || account.AccountHash != claim.AccountHash) throw BadResponse("another account");
            return account;
        }

        /// <summary>
        /// Uploads one sealed battle log of a claimed account (scope logs:upload). tcgkb checks the seal;
        /// a log it already has is accepted again, so a retry is harmless.
        /// </summary>
        public async Task<UploadResponse> UploadLogAsync(string accessToken, ClaimEvidence log, CancellationToken ct)
        {
            if (log == null || string.IsNullOrEmpty(log.Log)) throw new ArgumentException("A battle log is required.", nameof(log));
            var r = await SendAsync(new TcgkbRequest
            {
                Method = "POST", Path = "/api/ptcgl/logs", Bearer = accessToken,
                ContentType = "application/json", Body = Json.Write(log),
            }, ct).ConfigureAwait(false);
            return Ok<UploadResponse>(r);
        }

        /// <summary>Gives a claimed account up. One that is not the player's (any more) is not an error.</summary>
        public async Task ReleaseAsync(string accessToken, string accountHash, CancellationToken ct)
        {
            if (!IsAccountHash(accountHash)) throw new ArgumentException("Invalid account hash.", nameof(accountHash));
            var r = await SendAsync(new TcgkbRequest { Method = "DELETE", Path = "/api/ptcgl/accounts/" + accountHash, Bearer = accessToken }, ct).ConfigureAwait(false);
            if (r.Status != 200 && r.Status != 204 && r.Status != 404) throw Failure(r);
        }

        // ---- Helpers ----------------------------------------------------------------------------

        /// <summary>64 lowercase hex: the seal's accountHash (docs/LOG-SEAL.md §3).</summary>
        public static bool IsAccountHash(string s) =>
            s != null && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        /// <summary>A page of BaseUrl over https with nothing tricky in it: the only kind of address the tray opens for tcgkb.</summary>
        public static bool IsOwnPage(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 512) return false;
            foreach (var c in url)
                if (c <= ' ' || c >= 0x7F || c == '\\' || c == '@' || c == '#') return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            return u.Scheme == Uri.UriSchemeHttps && u.Host == new Uri(BaseUrl).Host && u.IsDefaultPort &&
                   u.UserInfo.Length == 0 && string.Equals(u.AbsoluteUri, url, StringComparison.Ordinal);
        }

        /// <summary>What the player types or compares: letters, digits and '-', at most 16.</summary>
        internal static bool IsUserCode(string s) =>
            !string.IsNullOrEmpty(s) && s.Length <= 16 && s.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-');

        private Task<TcgkbResponse> PostFormAsync(string path, CancellationToken ct, params string[] fields)
        {
            var body = new StringBuilder();
            for (int i = 0; i + 1 < fields.Length; i += 2)
            {
                if (fields[i + 1] == null) continue;
                if (body.Length > 0) body.Append('&');
                body.Append(Uri.EscapeDataString(fields[i])).Append('=').Append(Uri.EscapeDataString(fields[i + 1]));
            }
            return SendAsync(new TcgkbRequest { Method = "POST", Path = path, ContentType = "application/x-www-form-urlencoded", Body = body.ToString() }, ct);
        }

        private async Task<TcgkbResponse> SendAsync(TcgkbRequest request, CancellationToken ct)
        {
            TcgkbResponse r;
            try { r = await _send(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (TcgkbException) { throw; }
            catch (Exception e) { throw new TcgkbException("network", "Could not reach tcgkb: " + e.Message); }
            if (r == null) throw new TcgkbException("network", "No response from tcgkb.");
            if (r.Status == 401 && request.Bearer != null) throw new TcgkbAuthException("tcgkb no longer accepts this connection. Connect again.");
            return r;
        }

        private static TcgkbTokenResponse Tokens(TcgkbResponse r)
        {
            var t = Ok<TcgkbTokenResponse>(r);
            if (string.IsNullOrEmpty(t.AccessToken) || string.IsNullOrEmpty(t.RefreshToken) ||
                !string.Equals(t.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || t.ExpiresIn <= 0)
                throw BadResponse("incomplete tokens");
            return t;
        }

        private static T Ok<T>(TcgkbResponse r) where T : class
        {
            if (r.Status < 200 || r.Status > 299) throw Failure(r);
            T value = null;
            try { value = Json.Read<T>(r.Body); }
            catch (Exception) { }
            return value ?? throw BadResponse("something that is not JSON");
        }

        private static TcgkbError ErrorOf(TcgkbResponse r)
        {
            try { return Json.Read<TcgkbError>(r.Body); }
            catch (Exception) { return null; }
        }

        /// <summary>The API's error, worded for the player; the server's own message when it gives one.</summary>
        private static TcgkbException Failure(TcgkbResponse r)
        {
            var e = ErrorOf(r);
            var code = string.IsNullOrEmpty(e?.Error) ? "http_" + r.Status.ToString(CultureInfo.InvariantCulture) : e.Error;
            var detail = Clip(e?.Message ?? e?.Description, 300);
            string text;
            switch (code)
            {
                case "claimed_by_other": text = "This PTCGL account already belongs to another tcgkb user."; break;
                case "invalid_evidence": text = "tcgkb could not check these battle logs."; break;
                case "invalid_log": text = "tcgkb could not check this battle log."; break;
                case "not_claimed": text = "This PTCGL account is not claimed on tcgkb by you."; break;
                case "rate_limited": text = "tcgkb is busy. Try again in a few minutes."; break;
                default:
                    text = r.Status == 404 ? "tcgkb does not offer this yet (HTTP 404)."
                         : r.Status == 429 ? "tcgkb is busy. Try again in a few minutes."
                         : r.Status >= 500 ? "tcgkb had a problem (HTTP " + r.Status.ToString(CultureInfo.InvariantCulture) + "). Try again later."
                         : "tcgkb refused the request (" + code + ").";
                    break;
            }
            return new TcgkbException(code, string.IsNullOrEmpty(detail) ? text : text + " " + detail, r.Status);
        }

        private static TcgkbException BadResponse(string what) =>
            new TcgkbException("bad_response", "tcgkb answered with " + what + ". Try again later, or update this app.");

        private static string Clip(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max);
    }
}
