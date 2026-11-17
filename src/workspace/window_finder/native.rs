use std::{mem::size_of, num::NonZeroU32};

use windows::{
    Win32::{
        Foundation::{HWND, LPARAM},
        Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute},
        UI::WindowsAndMessaging::{
            EnumWindows, GWL_EXSTYLE, GetForegroundWindow, GetShellWindow, GetWindowLongPtrW,
            GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsIconic,
            IsWindowVisible, SW_RESTORE, SetForegroundWindow, ShowWindowAsync, WS_EX_NOACTIVATE,
        },
    },
    core::BOOL,
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct FoundWindow {
    handle: HWND,
    pub(super) pid: u32,
    pub(super) title: String,
}

pub(super) fn find_windows(raw: &str, by_pid: bool) -> Result<Vec<FoundWindow>, &'static str> {
    let query = raw.trim();
    let pid = if by_pid {
        Some(parse_pid(query)?.get())
    } else {
        if query.is_empty() {
            return Err("Введите часть заголовка окна.");
        }
        None
    };
    let title = query.to_lowercase();

    let mut handles = Vec::<HWND>::new();
    unsafe { EnumWindows(Some(collect_window), LPARAM((&raw mut handles) as isize)) }
        .map_err(|_| "Не удалось получить список окон Windows.")?;
    let mut matches: Vec<_> = handles
        .into_iter()
        .filter_map(|handle| describe_window(handle, pid))
        .filter(|window| pid.is_some() || title_matches(&window.title, &title))
        .collect();
    matches.sort_unstable_by(|a, b| a.title.cmp(&b.title).then(a.pid.cmp(&b.pid)));
    Ok(matches)
}

unsafe extern "system" fn collect_window(handle: HWND, data: LPARAM) -> BOOL {
    let handles = unsafe { &mut *(data.0 as *mut Vec<HWND>) };
    handles.push(handle);
    BOOL(1)
}

fn parse_pid(raw: &str) -> Result<NonZeroU32, &'static str> {
    if raw.bytes().all(|byte| byte.is_ascii_digit())
        && let Ok(pid) = raw.parse()
    {
        return Ok(pid);
    }
    Err("Введите PID процесса от 1 до 4294967295.")
}

fn title_matches(title: &str, lowercase_query: &str) -> bool {
    title.to_lowercase().contains(lowercase_query)
}

fn describe_window(handle: HWND, expected_pid: Option<u32>) -> Option<FoundWindow> {
    unsafe {
        let mut pid = 0;
        if GetWindowThreadProcessId(handle, Some(&mut pid)) == 0
            || expected_pid.is_some_and(|expected| expected != pid)
            || !IsWindowVisible(handle).as_bool()
            || handle == GetShellWindow()
            || GetWindowLongPtrW(handle, GWL_EXSTYLE) & WS_EX_NOACTIVATE.0 as isize != 0
        {
            return None;
        }

        let mut cloaked = 0u32;
        if DwmGetWindowAttribute(
            handle,
            DWMWA_CLOAKED,
            (&raw mut cloaked).cast(),
            size_of::<u32>() as u32,
        )
        .is_ok()
            && cloaked != 0
        {
            return None;
        }

        let length = GetWindowTextLengthW(handle);
        let mut title = vec![0; length.max(0) as usize + 1];
        let copied = GetWindowTextW(handle, &mut title);

        Some(FoundWindow {
            handle,
            pid,
            title: String::from_utf16_lossy(&title[..copied.max(0) as usize]),
        })
    }
}

pub(super) fn bring_to_front(window: &FoundWindow) -> Result<(), &'static str> {
    if describe_window(window.handle, Some(window.pid)).as_ref() != Some(window) {
        return Err("Окно закрылось или изменилось. Повторите поиск.");
    }
    unsafe {
        if IsIconic(window.handle).as_bool()
            && !ShowWindowAsync(window.handle, SW_RESTORE).as_bool()
        {
            return Err("Windows не удалось восстановить свёрнутое окно.");
        }
        if !SetForegroundWindow(window.handle).as_bool() && GetForegroundWindow() != window.handle {
            return Err("Windows не разрешила вывести окно на передний план.");
        }
    }
    Ok(())
}

#[cfg(test)]
#[path = "../../../tests/unit/window_finder.rs"]
mod tests;
