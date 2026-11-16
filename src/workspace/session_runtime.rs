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
    session::{Capture, Journal, Recovered, State, Tab, WindowSize},
    text_format::TextEncoding,
};

const SAVE_INTERVAL: Duration = Duration::from_secs(1);
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
        scroll: [f32; 2],
        cx: &mut Context<Workspace>,
    ) {
        self.restored_scroll = Some(scroll);
        self.editor.update(cx, |editor, cx| {
            editor.set_selected_range_with_direction(selection, reversed, cx);
            editor.set_scroll_offset(point(px(scroll[0]), px(scroll[1])), cx);
        });
    }

    pub(super) fn session_snapshot(&self, include_draft: bool, cx: &App) -> (Tab, Option<Rope>) {
        let editor = self.editor.read(cx);
        let offset = editor.scroll_offset();
        let scroll = if editor.line_height().is_none() {
            self.restored_scroll.unwrap_or([0., 0.])
        } else {
            [offset.x.into(), offset.y.into()]
        };
        let include_draft = include_draft && self.dirty;
        (
            Tab {
                id: self.id,
                file: self.file.clone(),
                format: self.format,
                selection: editor.selected_range(),
                reversed: editor.is_selection_reversed(),
                scroll,
                draft_revision: include_draft.then_some(self.revision),
                log: self.log.as_ref().map(|log| log.read(cx).bookmark()),
            },
            include_draft.then(|| editor.text().clone()),
        )
    }
}

pub(super) struct SessionWriter {
    journal: Arc<Mutex<Journal>>,
    last_saved: Option<State>,
    in_flight: bool,
    revision: u64,
}

impl SessionWriter {
    fn queue(&mut self, capture: Capture) -> Option<PendingWrite> {
        if !self.in_flight && self.last_saved.as_ref() == Some(&capture.state) {
            return None;
        }
        self.revision += 1;
        self.in_flight = true;
        Some(PendingWrite {
            journal: self.journal.clone(),
            revision: self.revision,
            capture,
        })
    }
}

struct PendingWrite {
    journal: Arc<Mutex<Journal>>,
    revision: u64,
    capture: Capture,
}

impl PendingWrite {
    fn commit(self) -> (u64, State, io::Result<()>) {
        let state = self.capture.state.clone();
        let result = self
            .journal
            .lock()
            .map_err(|_| io::Error::other("Журнал сессии недоступен"))
            .and_then(|mut journal| journal.commit(self.revision, self.capture));
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
        } else {
            self.add_document(None, window, cx);
        }
        if let Some(journal) = startup.journal {
            self.session = Some(SessionWriter {
                journal: Arc::new(Mutex::new(journal)),
                last_saved: (!repair_journal).then(|| self.capture_session(window, cx).state),
                revision: 0,
                in_flight: false,
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
            .map(|(tab, draft)| ClosedTab {
                tab,
                draft: draft.map(|text| Rope::from(text.as_str())),
            })
            .collect();

        let mut active = None;
        for (index, (tab, draft)) in state.tabs.into_iter().zip(drafts).enumerate() {
            let id = self.restore_tab(tab, draft, window, cx);
            if index == state.active {
                active = Some(id);
            }
        }
        if let Some(active) = active {
            self.select(active, window, cx);
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
                    this.prepare_session_write(window, cx)
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
        let Some(pending) = self.prepare_session_write(window, cx) else {
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

    fn prepare_session_write(&mut self, window: &Window, cx: &App) -> Option<PendingWrite> {
        self.session.as_ref()?;
        let capture = self.capture_session(window, cx);
        self.session.as_mut()?.queue(capture)
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

    fn capture_session(&self, window: &Window, cx: &App) -> Capture {
        let bounds = window.window_bounds();
        let size = bounds.get_bounds().size;
        let (tabs, drafts) = self
            .documents
            .iter()
            .map(|doc| doc.session_snapshot(true, cx))
            .unzip();
        let (closed, closed_drafts) = self
            .closed
            .iter()
            .map(|entry| (entry.tab.clone(), entry.draft.clone()))
            .unzip();
        Capture {
            state: State {
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
        }
    }
}

#[cfg(test)]
#[path = "../../tests/unit/session_runtime.rs"]
mod tests;
