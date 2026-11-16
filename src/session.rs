use std::{
    env,
    fs::{self, File, OpenOptions},
    io::{self, BufWriter, Write},
    ops::Range,
    path::{Path, PathBuf},
    time::UNIX_EPOCH,
};

use gpui_kit::component::input::Rope;
use serde::{Deserialize, Serialize};

use crate::{files::FileTarget, log_file::LogBookmark, text_format::TextFormat};

const FORMAT_VERSION: u32 = 1;
const RECORD_PREFIX: &str = "checkpoint-";
pub(crate) const MAX_CLOSED_TABS: usize = 100;

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct WindowSize {
    pub width: f32,
    pub height: f32,
    pub maximized: bool,
}

#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct State {
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
    pub id: u64,
    pub file: Option<FileTarget>,
    pub format: TextFormat,
    pub selection: Range<usize>,
    pub reversed: bool,
    pub scroll: [f32; 2],
    pub draft_revision: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub log: Option<LogBookmark>,
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

#[derive(Clone, Copy, Serialize, Deserialize)]
struct DraftInfo {
    bytes: u64,
    checksum: u32,
    #[serde(default)]
    modified_unix_nanos: Option<u128>,
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

pub struct Journal {
    directory: PathBuf,
    _lock: File,
    last_valid: Option<u64>,
    last_sequence: u64,
    committed_revision: u64,
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
        let last_sequence = records(&directory)?.first().copied().unwrap_or_default();
        Ok(Self {
            directory,
            _lock: lock,
            last_valid: None,
            last_sequence,
            committed_revision: 0,
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
        for sequence in sequences {
            match read_record(&self.record_path(sequence)) {
                Ok(recovered) => {
                    self.last_valid = Some(sequence);
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

    pub fn commit(&mut self, revision: u64, mut capture: Capture) -> io::Result<()> {
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
        let sequence = self
            .last_sequence
            .checked_add(1)
            .ok_or_else(|| io::Error::other("Номер записи сессии исчерпан"))?;
        let pending = tempfile::Builder::new()
            .prefix(".pending-")
            .tempdir_in(&self.directory)?;
        let previous_manifest =
            previous.and_then(|sequence| read_manifest(&self.record_path(sequence)).ok());
        let manifest = Manifest {
            version: FORMAT_VERSION,
            drafts: write_drafts(
                pending.path(),
                "draft",
                &capture.state.tabs,
                &capture.drafts,
                previous_manifest
                    .as_ref()
                    .map(|manifest| (manifest.state.tabs.as_slice(), manifest.drafts.as_slice())),
                previous.map(|sequence| self.record_path(sequence)),
            )?,
            closed_drafts: write_drafts(
                pending.path(),
                "closed-draft",
                &capture.state.closed,
                &capture.closed_drafts,
                previous_manifest.as_ref().map(|manifest| {
                    (
                        manifest.state.closed.as_slice(),
                        manifest.closed_drafts.as_slice(),
                    )
                }),
                previous.map(|sequence| self.record_path(sequence)),
            )?,
            state: capture.state,
        };
        let file = File::create(pending.path().join("manifest.json"))?;
        let mut writer = BufWriter::new(file);
        serde_json::to_writer(&mut writer, &manifest)?;
        writer.flush()?;
        writer.get_ref().sync_all()?;
        drop(writer);

        // The journal contains only records whose manifest and drafts were all flushed.
        fs::rename(pending.path(), self.record_path(sequence))?;
        self.last_sequence = sequence;
        self.last_valid = Some(sequence);
        self.committed_revision = revision;

        if let Ok(sequences) = records(&self.directory) {
            for old in sequences
                .into_iter()
                .filter(|old| *old != sequence && Some(*old) != previous)
            {
                let _ = fs::remove_dir_all(self.record_path(old));
            }
        }
        Ok(())
    }

    fn record_path(&self, sequence: u64) -> PathBuf {
        self.directory
            .join(format!("{RECORD_PREFIX}{sequence:020}"))
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

fn read_manifest(directory: &Path) -> io::Result<Manifest> {
    serde_json::from_slice(&fs::read(directory.join("manifest.json"))?).map_err(io::Error::other)
}

fn write_drafts(
    directory: &Path,
    prefix: &str,
    tabs: &[Tab],
    drafts: &[Option<Rope>],
    previous: Option<(&[Tab], &[Option<DraftInfo>])>,
    previous_directory: Option<PathBuf>,
) -> io::Result<Vec<Option<DraftInfo>>> {
    drafts
        .iter()
        .enumerate()
        .map(|(index, draft)| {
            draft
                .as_ref()
                .map(|text| {
                    let destination = directory.join(format!("{prefix}-{index}.txt"));
                    if let Some(info) = reusable_draft(&tabs[index], previous)
                        && info.draft.bytes == text.len() as u64
                        && let Some(previous_directory) = &previous_directory
                        && link_verified_draft(
                            &previous_directory.join(format!("{prefix}-{}.txt", info.index)),
                            &destination,
                            &info.draft,
                        )
                    {
                        return Ok(info.draft);
                    }
                    write_draft(&destination, text)
                })
                .transpose()
        })
        .collect()
}

struct ReusableDraft {
    index: usize,
    draft: DraftInfo,
}

fn reusable_draft(
    tab: &Tab,
    previous: Option<(&[Tab], &[Option<DraftInfo>])>,
) -> Option<ReusableDraft> {
    let (previous_tabs, previous_drafts) = previous?;
    let revision = tab.draft_revision?;
    let previous_index = previous_tabs.iter().position(|previous_tab| {
        previous_tab.id == tab.id && previous_tab.draft_revision == Some(revision)
    })?;
    let draft = previous_drafts.get(previous_index)?.as_ref()?;
    Some(ReusableDraft {
        index: previous_index,
        draft: *draft,
    })
}

fn link_verified_draft(source: &Path, destination: &Path, info: &DraftInfo) -> bool {
    let Ok(metadata) = fs::metadata(source) else {
        return false;
    };
    if info.modified_unix_nanos.is_none()
        || metadata.len() != info.bytes
        || modified_unix_nanos(&metadata).ok().flatten() != info.modified_unix_nanos
    {
        return false;
    }
    // Restore validates checksums before a record becomes last_valid. The saved
    // modification time detects ordinary external writes without rereading the text.
    // Writes through any hard link affect every record sharing this snapshot.
    fs::hard_link(source, destination).is_ok()
}

fn write_draft(path: &Path, text: &Rope) -> io::Result<DraftInfo> {
    let mut writer = BufWriter::new(File::create(path)?);
    let mut checksum = crc32fast::Hasher::new();
    for chunk in text.chunks() {
        checksum.update(chunk.as_bytes());
        writer.write_all(chunk.as_bytes())?;
    }
    writer.flush()?;
    writer.get_ref().sync_all()?;
    drop(writer);
    let modified_unix_nanos = fs::metadata(path)
        .ok()
        .and_then(|metadata| modified_unix_nanos(&metadata).ok().flatten());
    Ok(DraftInfo {
        bytes: text.len() as u64,
        checksum: checksum.finalize(),
        modified_unix_nanos,
    })
}

fn modified_unix_nanos(metadata: &fs::Metadata) -> io::Result<Option<u128>> {
    let modified = metadata.modified()?;
    Ok(modified
        .duration_since(UNIX_EPOCH)
        .ok()
        .map(|duration| duration.as_nanos()))
}

fn read_record(directory: &Path) -> io::Result<Recovered> {
    let Manifest {
        version,
        mut state,
        drafts,
        closed_drafts,
    } = read_manifest(directory)?;
    if version != FORMAT_VERSION
        || state.active >= state.tabs.len()
        || state.tabs.len() != drafts.len()
        || state.closed.len() != closed_drafts.len()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Некорректный манифест сессии",
        ));
    }
    let drafts = read_drafts(directory, "draft", &state.tabs, drafts, 0)?;
    let excess = state.closed.len().saturating_sub(MAX_CLOSED_TABS);
    let closed_drafts = read_drafts(
        directory,
        "closed-draft",
        &state.closed,
        closed_drafts,
        excess,
    )?;
    state.closed.drain(..excess);
    Ok(Recovered {
        state,
        drafts,
        closed_drafts,
    })
}

fn read_drafts(
    directory: &Path,
    prefix: &str,
    tabs: &[Tab],
    drafts: Vec<Option<DraftInfo>>,
    skip: usize,
) -> io::Result<Vec<Option<String>>> {
    drafts
        .into_iter()
        .zip(tabs)
        .enumerate()
        .skip(skip)
        .map(|(index, (draft, tab))| match (draft, tab.draft_revision) {
            (Some(draft), Some(_)) => {
                read_draft(&directory.join(format!("{prefix}-{index}.txt")), draft).map(Some)
            }
            (None, None) => Ok(None),
            _ => Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Отсутствует описание черновика",
            )),
        })
        .collect()
}

fn read_draft(path: &Path, info: DraftInfo) -> io::Result<String> {
    let text = fs::read(path)?;
    if text.len() as u64 != info.bytes || crc32fast::hash(&text) != info.checksum {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Повреждён снимок черновика",
        ));
    }
    String::from_utf8(text).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
}

#[cfg(test)]
#[path = "../tests/unit/session.rs"]
mod tests;
