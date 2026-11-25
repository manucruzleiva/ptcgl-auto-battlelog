//! HTTPS for the updater and the tcgkb and Pokematch clients: Windows' own TLS (SChannel) and
//! certificate store, and the system proxy, like the C# tray's HttpClient. https only, redirects
//! included (the SHA-256 check would catch a swapped file anyway; this refuses to even read one).
use std::io::{self, Read};
use std::sync::LazyLock;
use std::time::Duration;
use ureq::tls::{RootCerts, TlsConfig, TlsProvider};
use ureq::Agent;

fn agent(timeout: Duration) -> Agent {
    Agent::config_builder()
        .https_only(true)
        .http_status_as_error(false)
        .user_agent(format!("{}/{}", pab_core::paths::APP_ID, crate::VERSION))
        .timeout_global(Some(timeout))
        .tls_config(TlsConfig::builder().provider(TlsProvider::NativeTls).root_certs(RootCerts::PlatformVerifier).build())
        .build()
        .into()
}

/// API calls and manifests: 30 s for the whole request. Downloads: 10 minutes.
static SHORT: LazyLock<Agent> = LazyLock::new(|| agent(Duration::from_secs(30)));
static LONG: LazyLock<Agent> = LazyLock::new(|| agent(Duration::from_secs(600)));

fn io_err(e: ureq::Error) -> io::Error {
    match e {
        ureq::Error::Io(e) => e,
        e => io::Error::other(e.to_string()),
    }
}

/// GET for the updater (pab_core::update::OpenUrl): the body as a stream, or an error for a non-2xx.
pub fn open_url(url: &str, download: bool) -> io::Result<Box<dyn Read>> {
    let agent = if download { &*LONG } else { &*SHORT };
    let response = agent.get(url).call().map_err(io_err)?;
    let status = response.status();
    if !status.is_success() {
        return Err(io::Error::other(format!("{url} answered HTTP {}", status.as_u16())));
    }
    Ok(Box::new(response.into_body().into_reader()))
}

/// One API request for tcgkb or Pokematch: status and body text (at most 1 MB). Err only when no
/// answer came back. Redirects are not followed: the bearer token goes to its own host and nowhere else.
pub fn send(method: &str, url: &str, headers: &[(&str, &str)], body: Option<&str>, timeout: Duration) -> Result<(u16, String), String> {
    let mut b = ureq::http::Request::builder().method(method).uri(url);
    for (k, v) in headers {
        b = b.header(*k, *v);
    }
    let request = b.body(body.unwrap_or("").to_string()).map_err(|e| e.to_string())?;
    let request = SHORT.configure_request(request).timeout_global(Some(timeout)).max_redirects(0).build();
    let mut response = SHORT.run(request).map_err(|e| match e {
        ureq::Error::Timeout(_) => format!("no answer within {} seconds", timeout.as_secs()),
        e => e.to_string(),
    })?;
    let status = response.status().as_u16();
    let text = response.body_mut().with_config().limit(1024 * 1024).read_to_string().map_err(|e| e.to_string())?;
    Ok((status, text))
}
