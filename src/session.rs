use std::{
    collections::BTreeMap,
    env,
    fmt::Write as _,
    fs::{self, File, OpenOptions},
    io::{self, BufWriter, Read, Write},
    ops::Range,
    path::{Path, PathBuf},
};

use gpui_kit::component::input::Rope;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::{
    files::FileTarget,
    log_file::{FileIdentity, LogBookmark},
    text_format::TextFormat,
};

const FORMAT_VERSION: u32 = 1;
const RECORD_PREFIX: &str = "checkpoint-";
const PENDING_PREFIX: &str = ".pending-";
pub(crate) const MAX_CLOSED_TABS: usize = 100;

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct WindowSize {
    pub width: f32,
    pub height: f32,
    pub maximized: bool,
}

#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct State {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub layout: Option<crate::layout::Layout>,
    pub tabs: Vec<Tab>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub closed: Vec<Tab>,
    pub active: usize,
    pub window: WindowSize,
    pub wrap: bool,
    pub font_size: f32,
}

#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct Tab {
    #[serde(default)]
    pub json_validation_disabled: bool,
    pub id: u64,
    pub file: Option<FileTarget>,
    pub format: TextFormat,
    pub selection: Range<usize>,
    pub reversed: bool,
    pub scroll: [f64; 2],
    pub draft_revision: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub log: Option<LogBookmark>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub line_count: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_worked_ms: Option<u64>,
}

pub struct Capture {
    pub state: State,
    pub drafts: Vec<Option<Rope>>,
    pub closed_drafts: Vec<Option<Rope>>,
}

pub struct Recovered {
    pub state: State,
    pub drafts: Vec<Option<String>>,
    pub closed_drafts: Vec<Option<String>>,
}

#[derive(Serialize, Deserialize)]
struct Manifest {
    version: u32,
    state: State,
    drafts: Vec<Option<DraftInfo>>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    closed_drafts: Vec<Option<DraftInfo>>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct DraftDigest {
    pub bytes: u64,
    pub checksum: u32,
    pub sha256: [u8; 32],
}

impl DraftDigest {
    pub(crate) fn from_rope(text: &Rope) -> Self {
        let mut checksum = crc32fast::Hasher::new();
        let mut sha = Sha256::new();
        for chunk in text.chunks() {
            let bytes = chunk.as_bytes();
            checksum.update(bytes);
            sha.update(bytes);
        }
        Self {
            bytes: text.len() as u64,
            checksum: checksum.finalize(),
            sha256: sha.finalize().into(),
        }
    }

