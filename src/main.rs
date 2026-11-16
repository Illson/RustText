#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod files;

use std::path::{Path, PathBuf};

use gpui_kit::component::{
    ActiveTheme, Sizable, Theme, ThemeMode,
    button::{Button, ButtonVariants},
    input::{Editor, EditorState, InputEvent, Rope},
    label::Label,
    set_locale,
};
use gpui_kit::component::{Disableable, Selectable};
use gpui_kit::prelude::FluentBuilder;
use gpui_kit::*;

use crate::files::{FileTarget, OpenedFile, TextEncoding, TextFormat};

actions!(
    rusttext,
    [
        NewFile, OpenFile, Save, SaveAs, CloseTab, ReopenTab, ZoomIn, ZoomOut
    ]
);

struct Document {
    id: u64,
    name: SharedString,
    file: Option<FileTarget>,
    format: TextFormat,
    editor: Entity<EditorState>,
    saved_text: Rope,
    dirty: bool,
    _subscriptions: [Subscription; 2],
}

struct DraggedTab {
    id: u64,
    name: SharedString,
}

impl Render for DraggedTab {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .rounded_md()
            .border_1()
            .border_color(cx.theme().border)
            .bg(cx.theme().background)
            .text_color(cx.theme().foreground)
            .px_3()
            .py_1()
            .child(self.name.clone())
    }
}

struct Workspace {
    documents: Vec<Document>,
    active: u64,
    next_id: u64,
    closed: Vec<(PathBuf, bool)>,
    busy: Option<&'static str>,
    error: Option<SharedString>,
    wrap: bool,
    font_size: f32,
}

