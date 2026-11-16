use gpui_kit::component::{
    ActiveTheme, Disableable, Sizable,
    button::{Button, ButtonVariants},
    input::Editor,
    label::Label,
};
use gpui_kit::prelude::FluentBuilder;
use gpui_kit::*;

use super::{Document, Workspace, display_path, file_actions::OpenMode};
use crate::{appearance, go_to_line, search::Command};

actions!(
    rusttext,
    [
        NewFile,
        OpenFile,
        OpenLog,
        OpenWindows1251,
        OpenLogWindows1251,
        RegisterFileAssociations,
        Save,
        SaveAs,
        CloseTab,
        ReopenTab,
        GoToLine,
        FormatJson,
        FindText,
        ReplaceText,
        NextMatch,
        PreviousMatch,
        ZoomIn,
        ZoomOut,
        ToggleWrap
    ]
);

pub(crate) fn bind_keys(cx: &mut App) {
    cx.bind_keys([
        KeyBinding::new("ctrl-n", NewFile, Some("RustText")),
        KeyBinding::new("ctrl-o", OpenFile, Some("RustText")),
        KeyBinding::new("ctrl-s", Save, Some("RustText")),
        KeyBinding::new("ctrl-shift-s", SaveAs, Some("RustText")),
        KeyBinding::new("ctrl-w", CloseTab, Some("RustText")),
        KeyBinding::new("ctrl-shift-t", ReopenTab, Some("RustText")),
        KeyBinding::new("ctrl-g", GoToLine, Some("RustText")),
        KeyBinding::new("ctrl-shift-j", FormatJson, Some("RustText")),
        KeyBinding::new("ctrl-f", FindText, Some("RustText")),
        KeyBinding::new("ctrl-h", ReplaceText, Some("RustText")),
        KeyBinding::new("f3", NextMatch, Some("RustText")),
        KeyBinding::new("shift-f3", PreviousMatch, Some("RustText")),
        KeyBinding::new("ctrl-=", ZoomIn, Some("RustText")),
        KeyBinding::new("ctrl--", ZoomOut, Some("RustText")),
    ]);
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

impl Workspace {
    fn document_header(&self, doc: &Document) -> impl IntoElement {
        div()
            .h(px(34.))
            .flex()
            .flex_shrink_0()
            .items_center()
            .gap_3()
            .px_3()
            .bg(rgb(appearance::SURFACE))
            .border_b_1()
            .border_color(rgb(appearance::BORDER))
            .child(div().max_w(px(280.)).truncate().child(doc.name.clone()))
            .child(
                div()
                    .text_xs()
                    .text_color(rgb(if doc.dirty {
                        appearance::ACCENT
                    } else {
                        appearance::MUTED
                    }))
                    .child(if doc.placeholder {
                        "ТОЛЬКО ЧТЕНИЕ"
                    } else if doc.dirty {
                        "НЕ СОХРАНЕНО"
                    } else {
                        "ТЕКСТ"
                    }),
            )
    }

    fn go_to_line(&self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_none() && self.active_document().log.is_none() {
            go_to_line::open(self.active_document().editor.clone(), window, cx);
        }
    }

    fn tabs(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("tabs")
            .h_full()
            .flex()
            .flex_1()
            .min_w_0()
            .items_center()
            .px_3()
            .child(
                div()
                    .id("tab-list")
                    .flex()
                    .flex_1()
                    .min_w_0()
                    .h_full()
                    .items_center()
                    .overflow_x_scroll()
                    .track_scroll(&self.tab_scroll)
                    .gap_1()
                    .children(self.documents.iter().map(|doc| {
                        let id = doc.id;
                        let active = id == self.active;
                        let suffix = doc
                            .disk_status
                            .tab_suffix()
                            .or(doc.placeholder.then_some("откройте заново"));
                        let name = suffix.map_or_else(
                            || doc.name.clone(),
                            |status| format!("{} ({status})", doc.name).into(),
                        );
                        div()
                            .id(("tab", id))
                            .flex()
                            .flex_shrink_0()
                            .items_center()
                            .gap_1()
                            .rounded_md()
                            .border_1()
                            .border_color(if active {
                                rgb(appearance::ACCENT)
                            } else {
                                rgb(appearance::BORDER)
                            })
                            .bg(if active {
                                rgb(appearance::ACTIVE)
                            } else {
                                rgb(appearance::SURFACE)
                            })
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
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.select(id, window, cx)
                                    })),
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
                    .child(div().id("new-tab-space").flex_1().h_full().on_mouse_down(
                        MouseButton::Left,
                        cx.listener(|this, event: &MouseDownEvent, window, cx| {
                            if event.click_count == 2 {
                                this.new_file(window, cx);
                            }
                        }),
                    )),
            )
            .child(
                Button::new("new-tab")
                    .small()
                    .ghost()
                    .label("+")
                    .disabled(self.busy.is_some() || self.applying_update())
                    .tooltip("Создать вкладку · Ctrl+N")
                    .on_click(cx.listener(|this, _, window, cx| this.new_file(window, cx))),
            )
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

    fn status(&self, doc: &Document, cx: &mut Context<Self>) -> impl IntoElement {
        let cursor = doc.editor.read(cx).cursor_position();
        let message = if let Some(error) = &self.error {
            error.clone()
        } else if let Some(busy) = self.busy {
            busy.into()
        } else if let Some(notice) = doc.disk_status.notice() {
            notice.into()
        } else if doc.placeholder {
            "Файл снова доступен. Откройте его заново.".into()
        } else if let Some(notice) = self.session_error.as_ref().or(self.session_notice.as_ref()) {
            notice.clone()
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
            .bg(rgb(appearance::SURFACE))
            .border_t_1()
            .border_color(rgb(appearance::BORDER))
            .text_sm()
            .text_color(rgb(appearance::MUTED))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .when(
                        self.error.is_some()
                            || doc.disk_status.notice().is_some()
                            || self.session_error.is_some(),
                        |el| el.text_color(rgb(appearance::ERROR)),
                    )
                    .child(Label::new(message)),
            )
            .child(
                Button::new("go-to-line")
                    .xsmall()
                    .ghost()
                    .label(format!(
                        "Стр. {}, стлб. {}",
                        cursor.line + 1,
                        cursor.character + 1
                    ))
                    .disabled(self.busy.is_some())
                    .tooltip("Перейти к строке · Ctrl+G")
                    .on_click(cx.listener(|this, _, window, cx| this.go_to_line(window, cx))),
            )
            .child(Label::new(doc.format.encoding.label()))
            .child(Label::new(doc.format.line_endings_label()))
    }

    fn jump_to_json_error(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(super::json_validation::Status::Invalid(problem)) =
            self.active_document().json_status.as_ref()
        else {
            return;
        };
        let position = problem.range.start;
        let editor = self.active_document().editor.clone();
        editor.update(cx, |editor, cx| {
            editor.set_cursor_position(position, window, cx)
        });
    }

    fn json_validation_banner(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let status = self.active_document().json_status.clone()?;
        let (message, invalid) = match status {
            super::json_validation::Status::Invalid(problem) => (
                format!(
                    "Некорректный JSON · строка {}, столбец {} · {}",
                    problem.line, problem.column, problem.message
                ),
                true,
            ),
            super::json_validation::Status::TooLarge => (
                "Проверка JSON не выполнена. Размер превышает 64 МиБ.".to_owned(),
                false,
            ),
            super::json_validation::Status::Checking | super::json_validation::Status::Valid => {
                return None;
            }
        };
        Some(
            div()
                .w_full()
                .min_h(px(46.))
                .flex()
                .items_center()
                .gap_3()
                .px_4()
                .bg(rgb(if invalid { 0x6b1e24 } else { 0x5e431d }))
                .text_color(rgb(0xffe5e5))
                .border_b_1()
                .border_color(rgb(appearance::ERROR))
                .child(Label::new(message).text_sm())
                .when(invalid, |element| {
                    element.child(
                        Button::new("json-error-location")
                            .small()
                            .outline()
                            .label("Перейти к ошибке")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.jump_to_json_error(window, cx)
                            })),
                    )
                })
                .into_any_element(),
        )
    }
}