    fn sha256_hex(&self) -> String {
        let mut hex = String::with_capacity(64);
        for byte in self.sha256 {
            let _ = write!(hex, "{byte:02x}");
        }
        hex
    }
}

fn parse_sha256_hex(text: &str) -> Option<[u8; 32]> {
    if text.len() != 64 {
        return None;
    }
    let mut bytes = [0u8; 32];
    let (pairs, _) = text.as_bytes().as_chunks::<2>();
    for (slot, pair) in bytes.iter_mut().zip(pairs) {
        let pair = std::str::from_utf8(pair).ok()?;
        *slot = u8::from_str_radix(pair, 16).ok()?;
    }
    Some(bytes)
}

impl State {
    pub(crate) fn has_content_or_structure_change(&self, other: &Self) -> bool {
        let layout_changed = match (&self.layout, &other.layout) {
            (Some(left), Some(right)) => !left.same_structure(right),
            (None, None) => false,
            _ => true,
        };
        layout_changed
            || self.tabs.len() != other.tabs.len()
            || self.closed.len() != other.closed.len()
            || self
                .tabs
                .iter()
                .zip(&other.tabs)
                .any(|(left, right)| left.has_content_or_structure_change(right))
            || self
                .closed
                .iter()
                .zip(&other.closed)
                .any(|(left, right)| left.has_content_or_structure_change(right))
    }
}

impl Tab {
    fn has_content_or_structure_change(&self, other: &Self) -> bool {
        self.id != other.id
            || self.file != other.file
            || self.format != other.format
            || self.draft_revision != other.draft_revision
            || self.json_validation_disabled != other.json_validation_disabled
            || self
                .log
                .as_ref()
                .map(|log| (log.hidden, log.auto_refresh, log.follow_end))
                != other
                    .log
                    .as_ref()
                    .map(|log| (log.hidden, log.auto_refresh, log.follow_end))
    }
}

#[derive(Serialize, Deserialize)]
struct DraftInfo {
    bytes: u64,
    checksum: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    sha256: Option<String>,
}

pub struct Startup {
    pub journal: Option<Journal>,
    pub recovered: Option<Recovered>,
    pub warning: Option<String>,
}

pub fn startup() -> Startup {
    let result = env::var_os("LOCALAPPDATA")
        .filter(|directory| !directory.is_empty())
        .map(PathBuf::from)
        .map(|directory| directory.join("RustText").join("session"))
        .ok_or_else(|| io::Error::other("Папка LOCALAPPDATA недоступна"))
        .and_then(Journal::open);
    let mut journal = match result {
        Ok(journal) => journal,
        Err(error) => {
            return Startup {
                journal: None,
                recovered: None,
                warning: Some(format!("Сохранение сессии недоступно. {error}")),
            };
        }
    };
    let (recovered, warning) = journal.restore();
    Startup {
        journal: Some(journal),
        recovered,
        warning,
    }
}

#[derive(Clone)]
struct DonorFile {
    path: PathBuf,
    digest: DraftDigest,
}

pub struct Journal {
    directory: PathBuf,
    _lock: File,
    last_valid: Option<u64>,
    last_sequence: u64,
    committed_revision: u64,
    donors: BTreeMap<u64, Vec<DonorFile>>,
}

impl Journal {
    pub(super) fn open(directory: PathBuf) -> io::Result<Self> {
        fs::create_dir_all(&directory)?;
        let lock = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(directory.join("session.lock"))?;
        lock.try_lock().map_err(|error| {
            let error = io::Error::from(error);
            if error.kind() == io::ErrorKind::WouldBlock {
                io::Error::new(error.kind(), "Сессия уже открыта в другом окне RustText")
            } else {
                error
            }
        })?;
        cleanup_pending_dirs(&directory);
        let last_sequence = records(&directory)?.first().copied().unwrap_or_default();
        Ok(Self {
            directory,
            _lock: lock,
            last_valid: None,
            last_sequence,
            committed_revision: 0,
            donors: BTreeMap::new(),
        })
    }

    fn restore(&mut self) -> (Option<Recovered>, Option<String>) {
        let sequences = match records(&self.directory) {
            Ok(sequences) => sequences,
            Err(error) => {
                return (
                    None,
                    Some(format!("Не удалось прочитать журнал сессии. {error}")),
                );
            }
        };
        let mut latest_error = None;
        for (index, &sequence) in sequences.iter().enumerate() {
            match read_record_with_donors(&self.record_path(sequence)) {
                Ok((recovered, donors)) => {
                    self.last_valid = Some(sequence);
                    self.donors.insert(sequence, donors);
                    if let Some(&older) = sequences.get(index + 1)
                        && let Ok(older_donors) = read_manifest_donors(&self.record_path(older))
                    {
                        self.donors.insert(older, older_donors);
                    }
                    let warning = latest_error.map(|error| {
                        format!(
                            "Последняя запись сессии повреждена. Восстановлена предыдущая. {error}"
                        )
                    });
                    return (Some(recovered), warning);
                }
                Err(error) => {
                    latest_error.get_or_insert(error);
                }
            }
        }
        let warning = latest_error.map(|error| format!("Не удалось восстановить сессию. {error}"));
        (None, warning)
    }

    #[cfg(test)]
    pub fn commit(&mut self, revision: u64, capture: Capture) -> io::Result<()> {
        self.commit_prepared(revision, capture, Vec::new(), Vec::new())
    }

