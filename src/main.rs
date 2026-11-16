#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod files;
mod go_to_line;
mod json_format;
mod recent_files;
mod search;
mod session;
mod text_format;
mod workspace;

use std::path::PathBuf;

use gpui_kit::component::{Theme, ThemeMode, set_locale};
use gpui_kit::*;

use workspace::Workspace;

fn main() {
    let paths = std::env::args_os().skip(1).map(PathBuf::from).collect();

    let startup = session::startup();
    let restored_size = startup
        .recovered
        .as_ref()
        .map(|session| session.state.window);

    application().with_assets(assets::Assets).run(move |cx| {
        init(cx);
        set_locale("ru");
        Theme::change(ThemeMode::Dark, None, cx);
        workspace::bind_keys(cx);

        let min_size = size(px(760.0), px(350.0));
        let initial_size = restored_size.map_or(size(px(1080.0), px(700.0)), |restored| {
            size(
                px(restored.width).max(min_size.width),
                px(restored.height).max(min_size.height),
            )
        });
        let bounds = Bounds::centered(None, initial_size, cx);
        let options = WindowOptions {
            window_bounds: Some(if restored_size.is_some_and(|size| size.maximized) {
                WindowBounds::Maximized(bounds)
            } else {
                WindowBounds::Windowed(bounds)
            }),
            window_min_size: Some(min_size),
            titlebar: Some(TitlebarOptions {
                title: Some("RustText".into()),
                ..Default::default()
            }),
            ..Default::default()
        };
        open_window(options, cx, |window, cx| {
            cx.new(|cx| Workspace::new(paths, startup, window, cx))
        })
        .expect("Не удалось открыть окно RustText");
        cx.activate(true);
    });
}
