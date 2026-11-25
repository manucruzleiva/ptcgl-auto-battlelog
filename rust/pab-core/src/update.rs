//! OTA updates from GitHub Releases (UpdateChecker.cs, UpdateManifest.cs). The manifest says what
//! to download; this module decides whether to believe it. A file is only handed back when it came
//! from this repository's release downloads on github.com and matches the manifest's byte count
//! and SHA-256. HTTP is injected (`open_url`), so tests serve bytes from memory.

use std::cmp::Ordering;
use std::fmt;
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::hashing;

pub const EXE_ASSET_NAME: &str = "PtcglAutoBattleLog.exe";
pub const MANIFEST_ASSET_NAME: &str = "update.json";
pub const MAX_ASSET_BYTES: i64 = 100 * 1024 * 1024;
pub const MAX_MANIFEST_BYTES: usize = 1024 * 1024;
pub const MAX_RELEASE_LIST_BYTES: usize = 4 * 1024 * 1024;

#[derive(Debug, thiserror::Error)]
pub enum UpdateError {
    /// The text is not a manifest or release list (C# FormatException).
    #[error("{0}")]
    Format(String),
    /// Something does not match what the manifest promises (C# UpdateVerificationException).
    #[error("{0}")]
    Verification(String),
    #[error(transparent)]
    Io(#[from] io::Error),
    #[error("cancelled")]
    Cancelled,
}

/// update.json of a release.
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
pub struct UpdateManifest {
    pub version: String,
    #[serde(default)]
    pub tag: Option<String>,
    #[serde(default, rename = "releasedUtc")]
    pub released_utc: Option<String>,
    #[serde(default, rename = "notesUrl")]
    pub notes_url: Option<String>,
    #[serde(default, deserialize_with = "assets_without_nulls")]
    pub assets: Vec<UpdateAsset>,
}

/// One file of a release. Size and sha256 are what the download must match.
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
pub struct UpdateAsset {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub size: i64,
    #[serde(default)]
    pub sha256: Option<String>,
}

fn assets_without_nulls<'de, D: serde::Deserializer<'de>>(d: D) -> Result<Vec<UpdateAsset>, D::Error> {
    let list: Option<Vec<Option<UpdateAsset>>> = Option::deserialize(d)?;
    Ok(list.unwrap_or_default().into_iter().flatten().collect())
}

impl UpdateManifest {
    pub fn parse(json: &str) -> Result<UpdateManifest, UpdateError> {
        let m: UpdateManifest =
            serde_json::from_str(json).map_err(|e| UpdateError::Format(format!("update.json is not valid JSON: {e}")))?;
        if m.version.trim().is_empty() {
            return Err(UpdateError::Format("update.json has no version.".into()));
        }
        Ok(m)
    }

    /// The asset with exactly this name.
    pub fn find(&self, name: &str) -> Option<&UpdateAsset> {
        self.assets.iter().find(|a| a.name.as_deref() == Some(name))
    }
}

pub type OpenUrl<'a> = dyn FnMut(&str) -> io::Result<Box<dyn Read>> + 'a;

pub struct UpdateChecker {
    owner: String,
    repo: String,
    pub current_version: String,
    /// update.json of the release GitHub marks latest: the stable channel.
    pub manifest_url: String,
    /// The newest releases, pre-releases included: the beta channel picks from these.
    pub releases_api_url: String,
}

impl UpdateChecker {
    pub fn new(owner: &str, repo: &str, current_version: &str) -> Result<UpdateChecker, String> {
        if !is_safe_name(owner) {
            return Err(format!("Invalid GitHub owner: {owner}"));
        }
        if !is_safe_name(repo) {
            return Err(format!("Invalid GitHub repository: {repo}"));
        }
        Ok(UpdateChecker {
            owner: owner.into(),
            repo: repo.into(),
            current_version: current_version.into(),
            manifest_url: format!("https://github.com/{owner}/{repo}/releases/latest/download/{MANIFEST_ASSET_NAME}"),
            releases_api_url: format!("https://api.github.com/repos/{owner}/{repo}/releases?per_page=30"),
        })
    }

    /// update.json of release `tag` of this repository.
    pub fn manifest_url_for(&self, tag: &str) -> Result<String, String> {
        if !is_safe_name(tag) {
            return Err(format!("Invalid release tag: {tag}"));
        }
        Ok(format!("https://github.com/{}/{}/releases/download/{tag}/{MANIFEST_ASSET_NAME}", self.owner, self.repo))
    }

