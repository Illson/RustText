use std::{
    ffi::OsString,
    fs,
    mem::ManuallyDrop,
    os::windows::{
        ffi::{OsStrExt, OsStringExt},
        fs::MetadataExt,
    },
    path::{Component, Path, PathBuf, Prefix},
    ptr,
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};

use windows::{
    Win32::{
        Foundation::{POINT, S_OK},
        Globalization::{CSTR_EQUAL, CompareStringOrdinal},
        Storage::FileSystem::{FILE_ATTRIBUTE_REPARSE_POINT, GetDriveTypeW, WIN32_FIND_DATAW},
        System::{
            Com::{
                CLSCTX_LOCAL_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoTaskMemFree, CoUninitialize, IServiceProvider,
            },
            Variant::{VARIANT, VARIANT_0, VARIANT_0_0, VT_I4},
            WindowsProgramming::{DRIVE_FIXED, DRIVE_RAMDISK, DRIVE_REMOVABLE},
        },
        UI::Shell::{
            CSIDL_DESKTOP, Common::ITEMIDLIST, FOLDERID_Desktop, FOLDERID_PublicDesktop,
            FWF_AUTOARRANGE, IEnumIDList, IFolderView2, IShellBrowser, IShellFolder, IShellItem,
            IShellWindows, KF_FLAG_DONT_VERIFY, SHCreateItemWithParent, SHGDFIL_FINDDATA,
            SHGetDataFromIDListW, SHGetKnownFolderPath, SID_STopLevelBrowser, SIGDN_FILESYSPATH,
            SVGIO_ALLVIEW, SVSI_NOTAKEFOCUS, SVSI_POSITIONITEM, SVSI_SELECT, SWC_DESKTOP,
            SWFO_NEEDDISPATCH, ShellWindows,
        },
    },
    core::{GUID, Interface, PCWSTR, PWSTR},
};

use super::reveal::normalize_shell_path;
use crate::files::{FileTarget, FileVersion};

const CAPTURE_TIMEOUT: Duration = Duration::from_millis(250);
const RESTORE_TIMEOUT: Duration = Duration::from_secs(3);
const RESTORE_INTERVAL: Duration = Duration::from_millis(125);
static DESKTOP_WORKER_ACTIVE: AtomicBool = AtomicBool::new(false);

pub(super) struct DesktopIconPosition(POINT);

impl DesktopIconPosition {
    pub(super) fn capture(path: &Path) -> Option<Self> {
        let path = path.to_path_buf();
        run_desktop_operation(&DESKTOP_WORKER_ACTIVE, CAPTURE_TIMEOUT, move |deadline| {
            let parent = path.parent()?;
            if ![FOLDERID_Desktop, FOLDERID_PublicDesktop]
                .iter()
                .filter_map(known_folder)
                .any(|folder| desktop_folder_matches(&folder, parent, deadline))
                || Instant::now() >= deadline
            {
                return None;
            }
            let _apartment = Apartment::enter()?;
            let view = manual_desktop_view()?;
            let (item, _) = find_item(&view, &path, deadline)?;
            unsafe { view.GetItemPosition(item.0) }.ok().map(Self)
        })
    }

    pub(super) fn restore(self, saved: &FileTarget) {
        let saved = saved.clone();
        run_desktop_operation(&DESKTOP_WORKER_ACTIVE, RESTORE_TIMEOUT, move |deadline| {
            let _apartment = Apartment::enter()?;
            let metadata = fs::metadata(&saved.path).ok()?;
            let stamp = (metadata.file_size(), metadata.last_write_time());
            let view = manual_desktop_view()?;
            // Explorer updates its cached item asynchronously. Only position
            // the icon after the cache describes the published file.
            while Instant::now() < deadline {
                if auto_arrange(&view) != Some(false) || !saved_file_is_current(&saved, deadline) {
                    break;
                }
                if self.try_restore(&view, &saved, stamp, deadline).is_some() {
                    break;
                }
                thread::sleep(
                    RESTORE_INTERVAL.min(deadline.saturating_duration_since(Instant::now())),
                );
            }
            Some(())
        });
    }

