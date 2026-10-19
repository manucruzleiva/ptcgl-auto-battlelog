using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>
    /// How RSA keys are made and used: a seam, so the plugin can use what Unity's Mono implements
    /// while the .NET 8 tests may substitute RSA.Create().
    ///
    /// On Mono, RSACryptoServiceProvider is a managed implementation, and one created with a key
    /// size never touches a key container. Only APIs of Mono's .NET 4.x profile are used: SignHash
    /// takes the hash's OID (Mono rejects names there), no Span, no HashAlgorithmName overloads.
    /// </summary>
    public class RsaBackend
    {
        /// <summary>SHA-256's OID (2.16.840.1.101.3.4.2.1), what SignHash/VerifyHash expect on Mono.</summary>
        public const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

        public static readonly RsaBackend Default = new RsaBackend();

        /// <summary>A new key pair. Slow on Mono (seconds for 2048 bits): never call it on Unity's main thread.</summary>
        public virtual RSA Create(int bits)
        {
            var rsa = new RSACryptoServiceProvider(bits);
            NoContainer(rsa);
            return rsa;
        }

        public virtual RSA FromXml(string xml)
        {
            var rsa = new RSACryptoServiceProvider();
            NoContainer(rsa);
            rsa.FromXmlString(xml);
            return rsa;
        }

        /// <summary>RSASSA-PKCS1-v1_5 signature of a SHA-256 hash.</summary>
        public virtual byte[] SignHash(RSA key, byte[] sha256)
        {
            return ((RSACryptoServiceProvider)key).SignHash(sha256, Sha256Oid);
        }

        public virtual bool VerifyHash(RSA key, byte[] sha256, byte[] signature)
        {
            return ((RSACryptoServiceProvider)key).VerifyHash(sha256, Sha256Oid, signature);
        }

        /// <summary>
        /// The key lives in signing-key.xml only. PersistKeyInCsp is a Windows CSP notion: some
        /// runtimes throw for it (PlatformNotSupportedException is a NotSupportedException).
        /// </summary>
        private static void NoContainer(RSACryptoServiceProvider rsa)
        {
            try { rsa.PersistKeyInCsp = false; }
            catch (NotSupportedException) { }
            catch (CryptographicException) { }
        }
    }

    /// <summary>
    /// The per-user signing key of sealed logs, and the position of the latest seal in its chain.
    ///
    /// %LOCALAPPDATA%\PtcglAutoBattleLog\identity\signing-key.xml holds the RSA-2048 key pair
    /// (RSA.ToXmlString(true)); chain.json holds {"seq":N,"last":"&lt;hex&gt;","keyId":"&lt;keyId&gt;"}
    /// (seq 0 and last null before the first seal). The key is created on first use, on a
    /// background thread: Mono's managed RSA takes seconds to generate 2048 bits, which would
    /// freeze the game.
    ///
    /// A key file that cannot be used is kept aside as signing-key.xml.corrupt and replaced (the
    /// new key starts a new chain). chain.json is checked against the logs folder at every load:
    /// a lost, corrupt or other key's chain.json is rebuilt from the newest seal of this key there,
    /// and one that fell behind (a write that failed, a crash between the log and chain.json) moves
    /// up to the newest seal written since, so the next seal neither reuses a seq nor breaks a
    /// link. Only seals whose signature verifies with this key count: the keyId is public, so
    /// anyone could drop a file that claims it.
    ///
    /// Thread-safe: loading runs on its own thread; signing and committing happen on the caller's.
    /// </summary>
    public sealed class SigningIdentity
    {
        public const string FolderName = "identity";
        public const string KeyFileName = "signing-key.xml";
        public const string ChainFileName = "chain.json";
        public const string CorruptSuffix = ".corrupt";
        public const int KeyBits = 2048;
        /// <summary>What verifiers accept (docs/LOG-SEAL.md §1): a key file outside this is replaced, so no seal is wasted.</summary>
        public const int MaxKeyBits = 4096;
        public const string PublicExponent = "AQAB";   // 65537, base64url

        /// <summary>
        /// How far before chain.json's own write time a log is still looked at when the two are
        /// reconciled. Generous: file system clocks and time stamps are coarse, and only the
        /// newest logs are read.
        /// </summary>
        private static readonly TimeSpan ReconcileWindow = TimeSpan.FromMinutes(10);

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Regex ChainJson = new Regex(
            @"^\s*\{\s*""seq""\s*:\s*(\d{1,18})\s*,\s*""last""\s*:\s*(?:null|""([0-9a-f]{64})"")\s*(?:,\s*""keyId""\s*:\s*""([A-Za-z0-9_-]{1,128})""\s*)?\}\s*$");

        private readonly RsaBackend _backend;
        private readonly Action<string> _log, _warn;
        private readonly object _lock = new object();
        private readonly object _writeLock = new object();
        private readonly ManualResetEvent _done = new ManualResetEvent(false);
        private int _started;
        private RSA _key;
        private long _seq;
        private string _last;
        private volatile bool _chainFailing;

        public SigningIdentity(string folder, RsaBackend backend = null, Action<string> log = null, Action<string> warn = null)
        {
            Folder = folder;
            _backend = backend ?? RsaBackend.Default;
            _log = log ?? (_ => { });
            _warn = warn ?? _log;
        }

        /// <summary>&lt;localAppData&gt;\PtcglAutoBattleLog\identity.</summary>
        public static string FolderFor(string localAppData)
        {
            return Path.Combine(localAppData, "PtcglAutoBattleLog", FolderName);
        }

        public string Folder { get; }
        public string KeyPath { get { return Path.Combine(Folder, KeyFileName); } }
        public string ChainPath { get { return Path.Combine(Folder, ChainFileName); } }

        /// <summary>RFC 7638 JWK thumbprint of the public key (SHA-256, base64url).</summary>
        public string KeyId { get; private set; }
        /// <summary>The public key's modulus and exponent, base64url without padding, as in a JWK.</summary>
        public string Modulus { get; private set; }
        public string Exponent { get; private set; }

        /// <summary>Why the key cannot be used; null while it loads and once it works.</summary>
        public string Error { get; private set; }

        public bool Started { get { return Volatile.Read(ref _started) != 0; } }
        public bool IsReady { get { lock (_lock) return _key != null; } }

        /// <summary>
        /// Loads or creates the key on a background thread; idempotent. <paramref name="logsFolder"/>
        /// is where seals are looked for if chain.json has to be rebuilt.
        /// </summary>
        public void BeginLoad(string logsFolder)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            try
            {
                var thread = new Thread(() => LoadNow(logsFolder)) { IsBackground = true, Name = "PtcglAutoBattleLog signing key" };
                // Key generation competes with the game's own loading: let the game win.
                try { thread.Priority = ThreadPriority.BelowNormal; } catch { }
                thread.Start();
            }
            catch (Exception e)
            {
                Error = "the signing key could not be loaded (" + e.Message + ")";
                _done.Set();
            }
        }

        /// <summary>The same as <see cref="BeginLoad"/>, on the calling thread (tests, tools).</summary>
        public void Load(string logsFolder)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) { _done.WaitOne(); return; }
            LoadNow(logsFolder);
        }

        /// <summary>Waits for a load in progress; true when the key can be used.</summary>
        public bool WaitReady(TimeSpan timeout)
        {
            if (!Started) return false;
            _done.WaitOne(timeout);
            return IsReady;
        }

        private void LoadNow(string logsFolder)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                bool created = false;
                var key = LoadKey();
                if (key == null)
                {
                    key = _backend.Create(KeyBits);
                    CheckUsable(key);
                    LogStore.WriteAtomic(KeyPath, Utf8NoBom.GetBytes(key.ToXmlString(true)));
                    created = true;
                }
                var p = key.ExportParameters(false);
                Modulus = Base64Url(TrimLeadingZeros(p.Modulus));
                Exponent = Base64Url(TrimLeadingZeros(p.Exponent));
                KeyId = Thumbprint(Modulus, Exponent);
                LoadChain(created, logsFolder, key);
                lock (_lock) _key = key;
                _log("Signing key " + KeyId + (created ? " created" : " loaded") + "; last seal: seq " + _seq + ".");
            }
            catch (Exception e)
            {
                Error = "the signing key could not be created or loaded (" + e.Message + ")";
                _warn("Logs will be saved without a seal: " + Error);
            }
            finally
            {
                _done.Set();
            }
        }

        /// <summary>The saved key, or null when there is none (or it was unusable and has been set aside).</summary>
        private RSA LoadKey()
        {
            if (!File.Exists(KeyPath)) return null;
            // Not caught: a file that exists but cannot be read now (locked) is not a reason to replace it.
            var xml = File.ReadAllText(KeyPath, Encoding.UTF8);
            try
            {
                var key = _backend.FromXml(xml);
                CheckUsable(key);
                return key;
            }
            catch (Exception e)
            {
                var aside = KeyPath + CorruptSuffix;
                try { File.Delete(aside); } catch { }
                File.Move(KeyPath, aside);
                _warn(KeyFileName + " could not be used (" + e.Message + "); kept as " + Path.GetFileName(aside) + " and replaced by a new key.");
                return null;
            }
        }

        /// <summary>A private key of 2048 to 4096 bits, exponent 65537, that signs and verifies: what verifiers accept.</summary>
        private void CheckUsable(RSA key)
        {
            var p = key.ExportParameters(false);
            int bits = TrimLeadingZeros(p.Modulus).Length * 8;
            if (bits < KeyBits || bits > MaxKeyBits)
                throw new CryptographicException("the key has " + bits + " bits, " + KeyBits + " to " + MaxKeyBits + " are allowed");
            if (Base64Url(TrimLeadingZeros(p.Exponent)) != PublicExponent)
                throw new CryptographicException("the key's public exponent is not 65537");
            var hash = Sha256(Encoding.ASCII.GetBytes("ptcgl-abl:self-test"));
            var sig = _backend.SignHash(key, hash);
            if (!_backend.VerifyHash(key, hash, sig)) throw new CryptographicException("a test signature did not verify");
        }

        // ------------------------------------------------------------------ chain

        private void LoadChain(bool newKey, string logsFolder, RSA key)
        {
            if (newKey)
            {
                // A new key starts a new chain. Written now, so a missing chain.json always means "lost".
                _seq = 0;
                _last = null;
                SaveChain();
                return;
            }
            if (File.Exists(ChainPath))
            {
                var text = File.ReadAllText(ChainPath, Encoding.UTF8);
                long seq; string last, keyId;
                if (TryParseChain(text, out seq, out last, out keyId))
                {
                    // No keyId: written before chain.json named its key; it can only be this key's.
                    if (keyId != null && keyId != KeyId)
                    {
                        // A new key was written and the process died before its chain.json was.
                        Recover(logsFolder, key, ChainFileName + " belongs to another key (" + keyId + ")");
                        return;
                    }
                    _seq = seq;
                    _last = last;
                    Reconcile(logsFolder, key, File.GetLastWriteTimeUtc(ChainPath));
                    return;
                }
                var aside = ChainPath + CorruptSuffix;
                try { File.Delete(aside); } catch { }
                File.Move(ChainPath, aside);
                Recover(logsFolder, key, ChainFileName + " was corrupt (kept as " + Path.GetFileName(aside) + ")");
                return;
            }
            Recover(logsFolder, key, ChainFileName + " was missing");
        }

        /// <summary>Continues after the newest seal of this key found in the logs folder.</summary>
        private void Recover(string logsFolder, RSA key, string why)
        {
            _seq = 0;
            _last = null;
            var head = Newest(Scan(logsFolder, null), key);
            if (head != null)
            {
                _seq = head.Seal.Seq;
                _last = head.Seal.Hash;
            }
            _warn(why + "; " + (head == null
                ? "no earlier seal of this key was found, the chain starts again at seq 1."
                : "continuing after seq " + _seq + " from " + Path.GetFileName(head.Path) + "."));
            SaveChain();
        }

        /// <summary>
        /// chain.json can fall behind its logs: its write failed and the game exited before a retry,
        /// or the game died between writing a log and chain.json. A seal of this key written since
        /// (beyond chain.json's seq, or at that seq with another hash: a re-seal in place) is the
        /// real head of the chain. Only logs written around and after chain.json are read.
        /// </summary>
        private void Reconcile(string logsFolder, RSA key, DateTime chainWrittenUtc)
        {
            var recent = Scan(logsFolder, chainWrittenUtc - ReconcileWindow);
            var beyond = recent.Where(f => f.Seal.Seq > _seq || (f.Seal.Seq == _seq && f.Seal.Hash != _last)).ToList();
            if (beyond.Count == 0) return;
            var head = Newest(beyond, key);
            if (head == null) return;
            // Two seals at chain.json's own seq, one of them the one it names: a copy from elsewhere, not ours to follow.
            if (head.Seal.Seq == _seq && recent.Any(f => f.Seal.Seq == _seq && f.Seal.Hash == _last)) return;
            _warn(ChainFileName + " was behind the logs folder (seq " + _seq + "); continuing after seq " + head.Seal.Seq +
                  " from " + Path.GetFileName(head.Path) + ".");
            _seq = head.Seal.Seq;
            _last = head.Seal.Hash;
            SaveChain();
        }

        private sealed class FoundSeal
        {
            public SealReader Seal;
            public string Path;
            public DateTime WrittenUtc;
        }

        /// <summary>Seals that name this key in the logs folder (.txt footers and .seal files), written since <paramref name="sinceUtc"/> if given.</summary>
        private List<FoundSeal> Scan(string logsFolder, DateTime? sinceUtc)
        {
            var found = new List<FoundSeal>();
            if (string.IsNullOrEmpty(logsFolder) || !Directory.Exists(logsFolder)) return found;
            foreach (var file in new DirectoryInfo(logsFolder).GetFiles())
            {
                var ext = file.Extension;
                if (!ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(LogSealer.SidecarExtension, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime written;
                try { written = file.LastWriteTimeUtc; } catch (Exception) { continue; }
                if (sinceUtc.HasValue && written < sinceUtc.Value) continue;
                var seal = SealReader.TryReadFile(file.FullName);
                if (seal == null || seal.KeyId != KeyId) continue;
                found.Add(new FoundSeal { Seal = seal, Path = file.FullName, WrittenUtc = written });
            }
            return found;
        }

        /// <summary>The highest seq (then the newest file) whose seal really is this key's; null when none.</summary>
        private FoundSeal Newest(IEnumerable<FoundSeal> candidates, RSA key)
        {
            return candidates.OrderByDescending(f => f.Seal.Seq).ThenByDescending(f => f.WrittenUtc).FirstOrDefault(f => IsOwnSeal(f.Seal, key));
        }

        /// <summary>
        /// Made with this key: the seal's public key is ours and its signature, in the one canonical
        /// base64 spelling, verifies over its JSON line. A file anyone could write with our keyId
        /// and a chosen seq is not.
        /// </summary>
        private bool IsOwnSeal(SealReader seal, RSA key)
        {
            if (seal.Modulus != Modulus || seal.Exponent != Exponent || string.IsNullOrEmpty(seal.Signature)) return false;
            try
            {
                var signature = Convert.FromBase64String(seal.Signature);
                if (Convert.ToBase64String(signature) != seal.Signature) return false;
                return _backend.VerifyHash(key, Sha256(Encoding.ASCII.GetBytes(seal.Json)), signature);
            }
            catch (Exception) { return false; }
        }

        /// <summary>The latest seal: its seq (0 before the first) and chain hash (null before the first).</summary>
        public void GetChain(out long seq, out string last)
        {
            lock (_lock) { seq = _seq; last = _last; }
        }

        /// <summary>
        /// Records a seal that is now on disk. Memory first, so a chain.json that cannot be written
        /// right now never makes the next seal reuse a seq; the file catches up with the next
        /// commit or <see cref="RetryChain"/>, and the next load reconciles it with the logs if the
        /// game exits before that. Never throws.
        /// </summary>
        public void Commit(long seq, string hash)
        {
            lock (_lock)
            {
                _seq = seq;
                _last = hash;
            }
            SaveChain();
        }

        /// <summary>
        /// Writes chain.json again when its last write failed. The plugin calls this from its
        /// heartbeat and when it shuts down, so a transient failure (an antivirus lock) does not
        /// leave chain.json behind the logs. Cheap when nothing failed; never throws.
        /// </summary>
        public void RetryChain()
        {
            if (_chainFailing) SaveChain();
        }

        /// <summary>The current position to chain.json; one writer at a time, always the latest state.</summary>
        private void SaveChain()
        {
            lock (_writeLock)
            {
                string text;
                lock (_lock) text = ChainText(_seq, _last, KeyId);
                try
                {
                    LogStore.WriteAtomic(ChainPath, Utf8NoBom.GetBytes(text));
                    if (_chainFailing) _log(ChainFileName + " written again.");
                    _chainFailing = false;
                }
                catch (Exception e)
                {
                    if (!_chainFailing) _warn("Could not write " + ChainPath + " (kept in memory, retried in a few seconds and at the next seal): " + e.Message);
                    _chainFailing = true;
                }
            }
        }

        internal static string ChainText(long seq, string last, string keyId)
        {
            return "{\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"last\":" + (last == null ? "null" : "\"" + last + "\"") +
                   (keyId == null ? "" : ",\"keyId\":\"" + keyId + "\"") + "}";
        }

        internal static bool TryParseChain(string text, out long seq, out string last)
        {
            string keyId;
            return TryParseChain(text, out seq, out last, out keyId);
        }

        /// <summary>chain.json; <paramref name="keyId"/> is null in one written before it named its key.</summary>
        internal static bool TryParseChain(string text, out long seq, out string last, out string keyId)
        {
            seq = 0;
            last = null;
            keyId = null;
            var m = ChainJson.Match(text ?? "");
            if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return false;
            last = m.Groups[2].Success ? m.Groups[2].Value : null;
            keyId = m.Groups[3].Success ? m.Groups[3].Value : null;
            return (seq == 0) == (last == null);
        }

        // ------------------------------------------------------------------ signing

        /// <summary>
        /// RSASSA-PKCS1-v1_5 / SHA-256 signature of <paramref name="data"/>, checked before it is
        /// returned: a signature that does not verify is never written. Throws when the key is not
        /// ready or the runtime fails.
        /// </summary>
        public byte[] Sign(byte[] data)
        {
            lock (_lock)
            {
                if (_key == null) throw new InvalidOperationException(Error ?? "the signing key is not loaded");
                var hash = Sha256(data);
                var sig = _backend.SignHash(_key, hash);
                if (!_backend.VerifyHash(_key, hash, sig)) throw new CryptographicException("the signature did not verify");
                return sig;
            }
        }

        // ------------------------------------------------------------------ encoding helpers

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>JWK integers are big-endian with no leading zero bytes (RFC 7518 "Base64urlUInt").</summary>
        internal static byte[] TrimLeadingZeros(byte[] value)
        {
            int i = 0;
            while (i < value.Length - 1 && value[i] == 0) i++;
            if (i == 0) return value;
            var trimmed = new byte[value.Length - i];
            Array.Copy(value, i, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        /// <summary>RFC 7638: SHA-256 of {"e":…,"kty":"RSA","n":…} (members sorted, no whitespace), base64url.</summary>
        public static string Thumbprint(string n, string e)
        {
            var jwk = "{\"e\":\"" + e + "\",\"kty\":\"RSA\",\"n\":\"" + n + "\"}";
            return Base64Url(Sha256(Encoding.ASCII.GetBytes(jwk)));
        }
    }
}
