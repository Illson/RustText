mod install;
mod package;
mod release;

use semver::Version;

pub(crate) use install::{StagedUpdate, run_helper_if_requested};
pub(crate) use release::Release;

pub(crate) const VERSION: &str = env!("CARGO_PKG_VERSION");

pub(crate) fn check() -> Result<Option<Release>, String> {
    let current = Version::parse(VERSION).expect("invalid Cargo package version");
    release::check_update(&current)
}

pub(crate) fn stage(release: &Release) -> Result<StagedUpdate, String> {
    let staged = StagedUpdate::new_for_current_exe().map_err(|error| error.to_string())?;
    package::download_and_verify(release, staged.replacement_path())?;
    Ok(staged)
}
