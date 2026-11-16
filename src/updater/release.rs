use std::{str::FromStr, time::Duration};

use serde::Deserialize;

const RELEASE_API_URL: &str = "https://api.github.com/repos/Illson/RustText/releases/latest";
const RELEASE_PAGE_PREFIX: &str = "https://github.com/Illson/RustText/releases/tag/";
const ASSET_PREFIX: &str = "https://github.com/Illson/RustText/releases/download/";
const MAX_RELEASE_JSON_BYTES: u64 = 256 * 1024;
const MAX_PACKAGE_BYTES: u64 = 128 * 1024 * 1024;
const MAX_SHA256_BYTES: u64 = 512;

#[derive(Clone, Debug)]
pub struct Release {
    pub version: semver::Version,
    pub tag_name: String,
    pub html_url: String,
    pub zip_url: String,
    pub sha256_url: String,
    pub archive_sha256: Option<String>,
    pub archive_size: u64,
}

#[derive(Deserialize)]
struct LatestRelease {
    tag_name: String,
    html_url: String,
    draft: bool,
    prerelease: bool,
    assets: Vec<ReleaseAsset>,
}

#[derive(Deserialize)]
struct ReleaseAsset {
    name: String,
    browser_download_url: String,
    size: u64,
    digest: Option<String>,
}

pub fn check_update(current: &semver::Version) -> Result<Option<Release>, String> {
    let agent = new_agent(Duration::from_secs(20));
    let body = with_response(
        &agent,
        RELEASE_API_URL,
        MAX_RELEASE_JSON_BYTES,
        |response| {
            response
                .into_body()
                .with_config()
                .limit(MAX_RELEASE_JSON_BYTES)
                .read_to_string()
                .map_err(|error| error.to_string())
        },
    )?;
    parse_latest_release(&body, current)
}

fn parse_latest_release(body: &str, current: &semver::Version) -> Result<Option<Release>, String> {
    let latest: LatestRelease = serde_json::from_str(body)
        .map_err(|error| format!("GitHub вернул некорректные данные о выпуске. {error}"))?;
    if latest.draft || latest.prerelease {
        return Ok(None);
    }

    let version_text = latest
        .tag_name
        .strip_prefix('v')
        .ok_or_else(|| "У выпуска RustText недопустимый номер версии.".to_owned())?;
    let version = semver::Version::parse(version_text)
        .map_err(|_| "У выпуска RustText недопустимый номер версии.".to_owned())?;
    if !version.pre.is_empty()
        || !version.build.is_empty()
        || latest.tag_name != format!("v{version}")
    {
        return Err("У выпуска RustText недопустимый стабильный номер версии.".to_owned());
    }
    if latest.html_url != format!("{RELEASE_PAGE_PREFIX}{}", latest.tag_name) {
        return Err("Ссылка на выпуск ведёт за пределы репозитория RustText.".to_owned());
    }
    if version <= *current {
        return Ok(None);
    }

    let expected_zip = format!("{ASSET_PREFIX}{}/RustText.zip", latest.tag_name);
    let expected_sha = format!("{expected_zip}.sha256");
    let zip_asset = latest
        .assets
        .iter()
        .find(|asset| asset.name == "RustText.zip")
        .ok_or_else(|| "В выпуске RustText не найден архив RustText.zip.".to_owned())?;
    let sha_asset = latest
        .assets
        .iter()
        .find(|asset| asset.name == "RustText.zip.sha256")
        .ok_or_else(|| "В выпуске RustText не найден файл RustText.zip.sha256.".to_owned())?;
    if zip_asset.browser_download_url != expected_zip
        || zip_asset.size == 0
        || zip_asset.size > MAX_PACKAGE_BYTES
    {
        return Err("Архив RustText имеет недопустимую ссылку или размер.".to_owned());
    }
    if sha_asset.browser_download_url != expected_sha
        || sha_asset.size == 0
        || sha_asset.size > MAX_SHA256_BYTES
    {
        return Err(
            "Файл контрольной суммы RustText имеет недопустимую ссылку или размер.".to_owned(),
        );
    }

    let archive_sha256 = zip_asset
        .digest
        .as_deref()
        .map(parse_api_digest)
        .transpose()?;

    Ok(Some(Release {
        version,
        tag_name: latest.tag_name,
        html_url: latest.html_url,
        zip_url: zip_asset.browser_download_url.clone(),
        sha256_url: sha_asset.browser_download_url.clone(),
        archive_sha256,
        archive_size: zip_asset.size,
    }))
}

pub(super) fn new_agent(timeout: Duration) -> ureq::Agent {
    let tls = ureq::tls::TlsConfig::builder()
        .provider(ureq::tls::TlsProvider::NativeTls)
        .build();
    ureq::Agent::config_builder()
        .tls_config(tls)
        .https_only(true)
        .http_status_as_error(false)
        .max_redirects(0)
        .max_redirects_will_error(false)
        .timeout_global(Some(timeout))
        .build()
        .new_agent()
}

