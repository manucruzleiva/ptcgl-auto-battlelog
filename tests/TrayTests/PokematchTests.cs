using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PtcglAutoBattleLog.Core;
using PtcglAutoBattleLog.Tray.Logic;

// Pokematch uploads (docs/POKEMATCH-API.md): the API against a fake Pokematch, the saved token, and
// the uploader over logs the plugin's own LogStore writes.
static partial class Program
{
    /// <summary>A Pokematch that answers from a script: every request is recorded, each handler sees it.</summary>
    sealed class FakePokematch
    {
        public readonly List<PokematchRequest> Requests = new List<PokematchRequest>();
        public Func<PokematchRequest, PokematchResponse> Handle = r => Reply(404, "{\"error\":\"not found\"}");

        public Task<PokematchResponse> Send(PokematchRequest r, CancellationToken ct)
        {
            Requests.Add(r);
            return Task.FromResult(Handle(r));
        }

        public static PokematchResponse Reply(int status, string body) => new PokematchResponse { Status = status, Body = body };
    }

    static PokematchMatch SampleMatch() => new PokematchMatch { Result = "win", Opponent = "Gary", Log = "Setup\nTurn 1", Source = PokematchApi.Source };

    static async Task PokematchApiTests()
    {
        Console.WriteLine("37. PokematchApi: add and replace matches against a fake Pokematch");
        Check(PokematchApi.CleanToken("  abc.DEF-123_x  ") == "abc.DEF-123_x", "token trimmed");
        Check(PokematchApi.CleanToken("Bearer abc123") == "abc123", "\"Bearer \" dropped");
        Check(PokematchApi.CleanToken("") == null && PokematchApi.CleanToken(null) == null, "empty: no token");
        Check(PokematchApi.CleanToken("abc def") == null && PokematchApi.CleanToken("abc\ndef") == null, "spaces or newlines inside: no token");
        Check(PokematchApi.CleanToken("ñandú") == null, "non-ASCII: no token");
        Check(PokematchApi.CleanToken(new string('a', PokematchApi.MaxTokenLength + 1)) == null, "too long: no token");

        var fake = new FakePokematch();
        var api = new PokematchApi(fake.Send);
        fake.Handle = r => FakePokematch.Reply(201, "{\"_id\":\"65f0c0ffee\",\"result\":\"win\"}");
        var id = await api.CreateAsync("tok", SampleMatch(), CancellationToken.None);
        var req = fake.Requests.Last();
        Check(id == "65f0c0ffee", "POST: Mongo-style _id read back: " + id);
        Check(req.Method == "POST" && req.Path == "/api/v1/tcg-matches" && req.Token == "tok", "POST /api/v1/tcg-matches with the token");
        Check(req.Body.Contains("\"log\"") && req.Body.Contains("Setup\\nTurn 1") && req.Body.Contains("\"opponent\""), "body: JSON with the log and the opponent");

        fake.Handle = r => FakePokematch.Reply(200, "{\"id\":42}");
        Check(await api.CreateAsync("tok", SampleMatch(), CancellationToken.None) == "42", "numeric id read back");
        fake.Handle = r => FakePokematch.Reply(201, "{\"match\":{\"id\":\"abc\"}}");
        Check(await api.CreateAsync("tok", SampleMatch(), CancellationToken.None) == "abc", "id nested under \"match\"");
        fake.Handle = r => FakePokematch.Reply(201, "{\"data\":{\"_id\":\"d1\"}}");
        Check(await api.CreateAsync("tok", SampleMatch(), CancellationToken.None) == "d1", "id nested under \"data\"");
        fake.Handle = r => FakePokematch.Reply(201, "");
        Check(await api.CreateAsync("tok", SampleMatch(), CancellationToken.None) == null, "no body: added, no id");
        fake.Handle = r => FakePokematch.Reply(201, "{\"id\":\"../../admin\"}");
        Check(await api.CreateAsync("tok", SampleMatch(), CancellationToken.None) == null, "an id that is not a plain id is not kept");

        fake.Handle = r => FakePokematch.Reply(200, "{}");
        Check(await api.UpdateAsync("tok", "abc", SampleMatch(), CancellationToken.None), "PUT: true");
        Check(fake.Requests.Last().Method == "PUT" && fake.Requests.Last().Path == "/api/v1/tcg-matches/abc", "PUT /api/v1/tcg-matches/:id");
        fake.Handle = r => FakePokematch.Reply(404, "{\"error\":\"Not found\"}");
        Check(!await api.UpdateAsync("tok", "abc", SampleMatch(), CancellationToken.None), "PUT 404: false (deleted on Pokematch)");
        Check(Throws<ArgumentException>(() => api.UpdateAsync("tok", "a/b", SampleMatch(), CancellationToken.None).GetAwaiter().GetResult()), "PUT refuses an id with '/'");

        foreach (var status in new[] { 401, 403 })
        {
            fake.Handle = r => FakePokematch.Reply(status, "{\"error\":\"Invalid token\"}");
            Check(await Catch(() => api.CreateAsync("tok", SampleMatch(), CancellationToken.None)) is PokematchAuthException, status + ": PokematchAuthException");
        }
        int before = fake.Requests.Count;
        Check(await Catch(() => api.CreateAsync("bad token", SampleMatch(), CancellationToken.None)) is PokematchAuthException && fake.Requests.Count == before,
              "a malformed token is never sent");

        fake.Handle = r => FakePokematch.Reply(400, "{\"message\":\"result is required\"}");
        var e = await Catch(() => api.CreateAsync("tok", SampleMatch(), CancellationToken.None)) as PokematchException;
        Check(e != null && !e.Transient && e.Status == 400 && e.Message.Contains("result is required"), "400: refused, not transient, server message kept: " + e?.Message);
        foreach (var status in new[] { 429, 500, 503 })
        {
            fake.Handle = r => FakePokematch.Reply(status, "<html>");
            e = await Catch(() => api.CreateAsync("tok", SampleMatch(), CancellationToken.None)) as PokematchException;
            Check(e != null && e.Transient, status + ": transient");
        }
        var down = new PokematchApi((r, ct) => throw new System.Net.Http.HttpRequestException("No such host"));
        e = await Catch(() => down.CreateAsync("tok", SampleMatch(), CancellationToken.None)) as PokematchException;
        Check(e != null && e.Transient && e.Status == 0 && e.Message.Contains("No such host"), "network failure: transient PokematchException");
    }

