mod associations;
#[cfg(windows)]
mod desktop_icon;
mod disk_status;
mod file_actions;
mod home;
mod json_action;
mod json_validation;
mod launches;
mod list_comparison;
mod menus;
mod reveal;
mod search_panel;
mod session_runtime;
mod settings_panel;
mod template_generator;
mod updates;
mod version_actions;
mod versions_panel;
mod view;
#[cfg(windows)]
mod window_finder;

pub(crate) use associations::register_for_current_user;
pub(crate) use view::bind_keys;

use std::{
    path::{Path, PathBuf},
    rc::Rc,
    sync::Arc,
};

use gpui_kit::base::{Scrollbar, ScrollbarMode};
use gpui_kit::component::input::{EditorState, InputEvent, Rope};
use gpui_kit::*;

use crate::{
    files::{FileTarget, OpenedFile},
    log_file::LogBookmark,
    log_viewer::LogViewer,
    recent_files::{RecentFile, RecentFiles},
    session::{MAX_CLOSED_TABS, Tab},
    settings::{Settings, untitled_tab_name_rope},
    single_instance::SingleInstance,
    text_format::{TextEncoding, TextFormat},
};

struct Document {
    id: u64,
    name: SharedString,
    file: Option<FileTarget>,
    format: TextFormat,
    editor: Entity<EditorState>,
    search_viewport: crate::search_highlight::ViewportTracker,
    minimap: crate::minimap::MinimapState,
    saved_text: Option<Rope>,
    dirty: bool,
    revision: u64,
    draft_digest_cache: std::cell::RefCell<Option<(u64, crate::session::DraftDigest)>>,
    disk_status: disk_status::DiskStatus,
    placeholder: bool,
    json_status: Option<json_validation::Status>,
    json_validation_disabled: bool,
    json_validation_generation: u64,
    restored_scroll: Option<[f64; 2]>,
    log: Option<Entity<LogViewer>>,
    _subscriptions: [Subscription; 2],
}

struct ClosedTab {
    tab: Tab,
    draft: Option<Rope>,
    digest: Option<crate::session::DraftDigest>,
}

pub(crate) struct Workspace {
    documents: Vec<Document>,
    layout: crate::layout::Layout,
    fallback_focus: FocusHandle,
    home: home::Home,
    version_store: Option<Arc<crate::file_versions::VersionStore>>,
    versions: Option<versions_panel::VersionsPanel>,
    active: u64,
    next_id: u64,
    tab_scroll: ScrollHandle,
    closed: Vec<ClosedTab>,
    recent: RecentFiles,
    settings: Settings,
    settings_panel: Option<settings_panel::SettingsPanel>,
    settings_pending: Option<Settings>,
    settings_writing: bool,
    busy: Option<&'static str>,
    error: Option<SharedString>,
    wrap: bool,
    font_size: f32,
    search: Option<search_panel::SearchPanel>,
    template_generator: Option<template_generator::TemplateGenerator>,
    list_comparison: Option<list_comparison::ListComparison>,
    #[cfg(windows)]
    window_finder: Option<window_finder::WindowFinder>,
    session: Option<session_runtime::SessionWriter>,
    session_notice: Option<SharedString>,
    session_error: Option<SharedString>,
    updates: updates::Updates,
}

