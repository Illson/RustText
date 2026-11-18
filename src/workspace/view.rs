use std::path::Path;

use gpui_kit::component::{
    ActiveTheme, Disableable, Sizable, TitleBar,
    button::{Button, ButtonVariants},
    input::Editor,
    label::Label,
    menu::{ContextMenuExt, PopupMenuItem},
    resizable::{h_resizable, resizable_panel, v_resizable},
};
use gpui_kit::prelude::FluentBuilder;
use gpui_kit::*;

use super::{Document, Workspace, disk_status::DiskStatus, display_path, file_actions::OpenMode};
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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum DocumentSaveState {
    ReadOnly,
    Unsaved,
    UntitledClean,
    Saved,
    DiskChanged,
    DiskMissing,
    DiskUnavailable,
}

impl DocumentSaveState {
    fn from_document(
        placeholder: bool,
        dirty: bool,
        has_file: bool,
        disk_status: DiskStatus,
    ) -> Self {
        if placeholder {
            Self::ReadOnly
        } else if dirty {
            Self::Unsaved
        } else if !has_file {
            Self::UntitledClean
        } else {
            match disk_status {
                DiskStatus::Current => Self::Saved,
                DiskStatus::Changed => Self::DiskChanged,
                DiskStatus::Missing => Self::DiskMissing,
                DiskStatus::Unavailable => Self::DiskUnavailable,
            }
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::ReadOnly => "Только чтение",
            Self::Unsaved | Self::UntitledClean => "Не сохранён",
            Self::Saved => "Сохранён",
            Self::DiskChanged => "Изменён на диске",
            Self::DiskMissing => "Отсутствует",
            Self::DiskUnavailable => "Недоступен",
        }
    }