impl Render for Workspace {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let doc = self.active_document();
        let json_banner = self.json_validation_banner(cx);
        let body = if let Some(log) = &doc.log {
            log.clone().into_any_element()
        } else {
            Editor::new(&doc.editor)
                .readonly(doc.placeholder || self.applying_update())
                .bordered(false)
                .h_full()
                .w_full()
                .font_family("Consolas")
                .text_size(px(self.font_size))
                .into_any_element()
        };
        div()
            .id("rusttext")
            .key_context("RustText")
            .size_full()
            .flex()
            .flex_col()
            .bg(rgb(appearance::BACKGROUND))
            .text_color(rgb(appearance::FOREGROUND))
            .on_action(cx.listener(|this, _: &NewFile, window, cx| this.new_file(window, cx)))
            .on_action(cx.listener(|this, _: &OpenFile, window, cx| {
                this.open_picker(OpenMode::NORMAL, window, cx)
            }))
            .on_action(cx.listener(|this, _: &OpenLog, window, cx| {
                this.open_picker(OpenMode::LOG, window, cx)
            }))
            .on_action(cx.listener(|this, _: &OpenWindows1251, window, cx| {
                this.open_picker(OpenMode::WINDOWS_1251, window, cx)
            }))
            .on_action(cx.listener(|this, _: &OpenLogWindows1251, window, cx| {
                this.open_picker(OpenMode::LOG_1251, window, cx)
            }))
            .on_action(
                cx.listener(|this, _: &RegisterFileAssociations, window, cx| {
                    this.register_file_associations(window, cx)
                }),
            )
            .on_action(cx.listener(|this, _: &Save, window, cx| this.save(false, window, cx)))
            .on_action(cx.listener(|this, _: &SaveAs, window, cx| this.save(true, window, cx)))
            .on_action(
                cx.listener(|this, _: &CloseTab, window, cx| {
                    this.close_tab(this.active, window, cx)
                }),
            )
            .on_action(cx.listener(|this, _: &ReopenTab, window, cx| this.reopen(window, cx)))
            .on_action(cx.listener(|this, _: &GoToLine, window, cx| this.go_to_line(window, cx)))
            .on_action(cx.listener(|this, _: &FormatJson, window, cx| this.format_json(window, cx)))
            .on_action(
                cx.listener(|this, _: &FindText, window, cx| this.open_search(false, window, cx)),
            )
            .on_action(
                cx.listener(|this, _: &ReplaceText, window, cx| this.open_search(true, window, cx)),
            )
            .on_action(cx.listener(|this, _: &NextMatch, window, cx| {
                this.run_search(Command::Next, window, cx)
            }))
            .on_action(cx.listener(|this, _: &PreviousMatch, window, cx| {
                this.run_search(Command::Previous, window, cx)
            }))
            .on_action(cx.listener(|this, _: &ZoomIn, _, cx| this.zoom(1.0, cx)))
            .on_action(cx.listener(|this, _: &ZoomOut, _, cx| this.zoom(-1.0, cx)))
            .on_action(cx.listener(|this, _: &ToggleWrap, window, cx| this.toggle_wrap(window, cx)))
            .on_drop(cx.listener(|this, paths: &ExternalPaths, window, cx| {
                this.open_paths(paths.paths().to_vec(), OpenMode::NORMAL, window, cx);
            }))
            .child(self.menubar(cx))
            .child(
                div()
                    .h(px(38.0))
                    .flex()
                    .flex_shrink_0()
                    .items_center()
                    .bg(rgb(appearance::SURFACE))
                    .border_b_1()
                    .border_color(rgb(appearance::BORDER))
                    .child(self.tabs(cx)),
            )
            .children(doc.log.is_none().then(|| self.document_header(doc)))
            .children(json_banner)
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .overflow_hidden()
                    .child(body),
            )
            .children(self.search_panel(window, cx))
            .children(doc.log.is_none().then(|| self.status(doc, cx)))
            .children(self.update_panel(cx))
    }
}
