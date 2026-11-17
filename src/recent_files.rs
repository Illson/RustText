use std::{
    env,
    fs::{self, OpenOptions},
    io,
    ops::Range,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};

use serde::{Deserialize, Serialize};
use tempfile::NamedTempFile;

use crate::{log_file::LogBookmark, text_format::TextEncoding};

static DISK_GENERATION: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Default, PartialEq, Deserialize, Serialize)]
pub(crate) struct RecentPosition {
    #[serde(default)]
    pub json_validation_disabled: bool,
    pub selection: Range<usize>,
    pub reversed: bool,
    pub scroll: [f32; 2],
    pub log: Option<LogBookmark>,
}

#[derive(Clone, Deserialize, Serialize)]
pub(crate) struct RecentFile {
    pub path: PathBuf,
    pub windows_1251: bool,
    #[serde(default)]
    pub view_as_log: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub position: Option<RecentPosition>,
}

impl RecentFile {
    pub(crate) fn new(path: PathBuf, encoding: TextEncoding) -> Self {
        Self {
            path,
            windows_1251: matches!(encoding, TextEncoding::Windows1251),
            view_as_log: false,
            position: None,
        }
    }
}

#[derive(Clone, Default)]
pub(crate) struct RecentFiles {
    path: Option<PathBuf>,
    entries: Vec<RecentFile>,
    pending: Vec<RecentFile>,
    positions: Vec<(PathBuf, RecentPosition)>,
    persisted_gen: Option<u64>,
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

    pub(crate) fn detached(&self) -> Self {
        Self {
            path: self.path.clone(),
            ..Self::default()
        }
    }

    pub(crate) fn record_in_memory(&mut self, files: &[RecentFile]) {
        for file in files {
            move_to_front(&mut self.entries, file.clone());
        }
    }

    pub(crate) fn record(&mut self, files: impl IntoIterator<Item = RecentFile>) -> io::Result<()> {
        for file in files {
            if file.position.is_some() {
                self.positions.retain(|(path, _)| path != &file.path);
            }
            move_to_front(&mut self.entries, file.clone());
            move_to_front(&mut self.pending, file);
        }
        self.persist()
    }

    pub(crate) fn record_positions(&mut self, files: Vec<RecentFile>) -> io::Result<()> {
        for file in files {
            let Some(position) = file.position.clone() else {
                continue;
            };
            if let Some(entry) = self
                .entries
                .iter_mut()
                .find(|entry| entry.path == file.path)
            {
                entry.position = Some(position.clone());
            } else {
                self.entries.push(file.clone());
                self.pending.push(file.clone());
            }
            self.positions.retain(|(path, _)| path != &file.path);
            self.positions.push((file.path, position));
        }
        self.persist()
    }

    pub(crate) fn merge_loaded(&mut self, other: Self) {
        if let (Some(ours), Some(theirs)) = (self.persisted_gen, other.persisted_gen)
            && theirs < ours
        {
            return;
        }
        let mut indices: std::collections::HashMap<_, _> = self
            .entries
            .iter()
            .enumerate()
            .map(|(index, entry)| (entry.path.clone(), index))
            .collect();
        let pending_entries: std::collections::HashSet<_> =
            self.pending.iter().map(|entry| &entry.path).collect();
        let pending_positions: std::collections::HashSet<_> =
            self.positions.iter().map(|(path, _)| path).collect();
        for entry in other.entries {
            if let Some(&index) = indices.get(&entry.path) {
                if !pending_entries.contains(&entry.path) {
                    let position = if pending_positions.contains(&entry.path) {
                        self.entries[index].position.clone()
                    } else {
                        entry.position
                    };
                    self.entries[index] = RecentFile { position, ..entry };
                }
            } else {
                indices.insert(entry.path.clone(), self.entries.len());
                self.entries.push(entry);
            }
        }
        if let Some(theirs) = other.persisted_gen {
            self.persisted_gen = Some(self.persisted_gen.map_or(theirs, |ours| ours.max(theirs)));
        }
    }

    fn persist(&mut self) -> io::Result<()> {
        if self.pending.is_empty() && self.positions.is_empty() {
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

        for (path, position) in &self.positions {
            if let Some(entry) = entries.iter_mut().find(|entry| &entry.path == path) {
                entry.position = Some(position.clone());
            }
        }

        let mut pending_file = NamedTempFile::new_in(directory)?;
        serde_json::to_writer(pending_file.as_file_mut(), &entries)?;
        pending_file.as_file().sync_all()?;
        pending_file.persist(path).map_err(|error| error.error)?;

        self.entries = entries;
        self.pending.clear();
        self.positions.clear();
        self.persisted_gen = Some(DISK_GENERATION.fetch_add(1, Ordering::SeqCst) + 1);
        Ok(())
    }
}

fn move_to_front(entries: &mut Vec<RecentFile>, mut file: RecentFile) {
    if file.position.is_none() {
        file.position = entries
            .iter()
            .find(|entry| entry.path == file.path)
            .and_then(|entry| entry.position.clone());
    }
    entries.retain(|entry| entry.path != file.path);
    entries.insert(0, file);
}

fn read_entries(path: &Path) -> io::Result<Vec<RecentFile>> {
    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error),
    };
    let entries: Vec<RecentFile> = serde_json::from_slice(&bytes)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    Ok(entries)
}

#[cfg(test)]
#[path = "../tests/unit/recent_files.rs"]
mod tests;
