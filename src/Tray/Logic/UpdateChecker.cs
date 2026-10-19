using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// OTA updates from GitHub Releases. The manifest says what to download; this class decides
    /// whether to believe it. A file is only handed back when it came from this repository's
    /// release downloads on github.com and matches the manifest's byte count and SHA-256, so a
    /// tampered CDN response, a redirect elsewhere or a truncated download never gets installed.
    ///
    /// HTTP is injected (openUrl): the tray passes HttpClient, tests pass in-memory streams.
    /// </summary>
    public sealed class UpdateChecker
    {
        public const string ExeAssetName = "PtcglAutoBattleLog.exe";
        public const string ManifestAssetName = "update.json";
        public const long MaxAssetBytes = 100L * 1024 * 1024;
        internal const int MaxManifestBytes = 1024 * 1024;

        private readonly string _owner;
        private readonly string _repo;
        private readonly Func<string, CancellationToken, Task<Stream>> _openUrl;

        public UpdateChecker(string owner, string repo, string currentVersion,
                             Func<string, CancellationToken, Task<Stream>> openUrl)
        {
            if (!IsSafeName(owner)) throw new ArgumentException("Invalid GitHub owner: " + owner, nameof(owner));
            if (!IsSafeName(repo)) throw new ArgumentException("Invalid GitHub repository: " + repo, nameof(repo));
            _owner = owner;
            _repo = repo;
            CurrentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
            _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
            ManifestUrl = "https://github.com/" + owner + "/" + repo + "/releases/latest/download/" + ManifestAssetName;
        }

        public string ManifestUrl { get; }
        public string CurrentVersion { get; }

        /// <summary>Downloads and parses update.json. Throws on network errors, oversize or invalid manifests.</summary>
        public async Task<UpdateManifest> FetchManifestAsync(CancellationToken ct)
        {
            byte[] bytes;
            using (var s = await _openUrl(ManifestUrl, ct).ConfigureAwait(false))
            {
                if (s == null) throw new IOException("No response from " + ManifestUrl);
                bytes = await ReadCappedAsync(s, MaxManifestBytes, ct).ConfigureAwait(false);
            }
            using (var r = new StreamReader(new MemoryStream(bytes), FileOps.Utf8NoBom, true))
                return UpdateManifest.Parse(r.ReadToEnd());
        }

        /// <summary>
        /// True only for a release version strictly greater than ours. Pre-releases (a "-suffix")
        /// and unparseable versions are never offered. A pre-release build of ours is older than
        /// the release of the same number.
        /// </summary>
        public bool IsNewer(UpdateManifest m)
        {
            if (m == null || !TryParseVersion(m.Version, out var remote)) return false;
            if (TryParseVersion(CurrentVersion, out var current)) return remote > current;

            int cut = CurrentVersion.IndexOfAny(new[] { '-', '+' });
            if (cut <= 0 || cut == CurrentVersion.Length - 1 ||
                !TryParseVersion(CurrentVersion.Substring(0, cut), out var core)) return false;
            return CurrentVersion[cut] == '-' ? remote >= core : remote > core;   // "+build" does not change precedence
        }

        /// <summary>
        /// https://github.com/{owner}/{repo}/releases/download/{tag}/{file} of this repository
        /// (owner and repo compared case-insensitively) and nothing else: no userinfo, port, query,
        /// fragment, percent-encoding, backslashes, dot segments or non-ASCII. The URL must already
        /// be in canonical form, so what is checked is exactly what gets requested.
        /// </summary>
        public bool IsTrustedAssetUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 2048) return false;
            foreach (var c in url)
                if (c <= ' ' || c >= 0x7F || c == '\\' || c == '%' || c == '@' || c == '?' || c == '#') return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || u.HostNameType != UriHostNameType.Dns) return false;
            if (!string.Equals(u.Host, "github.com", StringComparison.Ordinal)) return false;
            if (u.UserInfo.Length != 0 || !u.IsDefaultPort || u.Query.Length != 0 || u.Fragment.Length != 0) return false;
            // Anything Uri had to normalize (dot segments, case, explicit port...) is refused.
            if (!string.Equals(u.AbsoluteUri, url, StringComparison.Ordinal)) return false;

            var seg = u.AbsolutePath.Split('/');   // "", owner, repo, "releases", "download", tag, file
            return seg.Length == 7 && seg[0].Length == 0 &&
                   string.Equals(seg[1], _owner, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(seg[2], _repo, StringComparison.OrdinalIgnoreCase) &&
                   seg[3] == "releases" && seg[4] == "download" &&
                   IsSafeName(seg[5]) && IsSafeName(seg[6]);
        }

        /// <summary>
        /// Streams the asset to updatesDir\&lt;name&gt;-&lt;version&gt;&lt;ext&gt;.partial, checks size and
        /// SHA-256, then renames it (PtcglAutoBattleLog-1.2.0.exe) and returns that path. The version
        /// comes from the release tag in the URL. Throws UpdateVerificationException when anything
        /// does not match; the .partial is deleted on every failure, cancellation included.
        /// </summary>
        public async Task<string> DownloadAsync(UpdateAsset asset, string updatesDir, CancellationToken ct)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrEmpty(updatesDir)) throw new ArgumentException("Updates folder is required.", nameof(updatesDir));
            if (!IsTrustedAssetUrl(asset.Url))
                throw new UpdateVerificationException("Refusing to download an update from an untrusted URL: " + asset.Url);
            if (asset.Size <= 0 || asset.Size > MaxAssetBytes)
                throw new UpdateVerificationException("Refusing an update that declares " + asset.Size + " bytes (limit " + MaxAssetBytes + ").");
            if (!Hashing.TryParseSha256(asset.Sha256, out var expected))
                throw new UpdateVerificationException("The update manifest has no valid SHA-256 for " + asset.Name + ".");
            if (!IsSafeName(asset.Name))
                throw new UpdateVerificationException("The update manifest has an unsafe asset name: " + asset.Name);

            var tag = new Uri(asset.Url).AbsolutePath.Split('/')[5];
            var label = TryParseVersion(tag, out var v) ? v.ToString(3) : asset.Sha256.Substring(0, 12).ToLowerInvariant();
            var final = Path.Combine(updatesDir, Path.GetFileNameWithoutExtension(asset.Name) + "-" + label + Path.GetExtension(asset.Name));
            var partial = final + ".partial";

            Directory.CreateDirectory(updatesDir);
            try
            {
                using (var src = await _openUrl(asset.Url, ct).ConfigureAwait(false))
                {
                    if (src == null) throw new IOException("No response from " + asset.Url);
                    using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var sha = SHA256.Create())
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            // Never ask for more than one byte past the declared size: enough to notice a longer file.
                            int want = (int)Math.Min(buffer.Length, asset.Size - total + 1);
                            int n = await src.ReadAsync(buffer, 0, want, ct).ConfigureAwait(false);
                            if (n <= 0) break;
                            total += n;
                            if (total > asset.Size)
                                throw new UpdateVerificationException("The update download is larger than the " + asset.Size + " bytes its manifest declares.");
                            sha.TransformBlock(buffer, 0, n, null, 0);
                            await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                        }
                        ct.ThrowIfCancellationRequested();
                        if (total != asset.Size)
                            throw new UpdateVerificationException("The update download has " + total + " bytes; its manifest declares " + asset.Size + ".");
                        sha.TransformFinalBlock(buffer, 0, 0);
                        if (!Hashing.BytesEqual(sha.Hash, expected))
                            throw new UpdateVerificationException("The update download's SHA-256 (" + Hashing.ToHex(sha.Hash) +
                                                                  ") does not match its manifest (" + asset.Sha256.ToLowerInvariant() + ").");
                        dst.Flush(true);
                    }
                }
                if (File.Exists(final)) File.Delete(final);
                File.Move(partial, final);
                return final;
            }
            catch
            {
                FileOps.TryDelete(partial);
                throw;
            }
        }

        /// <summary>
        /// "1.2.3", "v1.2.3" or "1.2.3.0" → 1.2.3. Rejects pre-release/build suffixes, other
        /// lengths, a non-zero 4th part, signs, spaces, leading zeros and non-ASCII digits.
        /// </summary>
        public static bool TryParseVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == 'v' || s[0] == 'V') s = s.Substring(1);
            var parts = s.Split('.');
            if (parts.Length != 3 && parts.Length != 4) return false;
            var n = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!TryParseNumber(parts[i], out n[i])) return false;
            if (parts.Length == 4 && n[3] != 0) return false;
            v = new Version(n[0], n[1], n[2]);
            return true;
        }

        /// <summary>
        /// True when <paramref name="installed"/>, the version of an installed copy as Windows
        /// reports it (FileVersionInfo.ProductVersion: "1.2.0", "1.2.0+commit", maybe "1.3.0-rc1"),
        /// is strictly newer than <paramref name="ours"/>. Build metadata after '+' is ignored and a
        /// pre-release comes before its release. False when either does not parse: a copy whose
        /// version cannot be read is replaced, as before.
        /// </summary>
        public static bool IsInstalledNewer(string installed, string ours)
        {
            if (!TryParseRelease(installed, out var a, out var aPre) || !TryParseRelease(ours, out var b, out var bPre)) return false;
            var c = a.CompareTo(b);
            return c > 0 || (c == 0 && !aPre && bPre);
        }

        /// <summary>"1.2.3", "1.2.3+build", "1.2.3-pre" or "1.2.3-pre+build": the numbers, and whether it is a pre-release.</summary>
        private static bool TryParseRelease(string s, out Version core, out bool preRelease)
        {
            core = null;
            preRelease = false;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                if (dash == s.Length - 1) return false;
                preRelease = true;
                s = s.Substring(0, dash);
            }
            return TryParseVersion(s, out core);
        }

        private static bool TryParseNumber(string s, out int value)
        {
            value = 0;
            if (s.Length == 0 || s.Length > 9 || (s.Length > 1 && s[0] == '0')) return false;
            foreach (var c in s)
            {
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        /// <summary>A GitHub owner/repo/tag/file name: ASCII letters, digits, '.', '_', '-'; not a dot segment.</summary>
        internal static bool IsSafeName(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 100 || s[0] == '.') return false;
            foreach (var c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                    return false;
            return true;
        }

        private static async Task<byte[]> ReadCappedAsync(Stream s, int max, CancellationToken ct)
        {
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[16384];
                int n;
                while ((n = await s.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > max) throw new UpdateVerificationException("update.json is larger than " + max + " bytes.");
                    ms.Write(buffer, 0, n);
                }
                return ms.ToArray();
            }
        }
    }
}
