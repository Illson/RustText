mod native;

use gpui_kit::{
    component::{
        Selectable, Sizable,
        button::{Button, ButtonVariants},
        input::{Input, InputEvent, InputState},
        label::Label,
    },
    *,
};

use super::Workspace;
use crate::appearance;
use native::{FoundWindow, bring_to_front, find_windows};

pub(super) struct WindowFinder {
    query: Entity<InputState>,
    by_pid: bool,
    results: Vec<FoundWindow>,
    message: Option<&'static str>,
    _input_events: Subscription,
}

impl WindowFinder {
    fn new(window: &mut Window, cx: &mut Context<Workspace>) -> Self {
        let query =
            cx.new(|cx| InputState::new(window, cx).placeholder("Введите PID или часть заголовка"));
        let input_events = cx.subscribe_in(&query, window, |this, _, event, _, cx| match event {
            InputEvent::PressEnter { .. } => this.search_windows(cx),
            InputEvent::Change => {
                if let Some(panel) = &mut this.window_finder {
                    panel.results.clear();
                    panel.message = None;
                    cx.notify();
                }
            }
            _ => {}
        });
        Self {
            query,
            by_pid: true,
            results: Vec::new(),
            message: None,
            _input_events: input_events,
        }
    }

    fn render(&self, cx: &mut Context<Workspace>) -> impl IntoElement {
        div()
            .w(relative(0.92))
            .h(relative(0.9))
            .max_w(px(680.))
            .max_h(px(460.))
            .p_4()
            .flex()
            .flex_col()
            .gap_2()
            .rounded_lg()
            .border_1()
            .border_color(rgb(appearance::ACCENT))
            .bg(rgb(appearance::SURFACE))
            .child(
                div()
                    .flex()
                    .items_center()
                    .child(Label::new("Вывод окна на передний план").text_lg())
                    .child(div().flex_1())
                    .child(
                        Button::new("window-finder-close")
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip("Закрыть поиск окон")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.close_window_finder(window, cx)
                            })),
                    ),
            )
            .child(
                div()
                    .flex()
                    .gap_2()
                    .children([(true, "По PID"), (false, "По заголовку")].map(
                        |(by_pid, label)| {
                            Button::new(("window-finder-mode", by_pid as usize))
                                .small()
                                .label(label)
                                .selected(self.by_pid == by_pid)
                                .on_click(cx.listener(move |this, _, window, cx| {
                                    if let Some(panel) = &mut this.window_finder {
                                        if panel.by_pid != by_pid {
                                            panel.by_pid = by_pid;
                                            panel.results.clear();
                                            panel.message = None;
                                        }
                                        panel.query.update(cx, |input, cx| input.focus(window, cx));
                                        cx.notify();
                                    }
                                }))
                        },
                    )),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(div().flex_1().min_w_0().child(Input::new(&self.query)))
                    .child(
                        Button::new("window-finder-search")
                            .primary()
                            .label("Найти")
                            .on_click(cx.listener(|this, _, _, cx| this.search_windows(cx))),
                    ),
            )
            .child(
                div()
                    .min_h(px(18.))
                    .children(self.message.map(|message| Label::new(message).text_sm())),
            )
            .child(
                div()
                    .id("window-finder-results")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .children(self.results.iter().enumerate().map(|(index, result)| {
                        let target = result.clone();
                        let title = if result.title.is_empty() {
                            "Окно без заголовка"
                        } else {
                            &result.title
                        };
                        div()
                            .w_full()
                            .flex()
                            .flex_shrink_0()
                            .items_center()
                            .gap_2()
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .overflow_hidden()
                                    .child(Label::new(title.to_owned()).truncate()),
                            )
                            .child(Label::new(format!("PID {}", result.pid)).text_sm())
                            .child(
                                Button::new(("window-finder-activate", index))
                                    .small()
                                    .label("Вывести")
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.activate_found_window(&target, window, cx);
                                    })),
                            )
                    })),
            )
    }
}

impl Workspace {
    pub(super) fn open_window_finder(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() || self.applying_update() {
            return;
        }
        let panel = WindowFinder::new(window, cx);
        panel.query.update(cx, |input, cx| input.focus(window, cx));
        self.window_finder = Some(panel);
        cx.notify();
    }

    fn close_window_finder(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.window_finder.take().is_some() {
            self.restore_workspace_focus(window, cx);
            cx.notify();
        }
    }

    fn search_windows(&mut self, cx: &mut Context<Self>) {
        if let Some(panel) = &mut self.window_finder {
            let query = panel.query.read(cx).value();
            panel.message = match find_windows(&query, panel.by_pid) {
                Ok(results) => {
                    panel.results = results;
                    panel.results.is_empty().then_some("Окна не найдены.")
                }
                Err(message) => {
                    panel.results.clear();
                    Some(message)
                }
            };
            cx.notify();
        }
    }

    fn activate_found_window(
        &mut self,
        target: &FoundWindow,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Some(panel) = &mut self.window_finder {
            match bring_to_front(target) {
                Ok(()) => self.close_window_finder(window, cx),
                Err(message) => {
                    panel.message = Some(message);
                    cx.notify();
                }
            }
        }
    }

    pub(super) fn window_finder_panel(&self, cx: &mut Context<Self>) -> Option<impl IntoElement> {
        let panel = self.window_finder.as_ref()?;
        Some(
            div()
                .id("window-finder")
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
                        this.close_window_finder(window, cx);
                        cx.stop_propagation();
                    }
                }))
                .child(panel.render(cx)),
        )
    }
}
