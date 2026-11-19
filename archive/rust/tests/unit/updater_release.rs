use semver::Version;

use super::{is_allowed_redirect, parse_latest_release, resolve_redirect};

const HASH: &str = "ce8d7c68b0067eda8b3fee4dbe9db65d5fae69504a17317945bc99bfa1e43f74";

fn release_body(tag: &str, draft: bool, prerelease: bool, zip_url: &str, digest: &str) -> String {
    format!(
        r#"{{
            "tag_name": "{tag}",
            "html_url": "https://github.com/Illson/RustText/releases/tag/{tag}",
            "draft": {draft},
            "prerelease": {prerelease},
            "assets": [
                {{"name":"RustText.zip","browser_download_url":"{zip_url}","size":11163684,"digest":"{digest}"}},
                {{"name":"RustText.zip.sha256","browser_download_url":"https://github.com/Illson/RustText/releases/download/{tag}/RustText.zip.sha256","size":79}}
            ]
        }}"#
    )
}

fn valid_body() -> String {
    release_body(
        "v0.2.0",
        false,
        false,
        "https://github.com/Illson/RustText/releases/download/v0.2.0/RustText.zip",
        &format!("sha256:{HASH}"),
    )
}

#[test]
fn accepts_new_stable_release_from_only_the_expected_repository() {
    let result = parse_latest_release(&valid_body(), &Version::parse("0.1.1").unwrap())
        .unwrap()
        .unwrap();
    assert_eq!(result.version, Version::parse("0.2.0").unwrap());
    assert_eq!(result.tag_name, "v0.2.0");
    assert_eq!(result.archive_sha256.as_deref(), Some(HASH));
    assert_eq!(result.archive_size, 11_163_684);
}

#[test]
fn ignores_same_or_older_version_and_prereleases() {
    let body = valid_body();
    assert!(
        parse_latest_release(&body, &Version::parse("0.2.0").unwrap())
            .unwrap()
            .is_none()
    );
    assert!(
        parse_latest_release(&body, &Version::parse("0.3.0").unwrap())
            .unwrap()
            .is_none()
    );
    let prerelease = release_body(
        "v0.3.0-rc.1",
        false,
        true,
        "https://github.com/Illson/RustText/releases/download/v0.3.0-rc.1/RustText.zip",
        &format!("sha256:{HASH}"),
    );
    assert!(
        parse_latest_release(&prerelease, &Version::parse("0.2.0").unwrap())
            .unwrap()
            .is_none()
    );
}

#[test]
fn rejects_drafts_unsafe_assets_wrong_repository_and_bad_hashes() {
    let version = Version::parse("0.1.1").unwrap();
    let draft = release_body(
        "v0.2.0",
        true,
        false,
        "https://github.com/Illson/RustText/releases/download/v0.2.0/RustText.zip",
        &format!("sha256:{HASH}"),
    );
    assert!(parse_latest_release(&draft, &version).unwrap().is_none());

    for invalid in [
        valid_body().replace(
            "https://github.com/Illson/RustText/releases/download/",
            "https://attacker.example/RustText/releases/download/",
        ),
        valid_body().replace(&format!("sha256:{HASH}"), "md5:123"),
        valid_body().replace("v0.2.0", "v0.2.0/../../payload"),
    ] {
        assert!(parse_latest_release(&invalid, &version).is_err());
    }
}

#[test]
fn ignores_release_after_manifest_changes_during_parse() {
    let mut value: serde_json::Value = serde_json::from_str(&valid_body()).unwrap();
    value["assets"][0]["size"] = serde_json::json!(200 * 1024 * 1024_u64);
    assert!(parse_latest_release(&value.to_string(), &Version::parse("0.1.1").unwrap()).is_err());
}

#[test]
fn follows_only_https_github_release_redirect_hosts() {
    assert!(is_allowed_redirect(
        "https://release-assets.githubusercontent.com/releases/file.zip?token=123"
    ));
    assert!(is_allowed_redirect(
        "https://objects.githubusercontent.com/releases/file.zip"
    ));
    assert!(!is_allowed_redirect(
        "http://release-assets.githubusercontent.com/file.zip"
    ));
    assert!(!is_allowed_redirect("https://attacker.example/file.zip"));
    assert!(!is_allowed_redirect(
        "https://github.com.attacker.example/file.zip"
    ));
    assert!(!is_allowed_redirect("https://github.com:444/file.zip"));
    assert_eq!(
        resolve_redirect(
            "https://github.com/Illson/RustText/releases/latest",
            "/asset"
        )
        .unwrap(),
        "https://github.com/asset"
    );
    assert!(
        resolve_redirect(
            "https://github.com/Illson/RustText/releases/latest",
            "//attacker.example/asset"
        )
        .is_err()
    );
}
