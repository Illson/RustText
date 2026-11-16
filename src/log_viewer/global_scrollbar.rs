use std::{cell::Cell, rc::Rc};

use gpui_kit::{
    base::{Scrollbar, ScrollbarHandle, ScrollbarMode, input::EditorScrollbarContext},
    *,
};

use super::{LogViewer, viewport::Destination};

const TRACK_WIDTH: f32 = 16.0;
const THUMB_WIDTH: f32 = 10.0;
const MIN_THUMB_HEIGHT: f32 = 24.0;

#[derive(Default)]
pub(super) struct DragState {
    active: Cell<bool>,
    offset: Cell<f32>,
    preview_fraction: Cell<Option<f32>>,
}

#[derive(Clone, Copy)]
enum ScrollTarget {
    Page(Pixels),
    File { start: u64, travel: u64 },
}

pub(super) fn render(
    scroll: EditorScrollbarContext,
    viewer: WeakEntity<LogViewer>,
    scroll_bottom: &Cell<Pixels>,
    drag_state: Rc<DragState>,
    _window: &mut Window,
    cx: &mut App,
) -> impl IntoElement {
    let viewport = scroll.viewport_bounds();
    let content = scroll.content_size();
    let bottom = (viewport.size.height - content.height).min(px(0.));
    scroll_bottom.set(bottom);

    let (file_size, hidden, page_start, page_end) = viewer
        .upgrade()
        .map(|viewer| viewer.read(cx))
        .map(|viewer| {
            let page = viewer.page.as_ref();
            (
                viewer.bookmark.size,
                viewer.bookmark.hidden,
                page.and_then(|page| page.rows.first().map(|row| row.start))
                    .unwrap_or(viewer.bookmark.offset),
                page.map_or(viewer.bookmark.offset, |page| page.next_offset),
            )
        })
        .unwrap_or_default();

    let track_height = f32::from(viewport.size.height).max(1.0);
    let file_span = file_size.saturating_sub(hidden);
    let page_span = page_end.saturating_sub(page_start).min(file_span);
    let (visible_page_span, local_fraction) = scroll_geometry(
        f32::from(viewport.size.height),
        f32::from(content.height),
        f32::from(scroll.offset().y),
        page_span,
    );
    let local_scroll_range = (content.height - viewport.size.height).max(px(0.));
    let current_offset = page_start.saturating_add(
        ((page_span.saturating_sub(visible_page_span)) as f64 * local_fraction) as u64,
    );
    let scrollable_span = file_span.saturating_sub(visible_page_span);
    let can_seek = scrollable_span > 0;
    let seek_within_page = page_span == file_span && can_seek;
    let mut global_fraction = if can_seek {
        (current_offset.saturating_sub(hidden) as f64 / scrollable_span as f64).clamp(0.0, 1.0)
            as f32
    } else {
        0.0
    };
    if let Some(preview_fraction) = drag_state.preview_fraction.get() {
        global_fraction = preview_fraction;
    }
    let thumb_height = if file_span > 0 {
        ((visible_page_span as f64 / file_span as f64) as f32 * track_height)
            .clamp(MIN_THUMB_HEIGHT.min(track_height), track_height)
    } else {
        track_height
    };
    let thumb_height = thumb_height.max(MIN_THUMB_HEIGHT.min(track_height));
    let thumb_travel = (track_height - thumb_height).max(0.0);
    let thumb_top = global_fraction * thumb_travel;
    let handle_viewer = viewer.clone();
    let handle = canvas(
        |bounds, window, _| window.insert_hitbox(bounds, HitboxBehavior::Normal),
        move |bounds, hitbox, window, _| {
            window.set_cursor_style(CursorStyle::Arrow, &hitbox);
            if drag_state.active.get() {
                window.set_window_cursor_style(CursorStyle::Arrow);
            }
            let track_origin = bounds.origin.y;
            let state = drag_state.clone();
            let down_viewer = handle_viewer.clone();
            let mouse_hitbox = hitbox.clone();
            window.on_mouse_event(move |event: &MouseDownEvent, phase, window, cx| {
                if !phase.capture()
                    || !can_seek
                    || event.button != MouseButton::Left
                    || !mouse_hitbox.is_hovered(window)
                {
                    return;
                }
                let pointer = f32::from(event.position.y - track_origin);
                let within_thumb = pointer >= thumb_top && pointer <= thumb_top + thumb_height;
                state.offset.set(if within_thumb {
                    pointer - thumb_top
                } else {
                    thumb_height / 2.0
                });
                state.active.set(true);
                let fraction = (pointer - state.offset.get()) / thumb_travel.max(1.0);
                state.preview_fraction.set(Some(fraction.clamp(0.0, 1.0)));
                notify(&down_viewer, cx);
                cx.stop_propagation();
            });

            let state = drag_state.clone();
            let move_viewer = handle_viewer.clone();
            window.on_mouse_event(move |event: &MouseMoveEvent, phase, _, cx| {
                if phase.capture() && state.active.get() {
                    if !event.dragging() {
                        state.active.set(false);
                        state.preview_fraction.set(None);
                        notify(&move_viewer, cx);
                        return;
                    }
                    let pointer = f32::from(event.position.y - track_origin);
                    let fraction = (pointer - state.offset.get()) / thumb_travel.max(1.0);
                    state.preview_fraction.set(Some(fraction.clamp(0.0, 1.0)));
                    notify(&move_viewer, cx);
                }
            });

            let state = drag_state.clone();
            let up_viewer = handle_viewer.clone();
            window.on_mouse_event(move |event: &MouseUpEvent, phase, window, cx| {
                if phase.capture() && event.button == MouseButton::Left {
                    if state.active.get() && can_seek {
                        let pointer = f32::from(event.position.y - track_origin);
                        let fraction = (pointer - state.offset.get()) / thumb_travel.max(1.0);
                        let target = if seek_within_page {
                            ScrollTarget::Page(local_scroll_range)
                        } else {
                            ScrollTarget::File {
                                start: hidden,
                                travel: scrollable_span,
                            }
                        };
                        seek(&up_viewer, fraction, target, window, cx);
                    }
                    state.active.set(false);
                    state.preview_fraction.set(None);
                    notify(&up_viewer, cx);
                }
            });
        },
    )
    .absolute()
    .top_0()
    .bottom_0()
    .right_0()
    .w(px(TRACK_WIDTH));

    let wheel_viewer = viewer.clone();
    let wheel_scroll = scroll.clone();
    let wheel_area = canvas(
        |bounds, window, _| window.insert_hitbox(bounds, HitboxBehavior::Normal),
        move |_, hitbox, window, _| {
            let scroll = wheel_scroll.clone();
            let viewer = wheel_viewer.clone();
            window.on_mouse_event(move |event: &ScrollEvent, phase, window, cx| {
                if !phase.capture() || !hitbox.should_handle_scroll(window) {
                    return;
                }
                let delta = event.delta.pixel_delta(px(1.));
                if delta.y == px(0.) || delta.x.abs() > delta.y.abs() {
                    return;
                }
                let forward = delta.y < px(0.);
                let edge = if forward { bottom } else { px(0.) };
                if (scroll.offset().y - edge).abs() <= px(0.5) {
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
    .size_full();

    let horizontal_scrollbar = (content.width > viewport.size.width).then(|| {
        Scrollbar::horizontal(&scroll)
            .mode(ScrollbarMode::Always)
            .styles(|_| crate::appearance::scrollbar_styles())
            .into_any_element()
    });

    div()
        .size_full()
        .relative()
        .children(horizontal_scrollbar)
        .child(
            div()
                .absolute()
                .top_0()
                .bottom_0()
                .right_0()
                .w(px(TRACK_WIDTH))
                .bg(rgb(crate::appearance::SURFACE))
                .border_l_1()
                .border_color(rgb(crate::appearance::BORDER))
                .child(
                    div()
                        .absolute()
                        .top(px(thumb_top))
                        .right(px(3.))
                        .w(px(THUMB_WIDTH))
                        .h(px(thumb_height))
                        .rounded(px(6.))
                        .bg(rgb(crate::appearance::MUTED)),
                ),
        )
        .child(wheel_area)
        .child(handle)
}

fn seek(
    viewer: &WeakEntity<LogViewer>,
    fraction: f32,
    target: ScrollTarget,
    window: &mut Window,
    cx: &mut App,
) {
    let fraction = fraction.clamp(0.0, 1.0);
    let _ = viewer.update(cx, |viewer, cx| {
        if !viewer.available() {
            return;
        }
        match target {
            ScrollTarget::Page(range) => {
                viewer.viewer.update(cx, |editor, cx| {
                    let x = editor.scroll_offset().x;
                    editor.set_scroll_offset(point(x, px(-f32::from(range) * fraction)), cx);
                });
            }
            ScrollTarget::File { .. } if fraction >= 1.0 => {
                viewer.navigate(Destination::End, window, cx);
            }
            ScrollTarget::File { start, travel } => {
                let offset = seek_target(start, travel, fraction);
                let file_size = viewer.bookmark.size;
                let percent = if file_size > 0 {
                    offset as f64 / file_size as f64 * 100.0
                } else {
                    0.0
                };
                viewer.navigate(Destination::Percentage(percent), window, cx);
            }
        }
    });
}

fn notify(viewer: &WeakEntity<LogViewer>, cx: &mut App) {
    let _ = viewer.update(cx, |_, cx| cx.notify());
}

fn scroll_geometry(
    viewport_height: f32,
    content_height: f32,
    scroll_offset: f32,
    page_span: u64,
) -> (u64, f64) {
    let viewport_height = viewport_height.max(1.0);
    let visible_fraction = (viewport_height / content_height.max(viewport_height)).clamp(0.0, 1.0);
    let visible_span = (page_span as f64 * visible_fraction as f64) as u64;
    let scroll_range = (content_height - viewport_height).max(0.0);
    let scroll_fraction = if scroll_range > 0.0 {
        (-scroll_offset / scroll_range).clamp(0.0, 1.0) as f64
    } else {
        0.0
    };
    (visible_span, scroll_fraction)
}

fn seek_target(hidden: u64, file_travel: u64, fraction: f32) -> u64 {
    hidden.saturating_add((file_travel as f64 * fraction.clamp(0.0, 1.0) as f64) as u64)
}

#[cfg(test)]
#[path = "../../tests/unit/log_scrollbar.rs"]
mod tests;
