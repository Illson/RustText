#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod files;
mod go_to_line;
mod json_format;
mod recent_files;
mod search;
mod text_format;
mod workspace;

use std::path::PathBuf;

use gpui_kit::component::{Theme, ThemeMode, set_locale};
use gpui_kit::*;

use workspace::Workspace;

fn main() {
    let paths = std::env::args_os().skip(1).map(PathBuf::from).collect();

    application().with_assets(assets::Assets).run(move |cx| {
        init(cx);
        set_locale("ru");
        Theme::change(ThemeMode::Dark, None, cx);
        workspace::bind_keys(cx);

        let options = WindowOptions {
            window_bounds: Some(WindowBounds::Windowed(Bounds::centered(
                None,
                size(px(1080.0), px(700.0)),
                cx,
            ))),
            window_min_size: Some(size(px(760.0), px(350.0))),
            titlebar: Some(TitlebarOptions {
                title: Some("RustText".into()),
                ..Default::default()
            }),
            ..Default::default()
        };
        open_window(options, cx, |window, cx| {
            cx.new(|cx| Workspace::new(paths, window, cx))
        })
        .expect("Не удалось открыть окно RustText");
        cx.activate(true);
    });
}
