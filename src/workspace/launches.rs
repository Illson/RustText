use std::time::Duration;

use gpui_kit::*;

use super::Workspace;
use crate::single_instance::SingleInstance;

impl Workspace {
    pub(super) fn listen_for_launches(
        &self,
        instance: SingleInstance,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let executor = cx.background_executor().clone();
        cx.spawn_in(window, async move |this, cx| {
            loop {
                executor.timer(Duration::from_millis(100)).await;
                let Ok(keep_listening) = this.update_in(cx, |this, window, cx| {
                    if this.busy.is_some() {
                        return true;
                    }
                    match instance.try_receive() {
                        Ok(Some(request)) => {
                            if request.activate {
                                cx.activate(true);
                                window.activate_window();
                            }
                            this.open_paths(
                                request.absolute_paths(),
                                super::file_actions::OpenMode::NORMAL,
                                window,
                                cx,
                            );
                            true
                        }
                        Ok(None) => true,
                        Err(error) => {
                            this.error = Some(
                                format!("Не удалось принять файлы нового запуска. {error}").into(),
                            );
                            cx.notify();
                            false
                        }
                    }
                }) else {
                    break;
                };
                if !keep_listening {
                    break;
                }
            }
        })
        .detach();
    }
}
