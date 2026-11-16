use std::{cell::Cell, rc::Rc, sync::atomic::Ordering};

use gpui_kit::{
    base::{Scrollbar, ScrollbarHandle, ScrollbarMode, input::EditorScrollbarContext},
    component::{
        ActiveTheme, Disableable, Selectable, Sizable, WindowExt,
        button::{Button, ButtonVariants},
        dialog::DialogFooter,
        input::{Editor, Input, InputState},
        label::Label,
        menu::{PopupMenu, PopupMenuItem},
    },
    *,
};

use super::{
    LogEnd, LogFind, LogNext, LogPrevious, LogStart, LogViewer, find::progress_percent,
    viewport::Destination,
};

impl LogViewer {
    fn context_bar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let name = self
            .path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| self.path.display().to_string());
        let full_path = self.path.display().to_string();
        let auto_refresh = self.bookmark.auto_refresh;
        let follow_end = self.bookmark.follow_end;

        div()
            .h(px(36.))
            .flex()
            .flex_shrink_0()
            .items_center()
            .gap_2()
            .px_3()
            .bg(rgb(crate::appearance::SURFACE))
            .border_b_1()
            .border_color(rgb(crate::appearance::BORDER))
            .child(
                div()
                    .id("log-file-name")
                    .flex()
                    .min_w_0()
                    .max_w(px(180.))
                    .tooltip(move |window, cx| {
                        gpui_kit::component::tooltip::Tooltip::new(full_path.clone())
                            .build(window, cx)
                    })
                    .child(
                        Label::new(name)
                            .text_color(rgb(crate::appearance::FOREGROUND))
                            .truncate(),
                    ),
            )
            .child(
                div()
                    .flex_shrink_0()
                    .rounded_sm()
                    .border_1()
                    .border_color(rgb(crate::appearance::BORDER))
                    .px_1()
                    .text_xs()
                    .text_color(rgb(crate::appearance::MUTED))
                    .child("ТОЛЬКО ЧТЕНИЕ"),
            )
            .child(
                Button::new("log-refresh")
                    .small()
                    .ghost()
                    .label(if auto_refresh {
                        "Пауза"
                    } else {
                        "Продолжить"
                    })
                    .selected(auto_refresh)
                    .tooltip(if auto_refresh {
                        "Приостановить автообновление"
                    } else {
                        "Продолжить автообновление"
                    })
                    .on_click(cx.listener(|this, _, _, cx| {
                        this.bookmark.auto_refresh = !this.bookmark.auto_refresh;
                        cx.notify();
                    })),
            )
            .child(
                Button::new("log-follow")
                    .small()
                    .ghost()
                    .label("Следовать за концом")
                    .selected(follow_end)
                    .tooltip("Перемещать область просмотра при дописывании")
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.bookmark.follow_end = !this.bookmark.follow_end;
                        if this.bookmark.follow_end && this.bookmark.auto_refresh {
                            this.navigate(Destination::End, window, cx);
                        }
                        cx.notify();
                    })),
            )
            .child(div().flex_1().min_w_0())
            .child(
                Label::new(if auto_refresh {
                    "Автообновление: вкл."
                } else {
                    "Автообновление: пауза"
                })
                .text_sm()
                .text_color(rgb(if auto_refresh {
                    crate::appearance::SUCCESS
                } else {
                    crate::appearance::MUTED
                }))
                .truncate(),
            )
    }

    pub(crate) fn tools_menu(
        &self,
        mut menu: PopupMenu,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) -> PopupMenu {
        let viewer = cx.entity().downgrade();
        let refresh = viewer.clone();
        let index = viewer.clone();
        let cancel = viewer.clone();
        let hide = viewer.clone();
        let trim = viewer.clone();
        let can_cancel = self.can_cancel_trim();
        let indexing = self.indexing.is_some();
        let index_paused = self.index_paused;
        let index_complete = self.index.scanned_to() >= self.bookmark.size;
        let hidden = self.bookmark.hidden > 0;
        let available = self.available();

        menu = menu
            .label("ЛОГ")
            .item(
                PopupMenuItem::new("Обновить сейчас")
                    .disabled(self.writing_file() || self.preparing_trim)
                    .on_click(move |_, window, cx| {
                        let _ = refresh.update(cx, |this, cx| {
                            this.load_page(Destination::Restore, window, cx);
                        });
                    }),
            )
            .item(
                PopupMenuItem::new(if indexing {
                    "Отменить индексирование"
                } else if index_paused {
                    "Продолжить индексирование"
                } else {
                    "Индексировать поиск"
                })
                .disabled(
                    self.writing_file()
                        || self.preparing_trim
                        || (!indexing && !index_paused && index_complete),
                )
                .on_click(move |_, window, cx| {
                    let _ = index.update(cx, |this, cx| this.toggle_index(window, cx));
                }),
            )
            .item(
                PopupMenuItem::new("Отменить обрезку")
                    .disabled(!can_cancel)
                    .on_click(move |_, _, cx| {
                        let _ = cancel.update(cx, |this, cx| this.cancel_trim(cx));
                    }),
            )
            .separator()
            .item(
                PopupMenuItem::new("Скрыть начало…")
                    .disabled(!available)
                    .on_click(move |_, window, cx| {
                        let _ = hide.update(cx, |this, cx| {
                            this.open_boundary_dialog(false, window, cx);
                        });
                    }),
            )
            .item(
                PopupMenuItem::new("Удалить начало…")
                    .disabled(!available || self.writing_file() || self.preparing_trim)
                    .on_click(move |_, window, cx| {
                        let _ = trim.update(cx, |this, cx| {
                            this.open_boundary_dialog(true, window, cx);
                        });
                    }),
            );

        if hidden {
            menu = menu.item(
                PopupMenuItem::new("Показать весь файл")
                    .disabled(!available)
                    .on_click(move |_, window, cx| {
                        let _ = viewer.update(cx, |this, cx| this.reveal_start(window, cx));
                    }),
            );
        }

        menu
    }

    fn open_boundary_dialog(&mut self, remove: bool, window: &mut Window, cx: &mut Context<Self>) {
        let input = cx.new(|cx| {
            InputState::new(window, cx)
                .default_value(self.trim_percentage.read(cx).value().to_string())
                .placeholder("Процент от 0 до 100")
        });
        let invalid = Rc::new(Cell::new(false));
        let viewer = cx.entity().downgrade();
        let initial_focus = Cell::new(true);

        window.open_dialog(cx, move |dialog, window, cx| {
            if initial_focus.replace(false) {
                input.focus_handle(cx).focus(window, cx);
            }
            let message = if invalid.get() {
                "Введите целый процент от 0 до 100."
            } else if remove {
                "Перед записью RustText создаст полную резервную копию рядом с исходным файлом."
            } else {
                "Скрытый участок останется в исходном файле и его можно будет показать снова."
            };
            let viewer = viewer.clone();
            let invalid = invalid.clone();
            let submit_input = input.clone();
            dialog
                .title(if remove {
                    "Удалить начало файла"
                } else {
                    "Скрыть начало файла"
                })
                .w(px(410.))
                .child(
                    div()
                        .flex()
                        .flex_col()
                        .gap_2()
                        .child(Label::new("Начало файла, %").text_sm())
                        .child(Input::new(&input))
                        .child(
                            Label::new(message)
                                .text_sm()
                                .text_color(cx.theme().muted_foreground),
                        ),
                )
                .footer(
                    DialogFooter::new()
                        .child(
                            Button::new("log-boundary-cancel")
                                .label("Отмена")
                                .on_click(|_, window, cx| window.close_dialog(cx)),
                        )
                        .child(
                            Button::new("log-boundary-confirm")
                                .primary()
                                .label(if remove {
                                    "Продолжить"
                                } else {
                                    "Скрыть"
                                })
                                .on_click(|_, window, cx| {
                                    window.dispatch_action(
                                        Box::new(gpui_kit::component::dialog::Confirm {
                                            secondary: false,
                                        }),
                                        cx,
                                    )
                                }),
                        ),
                )
                .on_ok(move |_, window, cx| {
                    let Some(percent) = submit_input
                        .read(cx)
                        .value()
                        .trim()
                        .parse::<u8>()
                        .ok()
                        .filter(|percent| *percent <= 100 && (!remove || *percent > 0))
                    else {
                        invalid.set(true);
                        submit_input.focus_handle(cx).focus(window, cx);
                        window.refresh();
                        return false;
                    };
                    let _ = viewer.update(cx, |this, cx| {
                        this.trim_percentage.update(cx, |percentage, cx| {
                            percentage.set_value(percent.to_string(), window, cx);
                        });
                        if remove {
                            this.confirm_trim(window, cx);
                        } else {
                            this.hide_start(window, cx);
                        }
                    });
                    true
                })
        });
    }

    pub(crate) fn search_visible(&self) -> bool {
        self.search.is_some()
    }

    pub(super) fn scrollbar(
        scroll: EditorScrollbarContext,
        viewer: WeakEntity<Self>,
        scroll_bottom: &Cell<Pixels>,
    ) -> impl IntoElement {
        let bottom =
            (scroll.viewport_bounds().size.height - scroll.content_size().height).min(px(0.));
        scroll_bottom.set(bottom);
        div()
            .size_full()
            .relative()
            .child(
                Scrollbar::new(&scroll)
                    .axis(scroll.axis())
                    .mode(ScrollbarMode::Always)
                    .styles(|_| crate::appearance::scrollbar_styles()),
            )
            .child(
                canvas(
                    |bounds, window, _| window.insert_hitbox(bounds, HitboxBehavior::Normal),
                    move |_, hitbox, window, _| {
                        window.on_mouse_event(move |event: &ScrollEvent, phase, window, cx| {
                            if !phase.capture() || !hitbox.should_handle_scroll(window) {
                                return;
                            }
                            let delta = event.delta.pixel_delta(px(1.));
                            if delta.y == px(0.) || delta.x.abs() > delta.y.abs() {
                                return;
                            }
                            let forward = delta.y < px(0.);
                            let offset = scroll.offset().y;
                            // The editor consumes wheel events even at its scroll limits.
                            let edge = if forward { bottom } else { px(0.) };
                            if (offset - edge).abs() <= px(0.5) {
                                let _ = viewer.update(cx, |viewer, cx| {
                                    if viewer.turn_page(forward, window, cx) {
                                        cx.stop_propagation();
                                    }
                                });
                            }
                        });
                    },
                )
                .absolute()
                .size_full(),
            )
    }

    fn toolbar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let enabled = self.available();
        let at_end = self.page.as_ref().is_none_or(|page| page.eof);
        div()
            .h(px(34.))
            .flex()
            .flex_shrink_0()
            .items_center()
            .gap_1()
            .px_2()
            .bg(rgb(crate::appearance::SURFACE))
            .border_t_1()
            .border_color(rgb(crate::appearance::BORDER))
            .child(
                Button::new("log-start")
                    .small()
                    .ghost()
                    .label("Начало")
                    .disabled(!enabled)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.navigate(Destination::Start, window, cx)
                    })),
            )
            .child(
                Button::new("log-prev")
                    .small()
                    .ghost()
                    .label("Назад")
                    .disabled(!enabled || self.bookmark.offset <= self.bookmark.hidden)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.turn_page(false, window, cx);
                    })),
            )
            .child(
                Button::new("log-next")
                    .small()
                    .ghost()
                    .label("Дальше")
                    .disabled(!enabled || at_end)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.turn_page(true, window, cx);
                    })),
            )
            .child(
                Button::new("log-end")
                    .small()
                    .ghost()
                    .label("Конец")
                    .disabled(!enabled)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.navigate(Destination::End, window, cx)
                    })),
            )
            .child(Label::new("Перейти, %").text_sm())
            .child(
                div()
                    .w(px(62.))
                    .child(Input::new(&self.percentage).disabled(!enabled)),
            )
            .child(
                Button::new("log-percent")
                    .small()
                    .ghost()
                    .label("Перейти")
                    .disabled(!enabled)
                    .on_click(cx.listener(|this, _, window, cx| this.jump_percent(window, cx))),
            )
    }

    fn status(&self) -> impl IntoElement {
        let message = if let Some(progress) = &self.trimming {
            let done = progress.done.load(Ordering::Relaxed);
            let total = progress.total.load(Ordering::Relaxed);
            format!(
                "Обрезка… {}%. Отмена доступна до изменения исходника.",
                progress_percent(done, total)
            )
            .into()
        } else if let Some(progress) = &self.preparation {
            format!(
                "Поиск границы… {}%",
                progress_percent(
                    progress.done.load(Ordering::Relaxed),
                    progress.total.load(Ordering::Relaxed)
                )
            )
            .into()
        } else if self.preparing_trim {
            "Проверка границы…".into()
        } else if self.reading {
            "Чтение лога…".into()
        } else if let Some(notice) = &self.notice {
            notice.clone()
        } else if let Some(progress) = &self.indexing {
            format!(
                "Индексирование… {}%",
                progress_percent(
                    progress.done.load(Ordering::Relaxed),
                    progress.total.load(Ordering::Relaxed)
                )
            )
            .into()
        } else if self.index.scanned_to() > 0 {
            format!("Строк в индексе: {}", self.index.line_count()).into()
        } else {
            "Просмотр лога".into()
        };
        let page_end = self.page.as_ref().map_or(0, |page| page.next_offset);
        let mut position = format!(
            "Байты {}–{} из {}",
            self.bookmark.offset, page_end, self.bookmark.size
        );
        if self.bookmark.hidden > 0 {
            position.push_str(&format!(" · скрыто {}", self.bookmark.hidden));
        }
        div()
            .flex()
            .items_center()
            .justify_between()
            .flex_shrink_0()
            .h(px(29.))
            .gap_3()
            .px_3()
            .bg(rgb(crate::appearance::SURFACE))
            .border_t_1()
            .border_color(rgb(crate::appearance::BORDER))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .child(Label::new(message).text_sm().truncate()),
            )
            .child(Label::new(position).text_sm())
            .child(Label::new(self.encoding.label()).text_sm())
    }
}

impl Render for LogViewer {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("log-viewer")
            .key_context("LogViewer")
            .size_full()
            .flex()
            .flex_col()
            .on_action(cx.listener(|this, _: &LogStart, window, cx| {
                this.navigate(Destination::Start, window, cx)
            }))
            .on_action(cx.listener(|this, _: &LogEnd, window, cx| {
                this.navigate(Destination::End, window, cx)
            }))
            .on_action(cx.listener(|this, _: &LogPrevious, window, cx| {
                this.scroll_screen(false, window, cx);
            }))
            .on_action(cx.listener(|this, _: &LogNext, window, cx| {
                this.scroll_screen(true, window, cx);
            }))
            .on_action(cx.listener(|this, _: &LogFind, window, cx| this.open_search(window, cx)))
            .child(self.context_bar(cx))
            .child(
                div().flex_1().min_h_0().w_full().overflow_hidden().child(
                    Editor::new(&self.viewer)
                        .readonly(true)
                        .bordered(false)
                        .h_full()
                        .w_full()
                        .font_family("Consolas")
                        .text_size(px(15.)),
                ),
            )
            .children(self.search_panel(window, cx))
            .child(self.toolbar(cx))
            .child(self.status())
    }
}