impl Workspace {
    fn new(paths: Vec<PathBuf>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let mut this = Self {
            documents: Vec::new(),
            active: 0,
            next_id: 1,
            closed: Vec::new(),
            busy: None,
            error: None,
            wrap: false,
            font_size: 15.0,
        };
        this.add_document(None, window, cx);
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
        let id = self.next_id;
        self.next_id += 1;
        let (file, text, format) = match opened {
            Some(opened) => (Some(opened.target), opened.text, opened.format),
            None => (None, String::new(), TextFormat::default()),
        };
        let name = file
            .as_ref()
            .map_or_else(|| "Без имени".into(), |file| file_name(&file.path));
        let language = file
            .as_ref()
            .map_or_else(|| "text".to_owned(), |file| syntax_for(&file.path));

        let editor = cx.new(|cx| {
            EditorState::new(window, cx)
                .line_number(true)
                .soft_wrap(self.wrap)
                .language(language)
                .default_value(text)
        });
        let saved_text = editor.read(cx).text().clone();
        let change = cx.subscribe(&editor, move |this, editor, event, cx| {
            if matches!(event, InputEvent::Change)
                && let Some(doc) = this.documents.iter_mut().find(|doc| doc.id == id)
            {
                doc.dirty = editor.read(cx).text() != &doc.saved_text;
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
            saved_text,
            dirty: false,
            _subscriptions: [change, cursor],
        });
        self.select(id, window, cx);
    }

    fn select(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(doc) = self.documents.iter().find(|doc| doc.id == id) {
            self.active = id;
            let focus = doc.editor.read(cx).focus_handle(cx);
            window.focus(&focus, cx);
            cx.notify();
        }
    }

    fn new_file(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.error = None;
        self.add_document(None, window, cx);
    }

    fn open_picker(&mut self, windows_1251: bool, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() {
            return;
        }
        self.busy = Some("Выбор файла…");
        self.error = None;
        let result = cx.prompt_for_paths(PathPromptOptions {
            files: true,
            directories: false,
            multiple: true,
            prompt: Some("Открыть".into()),
        });
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let result = result.await;
            this.update_in(cx, |this, window, cx| {
                this.busy = None;
                match result {
                    Ok(Ok(Some(paths))) if !paths.is_empty() => {
                        this.open_paths(paths, windows_1251, window, cx);
                    }
                    Ok(Ok(_)) => {}
                    Ok(Err(error)) => {
                        this.error = Some(format!("Не удалось открыть диалог. {error}").into())
                    }
                    Err(_) => this.error = Some("Диалог открытия прерван".into()),
                }
                cx.notify();
            })
            .ok();
        })
        .detach();
    }

    fn open_paths(
        &mut self,
        paths: Vec<PathBuf>,
        windows_1251: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if paths.is_empty() || self.busy.is_some() {
            return;
        }

        self.busy = Some("Открытие…");
        self.error = None;
        let executor = cx.background_executor().clone();
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let mut first_error = None;
            let mut failures = 0;
            for path in paths {
                let name = file_name(&path);
                match executor
                    .spawn(async move { files::open(&path, windows_1251) })
                    .await
                {
                    Ok(opened) => {
                        if this
                            .update_in(cx, |this, window, cx| this.show_opened(opened, window, cx))
                            .is_err()
                        {
                            return;
                        }
                    }
                    Err(error) => {
                        failures += 1;
                        first_error
                            .get_or_insert_with(|| format!("Не удалось открыть «{name}». {error}"));
                    }
                }
            }

            this.update_in(cx, |this, _, cx| {
                this.busy = None;
                this.error = first_error.map(|error| {
                    if failures == 1 {
                        error.into()
                    } else {
                        format!("{error} Ошибок при открытии: {failures}").into()
                    }
                });
                cx.notify();
            })
            .ok();
        })
        .detach();
    }

    fn show_opened(&mut self, opened: OpenedFile, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(doc) = self.documents.iter().find(|doc| {
            doc.file
                .as_ref()
                .is_some_and(|file| file.path == opened.target.path)
        }) {
            self.select(doc.id, window, cx);
            return;
        }

        if self.documents.len() == 1
            && self.active_document().file.is_none()
            && !self.active_document().dirty
        {
            self.documents.clear();
        }
        self.add_document(Some(opened), window, cx);
    }

    fn save(&mut self, save_as: bool, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() {
            return;
        }
        self.error = None;
        let doc = self.active_document();
        let id = doc.id;

        if !save_as && let Some(file) = doc.file.clone() {
            self.write_file(id, file, window, cx);
            return;
        }

        let directory = doc
            .file
            .as_ref()
            .and_then(|file| file.path.parent())
            .unwrap_or_else(|| Path::new(""));
        let suggested = if doc.file.is_some() {
            doc.name.as_ref()
        } else {
            "Без имени.txt"
        };
        let result = cx.prompt_for_new_path(directory, Some(suggested));
        self.busy = Some("Выбор имени файла…");
        let executor = cx.background_executor().clone();
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let target = match result.await {
                Ok(Ok(Some(path))) => executor
                    .spawn(async move { FileTarget::from_picker(path) })
                    .await
                    .map(Some)
                    .map_err(|error| error.to_string()),
                Ok(Ok(None)) => Ok(None),
                Ok(Err(error)) => Err(error.to_string()),
                Err(_) => Err("Диалог сохранения прерван".into()),
            };
            this.update_in(cx, |this, window, cx| {
                this.busy = None;
                match target {
                    Ok(Some(target)) => this.write_file(id, target, window, cx),
                    Ok(None) => {}
                    Err(error) => {
                        this.error = Some(format!("Не удалось выбрать файл. {error}").into())
                    }
                }
                cx.notify();
            })
            .ok();
        })
        .detach();
    }

    fn write_file(
        &mut self,
        id: u64,
        mut target: FileTarget,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Some(other) = self.documents.iter().find(|doc| {
            doc.id != id
                && doc
                    .file
                    .as_ref()
                    .is_some_and(|file| file.path == target.path)
        }) {
            self.error = Some(format!("Файл «{}» уже открыт в другой вкладке", other.name).into());
            cx.notify();
            return;
        }

        let Some(doc) = self.documents.iter().find(|doc| doc.id == id) else {
            return;
        };
        if let Some(file) = &doc.file
            && file.path == target.path
        {
            target.version = file.version;
        }

        let snapshot = doc.editor.read(cx).text().clone();
        let format = doc.format;
        self.busy = Some("Сохранение…");
        cx.notify();
        let task = cx.background_spawn(async move {
            let result = files::save(target, format, &snapshot.to_string());
            (snapshot, result)
        });
        cx.spawn_in(window, async move |this, cx| {
            let (snapshot, result) = task.await;
            this.update_in(cx, |this, _, cx| {
                this.busy = None;
                match result {
                    Ok(file) => {
                        if let Some(doc) = this.documents.iter_mut().find(|doc| doc.id == id) {
                            let language = syntax_for(&file.path);
                            doc.editor
                                .update(cx, |editor, cx| editor.set_highlighter(language, cx));
                            doc.name = file_name(&file.path);
                            doc.file = Some(file);
                            doc.saved_text = snapshot;
                            doc.dirty = doc.editor.read(cx).text() != &doc.saved_text;
                        }
                    }
                    Err(error) => {
                        this.error = Some(format!("Не удалось сохранить файл. {error}").into())
                    }
                }
                cx.notify();
            })
            .ok();
        })
        .detach();
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

    fn toolbar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.busy.is_some();
        div()
            .h(px(46.0))
            .flex()
            .flex_shrink_0()
            .items_center()
            .justify_between()
            .gap_2()
            .px_3()
            .bg(rgb(0x18202b))
            .border_b_1()
            .border_color(rgb(0x293442))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(
                        Button::new("new")
                            .small()
                            .ghost()
                            .label("Создать")
                            .tooltip("Создать вкладку · Ctrl+N")
                            .on_click(cx.listener(|this, _, window, cx| this.new_file(window, cx))),
                    )
                    .child(
                        Button::new("open")
                            .small()
                            .ghost()
                            .label("Открыть")
                            .disabled(busy)
                            .tooltip("Открыть файл · Ctrl+O")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.open_picker(false, window, cx)
                            })),
                    )
                    .child(
                        Button::new("open-1251")
                            .small()
                            .ghost()
                            .label("1251")
                            .disabled(busy)
                            .tooltip("Открыть файл в кодировке Windows-1251")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.open_picker(true, window, cx)
                            })),
                    )
                    .child(
                        Button::new("save")
                            .small()
                            .ghost()
                            .label("Сохранить")
                            .disabled(busy)
                            .tooltip("Сохранить файл · Ctrl+S")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.save(false, window, cx)),
                            ),
                    )
                    .child(
                        Button::new("save-as")
                            .small()
                            .ghost()
                            .label("Сохранить как…")
                            .disabled(busy)
                            .tooltip("Сохранить как · Ctrl+Shift+S")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.save(true, window, cx)),
                            ),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(
                        Button::new("wrap")
                            .small()
                            .ghost()
                            .label("Перенос")
                            .selected(self.wrap)
                            .tooltip("Перенос длинных строк")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.toggle_wrap(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("zoom-out")
                            .small()
                            .ghost()
                            .label("−")
                            .tooltip("Уменьшить шрифт")
                            .on_click(cx.listener(|this, _, _, cx| this.zoom(-1.0, cx))),
                    )
                    .child(
                        Button::new("zoom-in")
                            .small()
                            .ghost()
                            .label("+")
                            .tooltip("Увеличить шрифт")
                            .on_click(cx.listener(|this, _, _, cx| this.zoom(1.0, cx))),
                    ),
            )
    }

    fn tabs(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("tabs")
            .h(px(43.0))
            .flex()
            .flex_shrink_0()
            .items_center()
            .overflow_x_scroll()
            .gap_1()
            .px_3()
            .bg(rgb(0x131a24))
            .border_b_1()
            .border_color(rgb(0x293442))
            .children(self.documents.iter().map(|doc| {
                let id = doc.id;
                let active = id == self.active;
                let name = doc.name.clone();
                div()
                    .id(("tab", id))
                    .flex()
                    .flex_shrink_0()
                    .items_center()
                    .gap_1()
                    .rounded_md()
                    .border_1()
                    .border_color(if active { rgb(0x4a6380) } else { rgb(0x263141) })
                    .bg(if active { rgb(0x26364a) } else { rgb(0x18202b) })
                    .on_drag(
                        DraggedTab {
                            id,
                            name: name.clone(),
                        },
                        |drag, _, _, cx| {
                            cx.new(|_| DraggedTab {
                                id: drag.id,
                                name: drag.name.clone(),
                            })
                        },
                    )
                    .on_drop(cx.listener(move |this, drag: &DraggedTab, _, cx| {
                        this.reorder_tabs(drag.id, id, cx);
                    }))
                    .child(
                        Button::new(("select-tab", id))
                            .small()
                            .ghost()
                            .label(if doc.dirty {
                                format!("●  {name}").into()
                            } else {
                                name
                            })
                            .max_w(px(230.0))
                            .tooltip(doc.file.as_ref().map_or_else(
                                || "Без имени".to_owned(),
                                |file| display_path(&file.path),
                            ))
                            .on_click(
                                cx.listener(move |this, _, window, cx| this.select(id, window, cx)),
                            ),
                    )
                    .child(
                        Button::new(("close-tab", id))
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip("Закрыть вкладку · Ctrl+W")
                            .on_click(cx.listener(move |this, _, window, cx| {
                                this.close_tab(id, window, cx)
                            })),
                    )
            }))
            .child(
                Button::new("reopen")
                    .small()
                    .ghost()
                    .label("↶")
                    .disabled(self.busy.is_some() || self.closed.is_empty())
                    .tooltip("Открыть закрытую вкладку · Ctrl+Shift+T")
                    .on_click(cx.listener(|this, _, window, cx| this.reopen(window, cx))),
            )
    }

    fn status(&self, doc: &Document, cx: &App) -> impl IntoElement {
        let cursor = doc.editor.read(cx).cursor_position();
        let message = if let Some(error) = &self.error {
            error.clone()
        } else if let Some(busy) = self.busy {
            busy.into()
        } else if let Some(file) = &doc.file {
            display_path(&file.path).into()
        } else {
            "Новый документ".into()
        };

        div()
            .flex()
            .flex_shrink_0()
            .items_center()
            .h(px(31.0))
            .gap_4()
            .px_4()
            .bg(rgb(0x18202b))
            .border_t_1()
            .border_color(rgb(0x293442))
            .text_sm()
            .text_color(rgb(0xaebdd0))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .when(self.error.is_some(), |el| el.text_color(rgb(0xffb4ad)))
                    .child(Label::new(message)),
            )
            .child(Label::new(format!(
                "Стр. {}, стлб. {}",
                cursor.line + 1,
                cursor.character + 1
            )))
            .child(Label::new(doc.format.encoding.label()))
            .child(Label::new(doc.format.line_endings_label()))
    }
}

