use std::{
    env,
    fs::{self, File, OpenOptions},
    io::{self, BufWriter, Write},
    ops::Range,
    path::{Path, PathBuf},
};

use gpui_kit::component::input::Rope;
use serde::{Deserialize, Serialize};

use crate::{files::FileTarget, text_format::TextFormat};

const FORMAT_VERSION: u32 = 1;
const RECORD_PREFIX: &str = "checkpoint-";

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct WindowSize {
    pub width: f32,
    pub height: f32,
    pub maximized: bool,
}

#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct State {
    pub tabs: Vec<Tab>,
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
}

pub struct Capture {
    pub state: State,
    pub drafts: Vec<Option<Rope>>,
}

pub struct Recovered {
    pub state: State,
    pub drafts: Vec<Option<String>>,
}

#[derive(Serialize, Deserialize)]
struct Manifest {
    version: u32,
    state: State,
    drafts: Vec<Option<DraftInfo>>,
}

#[derive(Serialize, Deserialize)]
struct DraftInfo {
    bytes: u64,
    checksum: u32,
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
    fn open(directory: PathBuf) -> io::Result<Self> {
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

    pub fn commit(&mut self, revision: u64, capture: Capture) -> io::Result<()> {
        if revision <= self.committed_revision {
            return Ok(());
        }

        let previous = self.last_valid;
        let sequence = self
            .last_sequence
            .checked_add(1)
            .ok_or_else(|| io::Error::other("Номер записи сессии исчерпан"))?;
        let pending = tempfile::Builder::new()
            .prefix(".pending-")
            .tempdir_in(&self.directory)?;
        let draft_info = capture
            .drafts
            .iter()
            .enumerate()
            .map(|(index, draft)| {
                draft
                    .as_ref()
                    .map(|text| write_draft(pending.path(), index, text))
                    .transpose()
            })
            .collect::<io::Result<Vec<_>>>()?;
        let manifest = Manifest {
            version: FORMAT_VERSION,
            state: capture.state,
            drafts: draft_info,
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

fn draft_path(directory: &Path, index: usize) -> PathBuf {
    directory.join(format!("draft-{index}.txt"))
}

fn write_draft(directory: &Path, index: usize, text: &Rope) -> io::Result<DraftInfo> {
    let mut writer = BufWriter::new(File::create(draft_path(directory, index))?);
    let mut checksum = crc32fast::Hasher::new();
    for chunk in text.chunks() {
        checksum.update(chunk.as_bytes());
        writer.write_all(chunk.as_bytes())?;
    }
    writer.flush()?;
    writer.get_ref().sync_all()?;
    Ok(DraftInfo {
        bytes: text.len() as u64,
        checksum: checksum.finalize(),
    })
}

fn read_record(directory: &Path) -> io::Result<Recovered> {
    let Manifest {
        version,
        state,
        drafts,
    } = serde_json::from_slice(&fs::read(directory.join("manifest.json"))?)?;
    if version != FORMAT_VERSION
        || state.active >= state.tabs.len()
        || state.tabs.len() != drafts.len()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Некорректный манифест сессии",
        ));
    }
    let drafts = drafts
        .into_iter()
        .zip(&state.tabs)
        .enumerate()
        .map(|(index, (draft, tab))| match draft {
            Some(draft) if tab.draft_revision.is_some() => {
                read_draft(directory, index, draft).map(Some)
            }
            None if tab.draft_revision.is_none() => Ok(None),
            _ => Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Отсутствует описание черновика",
            )),
        })
        .collect::<io::Result<Vec<_>>>()?;
    Ok(Recovered { state, drafts })
}

fn read_draft(directory: &Path, index: usize, info: DraftInfo) -> io::Result<String> {
    let text = fs::read(draft_path(directory, index))?;
    if text.len() as u64 != info.bytes || crc32fast::hash(&text) != info.checksum {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Повреждён снимок черновика",
        ));
    }
    String::from_utf8(text).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn capture(text: &str) -> Capture {
        Capture {
            state: State {
                tabs: vec![Tab {
                    id: 1,
                    file: None,
                    format: TextFormat::default(),
                    selection: 0..2,
                    reversed: true,
                    scroll: [0., -154.],
                    draft_revision: Some(1),
                }],
                active: 0,
                window: WindowSize {
                    width: 870.,
                    height: 640.,
                    maximized: false,
                },
                wrap: true,
                font_size: 16.,
            },
            drafts: vec![Some(Rope::from(text))],
        }
    }

    #[test]
    fn restores_previous_commit_if_the_latest_draft_or_manifest_is_corrupt() {
        for damaged_file in ["draft-0.txt", "manifest.json"] {
            let temporary = tempfile::tempdir().unwrap();
            let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
            journal.commit(1, capture("ёж")).unwrap();
            journal.commit(2, capture("новый")).unwrap();
            fs::write(journal.record_path(2).join(damaged_file), "corrupt").unwrap();
            fs::create_dir(temporary.path().join(".pending-incomplete")).unwrap();
            drop(journal);
            let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
            let (recovered, warning) = journal.restore();
            let recovered = recovered.unwrap();
            assert!(warning.is_some());
            assert_eq!(recovered.drafts, [Some("ёж".into())]);
            assert_eq!(recovered.state.tabs[0].selection, 0..2);
            assert!(recovered.state.tabs[0].reversed);
            assert_eq!(recovered.state.tabs[0].scroll, [0., -154.]);
            assert_eq!(recovered.state.window.width, 870.);
        }
    }

    #[test]
    fn a_delayed_periodic_commit_cannot_overwrite_a_newer_shutdown_commit() {
        let temporary = tempfile::tempdir().unwrap();
        let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
        journal.commit(2, capture("при закрытии")).unwrap();
        journal.commit(1, capture("устарело")).unwrap();
        let (recovered, warning) = journal.restore();
        assert!(warning.is_none());
        assert_eq!(recovered.unwrap().drafts, [Some("при закрытии".into())]);
    }
}
