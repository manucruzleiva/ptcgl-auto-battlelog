using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
        internal const int MaxReleaseListBytes = 4 * 1024 * 1024;

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
            ReleasesApiUrl = "https://api.github.com/repos/" + owner + "/" + repo + "/releases?per_page=30";
        }

        /// <summary>update.json of the release GitHub marks latest: the stable channel.</summary>
        public string ManifestUrl { get; }
        /// <summary>The newest releases, pre-releases included: the beta channel picks from these.</summary>
        public string ReleasesApiUrl { get; }
        public string CurrentVersion { get; }

        /// <summary>update.json of release {tag} of this repository.</summary>
        public string ManifestUrlFor(string tag)
        {
            if (!IsSafeName(tag)) throw new ArgumentException("Invalid release tag: " + tag, nameof(tag));
            return "https://github.com/" + _owner + "/" + _repo + "/releases/download/" + tag + "/" + ManifestAssetName;
        }

        /// <summary>Downloads and parses update.json of the latest release. Throws on network errors, oversize or invalid manifests.</summary>
        public Task<UpdateManifest> FetchManifestAsync(CancellationToken ct) => FetchManifestAsync(false, ct);

        /// <summary>
        /// Stable channel (includePreReleases false): update.json of the release GitHub marks latest.
        /// Beta channel: lists the newest releases, takes the highest version among those that are
        /// not drafts (a pre-release or a release, whichever is higher) and reads that release's
        /// update.json, which must describe the same version as its tag.
        /// </summary>
        public async Task<UpdateManifest> FetchManifestAsync(bool includePreReleases, CancellationToken ct)
        {
            if (!includePreReleases) return UpdateManifest.Parse(await FetchTextAsync(ManifestUrl, MaxManifestBytes, ct).ConfigureAwait(false));

            var tag = NewestTag(ParseReleaseList(await FetchTextAsync(ReleasesApiUrl, MaxReleaseListBytes, ct).ConfigureAwait(false)));
            if (tag == null) throw new FormatException("The repository lists no release with a version tag.");
            var m = UpdateManifest.Parse(await FetchTextAsync(ManifestUrlFor(tag), MaxManifestBytes, ct).ConfigureAwait(false));
            if (!TryParseSemVer(m.Version, out var mv) || !TryParseSemVer(tag, out var tv) || mv.CompareTo(tv) != 0)
                throw new UpdateVerificationException("Release " + tag + " has an update.json for version " + m.Version + ".");
            return m;
        }

        /// <summary>
        /// True only for a version strictly greater than ours. Pre-releases (a "-suffix") are offered
        /// only with includePreReleases; versions with "+build" metadata and unparseable versions
        /// never are. Versions compare as SemVer 2.0: 1.3.2-beta.1 &lt; 1.3.2-beta.2 &lt; 1.3.2, so a
        /// pre-release build of ours is older than the release of the same number.
        /// </summary>
        public bool IsNewer(UpdateManifest m, bool includePreReleases = false)
        {
            if (m == null || m.Version == null || m.Version.IndexOf('+') >= 0 || !TryParseSemVer(m.Version, out var remote)) return false;
            if (remote.IsPreRelease && !includePreReleases) return false;
            if (!TryParseSemVer(CurrentVersion, out var current)) return false;
            return remote.CompareTo(current) > 0;
        }

        /// <summary>The release tags of GitHub's "list releases" answer that are not drafts.</summary>
        internal static List<string> ParseReleaseList(string json)
        {
            var trimmed = (json ?? "").TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '[') throw new FormatException("The release list is not a JSON array.");
            List<GitHubRelease> list;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(List<GitHubRelease>));
                using (var ms = new MemoryStream(FileOps.Utf8NoBom.GetBytes(json)))
                    list = (List<GitHubRelease>)ser.ReadObject(ms);
            }
            catch (Exception e) { throw new FormatException("The release list is not valid JSON: " + e.Message, e); }
            var tags = new List<string>();
            if (list != null)
                foreach (var r in list)
                    if (r != null && !r.Draft && !string.IsNullOrEmpty(r.TagName)) tags.Add(r.TagName);
            return tags;
        }

        /// <summary>The highest tag that is a safe name and a version without "+build"; null if none.</summary>
        internal static string NewestTag(IEnumerable<string> tags)
        {
            string best = null;
            SemVer bestV = null;
            foreach (var t in tags)
            {
                if (!IsSafeName(t) || t.IndexOf('+') >= 0 || !TryParseSemVer(t, out var v)) continue;
                if (bestV == null || v.CompareTo(bestV) > 0) { best = t; bestV = v; }
            }
            return best;
        }

        private async Task<string> FetchTextAsync(string url, int max, CancellationToken ct)
        {
            byte[] bytes;
            using (var s = await _openUrl(url, ct).ConfigureAwait(false))
            {
                if (s == null) throw new IOException("No response from " + url);
                bytes = await ReadCappedAsync(s, max, ct).ConfigureAwait(false);
            }
            using (var r = new StreamReader(new MemoryStream(bytes), FileOps.Utf8NoBom, true))
                return r.ReadToEnd();
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
            var label = TryParseVersion(tag, out var v) ? v.ToString(3)
                      : TryParseSemVer(tag, out var sv) && tag.IndexOf('+') < 0 ? sv.ToString()
                      : asset.Sha256.Substring(0, 12).ToLowerInvariant();
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

        /// <summary>
        /// SemVer 2.0 with an optional 'v': "1.2.3", "v1.3.2-beta.1", "1.2.3-rc1+commit". Also
        /// "1.2.3.0" as TryParseVersion takes it. Pre-release identifiers are ASCII letters, digits
        /// and '-', none empty, numeric ones without leading zeros; build metadata is ignored.
        /// </summary>
        internal static bool TryParseSemVer(string s, out SemVer v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            int plus = s.IndexOf('+');
            if (plus >= 0)
            {
                if (!IsIdentifierList(s.Substring(plus + 1), false)) return false;
                s = s.Substring(0, plus);
            }
            string[] pre = new string[0];
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                var p = s.Substring(dash + 1);
                if (!IsIdentifierList(p, true)) return false;
                pre = p.Split('.');
                s = s.Substring(0, dash);
            }
            if (!TryParseVersion(s, out var core)) return false;
            v = new SemVer(core, pre);
            return true;
        }

        private static bool IsIdentifierList(string s, bool noLeadingZeros)
        {
            if (s.Length == 0 || s.Length > 64) return false;
            foreach (var id in s.Split('.'))
            {
                if (id.Length == 0) return false;
                bool numeric = true;
                foreach (var c in id)
                {
                    if (c >= '0' && c <= '9') continue;
                    numeric = false;
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-')) return false;
                }
                if (noLeadingZeros && numeric && (id.Length > 9 || (id.Length > 1 && id[0] == '0'))) return false;
            }
            return true;
        }

        /// <summary>A parsed SemVer: the numbers and the pre-release identifiers (empty for a release).</summary>
        internal sealed class SemVer : IComparable<SemVer>
        {
            public SemVer(Version core, string[] preRelease) { Core = core; PreRelease = preRelease; }
            public Version Core { get; }
            public string[] PreRelease { get; }
            public bool IsPreRelease => PreRelease.Length > 0;

            /// <summary>SemVer 2.0 §11: a release beats its pre-releases; identifiers compare numerically, else ordinally; more of them wins a tie.</summary>
            public int CompareTo(SemVer other)
            {
                int c = Core.CompareTo(other.Core);
                if (c != 0) return c;
                if (!IsPreRelease) return other.IsPreRelease ? 1 : 0;
                if (!other.IsPreRelease) return -1;
                for (int i = 0; i < Math.Min(PreRelease.Length, other.PreRelease.Length); i++)
                {
                    string a = PreRelease[i], b = other.PreRelease[i];
                    bool an = int.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ai);
                    bool bn = int.TryParse(b, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bi);
                    if (an && bn) c = ai.CompareTo(bi);
                    else if (an) c = -1;
                    else if (bn) c = 1;
                    else c = string.CompareOrdinal(a, b);
                    if (c != 0) return Math.Sign(c);
                }
                return PreRelease.Length.CompareTo(other.PreRelease.Length);
            }

            public override string ToString() => Core.ToString(3) + (IsPreRelease ? "-" + string.Join(".", PreRelease) : "");
        }

        /// <summary>The fields of a GitHub release object the beta channel needs; the rest is ignored.</summary>
        [DataContract]
        internal sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")] public string TagName;
            [DataMember(Name = "draft")] public bool Draft;
            [DataMember(Name = "prerelease")] public bool PreRelease;
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
                    if (ms.Length + n > max) throw new UpdateVerificationException("The update information is larger than " + max + " bytes.");
                    ms.Write(buffer, 0, n);
                }
                return ms.ToArray();
            }
        }
    }
}
