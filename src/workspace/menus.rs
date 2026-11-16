use gpui_kit::{
    component::{
        Selectable, Sizable,
        button::{Button, ButtonVariants},
        label::Label,
        menu::{DropdownMenu, PopupMenuItem},
    },
    *,
};

use super::{Workspace, display_path, file_actions::OpenMode, file_name, view::*};
use crate::appearance;

impl Workspace {
    pub(super) fn menubar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        static LOGO: std::sync::OnceLock<std::sync::Arc<Image>> = std::sync::OnceLock::new();
        let logo = LOGO
            .get_or_init(|| {
                std::sync::Arc::new(Image::from_bytes(
                    ImageFormat::Png,
                    include_bytes!("../../assets/rusttext.png").to_vec(),
                ))
            })
            .clone();
        let searching = self.search.is_some()
            || self
                .active_document()
                .log
                .as_ref()
                .is_some_and(|log| log.read(cx).search_visible());
        div()
            .h(px(36.))
            .flex()
            .flex_shrink_0()
            .items_center()
            .gap_1()
            .px_3()
            .bg(rgb(appearance::SURFACE))
            .border_b_1()
            .border_color(rgb(appearance::BORDER))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .pr_3()
                    .text_color(rgb(appearance::ACCENT))
                    .child(img(logo).w(px(22.)).h(px(22.)).flex_shrink_0())
                    .child(Label::new("RustText")),
            )
            .child(self.file_menu_button(cx))
            .child(self.tools_menu_button(cx))
            .child(
                Button::new("find-text")
                    .small()
                    .ghost()
                    .label("Поиск")
                    .selected(searching)
                    .tooltip("Найти · Ctrl+F")
                    .on_click(
                        cx.listener(|this, _, window, cx| this.open_search(false, window, cx)),
                    ),
            )
            .child(div().flex_1())
    }

    fn menu_focus(&self, cx: &App) -> FocusHandle {
        let doc = self.active_document();
        doc.log.as_ref().map_or_else(
            || doc.editor.read(cx).focus_handle(cx),
            |log| log.read(cx).focus_handle(cx),
        )
    }

    fn file_menu_button(&self, cx: &Context<Self>) -> impl IntoElement {
        let busy = self.busy.is_some() || self.applying_update();
        let doc = self.active_document();
        let can_save = !busy && doc.log.is_none() && !doc.placeholder;
        let closed_entries = self.closed_tab_entries();
        let can_reopen = !busy && !closed_entries.is_empty();
        let recent_entries = self.recent.entries().to_vec();
        let workspace = cx.entity().downgrade();
        let focus = self.menu_focus(cx);
        Button::new("file-menu")
            .small()
            .ghost()
            .label("Файл")
            .dropdown_menu(move |menu, window, cx| {
                let closed_workspace = workspace.clone();
                let closed_entries = closed_entries.clone();
                let recent_workspace = workspace.clone();
                let recent_entries = recent_entries.clone();
                menu.action_context(focus.clone())
                    .min_w(px(280.))
                    .scrollable(true)
                    .item(
                        PopupMenuItem::new("Новая вкладка")
                            .action(Box::new(NewFile))
                            .disabled(busy),
                    )
                    .item(
                        PopupMenuItem::new("Открыть…")
                            .action(Box::new(OpenFile))
                            .disabled(busy),
                    )
                    .item(
                        PopupMenuItem::new("Открыть как лог…")
                            .action(Box::new(OpenLog))
                            .disabled(busy),
                    )
                    .item(
                        PopupMenuItem::new("Открыть в Windows-1251…")
                            .action(Box::new(OpenWindows1251))
                            .disabled(busy),
                    )
                    .item(
                        PopupMenuItem::new("Открыть лог в Windows-1251…")
                            .action(Box::new(OpenLogWindows1251))
                            .disabled(busy),
                    )
                    .separator()
                    .submenu(
                        "Недавние файлы",
                        window,
                        cx,
                        move |menu, _, _| {
                            if recent_entries.is_empty() {
                                return menu.item(PopupMenuItem::label("Недавних файлов пока нет"));
                            }
                            recent_entries
                                .iter()
                                .fold(menu.scrollable(true), |menu, entry| {
                                    let entry = entry.clone();
                                    let workspace = recent_workspace.clone();
                                    let name = file_name(&entry.path);
                                    let directory =
                                        entry.path.parent().map(display_path).unwrap_or_default();
                                    menu.item(
                                        PopupMenuItem::new(format!("{name} — {directory}"))
                                            .disabled(busy)
                                            .on_click(move |_, window, cx| {
                                                let _ = workspace.update(cx, |this, cx| {
                                                    this.open_paths(
                                                        vec![entry.path.clone()],
                                                        OpenMode::from_recent(&entry),
                                                        window,
                                                        cx,
                                                    );
                                                });
                                            }),
                                    )
                                })
                        },
                    )
                    .submenu(
                        "Закрытые вкладки",
                        window,
                        cx,
                        move |menu, _, _| {
                            if closed_entries.is_empty() {
                                return menu.item(PopupMenuItem::label("Закрытых вкладок нет"));
                            }
                            closed_entries.iter().fold(
                                menu.scrollable(true),
                                |menu, (id, label)| {
                                    let id = *id;
                                    let workspace = closed_workspace.clone();
                                    menu.item(
                                        PopupMenuItem::new(label.clone()).disabled(busy).on_click(
                                            move |_, window, cx| {
                                                let _ = workspace.update(cx, |this, cx| {
                                                    this.reopen_closed_tab(id, window, cx)
                                                });
                                            },
                                        ),
                                    )
                                },
                            )
                        },
                    )
                    .separator()
                    .item(
                        PopupMenuItem::new("Сохранить")
                            .action(Box::new(Save))
                            .disabled(!can_save),
                    )
                    .item(
                        PopupMenuItem::new("Сохранить как…")
                            .action(Box::new(SaveAs))
                            .disabled(!can_save),
                    )
                    .separator()
                    .item(
                        PopupMenuItem::new("Закрыть вкладку")
                            .action(Box::new(CloseTab))
                            .disabled(busy),
                    )
                    .item(
                        PopupMenuItem::new("Восстановить вкладку")
                            .action(Box::new(ReopenTab))
                            .disabled(!can_reopen),
                    )
                    .separator()
                    .item(
                        PopupMenuItem::new("Настроить открытие файлов в Windows…")
                            .action(Box::new(RegisterFileAssociations))
                            .disabled(busy || !cfg!(windows)),
                    )
            })
    }

    fn tools_menu_button(&self, cx: &Context<Self>) -> impl IntoElement {
        let doc = self.active_document();
        let log = doc.log.clone();
        let can_edit =
            self.busy.is_none() && !self.applying_update() && log.is_none() && !doc.placeholder;
        let wrap = log
            .as_ref()
            .map_or(self.wrap, |log| log.read(cx).wraps_lines());
        let focus = self.menu_focus(cx);
        Button::new("tools-menu")
            .small()
            .ghost()
            .label("Инструменты")
            .dropdown_menu(move |menu, window, cx| {
                let menu = menu
                    .action_context(focus.clone())
                    .min_w(px(280.))
                    .scrollable(true)
                    .label("Текст")
                    .item(
                        PopupMenuItem::new("Форматировать JSON")
                            .action(Box::new(FormatJson))
                            .disabled(!can_edit),
                    )
                    .item(
                        PopupMenuItem::new("Найти и заменить…")
                            .action(Box::new(ReplaceText))
                            .disabled(!can_edit),
                    )
                    .item(
                        PopupMenuItem::new("Перейти к строке…")
                            .action(Box::new(GoToLine))
                            .disabled(!can_edit),
                    )
                    .separator()
                    .label("Вид")
                    .item(
                        PopupMenuItem::new("Перенос строк")
                            .action(Box::new(ToggleWrap))
                            .checked(wrap),
                    )
                    .item(
                        PopupMenuItem::new("Увеличить текст")
                            .action(Box::new(ZoomIn))
                            .disabled(log.is_some()),
                    )
                    .item(
                        PopupMenuItem::new("Уменьшить текст")
                            .action(Box::new(ZoomOut))
                            .disabled(log.is_some()),
                    );
                if let Some(log) = &log {
                    log.update(cx, |viewer, cx| viewer.tools_menu(menu, window, cx))
                } else {
                    menu
                }
            })
    }
}