impl Render for Workspace {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let doc = self.active_document();
        div()
            .id("rusttext")
            .key_context("RustText")
            .size_full()
            .flex()
            .flex_col()
            .bg(rgb(0x10161f))
            .text_color(rgb(0xe4eaf2))
            .on_action(cx.listener(|this, _: &NewFile, window, cx| this.new_file(window, cx)))
            .on_action(
                cx.listener(|this, _: &OpenFile, window, cx| this.open_picker(false, window, cx)),
            )
            .on_action(cx.listener(|this, _: &Save, window, cx| this.save(false, window, cx)))
            .on_action(cx.listener(|this, _: &SaveAs, window, cx| this.save(true, window, cx)))
            .on_action(
                cx.listener(|this, _: &CloseTab, window, cx| {
                    this.close_tab(this.active, window, cx)
                }),
            )
            .on_action(cx.listener(|this, _: &ReopenTab, window, cx| this.reopen(window, cx)))
            .on_action(cx.listener(|this, _: &ZoomIn, _, cx| this.zoom(1.0, cx)))
            .on_action(cx.listener(|this, _: &ZoomOut, _, cx| this.zoom(-1.0, cx)))
            .on_drop(cx.listener(|this, paths: &ExternalPaths, window, cx| {
                this.open_paths(paths.paths().to_vec(), false, window, cx);
            }))
            .child(self.toolbar(cx))
            .child(self.tabs(cx))
            .child(
                div().flex_1().min_h_0().w_full().overflow_hidden().child(
                    Editor::new(&doc.editor)
                        .bordered(false)
                        .h_full()
                        .w_full()
                        .font_family("Consolas")
                        .text_size(px(self.font_size)),
                ),
            )
            .child(self.status(doc, cx))
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

fn main() {
    let paths = std::env::args_os().skip(1).map(PathBuf::from).collect();

    application().with_assets(assets::Assets).run(move |cx| {
        init(cx);
        set_locale("ru");
        Theme::change(ThemeMode::Dark, None, cx);
        cx.bind_keys([
            KeyBinding::new("ctrl-n", NewFile, Some("RustText")),
            KeyBinding::new("ctrl-o", OpenFile, Some("RustText")),
            KeyBinding::new("ctrl-s", Save, Some("RustText")),
            KeyBinding::new("ctrl-shift-s", SaveAs, Some("RustText")),
            KeyBinding::new("ctrl-w", CloseTab, Some("RustText")),
            KeyBinding::new("ctrl-shift-t", ReopenTab, Some("RustText")),
            KeyBinding::new("ctrl-=", ZoomIn, Some("RustText")),
            KeyBinding::new("ctrl--", ZoomOut, Some("RustText")),
        ]);

        let options = WindowOptions {
            window_bounds: Some(WindowBounds::Windowed(Bounds::centered(
                None,
                size(px(1080.0), px(700.0)),
                cx,
            ))),
            window_min_size: Some(size(px(640.0), px(350.0))),
            titlebar: Some(TitlebarOptions {
                title: Some("RustText".into()),
                ..Default::default()
            }),
            ..Default::default()
        };
        open_window(options, cx, |window, cx| {
            cx.new(|cx| Workspace::new(paths, window, cx))
        })
        .expect("Не удалось открыть окно RustText");
        cx.activate(true);
    });
}
