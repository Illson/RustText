//! Shared palette for the compact editor and log workspace.

use gpui_kit::{component::Theme, *};

pub const BACKGROUND: u32 = 0x17191c;
pub const SURFACE: u32 = 0x202226;
pub const BORDER: u32 = 0x33363c;
pub const FOREGROUND: u32 = 0xe2e5eb;
pub const MUTED: u32 = 0xa0a8b4;
pub const ACCENT: u32 = 0xe4bd7f;
pub const ACTIVE: u32 = 0x3d3428;
pub const ERROR: u32 = 0xef9aa2;
pub const SUCCESS: u32 = 0x8acbb5;

pub fn scrollbar_styles() -> gpui_kit::base::ScrollbarStyles {
    gpui_kit::base::ScrollbarStyles::default()
        .track(|style| {
            style
                .bg(rgb(SURFACE).into())
                .border_color(rgb(BORDER).into())
                .width(px(16.))
        })
        .track_hover(|style| {
            style
                .bg(rgb(SURFACE).into())
                .border_color(rgb(BORDER).into())
                .width(px(16.))
        })
        .track_active(|style| {
            style
                .bg(rgb(SURFACE).into())
                .border_color(rgb(BORDER).into())
                .width(px(16.))
        })
        .thumb(|style| {
            style
                .bg(rgb(MUTED))
                .width(px(12.))
                .inset(px(2.))
                .radius(px(5.))
                .min_length(px(36.))
        })
        .thumb_hover(|style| {
            style
                .bg(rgb(ACCENT))
                .width(px(14.))
                .inset(px(1.))
                .radius(px(5.))
                .min_length(px(36.))
        })
        .thumb_active(|style| {
            style
                .bg(rgb(ACCENT))
                .width(px(14.))
                .inset(px(1.))
                .radius(px(5.))
                .min_length(px(36.))
        })
}

pub fn apply(cx: &mut App) {
    Theme::update(cx, |theme| {
        theme.font_family = "Segoe UI".into();
        theme.font_size = px(15.);
        theme.radius = px(4.);
        theme.background = rgb(BACKGROUND).into();
        theme.foreground = rgb(FOREGROUND).into();
        theme.border = rgb(BORDER).into();
        theme.muted = rgb(SURFACE).into();
        theme.muted_foreground = rgb(MUTED).into();
        theme.popover = rgb(SURFACE).into();
        theme.popover_foreground = rgb(FOREGROUND).into();
        theme.accent = rgb(ACTIVE).into();
        theme.accent_foreground = rgb(ACCENT).into();
        theme.primary = rgb(ACCENT).into();
        theme.primary_foreground = rgb(BACKGROUND).into();
        theme.ring = rgb(ACCENT).into();
        theme.selection = rgb(ACTIVE).into();
        theme.caret = rgb(ACCENT).into();
        theme.input = rgb(BORDER).into();
        theme.colors.list = rgb(BACKGROUND).into();
        theme.list_hover = rgb(SURFACE).into();
        theme.list_active = rgb(ACTIVE).into();
        theme.list_active_border = rgb(ACCENT).into();
        theme.button = rgb(SURFACE).into();
        theme.button_hover = rgb(BORDER).into();
        theme.button_active = rgb(ACTIVE).into();
        theme.button_foreground = rgb(FOREGROUND).into();
        let highlight = std::sync::Arc::make_mut(&mut theme.highlight_theme);
        highlight.style.editor_background = Some(rgb(BACKGROUND).into());
        highlight.style.editor_foreground = Some(rgb(FOREGROUND).into());
        highlight.style.editor_active_line = Some(rgb(SURFACE).into());
        highlight.style.editor_gutter_background = Some(rgb(BACKGROUND).into());
        highlight.style.editor_line_number = Some(rgb(MUTED).into());
        highlight.style.editor_active_line_number = Some(rgb(ACCENT).into());
    });
}
