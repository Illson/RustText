use std::{
    io::{self, BufWriter, Write},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::Duration,
};

use gpui_kit::{component::input::Rope, *};
use tempfile::NamedTempFile;

use super::{
    Document, Workspace,
    disk_status::DiskStatus,
    draft_name,
    file_actions::{LoadedDocument, OpenMode, read_document},
};
use crate::{
    files::{self, FileTarget, OpenedFile},
    log_file::{self, LogBookmark},
    log_viewer::LogViewer,
    session::{Draft, DraftDigest, Tab},
    text_format::TextEncoding,
};

// GPUI builds one layout entry per logical line even when wrapping is off.
const MAX_EDITOR_LINES: usize = 50_000;
const MAX_EDITOR_LINE_BYTES: usize = 64 * 1024;
pub(super) const MAX_EDITOR_DRAFT_BYTES: u64 = 8 * 1024 * 1024;

#[derive(Default)]
struct EditorBudget {
    bytes: u64,
    breaks: usize,
    line_bytes: usize,
}

impl EditorBudget {
    fn accepts(&mut self, bytes: &[u8]) -> bool {
        self.bytes += bytes.len() as u64;
        if self.bytes > MAX_EDITOR_DRAFT_BYTES {
            return false;
        }
        for part in bytes.split_inclusive(|byte| *byte == b'\n') {
            let newline = part.ends_with(b"\n");
            self.line_bytes += part.len() - usize::from(newline);
            if self.line_bytes > MAX_EDITOR_LINE_BYTES {
                return false;
            }
            if newline {
                self.line_bytes = 0;
                self.breaks += 1;
                if self.breaks >= MAX_EDITOR_LINES {
                    return false;
                }
            }
        }
        true
    }
}

enum StagedDraft {
    Editable(String),
    Preview(NamedTempFile),
}

struct DraftBuffer<'a> {
    budget: EditorBudget,
    prefix: Vec<u8>,
    preview: Option<BufWriter<NamedTempFile>>,
    progress: &'a RestoreProgress,
}

impl Write for DraftBuffer<'_> {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.progress.check_cancelled()?;
        if self.preview.is_none() && !self.budget.accepts(bytes) {
            let mut preview = BufWriter::with_capacity(256 * 1024, NamedTempFile::new()?);
            preview.write_all(&std::mem::take(&mut self.prefix))?;
            self.preview = Some(preview);
        }
        if let Some(preview) = &mut self.preview {
            preview.write_all(bytes)?;
        } else {
            self.prefix.extend_from_slice(bytes);
        }
        self.progress
            .done
            .fetch_add(bytes.len() as u64, Ordering::Relaxed);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        self.preview.as_mut().map_or(Ok(()), Write::flush)
    }
}

fn stage_draft(draft: &Draft, progress: &RestoreProgress) -> io::Result<StagedDraft> {
    let mut buffer = DraftBuffer {
        budget: EditorBudget::default(),
        prefix: Vec::new(),
        preview: None,
        progress,
    };
    progress.check_cancelled()?;
    draft.write_to(&mut buffer)?;
    progress.check_cancelled()?;
    match buffer.preview {
        Some(file) => Ok(StagedDraft::Preview(
            file.into_inner().map_err(io::IntoInnerError::into_error)?,
        )),
        None => String::from_utf8(buffer.prefix)
            .map(StagedDraft::Editable)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error)),
    }
}

enum PreparedDraft {
    Editable {
        text: String,
        baseline: Option<Rope>,
        status: DiskStatus,
    },
    Preview(Arc<NamedTempFile>, LogBookmark),
    File(OpenedFile),
    FileLog(FileTarget),
}

fn prepare_draft(
    tab: &Tab,
    draft: &Draft,
    progress: &RestoreProgress,
) -> io::Result<PreparedDraft> {
    match stage_draft(draft, progress)? {
        StagedDraft::Preview(mut file) => {
            let (observation, _) = log_file::observe_file(file.as_file_mut(), None)?;
            progress.check_cancelled()?;
            let mut bookmark = LogBookmark {
                offset: (tab.selection.start as u64).min(draft.byte_len()),
                auto_refresh: false,
                ..LogBookmark::default()
            };
            bookmark.record_observation(&observation);
            Ok(PreparedDraft::Preview(Arc::new(file), bookmark))
        }
        StagedDraft::Editable(text) => {
            let (baseline, status) = if let Some(file) = &tab.file {
                match files::open(&file.path, tab.format.encoding == TextEncoding::Windows1251) {
                    Ok(opened) if *file == opened.target => {
                        (Some(Rope::from(opened.text.as_str())), DiskStatus::Current)
                    }
                    Ok(_) => (None, DiskStatus::Changed),
                    Err(error) => (None, DiskStatus::from_error(&error)),
                }
            } else {
                (Some(Rope::new()), DiskStatus::Current)
            };
            progress.check_cancelled()?;
            Ok(PreparedDraft::Editable {
                text,
                baseline,
                status,
            })
        }
    }
}