    static async Task PokematchUploaderTests()
    {
        Console.WriteLine("38. PokematchUploader + PokematchTokenStore: what goes to Pokematch, once");

        // Token store: encrypted with the given functions, corrupt = gone.
        var dataDir = TempDir();
        var tokens = new PokematchTokenStore(dataDir, Xor, Xor);
        Check(tokens.Load() == null, "no file: no token");
        tokens.Save(new PokematchToken { Token = "secret-token", SetUtc = DateTime.UtcNow });
        Check(!File.ReadAllText(tokens.FilePath).Contains("secret-token"), "the file does not hold the token in clear");
        Check(tokens.Load()?.Token == "secret-token", "token read back");
        Check(Throws<ArgumentException>(() => tokens.Save(new PokematchToken { Token = "two words" })), "a malformed token is not saved");
        File.WriteAllBytes(tokens.FilePath, new byte[] { 1, 2, 3 });
        Check(tokens.Load() == null && !File.Exists(tokens.FilePath), "corrupt file: deleted, no token");

        // Logs written by the plugin's own LogStore.
        var folder = Path.Combine(TempDir(), "PTCGL Battle Logs");
        var identity = new SigningIdentity(Path.Combine(TempDir(), "identity"), new TestRsa());
        identity.Load(null);
        var mode = SealMode.Footer;
        var store = new LogStore(folder) { Sealer = new LogSealer(identity, "1.2.0", "1.24.0") { Mode = () => mode } };
        var t0 = new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);
        string Save(int n, bool? won, string matchId, string log, bool manual = false, string existing = null) =>
            store.Save(new MatchRecord
            {
                MatchId = manual ? null : matchId, StartUtc = t0.AddHours(n), EndUtc = manual ? (DateTime?)null : t0.AddHours(n).AddMinutes(12), Won = won,
                Winner = won == null ? (int?)null : won == true ? 1 : 2, MySeat = 1, MyName = "Ash", MyAccountId = "ash-id", MyDeck = "Charizard ex",
                OpponentName = "Gary", OpponentAccountId = "gary-id", ManualOnly = manual, Capture = LogSealer.CaptureAuto,
            }, log, existing);

        var win = Save(1, true, "m-1", "Setup\nAsh wins");
        mode = SealMode.Sidecar;
        var loss = Save(2, false, "m-2", "Setup\nGary wins");
        mode = SealMode.Off;
        var unsealed = Save(3, true, "m-3", "Setup\nunsealed win");
        mode = SealMode.Footer;
        var noResult = Save(4, null, "m-4", "Setup\nnobody");
        var exported = Save(5, null, null, "Setup\nexported", manual: true);

