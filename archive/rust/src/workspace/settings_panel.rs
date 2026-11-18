use gpui_kit::{
    component::{
        Sizable,
        button::{Button, ButtonVariants},
        input::{Input, InputEvent, InputState},
        label::Label,
    },
    *,
};

use super::Workspace;
use crate::{appearance, settings::parse_untitled_tab_limit};

pub(super) struct SettingsPanel {
    untitled_tab_limit: Entity<InputState>,
    error: Option<SharedString>,
    _submit: Subscription,
}

impl SettingsPanel {
    fn new(limit: usize, window: &mut Window, cx: &mut Context<Workspace>) -> Self {
        let untitled_tab_limit =
            cx.new(|cx| InputState::new(window, cx).default_value(limit.to_string()));
        let submit = cx.subscribe_in(&untitled_tab_limit, window, |this, _, event, window, cx| {
            if matches!(event, InputEvent::PressEnter { .. }) {
                this.save_settings(window, cx);
            }
        });
        Self {
            untitled_tab_limit,
            error: None,
            _submit: submit,
        }
    }

    fn render(&self, cx: &mut Context<Workspace>) -> impl IntoElement {
        div()
            .w_full()
            .max_w(px(460.))
            .mx_4()
            .p_4()
            .flex()
            .flex_col()
            .gap_3()
            .rounded_lg()
            .border_1()
            .border_color(rgb(appearance::ACCENT))
            .bg(rgb(appearance::SURFACE))
            .shadow_lg()
            .child(
                div()
                    .flex()
                    .items_center()
                    .child(
                        Label::new("Настройки")
                            .text_lg()
                            .text_color(rgb(appearance::FOREGROUND)),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("settings-close")
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip("Закрыть настройки")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.close_settings(window, cx)),
                            ),
                    ),
            )
            .child(Label::new("Лимит символов названия пустой вкладки (1–200)").text_sm())
            .child(Input::new(&self.untitled_tab_limit))
            .child(
                Label::new("Пустая вкладка без файла повторяет первую строку текста в пределах указанного лимита.")
                    .text_sm()
                    .text_color(rgb(appearance::MUTED)),
            )
            .children(self.error.iter().map(|error| {
                div()
                    .text_sm()
                    .text_color(rgb(appearance::ERROR))
                    .child(error.clone())
            }))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(div().flex_1())
                    .child(
                        Button::new("settings-cancel")
                            .small()
                            .ghost()
                            .label("Отмена")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.close_settings(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("settings-save")
                            .small()
                            .primary()
                            .label("Сохранить")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.save_settings(window, cx)),
                            ),
                    ),
            )
    }
}

impl Workspace {
    pub(super) fn open_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() || self.applying_update() {
            return;
        }
        let panel = SettingsPanel::new(self.settings.untitled_tab_limit, window, cx);
        panel.untitled_tab_limit.update(cx, |input, cx| {
            input.focus(window, cx);
            input.select_all(window, cx);
        });
        self.settings_panel = Some(panel);
        cx.notify();
    }

    pub(super) fn close_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.settings_panel.take().is_some() {
            self.restore_workspace_focus(window, cx);
            cx.notify();
        }
    }

    pub(super) fn save_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(panel) = &mut self.settings_panel else {
            return;
        };
        let raw = panel.untitled_tab_limit.read(cx).value().to_string();
        let limit = match parse_untitled_tab_limit(&raw) {
            Ok(limit) => limit,
            Err(error) => {
                panel.error = Some(error.into());
                cx.notify();
                return;
            }
        };
        self.settings.untitled_tab_limit = limit;
        self.refresh_untitled_tab_names(cx);
        self.settings_panel = None;
        self.restore_workspace_focus(window, cx);
        self.settings_pending = Some(self.settings.clone());
        self.flush_settings(window, cx);
        cx.notify();
    }

    fn flush_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.settings_writing {
            return;
        }
        let Some(settings) = self.settings_pending.take() else {
            return;
        };
        self.settings_writing = true;
        let task = cx.background_spawn(async move { settings.save() });
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |this, window, cx| {
                this.settings_writing = false;
                if let Err(error) = result
                    && this.settings_pending.is_none()
                {
                    this.error = Some(format!("Не удалось сохранить настройки. {error}").into());
                }
                if this.settings_pending.is_some() {
                    this.flush_settings(window, cx);
                }
                cx.notify();
            });
        })
        .detach();
    }

    pub(super) fn settings_panel(&self, cx: &mut Context<Self>) -> Option<impl IntoElement> {
        let panel = self.settings_panel.as_ref()?;
        Some(
            div()
                .id("settings-panel")
                .absolute()
                .inset_0()
                .flex()
                .items_center()
                .justify_center()
                .occlude()
                .tab_group()
                .bg(rgba(0x00000077))
                .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                .on_key_down(cx.listener(|this, event: &KeyDownEvent, window, cx| {
                    if event.keystroke.key == "escape" {
                        this.close_settings(window, cx);
                        cx.stop_propagation();
                    }
                }))
                .child(panel.render(cx)),
        )
    }
}
