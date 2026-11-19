use std::{cell::Cell, rc::Rc};

use gpui_kit::component::{
    ActiveTheme, WindowExt,
    button::{Button, ButtonVariants},
    dialog::{Confirm, DialogFooter},
    input::{EditorState, Input, InputState, Position, RopeExt},
    label::Label,
};
use gpui_kit::*;

pub(crate) fn open(editor: Entity<EditorState>, window: &mut Window, cx: &mut App) {
    if window.has_active_dialog(cx) {
        return;
    }

    let line_count = editor.read(cx).text().lines_len();
    let input = cx.new(|cx| InputState::new(window, cx).placeholder("Номер строки"));
    let initial_focus = Cell::new(true);
    let invalid = Rc::new(Cell::new(false));

    editor.focus_handle(cx).focus(window, cx);
    window.open_dialog(cx, move |dialog, window, cx| {
        if initial_focus.replace(false) {
            input.focus_handle(cx).focus(window, cx);
        }

        let submit_input = input.clone();
        let editor = editor.clone();
        let invalid = invalid.clone();
        let range_hint = if invalid.get() {
            format!("Введите целое число от 1 до {line_count}.")
        } else {
            format!("Строки с 1 по {line_count}")
        };
        dialog
            .title("Перейти к строке")
            .w(px(360.0))
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap_2()
                    .child(Input::new(&input))
                    .child(
                        div()
                            .text_sm()
                            .text_color(if invalid.get() {
                                rgb(0xffb4ad).into()
                            } else {
                                cx.theme().muted_foreground
                            })
                            .child(Label::new(range_hint)),
                    ),
            )
            .footer(
                DialogFooter::new()
                    .child(
                        Button::new("go-to-line-cancel")
                            .label("Отмена")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .child(
                        Button::new("go-to-line-confirm")
                            .primary()
                            .label("Перейти")
                            .on_click(|_, window, cx| {
                                window.dispatch_action(Box::new(Confirm { secondary: false }), cx)
                            }),
                    ),
            )
            .on_ok(move |_, window, cx| {
                let value = submit_input.read(cx).value();
                let Some(line) = line_index(value.as_ref(), line_count) else {
                    invalid.set(true);
                    submit_input.focus_handle(cx).focus(window, cx);
                    window.refresh();
                    return false;
                };
                editor.update(cx, |state, cx| {
                    state.set_cursor_position(Position::new(line, 0), window, cx);
                });
                true
            })
    });
}

fn line_index(value: &str, line_count: usize) -> Option<u32> {
    let number = value.trim().parse::<u32>().ok()?;
    if number as usize > line_count {
        return None;
    }
    number.checked_sub(1)
}

#[cfg(test)]
#[path = "../tests/unit/go_to_line.rs"]
mod tests;