        var c = PokematchUploader.Read(win);
        Check(c != null && c.Key == "match:m-1" && c.Match.Result == "win" && c.Match.Opponent == "Gary" && c.Match.Deck == "Charizard ex",
              "footer log: key from the seal's match id, result, opponent, deck");
        Check(c?.Match.Log == "Setup\nAsh wins", "footer log: only the Export text goes (no BOM, no seal): " + c?.Match.Log);
        Check(c?.Match.PlayedAt == "2026-10-20T19:12:00Z", "playedAt: the seal's endedUtc: " + c?.Match.PlayedAt);
        Check(c?.Match.Source == PokematchApi.Source, "source names this app");
        c = PokematchUploader.Read(loss);
        Check(c != null && c.Key == "match:m-2" && c.Match.Result == "loss" && c.Match.Log == "Setup\nGary wins", "sidecar log: same, the .txt is the Export text");
        c = PokematchUploader.Read(unsealed);
        Check(c != null && c.Key == "file:" + Path.GetFileName(unsealed) && c.Match.Deck == null && c.Match.Opponent == "Gary",
              "unsealed log: key from the file name, opponent from the file name");
        Check(PokematchUploader.LogText("﻿quoted\n\n" + SealSummary.Begin + "\nx\n\nreal\n\n" + SealSummary.Begin + "\n{}\nsig\n" + SealSummary.End + "\n")
              == "quoted\n\n" + SealSummary.Begin + "\nx\n\nreal", "a log that quotes a seal: cut at the last seal (docs/LOG-SEAL.md §2)");

        var now = DateTime.UtcNow + TimeSpan.FromMinutes(10);   // every log has settled
        var fake = new FakePokematch();
        int next = 0;
        fake.Handle = r => r.Method == "POST" ? FakePokematch.Reply(201, "{\"_id\":\"pm" + (++next) + "\"}") : FakePokematch.Reply(200, "{}");
        var stateFile = Path.Combine(dataDir, PokematchUploadState.FileName);
        var up = new PokematchUploader(new PokematchApi(fake.Send), stateFile, () => now);

        var eligible = up.Eligible(folder, DateTime.MinValue);
        Check(eligible.SequenceEqual(new[] { win, loss, unsealed }), "eligible: VICTORY and DEFEAT, oldest first; not NO-RESULT, not EXPORTED");
        Check(!eligible.Contains(noResult) && !eligible.Contains(exported), "NO-RESULT and EXPORTED stay on this computer");
        Check(up.Eligible(folder, t0.AddHours(2)).SequenceEqual(new[] { loss, unsealed }), "since: older matches are left out");
        Check(new PokematchUploader(new PokematchApi(fake.Send), stateFile, () => DateTime.UtcNow).Eligible(folder, DateTime.MinValue).Count == 0,
              "a log written in the last 2 minutes waits (the plugin may still rewrite it)");
        Check(up.Eligible(Path.Combine(folder, "missing"), DateTime.MinValue).Count == 0, "missing folder: nothing");
        Check(up.CountNew(folder, DateTime.MinValue) == 3, "CountNew: 3");

        var r1 = await up.SyncAsync("tok", folder, DateTime.MinValue, CancellationToken.None);
        Check(r1.Uploaded == 3 && r1.Updated == 0 && r1.Error == null && !r1.More, "first run: 3 uploaded");
        Check(fake.Requests.All(x => x.Token == "tok" && x.Method == "POST"), "each with the token, POST");
        var state = PokematchUploadState.Load(stateFile);
        Check(state.Find("match:m-1")?.Id == "pm1" && state.Find("match:m-1")?.Status == PokematchUpload.Uploaded, "state: Pokematch's id kept");
        Check(up.CountNew(folder, DateTime.MinValue) == 0, "CountNew after: 0");

        int sent = fake.Requests.Count;
        var r2 = await up.SyncAsync("tok", folder, DateTime.MinValue, CancellationToken.None);
        Check(r2.Uploaded + r2.Updated == 0 && fake.Requests.Count == sent, "second run: nothing sent twice");

        // The plugin rewrites the log with a longer read: the copy on Pokematch is replaced, not duplicated.
        Save(1, true, "m-1", "Setup\nAsh wins\nand a longer read", existing: win);
        var r3 = await up.SyncAsync("tok", folder, DateTime.MinValue, CancellationToken.None);
        var put = fake.Requests.Last();
        Check(r3.Updated == 1 && r3.Uploaded == 0 && put.Method == "PUT" && put.Path == "/api/v1/tcg-matches/pm1" && put.Body.Contains("longer read"),
              "rewritten log: PUT on the same Pokematch id");

        // Deleted on Pokematch: never sent again, even when rewritten.
        fake.Handle = r => r.Method == "PUT" ? FakePokematch.Reply(404, "{}") : FakePokematch.Reply(201, "{\"_id\":\"pmX\"}");
        Save(1, true, "m-1", "Setup\nAsh wins\nthird read", existing: win);
        await up.SyncAsync("tok", folder, DateTime.MinValue, CancellationToken.None);
        Check(PokematchUploadState.Load(stateFile).Find("match:m-1")?.Status == PokematchUpload.Deleted, "PUT 404: marked deleted");
        Save(1, true, "m-1", "Setup\nAsh wins\nfourth read", existing: win);
        sent = fake.Requests.Count;
        await up.SyncAsync("tok", folder, DateTime.MinValue, CancellationToken.None);
        Check(fake.Requests.Count == sent, "a match deleted on Pokematch is not sent again");