    /// Stable channel: update.json of the release GitHub marks latest. Beta channel: the highest
    /// version among the newest non-draft releases, whose update.json must describe that version.
    pub fn fetch_manifest(&self, include_pre_releases: bool, open_url: &mut OpenUrl) -> Result<UpdateManifest, UpdateError> {
        if !include_pre_releases {
            return UpdateManifest::parse(&fetch_text(open_url, &self.manifest_url, MAX_MANIFEST_BYTES)?);
        }
        let tags = parse_release_list(&fetch_text(open_url, &self.releases_api_url, MAX_RELEASE_LIST_BYTES)?)?;
        let tag = newest_tag(tags.iter().map(String::as_str))
            .ok_or_else(|| UpdateError::Format("The repository lists no release with a version tag.".into()))?;
        let url = self.manifest_url_for(tag).map_err(UpdateError::Format)?;
        let m = UpdateManifest::parse(&fetch_text(open_url, &url, MAX_MANIFEST_BYTES)?)?;
        match (SemVer::parse(&m.version), SemVer::parse(tag)) {
            (Some(mv), Some(tv)) if mv == tv => Ok(m),
            _ => Err(UpdateError::Verification(format!("Release {tag} has an update.json for version {}.", m.version))),
        }
    }

    /// True only for a version strictly greater than ours. Pre-releases only with
    /// include_pre_releases; "+build" and unparseable versions never.
    pub fn is_newer(&self, version: &str, include_pre_releases: bool) -> bool {
        if version.contains('+') {
            return false;
        }
        let (Some(remote), Some(current)) = (SemVer::parse(version), SemVer::parse(&self.current_version)) else { return false };
        (!remote.is_pre_release() || include_pre_releases) && remote > current
    }

    /// https://github.com/{owner}/{repo}/releases/download/{tag}/{file} of this repository (owner
    /// and repo case-insensitive) and nothing else: no userinfo, port, query, fragment,
    /// percent-encoding, backslashes, dot segments or non-ASCII.
    pub fn is_trusted_asset_url(&self, url: &str) -> bool {
        if url.is_empty() || url.len() > 2048 {
            return false;
        }
        if url.bytes().any(|c| c <= b' ' || c >= 0x7F || matches!(c, b'\\' | b'%' | b'@' | b'?' | b'#')) {
            return false;
        }
        let Some(path) = url.strip_prefix("https://github.com/") else { return false };
        let seg: Vec<&str> = path.split('/').collect();
        seg.len() == 6
            && seg[0].eq_ignore_ascii_case(&self.owner)
            && seg[1].eq_ignore_ascii_case(&self.repo)
            && seg[2] == "releases"
            && seg[3] == "download"
            && is_safe_name(seg[4])
            && is_safe_name(seg[5])
    }

