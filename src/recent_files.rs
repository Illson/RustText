use std::{
    env,
    fs::{self, OpenOptions},
    io,
    path::{Path, PathBuf},
};

use serde::{Deserialize, Serialize};
use tempfile::NamedTempFile;

use crate::text_format::TextEncoding;

const MAX_FILES: usize = 16;

#[derive(Clone, Deserialize, Serialize)]
pub(crate) struct RecentFile {
    pub path: PathBuf,
    pub windows_1251: bool,
}

impl RecentFile {
    pub(crate) fn new(path: PathBuf, encoding: TextEncoding) -> Self {
        Self {
            path,
            windows_1251: matches!(encoding, TextEncoding::Windows1251),
        }
    }
}

#[derive(Clone, Default)]
pub(crate) struct RecentFiles {
    path: Option<PathBuf>,
    entries: Vec<RecentFile>,
    pending: Vec<RecentFile>,
}

impl RecentFiles {
    pub(crate) fn load() -> (Self, Option<io::Error>) {
        let path = env::var_os("LOCALAPPDATA")
            .filter(|directory| !directory.is_empty())
            .map(PathBuf::from)
            .map(|directory| directory.join("RustText").join("recent-files.json"));
        let mut recent = Self {
            path,
            ..Self::default()
        };
        let error = recent.reload().err();
        (recent, error)
    }

    fn reload(&mut self) -> io::Result<()> {
        if let Some(path) = &self.path {
            self.entries = read_entries(path)?;
        }
        Ok(())
    }

    pub(crate) fn entries(&self) -> &[RecentFile] {
        &self.entries
    }

    pub(crate) fn record(&mut self, files: impl IntoIterator<Item = RecentFile>) -> io::Result<()> {
        for file in files {
            move_to_front(&mut self.entries, file.clone());
            move_to_front(&mut self.pending, file);
        }
        if self.pending.is_empty() {
            return Ok(());
        }

        let path = self
            .path
            .as_ref()
            .ok_or_else(|| io::Error::other("Папка LOCALAPPDATA недоступна"))?;
        let directory = path
            .parent()
            .expect("recent history has a parent directory");
        fs::create_dir_all(directory)?;

        // Multiple running windows merge their updates under the same file lock.
        let lock = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(directory.join("recent-files.lock"))?;
        lock.lock()?;

        let mut entries = match read_entries(path) {
            Ok(entries) => entries,
            Err(error) if error.kind() == io::ErrorKind::InvalidData => Vec::new(),
            Err(error) => return Err(error),
        };
        for file in self.pending.iter().rev() {
            move_to_front(&mut entries, file.clone());
        }

        let mut pending_file = NamedTempFile::new_in(directory)?;
        serde_json::to_writer(pending_file.as_file_mut(), &entries)?;
        pending_file.as_file().sync_all()?;
        pending_file.persist(path).map_err(|error| error.error)?;

        self.entries = entries;
        self.pending.clear();
        Ok(())
    }
}

fn move_to_front(entries: &mut Vec<RecentFile>, file: RecentFile) {
    entries.retain(|entry| entry.path != file.path);
    entries.insert(0, file);
    entries.truncate(MAX_FILES);
}

fn read_entries(path: &Path) -> io::Result<Vec<RecentFile>> {
    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error),
    };
    let mut entries: Vec<RecentFile> = serde_json::from_slice(&bytes)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    entries.truncate(MAX_FILES);
    Ok(entries)
}

#[cfg(test)]
#[path = "../tests/unit/recent_files.rs"]
mod tests;