    pub(crate) fn commit_prepared(
        &mut self,
        revision: u64,
        mut capture: Capture,
        draft_digests: Vec<Option<DraftDigest>>,
        mut closed_draft_digests: Vec<Option<DraftDigest>>,
    ) -> io::Result<()> {
        if revision <= self.committed_revision {
            return Ok(());
        }

        let previous = self.last_valid;
        if capture.state.tabs.len() != capture.drafts.len() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Количество открытых вкладок не совпадает с количеством черновиков",
            ));
        }
        if capture.state.closed.len() != capture.closed_drafts.len() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Количество закрытых вкладок не совпадает с количеством черновиков",
            ));
        }
        let excess = capture.state.closed.len().saturating_sub(MAX_CLOSED_TABS);
        capture.state.closed.drain(..excess);
        capture.closed_drafts.drain(..excess);
        if !closed_draft_digests.is_empty() {
            closed_draft_digests.drain(..excess.min(closed_draft_digests.len()));
        }
        let sequence = self
            .last_sequence
            .checked_add(1)
            .ok_or_else(|| io::Error::other("Номер записи сессии исчерпан"))?;
        let final_dir = self.record_path(sequence);
        let pending = tempfile::Builder::new()
            .prefix(PENDING_PREFIX)
            .tempdir_in(&self.directory)?;
        // Reuse unchanged blobs only from non-fallback checkpoints that do not
        // share physical files with `previous`, keeping two independent backups.
        let excluded_identities: Option<Vec<FileIdentity>> = match previous {
            None => Some(Vec::new()),
            Some(prev) => checkpoint_file_identities(&self.record_path(prev)).ok(),
        };
        let (drafts, mut new_donors) = write_or_link_drafts(
            pending.path(),
            &final_dir,
            "draft",
            &capture.drafts,
            &draft_digests,
            &self.donors,
            previous,
            excluded_identities.as_deref(),
        )?;
        let (closed_drafts, closed_donors) = write_or_link_drafts(
            pending.path(),
            &final_dir,
            "closed-draft",
            &capture.closed_drafts,
            &closed_draft_digests,
            &self.donors,
            previous,
            excluded_identities.as_deref(),
        )?;
        new_donors.extend(closed_donors);
        let manifest = Manifest {
            version: FORMAT_VERSION,
            drafts,
            closed_drafts,
            state: capture.state,
        };
        let file = File::create(pending.path().join("manifest.json"))?;
        let mut writer = BufWriter::new(file);
        serde_json::to_writer(&mut writer, &manifest)?;
        writer.flush()?;
        writer.get_ref().sync_all()?;
        drop(writer);

        // The journal contains only records whose manifest and drafts were all flushed.
        fs::rename(pending.path(), &final_dir)?;
        self.last_sequence = sequence;
        self.last_valid = Some(sequence);
        self.committed_revision = revision;
        self.donors.insert(sequence, new_donors);

        if let Ok(sequences) = records(&self.directory) {
            for old in sequences
                .into_iter()
                .filter(|old| *old != sequence && Some(*old) != previous)
            {
                let _ = fs::remove_dir_all(self.record_path(old));
                self.donors.remove(&old);
            }
        }
        Ok(())
    }

    fn record_path(&self, sequence: u64) -> PathBuf {
        self.directory
            .join(format!("{RECORD_PREFIX}{sequence:020}"))
    }
}

fn cleanup_pending_dirs(directory: &Path) {
    let Ok(entries) = fs::read_dir(directory) else {
        return;
    };
    for entry in entries.flatten() {
        if entry.file_type().is_ok_and(|kind| kind.is_dir())
            && entry
                .file_name()
                .to_str()
                .is_some_and(|name| name.starts_with(PENDING_PREFIX))
        {
            let _ = fs::remove_dir_all(entry.path());
        }
    }
}

fn records(directory: &Path) -> io::Result<Vec<u64>> {
    let mut sequences = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        if entry.file_type()?.is_dir()
            && let Some(name) = entry.file_name().to_str()
            && let Some(number) = name
                .strip_prefix(RECORD_PREFIX)
                .filter(|number| number.len() == 20)
            && let Ok(sequence) = number.parse::<u64>()
        {
            sequences.push(sequence);
        }
    }
    sequences.sort_unstable_by(|left, right| right.cmp(left));
    Ok(sequences)
}

fn checkpoint_file_identities(directory: &Path) -> io::Result<Vec<FileIdentity>> {
    let mut ids = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        if !entry.file_type()?.is_file() {
            continue;
        }
        let file = File::open(entry.path())?;
        ids.push(FileIdentity::read(&file)?);
    }
    Ok(ids)
}