    /// Streams the asset to updates_dir/<name>-<version><ext>.partial, checks size and SHA-256,
    /// then renames it (PtcglAutoBattleLog-1.2.0.exe) and returns that path. The .partial is
    /// deleted on every failure. `cancelled` is polled between chunks.
    pub fn download(
        &self,
        asset: &UpdateAsset,
        updates_dir: &Path,
        open_url: &mut OpenUrl,
        cancelled: &dyn Fn() -> bool,
    ) -> Result<PathBuf, UpdateError> {
        let url = asset.url.as_deref().unwrap_or("");
        if !self.is_trusted_asset_url(url) {
            return Err(UpdateError::Verification(format!("Refusing to download an update from an untrusted URL: {url}")));
        }
        if asset.size <= 0 || asset.size > MAX_ASSET_BYTES {
            return Err(UpdateError::Verification(format!(
                "Refusing an update that declares {} bytes (limit {MAX_ASSET_BYTES}).",
                asset.size
            )));
        }
        let sha = asset.sha256.as_deref().unwrap_or("");
        let expected = hashing::parse_sha256(sha).ok_or_else(|| {
            UpdateError::Verification(format!("The update manifest has no valid SHA-256 for {}.", asset.name.as_deref().unwrap_or("")))
        })?;
        let name = asset.name.as_deref().unwrap_or("");
        if !is_safe_name(name) {
            return Err(UpdateError::Verification(format!("The update manifest has an unsafe asset name: {name}")));
        }

        let tag = url.split('/').nth(7).unwrap_or("");
        let label = match (parse_version(tag), SemVer::parse(tag)) {
            (Some((a, b, c)), _) => format!("{a}.{b}.{c}"),
            (None, Some(v)) if !tag.contains('+') => v.to_string(),
            _ => sha[..12].to_ascii_lowercase(),
        };
        let (stem, ext) = match name.rfind('.') {
            Some(i) if i > 0 => (&name[..i], &name[i..]),
            _ => (name, ""),
        };
        let final_path = updates_dir.join(format!("{stem}-{label}{ext}"));
        let partial = updates_dir.join(format!("{stem}-{label}{ext}.partial"));

        fs::create_dir_all(updates_dir)?;
        let result = (|| -> Result<(), UpdateError> {
            if cancelled() {
                return Err(UpdateError::Cancelled);
            }
            let mut src = open_url(url)?;
            let mut dst = fs::File::create(&partial)?;
            let mut hasher = Sha256::new();
            let mut buffer = vec![0u8; 81920];
            let mut total: i64 = 0;
            loop {
                if cancelled() {
                    return Err(UpdateError::Cancelled);
                }
                // Never ask for more than one byte past the declared size: enough to notice a longer file.
                let want = (buffer.len() as i64).min(asset.size - total + 1) as usize;
                let n = match src.read(&mut buffer[..want]) {
                    Ok(0) => break,
                    Ok(n) => n,
                    Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                    Err(e) => return Err(e.into()),
                };
                total += n as i64;
                if total > asset.size {
                    return Err(UpdateError::Verification(format!(
                        "The update download is larger than the {} bytes its manifest declares.",
                        asset.size
                    )));
                }
                hasher.update(&buffer[..n]);
                dst.write_all(&buffer[..n])?;
            }
            if cancelled() {
                return Err(UpdateError::Cancelled);
            }
            if total != asset.size {
                return Err(UpdateError::Verification(format!(
                    "The update download has {total} bytes; its manifest declares {}.",
                    asset.size
                )));
            }
            let actual: [u8; 32] = hasher.finalize().into();
            if actual != expected {
                return Err(UpdateError::Verification(format!(
                    "The update download's SHA-256 ({}) does not match its manifest ({}).",
                    hashing::to_hex(&actual),
                    sha.to_ascii_lowercase()
                )));
            }
            dst.sync_all()?;
            drop(dst);
            if final_path.exists() {
                fs::remove_file(&final_path)?;
            }
            fs::rename(&partial, &final_path)?;
            Ok(())
        })();
        match result {
            Ok(()) => Ok(final_path),
            Err(e) => {
                let _ = fs::remove_file(&partial);
                Err(e)
            }
        }
    }
}

fn fetch_text(open_url: &mut OpenUrl, url: &str, max: usize) -> Result<String, UpdateError> {
    let mut bytes = Vec::new();
    open_url(url)?.take(max as u64 + 1).read_to_end(&mut bytes)?;
    if bytes.len() > max {
        return Err(UpdateError::Verification(format!("The update information is larger than {max} bytes.")));
    }
    let bytes = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes);
    Ok(String::from_utf8_lossy(bytes).into_owned())
}

/// The release tags of GitHub's "list releases" answer that are not drafts.
pub fn parse_release_list(json: &str) -> Result<Vec<String>, UpdateError> {
    #[derive(Deserialize)]
    struct Release {
        #[serde(default)]
        tag_name: Option<String>,
        #[serde(default)]
        draft: bool,
    }
    if !json.trim_start().starts_with('[') {
        return Err(UpdateError::Format("The release list is not a JSON array.".into()));
    }
    let list: Vec<Option<Release>> =
        serde_json::from_str(json).map_err(|e| UpdateError::Format(format!("The release list is not valid JSON: {e}")))?;
    Ok(list.into_iter().flatten().filter(|r| !r.draft).filter_map(|r| r.tag_name).filter(|t| !t.is_empty()).collect())
}

/// The highest tag that is a safe name and a version without "+build".
pub fn newest_tag<'a>(tags: impl IntoIterator<Item = &'a str>) -> Option<&'a str> {
    let mut best: Option<(&str, SemVer)> = None;
    for t in tags {
        if !is_safe_name(t) || t.contains('+') {
            continue;
        }
        let Some(v) = SemVer::parse(t) else { continue };
        if best.as_ref().is_none_or(|(_, b)| v > *b) {
            best = Some((t, v));
        }
    }
    best.map(|(t, _)| t)
}

/// "1.2.3", "v1.2.3" or "1.2.3.0" → (1, 2, 3). Rejects pre-release/build suffixes, other lengths,
/// a non-zero 4th part, signs, spaces, leading zeros and non-ASCII digits.
pub fn parse_version(s: &str) -> Option<(u32, u32, u32)> {
    let s = s.strip_prefix(['v', 'V']).unwrap_or(s);
    let parts: Vec<&str> = s.split('.').collect();
    if parts.len() != 3 && parts.len() != 4 {
        return None;
    }
    let n: Vec<u32> = parts.iter().map(|p| parse_number(p)).collect::<Option<_>>()?;
    if parts.len() == 4 && n[3] != 0 {
        return None;
    }
    Some((n[0], n[1], n[2]))
}

