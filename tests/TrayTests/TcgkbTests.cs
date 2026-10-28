using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PtcglAutoBattleLog.Core;
using PtcglAutoBattleLog.Tray.Logic;

// The tcgkb connection (docs/TCGKB-API.md): the PTCGL accounts found in the plugin's own sealed
// logs, the device flow and the claims against a fake tcgkb, and the saved connection.
static partial class Program
{
    const string HashAsh = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>A tcgkb that answers from a script: every request is recorded, each handler sees it.</summary>
    sealed class FakeTcgkb
    {
        public readonly List<TcgkbRequest> Requests = new List<TcgkbRequest>();
        public Func<TcgkbRequest, TcgkbResponse> Handle = r => Reply(404, "{\"error\":\"not_found\"}");

        public Task<TcgkbResponse> Send(TcgkbRequest r, CancellationToken ct)
        {
            Requests.Add(r);
            return Task.FromResult(Handle(r));
        }

        public static TcgkbResponse Reply(int status, string body) => new TcgkbResponse { Status = status, Body = body };
        public static string Field(TcgkbRequest r, string name) =>
            (r.Body ?? "").Split('&').Select(p => p.Split('=')).Where(p => p.Length == 2 && Uri.UnescapeDataString(p[0]) == name)
                          .Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
    }

    const string CodeJson = "{\"device_code\":\"dev-1\",\"user_code\":\"WDJB-MJHT\",\"verification_uri\":\"https://tcgkb.app/link\"," +
                            "\"verification_uri_complete\":\"https://tcgkb.app/link?code=WDJB-MJHT\",\"expires_in\":600,\"interval\":5}";
    static string TokenJson(string access, string refresh, int expiresIn = 3600) =>
        "{\"access_token\":\"" + access + "\",\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn + ",\"refresh_token\":\"" + refresh + "\",\"scope\":\"claims:write logs:upload\"}";
    const string MeJson = "{\"user\":{\"name\":\"Manu\"},\"accounts\":[{\"accountHash\":\"" + HashAsh + "\",\"name\":\"Ash\",\"status\":\"verified\"},{\"accountHash\":\"bad\",\"name\":\"x\",\"status\":\"claimed\"}]}";

    static byte[] Xor(byte[] b) => b.Select(x => (byte)(x ^ 0x5A)).ToArray();

    static void SealSummaryTests()
    {
        Console.WriteLine("33. SealSummary + PtcglAccountScanner: the PTCGL accounts in the plugin's sealed logs");
        var folder = Path.Combine(TempDir(), "PTCGL Battle Logs");
        var identity = new SigningIdentity(Path.Combine(TempDir(), "identity"), new TestRsa());
        identity.Load(null);
        var mode = SealMode.Footer;
        var store = new LogStore(folder) { Sealer = new LogSealer(identity, "1.1.0", "1.24.0") { Mode = () => mode } };
        var t = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc);
        int n = 0;
        string Save(string me, string myId, bool? won, string matchId = null, bool manual = false)
        {
            n++;
            var start = t.AddHours(n);
            return store.Save(new MatchRecord
            {
                MatchId = manual ? null : matchId ?? "m-" + n, StartUtc = start, EndUtc = manual ? (DateTime?)null : start.AddMinutes(10), Won = won,
                Winner = won == null ? (int?)null : won == true ? 1 : 2, MySeat = 1, MyName = me, MyAccountId = myId,
                OpponentName = "Gary", OpponentAccountId = "gary-id", ManualOnly = manual, Capture = LogSealer.CaptureAuto,
            }, "Setup\nTurn " + n, null);
        }

        var ashHash = LogSealer.AccountHash("ash-id");
        var footer = Save("Ash", "ash-id", true);
        var s = SealSummary.TryRead(footer);
        Check(s != null && s.PlayerName == "Ash" && s.PlayerAccountHash == ashHash && s.MatchId == "m-1" && s.Result == "VICTORY" &&
              s.KeyId == identity.KeyId && s.SealFile == null && s.LogFile == footer && s.SealedUtc.Kind == DateTimeKind.Utc,
              "footer seal: player, account hash, match, result, key");
        Check(s.Version == 2 && s.DeckSha256 == null && s.ReadDeck() == null, "v2, no deck: nothing to send");
        Check(s.Queue == null && s.Format == null, "no queue or format known: null");