impl Workspace {
    pub(crate) fn new(
        paths: Vec<PathBuf>,
        startup: crate::session::Startup,
        instance: SingleInstance,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let (recent, recent_error) = RecentFiles::load();
        let (settings, settings_error) = Settings::load();
        let (version_store, version_error) = match crate::file_versions::VersionStore::load() {
            Ok(store) => (Some(Arc::new(store)), None),
            Err(error) => (
                None,
                Some(format!("История версий недоступна. {error}").into()),
            ),
        };
        let mut this = Self {
            documents: Vec::new(),
            layout: crate::layout::Layout::default(),
            fallback_focus: cx.focus_handle(),
            home: home::Home::new(window, cx),
            version_store,
            versions: None,
            active: 0,
            next_id: 1,
            tab_scroll: ScrollHandle::new(),
            closed: Vec::new(),
            recent,
            settings,
            settings_panel: None,
            settings_pending: None,
            settings_writing: false,
            busy: None,
            error: recent_error
                .map(recent_warning)
                .or_else(|| settings_error.map(settings_warning))
                .or(version_error),
            wrap: false,
            font_size: 15.0,
            search: None,
            template_generator: None,
            list_comparison: None,
            #[cfg(windows)]
            window_finder: None,
            session: None,
            session_notice: None,
            session_error: None,
            updates: updates::Updates::default(),
        };
        this.init_session(startup, window, cx);
        this.listen_for_launches(instance, window, cx);
        let workspace = cx.entity().downgrade();
        window.on_window_should_close(cx, move |window, cx| {
            workspace
                .update(cx, |this, cx| this.request_window_close(window, cx))
                .unwrap_or(true)
        });

        if !paths.is_empty() {
            this.open_paths(paths, file_actions::OpenMode::NORMAL, window, cx);
        }
        this.check_for_updates(window, cx);
        if this.active_document_opt().is_none() {
            this.fallback_focus.focus(window, cx);
        }
        this
    }

    fn active_document(&self) -> &Document {
        self.active_document_opt().expect("active tab must exist")
    }

