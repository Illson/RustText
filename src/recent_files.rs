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
mod tests {
    use super::*;

    #[test]
    fn persists_order_and_encoding_and_moves_reopened_files_to_front() {
        let dir = tempfile::tempdir().unwrap();
        let mut recent = RecentFiles {
            path: Some(dir.path().join("recent-files.json")),
            ..RecentFiles::default()
        };
        let first = dir.path().join("Журнал.txt");
        let second = dir.path().join("Черновик.txt");
        recent
            .record([
                RecentFile::new(first.clone(), TextEncoding::Windows1251),
                RecentFile::new(second.clone(), TextEncoding::Utf8),
                RecentFile::new(first.clone(), TextEncoding::Windows1251),
            ])
            .unwrap();

        recent = RecentFiles {
            path: recent.path,
            ..RecentFiles::default()
        };
        recent.reload().unwrap();
        let entries = recent.entries();
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].path, first);
        assert!(entries[0].windows_1251);
        assert_eq!(entries[1].path, second);
        assert!(!entries[1].windows_1251);
    }

    #[test]
    fn repairs_truncated_history_when_a_file_is_opened() {
        let dir = tempfile::tempdir().unwrap();
        let history_path = dir.path().join("recent-files.json");
        fs::write(&history_path, "[").unwrap();
        let mut recent = RecentFiles {
            path: Some(history_path),
            ..RecentFiles::default()
        };
        assert_eq!(
            recent.reload().unwrap_err().kind(),
            io::ErrorKind::InvalidData
        );

        let file = dir.path().join("example.txt");
        recent
            .record([RecentFile::new(file.clone(), TextEncoding::Utf8)])
            .unwrap();
        recent.reload().unwrap();
        assert_eq!(recent.entries()[0].path, file);
    }

    #[test]
    fn merges_updates_from_two_windows_and_keeps_only_the_most_recent_files() {
        let dir = tempfile::tempdir().unwrap();
        let mut first_window = RecentFiles {
            path: Some(dir.path().join("recent-files.json")),
            ..RecentFiles::default()
        };
        let mut second_window = first_window.clone();
        let files = (0..MAX_FILES).map(|index| {
            RecentFile::new(dir.path().join(format!("{index}.txt")), TextEncoding::Utf8)
        });
        first_window.record(files).unwrap();
        let new_file = dir.path().join("new.txt");
        second_window
            .record([RecentFile::new(new_file.clone(), TextEncoding::Utf8)])
            .unwrap();

        first_window.reload().unwrap();
        assert_eq!(first_window.entries().len(), MAX_FILES);
        assert_eq!(first_window.entries()[0].path, new_file);
        assert_eq!(
            first_window.entries()[MAX_FILES - 1].path,
            dir.path().join("1.txt")
        );
    }
}
