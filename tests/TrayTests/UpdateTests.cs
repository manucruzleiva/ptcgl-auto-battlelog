using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtcglAutoBattleLog;
using PtcglAutoBattleLog.Tray.Logic;

static partial class Program
{
    const string Owner = "manucruzleiva";
    const string Repo = "ptcgl-auto-battlelog";
    const string GoodUrl = "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe";

    // The example in docs/ARCHITECTURE.md, with a real-looking hash in place of the placeholder.
    static readonly string ContractManifestJson = @"{
  ""version"": ""1.2.0"",
  ""tag"": ""v1.2.0"",
  ""releasedUtc"": ""2026-11-01T12:00:00Z"",
  ""notesUrl"": ""https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.2.0"",
  ""assets"": [
    {
      ""name"": ""PtcglAutoBattleLog.exe"",
      ""url"": ""https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe"",
      ""size"": 812345,
      ""sha256"": """ + new string('a', 64) + @"""
    }
  ]
}";

    static UpdateChecker Checker(string current = "1.1.0", Func<string, CancellationToken, Task<Stream>> open = null) =>
        new UpdateChecker(Owner, Repo, current, open ?? ((u, ct) => throw new InvalidOperationException("no network in tests")));

    static void ManifestTests()
    {
        Console.WriteLine("8. UpdateManifest");
        var m = UpdateManifest.Parse(ContractManifestJson);
        Check(m.Version == "1.2.0" && m.Tag == "v1.2.0" && m.ReleasedUtc == "2026-11-01T12:00:00Z" &&
              m.NotesUrl == "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.2.0", "contract example: release fields");
        var a = m.Find(UpdateChecker.ExeAssetName);
        Check(a != null && a.Name == "PtcglAutoBattleLog.exe" && a.Url == GoodUrl && a.Size == 812345 && a.Sha256 == new string('a', 64), "contract example: exe asset");
        var verbatim = UpdateManifest.Parse(ContractManifestJson.Replace(new string('a', 64), "<64 lowercase hex chars>"));
        Check(verbatim.Find(UpdateChecker.ExeAssetName)?.Sha256 == "<64 lowercase hex chars>", "verbatim contract example (placeholder hash) parses");
        Check(m.Find("update.json") == null && m.Find(null) == null && m.Find("ptcglautobattlelog.exe") == null, "Find is exact");
        Check(UpdateManifest.Parse("{\"version\":\"1.0.0\"}").Assets.Count == 0, "no assets -> empty list");
        Check(UpdateManifest.Parse("{\"version\":\"1.0.0\",\"assets\":[null,{\"name\":\"x\"}],\"extra\":1}").Assets.Count == 1, "null assets dropped, unknown fields ignored");
        foreach (var bad in new[] { "", "{", "null", "[]", "{}", "{\"version\":\"\"}", "{\"version\":\"1\",\"assets\":7}", "{\"version\":\"1\",\"assets\":[{\"size\":\"big\"}]}" })
            Check(Throws<FormatException>(() => UpdateManifest.Parse(bad)), "invalid manifest -> FormatException: " + bad);
        // The serializer is lenient about some types; such a manifest parses but is never "newer".
        Check(!Checker().IsNewer(UpdateManifest.Parse("{\"version\":1.2}")) && !Checker().IsNewer(UpdateManifest.Parse("{\"version\":\" 9.9.9\"}")), "odd versions parse but are never offered");
    }

    static void VersionTests()
    {
        Console.WriteLine("9. Versions");
        foreach (var (s, v) in new[] { ("1.2.3", "1.2.3"), ("v1.2.3", "1.2.3"), ("V1.2.3", "1.2.3"), ("1.2.3.0", "1.2.3"), ("0.0.1", "0.0.1"),
                                       ("10.20.30", "10.20.30"), ("v0.0.0.0", "0.0.0"), ("999999999.0.0", "999999999.0.0") })
        {
            var ok = UpdateChecker.TryParseVersion(s, out var parsed);
            Check(ok && parsed.ToString() == v && parsed.Revision == -1, "parses " + s + " -> " + parsed);
        }
        foreach (var s in new[] { null, "", "v", "1", "1.2", "1.2.3.4", "1.2.3.00", "1.2.3.0.0", "1.2.3-beta", "1.2.3-rc.1", "1.2.3+build", "1.2.3 ", " 1.2.3",
                                  "01.2.3", "1.02.3", "1.2.03", "1.2.3.", ".1.2.3", "1..3", "-1.2.3", "+1.2.3", "1.-2.3", "1.2.x", "vv1.2.3", "v 1.2.3",
                                  "\u0661.\u0662.\u0663", "\uFF11.\uFF12.\uFF13", "1.2.3\0", "1.2.9999999999", "1,2,3", "1.2.3\n" })
            Check(!UpdateChecker.TryParseVersion(s, out var v) && v == null, "rejects " + (s == null ? "(null)" : "\"" + s.Replace("\0", "\\0").Replace("\n", "\\n") + "\""));

        var c = Checker("1.1.0");
        bool Newer(UpdateChecker ch, string version) => ch.IsNewer(new UpdateManifest { Version = version });
        Check(Newer(c, "1.2.0") && Newer(c, "1.1.1") && Newer(c, "2.0.0") && Newer(c, "v1.1.1") && Newer(c, "1.10.0"), "greater versions are newer (numeric, not text)");
        Check(!Newer(c, "1.1.0") && !Newer(c, "1.1.0.0") && !Newer(c, "v1.1.0") && !Newer(c, "1.0.9") && !Newer(c, "0.9.9"), "equal or lower versions are not");
        Check(!Newer(c, "1.2.0-beta") && !Newer(c, "9.0.0-rc1") && !Newer(c, "1.2.0+x") && !Newer(c, "garbage") && !Newer(c, null) && !c.IsNewer(null), "pre-releases and garbage are not");
        Check(Newer(Checker("1.2.0-rc1"), "1.2.0") && !Newer(Checker("1.2.0-rc1"), "1.1.9") && Newer(Checker("1.2.0-rc1"), "1.2.1"), "a pre-release build updates to its release");
        Check(!Newer(Checker("1.2.0+abc"), "1.2.0") && Newer(Checker("1.2.0+abc"), "1.2.1"), "build metadata does not change precedence");
        Check(!Newer(Checker("dev"), "9.9.9") && !Newer(Checker("1.2.0-"), "1.2.0"), "unparseable current version never updates");
        Check(UpdateChecker.TryParseVersion(BuildInfo.Version, out _), "this build's version parses: " + BuildInfo.Version);

        // Installing over a newer copy (an old download run again) must not downgrade it.
        Check(UpdateChecker.IsInstalledNewer("1.2.0", "1.1.0") && UpdateChecker.IsInstalledNewer("1.2.0+16fdb0f52796cca94b7a9eb700cc6ae08187c70f", "1.1.0") &&
              UpdateChecker.IsInstalledNewer("1.10.0", "1.9.9") && UpdateChecker.IsInstalledNewer(" 2.0.0 ", "1.1.0+abc"), "a newer installed copy (ProductVersion with +commit) is newer");
        Check(!UpdateChecker.IsInstalledNewer("1.1.0", "1.1.0") && !UpdateChecker.IsInstalledNewer("1.1.0+aaa", "1.1.0+bbb") &&
              !UpdateChecker.IsInstalledNewer("1.0.9", "1.1.0"), "the same or an older one is not (reinstall, upgrade)");
        Check(UpdateChecker.IsInstalledNewer("1.2.0", "1.2.0-rc1") && !UpdateChecker.IsInstalledNewer("1.2.0-rc1", "1.2.0") &&
              !UpdateChecker.IsInstalledNewer("1.2.0-rc1", "1.2.0-rc2") && UpdateChecker.IsInstalledNewer("1.3.0-rc1", "1.2.0"), "a pre-release comes before its release");
        Check(!UpdateChecker.IsInstalledNewer(null, "1.1.0") && !UpdateChecker.IsInstalledNewer("", "1.1.0") && !UpdateChecker.IsInstalledNewer("dev", "1.1.0") &&
              !UpdateChecker.IsInstalledNewer("9.9.9", "dev") && !UpdateChecker.IsInstalledNewer("1.2.0-", "1.1.0") && !UpdateChecker.IsInstalledNewer("1.2", "1.1.0"),
              "unreadable versions: not newer (the copy is replaced, as before)");
    }

    static void TrustedUrlTests()
    {
        Console.WriteLine("10. Trusted asset URLs");
        var c = new UpdateChecker(BuildInfo.RepoOwner, BuildInfo.RepoName, BuildInfo.Version, (u, ct) => Task.FromResult<Stream>(Stream.Null));
        Check(c.ManifestUrl == "https://github.com/" + BuildInfo.RepoOwner + "/" + BuildInfo.RepoName + "/releases/latest/download/update.json", "manifest URL: " + c.ManifestUrl);
        foreach (var bad in new[] { "", "a/b", "evil.com/x", "..", ".", "o?x", "o#x", "o@x", "o x", "o\\x", "ö" })
            Check(Throws<ArgumentException>(() => new UpdateChecker(bad, Repo, "1.0.0", (u, ct) => null)) &&
                  Throws<ArgumentException>(() => new UpdateChecker(Owner, bad, "1.0.0", (u, ct) => null)), "constructor rejects owner/repo \"" + bad + "\"");

        var t = Checker();
        foreach (var good in new[]
        {
            GoodUrl,
            "https://github.com/ManuCruzLeiva/PTCGL-Auto-BattleLog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/update.json",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/1.2.0/PtcglAutoBattleLog.exe",
        })
            Check(t.IsTrustedAssetUrl(good), "trusted: " + good);

        const string P = "/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe";
        foreach (var bad in new[]
        {
            null, "", " ", "not a url", P, "github.com" + P, "//github.com" + P, "https:github.com" + P, "https:/github.com" + P,
            "http://github.com" + P, "ftp://github.com" + P, "file:///C:/PtcglAutoBattleLog.exe", "HTTPS://github.com" + P,
            "https://GitHub.com" + P, "https://github.com." + P, "https://www.github.com" + P, "https://api.github.com" + P,
            "https://github.com.evil.com" + P, "https://evilgithub.com" + P, "https://evil.com" + P, "https://raw.githubusercontent.com" + P,
            "https://objects.githubusercontent.com" + P, "https://140.82.112.3" + P, "https://[::1]" + P, "https://g\u0456thub.com" + P,
            "https://github.com@evil.com" + P, "https://evil.com@github.com" + P, "https://user:pass@github.com" + P, "https://:@github.com" + P,
            "https://github.com:443" + P, "https://github.com:8443" + P, "https://github.com:" + P,
            "https://github.com/someone/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog-fork/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelo/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/evil/repo/releases/download/../../../manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/../../../../evil/x/releases/download/v1/x.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/../../../../evil.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/./v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/%2e%2e/%2e%2e/evil/x.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/..%2F..%2Fevil.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/..%5C..%5Cevil.exe",
            "https://github.com/manucruzleiva%2Fptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog%2Eexe",
            "https://github.com\\manucruzleiva\\ptcgl-auto-battlelog\\releases\\download\\v1.2.0\\PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0\\..\\..\\x.exe",
            "https://evil.com\\@github.com" + P,
            "https://github.com" + P + "?raw=true", "https://github.com" + P + "?", "https://github.com" + P + "#frag", "https://github.com" + P + "#",
            "https://evil.com/?https://github.com" + P, "https://evil.com/#https://github.com" + P, "https://evil.com/https://github.com" + P,
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.2.0",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/archive/refs/tags/v1.2.0.zip",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/Releases/Download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/sub/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe/",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/.../PtcglAutoBattleLog.exe",
            "https://github.com//manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe;.txt",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/Ptcgl AutoBattleLog.exe",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.\u00e9xe",
            " " + "https://github.com" + P, "https://github.com" + P + " ", "https://github.com" + P + "\n", "https://github.com" + P + "\0",
            "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/Ptcgl\tAutoBattleLog.exe",
            "https://github.com" + P.Replace("v1.2.0", new string('v', 3000)),
        })
            Check(!t.IsTrustedAssetUrl(bad), "untrusted: " + (bad == null ? "(null)" : bad.Replace("\n", "\\n").Replace("\0", "\\0").Replace("\t", "\\t")));
    }

    /// <summary>Serves a fixed byte array, or generates bytes forever; counts what was read.</summary>
    sealed class FakeDownload : Stream
    {
        readonly byte[] data;
        readonly long length;
        readonly Action<long> onRead;
        long pos;
        public long Served => pos;
        public bool Disposed;
        public FakeDownload(byte[] data, long length = -1, Action<long> onRead = null) { this.data = data; this.length = length < 0 ? data.Length : length; this.onRead = onRead; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, Math.Min(length - pos, 1000));   // small chunks, like a network
            for (int i = 0; i < n; i++) buffer[offset + i] = data[(pos + i) % data.Length];
            pos += n;
            onRead?.Invoke(pos);
            return n;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    static UpdateAsset AssetFor(byte[] content, string url = GoodUrl) =>
        new UpdateAsset { Name = UpdateChecker.ExeAssetName, Url = url, Size = content.Length, Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() };

    static async Task DownloadTests()
    {
        Console.WriteLine("11. DownloadAsync");
        var exe = Encoding.ASCII.GetBytes("MZ new tray exe " + string.Join(",", Enumerable.Range(0, 30000)));
        var dir = Path.Combine(TempDir(), "updates");
        string requested = null;
        int opens = 0;
        FakeDownload last = null;
        Func<string, CancellationToken, Task<Stream>> Serve(Func<FakeDownload> make) => (u, ct) => { requested = u; opens++; last = make(); return Task.FromResult<Stream>(last); };
        bool Clean() => !Directory.Exists(dir) || !Directory.GetFiles(dir).Any(f => f.EndsWith(".partial"));
        string[] Exes() => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.exe") : new string[0];

        // Success; the hash is compared case-insensitively.
        var asset = AssetFor(exe);
        asset.Sha256 = asset.Sha256.ToUpperInvariant();
        var c = Checker(open: Serve(() => new FakeDownload(exe)));
        var final = await c.DownloadAsync(asset, dir, CancellationToken.None);
        Check(final == Path.Combine(dir, "PtcglAutoBattleLog-1.2.0.exe"), "verified file at updates/PtcglAutoBattleLog-<version>.exe: " + final);
        Check(File.ReadAllBytes(final).SequenceEqual(exe) && requested == GoodUrl && last.Disposed, "content, requested URL, response disposed");
        Check(Clean(), "no .partial left after success");
        var again = await c.DownloadAsync(AssetFor(exe), dir, CancellationToken.None);
        Check(again == final && File.ReadAllBytes(final).SequenceEqual(exe), "a previous download is replaced");

        async Task Refused(string what, UpdateAsset a, Func<FakeDownload> make, bool expectOpen, string messagePart = null)
        {
            File.Delete(final);
            opens = 0;
            var ch = Checker(open: Serve(make));
            var e = await Catch(() => ch.DownloadAsync(a, dir, CancellationToken.None));
            Check(e is UpdateVerificationException && (messagePart == null || e.Message.Contains(messagePart)),
                  what + " -> " + (e == null ? "no exception" : e.GetType().Name + ": " + e.Message));
            Check(Clean() && Exes().Length == 0, what + ": nothing left in updates/");
            Check(expectOpen ? opens == 1 : opens == 0, what + (expectOpen ? ": downloaded once" : ": refused before downloading"));
        }

        await Refused("too few bytes", AssetFor(exe), () => new FakeDownload(exe.Take(exe.Length - 1).ToArray()), true, "declares");
        var bigger = AssetFor(exe);
        FakeDownload endless = null;
        await Refused("more bytes than declared", bigger, () => endless = new FakeDownload(exe, 50L * 1024 * 1024), true, "larger");
        Check(endless.Served <= exe.Length + 1000, "stopped reading right after the declared size (" + endless.Served + " bytes read)");
        var tampered = (byte[])exe.Clone();
        tampered[100] ^= 1;
        await Refused("hash mismatch", AssetFor(exe), () => new FakeDownload(tampered), true, "SHA-256");
        await Refused("untrusted URL", AssetFor(exe, "https://evil.com/PtcglAutoBattleLog.exe"), () => new FakeDownload(exe), false, "untrusted");
        await Refused("redirect-looking URL", AssetFor(exe, "https://github.com@evil.com/x.exe"), () => new FakeDownload(exe), false);
        var huge = AssetFor(exe); huge.Size = UpdateChecker.MaxAssetBytes + 1;
        await Refused("manifest declares > 100 MB", huge, () => new FakeDownload(exe, huge.Size), false, "bytes");
        var zero = AssetFor(exe); zero.Size = 0;
        await Refused("size 0", zero, () => new FakeDownload(exe), false);
        var negative = AssetFor(exe); negative.Size = -5;
        await Refused("negative size", negative, () => new FakeDownload(exe), false);
        foreach (var sha in new[] { null, "", "<64 lowercase hex chars>", new string('a', 63), new string('a', 65), new string('g', 64), "0x" + new string('a', 62), new string(' ', 64) })
        {
            var badSha = AssetFor(exe); badSha.Sha256 = sha;
            await Refused("bad sha256 \"" + sha + "\"", badSha, () => new FakeDownload(exe), false);
        }
        foreach (var name in new[] { null, "", "..", "../PtcglAutoBattleLog.exe", "..\\PtcglAutoBattleLog.exe", "sub/x.exe", "C:x.exe", ".exe" })
        {
            var badName = AssetFor(exe); badName.Name = name;
            await Refused("unsafe asset name \"" + name + "\"", badName, () => new FakeDownload(exe), false);
        }
        Check(await Catch(() => Checker().DownloadAsync(null, dir, CancellationToken.None)) is ArgumentNullException, "null asset -> ArgumentNullException");

        // Cancellation mid-download.
        File.Delete(final);
        var cts = new CancellationTokenSource();
        var cc = Checker(open: Serve(() => new FakeDownload(exe, -1, read => { if (read > 5000) cts.Cancel(); })));
        var ce = await Catch(() => cc.DownloadAsync(AssetFor(exe), dir, cts.Token));
        Check(ce is OperationCanceledException, "cancellation -> OperationCanceledException (" + ce?.GetType().Name + ")");
        Check(Clean() && Exes().Length == 0, "cancellation leaves no partial");
        var pre = new CancellationTokenSource(); pre.Cancel();
        Check(await Catch(() => Checker(open: (u, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(new FakeDownload(exe)); })
                                .DownloadAsync(AssetFor(exe), dir, pre.Token)) is OperationCanceledException && Clean(), "already cancelled -> nothing written");

        // Network errors propagate as they are.
        var ne = await Catch(() => Checker(open: (u, ct) => throw new IOException("connection reset")).DownloadAsync(AssetFor(exe), dir, CancellationToken.None));
        Check(ne is IOException && !(ne is UpdateVerificationException) && Clean(), "network error propagates, no partial");
        var fe = await Catch(() => Checker(open: (u, ct) => Task.FromException<Stream>(new IOException("dns"))).DownloadAsync(AssetFor(exe), dir, CancellationToken.None));
        Check(fe is IOException && Clean(), "faulted task propagates");
        var midway = await Catch(() => Checker(open: Serve(() => new FakeDownload(exe, -1, read => { if (read > 3000) throw new IOException("reset"); })))
                                       .DownloadAsync(AssetFor(exe), dir, CancellationToken.None));
        Check(midway is IOException && Clean() && Exes().Length == 0, "error mid-stream deletes the partial");

        // A stale .partial from a crash is overwritten, not appended to.
        File.WriteAllText(Path.Combine(dir, "PtcglAutoBattleLog-1.2.0.exe.partial"), "stale junk");
        var ok2 = await Checker(open: Serve(() => new FakeDownload(exe))).DownloadAsync(AssetFor(exe), dir, CancellationToken.None);
        Check(File.ReadAllBytes(ok2).SequenceEqual(exe) && Clean(), "stale .partial replaced");

        // A tag that is not a version still gets a unique, safe name.
        var odd = AssetFor(exe, "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/nightly/PtcglAutoBattleLog.exe");
        var oddPath = await Checker(open: Serve(() => new FakeDownload(exe))).DownloadAsync(odd, dir, CancellationToken.None);
        Check(Path.GetFileName(oddPath) == "PtcglAutoBattleLog-" + odd.Sha256.Substring(0, 12) + ".exe", "non-version tag -> hash in the name: " + Path.GetFileName(oddPath));
    }

    static async Task FetchManifestTests()
    {
        Console.WriteLine("12. FetchManifestAsync");
        string requested = null;
        Func<string, CancellationToken, Task<Stream>> Serve(byte[] body) => (u, ct) => { requested = u; return Task.FromResult<Stream>(new MemoryStream(body)); };
        var m = await Checker(open: Serve(Encoding.UTF8.GetBytes(ContractManifestJson))).FetchManifestAsync(CancellationToken.None);
        Check(m.Version == "1.2.0" && m.Find(UpdateChecker.ExeAssetName)?.Size == 812345, "fetched and parsed");
        Check(requested == "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/update.json", "requested the manifest URL");
        Check(Checker().IsNewer(m) && !Checker("1.2.0").IsNewer(m), "contract manifest is newer than 1.1.0, not than 1.2.0");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(ContractManifestJson)).ToArray();
        Check((await Checker(open: Serve(bom)).FetchManifestAsync(CancellationToken.None)).Version == "1.2.0", "BOM tolerated");
        var big = Encoding.UTF8.GetBytes("{\"version\":\"1.2.0\",\"pad\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
        Check(await Catch(() => Checker(open: Serve(big)).FetchManifestAsync(CancellationToken.None)) is UpdateVerificationException, "oversize manifest refused");
        Check(await Catch(() => Checker(open: Serve(Encoding.UTF8.GetBytes("<html>rate limited</html>"))).FetchManifestAsync(CancellationToken.None)) is FormatException, "HTML instead of JSON -> FormatException");
        Check(await Catch(() => Checker(open: (u, ct) => Task.FromResult<Stream>(null)).FetchManifestAsync(CancellationToken.None)) is IOException, "no response -> IOException");
    }

    static void SelfUpdateTests()
    {
        Console.WriteLine("13. SelfUpdate");
        var dir = TempDir();
        var installed = Path.Combine(dir, "PtcglAutoBattleLog.exe");
        var updates = Path.Combine(dir, "updates");
        Directory.CreateDirectory(updates);
        string NewExe(string content) { var p = Path.Combine(updates, "PtcglAutoBattleLog-1.2.0.exe"); File.WriteAllText(p, content); return p; }

        File.WriteAllText(installed, "v1.1");
        var n = NewExe("v1.2");
        SelfUpdate.Swap(n, installed);
        Check(File.ReadAllText(installed) == "v1.2" && File.ReadAllText(installed + ".old") == "v1.1" && !File.Exists(n), "installed -> .old, new -> installed");

        n = NewExe("v1.3");
        SelfUpdate.Swap(n, installed);
        Check(File.ReadAllText(installed) == "v1.3" && File.ReadAllText(installed + ".old") == "v1.2", "stale .old replaced");

        SelfUpdate.CleanupOld(installed);
        Check(!File.Exists(installed + ".old"), "CleanupOld deletes .old");
        Check(NoThrow(() => SelfUpdate.CleanupOld(installed)) && NoThrow(() => SelfUpdate.CleanupOld(null)) && NoThrow(() => SelfUpdate.CleanupOld("bad\0path")), "CleanupOld never throws");

        Check(Throws<FileNotFoundException>(() => SelfUpdate.Swap(Path.Combine(updates, "missing.exe"), installed)) &&
              File.ReadAllText(installed) == "v1.3" && !File.Exists(installed + ".old"), "missing download: installed exe untouched");
        Check(Throws<ArgumentException>(() => SelfUpdate.Swap(installed, installed)) && File.ReadAllText(installed) == "v1.3", "swapping the exe with itself is refused");

        var fresh = Path.Combine(TempDir(), "PtcglAutoBattleLog.exe");
        SelfUpdate.Swap(NewExe("first"), fresh);
        Check(File.ReadAllText(fresh) == "first" && !File.Exists(fresh + ".old"), "no installed exe yet: just moved into place");

        // The second rename fails: the old exe must be back.
        n = NewExe("v1.4");
        Action<string, string> failNewIntoPlace = (src, dst) =>
        {
            if (src == n) throw new IOException("disk full");
            File.Move(src, dst);
        };
        var e = Throws<IOException>(() => SelfUpdate.Swap(n, installed, failNewIntoPlace));
        Check(e && File.ReadAllText(installed) == "v1.3" && !File.Exists(installed + ".old") && File.Exists(n), "failed swap rolled back: old exe in place, download kept");

        // Even the rename back fails (old exe still running): copied back instead.
        Action<string, string> failAllButFirst = (src, dst) =>
        {
            if (src == installed) { File.Move(src, dst); return; }
            throw new IOException("locked");
        };
        Check(Throws<IOException>(() => SelfUpdate.Swap(n, installed, failAllButFirst)) && File.ReadAllText(installed) == "v1.3", "rollback falls back to copying the old exe");
    }
}