        // Refused (400): kept as refused, retried only when the log changes.
        var refusedFolder = Path.Combine(TempDir(), "logs");
        store = new LogStore(refusedFolder) { Sealer = new LogSealer(identity, "1.2.0", "1.24.0") { Mode = () => mode } };
        var a = Save(1, true, "r-1", "Setup\nLOG-ONE");
        Save(2, false, "r-2", "Setup\ntwo");
        var stateFile2 = Path.Combine(TempDir(), PokematchUploadState.FileName);
        var up2 = new PokematchUploader(new PokematchApi(fake.Send), stateFile2, () => now);
        fake.Handle = r => r.Body.Contains("LOG-ONE") ? FakePokematch.Reply(422, "{\"message\":\"bad log\"}") : FakePokematch.Reply(201, "{\"_id\":\"ok\"}");
        var r4 = await up2.SyncAsync("tok", refusedFolder, DateTime.MinValue, CancellationToken.None);
        Check(r4.Rejected == 1 && r4.Uploaded == 1 && r4.Error.Contains("bad log"), "a refused log does not stop the others");
        sent = fake.Requests.Count;
        await up2.SyncAsync("tok", refusedFolder, DateTime.MinValue, CancellationToken.None);
        Check(fake.Requests.Count == sent, "refused log: not retried while unchanged");
        fake.Handle = r => FakePokematch.Reply(201, "{\"_id\":\"ok2\"}");
        Save(1, true, "r-1", "Setup\nLOG-ONE, rewritten", existing: a);
        var r5 = await up2.SyncAsync("tok", refusedFolder, DateTime.MinValue, CancellationToken.None);
        Check(r5.Uploaded == 1 && PokematchUploadState.Load(stateFile2).Find("match:r-1")?.Status == PokematchUpload.Uploaded, "refused log rewritten: sent again");

        // Token refused, Pokematch down: stop, keep everything for later.
        var downFolder = Path.Combine(TempDir(), "logs");
        store = new LogStore(downFolder) { Sealer = new LogSealer(identity, "1.2.0", "1.24.0") { Mode = () => mode } };
        Save(1, true, "d-1", "Setup\nd1");
        Save(2, true, "d-2", "Setup\nd2");
        var stateFile3 = Path.Combine(TempDir(), PokematchUploadState.FileName);
        var up3 = new PokematchUploader(new PokematchApi(fake.Send), stateFile3, () => now);
        fake.Handle = r => FakePokematch.Reply(401, "{}");
        sent = fake.Requests.Count;
        var r6 = await up3.SyncAsync("tok", downFolder, DateTime.MinValue, CancellationToken.None);
        Check(r6.TokenRejected && fake.Requests.Count == sent + 1 && PokematchUploadState.Load(stateFile3).Uploads.Count == 0, "401: stops at once, nothing recorded");
        fake.Handle = r => FakePokematch.Reply(503, "");
        sent = fake.Requests.Count;
        var r7 = await up3.SyncAsync("tok", downFolder, DateTime.MinValue, CancellationToken.None);
        Check(r7.Waiting == 1 && r7.More && fake.Requests.Count == sent + 1 && PokematchUploadState.Load(stateFile3).Uploads.Count == 0, "503: stops, tries again next run");
        fake.Handle = r => FakePokematch.Reply(201, "{\"_id\":\"x\"}");
        var r8 = await up3.SyncAsync("tok", downFolder, DateTime.MinValue, CancellationToken.None);
        Check(r8.Uploaded == 2, "back up: both sent");

        // At most MaxPerRun per run.
        var manyFolder = Path.Combine(TempDir(), "logs");
        store = new LogStore(manyFolder);
        for (int i = 0; i < PokematchUploader.MaxPerRun + 3; i++) Save(i, i % 2 == 0, "n-" + i, "Setup\n" + i);
        var up4 = new PokematchUploader(new PokematchApi(fake.Send), Path.Combine(TempDir(), PokematchUploadState.FileName), () => now);
        var r9 = await up4.SyncAsync("tok", manyFolder, DateTime.MinValue, CancellationToken.None);
        var r10 = await up4.SyncAsync("tok", manyFolder, DateTime.MinValue, CancellationToken.None);
        Check(r9.Uploaded == PokematchUploader.MaxPerRun && r9.More && r10.Uploaded == 3 && !r10.More, "MaxPerRun per run, the rest next run");

        File.WriteAllText(stateFile, "{ not json");
        Check(PokematchUploadState.Load(stateFile).Uploads.Count == 0, "corrupt state: empty, never throws");
    }
}
