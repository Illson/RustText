use std::path::Path;

use gpui_kit::component::{
    ActiveTheme, Disableable, Selectable, Sizable,
    button::{Button, ButtonVariants},
    input::Editor,
    label::Label,
    menu::{DropdownMenu, PopupMenuItem},
};
use gpui_kit::prelude::FluentBuilder;
use gpui_kit::*;

use super::{Document, Workspace, file_actions::OpenMode, file_name};
use crate::{go_to_line, search::Command};

actions!(
    rusttext,
    [
        NewFile,
        OpenFile,
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
        ZoomOut
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
    fn go_to_line(&self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_none() && self.active_document().log.is_none() {
            go_to_line::open(self.active_document().editor.clone(), window, cx);
        }
    }

    fn toolbar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.busy.is_some();
        let log = self.active_document().log.as_ref();
        let can_edit = !busy && log.is_none();
        let wrap = log.map_or(self.wrap, |viewer| viewer.read(cx).wraps_lines());
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
                            .disabled(self.applying_update())
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
                                this.open_picker(OpenMode::NORMAL, window, cx)
                            })),
                    )
                    .child(self.recent_files_button(cx))
                    .child(self.open_log_button(cx))
                    .child(
                        Button::new("open-1251")
                            .small()
                            .ghost()
                            .label("1251")
                            .disabled(busy)
                            .tooltip("Открыть файл в кодировке Windows-1251")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.open_picker(OpenMode::WINDOWS_1251, window, cx)
                            })),
                    )
                    .child(
                        Button::new("save")
                            .small()
                            .ghost()
                            .label("Сохранить")
                            .disabled(!can_edit)
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
                            .disabled(!can_edit)
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
                        Button::new("find-text")
                            .small()
                            .ghost()
                            .label("Поиск")
                            .tooltip("Найти · Ctrl+F")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.open_search(false, window, cx)
                            })),
                    )
                    .child(
                        Button::new("format-json")
                            .small()
                            .ghost()
                            .label("JSON")
                            .tooltip("Форматировать JSON · Ctrl+Shift+J")
                            .disabled(!can_edit)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.format_json(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("wrap")
                            .small()
                            .ghost()
                            .label("Перенос")
                            .selected(wrap)
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
                            .disabled(log.is_some())
                            .tooltip("Уменьшить шрифт")
                            .on_click(cx.listener(|this, _, _, cx| this.zoom(-1.0, cx))),
                    )
                    .child(
                        Button::new("zoom-in")
                            .small()
                            .ghost()
                            .label("+")
                            .disabled(log.is_some())
                            .tooltip("Увеличить шрифт")
                            .on_click(cx.listener(|this, _, _, cx| this.zoom(1.0, cx))),
                    ),
            )
    }

    fn open_log_button(&self, cx: &Context<Self>) -> impl IntoElement {
        let workspace = cx.entity().downgrade();
        Button::new("open-as-log")
            .small()
            .ghost()
            .label("Лог")
            .disabled(self.busy.is_some())
            .dropdown_menu(move |menu, _, _| {
                let utf8 = workspace.clone();
                let cp1251 = workspace.clone();
                menu.item(
                    PopupMenuItem::new("Открыть как лог").on_click(move |_, window, cx| {
                        let _ =
                            utf8.update(cx, |this, cx| this.open_picker(OpenMode::LOG, window, cx));
                    }),
                )
                .item(PopupMenuItem::new("Открыть лог в Windows-1251").on_click(
                    move |_, window, cx| {
                        let _ = cp1251.update(cx, |this, cx| {
                            this.open_picker(OpenMode::LOG_1251, window, cx)
                        });
                    },
                ))
            })
    }

    fn recent_files_button(&self, cx: &Context<Self>) -> impl IntoElement {
        let entries = self.recent.entries().to_vec();
        let workspace = cx.entity().downgrade();
        Button::new("recent-files")
            .small()
            .ghost()
            .label("Недавние")
            .disabled(self.busy.is_some())
            .dropdown_menu(move |menu, _, _| {
                if entries.is_empty() {
                    return menu.item(PopupMenuItem::label("Недавних файлов пока нет"));
                }

                entries.iter().fold(menu.scrollable(true), |menu, entry| {
                    let entry = entry.clone();
                    let workspace = workspace.clone();
                    let name = file_name(&entry.path);
                    let directory = entry.path.parent().map(display_path).unwrap_or_default();
                    menu.item(
                        PopupMenuItem::new(format!("{name} — {directory}")).on_click(
                            move |_, window, cx| {
                                let _ = workspace.update(cx, |this, cx| {
                                    this.open_paths(
                                        vec![entry.path.clone()],
                                        OpenMode::from_recent(&entry),
                                        window,
                                        cx,
                                    );
                                });
                            },
                        ),
                    )
                })
            })
    }

    fn tabs(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("tabs")
            .h_full()
            .flex()
            .flex_1()
            .min_w_0()
            .items_center()
            .overflow_x_scroll()
            .gap_1()
            .px_3()
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
                    .when(
                        self.error.is_some()
                            || doc.disk_status.notice().is_some()
                            || self.session_error.is_some(),
                        |el| el.text_color(rgb(0xffb4ad)),
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
}

impl Render for Workspace {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let doc = self.active_document();
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
            .bg(rgb(0x10161f))
            .text_color(rgb(0xe4eaf2))
            .on_action(cx.listener(|this, _: &NewFile, window, cx| this.new_file(window, cx)))
            .on_action(cx.listener(|this, _: &OpenFile, window, cx| {
                this.open_picker(OpenMode::NORMAL, window, cx)
            }))
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
            .on_drop(cx.listener(|this, paths: &ExternalPaths, window, cx| {
                this.open_paths(paths.paths().to_vec(), OpenMode::NORMAL, window, cx);
            }))
            .child(self.toolbar(cx))
            .child(
                div()
                    .h(px(43.0))
                    .flex()
                    .flex_shrink_0()
                    .items_center()
                    .bg(rgb(0x131a24))
                    .border_b_1()
                    .border_color(rgb(0x293442))
                    .child(self.tabs(cx))
                    .child(div().flex_shrink_0().pr_3().child(self.update_button(cx))),
            )
            .children(self.update_panel(cx))
            .children(self.search_panel(cx))
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .overflow_hidden()
                    .child(body),
            )
            .children(doc.log.is_none().then(|| self.status(doc, cx)))
    }
}

fn display_path(path: &Path) -> String {
    let display = path.to_string_lossy();
    if let Some(unc) = display.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{unc}")
    } else {
        display.strip_prefix(r"\\?\").unwrap_or(&display).to_owned()
    }
}