#[allow(clippy::too_many_arguments)]
fn write_or_link_drafts(
    pending_dir: &Path,
    final_dir: &Path,
    prefix: &str,
    drafts: &[Option<Rope>],
    precomputed: &[Option<DraftDigest>],
    donors: &BTreeMap<u64, Vec<DonorFile>>,
    excluded_previous: Option<u64>,
    excluded_identities: Option<&[FileIdentity]>,
) -> io::Result<(Vec<Option<DraftInfo>>, Vec<DonorFile>)> {
    let mut infos = Vec::with_capacity(drafts.len());
    let mut created_donors = Vec::new();
    for (index, draft) in drafts.iter().enumerate() {
        let Some(text) = draft.as_ref() else {
            infos.push(None);
            continue;
        };
        let digest = precomputed
            .get(index)
            .copied()
            .flatten()
            .unwrap_or_else(|| DraftDigest::from_rope(text));
        let file_name = format!("{prefix}-{index}.txt");
        let pending_path = pending_dir.join(&file_name);
        let final_path = final_dir.join(&file_name);

        let linked = excluded_identities.is_some_and(|identities| {
            try_link_matching_donor(
                donors,
                excluded_previous,
                identities,
                &digest,
                &pending_path,
            )
        });
        if !linked {
            write_draft(&pending_path, text)?;
        }
        created_donors.push(DonorFile {
            path: final_path,
            digest,
        });
        infos.push(Some(DraftInfo {
            bytes: digest.bytes,
            checksum: digest.checksum,
            sha256: Some(digest.sha256_hex()),
        }));
    }
    Ok((infos, created_donors))
}

fn try_link_matching_donor(
    donors: &BTreeMap<u64, Vec<DonorFile>>,
    excluded_previous: Option<u64>,
    excluded_identities: &[FileIdentity],
    digest: &DraftDigest,
    target_path: &Path,
) -> bool {
    donors
        .iter()
        .rev()
        .filter(|(seq, _)| Some(**seq) != excluded_previous)
        .flat_map(|(_, files)| files.iter())
        .filter(|donor| donor.digest == *digest)
        .any(|donor| try_hard_link_donor(donor, target_path, excluded_identities))
}

fn open_read_blocking_writers(path: &Path) -> io::Result<File> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt as _;
        const FILE_SHARE_READ: u32 = 1;
        options.share_mode(FILE_SHARE_READ);
    }
    options.open(path)
}

fn try_hard_link_donor(
    donor: &DonorFile,
    target_path: &Path,
    excluded_identities: &[FileIdentity],
) -> bool {
    let Ok(meta) = fs::symlink_metadata(&donor.path) else {
        return false;
    };
    if !meta.file_type().is_file() || meta.len() != donor.digest.bytes {
        return false;
    }
    let Ok(mut donor_file) = open_read_blocking_writers(&donor.path) else {
        return false;
    };
    let Ok(donor_meta) = donor_file.metadata() else {
        return false;
    };
    if !donor_meta.file_type().is_file() || donor_meta.len() != donor.digest.bytes {
        return false;
    }
    let Ok(donor_id) = FileIdentity::read(&donor_file) else {
        return false;
    };
    if excluded_identities.contains(&donor_id)
        || !verify_open_file_digest(&mut donor_file, &donor.digest)
    {
        return false;
    }
    if fs::hard_link(&donor.path, target_path).is_err() {
        return false;
    }
    let verified = open_read_blocking_writers(target_path)
        .ok()
        .and_then(|target_file| FileIdentity::read(&target_file).ok())
        .is_some_and(|target_id| target_id == donor_id);
    if !verified {
        let _ = fs::remove_file(target_path);
        return false;
    }
    true
}

fn verify_open_file_digest(file: &mut File, expected: &DraftDigest) -> bool {
    let mut buffer = [0u8; 64 * 1024];
    let mut checksum = crc32fast::Hasher::new();
    let mut sha = Sha256::new();
    let mut total = 0u64;
    loop {
        let Ok(read) = file.read(&mut buffer) else {
            return false;
        };
        if read == 0 {
            break;
        }
        let slice = &buffer[..read];
        total += read as u64;
        if total > expected.bytes {
            return false;
        }
        checksum.update(slice);
        sha.update(slice);
    }
    let actual_sha: [u8; 32] = sha.finalize().into();
    total == expected.bytes
        && checksum.finalize() == expected.checksum
        && actual_sha == expected.sha256
}

fn write_draft(path: &Path, text: &Rope) -> io::Result<()> {
    let mut writer = BufWriter::with_capacity(256 * 1024, File::create(path)?);
    for chunk in text.chunks() {
        writer.write_all(chunk.as_bytes())?;
    }
    writer.flush()?;
    let file = writer.into_inner().map_err(io::Error::from)?;
    file.sync_all()?;
    Ok(())
}