/// True when `installed` (a ProductVersion like "1.2.0", "1.2.0+commit" or "1.3.0-rc1") is
/// strictly newer than `ours`. False when either does not parse.
pub fn is_installed_newer(installed: &str, ours: &str) -> bool {
    fn release(s: &str) -> Option<((u32, u32, u32), bool)> {
        let s = s.trim();
        let s = s.split('+').next().unwrap_or(s);
        match s.find('-') {
            Some(d) if d == s.len() - 1 => None,
            Some(d) => parse_version(&s[..d]).map(|v| (v, true)),
            None => parse_version(s).map(|v| (v, false)),
        }
    }
    let (Some((a, a_pre)), Some((b, b_pre))) = (release(installed), release(ours)) else { return false };
    a > b || (a == b && !a_pre && b_pre)
}

/// A GitHub owner/repo/tag/file name: ASCII letters, digits, '.', '_', '-'; not a dot segment.
pub fn is_safe_name(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 100
        && !s.starts_with('.')
        && s.bytes().all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'_' | b'-'))
}

fn parse_number(s: &str) -> Option<u32> {
    if s.is_empty() || s.len() > 9 || (s.len() > 1 && s.starts_with('0')) || !s.bytes().all(|c| c.is_ascii_digit()) {
        return None;
    }
    s.parse().ok()
}

/// SemVer 2.0 with an optional 'v': "1.2.3", "v1.3.2-beta.1", "1.2.3-rc1+commit", and "1.2.3.0".
/// Build metadata is ignored for precedence.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SemVer {
    pub core: (u32, u32, u32),
    pub pre_release: Vec<String>,
}

impl SemVer {
    pub fn parse(s: &str) -> Option<SemVer> {
        let mut s = s;
        if let Some(plus) = s.find('+') {
            if !is_identifier_list(&s[plus + 1..], false) {
                return None;
            }
            s = &s[..plus];
        }
        let mut pre = Vec::new();
        if let Some(dash) = s.find('-') {
            let p = &s[dash + 1..];
            if !is_identifier_list(p, true) {
                return None;
            }
            pre = p.split('.').map(String::from).collect();
            s = &s[..dash];
        }
        Some(SemVer { core: parse_version(s)?, pre_release: pre })
    }

    pub fn is_pre_release(&self) -> bool {
        !self.pre_release.is_empty()
    }
}

fn is_identifier_list(s: &str, no_leading_zeros: bool) -> bool {
    if s.is_empty() || s.len() > 64 {
        return false;
    }
    s.split('.').all(|id| {
        if id.is_empty() || !id.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-') {
            return false;
        }
        let numeric = id.bytes().all(|c| c.is_ascii_digit());
        !(no_leading_zeros && numeric && (id.len() > 9 || (id.len() > 1 && id.starts_with('0'))))
    })
}

impl Ord for SemVer {
    /// SemVer 2.0 §11: a release beats its pre-releases; identifiers compare numerically, else
    /// ordinally; more of them wins a tie.
    fn cmp(&self, other: &Self) -> Ordering {
        self.core.cmp(&other.core).then_with(|| match (self.is_pre_release(), other.is_pre_release()) {
            (false, false) => Ordering::Equal,
            (false, true) => Ordering::Greater,
            (true, false) => Ordering::Less,
            (true, true) => {
                for (a, b) in self.pre_release.iter().zip(&other.pre_release) {
                    let c = match (a.parse::<u32>(), b.parse::<u32>()) {
                        (Ok(x), Ok(y)) => x.cmp(&y),
                        (Ok(_), Err(_)) => Ordering::Less,
                        (Err(_), Ok(_)) => Ordering::Greater,
                        _ => a.as_str().cmp(b.as_str()),
                    };
                    if c != Ordering::Equal {
                        return c;
                    }
                }
                self.pre_release.len().cmp(&other.pre_release.len())
            }
        })
    }
}

impl PartialOrd for SemVer {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl fmt::Display for SemVer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let (a, b, c) = self.core;
        write!(f, "{a}.{b}.{c}")?;
        if self.is_pre_release() {
            write!(f, "-{}", self.pre_release.join("."))?;
        }
        Ok(())
    }
}
