use gpui_kit::{
    component::{
        Disableable, Sizable,
        button::{Button, ButtonVariants},
        label::Label,
    },
    *,
};

use super::{Workspace, session_runtime::AfterShutdown};
use crate::updater::{self, Release, StagedUpdate};

#[derive(Default)]
pub(super) struct Updates {
    state: UpdateState,
    notice: Option<SharedString>,
}

#[derive(Default)]
enum UpdateState {
    #[default]
    Idle,
    Checking,
    Available(Release),
    Downloading,
    Ready {
        version: String,
        staged: StagedUpdate,
    },
    Applying,
}

impl Workspace {
    pub(super) fn check_for_updates(
        &mut self,
        manual: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !matches!(self.updates.state, UpdateState::Idle) {
            return;
        }
        self.updates.state = UpdateState::Checking;
        self.updates.notice = manual.then(|| "Проверка обновлений…".into());
        let job = cx.background_spawn(async { updater::check() });
        cx.spawn_in(window, async move |this, cx| {
            let result = job.await;
            let _ = this.update_in(cx, |this, _, cx| {
                this.updates.state = UpdateState::Idle;
                this.updates.notice = match result {
                    Ok(Some(release)) => {
                        this.updates.state = UpdateState::Available(release);
                        None
                    }
                    Ok(None) if manual => {
                        Some(format!("Установлена актуальная версия {}.", updater::VERSION).into())
                    }
                    Err(error) if manual => {
                        Some(format!("Не удалось проверить обновления. {error}").into())
                    }
                    _ => None,
                };
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }

    fn download_update(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let UpdateState::Available(release) = &self.updates.state else {
            return;
        };
        let release = release.clone();
        self.updates.state = UpdateState::Downloading;
        self.updates.notice = None;
        let job = cx.background_spawn(async move {
            let version = release.version.to_string();
            updater::stage(&release).map(|staged| UpdateState::Ready { version, staged })
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = job.await;
            let _ = this.update_in(cx, |this, _, cx| {
                match result {
                    Ok(state) => this.updates.state = state,
                    Err(error) => {
                        this.updates.state = UpdateState::Idle;
                        this.updates.notice =
                            Some(format!("Не удалось скачать обновление. {error}").into());
                    }
                }
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }

    fn restart_for_update(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some()
            || self.log_is_writing(cx)
            || !matches!(self.updates.state, UpdateState::Ready { .. })
        {
            return;
        }
        if self.session.is_none() && self.documents.iter().any(|doc| doc.dirty) {
            self.updates.notice =
                Some("Сессия недоступна. Сохраните открытые документы перед обновлением.".into());
            cx.notify();
            return;
        }
        self.updates.notice = None;
        self.begin_session_shutdown(AfterShutdown::InstallUpdate, window, cx);
    }

    pub(super) fn finish_update_shutdown(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let UpdateState::Ready { staged, .. } = std::mem::take(&mut self.updates.state) else {
            return;
        };
        self.updates.state = UpdateState::Applying;
        self.busy = Some("Запуск обновления…");
        let job = cx.background_spawn(async move { staged.launch() });
        cx.spawn_in(window, async move |this, cx| {
            let result = job.await;
            let _ = this.update_in(cx, |this, window, cx| match result {
                Ok(()) => window.remove_window(),
                Err(error) => {
                    this.busy = None;
                    this.updates.state = UpdateState::Idle;
                    this.updates.notice =
                        Some(format!("Не удалось запустить обновление. {error}").into());
                    cx.notify();
                }
            });
        })
        .detach();
        cx.notify();
    }

    pub(super) fn applying_update(&self) -> bool {
        matches!(self.updates.state, UpdateState::Applying)
    }

    pub(super) fn defer_restart_after_session_error(&mut self, cx: &mut Context<Self>) {
        self.updates.notice = self.session_error.clone();
        cx.notify();
    }

    pub(super) fn update_button(&self, cx: &mut Context<Self>) -> impl IntoElement {
        Button::new("check-updates")
            .small()
            .ghost()
            .label("Обновления")
            .tooltip(format!(
                "Проверить обновления · RustText {}",
                updater::VERSION
            ))
            .disabled(self.busy.is_some() || !matches!(self.updates.state, UpdateState::Idle))
            .on_click(cx.listener(|this, _, window, cx| this.check_for_updates(true, window, cx)))
    }

    pub(super) fn update_panel(&self, cx: &mut Context<Self>) -> Option<impl IntoElement> {
        let message = self
            .updates
            .notice
            .clone()
            .or_else(|| match &self.updates.state {
                UpdateState::Available(release) => {
                    Some(format!("Доступен RustText {}.", release.version).into())
                }
                UpdateState::Downloading => Some("Загрузка и проверка обновления…".into()),
                UpdateState::Applying => Some("Запуск обновления…".into()),
                UpdateState::Ready { version, .. } => {
                    Some(format!("RustText {version} готов к установке.").into())
                }
                _ => None,
            })?;
        let download = matches!(self.updates.state, UpdateState::Available(_));
        let ready = matches!(self.updates.state, UpdateState::Ready { .. });
        let dismissible = download || ready || matches!(self.updates.state, UpdateState::Idle);
        Some(
            div()
                .flex()
                .flex_shrink_0()
                .min_h(px(36.0))
                .items_center()
                .gap_2()
                .px_3()
                .bg(rgb(0x18202b))
                .border_b_1()
                .border_color(rgb(0x293442))
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .child(Label::new(message).text_sm()),
                )
                .children(download.then(|| {
                    Button::new("download-update")
                        .small()
                        .outline()
                        .label("Скачать")
                        .disabled(self.busy.is_some())
                        .on_click(
                            cx.listener(|this, _, window, cx| this.download_update(window, cx)),
                        )
                }))
                .children(ready.then(|| {
                    Button::new("restart-for-update")
                        .small()
                        .outline()
                        .label("Перезапустить")
                        .disabled(self.busy.is_some() || self.log_is_writing(cx))
                        .on_click(
                            cx.listener(|this, _, window, cx| this.restart_for_update(window, cx)),
                        )
                }))
                .children(dismissible.then(|| {
                    Button::new("dismiss-update")
                        .small()
                        .ghost()
                        .label(if download || ready {
                            "Позже"
                        } else {
                            "Закрыть"
                        })
                        .disabled(self.busy.is_some())
                        .on_click(cx.listener(|this, _, _, cx| {
                            this.updates = Updates::default();
                            cx.notify();
                        }))
                })),
        )
    }
}