    fn active_document_opt(&self) -> Option<&Document> {
        self.documents.iter().find(|doc| doc.id == self.active)
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
        let language = file
            .as_ref()
            .map_or_else(|| "text".to_owned(), |file| syntax_for(&file.path));
        let json_enabled = file
            .as_ref()
            .is_some_and(|file| json_validation::is_json_file(&file.path));

        let editor = cx.new(|cx| {
            let mut state = EditorState::new(window, cx)
                .line_number(true)
                .searchable(false)
                .soft_wrap(self.wrap)
                .language(language)
                .default_value(text);
            state.set_scrollbar_renderer(Some(Rc::new(|scroll, _, _| {
                Scrollbar::new(&scroll)
                    .axis(scroll.axis())
                    .mode(ScrollbarMode::Always)
                    .styles(|_| crate::appearance::scrollbar_styles())
                    .into_any_element()
            })));
            state
        });
        let saved_text = editor.read(cx).text().clone();
        let name = file.as_ref().map_or_else(
            || untitled_tab_name_rope(&saved_text, self.settings.untitled_tab_limit).into(),
            |file| file_name(&file.path),
        );
        let (search_viewport, minimap) = crate::minimap::install(&editor, cx);
        let change =
            cx.subscribe_in(
                &editor,
                window,
                move |this, editor, event, window, cx| match event {
                    InputEvent::Change => {
                        let untitled_limit = this.settings.untitled_tab_limit;
                        if let Some(doc) = this.documents.iter_mut().find(|doc| doc.id == id) {
                            let text = editor.read(cx).text();
                            doc.dirty = doc.saved_text.as_ref() != Some(text);
                            if doc.file.is_none() {
                                doc.name = untitled_tab_name_rope(text, untitled_limit).into();
                            }
                            doc.revision = doc.revision.wrapping_add(1);
                            cx.notify();
                            this.schedule_json_validation(id, cx);
                        }
                    }
                    InputEvent::Focus if this.active != id || this.layout.active() != Some(id) => {
                        this.select(id, window, cx);
                    }
                    _ => {}
                },
            );
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
            search_viewport,
            minimap,
            saved_text: Some(saved_text),
            dirty: false,
            revision: 0,
            draft_digest_cache: std::cell::RefCell::new(None),
            disk_status: disk_status::DiskStatus::Current,
            placeholder: false,
            json_status: json_enabled.then_some(json_validation::Status::Checking),
            json_validation_disabled: false,
            json_validation_generation: 0,
            restored_scroll: None,
            log: None,
            _subscriptions: [change, cursor],
        });
        self.layout.attach(id);
        self.schedule_json_validation(id, cx);
        id
    }

    fn select(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(index) = self.documents.iter().position(|doc| doc.id == id) {
            self.home.open = false;
            if self.active != id {
                self.remember_position(self.active, window, cx);
            }
            self.layout.select(id);
            if self.active != id {
                self.versions = None;
                self.error = None;
                self.session_notice = None;
                self.active = id;
            }
            self.tab_scroll.scroll_to_item(index);
            let log = self.documents[index].log.clone();
            let focus = if let Some(log) = log {
                self.clear_search(cx);
                log.read(cx).focus_handle(cx)
            } else {
                self.documents[index].editor.read(cx).focus_handle(cx)
            };
            window.focus(&focus, cx);
            self.refresh_all_search_highlights(cx);
            cx.notify();
        }
    }

    pub(super) fn showing_home(&self) -> bool {
        self.documents.is_empty() || self.home.open
    }

    pub(super) fn focus_active_document(&self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(doc) = self.active_document_opt() {
            let focus = doc.log.as_ref().map_or_else(
                || doc.editor.read(cx).focus_handle(cx),
                |log| log.read(cx).focus_handle(cx),
            );
            window.focus(&focus, cx);
        } else {
            self.fallback_focus.focus(window, cx);
        }
    }

    pub(super) fn restore_workspace_focus(&self, window: &mut Window, cx: &mut Context<Self>) {
        if self.showing_home() {
            self.focus_home_query(window, cx);
        } else {
            self.focus_active_document(window, cx);
        }
    }

    pub(super) fn refresh_untitled_tab_names(&mut self, cx: &App) {
        let limit = self.settings.untitled_tab_limit;
        for doc in &mut self.documents {
            if doc.file.is_none() {
                doc.name = untitled_tab_name_rope(doc.editor.read(cx).text(), limit).into();
            }
        }
    }
    fn insert_log(
        &mut self,
        target: FileTarget,
        windows_1251: bool,
        bookmark: LogBookmark,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> u64 {
        let path = target.path.clone();
        let format = TextFormat::for_encoding(if windows_1251 {
            TextEncoding::Windows1251
        } else {
            TextEncoding::Utf8
        });
        let id = self.insert_document(Some(target), String::new(), format, window, cx);
        let viewer = cx.new(|cx| LogViewer::new(path, windows_1251, bookmark, window, cx));
        self.documents.last_mut().expect("log tab inserted").log = Some(viewer);
        id
    }

    fn log_is_writing(&self, cx: &App) -> bool {
        self.documents.iter().any(|doc| {
            doc.log
                .as_ref()
                .is_some_and(|log| log.read(cx).writing_file())
        })
    }

    fn new_file(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() || self.applying_update() {
            return;
        }
        self.home.open = false;
        self.error = None;
        self.session_notice = None;
        self.add_document(None, window, cx);
    }

    pub(super) fn focus_empty_group(
        &mut self,
        group_id: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !self.layout.focus(group_id) {
            return;
        }
        self.home.open = false;
        if self.active != 0 {
            self.remember_position(self.active, window, cx);
        }
        self.active = 0;
        self.versions = None;
        self.fallback_focus.focus(window, cx);
        self.refresh_all_search_highlights(cx);
        cx.notify();
    }

    pub(super) fn close_empty_group(
        &mut self,
        group_id: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.busy.is_some() || self.applying_update() || !self.layout.close_empty_group(group_id)
        {
            return;
        }
        if let Some(next) = self.layout.active() {
            self.select(next, window, cx);
        } else {
            self.focus_empty_group(self.layout.focused, window, cx);
        }
    }

    fn close_tab(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() {
            return;
        }
        let Some(doc) = self.documents.iter().find(|doc| doc.id == id) else {
            return;
        };
        if doc
            .log
            .as_ref()
            .is_some_and(|log| log.read(cx).writing_file())
        {
            return;
        }
        if !doc.dirty || (doc.file.is_none() && self.session.is_some()) {
            self.remove_tab(id, window, cx);
            return;
        }

        self.confirm_discard(Some(id), window, cx);
    }

    fn remove_tab(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        let Some(index) = self.documents.iter().position(|doc| doc.id == id) else {
            return;
        };
        self.remember_activity(id, window, cx);
        let doc = self.documents.remove(index);
        self.layout.remove(id);
        let (tab, draft, digest) = doc.session_snapshot_with_digest(doc.file.is_none(), cx);
        if self.closed.len() == MAX_CLOSED_TABS {
            self.closed.remove(0);
        }
        self.closed.push(ClosedTab { tab, draft, digest });

        self.clear_search(cx);
        if let Some(next) = self.layout.active() {
            self.select(next, window, cx);
        } else {
            self.active = 0;
            self.versions = None;
            self.fallback_focus.focus(window, cx);
            cx.notify();
        }
    }

    fn reopen(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(entry) = self.closed.last() {
            self.reopen_closed_tab(entry.tab.id, window, cx);
        }
    }

    fn closed_tab_entries(&self) -> Vec<(u64, String)> {
        self.closed
            .iter()
            .rev()
            .map(|entry| {
                let label = if let Some(file) = &entry.tab.file {
                    display_path(&file.path)
                } else {
                    entry.draft.as_ref().map_or_else(
                        || "Без имени".to_owned(),
                        |draft| untitled_tab_name_rope(draft, self.settings.untitled_tab_limit),
                    )
                };
                (entry.tab.id, label)
            })
            .collect()
    }

    fn reopen_closed_tab(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() {
            return;
        }
        let Some(index) = self.closed.iter().position(|entry| entry.tab.id == id) else {
            return;
        };
        let tab = &self.closed[index].tab;
        if let Some(file) = &tab.file {
            let mut recent = RecentFile::new(file.path.clone(), tab.format.encoding);
            recent.view_as_log = tab.log.is_some();
            let tab = tab.clone();
            self.open_closed_file(recent, tab, window, cx);
        } else {
            let ClosedTab { tab, draft, .. } = self.closed.remove(index);
            let id = self.restore_tab(tab, draft.map(|draft| draft.to_string()), window, cx);
            self.select(id, window, cx);
        }
    }

    fn request_window_close(&mut self, window: &mut Window, cx: &mut Context<Self>) -> bool {
        if self.busy.is_some() || self.log_is_writing(cx) {
            return false;
        }
        if !self.home.close_recorded {
            self.home.close_recorded = true;
            let active = self.active;
            for id in self.documents.iter().map(|doc| doc.id).collect::<Vec<_>>() {
                if id == active {
                    self.remember_activity(id, window, cx);
                } else {
                    self.remember_position(id, window, cx);
                }
            }
        }
        if self.history_is_writing() {
            self.home.close_pending = true;
            return false;
        }
        self.home.close_recorded = false;
        if self.session.is_some() {
            return self.begin_session_shutdown(session_runtime::AfterShutdown::Close, window, cx);
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

    fn toggle_wrap(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(viewer) = self.active_document_opt().and_then(|doc| doc.log.clone()) {
            viewer.update(cx, |viewer, cx| viewer.toggle_wrap(window, cx));
            cx.notify();
            return;
        }
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

fn display_path(path: &Path) -> String {
    let display = path.to_string_lossy();
    if let Some(unc) = display.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{unc}")
    } else {
        display.strip_prefix(r"\\?\").unwrap_or(&display).to_owned()
    }
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

fn settings_warning(error: std::io::Error) -> SharedString {
    format!("Настройки недоступны. {error}").into()
}
