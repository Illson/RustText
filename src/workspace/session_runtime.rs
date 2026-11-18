use std::{
    io,
    ops::Range,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

use gpui_kit::{component::input::Rope, *};

use super::{ClosedTab, Document, Workspace, disk_status::DiskStatus};
use crate::{
    files,
    session::{Capture, DraftDigest, Journal, Recovered, State, Tab, WindowSize},
    text_format::TextEncoding,
};

const SAVE_INTERVAL: Duration = Duration::from_secs(1);
const VIEW_SAVE_QUIET: Duration = Duration::from_secs(5);
const CLOSING_SESSION: &str = "Сохранение сессии…";

#[derive(Clone, Copy)]
pub(super) enum AfterShutdown {
    Close,
    InstallUpdate,
}

impl Document {
    pub(super) fn restore_view(
        &mut self,
        selection: Range<usize>,
        reversed: bool,
        scroll: [f64; 2],
        cx: &mut Context<Workspace>,
    ) {
        self.restored_scroll = Some(scroll);
        self.editor.update(cx, |editor, cx| {
            editor.set_selected_range_with_direction(selection, reversed, cx);
            editor.set_scroll_offset(point(px(scroll[0] as f32), px(scroll[1] as f32)), cx);
            editor.set_scroll_y_f64(scroll[1], cx);
        });
    }

    pub(super) fn session_snapshot(&self, include_draft: bool, cx: &App) -> (Tab, Option<Rope>) {
        let (tab, draft, _) = self.session_snapshot_with_digest(include_draft, cx);
        (tab, draft)
    }

    pub(super) fn session_snapshot_with_digest(
        &self,
        include_draft: bool,
        cx: &App,
    ) -> (Tab, Option<Rope>, Option<DraftDigest>) {
        let editor = self.editor.read(cx);
        let offset = editor.scroll_offset();
        let scroll = if editor.line_height().is_none() {
            self.restored_scroll.unwrap_or([0., 0.])
        } else {
            [f64::from(f32::from(offset.x)), editor.scroll_y_f64()]
        };
        let include_draft = include_draft && self.dirty;
        let tab = Tab {
            json_validation_disabled: self.json_validation_disabled,
            id: self.id,
            file: self.file.clone(),
            format: self.format,
            selection: editor.selected_range(),
            reversed: editor.is_selection_reversed(),
            scroll,
            draft_revision: include_draft.then_some(self.revision),
            log: self.log.as_ref().map(|log| log.read(cx).bookmark()),
        };
        if !include_draft {
            return (tab, None, None);
        }
        let text = editor.text();
        let digest = {
            let mut cache = self.draft_digest_cache.borrow_mut();
            if let Some((revision, digest)) = *cache
                && revision == self.revision
            {
                digest
            } else {
                let digest = DraftDigest::from_rope(text);
                *cache = Some((self.revision, digest));
                digest
            }
        };
        (tab, Some(text.clone()), Some(digest))
    }
}

pub(super) struct SessionWriter {
    journal: Arc<Mutex<Journal>>,
    last_saved: Option<State>,
    in_flight: bool,
    revision: u64,
    pending_view: Option<(State, Instant)>,
}

impl SessionWriter {
    #[cfg(test)]
    fn queue(&mut self, capture: Capture) -> Option<PendingWrite> {
        self.queue_prepared(capture, Vec::new(), Vec::new(), true, Instant::now())
    }

    fn queue_prepared(
        &mut self,
        capture: Capture,
        draft_digests: Vec<Option<DraftDigest>>,
        closed_draft_digests: Vec<Option<DraftDigest>>,
        immediate: bool,
        now: Instant,
    ) -> Option<PendingWrite> {
        if !immediate && self.in_flight {
            return None;
        }
        if !self.in_flight && self.last_saved.as_ref() == Some(&capture.state) {
            self.pending_view = None;
            return None;
        }
        let content_changed = self
            .last_saved
            .as_ref()
            .is_none_or(|saved| saved.has_content_or_structure_change(&capture.state));
        if !immediate && !content_changed {
            match &self.pending_view {
                Some((pending_state, since))
                    if *pending_state == capture.state
                        && now.duration_since(*since) >= VIEW_SAVE_QUIET =>
                {
                    self.pending_view = None;
                }
                Some((pending_state, _)) if *pending_state == capture.state => return None,
                _ => {
                    self.pending_view = Some((capture.state, now));
                    return None;
                }
            }
        } else {
            self.pending_view = None;
        }
        self.revision += 1;
        self.in_flight = true;
        Some(PendingWrite {
            journal: self.journal.clone(),
            revision: self.revision,
            capture,
            draft_digests,
            closed_draft_digests,
        })
    }
}

struct PendingWrite {
    journal: Arc<Mutex<Journal>>,
    revision: u64,
    capture: Capture,
    draft_digests: Vec<Option<DraftDigest>>,
    closed_draft_digests: Vec<Option<DraftDigest>>,
}

impl PendingWrite {
    fn commit(self) -> (u64, State, io::Result<()>) {
        let state = self.capture.state.clone();
        let result = self
            .journal
            .lock()
            .map_err(|_| io::Error::other("Журнал сессии недоступен"))
            .and_then(|mut journal| {
                journal.commit_prepared(
                    self.revision,
                    self.capture,
                    self.draft_digests,
                    self.closed_draft_digests,
                )
            });
        (self.revision, state, result)
    }
}

impl Workspace {
    pub(super) fn init_session(
        &mut self,
        startup: crate::session::Startup,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let repair_journal = startup.recovered.is_some() && startup.warning.is_some();
        self.session_notice = startup.warning.map(Into::into);
        if let Some(recovered) = startup.recovered {
            self.restore_session(recovered, window, cx);
        }
        if let Some(journal) = startup.journal {
            self.session = Some(SessionWriter {
                journal: Arc::new(Mutex::new(journal)),
                last_saved: (!repair_journal).then(|| self.capture_session(window, cx).0.state),
                revision: 0,
                in_flight: false,
                pending_view: None,
            });
            self.start_session_autosave(window, cx);
        }
        self.start_disk_monitor(window, cx);
    }

    fn restore_session(
        &mut self,
        recovered: Recovered,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Recovered {
            state,
            drafts,
            closed_drafts,
        } = recovered;
        self.wrap = state.wrap;
        self.font_size = state.font_size.clamp(9.0, 35.0);
        if let Some(id) = state
            .tabs
            .iter()
            .chain(&state.closed)
            .map(|tab| tab.id)
            .max()
        {
            self.next_id = id + 1;
        }
        self.closed = state
            .closed
            .into_iter()
            .zip(closed_drafts)
            .map(|(tab, draft)| {
                let draft = draft.map(|text| Rope::from(text.as_str()));
                let digest = draft.as_ref().map(DraftDigest::from_rope);
                ClosedTab { tab, draft, digest }
            })
            .collect();

        let mut active = None;
        let mut remap = std::collections::HashMap::new();
        for (index, (tab, draft)) in state.tabs.into_iter().zip(drafts).enumerate() {
            let saved_id = tab.id;
            let id = self.restore_tab(tab, draft, window, cx);
            remap.insert(saved_id, id);
            if index == state.active {
                active = Some(id);
            }
        }
        let ids: Vec<_> = self.documents.iter().map(|doc| doc.id).collect();
        self.layout = state.layout.map_or_else(
            || crate::layout::Layout::new(ids.clone(), active),
            |mut layout| {
                layout.remap(&remap);
                layout.normalize(&ids);
                layout
            },
        );
        active = self.layout.active();
        if let Some(active) = active {
            self.select(active, window, cx);
        } else {
            self.active = 0;
        }
    }

    pub(super) fn restore_tab(
        &mut self,
        tab: Tab,
        draft: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> u64 {
        let selection = tab.selection.clone();
        let json_validation_disabled = tab.json_validation_disabled;
        let reversed = tab.reversed;
        let scroll = tab.scroll;
        if let (Some(bookmark), Some(file)) = (tab.log.clone(), tab.file.clone()) {
            return self.insert_log(
                file,
                matches!(tab.format.encoding, TextEncoding::Windows1251),
                bookmark,
                window,
                cx,
            );
        }
        let dirty = draft.is_some();
        let fresh = tab.file.as_ref().map(|file| {
            files::open(
                &file.path,
                matches!(tab.format.encoding, TextEncoding::Windows1251),
            )
        });
        let mut file = tab.file;
        let mut format = tab.format;
        let mut placeholder = false;
        let mut status = DiskStatus::Current;
        let mut baseline = Some(Rope::new());
        let text = match (draft, fresh) {
            (Some(draft), Some(Ok(opened))) => {
                if file.as_ref() == Some(&opened.target) {
                    baseline = Some(Rope::from(opened.text.as_str()));
                } else {
                    status = DiskStatus::Changed;
                    baseline = None;
                }
                draft
            }
            (Some(draft), Some(Err(error))) => {
                status = DiskStatus::from_error(&error);
                baseline = None;
                draft
            }
            (Some(draft), None) => draft,
            (None, Some(Ok(opened))) => {
                file = Some(opened.target);
                format = opened.format;
                opened.text
            }
            (None, Some(Err(error))) => {
                status = DiskStatus::from_error(&error);
                placeholder = true;
                String::new()
            }
            (None, None) => String::new(),
        };

        let id = self.insert_document(file, text, format, window, cx);
        let doc = self.documents.last_mut().expect("restored tab exists");
        doc.json_validation_disabled = json_validation_disabled;
        doc.json_validation_generation = doc.json_validation_generation.wrapping_add(1);
        doc.disk_status = status;
        doc.placeholder = placeholder;
        if placeholder {
            doc.json_status = None;
            doc.revision = doc.revision.wrapping_add(1);
        }
        if dirty {
            doc.saved_text = baseline;
            doc.dirty = true;
        }

        doc.restore_view(selection, reversed, scroll, cx);
        self.schedule_json_validation(id, cx);
        id
    }

    fn start_session_autosave(&self, window: &mut Window, cx: &mut Context<Self>) {
        let executor = cx.background_executor().clone();
        cx.spawn_in(window, async move |this, cx| {
            let mut next_write = Instant::now() + SAVE_INTERVAL;
            loop {
                executor
                    .timer(next_write.saturating_duration_since(Instant::now()))
                    .await;
                next_write = Instant::now() + SAVE_INTERVAL;
                let Ok(pending) = this.update_in(cx, |this, window, cx| {
                    if this.busy == Some(CLOSING_SESSION) {
                        return None;
                    }
                    this.prepare_session_write(false, window, cx)
                }) else {
                    break;
                };
                if let Some(pending) = pending {
                    let (revision, state, result) =
                        executor.spawn(async move { pending.commit() }).await;
                    let _ = this.update_in(cx, |this, _, cx| {
                        this.session_write_finished(revision, state, result, cx);
                    });
                }
            }
        })
        .detach();
    }

    pub(super) fn begin_session_shutdown(
        &mut self,
        after: AfterShutdown,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> bool {
        let Some(pending) = self.prepare_session_write(true, window, cx) else {
            return match after {
                AfterShutdown::Close => true,
                AfterShutdown::InstallUpdate => {
                    self.finish_update_shutdown(window, cx);
                    false
                }
            };
        };
        self.busy = Some(CLOSING_SESSION);
        cx.notify();
        let job = cx.background_spawn(async move { pending.commit() });
        cx.spawn_in(window, async move |this, cx| {
            let (revision, state, result) = job.await;
            let _ = this.update_in(cx, |this, window, cx| {
                let saved = result.is_ok();
                this.session_write_finished(revision, state, result, cx);
                this.busy = None;
                if saved {
                    if this.begin_session_shutdown(after, window, cx) {
                        window.remove_window();
                    }
                } else {
                    match after {
                        AfterShutdown::Close => this.confirm_close_after_session_error(window, cx),
                        AfterShutdown::InstallUpdate => this.defer_restart_after_session_error(cx),
                    }
                }
            });
        })
        .detach();
        false
    }

    fn confirm_close_after_session_error(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let response = window.prompt(
            PromptLevel::Warning,
            "Сессия не сохранена. Закрыть окно?",
            self.session_error.as_deref(),
            &[
                PromptButton::cancel("Остаться"),
                PromptButton::new("Закрыть"),
            ],
            cx,
        );
        self.busy = Some("Подтверждение закрытия…");
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let close = response.await.ok() == Some(1);
            let _ = this.update_in(cx, |this, window, cx| {
                this.busy = None;
                if close {
                    window.remove_window();
                }
                cx.notify();
            });
        })
        .detach();
    }

    fn prepare_session_write(
        &mut self,
        immediate: bool,
        window: &Window,
        cx: &App,
    ) -> Option<PendingWrite> {
        self.session.as_ref()?;
        let (capture, draft_digests, closed_draft_digests) = self.capture_session(window, cx);
        self.session.as_mut()?.queue_prepared(
            capture,
            draft_digests,
            closed_draft_digests,
            immediate,
            Instant::now(),
        )
    }

    fn session_write_finished(
        &mut self,
        revision: u64,
        state: State,
        result: io::Result<()>,
        cx: &mut Context<Self>,
    ) {
        if let Some(writer) = &mut self.session
            && writer.revision == revision
        {
            writer.in_flight = false;
            match result {
                Ok(()) => {
                    writer.last_saved = Some(state);
                    if self.session_error.take().is_some() {
                        cx.notify();
                    }
                }
                Err(error) => {
                    self.session_error =
                        Some(format!("Не удалось сохранить сессию. {error}").into());
                    cx.notify();
                }
            }
        }
    }

    fn capture_session(
        &self,
        window: &Window,
        cx: &App,
    ) -> (Capture, Vec<Option<DraftDigest>>, Vec<Option<DraftDigest>>) {
        let bounds = window.window_bounds();
        let size = bounds.get_bounds().size;
        let mut tabs = Vec::with_capacity(self.documents.len());
        let mut drafts = Vec::with_capacity(self.documents.len());
        let mut draft_digests = Vec::with_capacity(self.documents.len());
        for doc in &self.documents {
            let (tab, draft, digest) = doc.session_snapshot_with_digest(true, cx);
            tabs.push(tab);
            drafts.push(draft);
            draft_digests.push(digest);
        }
        let mut closed = Vec::with_capacity(self.closed.len());
        let mut closed_drafts = Vec::with_capacity(self.closed.len());
        let mut closed_draft_digests = Vec::with_capacity(self.closed.len());
        for entry in &self.closed {
            closed.push(entry.tab.clone());
            closed_drafts.push(entry.draft.clone());
            closed_draft_digests.push(entry.digest);
        }
        (
            Capture {
                state: State {
                    layout: Some(self.layout.clone()),
                    tabs,
                    closed,
                    active: self
                        .documents
                        .iter()
                        .position(|doc| doc.id == self.active)
                        .unwrap_or(0),
                    window: WindowSize {
                        width: size.width.into(),
                        height: size.height.into(),
                        maximized: matches!(bounds, WindowBounds::Maximized(_)),
                    },
                    wrap: self.wrap,
                    font_size: self.font_size,
                },
                drafts,
                closed_drafts,
            },
            draft_digests,
            closed_draft_digests,
        )
    }
}

#[cfg(test)]
#[path = "../../tests/unit/session_runtime.rs"]
mod tests;