pub(super) fn with_response<T>(
    agent: &ureq::Agent,
    initial_url: &str,
    body_limit: u64,
    read_body: impl FnOnce(ureq::http::Response<ureq::Body>) -> Result<T, String>,
) -> Result<T, String> {
    if !is_allowed_initial_url(initial_url) {
        return Err("Адрес обновления RustText не принадлежит GitHub.".to_owned());
    }
    let mut current_url = initial_url.to_owned();
    for _ in 0..=5 {
        let response = agent
            .get(&current_url)
            .header("Accept", "application/vnd.github+json")
            .header("X-GitHub-Api-Version", "2022-11-28")
            .header("User-Agent", "RustText updater")
            .call()
            .map_err(|error| format!("Не удалось загрузить обновление RustText. {error}"))?;
        if response.status().is_redirection() {
            let target = response
                .headers()
                .get(ureq::http::header::LOCATION)
                .and_then(|value| value.to_str().ok())
                .ok_or_else(|| "GitHub прислал перенаправление без адреса.".to_owned())?;
            let redirect = resolve_redirect(&current_url, target)?;
            if !is_allowed_redirect(&redirect) {
                return Err("GitHub перенаправил загрузку на посторонний адрес.".to_owned());
            }
            current_url = redirect;
            continue;
        }
        if !response.status().is_success() {
            return Err(format!(
                "GitHub вернул ошибку загрузки HTTP {}.",
                response.status()
            ));
        }
        let content_length = response
            .headers()
            .get(ureq::http::header::CONTENT_LENGTH)
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.parse::<u64>().ok());
        if content_length.is_some_and(|length| length > body_limit) {
            return Err("Ответ GitHub превышает допустимый размер.".to_owned());
        }
        return read_body(response);
    }
    Err("GitHub превысил число допустимых перенаправлений.".to_owned())
}

fn is_allowed_initial_url(value: &str) -> bool {
    value == RELEASE_API_URL
        || (value.starts_with(ASSET_PREFIX)
            && (value.ends_with("/RustText.zip") || value.ends_with("/RustText.zip.sha256")))
}

fn resolve_redirect(current: &str, location: &str) -> Result<String, String> {
    if location.starts_with("https://") {
        return Ok(location.to_owned());
    }
    if location.starts_with('/') {
        if location.starts_with("//") {
            return Err("GitHub прислал ссылку на другой сервер.".to_owned());
        }
        let uri = ureq::http::Uri::from_str(current)
            .map_err(|_| "Ссылка GitHub некорректна.".to_owned())?;
        let authority = uri
            .authority()
            .ok_or_else(|| "Ссылка GitHub некорректна.".to_owned())?;
        return Ok(format!("https://{authority}{location}"));
    }
    Err("GitHub прислал недопустимый адрес перенаправления.".to_owned())
}

fn is_allowed_redirect(value: &str) -> bool {
    let Ok(uri) = ureq::http::Uri::from_str(value) else {
        return false;
    };
    if uri.scheme_str() != Some("https")
        || uri.port_u16().is_some_and(|port| port != 443)
        || uri
            .authority()
            .is_none_or(|authority| authority.as_str().contains('@'))
    {
        return false;
    }
    uri.host().is_some_and(|host| {
        [
            "api.github.com",
            "github.com",
            "release-assets.githubusercontent.com",
            "objects.githubusercontent.com",
        ]
        .iter()
        .any(|allowed| host.eq_ignore_ascii_case(allowed))
    })
}

fn parse_api_digest(digest: &str) -> Result<String, String> {
    let hash = digest
        .strip_prefix("sha256:")
        .ok_or_else(|| "GitHub вернул неподдерживаемую контрольную сумму.".to_owned())?;
    if !is_sha256(hash) {
        return Err("GitHub вернул некорректную контрольную сумму.".to_owned());
    }
    Ok(hash.to_ascii_lowercase())
}

pub(super) fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

pub(super) fn max_archive_bytes() -> u64 {
    MAX_PACKAGE_BYTES
}

pub(super) fn max_checksum_bytes() -> u64 {
    MAX_SHA256_BYTES
}

pub(super) fn validate_release(release: &Release) -> Result<(), String> {
    let tag = format!("v{}", release.version);
    let expected_zip = format!("{ASSET_PREFIX}{tag}/RustText.zip");
    if !release.version.pre.is_empty()
        || !release.version.build.is_empty()
        || release.tag_name != tag
        || release.html_url != format!("{RELEASE_PAGE_PREFIX}{tag}")
        || release.zip_url != expected_zip
        || release.sha256_url != format!("{expected_zip}.sha256")
        || release.archive_size == 0
        || release.archive_size > MAX_PACKAGE_BYTES
    {
        return Err("Ссылка на обновление RustText не прошла проверку.".to_owned());
    }
    Ok(())
}

#[cfg(test)]
#[path = "../../tests/unit/updater_release.rs"]
mod tests;