fn prepare_tab(
    tab: &Tab,
    draft: Option<&Draft>,
    progress: &RestoreProgress,
) -> io::Result<PreparedDraft> {
    if let Some(draft) = draft {
        return prepare_draft(tab, draft, progress);
    }
    let file = tab
        .file
        .as_ref()
        .ok_or_else(|| io::Error::other("Не указан файл вкладки"))?;
    progress.check_cancelled()?;
    let mode = if tab.format.encoding == TextEncoding::Windows1251 {
        OpenMode::WINDOWS_1251
    } else {
        OpenMode::NORMAL
    };
    let opened = read_document(&file.path, mode)?;
    progress.check_cancelled()?;
    Ok(match opened {
        LoadedDocument::Text(file) => PreparedDraft::File(file),
        LoadedDocument::Log(file) => PreparedDraft::FileLog(file),
    })
}

pub(super) struct RestoreProgress {
    pub(super) cancelled: AtomicBool,
    pub(super) done: AtomicU64,
    pub(super) total: u64,
}

impl RestoreProgress {
    pub(super) fn new(total: u64) -> Self {
        Self {
            cancelled: AtomicBool::new(false),
            done: AtomicU64::new(0),
            total,
        }
    }

    pub(super) fn percent(&self) -> Option<u64> {
        (self.total > 0).then(|| self.done.load(Ordering::Relaxed).saturating_mul(100) / self.total)
    }

    fn check_cancelled(&self) -> io::Result<()> {
        if self.cancelled.load(Ordering::Relaxed) {
            return Err(io::Error::other("Восстановление отменено"));
        }
        Ok(())
    }
}

pub(super) enum Stage {
    Queued,
    Loading(Arc<RestoreProgress>),
    Failed(SharedString),
    Preview,
}

// The original snapshot stays here throughout loading, cancellation, errors
// and preview. None of these stages replaces the draft with the empty editor.
pub(super) struct Recovery {
    pub(super) tab: Tab,
    pub(super) draft: Option<Draft>,
    digest: Option<DraftDigest>,
    pub(super) stage: Stage,
}

impl Recovery {
    pub(super) fn snapshot(
        &self,
        include_draft: bool,
    ) -> (Tab, Option<Draft>, Option<DraftDigest>) {
        let mut tab = self.tab.clone();
        let draft = self.draft.as_ref().filter(|_| include_draft).cloned();
        let digest = draft.as_ref().and(self.digest);
        if draft.is_none() {
            tab.draft_revision = None;
        }
        (tab, draft, digest)
    }

    pub(super) fn cancel(&self) {
        if let Stage::Loading(progress) = &self.stage {
            progress.cancelled.store(true, Ordering::Relaxed);
        }
    }
}

impl Document {
    pub(super) fn begin_recovery(
        &mut self,
        mut tab: Tab,
        draft: Option<Draft>,
        digest: Option<DraftDigest>,
        name_limit: usize,
    ) {
        tab.id = self.id;
        self.placeholder = true;
        self.dirty = draft.is_some();
        if tab.file.is_none()
            && let Some(draft) = &draft
        {
            self.name = draft_name(draft, name_limit).into();
        }
        self.recovery = Some(Recovery {
            tab,
            draft,
            digest,
            stage: Stage::Queued,
        });
    }
}

impl Workspace {
    pub(super) fn cancel_tab_restore(
        &mut self,
        id: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.documents.iter().any(|doc| {
            doc.id == id
                && doc
                    .recovery
                    .as_ref()
                    .is_some_and(|recovery| !matches!(recovery.stage, Stage::Preview))
        }) {
            self.remove_tab(id, window, cx);
        }
    }

