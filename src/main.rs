#![cfg_attr(windows, windows_subsystem = "windows")]

mod appearance;
mod files;
mod go_to_line;
mod json_format;
mod log_file;
mod log_io;
mod log_trim;
mod log_viewer;
mod recent_files;
mod search;
mod session;
mod single_instance;
mod text_format;
mod updater;
mod workspace;

use gpui_kit::component::{Theme, ThemeMode, set_locale};
use gpui_kit::*;

use workspace::Workspace;

fn main() {
    if let Some(result) = updater::run_helper_if_requested() {
        if let Err(error) = result {
            report_startup_error(format!("Не удалось установить обновление. {error}"));
            std::process::exit(1);
        }
        return;
    }
    log_viewer::enable_measurements();
    let launch = match single_instance::LaunchRequest::from_process() {
        Ok(launch) => launch,
        Err(error) => return launch_error(error),
    };
    let paths = launch.absolute_paths();
    let instance = match single_instance::SingleInstance::acquire(launch) {
        Ok(single_instance::InstanceRole::Primary(instance)) => instance,
        Ok(single_instance::InstanceRole::Forwarded) => return,
        Err(error) => return launch_error(error),
    };

    let startup = session::startup();
    let restored_size = startup
        .recovered
        .as_ref()
        .map(|session| session.state.window);

    application().with_assets(assets::Assets).run(move |cx| {
        init(cx);
        set_locale("ru");
        Theme::change(ThemeMode::Dark, None, cx);
        appearance::apply(cx);
        workspace::bind_keys(cx);
        log_viewer::bind_keys(cx);

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
            cx.new(|cx| Workspace::new(paths, startup, instance, window, cx))
        })
        .expect("Не удалось открыть окно RustText");
        cx.activate(true);
    });
}

fn launch_error(error: std::io::Error) {
    report_startup_error(format!("Не удалось запустить RustText. {error}"));
}

fn report_startup_error(message: String) {
    eprintln!("{message}");
    #[cfg(windows)]
    {
        use windows::{
            Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW},
            core::PCWSTR,
        };
        let title: Vec<_> = "RustText\0".encode_utf16().collect();
        let message: Vec<_> = format!("{message}\0").encode_utf16().collect();
        // Both strings are NUL-terminated and remain alive throughout the call.
        unsafe {
            MessageBoxW(
                None,
                PCWSTR(message.as_ptr()),
                PCWSTR(title.as_ptr()),
                MB_OK | MB_ICONERROR,
            )
        };
    }
}
