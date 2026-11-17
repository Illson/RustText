use std::{
    cell::Cell,
    fs::File,
    io::{self, Read, Seek, SeekFrom},
    path::Path,
    rc::Rc,
};

use gpui_kit::{
    base::{Scrollbar, ScrollbarHandle, ScrollbarMode, input::EditorScrollbarContext},
    component::input::{EditorState, RopeExt},
    *,
};

use crate::{appearance, text_format::TextEncoding};

pub(crate) const WIDTH: f32 = 88.0;
const SAMPLES: usize = 160;
const SAMPLE_BYTES: usize = 256;

pub(crate) fn install(
    editor: &Entity<EditorState>,
    cx: &mut App,
) -> crate::search_highlight::ViewportTracker {
    let viewport = crate::search_highlight::ViewportTracker::default();
    let rendered_viewport = viewport.clone();
    let weak = editor.downgrade();
    let dragging = Rc::new(Cell::new(None));
    editor.update(cx, |editor, _| {
        editor.set_scrollbar_renderer(Some(Rc::new(move |scroll, window, cx| {
            rendered_viewport.record(&scroll, window);
            let samples = weak
                .upgrade()
                .map(|editor| {
                    let editor = editor.read(cx);
                    let text = editor.text();
                    let lines = text.lines_len();
                    let count = lines.min(SAMPLES);
                    (0..count)
                        .map(|index| {
                            let row = sample_offset(lines.saturating_sub(1) as u64, index, count)
                                as usize;
                            text.slice_line(row).chars().take(64).collect::<String>()
                        })
                        .collect::<Vec<_>>()
                })
                .unwrap_or_default();
            render(scroll, &samples, weak.clone(), dragging.clone()).into_any_element()
        })));
    });
    viewport
}

pub(crate) fn sample_file(
    path: &Path,
    encoding: TextEncoding,
    hidden: u64,
    size: u64,
) -> io::Result<Vec<String>> {
    let mut file = File::open(path)?;
    let span = size.saturating_sub(hidden);
    let count = (span as usize).min(SAMPLES);
    let mut samples = Vec::with_capacity(count);
    for index in 0..count {
        let offset = crate::log_io::align_offset(
            hidden.saturating_add(sample_offset(span.saturating_sub(1), index, count)),
            encoding,
        );
        file.seek(SeekFrom::Start(offset))?;
        let mut bytes = [0; SAMPLE_BYTES];
        let read = file.read(&mut bytes)?;
        let text = crate::log_io::decode(&bytes[..read], encoding, offset == 0);
        samples.push(
            text.chars()
                .take_while(|c| *c != '\n' && *c != '\r')
                .take(64)
                .collect(),
        );
    }
    Ok(samples)
}

fn sample_offset(span: u64, index: usize, count: usize) -> u64 {
    ((span as u128 * index as u128) / count.saturating_sub(1).max(1) as u128) as u64
}

pub(crate) fn preview(samples: &[String], height: f32) -> impl IntoElement {
    div()
        .absolute()
        .top_0()
        .bottom_0()
        .left_0()
        .right(px(16.))
        .overflow_hidden()
        .children(samples.iter().enumerate().map(|(index, sample)| {
            let top = index as f32 / samples.len().max(1) as f32 * height;
            div()
                .absolute()
                .left(px(3.))
                .top(px(top))
                .text_size(px(2.))
                .line_height(px(3.))
                .text_color(rgb(appearance::MUTED))
                .whitespace_nowrap()
                .child(sample.clone())
        }))
}

fn thumb_grab_offset(pointer_y: f32, thumb_top: f32, thumb_height: f32) -> f32 {
    if pointer_y >= thumb_top && pointer_y <= thumb_top + thumb_height {
        pointer_y - thumb_top
    } else {
        thumb_height * 0.5
    }
}

