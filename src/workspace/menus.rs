use gpui_kit::{
    component::{
        Disableable, Selectable, Sizable,
        button::{Button, ButtonVariants},
        label::Label,
        menu::{DropdownMenu, PopupMenuItem},
    },
    *,
};

use super::{Workspace, display_path, file_name, view::*};
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
        let showing_home = self.showing_home();
        let searching = !showing_home
            && (self.search.is_some()
                || self
                    .active_document_opt()
                    .and_then(|doc| doc.log.as_ref())
                    .is_some_and(|log| log.read(cx).search_visible()));
        div()
            .h(px(34.))
            .flex_1()
            .min_w_0()
            .flex()
            .flex_shrink_0()
            .items_center()
            .gap_1()
            .px_3()
            .bg(rgb(appearance::SURFACE))
            .border_b_1()
            .border_color(rgb(appearance::BORDER))
            .child(
                Button::new("open-home")
                    .small()
                    .ghost()
                    .selected(showing_home)
                    .tooltip("История файлов")
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_2()
                            .text_color(rgb(appearance::ACCENT))
                            .child(img(logo).w(px(22.)).h(px(22.)).flex_shrink_0())
                            .child(Label::new("RustText")),
                    )
                    .on_click(cx.listener(|this, _, window, cx| this.toggle_home(window, cx))),
            )
            .child(self.file_menu_button(cx))
            .child(self.tools_menu_button(cx))
            .child(
                Button::new("find-text")
                    .small()
                    .ghost()
                    .disabled(showing_home)
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
        if self.showing_home() {
            return self.fallback_focus.clone();
        }
        let Some(doc) = self.active_document_opt() else {
            return self.fallback_focus.clone();
        };
        doc.log.as_ref().map_or_else(
            || doc.editor.read(cx).focus_handle(cx),
            |log| log.read(cx).focus_handle(cx),
        )
    }

    fn file_menu_button(&self, cx: &Context<Self>) -> impl IntoElement {
        let busy = self.busy.is_some() || self.applying_update();
        let showing_home = self.showing_home();
        let doc = (!showing_home)
            .then(|| self.active_document_opt())
            .flatten();
        let can_save = !busy && doc.is_some_and(|doc| doc.log.is_none() && !doc.placeholder);
        let closed_entries = self.closed_tab_entries();
        let can_reopen = !busy && !closed_entries.is_empty();
        let recent_entries = self
            .recent
            .entries()
            .iter()
            .take(16)
            .cloned()
            .collect::<Vec<_>>();
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
                                                    this.open_recent(entry.clone(), window, cx);
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
                            .disabled(busy || showing_home),
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
        let showing_home = self.showing_home();
        let doc = (!showing_home)
            .then(|| self.active_document_opt())
            .flatten();
        let log = doc.and_then(|doc| doc.log.clone());
        let can_edit = self.busy.is_none()
            && !self.applying_update()
            && !showing_home
            && doc.is_some_and(|doc| doc.log.is_none() && !doc.placeholder);
        let wrap = log
            .as_ref()
            .map_or(self.wrap, |log| log.read(cx).wraps_lines());
        let focus = self.menu_focus(cx);
        let generator_workspace = cx.entity().downgrade();
        let comparison_workspace = cx.entity().downgrade();
        let versions_workspace = cx.entity().downgrade();
        let json_workspace = cx.entity().downgrade();
        let settings_workspace = cx.entity().downgrade();
        let json_disabled = doc.is_some_and(|doc| doc.json_validation_disabled);
        let can_show_versions = can_edit && doc.is_some_and(|doc| doc.file.is_some());
        let can_generate = self.busy.is_none() && !self.applying_update();
        Button::new("tools-menu")
            .small()
            .ghost()
            .label("Инструменты")
            .dropdown_menu(move |menu, window, cx| {
                let generator_workspace = generator_workspace.clone();
                let comparison_workspace = comparison_workspace.clone();
                let versions_workspace = versions_workspace.clone();
                let json_workspace = json_workspace.clone();
                let settings_workspace = settings_workspace.clone();
                let menu = menu
                    .action_context(focus.clone())
                    .min_w(px(280.))
                    .scrollable(true)
                    .label("Текст")
                    .item(
                        PopupMenuItem::new("Версии файла…")
                            .disabled(!can_show_versions)
                            .on_click(move |_, window, cx| {
                                let _ = versions_workspace
                                    .update(cx, |this, cx| this.open_versions(window, cx));
                            }),
                    )
                    .item(
                        PopupMenuItem::new(if json_disabled {
                            "Включить проверку JSON"
                        } else {
                            "Отключить проверку JSON"
                        })
                        .disabled(!can_edit)
                        .on_click(move |_, _, cx| {
                            let _ = json_workspace
                                .update(cx, |this, cx| this.toggle_json_validation(cx));
                        }),
                    )
                    .item(
                        PopupMenuItem::new("Сравнить списки…")
                            .disabled(!can_generate)
                            .on_click(move |_, window, cx| {
                                let _ = comparison_workspace
                                    .update(cx, |this, cx| this.open_list_comparison(window, cx));
                            }),
                    )
                    .item(
                        PopupMenuItem::new("Генератор строк…")
                            .disabled(!can_generate)
                            .on_click(move |_, window, cx| {
                                let _ = generator_workspace.update(cx, |this, cx| {
                                    this.open_template_generator(window, cx)
                                });
                            }),
                    )
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
                            .disabled(showing_home)
                            .checked(wrap),
                    )
                    .item(
                        PopupMenuItem::new("Увеличить текст")
                            .action(Box::new(ZoomIn))
                            .disabled(showing_home || log.is_some()),
                    )
                    .item(
                        PopupMenuItem::new("Уменьшить текст")
                            .action(Box::new(ZoomOut))
                            .disabled(showing_home || log.is_some()),
                    )
                    .separator()
                    .item(
                        PopupMenuItem::new("Настройки…")
                            .disabled(!can_generate)
                            .on_click(move |_, window, cx| {
                                let _ = settings_workspace
                                    .update(cx, |this, cx| this.open_settings(window, cx));
                            }),
                    );
                if let Some(log) = &log {
                    log.update(cx, |viewer, cx| viewer.tools_menu(menu, window, cx))
                } else {
                    menu
                }
            })
    }
}