        // A footer seal of an earlier build (v1): same fields, no deck.
        var block = File.ReadAllText(footer);
        var v1 = Path.Combine(folder, "2026-10-01 10.00.00 - VICTORY vs Gary.txt");
        File.WriteAllText(v1, block.Replace("SEAL v2", "SEAL v1").Replace("{\"v\":2,", "{\"v\":1,").Replace(",\"deckSha256\":null,\"queue\":null,\"format\":null", ""));
        var old = SealSummary.TryRead(v1);
        Check(old != null && old.Version == 1 && old.PlayerAccountHash == ashHash && old.MatchId == "m-1" && old.DeckSha256 == null, "a v1 seal is read too");
        File.WriteAllText(v1, block.Replace("{\"v\":2,", "{\"v\":1,"));
        Check(SealSummary.TryRead(v1) == null, "a v2 BEGIN line with v 1: not a seal");
        File.Delete(v1);

        mode = SealMode.Sidecar;
        var sidecar = Save("Ash \"the\" Kétchum ★", "ash-id", false);
        s = SealSummary.TryRead(sidecar);
        Check(s != null && s.SealFile == LogStore.SidecarPathFor(sidecar) && s.Result == "DEFEAT", "sidecar seal found beside the .txt");
        Check(s?.PlayerName == "Ash \"the\" Kétchum ★", "escaped name read back exactly: " + s?.PlayerName);

        mode = SealMode.Off;
        Check(SealSummary.TryRead(Save("Ash", "ash-id", true)) == null, "unsealed log: null");
        mode = SealMode.Footer;
        Check(SealSummary.TryRead(Path.Combine(folder, "missing.txt")) == null, "missing file: null");
        var noId = SealSummary.TryRead(Save("Ash", null, true));
        Check(noId != null && noId.PlayerAccountHash == null, "no account id in the client: no hash");

        Save("Misty", "misty-id", true);
        Save("Ash2", "ash-id", true);                    // the same account, renamed
        Save("Ash2", "ash-id", null, manual: true);      // an Export click: counted, never evidence
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a battle log");
        Check(SealSummary.Unescape("a\\u00e9\\\\\\\"\\n") == "aé\\\"\n" && SealSummary.Unescape("bad\\x") == null && SealSummary.Unescape("cut\\u12") == null,
              "JSON string unescape");

        var accounts = PtcglAccountScanner.Scan(folder);
        Check(accounts.Count == 2 && accounts[0].AccountHash == ashHash && accounts[1].Name == "Misty",
              "two accounts, most matches first: " + string.Join(", ", accounts.Select(a => a.Name + " " + a.Matches)));
        var ash = accounts[0];
        Check(ash.Matches == 4 && ash.Name == "Ash2", "renamed account: one entry, newest name, every match counted (" + ash.Matches + ")");
        Check(ash.Evidence.Count == PtcglAccountScanner.MaxEvidence && ash.Evidence.All(e => e.MatchId != null && e.Result != "EXPORTED"),
              "at most 3 evidence logs, tracked matches only");
        Check(ash.Evidence.Select(e => e.SealedUtc).SequenceEqual(ash.Evidence.Select(e => e.SealedUtc).OrderByDescending(d => d)), "evidence newest first");
        Check(PtcglAccountScanner.Scan(Path.Combine(folder, "nope")).Count == 0 && PtcglAccountScanner.Scan(null).Count == 0, "missing folder: no accounts");