    pub(super) fn start_tab_restore(
        &mut self,
        id: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(recovery) = self
            .documents
            .iter_mut()
            .find(|doc| doc.id == id)
            .and_then(|doc| doc.recovery.as_mut())
        else {
            return;
        };
        if !matches!(recovery.stage, Stage::Queued | Stage::Failed(_)) {
            return;
        }
        let draft = recovery.draft.clone();
        let tab = recovery.tab.clone();
        let progress = Arc::new(RestoreProgress::new(
            draft.as_ref().map_or(0, Draft::byte_len),
        ));
        recovery.stage = Stage::Loading(progress.clone());

        let job_progress = progress.clone();
        let job =
            cx.background_spawn(async move { prepare_tab(&tab, draft.as_ref(), &job_progress) });
        let refresh = cx.background_executor().clone();
        let refresh_progress = progress.clone();
        cx.spawn_in(window, async move |this, cx| {
            loop {
                refresh.timer(Duration::from_millis(150)).await;
                let loading = this
                    .update_in(cx, |this, _, cx| {
                        let loading = this.documents.iter().any(|doc| {
                            doc.id == id
                                && doc
                                    .recovery
                                    .as_ref()
                                    .is_some_and(|recovery| recovery.is_loading(&refresh_progress))
                        });
                        if loading {
                            cx.notify();
                        }
                        loading
                    })
                    .unwrap_or(false);
                if !loading {
                    break;
                }
            }
        })
        .detach();
        cx.spawn_in(window, async move |this, cx| {
            let result = job.await;
            let _ = this.update_in(cx, |this, window, cx| {
                this.finish_tab_restore(id, &progress, result, window, cx);
            });
        })
        .detach();
        cx.notify();
    }

    fn finish_tab_restore(
        &mut self,
        id: u64,
        progress: &Arc<RestoreProgress>,
        result: io::Result<PreparedDraft>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(doc) = self.documents.iter_mut().find(|doc| doc.id == id) else {
            return;
        };
        let Some(recovery) = &mut doc.recovery else {
            return;
        };
        if !recovery.is_loading(progress) {
            return;
        }
        match result {
            Ok(PreparedDraft::Preview(file, bookmark)) => {
                // Keep the original draft in recovery for session writes and
                // export. The temporary file is only a disposable view.
                doc.log = Some(cx.new(|cx| LogViewer::new_draft(file, bookmark, window, cx)));
                recovery.stage = Stage::Preview;
            }
            Ok(PreparedDraft::Editable {
                text,
                baseline,
                status,
            }) => {
                let tab = recovery.tab.clone();
                doc.editor
                    .update(cx, |editor, cx| editor.set_value(text, window, cx));
                doc.saved_text = baseline;
                doc.dirty = true;
                doc.revision = tab.draft_revision.unwrap_or(0).wrapping_add(1);
                doc.disk_status = status;
                doc.placeholder = false;
                doc.restore_view(tab.selection, tab.reversed, tab.scroll, cx);
                doc.recovery = None;
                self.schedule_json_validation(id, cx);
            }
            Ok(PreparedDraft::File(opened)) => {
                let tab = recovery.tab.clone();
                doc.editor
                    .update(cx, |editor, cx| editor.set_value(opened.text, window, cx));
                doc.file = Some(opened.target);
                doc.format = opened.format;
                doc.saved_text = Some(doc.editor.read(cx).text().clone());
                doc.placeholder = false;
                doc.disk_status = DiskStatus::Current;
                doc.restore_view(tab.selection, tab.reversed, tab.scroll, cx);
                doc.recovery = None;
                self.schedule_json_validation(id, cx);
            }
            Ok(PreparedDraft::FileLog(file)) => {
                let path = file.path.clone();
                let windows_1251 = recovery.tab.format.encoding == TextEncoding::Windows1251;
                doc.log = Some(cx.new(|cx| {
                    LogViewer::new(path, windows_1251, LogBookmark::default(), window, cx)
                }));
                doc.file = Some(file);
                doc.placeholder = false;
                doc.disk_status = DiskStatus::Current;
                doc.recovery = None;
            }
            Err(error) => {
                recovery.stage =
                    Stage::Failed(format!("Не удалось восстановить вкладку. {error}").into());
            }
        }
        if self.active == id && !self.showing_home() && self.fallback_focus.is_focused(window) {
            self.focus_active_document(window, cx);
        }
        self.refresh_all_search_highlights(cx);
        cx.notify();
    }
}

impl Recovery {
    fn is_loading(&self, job: &Arc<RestoreProgress>) -> bool {
        matches!(&self.stage, Stage::Loading(progress) if Arc::ptr_eq(progress, job))
    }
}

#[cfg(test)]
#[path = "../../tests/unit/draft_recovery.rs"]
mod tests;
