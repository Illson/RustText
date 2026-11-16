use gpui_kit::{
    component::{
        Disableable, Sizable,
        button::{Button, ButtonVariants},
        input::{Input, InputEvent, InputState},
        label::Label,
    },
    *,
};

use super::Workspace;
use crate::{appearance, template};

pub(super) struct TemplateGenerator {
    template: Entity<InputState>,
    rows: Entity<InputState>,
    error: Option<SharedString>,
    generating: bool,
    _submit: [Subscription; 2],
}

impl TemplateGenerator {
    fn new(window: &mut Window, cx: &mut Context<Workspace>) -> Self {
        let template =
            cx.new(|cx| InputState::new(window, cx).default_value("strokna {int_from_5}"));
        let rows = cx.new(|cx| InputState::new(window, cx).default_value("10"));
        let submit_template = cx.subscribe_in(&template, window, |this, _, event, window, cx| {
            if matches!(event, InputEvent::PressEnter { .. }) {
                this.generate_template(window, cx);
            }
        });
        let submit_rows = cx.subscribe_in(&rows, window, |this, _, event, window, cx| {
            if matches!(event, InputEvent::PressEnter { .. }) {
                this.generate_template(window, cx);
            }
        });
        Self {
            template,
            rows,
            error: None,
            generating: false,
            _submit: [submit_template, submit_rows],
        }
    }

    fn render(&self, cx: &mut Context<Workspace>) -> impl IntoElement {
        let generating = self.generating;
        div()
            .w_full()
            .max_w(px(560.))
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
                        Label::new("Генератор строк")
                            .text_lg()
                            .text_color(rgb(appearance::FOREGROUND)),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("template-generator-close")
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip("Закрыть генератор")
                            .disabled(generating)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.close_template_generator(window, cx)
                            })),
                    ),
            )
            .child(Label::new("Шаблон одной строки").text_sm())
            .child(Input::new(&self.template).disabled(generating))
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .text_sm()
                    .text_color(rgb(appearance::MUTED))
                    .child(Label::new(
                        "{int_from_5} увеличивает число на каждой строке. {guid} создаёт UUID.",
                    ))
                    .child(Label::new(
                        "{rand_string} создаёт 12 случайных букв и цифр, {rand_string_16} — строку из 16 символов.",
                    ))
                    .child(Label::new(
                        "В одном шаблоне можно сочетать несколько правил.",
                    ))
                    .child(Label::new(
                        "strokna {int_from_5} → strokna 5, strokna 6, …",
                    )),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(Label::new("Количество строк"))
                    .child(
                        div()
                            .w(px(110.))
                            .child(Input::new(&self.rows).disabled(generating)),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("template-generator-run")
                            .small()
                            .primary()
                            .label(if generating { "Создание…" } else { "Создать" })
                            .disabled(generating)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.generate_template(window, cx)
                            })),
                    ),
            )
            .children(self.error.as_ref().map(|error| {
                Label::new(error.clone())
                    .text_sm()
                    .text_color(rgb(appearance::ERROR))
            }))
    }
}

impl Workspace {
    pub(super) fn open_template_generator(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() || self.applying_update() {
            return;
        }
        if self.template_generator.is_none() {
            self.template_generator = Some(TemplateGenerator::new(window, cx));
        }
        if let Some(generator) = &self.template_generator {
            generator.template.update(cx, |input, cx| {
                input.focus(window, cx);
                input.select_all(window, cx);
            });
        }
        cx.notify();
    }

    fn close_template_generator(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self
            .template_generator
            .as_ref()
            .is_some_and(|generator| generator.generating)
        {
            return;
        }
        self.template_generator = None;
        self.select(self.active, window, cx);
    }

    pub(super) fn template_generator_panel(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        self.template_generator.as_ref().map(|generator| {
            div()
                .id("template-generator")
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
                        this.close_template_generator(window, cx);
                        cx.stop_propagation();
                    }
                }))
                .child(generator.render(cx))
                .into_any_element()
        })
    }

    fn generate_template(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(generator) = &mut self.template_generator else {
            return;
        };
        if generator.generating {
            return;
        }
        let template_text = generator.template.read(cx).value().to_string();
        let rows_text = generator.rows.read(cx).value().to_string();
        let rows = match rows_text.trim().parse::<usize>() {
            Ok(rows) if rows > 0 => rows,
            _ => {
                generator.error = Some("Укажите положительное целое число строк.".into());
                cx.notify();
                return;
            }
        };

        generator.generating = true;
        generator.error = None;
        let operation =
            cx.background_spawn(async move { template::generate(&template_text, rows) });
        cx.notify();

        cx.spawn_in(window, async move |this, cx| {
            let result = operation.await;
            let _ = this.update_in(cx, |this, window, cx| {
                let Some(generator) = &mut this.template_generator else {
                    return;
                };
                match result {
                    Ok(text) => {
                        this.template_generator = None;
                        this.open_generated_document(text, window, cx);
                    }
                    Err(error) => {
                        generator.generating = false;
                        generator.error = Some(error.to_string().into());
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }

    fn open_generated_document(
        &mut self,
        text: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let dirty = !text.is_empty();
        let id = self.insert_document(
            None,
            text,
            crate::text_format::TextFormat::default(),
            window,
            cx,
        );
        if let Some(document) = self.documents.last_mut() {
            document.saved_text = Some(gpui_kit::component::input::Rope::new());
            document.dirty = dirty;
        }
        self.select(id, window, cx);
    }
}