    fn try_restore(
        &self,
        view: &IFolderView2,
        saved: &FileTarget,
        stamp: (u64, u64),
        deadline: Instant,
    ) -> Option<()> {
        let (item, info) = find_item(view, &saved.path, deadline)?;
        let size = (u64::from(info.nFileSizeHigh) << 32) | u64::from(info.nFileSizeLow);
        let time = info.ftLastWriteTime;
        let write_time = (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime);
        if (size, write_time) != stamp {
            return None;
        }
        let position = unsafe { view.GetItemPosition(item.0) }.ok()?;
        if position != self.0 {
            let selection = unsafe { view.GetSelectionState(item.0) }.ok()?;
            if !saved_file_is_current(saved, deadline) {
                return Some(());
            }
            let flags = SVSI_POSITIONITEM.0 as u32
                | SVSI_NOTAKEFOCUS.0 as u32
                | (selection & SVSI_SELECT.0 as u32);
            let item_ids = [item.0.cast_const()];
            unsafe {
                view.SelectAndPositionItems(1, item_ids.as_ptr(), Some(&raw const self.0), flags)
            }
            .ok()?;
        }
        Some(())
    }
}

fn known_folder(id: &GUID) -> Option<PathBuf> {
    let path = unsafe { SHGetKnownFolderPath(id, KF_FLAG_DONT_VERIFY, None) }.ok()?;
    owned_shell_path(path)
}

fn desktop_folder_matches(configured: &Path, parent: &Path, deadline: Instant) -> bool {
    if same_shell_path(configured, parent) {
        return true;
    }

    let mut folder = configured.to_path_buf();
    for _ in 0..32 {
        let Some(target) = local_folder_link(&folder, deadline) else {
            return false;
        };
        if same_shell_path(&target, parent) {
            return true;
        }
        folder = target;
    }
    false
}

fn local_folder_link(folder: &Path, deadline: Instant) -> Option<PathBuf> {
    let Some(Component::Prefix(prefix)) = folder.components().next() else {
        return None;
    };
    let (Prefix::Disk(drive) | Prefix::VerbatimDisk(drive)) = prefix.kind() else {
        return None;
    };
    if !folder.is_absolute() || Instant::now() >= deadline {
        return None;
    }
    let root = [u16::from(drive), u16::from(b':'), u16::from(b'\\'), 0];
    if !matches!(
        unsafe { GetDriveTypeW(PCWSTR(root.as_ptr())) },
        DRIVE_FIXED | DRIVE_REMOVABLE | DRIVE_RAMDISK
    ) {
        return None;
    }

    // Inspect each component before traversing it. A local directory link
    // can lead to a network path, which must only be compared by name.
    let ancestors: Vec<_> = folder.ancestors().collect();
    for directory in ancestors.into_iter().rev().skip(1) {
        if Instant::now() >= deadline {
            return None;
        }
        let attributes = fs::symlink_metadata(directory).ok()?.file_attributes();
        if attributes & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0 {
            let link = fs::read_link(directory).ok()?;
            let suffix = folder.strip_prefix(directory).ok()?;
            return Some(directory.parent()?.join(link).join(suffix));
        }
    }
    None
}

fn same_shell_path(left: &Path, right: &Path) -> bool {
    // Compare the configured path without opening the Desktop directory.
    // A redirected Desktop can point to an offline UNC share.
    fn units(path: &Path) -> Option<Vec<u16>> {
        let path = normalize_shell_path(path).ok()?;
        let path = Path::new(&path);
        if !path.is_absolute() {
            return None;
        }
        let normalized: PathBuf = path.components().collect();
        Some(normalized.as_os_str().encode_wide().collect())
    }

    let (Some(left), Some(right)) = (units(left), units(right)) else {
        return false;
    };
    unsafe { CompareStringOrdinal(&left, &right, true) == CSTR_EQUAL }
}

fn saved_file_is_current(saved: &FileTarget, deadline: Instant) -> bool {
    Instant::now() < deadline
        && FileVersion::at(&saved.path).ok() == Some(saved.version)
        && Instant::now() < deadline
}