    fn color(self) -> u32 {
        match self {
            Self::ReadOnly | Self::UntitledClean => appearance::MUTED,
            Self::Unsaved => appearance::ACCENT,
            Self::Saved => appearance::SUCCESS,
            Self::DiskChanged | Self::DiskMissing | Self::DiskUnavailable => appearance::ERROR,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct StatusMessage {
    text: SharedString,
    is_error: bool,
}

impl StatusMessage {
    fn color(&self) -> u32 {
        if self.is_error {
            appearance::ERROR
        } else {
            appearance::MUTED
        }
    }
}

fn status_message(
    error: Option<&SharedString>,
    busy: Option<&'static str>,
    disk_status: DiskStatus,
    placeholder: bool,
    session_error: Option<&SharedString>,
    session_notice: Option<&SharedString>,
    file_path: Option<&Path>,
) -> StatusMessage {
    if let Some(error) = error {
        StatusMessage {
            text: error.clone(),
            is_error: true,
        }
    } else if let Some(busy) = busy {
        StatusMessage {
            text: busy.into(),
            is_error: false,
        }
    } else if let Some(notice) = disk_status.notice() {
        StatusMessage {
            text: notice.into(),
            is_error: true,
        }
    } else if placeholder {
        StatusMessage {
            text: "Файл снова доступен. Откройте его заново.".into(),
            is_error: false,
        }
    } else if let Some(error) = session_error {
        StatusMessage {
            text: error.clone(),
            is_error: true,
        }
    } else if let Some(notice) = session_notice {
        StatusMessage {
            text: notice.clone(),
            is_error: false,
        }
    } else if let Some(path) = file_path {
        StatusMessage {
            text: display_path(path).into(),
            is_error: false,
        }
    } else {
        StatusMessage {
            text: "Новый документ".into(),
            is_error: false,
        }
    }
}

impl Workspace {
    fn go_to_line(&self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_none()
            && let Some(doc) = self.documents.iter().find(|doc| doc.id == self.active)
            && doc.log.is_none()
        {
            go_to_line::open(doc.editor.clone(), window, cx);
        }
    }

    fn tabs(
        &self,
        group_id: u64,
        group: &crate::layout::Group,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let scroll = window.use_keyed_state(("group-tab-scroll", group_id), cx, |_, _| {
            (ScrollHandle::new(), None)
        });
        let scroll = scroll.update(cx, |(scroll, active), _| {
            if *active != group.active {
                if let Some(index) = group
                    .active
                    .and_then(|id| group.documents.iter().position(|doc| *doc == id))
                {
                    scroll.scroll_to_item(index);
                }
                *active = group.active;
            }
            scroll.clone()
        });
        div()
            .id(("tabs", group_id))
            .h_full()
            .flex()
            .flex_1()
            .min_w_0()
            .items_center()
            .px_3()
            .child(
                div()
                    .id(("tab-list", group_id))
                    .flex()
                    .flex_1()
                    .min_w_0()
                    .h_full()
                    .items_center()
                    .overflow_x_scroll()
                    .track_scroll(&scroll)
                    .gap_1()
                    .children(
                        group
                            .documents
                            .iter()
                            .filter_map(|id| self.documents.iter().find(|doc| doc.id == *id))
                            .map(|doc| {
                                let id = doc.id;
                                let active = group.active == Some(id);
                                let suffix = doc
                                    .disk_status
                                    .tab_suffix()
                                    .or(doc.placeholder.then_some("откройте заново"));
                                let name = suffix.map_or_else(
                                    || doc.name.clone(),
                                    |status| format!("{} ({status})", doc.name).into(),
                                );
                                let tab = div()
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
                                    .on_drop(cx.listener(
                                        move |this, drag: &DraggedTab, window, cx| {
                                            cx.stop_propagation();
                                            if !this.applying_update()
                                                && this.layout.move_document(
                                                    drag.id,
                                                    group_id,
                                                    Some(id),
                                                )
                                            {
                                                this.select(drag.id, window, cx);
                                            }
                                        },
                                    ))
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
                                            .on_click(cx.listener(
                                                move |this, event: &ClickEvent, window, cx| {
                                                    if event.click_count() == 2 {
                                                        this.close_tab(id, window, cx);
                                                    } else {
                                                        this.select(id, window, cx);
                                                    }
                                                },
                                            )),
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
                                    );
                                let Some(path) = doc.file.as_ref().map(|file| file.path.clone())
                                else {
                                    return tab.into_any_element();
                                };
                                let workspace = cx.entity().downgrade();
                                tab.context_menu(move |menu, _, _| {
                                    if !path.is_file() {
                                        return menu;
                                    }
                                    let reveal_path = path.clone();
                                    let copy_path = display_path(&path);
                                    let workspace = workspace.clone();
                                    menu.item(PopupMenuItem::new("Показать в Проводнике").on_click(
                                        move |_, window, cx| {
                                            let reveal_path = reveal_path.clone();
                                            let task = cx.background_spawn(async move {
                                                super::reveal::select_in_explorer(&reveal_path)
                                            });
                                            window
                                                .spawn(cx, async move |cx| {
                                                    if let Err(error) = task.await {
                                                        let detail = error.to_string();
                                                        let _ = cx.update(|window, cx| {
                                                            drop(window.prompt(
                                                        PromptLevel::Critical,
                                                        "Не удалось показать файл в Проводнике.",
                                                        Some(&detail),
                                                        &[PromptButton::ok("Закрыть")],
                                                        cx,
                                                    ));
                                                        });
                                                    }
                                                })
                                                .detach();
                                        },
                                    ))
                                    .item(PopupMenuItem::new("Копировать путь").on_click(
                                        move |_, _, cx| {
                                            cx.write_to_clipboard(ClipboardItem::new_string(
                                                copy_path.clone(),
                                            ));
                                        },
                                    ))
                                    .separator()
                                    .item(
                                        PopupMenuItem::new("Закрыть вкладку").on_click(
                                            move |_, window, cx| {
                                                let _ = workspace.update(cx, |this, cx| {
                                                    this.close_tab(id, window, cx)
                                                });
                                            },
                                        ),
                                    )
                                })
                                .into_any_element()
                            }),
                    )
                    .child(
                        div()
                            .id(("new-tab-space", group_id))
                            .flex_1()
                            .h_full()
                            .on_mouse_down(
                                MouseButton::Left,
                                cx.listener(move |this, event: &MouseDownEvent, window, cx| {
                                    if event.click_count == 2 {
                                        this.layout.focus(group_id);
                                        this.new_file(window, cx);
                                    }
                                }),
                            ),
                    ),
            )
            .child(
                Button::new(("new-tab", group_id))
                    .small()
                    .ghost()
                    .label("+")
                    .disabled(self.busy.is_some() || self.applying_update())
                    .tooltip("Создать вкладку · Ctrl+N")
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.layout.focus(group_id);
                        this.new_file(window, cx)
                    })),
            )
            .child(
                Button::new(("reopen", group_id))
                    .small()
                    .ghost()
                    .label("↶")
                    .disabled(self.busy.is_some() || self.closed.is_empty())
                    .tooltip("Открыть закрытую вкладку · Ctrl+Shift+T")
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.layout.focus(group_id);
                        this.reopen(window, cx)
                    })),
            )
    }

    fn group_tree(
        &self,
        node: &crate::layout::Node,
        available: Size<Pixels>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        if let Some(split) = &node.split {
            let horizontal = split.axis == crate::layout::Axis::Horizontal;
            let extent = if horizontal {
                available.width
            } else {
                available.height
            };
            let first_size = extent * split.ratio;
            let second_size = extent * (1.0 - split.ratio);
            let first_available = if horizontal {
                size(first_size, available.height)
            } else {
                size(available.width, first_size)
            };
            let second_available = if horizontal {
                size(second_size, available.height)
            } else {
                size(available.width, second_size)
            };
            let first = self.group_tree(&split.first, first_available, window, cx);
            let second = self.group_tree(&split.second, second_available, window, cx);
            let id = node.id;
            let weak = cx.entity().downgrade();
            let panels = if horizontal {
                h_resizable(("editor-split", id))
            } else {
                v_resizable(("editor-split", id))
            };
            return panels
                .child(
                    resizable_panel()
                        .size(first_size)
                        .size_range(px(60.)..Pixels::MAX)
                        .child(first),
                )
                .child(
                    resizable_panel()
                        .size(second_size)
                        .size_range(px(60.)..Pixels::MAX)
                        .child(second),
                )
                .on_resize(move |state, _, cx| {
                    let sizes = state.read(cx).sizes();
                    if sizes.len() == 2 {
                        let total = f32::from(sizes[0] + sizes[1]);
                        if total > 0. {
                            let ratio = f32::from(sizes[0]) / total;
                            let _ = weak.update(cx, |this, cx| {
                                this.layout.set_ratio(id, ratio);
                                cx.notify();
                            });
                        }
                    }
                })
                .into_any_element();
        }
        let Some(group) = &node.group else {
            return div().into_any_element();
        };
        let id = node.id;
        let doc = group
            .active
            .and_then(|active| self.documents.iter().find(|doc| doc.id == active));
        let body = doc
            .map(|doc| {
                if let Some(log) = &doc.log {
                    return log.clone().into_any_element();
                }
                div()
                    .size_full()
                    .flex()
                    .child(
                        div().flex_1().min_w_0().h_full().child(
                            Editor::new(&doc.editor)
                                .readonly(doc.placeholder || self.applying_update())
                                .bordered(false)
                                .h_full()
                                .w_full()
                                .font_family("Consolas")
                                .text_size(px(self.font_size)),
                        ),
                    )
                    .child(crate::minimap::strip(&doc.minimap))
                    .into_any_element()
            })
            .unwrap_or_else(|| {
                div()
                    .size_full()
                    .flex()
                    .items_center()
                    .justify_center()
                    .text_color(rgb(appearance::MUTED))
                    .child("Перетащите вкладку или создайте документ")
                    .into_any_element()
            });
        div()
            .id(("editor-group", id))
            .size_full()
            .min_w_0()
            .min_h_0()
            .flex()
            .flex_col()
            .border_1()
            .border_color(rgb(if self.layout.focused == id {
                appearance::ACCENT
            } else {
                appearance::BORDER
            }))
            .capture_any_mouse_down(cx.listener(move |this, _, window, cx| {
                let active = this
                    .layout
                    .groups()
                    .into_iter()
                    .find(|(group, _)| *group == id)
                    .and_then(|(_, group)| group.active);
                if this.layout.focused != id || active.unwrap_or(0) != this.active {
                    if let Some(active) = active {
                        this.select(active, window, cx);
                    } else {
                        this.focus_empty_group(id, window, cx);
                    }
                }
            }))
            .on_drop(cx.listener(move |this, dragged: &DraggedTab, window, cx| {
                cx.stop_propagation();
                if !this.applying_update() && this.layout.move_document(dragged.id, id, None) {
                    this.select(dragged.id, window, cx);
                }
            }))
            .child(
                div()
                    .h(px(36.))
                    .flex_shrink_0()
                    .flex()
                    .items_center()
                    .bg(rgb(appearance::SURFACE))
                    .border_b_1()
                    .border_color(rgb(appearance::BORDER))
                    .child(self.tabs(id, group, window, cx))
                    .child(
                        Button::new(("split-horizontal", id))
                            .small()
                            .ghost()
                            .label("◫")
                            .tooltip("Разделить по горизонтали")
                            .disabled(self.applying_update())
                            .on_click(cx.listener(move |this, _, window, cx| {
                                if let Some(empty) =
                                    this.layout.split(id, crate::layout::Axis::Horizontal)
                                {
                                    this.focus_empty_group(empty, window, cx);
                                }
                            })),
                    )
                    .child(
                        Button::new(("split-vertical", id))
                            .small()
                            .ghost()
                            .label("⬒")
                            .tooltip("Разделить по вертикали")
                            .disabled(self.applying_update())
                            .on_click(cx.listener(move |this, _, window, cx| {
                                if let Some(empty) =
                                    this.layout.split(id, crate::layout::Axis::Vertical)
                                {
                                    this.focus_empty_group(empty, window, cx);
                                }
                            })),
                    )
                    .when(
                        group.documents.is_empty() && self.layout.groups().len() > 1,
                        |header| {
                            header.child(
                                Button::new(("close-empty-group", id))
                                    .small()
                                    .ghost()
                                    .label("×")
                                    .tooltip("Закрыть пустую панель · Ctrl+W")
                                    .disabled(self.busy.is_some() || self.applying_update())
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.close_empty_group(id, window, cx);
                                    })),
                            )
                        },
                    ),
            )
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .min_w_0()
                    .overflow_hidden()
                    .child(body),
            )
            .children(
                doc.filter(|doc| doc.log.is_none())
                    .map(|doc| self.status(doc, cx)),
            )
            .into_any_element()
    }

    fn status(&self, doc: &Document, cx: &mut Context<Self>) -> impl IntoElement {
        let cursor = doc.editor.read(cx).cursor_position();
        let message = status_message(
            self.error.as_ref(),
            self.busy,
            doc.disk_status,
            doc.placeholder,
            self.session_error.as_ref(),
            self.session_notice.as_ref(),
            doc.file.as_ref().map(|file| file.path.as_path()),
        );
        let message_color = message.color();
        let save_state = DocumentSaveState::from_document(
            doc.placeholder,
            doc.dirty,
            doc.file.is_some(),
            doc.disk_status,
        );

        div()
            .flex()
            .flex_shrink_0()
            .items_center()
            .overflow_hidden()
            .h(px(31.0))
            .gap_2()
            .px_3()
            .bg(rgb(appearance::SURFACE))
            .border_t_1()
            .border_color(rgb(appearance::BORDER))
            .text_sm()
            .text_color(rgb(appearance::MUTED))
            .child(
                div().flex_1().min_w_0().truncate().child(
                    Label::new(message.text)
                        .truncate()
                        .text_color(rgb(message_color)),
                ),
            )
            .child(
                Label::new(save_state.label())
                    .flex_shrink_0()
                    .text_color(rgb(save_state.color())),
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
            .child(
                Label::new(doc.format.encoding.label())
                    .min_w_0()
                    .truncate()
                    .text_color(rgb(appearance::MUTED)),
            )
            .child(
                Label::new(doc.format.line_endings_label())
                    .min_w_0()
                    .truncate()
                    .text_color(rgb(appearance::MUTED)),
            )
    }

    fn jump_to_json_error(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(super::json_validation::Status::Invalid(problem)) = self
            .active_document_opt()
            .and_then(|doc| doc.json_validation.status())
        else {
            return;
        };
        let position = problem.range.start;
        let editor = self.active_document().editor.clone();
        editor.update(cx, |editor, cx| {
            editor.set_cursor_position(position, window, cx)
        });
    }

    pub(super) fn json_validation_banner(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let status = self
            .documents
            .iter()
            .find(|doc| doc.id == self.active)?
            .json_validation
            .status()?;
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
            super::json_validation::Status::Valid => return None,
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
                .child(div().flex_1())
                .child(
                    Button::new("dismiss-json-validation")
                        .small()
                        .ghost()
                        .label("Отключить проверку")
                        .on_click(cx.listener(|this, _, _, cx| this.dismiss_json_validation(cx))),
                )
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
        let root = self.layout.root.clone();
        let showing_home = self.showing_home();
        let body = if showing_home {
            self.home_page(window, cx)
        } else {
            self.group_tree(&root, window.viewport_size(), window, cx)
        };
        let json_banner = (!showing_home)
            .then(|| self.json_validation_banner(cx))
            .flatten();
        let workspace = div()
            .id("rusttext")
            .key_context("RustText")
            .track_focus(&self.fallback_focus)
            .size_full()
            .flex()
            .flex_col()
            .bg(rgb(appearance::BACKGROUND))
            .text_color(rgb(appearance::FOREGROUND))
            .on_key_down(cx.listener(|this, event: &KeyDownEvent, window, cx| {
                if event.keystroke.key == "escape"
                    && this.home.open
                    && !this.documents.is_empty()
                    && this.settings_panel.is_none()
                    && this.versions.is_none()
                    && this.template_generator.is_none()
                    && this.list_comparison.is_none()
                {
                    this.close_home(window, cx);
                    cx.stop_propagation();
                }
            }))
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
            .on_action(cx.listener(|this, _: &Save, window, cx| {
                if !this.showing_home() {
                    this.save(false, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &SaveAs, window, cx| {
                if !this.showing_home() {
                    this.save(true, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &CloseTab, window, cx| {
                if this.showing_home() {
                    return;
                }
                if this.active == 0 && this.layout.groups().len() > 1 {
                    this.close_empty_group(this.layout.focused, window, cx);
                } else {
                    this.close_tab(this.active, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ReopenTab, window, cx| this.reopen(window, cx)))
            .on_action(cx.listener(|this, _: &GoToLine, window, cx| {
                if !this.showing_home() {
                    this.go_to_line(window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &FormatJson, window, cx| {
                if !this.showing_home() {
                    this.format_json(window, cx);
                }
            }))
            .on_action(
                cx.listener(|this, _: &FindText, window, cx| this.open_search(false, window, cx)),
            )
            .on_action(
                cx.listener(|this, _: &ReplaceText, window, cx| this.open_search(true, window, cx)),
            )
            .on_action(cx.listener(|this, _: &NextMatch, window, cx| {
                if !this.showing_home() {
                    this.run_search(Command::Next, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &PreviousMatch, window, cx| {
                if !this.showing_home() {
                    this.run_search(Command::Previous, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ZoomIn, _, cx| {
                if !this.showing_home() {
                    this.zoom(1.0, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ZoomOut, _, cx| {
                if !this.showing_home() {
                    this.zoom(-1.0, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleWrap, window, cx| {
                if !this.showing_home() {
                    this.toggle_wrap(window, cx);
                }
            }))
            .on_drop(cx.listener(|this, paths: &ExternalPaths, window, cx| {
                this.open_paths(paths.paths().to_vec(), OpenMode::NORMAL, window, cx);
            }))
            .child(TitleBar::new().child(self.menubar(cx)))
            .children(json_banner)
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .overflow_hidden()
                    .child(body),
            )
            .children(
                (!showing_home)
                    .then(|| self.search_panel(window, cx))
                    .flatten(),
            )
            .children(self.update_panel(cx));
        let layers = div()
            .relative()
            .size_full()
            .child(workspace)
            .children(self.template_generator_panel(cx))
            .children(self.list_comparison_panel(cx))
            .children(self.versions_panel(cx))
            .children(self.settings_panel(cx));
        #[cfg(windows)]
        let layers = layers.children(self.window_finder_panel(cx));
        layers
    }
}

#[cfg(test)]
#[path = "../../tests/unit/workspace_view.rs"]
mod tests;