fn drag_fraction(pointer_y: f32, grab_offset: f32, height: f32, thumb_height: f32) -> f32 {
    let travel = (height - thumb_height).max(1.);
    ((pointer_y - grab_offset) / travel).clamp(0., 1.)
}

fn render(
    scroll: EditorScrollbarContext,
    samples: &[String],
    editor: WeakEntity<EditorState>,
    dragging: Rc<Cell<Option<f32>>>,
) -> impl IntoElement {
    let viewport = scroll.viewport_bounds();
    let content = scroll.content_size();
    let height = f32::from(viewport.size.height).max(1.);
    let range = (content.height - viewport.size.height).max(px(0.));
    let thumb_height =
        (height * height / f32::from(content.height).max(height)).clamp(24_f32.min(height), height);
    let travel = (height - thumb_height).max(1.);
    let fraction = if range > px(0.) {
        (-f32::from(scroll.offset().y) / f32::from(range)).clamp(0., 1.)
    } else {
        0.
    };
    let thumb_top = fraction * travel;
    let handle = canvas(
        |bounds, window, _| window.insert_hitbox(bounds, HitboxBehavior::Normal),
        move |bounds, hitbox, window, _| {
            let state = dragging.clone();
            let target = editor.clone();
            window.on_mouse_event(move |event: &MouseDownEvent, phase, window, cx| {
                if phase.capture() && event.button == MouseButton::Left && hitbox.is_hovered(window)
                {
                    let pointer_y = f32::from(event.position.y - bounds.top());
                    let grab_offset = thumb_grab_offset(pointer_y, thumb_top, thumb_height);
                    state.set(Some(grab_offset));
                    seek(
                        &target,
                        drag_fraction(pointer_y, grab_offset, height, thumb_height),
                        range,
                        cx,
                    );
                    cx.stop_propagation();
                }
            });
            let state = dragging.clone();
            let target = editor.clone();
            window.on_mouse_event(move |event: &MouseMoveEvent, phase, _, cx| {
                if phase.capture()
                    && let Some(grab_offset) = state.get()
                {
                    if !event.dragging() {
                        state.set(None);
                        return;
                    }
                    let pointer_y = f32::from(event.position.y - bounds.top());
                    seek(
                        &target,
                        drag_fraction(pointer_y, grab_offset, height, thumb_height),
                        range,
                        cx,
                    );
                }
            });
            let state = dragging.clone();
            window.on_mouse_event(move |event: &MouseUpEvent, phase, _, _| {
                if phase.capture() && event.button == MouseButton::Left {
                    state.set(None);
                }
            });
        },
    )
    .absolute()
    .top_0()
    .bottom_0()
    .right_0()
    .w(px(WIDTH));
    div()
        .size_full()
        .relative()
        .children((content.width > viewport.size.width).then(|| {
            Scrollbar::horizontal(&scroll)
                .mode(ScrollbarMode::Always)
                .styles(|_| appearance::scrollbar_styles())
        }))
        .child(
            div()
                .absolute()
                .top_0()
                .bottom_0()
                .right_0()
                .w(px(WIDTH))
                .bg(rgb(appearance::SURFACE))
                .border_l_1()
                .border_color(rgb(appearance::BORDER))
                .child(preview(samples, height))
                .child(
                    div()
                        .absolute()
                        .top(px(thumb_top))
                        .right_0()
                        .w(px(WIDTH))
                        .h(px(thumb_height))
                        .bg(rgb(appearance::ACCENT).opacity(0.16))
                        .border_1()
                        .border_color(rgb(appearance::ACCENT)),
                ),
        )
        .child(handle)
}

fn seek(editor: &WeakEntity<EditorState>, fraction: f32, range: Pixels, cx: &mut App) {
    let _ = editor.update(cx, |editor, cx| {
        let x = editor.scroll_offset().x;
        editor.set_scroll_offset(point(x, px(-f32::from(range) * fraction.clamp(0., 1.))), cx);
    });
}

#[cfg(test)]
#[path = "../tests/unit/minimap.rs"]
mod tests;