fn manual_desktop_view() -> Option<IFolderView2> {
    // The desktop is registered separately from the ordinary Explorer windows.
    let windows: IShellWindows =
        unsafe { CoCreateInstance(&ShellWindows, None, CLSCTX_LOCAL_SERVER) }.ok()?;
    let mut desktop_id = VARIANT_0_0 {
        vt: VT_I4,
        ..Default::default()
    };
    desktop_id.Anonymous.lVal = CSIDL_DESKTOP as i32;
    let desktop = VARIANT {
        Anonymous: VARIANT_0 {
            Anonymous: ManuallyDrop::new(desktop_id),
        },
    };
    let mut hwnd = 0;
    let dispatch = unsafe {
        windows.FindWindowSW(
            &desktop,
            &VARIANT::default(),
            SWC_DESKTOP,
            &mut hwnd,
            SWFO_NEEDDISPATCH,
        )
    }
    .ok()?;
    let provider: IServiceProvider = dispatch.cast().ok()?;
    let browser: IShellBrowser = unsafe { provider.QueryService(&SID_STopLevelBrowser) }.ok()?;
    let view: IFolderView2 = unsafe { browser.QueryActiveShellView() }
        .ok()?
        .cast()
        .ok()?;
    (auto_arrange(&view) == Some(false)).then_some(view)
}

fn auto_arrange(view: &IFolderView2) -> Option<bool> {
    let flags = unsafe { view.GetCurrentFolderFlags() }.ok()?;
    Some(flags & FWF_AUTOARRANGE.0 as u32 != 0)
}

fn find_item(
    view: &IFolderView2,
    path: &Path,
    deadline: Instant,
) -> Option<(Pidl, WIN32_FIND_DATAW)> {
    let name = path.file_name()?;
    let folder: IShellFolder = unsafe { view.GetFolder() }.ok()?;
    let items: IEnumIDList = unsafe { view.Items(SVGIO_ALLVIEW) }.ok()?;
    while Instant::now() < deadline {
        let mut ids = [ptr::null_mut()];
        if unsafe { items.Next(&mut ids, None) } != S_OK || ids[0].is_null() {
            return None;
        }
        let pidl = Pidl(ids[0]);
        let mut data = WIN32_FIND_DATAW::default();
        let info = unsafe {
            SHGetDataFromIDListW(
                &folder,
                pidl.0,
                SHGDFIL_FINDDATA,
                (&raw mut data).cast(),
                size_of::<WIN32_FIND_DATAW>() as i32,
            )
        };
        if info.is_err() || OsString::from_wide(wide_string(&data.cFileName)) != name {
            continue;
        }
        // The Shell merges the user's Desktop and Public Desktop into one view.
        let item: IShellItem = unsafe { SHCreateItemWithParent(None, &folder, pidl.0) }.ok()?;
        let item_path = unsafe { item.GetDisplayName(SIGDN_FILESYSPATH) }.ok()?;
        if fs::canonicalize(owned_shell_path(item_path)?).is_ok_and(|candidate| candidate == path) {
            return Some((pidl, data));
        }
    }
    None
}

fn wide_string(string: &[u16]) -> &[u16] {
    &string[..string
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(string.len())]
}

fn owned_shell_path(path: PWSTR) -> Option<PathBuf> {
    if path.is_null() {
        return None;
    }
    let result = PathBuf::from(OsString::from_wide(unsafe { path.as_wide() }));
    unsafe { CoTaskMemFree(Some(path.0.cast())) };
    Some(result)
}

fn run_desktop_operation<T: Send + 'static>(
    active: &'static AtomicBool,
    timeout: Duration,
    action: impl FnOnce(Instant) -> Option<T> + Send + 'static,
) -> Option<T> {
    // Limit unresponsive COM work to one thread, even when saves are retried.
    active
        .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
        .ok()?;
    let worker = DesktopWorker(active);
    let deadline = Instant::now() + timeout;
    let (sender, receiver) = mpsc::sync_channel(1);
    thread::Builder::new()
        .name("rusttext-desktop-icon".to_owned())
        .spawn(move || {
            let result = if Instant::now() < deadline {
                action(deadline)
            } else {
                None
            };
            drop(worker);
            let _ = sender.send(result);
        })
        .ok()?;
    receiver
        .recv_timeout(deadline.saturating_duration_since(Instant::now()))
        .ok()?
        .filter(|_| Instant::now() < deadline)
}

struct DesktopWorker(&'static AtomicBool);

impl Drop for DesktopWorker {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}

struct Apartment;

impl Apartment {
    fn enter() -> Option<Self> {
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
            .ok()
            .ok()?;
        Some(Self)
    }
}

impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}

struct Pidl(*mut ITEMIDLIST);

impl Drop for Pidl {
    fn drop(&mut self) {
        unsafe { CoTaskMemFree(Some(self.0.cast())) };
    }
}

#[cfg(test)]
#[path = "../../tests/unit/workspace_desktop_icon.rs"]
mod tests;