fn read_manifest_donors(directory: &Path) -> io::Result<Vec<DonorFile>> {
    let Manifest {
        version,
        state,
        drafts,
        closed_drafts,
    } = serde_json::from_slice(&fs::read(directory.join("manifest.json"))?)?;
    if version != FORMAT_VERSION
        || state.active >= state.tabs.len().max(1)
        || state.tabs.len() != drafts.len()
        || state.closed.len() != closed_drafts.len()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Некорректный манифест сессии",
        ));
    }
    let mut donors = Vec::new();
    for (prefix, list) in [("draft", drafts), ("closed-draft", closed_drafts)] {
        for (index, info) in list.into_iter().enumerate() {
            let Some(info) = info else { continue };
            let Some(sha256) = info.sha256.as_deref().and_then(parse_sha256_hex) else {
                continue;
            };
            let path = directory.join(format!("{prefix}-{index}.txt"));
            let Ok(meta) = fs::symlink_metadata(&path) else {
                continue;
            };
            if meta.file_type().is_file() && meta.len() == info.bytes {
                donors.push(DonorFile {
                    path,
                    digest: DraftDigest {
                        bytes: info.bytes,
                        checksum: info.checksum,
                        sha256,
                    },
                });
            }
        }
    }
    Ok(donors)
}

fn read_record_with_donors(directory: &Path) -> io::Result<(Recovered, Vec<DonorFile>)> {
    let Manifest {
        version,
        mut state,
        drafts,
        closed_drafts,
    } = serde_json::from_slice(&fs::read(directory.join("manifest.json"))?)?;
    if version != FORMAT_VERSION
        || state.active >= state.tabs.len().max(1)
        || state.tabs.len() != drafts.len()
        || state.closed.len() != closed_drafts.len()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Некорректный манифест сессии",
        ));
    }
    let mut donors = Vec::new();
    let drafts = read_drafts(directory, "draft", &state.tabs, drafts, 0, &mut donors)?;
    let excess = state.closed.len().saturating_sub(MAX_CLOSED_TABS);
    let closed_drafts = read_drafts(
        directory,
        "closed-draft",
        &state.closed,
        closed_drafts,
        excess,
        &mut donors,
    )?;
    state.closed.drain(..excess);
    Ok((
        Recovered {
            state,
            drafts,
            closed_drafts,
        },
        donors,
    ))
}

fn read_drafts(
    directory: &Path,
    prefix: &str,
    tabs: &[Tab],
    drafts: Vec<Option<DraftInfo>>,
    skip: usize,
    donors: &mut Vec<DonorFile>,
) -> io::Result<Vec<Option<String>>> {
    drafts
        .into_iter()
        .zip(tabs)
        .enumerate()
        .skip(skip)
        .map(|(index, (draft, tab))| match (draft, tab.draft_revision) {
            (Some(draft), Some(_)) => {
                let path = directory.join(format!("{prefix}-{index}.txt"));
                let (text, donor) = read_draft(&path, draft)?;
                donors.push(donor);
                Ok(Some(text))
            }
            (None, None) => Ok(None),
            _ => Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Отсутствует описание черновика",
            )),
        })
        .collect()
}

fn read_draft(path: &Path, info: DraftInfo) -> io::Result<(String, DonorFile)> {
    let text = fs::read(path)?;
    let actual_checksum = crc32fast::hash(&text);
    let actual_sha: [u8; 32] = Sha256::digest(&text).into();
    let sha_matches = match info.sha256.as_deref() {
        Some(expected) => parse_sha256_hex(expected).is_some_and(|bytes| bytes == actual_sha),
        None => true,
    };
    if text.len() as u64 != info.bytes || actual_checksum != info.checksum || !sha_matches {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Повреждён снимок черновика",
        ));
    }
    let digest = DraftDigest {
        bytes: info.bytes,
        checksum: actual_checksum,
        sha256: actual_sha,
    };
    let text = String::from_utf8(text)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    Ok((
        text,
        DonorFile {
            path: path.to_path_buf(),
            digest,
        },
    ))
}

#[cfg(test)]
#[path = "../tests/unit/session.rs"]
mod tests;
