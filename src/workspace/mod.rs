mod disk_status;
mod file_actions;
mod json_action;
mod search_panel;
mod session_runtime;
mod view;

pub(crate) use view::bind_keys;

use std::path::{Path, PathBuf};

use gpui_kit::component::input::{EditorState, InputEvent, Rope};
use gpui_kit::*;

use crate::{
    files::{FileTarget, OpenedFile},
    recent_files::RecentFiles,
    text_format::{TextEncoding, TextFormat},
};

struct Document {
    id: u64,
    name: SharedString,
    file: Option<FileTarget>,
    format: TextFormat,
    editor: Entity<EditorState>,
    saved_text: Option<Rope>,
    dirty: bool,
    revision: u64,
    disk_status: disk_status::DiskStatus,
    placeholder: bool,
    restored_scroll: Option<[f32; 2]>,
    _subscriptions: [Subscription; 2],
}

pub(crate) struct Workspace {
    documents: Vec<Document>,
    active: u64,
    next_id: u64,
    closed: Vec<(PathBuf, bool)>,
    recent: RecentFiles,
    busy: Option<&'static str>,
    error: Option<SharedString>,
    wrap: bool,
    font_size: f32,
    search: Option<search_panel::SearchPanel>,
    session: Option<session_runtime::SessionWriter>,
    session_notice: Option<SharedString>,
    session_error: Option<SharedString>,
}

impl Workspace {
    pub(crate) fn new(
        paths: Vec<PathBuf>,
        startup: crate::session::Startup,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let (recent, recent_error) = RecentFiles::load();
        let mut this = Self {
            documents: Vec::new(),
            active: 0,
            next_id: 1,
            closed: Vec::new(),
            recent,
            busy: None,
            error: recent_error.map(recent_warning),
            wrap: false,
            font_size: 15.0,
            search: None,
            session: None,
            session_notice: None,
            session_error: None,
        };
        this.init_session(startup, window, cx);
        let workspace = cx.entity().downgrade();
        window.on_window_should_close(cx, move |window, cx| {
            workspace
                .update(cx, |this, cx| this.request_window_close(window, cx))
                .unwrap_or(true)
        });

        if !paths.is_empty() {
            this.open_paths(paths, false, window, cx);
        }
        this
    }

    fn active_document(&self) -> &Document {
        self.documents
            .iter()
            .find(|doc| doc.id == self.active)
            .expect("active tab must exist")
    }

    fn add_document(
        &mut self,
        opened: Option<OpenedFile>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (file, text, format) = match opened {
            Some(opened) => (Some(opened.target), opened.text, opened.format),
            None => (None, String::new(), TextFormat::default()),
        };
        let id = self.insert_document(file, text, format, window, cx);
        self.select(id, window, cx);
    }

