using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>One HTTP request to Pokematch: a path under PokematchApi.BaseUrl, never a full URL.</summary>
    public sealed class PokematchRequest
    {
        public string Method;
        public string Path;
        /// <summary>The player's API token, for "Authorization: Bearer".</summary>
        public string Token;
        /// <summary>JSON, or null.</summary>
        public string Body;
    }

    public sealed class PokematchResponse
    {
        public int Status;
        public string Body;
    }

    /// <summary>Pokematch answered with an error, or could not be reached. Message is for people.</summary>
    public class PokematchException : Exception
    {
        public PokematchException(string message, int status = 0) : base(message) { Status = status; }

        /// <summary>The HTTP status, 0 when Pokematch was not reached.</summary>
        public int Status { get; }

        /// <summary>Worth trying again later: not reached, busy, or a server error.</summary>
        public bool Transient => Status == 0 || Status == 408 || Status == 429 || Status >= 500;
    }

    /// <summary>Pokematch does not accept the token (wrong, deleted, or revoked by a password change).</summary>
    public sealed class PokematchAuthException : PokematchException
    {
        public PokematchAuthException(int status)
            : base("Pokematch does not accept the API token. Make a new one in Pokematch (changing your password revokes them) and set it again.", status) { }
    }

    [DataContract]
    public sealed class PokematchDeck
    {
        [DataMember(Name = "name")] public string Name;
    }

    [DataContract]
    public sealed class PokematchGame
    {
        /// <summary>"win", "loss" or "tie".</summary>
        [DataMember(Name = "result")] public string Result;
    }

    /// <summary>A TCG log as Pokematch's POST /api/v1/tcg-matches takes it (docs/POKEMATCH-API.md).</summary>
    [DataContract]
    public sealed class PokematchMatch
    {
        public const string Unknown = "Unknown";
        /// <summary>Lowercase, at most 30 characters: Pokematch's tag rules.</summary>
        public const string Tag = "ptcgl-auto-battle-log";
        /// <summary>Pokematch keeps at most this many characters of a tag.</summary>
        public const int MaxTagLength = 30;

        [DataMember(Name = "activeDeck", Order = 1)] public PokematchDeck ActiveDeck;
        [DataMember(Name = "opponentDeck", Order = 2)] public string OpponentDeck;
        /// <summary>"Bo1", "Bo3" or "Bo5".</summary>
        [DataMember(Name = "format", Order = 3)] public string Format;
        [DataMember(Name = "games", Order = 4)] public List<PokematchGame> Games;
        /// <summary>"yyyy-MM-dd".</summary>
        [DataMember(Name = "date", Order = 5)] public string Date;
        [DataMember(Name = "notes", Order = 6, EmitDefaultValue = false)] public string Notes;
        [DataMember(Name = "tags", Order = 7, EmitDefaultValue = false)] public List<string> Tags;
    }

    /// <summary>Pokematch's answer to a POST: the new match's id, at the top or under "match" or "data".</summary>
    [DataContract]
    public sealed class PokematchCreated
    {
        [DataMember(Name = "id")] public object Id;
        [DataMember(Name = "_id")] public object MongoId;
        [DataMember(Name = "match")] public PokematchCreated Match;
        [DataMember(Name = "data")] public PokematchCreated Data;

        public string AnyId() => Text(Id) ?? Text(MongoId) ?? Match?.AnyId() ?? Data?.AnyId();

        private static string Text(object o) =>
            o is string s ? s : o is int || o is long || o is decimal || o is double ? Convert.ToString(o, CultureInfo.InvariantCulture) : null;
    }

    [DataContract]
    public sealed class PokematchError
    {
        [DataMember(Name = "error")] public object Error;
        [DataMember(Name = "message")] public object Message;
    }

    /// <summary>
    /// The part of Pokematch's API the tray uses: add a match (POST /api/v1/tcg-matches) and replace
    /// one it added before (PUT /api/v1/tcg-matches/:id), with the player's own API token. HTTP is
    /// injected (send), as in TcgkbApi: the tray passes HttpClient, tests a fake server.
    /// </summary>
    public sealed class PokematchApi
    {
        public const string BaseUrl = "https://pokematch-vslogger.onrender.com";
        public const string MatchesPath = "/api/v1/tcg-matches";
        public const string MePath = "/api/v1/me";
        /// <summary>The page where players make API tokens.</summary>
        public const string TokensPage = BaseUrl + "/";
        public const string Source = "PTCGL Auto Battle Log";
        /// <summary>Tokens are opaque; this only keeps pasted junk (a whole curl line, a newline) out of a header.</summary>
        public const int MaxTokenLength = 512;

        private readonly Func<PokematchRequest, CancellationToken, Task<PokematchResponse>> _send;

        public PokematchApi(Func<PokematchRequest, CancellationToken, Task<PokematchResponse>> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        /// <summary>
        /// The token as the player pasted it, trimmed, "Bearer " dropped; null when it cannot be one
        /// (empty, too long, spaces or control characters inside).
        /// </summary>
        public static string CleanToken(string pasted)
        {
            var t = (pasted ?? "").Trim();
            if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7).Trim();
            if (t.Length == 0 || t.Length > MaxTokenLength) return null;
            foreach (var c in t)
                if (c <= ' ' || c >= 0x7F) return null;
            return t;
        }

        /// <summary>GET /api/v1/me: true when Pokematch accepts the token; PokematchAuthException when it does not.</summary>
        public async Task<bool> CheckTokenAsync(string token, CancellationToken ct)
        {
            var r = await SendAsync(new PokematchRequest { Method = "GET", Path = MePath, Token = token }, ct).ConfigureAwait(false);
            if (r.Status < 200 || r.Status > 299) throw Failure(r);
            return true;
        }

        /// <summary>Adds the match; returns Pokematch's id for it, or null when the answer names none.</summary>
        public async Task<string> CreateAsync(string token, PokematchMatch match, CancellationToken ct)
        {
            var r = await SendAsync(new PokematchRequest { Method = "POST", Path = MatchesPath, Token = token, Body = Json.Write(match) }, ct).ConfigureAwait(false);
            if (r.Status < 200 || r.Status > 299) throw Failure(r);
            try { return Clean(Json.Read<PokematchCreated>(r.Body)?.AnyId()); }
            catch (Exception) { return null; }
        }

        /// <summary>Replaces a match it added before. False when Pokematch no longer has it (the player deleted it there).</summary>
        public async Task<bool> UpdateAsync(string token, string id, PokematchMatch match, CancellationToken ct)
        {
            if (Clean(id) == null) throw new ArgumentException("Invalid Pokematch id.", nameof(id));
            var r = await SendAsync(new PokematchRequest
            {
                Method = "PUT", Path = MatchesPath + "/" + Uri.EscapeDataString(id), Token = token, Body = Json.Write(match),
            }, ct).ConfigureAwait(false);
            if (r.Status == 404 || r.Status == 410) return false;
            if (r.Status < 200 || r.Status > 299) throw Failure(r);
            return true;
        }

        /// <summary>An id fit to go back into a URL path: letters, digits, '-' and '_', at most 128.</summary>
        internal static string Clean(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return null;
            foreach (var c in id)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_')) return null;
            return id;
        }

        private async Task<PokematchResponse> SendAsync(PokematchRequest request, CancellationToken ct)
        {
            if (CleanToken(request.Token) != request.Token) throw new PokematchAuthException(0);
            PokematchResponse r;
            try { r = await _send(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (PokematchException) { throw; }
            catch (Exception e) { throw new PokematchException("Could not reach Pokematch: " + e.Message); }
            if (r == null) throw new PokematchException("No response from Pokematch.");
            if (r.Status == 401 || r.Status == 403) throw new PokematchAuthException(r.Status);
            return r;
        }

        private static PokematchException Failure(PokematchResponse r)
        {
            string detail = null;
            try
            {
                var e = Json.Read<PokematchError>(r.Body);
                detail = (e?.Message as string) ?? (e?.Error as string);
            }
            catch (Exception) { }
            if (detail != null && detail.Length > 300) detail = detail.Substring(0, 300);
            var status = r.Status.ToString(CultureInfo.InvariantCulture);
            var text = r.Status == 429 ? "Pokematch is busy (HTTP 429)."
                     : r.Status >= 500 ? "Pokematch had a problem (HTTP " + status + ")."
                     : "Pokematch refused the battle log (HTTP " + status + ").";
            return new PokematchException(string.IsNullOrWhiteSpace(detail) ? text : text + " " + detail.Trim(), r.Status);
        }
    }
}