        var claim = TcgkbConnection.BuildClaim(ash);
        Check(claim.AccountHash == ashHash && claim.Evidence.Count == 3, "claim: the account and its evidence");
        var withSidecar = claim.Evidence.FirstOrDefault(e => e.Seal != null);
        Check(withSidecar != null && Convert.FromBase64String(withSidecar.Seal).SequenceEqual(File.ReadAllBytes(LogStore.SidecarPathFor(sidecar))) &&
              Convert.FromBase64String(withSidecar.Log).SequenceEqual(File.ReadAllBytes(sidecar)),
              "claim evidence = the files' exact bytes, .seal included");
        foreach (var e in ash.Evidence) File.Delete(e.LogFile);
        Check(Throws<IOException>(() => TcgkbConnection.BuildClaim(ash)), "evidence gone since the scan: IOException");
    }

    static async Task TcgkbApiTests()
    {
        Console.WriteLine("34. TcgkbApi: device flow and claims against a fake tcgkb");
        var server = new FakeTcgkb();
        var api = new TcgkbApi(server.Send);
        var ct = CancellationToken.None;

        server.Handle = r => FakeTcgkb.Reply(200, CodeJson);
        var code = await api.StartDeviceAuthorizationAsync("ASH-PC", ct);
        var req = server.Requests.Last();
        Check(code.UserCode == "WDJB-MJHT" && code.VerificationUriComplete == "https://tcgkb.app/link?code=WDJB-MJHT", "device code read");
        Check(req.Method == "POST" && req.Path == "/api/device/code" && req.Bearer == null && req.ContentType == "application/x-www-form-urlencoded" &&
              FakeTcgkb.Field(req, "client_id") == TcgkbApi.ClientId && FakeTcgkb.Field(req, "scope") == "claims:write logs:upload" &&
              FakeTcgkb.Field(req, "device_name") == "ASH-PC", "device code request: form with client, scopes, device name");

        server.Handle = r => FakeTcgkb.Reply(200, CodeJson.Replace("https://tcgkb.app/link\"", "https://evil.example/link\""));
        Check((await Catch(() => api.StartDeviceAuthorizationAsync("pc", ct)) as TcgkbException)?.Code == "bad_response", "a sign-in page elsewhere is refused");
        server.Handle = r => FakeTcgkb.Reply(200, CodeJson.Replace("https://tcgkb.app/link?code", "https://evil.example/?code"));
        Check((await api.StartDeviceAuthorizationAsync("pc", ct)).VerificationUriComplete == null, "a foreign complete URI is dropped, the plain one kept");
        server.Handle = r => FakeTcgkb.Reply(200, CodeJson.Replace("WDJB-MJHT", "<script>"));
        Check(await Catch(() => api.StartDeviceAuthorizationAsync("pc", ct)) is TcgkbException, "an odd user code is refused");
        server.Handle = r => FakeTcgkb.Reply(200, "<html>");
        Check((await Catch(() => api.StartDeviceAuthorizationAsync("pc", ct)) as TcgkbException)?.Code == "bad_response", "not JSON: bad_response");
        server.Handle = r => throw new IOException("offline");
        Check((await Catch(() => api.StartDeviceAuthorizationAsync("pc", ct)) as TcgkbException)?.Code == "network", "network failure: network");

        foreach (var url in new[] { "https://tcgkb.app/link", "https://tcgkb.app/link?code=AB-12" })
            Check(TcgkbApi.IsOwnPage(url), "own page: " + url);
        foreach (var url in new[] { "http://tcgkb.app/link", "https://tcgkb.app.evil.example/link", "https://user@tcgkb.app/link", "https://tcgkb.app:444/link",
                                    "https://TCGKB.app/link", "https://tcgkb.app/a/../link", "https://tcgkb.app/link#x", "javascript:alert(1)", null, "" })
            Check(!TcgkbApi.IsOwnPage(url), "not an own page: " + (url ?? "null"));

        // Polling: pending, slow_down (interval + 5 s), then the tokens.
        var answers = new Queue<TcgkbResponse>(new[]
        {
            FakeTcgkb.Reply(400, "{\"error\":\"authorization_pending\"}"),
            FakeTcgkb.Reply(400, "{\"error\":\"slow_down\"}"),
            FakeTcgkb.Reply(200, TokenJson("a1", "r1")),
        });
        server.Handle = r => answers.Dequeue();
        var delays = new List<TimeSpan>();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Func<TimeSpan, CancellationToken, Task> delay = (d, c) => { delays.Add(d); now += d; return Task.CompletedTask; };
        var tokens = await api.WaitForApprovalAsync(code, delay, () => now, ct);
        req = server.Requests.Last();
        Check(tokens.AccessToken == "a1" && tokens.RefreshToken == "r1", "approved: tokens");
        Check(delays.Select(d => (int)d.TotalSeconds).SequenceEqual(new[] { 5, 5, 10 }), "waits the interval, 5 s longer after slow_down: " + string.Join(",", delays.Select(d => d.TotalSeconds)));
        Check(req.Path == "/api/device/token" && FakeTcgkb.Field(req, "grant_type") == TcgkbApi.DeviceCodeGrant && FakeTcgkb.Field(req, "device_code") == "dev-1",
              "token request: the device code grant");

        server.Handle = r => FakeTcgkb.Reply(400, "{\"error\":\"access_denied\"}");
        Check((await Catch(() => api.WaitForApprovalAsync(code, delay, () => now, ct)) as TcgkbException)?.Code == "access_denied", "declined: access_denied");
        server.Handle = r => FakeTcgkb.Reply(400, "{\"error\":\"authorization_pending\"}");
        var polls = server.Requests.Count;
        Check((await Catch(() => api.WaitForApprovalAsync(code, delay, () => now, ct)) as TcgkbException)?.Code == "expired_token" &&
              server.Requests.Count - polls == 119, "never approved: gives up when the code expires (" + (server.Requests.Count - polls) + " polls)");
        server.Handle = r => FakeTcgkb.Reply(200, TokenJson("a1", "r1").Replace("Bearer", "MAC"));
        Check((await Catch(() => api.WaitForApprovalAsync(code, delay, () => now, ct)) as TcgkbException)?.Code == "bad_response", "not a bearer token: refused");
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check(await Catch(() => api.WaitForApprovalAsync(code, (d, c) => Task.FromCanceled(c), () => now, cancelled.Token)) is OperationCanceledException, "cancel stops waiting");

        server.Handle = r => FakeTcgkb.Reply(400, "{\"error\":\"invalid_grant\"}");
        Check(await Catch(() => api.RefreshAsync("r1", ct)) is TcgkbAuthException, "refresh refused: TcgkbAuthException");
        server.Handle = r => FakeTcgkb.Reply(503, "");
        var down = await Catch(() => api.RefreshAsync("r1", ct)) as TcgkbException;
        Check(down != null && !(down is TcgkbAuthException) && down.Status == 503, "tcgkb down: not a disconnect");

        server.Handle = r => FakeTcgkb.Reply(200, MeJson);
        var me = await api.GetAccountsAsync("a1", ct);
        req = server.Requests.Last();
        Check(req.Method == "GET" && req.Path == "/api/ptcgl/accounts" && req.Bearer == "a1", "accounts: GET with the bearer");
        Check(me.User.Name == "Manu" && me.Accounts.Count == 1 && me.Accounts[0].Status == TcgkbAccount.Verified, "accounts read, a malformed hash dropped");
        server.Handle = r => FakeTcgkb.Reply(401, "");
        Check(await Catch(() => api.GetAccountsAsync("a1", ct)) is TcgkbAuthException, "401 on an API call: TcgkbAuthException");

        var claim = new ClaimRequest { AccountHash = HashAsh, Evidence = new List<ClaimEvidence> { new ClaimEvidence { FileName = "a.txt", Log = "QQ==" } } };
        server.Handle = r => FakeTcgkb.Reply(200, "{\"account\":{\"accountHash\":\"" + HashAsh + "\",\"name\":\"Ash\",\"status\":\"claimed\"}}");
        var claimed = await api.ClaimAsync("a1", claim, ct);
        req = server.Requests.Last();
        var sent = Json.Read<ClaimRequest>(req.Body);
        Check(claimed.Status == TcgkbAccount.Claimed && req.Path == "/api/ptcgl/claims" && req.ContentType == "application/json" &&
              sent.AccountHash == HashAsh && sent.Evidence.Single().Log == "QQ==" && sent.Evidence.Single().Seal == null, "claim: JSON with the evidence");
        server.Handle = r => FakeTcgkb.Reply(409, "{\"error\":\"claimed_by_other\"}");
        var taken = await Catch(() => api.ClaimAsync("a1", claim, ct)) as TcgkbException;
        Check(taken?.Code == "claimed_by_other" && taken.Message.Contains("another tcgkb user"), "claimed by someone else: worded for the player");
        server.Handle = r => FakeTcgkb.Reply(422, "{\"error\":\"invalid_evidence\",\"message\":\"bad signature in a.txt\"}");
        Check((await Catch(() => api.ClaimAsync("a1", claim, ct)))?.Message == "tcgkb could not check these battle logs. bad signature in a.txt", "server message appended");
        Check(await Catch(() => api.ClaimAsync("a1", new ClaimRequest { AccountHash = "x", Evidence = claim.Evidence }, ct)) is ArgumentException &&
              await Catch(() => api.ClaimAsync("a1", new ClaimRequest { AccountHash = HashAsh, Evidence = new List<ClaimEvidence>() }, ct)) is ArgumentException,
              "bad claims never sent");
        server.Handle = r => FakeTcgkb.Reply(404, "");
        Check((await Catch(() => api.ReleaseAsync("a1", HashAsh, ct))) == null && server.Requests.Last().Method == "DELETE" &&
              server.Requests.Last().Path == "/api/ptcgl/accounts/" + HashAsh, "release: DELETE, 404 is fine");
    }

    static async Task TcgkbConnectionTests()
    {
        Console.WriteLine("35. TcgkbConnection + TcgkbTokenStore: connect, refresh, disconnect");
        var dir = TempDir();
        var store = new TcgkbTokenStore(dir, Xor, Xor);
        var server = new FakeTcgkb();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var conn = new TcgkbConnection(new TcgkbApi(server.Send), store, () => now);
        var ct = CancellationToken.None;
        Func<TimeSpan, CancellationToken, Task> noDelay = (d, c) => Task.CompletedTask;
        int refreshes = 0;
        string validAccess = "a1";
        server.Handle = r =>
        {
            switch (r.Path)
            {
                case "/api/device/code": return FakeTcgkb.Reply(200, CodeJson);
                case "/api/device/token":
                    if (FakeTcgkb.Field(r, "grant_type") == "refresh_token")
                    {
                        if (FakeTcgkb.Field(r, "refresh_token") != "r" + (refreshes + 1)) return FakeTcgkb.Reply(400, "{\"error\":\"invalid_grant\"}");
                        refreshes++;
                        validAccess = "a" + (refreshes + 1);
                        return FakeTcgkb.Reply(200, TokenJson(validAccess, "r" + (refreshes + 1)));
                    }
                    refreshes = 0;
                    validAccess = "a1";
                    return FakeTcgkb.Reply(200, TokenJson("a1", "r1"));
                case "/api/device/revoke": return FakeTcgkb.Reply(200, "{}");
                default: return r.Bearer == validAccess ? FakeTcgkb.Reply(200, MeJson) : FakeTcgkb.Reply(401, "");
            }
        };

        Check(!conn.IsConnected && conn.UserName == null, "not connected at first");
        Check(await Catch(() => conn.GetAccountsAsync(ct)) is TcgkbAuthException, "an API call while not connected: TcgkbAuthException");
        DeviceAuthorization shown = null;
        var name = await conn.ConnectAsync("ASH-PC", a => shown = a, noDelay, ct);
        Check(name == "Manu" && conn.UserName == "Manu" && conn.IsConnected && shown?.UserCode == "WDJB-MJHT", "connected as the tcgkb user; the code was shown");
        var raw = File.ReadAllBytes(store.FilePath);
        Check(!Encoding.UTF8.GetString(raw).Contains("r1") && Encoding.UTF8.GetString(Xor(raw)).Contains("\"r1\""), "tokens saved only through protect");
        Check(!File.Exists(store.FilePath + ".tmp"), "no temp file left");

        Check((await conn.GetAccountsAsync(ct)).User.Name == "Manu" && refreshes == 0, "fresh access token used as is");
        now = now.AddMinutes(59.5);
        Check((await conn.GetAccountsAsync(ct)).Accounts.Count == 1 && refreshes == 1 && store.Load().RefreshToken == "r2", "refreshed a minute before expiry, rotated refresh token saved");
        validAccess = "revoked-elsewhere";
        var before = refreshes;
        // A 401 with a valid-looking token: one refresh, then the call again.
        var accounts = await Catch(() => conn.GetAccountsAsync(ct));
        Check(accounts == null && refreshes == before + 1, "401: refreshed once and retried");

        var corrupt = new TcgkbTokenStore(TempDir(), Xor, Xor);
        File.WriteAllBytes(corrupt.FilePath, new byte[] { 1, 2, 3 });
        Check(corrupt.Load() == null && !File.Exists(corrupt.FilePath), "a file that does not decrypt: deleted, not connected");

        // tcgkb revoked the connection: the refresh fails, the saved connection goes.
        store.Save(new TcgkbTokens { AccessToken = "old", AccessExpiresUtc = now.AddMinutes(-1), RefreshToken = "stale", UserName = "Manu" });
        Check(await Catch(() => conn.GetAccountsAsync(ct)) is TcgkbAuthException && !conn.IsConnected && !File.Exists(store.FilePath),
              "refresh refused: disconnected here too");

        await conn.ConnectAsync("ASH-PC", null, noDelay, ct);
        var revokes = server.Requests.Count(r => r.Path == "/api/device/revoke");
        Check(await conn.DisconnectAsync(ct) && !conn.IsConnected && server.Requests.Count(r => r.Path == "/api/device/revoke") == revokes + 1 &&
              FakeTcgkb.Field(server.Requests.Last(), "token") != null, "disconnect: revoked on tcgkb, forgotten here");
        await conn.ConnectAsync("ASH-PC", null, noDelay, ct);
        var offline = new TcgkbConnection(new TcgkbApi((r, c) => throw new IOException("offline")), store, () => now);
        Check(!await offline.DisconnectAsync(ct) && !offline.IsConnected, "disconnect offline: forgotten here anyway, reported");

        // The user's name could not be read after approval: nothing saved, the grant given back.
        server.Handle = r => r.Path == "/api/ptcgl/accounts" ? FakeTcgkb.Reply(500, "") : r.Path == "/api/device/code" ? FakeTcgkb.Reply(200, CodeJson)
                           : r.Path == "/api/device/revoke" ? FakeTcgkb.Reply(200, "{}") : FakeTcgkb.Reply(200, TokenJson("a1", "r1"));
        revokes = server.Requests.Count(r => r.Path == "/api/device/revoke");
        Check(await Catch(() => conn.ConnectAsync("pc", null, noDelay, ct)) is TcgkbException && !conn.IsConnected &&
              server.Requests.Count(r => r.Path == "/api/device/revoke") == revokes + 1, "connect half done: nothing saved, grant revoked");
    }

    static async Task LogUploaderTests()
    {
        Console.WriteLine("36. LogUploader: each match of a claimed account sent once");
        var folder = Path.Combine(TempDir(), "PTCGL Battle Logs");
        var identity = new SigningIdentity(Path.Combine(TempDir(), "identity"), new TestRsa());
        identity.Load(null);
        var logStore = new LogStore(folder) { Sealer = new LogSealer(identity, "1.2.0", "1.43.0") { Mode = () => SealMode.Footer } };
        var t = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        int n = 0;
        string Save(string me, string myId, bool manual = false, DeckList deck = null)
        {
            n++;
            var start = t.AddHours(n);
            return logStore.Save(new MatchRecord
            {
                MatchId = manual ? null : "m-" + n, StartUtc = start, EndUtc = manual ? (DateTime?)null : start.AddMinutes(10), Won = true, Winner = 1,
                MySeat = 1, MyName = me, MyAccountId = myId, OpponentName = "Gary", OpponentAccountId = "gary-id", ManualOnly = manual,
                Capture = LogSealer.CaptureAuto, MyDeckList = deck,
            }, "Setup\nTurn " + n + " - " + me + " played", null);
        }
        var ashHash = LogSealer.AccountHash("ash-id");
        var deckList = new DeckList { DeckDefinitionId = "bnb-me3", Size = 2 };
        deckList.Cards["sv8_174"] = 2;
        var olderWithDeck = Save("Ash", "ash-id", deck: deckList);
        var withDeck = Save("Ash", "ash-id", deck: deckList);   // the same deck again: one file, sent once
        Save("Ash", "ash-id");
        var deckSha = SealSummary.TryRead(withDeck).DeckSha256;
        var deckFile = LogStore.DeckPathFor(folder, deckSha);
        Save("Ash", "ash-id", manual: true);   // an Export click: never sent
        Save("Misty", "misty-id");             // not one of the player's accounts on tcgkb

        var store = new TcgkbTokenStore(TempDir(), Xor, Xor);
        store.Save(new TcgkbTokens { AccessToken = "a1", AccessExpiresUtc = DateTime.UtcNow.AddHours(1), RefreshToken = "r1", UserName = "Manu" });
        var server = new FakeTcgkb();
        var received = new List<ClaimEvidence>();
        var meJson = "{\"user\":{\"name\":\"Manu\"},\"accounts\":[{\"accountHash\":\"" + ashHash + "\",\"name\":\"Ash\",\"status\":\"claimed\"}]}";
        var serverDecks = new HashSet<string>();       // the deck files tcgkb has, by SHA-256
        Func<TcgkbRequest, TcgkbResponse> upload = r =>
        {
            var e = Json.Read<ClaimEvidence>(r.Body);
            received.Add(e);
            if (e.Deck != null) serverDecks.Add(Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(e.Deck))).ToLowerInvariant());
            var named = Regex.Match(Encoding.UTF8.GetString(Convert.FromBase64String(e.Log)), "\"deckSha256\":\"([0-9a-f]{64})\"");
            var deckRef = named.Success && serverDecks.Contains(named.Groups[1].Value) ? "{\"deckId\":\"d1\",\"version\":1,\"changed\":false}" : "null";
            return FakeTcgkb.Reply(200, "{\"added\":true,\"account\":{\"accountHash\":\"" + ashHash + "\",\"name\":\"Ash\",\"status\":\"claimed\"}," +
                                        "\"playerDeck\":\"Mega Chandelure ex\",\"opponentDeck\":\"Alakazam\",\"deckRef\":" + deckRef + "}");
        };
        Func<TcgkbRequest, TcgkbResponse> uploadAnswer = upload;
        server.Handle = r => r.Path == "/api/ptcgl/accounts" ? FakeTcgkb.Reply(200, meJson) : r.Path == "/api/ptcgl/logs" ? uploadAnswer(r) : FakeTcgkb.Reply(404, "");
        var stateDir = TempDir();
        var uploader = new LogUploader(new TcgkbConnection(new TcgkbApi(server.Send), store), stateDir);
        var ct = CancellationToken.None;

        var run = await uploader.SyncAsync(folder, ct);
        var req = server.Requests.Last();
        Check(run.Uploaded == 3 && run.Left == 0 && !run.HitLimit && run.StoppedBecause == null, "the 3 tracked matches of the claimed account sent");
        Check(received.All(e => e.FileName.EndsWith(".txt") && e.Seal == null) && req.Method == "POST" && req.Bearer == "a1" && req.ContentType == "application/json",
              "POST /api/ptcgl/logs with the bearer, footer seals in the .txt");
        Check(received.Select(e => Encoding.UTF8.GetString(Convert.FromBase64String(e.Log))).All(l => l.Contains("Ash played")) &&
              !received.Any(e => Encoding.UTF8.GetString(Convert.FromBase64String(e.Log)).Contains("Misty")), "only Ash's logs, exactly as on disk");
        var order = received.Select(e => e.FileName).ToList();
        Check(order.SequenceEqual(order.OrderByDescending(f => f, StringComparer.Ordinal)), "newest first");
        Check(run.Newest?.PlayerDeck == "Mega Chandelure ex" && run.Newest.OpponentDeck == "Alakazam", "tcgkb's decks read back");
        var sentDeck = received.Single(e => e.FileName == Path.GetFileName(withDeck));
        Check(sentDeck.Deck != null && Convert.FromBase64String(sentDeck.Deck).SequenceEqual(File.ReadAllBytes(deckFile)),
              "the newest log with the deck carries the deck file, exactly as on disk");
        Check(received.Single(e => e.FileName == Path.GetFileName(olderWithDeck)).Deck == null && received.Count(e => e.Deck != null) == 1,
              "the older log with the same deck sends only its seal (tcgkb links it by deckSha256)");
        Check(uploader.LoadState().Decks.SequenceEqual(new[] { deckSha }), "the deck tcgkb has is remembered");
        Check(server.Requests.Count(r => r.Path == "/api/ptcgl/logs" && r.Body.Contains("\"deck\":")) == 1, "\"deck\" is left out of the request without one");
        var changed = SealSummary.TryRead(withDeck);
        var deckBytes = File.ReadAllBytes(deckFile);
        File.WriteAllText(deckFile, "{\"v\":1}");
        Check(changed.ReadDeck() == null && TcgkbConnection.BuildEvidence(changed, withDeck: true).Deck == null,
              "a deck file that no longer matches its seal is not sent (the log still is)");
        File.WriteAllBytes(deckFile, deckBytes);
        var otherStore = new LogStore(Path.Combine(TempDir(), "other")) { Sealer = logStore.Sealer };
        var claimLog = otherStore.Save(new MatchRecord
        {
            MatchId = "claim-1", StartUtc = t, EndUtc = t.AddMinutes(10), Won = true, Winner = 1, MySeat = 1, MyName = "Ash", MyAccountId = "ash-id",
            OpponentName = "Gary", Capture = LogSealer.CaptureAuto, MyDeckList = deckList,
        }, "Setup", null);
        Check(SealSummary.TryRead(claimLog).ReadDeck() != null && TcgkbConnection.BuildEvidence(SealSummary.TryRead(claimLog)).Deck == null,
              "claims never carry the deck");
        Check(uploader.LoadState().Uploaded.Count == 3 && File.Exists(uploader.StatePath), "what was sent is remembered");

        var calls = server.Requests.Count;
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Uploaded == 0 && server.Requests.Count - calls == 1, "a second run sends nothing (only the accounts list)");

        Save("Ash", "ash-id");
        uploadAnswer = r => FakeTcgkb.Reply(503, "");
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Uploaded == 0 && run.Left == 1 && run.StoppedBecause != null, "tcgkb down: stops, the log left for later");
        uploadAnswer = upload;
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Uploaded == 1 && run.Left == 0, "sent on the next run");

        Save("Ash", "ash-id");
        uploadAnswer = r => FakeTcgkb.Reply(422, "{\"error\":\"invalid_log\",\"message\":\"bad signature\"}");
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Rejected == 1 && uploader.LoadState().Rejected.Count == 1, "an invalid log: refused once");
        uploadAnswer = upload;
        calls = server.Requests.Count;
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Uploaded == 0 && run.Rejected == 0 && server.Requests.Count - calls == 1, "never sent again");

        Save("Ash", "ash-id");
        uploadAnswer = r => FakeTcgkb.Reply(403, "{\"error\":\"not_claimed\"}");
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Uploaded == 0 && run.StoppedBecause == null && !uploader.LoadState().Uploaded.Contains(LogUploader.Key(SealSummary.TryRead(LogUploader.Pending(folder, new HashSet<string> { ashHash }, new HashSet<string>())[0].LogFile))),
              "not claimed (any more): skipped, not remembered");

        uploadAnswer = upload;
        for (int i = 0; i < LogUploader.MaxPerRun + 5; i++) Save("Ash", "ash-id");
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Uploaded == LogUploader.MaxPerRun && run.HitLimit && run.Left == 6, "at most " + LogUploader.MaxPerRun + " per run, the rest next (" + run.Left + " left)");
        run = await uploader.SyncAsync(folder, ct);
        Check(run.Uploaded == 6 && !run.HitLimit && run.Left == 0, "the next run sends the rest");

        server.Handle = r => FakeTcgkb.Reply(401, "");
        Check(await Catch(() => uploader.SyncAsync(folder, ct)) is TcgkbAuthException, "connection gone: TcgkbAuthException");

        File.WriteAllText(uploader.StatePath, "not json");
        Check(uploader.LoadState().Uploaded.Count == 0, "an unreadable state file: empty, logs offered again");
        Check(LogUploader.Pending(folder, new HashSet<string>(), new HashSet<string>()).Count == 0 &&
              LogUploader.Pending(Path.Combine(folder, "nope"), new HashSet<string> { ashHash }, new HashSet<string>()).Count == 0, "no accounts or no folder: nothing pending");
    }
}
