use std::sync::Arc;

use gpui_kit::{
    component::{
        Disableable, Selectable, Sizable,
        button::{Button, ButtonVariants},
        input::{Editor, EditorState, InputEvent},
        label::Label,
    },
    *,
};

use super::Workspace;
use crate::{appearance, list_compare::ComparedLists};

pub(super) struct ListComparison {
    left: Entity<EditorState>,
    right: Entity<EditorState>,
    output: Entity<EditorState>,
    result: ComparedLists,
    selected: usize,
    current: bool,
    running: bool,
    token: Arc<()>,
    _changes: [Subscription; 2],
}

impl ListComparison {
    fn new(window: &mut Window, cx: &mut Context<Workspace>) -> Self {
        let left = cx.new(|cx| EditorState::new(window, cx).line_number(true));
        let right = cx.new(|cx| EditorState::new(window, cx).line_number(true));
        let output = cx.new(|cx| EditorState::new(window, cx).line_number(true));
        let changes = [left.clone(), right.clone()].map(|editor| {
            cx.subscribe(&editor, |this, _, event, cx| {
                if matches!(event, InputEvent::Change)
                    && let Some(panel) = &mut this.list_comparison
                {
                    panel.current = false;
                    cx.notify();
                }
            })
        });
        Self {
            left,
            right,
            output,
            result: ComparedLists::default(),
            selected: 0,
            current: false,
            running: false,
            token: Arc::new(()),
            _changes: changes,
        }
    }

    fn selected_text(&self) -> String {
        match self.selected {
            1 => &self.result.left_only,
            2 => &self.result.right_only,
            _ => &self.result.common,
        }
        .join("\n")
    }

    fn render(&self, cx: &mut Context<Workspace>) -> impl IntoElement {
        let results = [
            ("compare-common", "Общие", self.result.common.len()),
            ("compare-left", "Только слева", self.result.left_only.len()),
            (
                "compare-right",
                "Только справа",
                self.result.right_only.len(),
            ),
        ];
        div()
            .w(relative(0.92))
            .h(relative(0.9))
            .max_w(px(1100.))
            .max_h(px(760.))
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
                    .child(Label::new("Сравнение списков").text_lg())
                    .child(div().flex_1())
                    .child(
                        Button::new("compare-close")
                            .small()
                            .ghost()
                            .label("×")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.close_list_comparison(window, cx)
                            })),
                    ),
            )
            .child(
                Label::new(
                    "Одна запись на строку. Регистр и пробелы учитываются; повторы объединяются.",
                )
                .text_sm(),
            )
            .child(div().flex().flex_1().min_h_0().gap_2().children(
                [("Левый список", &self.left), ("Правый список", &self.right)].map(
                    |(label, editor)| {
                        div()
                            .flex()
                            .flex_col()
                            .flex_1()
                            .min_w_0()
                            .gap_1()
                            .child(Label::new(label).text_sm())
                            .child(
                                Editor::new(editor)
                                    .readonly(self.running)
                                    .size_full()
                                    .font_family("Consolas"),
                            )
                    },
                ),
            ))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(
                        Button::new("compare-run")
                            .small()
                            .label(if self.running {
                                "Сравнение…"
                            } else {
                                "Сравнить"
                            })
                            .disabled(self.running)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.compare_lists(window, cx)),
                            ),
                    )
                    .children(results.into_iter().enumerate().map(
                        |(index, (id, label, count))| {
                            Button::new(id)
                                .small()
                                .ghost()
                                .label(format!("{label}: {count}"))
                                .selected(self.selected == index)
                                .disabled(!self.current || self.running)
                                .on_click(cx.listener(move |this, _, window, cx| {
                                    this.select_comparison_result(index, window, cx)
                                }))
                        },
                    )),
            )
            .child(
                div().flex_1().min_h_0().child(
                    Editor::new(&self.output)
                        .readonly(true)
                        .size_full()
                        .font_family("Consolas"),
                ),
            )
            .child(
                div()
                    .flex()
                    .gap_2()
                    .child(
                        Label::new(if self.current {
                            "Результат сравнения"
                        } else {
                            "Нажмите «Сравнить», чтобы обновить результат"
                        })
                        .text_sm(),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("compare-copy")
                            .small()
                            .ghost()
                            .label("Копировать")
                            .disabled(!self.current)
                            .on_click(cx.listener(|this, _, _, cx| {
                                if let Some(panel) = &this.list_comparison {
                                    cx.write_to_clipboard(ClipboardItem::new_string(
                                        panel.selected_text(),
                                    ));
                                }
                            })),
                    )
                    .child(
                        Button::new("compare-open-result")
                            .small()
                            .label("Во вкладку")
                            .disabled(!self.current)
                            .on_click(cx.listener(|this, _, window, cx| {
                                if let Some(panel) = this.list_comparison.take() {
                                    this.open_generated_document(panel.selected_text(), window, cx);
                                }
                            })),
                    ),
            )
    }
}

impl Workspace {
    pub(super) fn open_list_comparison(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() || self.applying_update() {
            return;
        }
        self.list_comparison = Some(ListComparison::new(window, cx));
        if let Some(panel) = &self.list_comparison {
            panel.left.update(cx, |editor, cx| editor.focus(window, cx));
        }
        cx.notify();
    }

    fn close_list_comparison(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.list_comparison = None;
        self.select(self.active, window, cx);
        cx.notify();
    }

    pub(super) fn list_comparison_panel(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        self.list_comparison.as_ref().map(|panel| {
            div()
                .id("list-comparison")
                .absolute()
                .inset_0()
                .flex()
                .items_center()
                .justify_center()
                .occlude()
                .tab_group()
                .bg(rgb(appearance::BACKGROUND).opacity(0.82))
                .on_key_down(cx.listener(|this, event: &KeyDownEvent, window, cx| {
                    if event.keystroke.key == "escape" {
                        this.close_list_comparison(window, cx);
                        cx.stop_propagation();
                    }
                }))
                .child(panel.render(cx))
                .into_any_element()
        })
    }

    fn compare_lists(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(panel) = &mut self.list_comparison else {
            return;
        };
        if panel.running {
            return;
        }
        let left = panel.left.read(cx).text().clone();
        let right = panel.right.read(cx).text().clone();
        let token = panel.token.clone();
        panel.running = true;
        let task = cx.background_spawn(async move {
            crate::list_compare::compare(&left.to_string(), &right.to_string())
        });
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |this, window, cx| {
                let Some(panel) = &mut this.list_comparison else {
                    return;
                };
                if !Arc::ptr_eq(&panel.token, &token) {
                    return;
                }
                panel.result = result;
                panel.current = true;
                panel.running = false;
                panel.selected = 0;
                let text = panel.selected_text();
                panel
                    .output
                    .update(cx, |editor, cx| editor.set_value(text, window, cx));
                cx.notify();
            });
        })
        .detach();
    }

    fn select_comparison_result(
        &mut self,
        index: usize,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Some(panel) = &mut self.list_comparison {
            panel.selected = index;
            let text = panel.selected_text();
            panel
                .output
                .update(cx, |editor, cx| editor.set_value(text, window, cx));
            cx.notify();
        }
    }
}