    fn insert_document(
        &mut self,
        file: Option<FileTarget>,
        text: String,
        format: TextFormat,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> u64 {
        let id = self.next_id;
        self.next_id += 1;
        let name = file
            .as_ref()
            .map_or_else(|| "Без имени".into(), |file| file_name(&file.path));
        let language = file
            .as_ref()
            .map_or_else(|| "text".to_owned(), |file| syntax_for(&file.path));

        let editor = cx.new(|cx| {
            EditorState::new(window, cx)
                .line_number(true)
                .searchable(false)
                .soft_wrap(self.wrap)
                .language(language)
                .default_value(text)
        });
        let saved_text = editor.read(cx).text().clone();
        let change = cx.subscribe(&editor, move |this, editor, event, cx| {
            if matches!(event, InputEvent::Change)
                && let Some(doc) = this.documents.iter_mut().find(|doc| doc.id == id)
            {
                doc.dirty = doc.saved_text.as_ref() != Some(editor.read(cx).text());
                doc.revision = doc.revision.wrapping_add(1);
                cx.notify();
            }
        });
        let cursor = cx.observe(&editor, move |this, _, cx| {
            if this.active == id {
                cx.notify();
            }
        });

        self.documents.push(Document {
            id,
            name,
            file,
            format,
            editor,
            saved_text: Some(saved_text),
            dirty: false,
            revision: 0,
            disk_status: disk_status::DiskStatus::Current,
            placeholder: false,
            restored_scroll: None,
            _subscriptions: [change, cursor],
        });
        id
    }

    fn select(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(doc) = self.documents.iter().find(|doc| doc.id == id) {
            if self.active != id {
                self.error = None;
                self.active = id;
            }
            let focus = doc.editor.read(cx).focus_handle(cx);
            window.focus(&focus, cx);
            cx.notify();
        }
    }

    fn new_file(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.error = None;
        self.add_document(None, window, cx);
    }

    fn close_tab(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() {
            return;
        }
        let Some(doc) = self.documents.iter().find(|doc| doc.id == id) else {
            return;
        };
        if !doc.dirty {
            self.remove_tab(id, window, cx);
            return;
        }

        self.confirm_discard(Some(id), window, cx);
    }

    fn remove_tab(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        let Some(index) = self.documents.iter().position(|doc| doc.id == id) else {
            return;
        };
        let doc = self.documents.remove(index);
        if let Some(file) = doc.file {
            if self.closed.len() == 16 {
                self.closed.remove(0);
            }
            self.closed.push((
                file.path,
                matches!(doc.format.encoding, TextEncoding::Windows1251),
            ));
        }

        if self.documents.is_empty() {
            self.add_document(None, window, cx);
        } else if id == self.active {
            let next = self.documents[index.min(self.documents.len() - 1)].id;
            self.select(next, window, cx);
        } else {
            cx.notify();
        }
    }

    fn reopen(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_none()
            && let Some((path, windows_1251)) = self.closed.pop()
        {
            self.open_paths(vec![path], windows_1251, window, cx);
        }
    }

    fn request_window_close(&mut self, window: &mut Window, cx: &mut Context<Self>) -> bool {
        if self.busy.is_some() {
            return false;
        }
        if self.session.is_some() {
            return self.begin_session_shutdown(window, cx);
        }
        if self.documents.iter().any(|doc| doc.dirty) {
            self.confirm_discard(None, window, cx);
            return false;
        }
        true
    }

    fn confirm_discard(&mut self, tab: Option<u64>, window: &mut Window, cx: &mut Context<Self>) {
        let detail = tab
            .and_then(|id| self.documents.iter().find(|doc| doc.id == id))
            .map_or_else(
                || "В открытых вкладках есть несохранённый текст.".to_owned(),
                |doc| format!("Вкладка «{}» содержит несохранённый текст.", doc.name),
            );
        let answer = window.prompt(
            PromptLevel::Warning,
            "Закрыть без сохранения?",
            Some(&detail),
            &[PromptButton::cancel("Отмена"), PromptButton::new("Закрыть")],
            cx,
        );

        self.busy = Some("Подтверждение закрытия…");
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let discard = answer.await.ok() == Some(1);
            this.update_in(cx, |this, window, cx| {
                this.busy = None;
                if discard {
                    if let Some(id) = tab {
                        this.remove_tab(id, window, cx);
                    } else {
                        window.remove_window();
                    }
                }
                cx.notify();
            })
            .ok();
        })
        .detach();
    }

    fn reorder_tabs(&mut self, dragged_id: u64, target_id: u64, cx: &mut Context<Self>) {
        let from = self.documents.iter().position(|doc| doc.id == dragged_id);
        let to = self.documents.iter().position(|doc| doc.id == target_id);
        if let (Some(from), Some(to)) = (from, to)
            && from != to
        {
            let tab = self.documents.remove(from);
            self.documents.insert(to, tab);
            cx.notify();
        }
    }

    fn toggle_wrap(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.wrap = !self.wrap;
        for doc in &self.documents {
            doc.editor
                .update(cx, |editor, cx| editor.set_soft_wrap(self.wrap, window, cx));
        }
        cx.notify();
    }

    fn zoom(&mut self, amount: f32, cx: &mut Context<Self>) {
        self.font_size = (self.font_size + amount).clamp(9.0, 35.0);
        cx.notify();
    }
}

fn file_name(path: &Path) -> SharedString {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| path.display().to_string())
        .into()
}

fn syntax_for(path: &Path) -> String {
    path.extension()
        .and_then(|extension| extension.to_str())
        .unwrap_or("text")
        .to_ascii_lowercase()
}

fn recent_warning(error: std::io::Error) -> SharedString {
    format!("Список недавних файлов недоступен. {error}").into()
}
