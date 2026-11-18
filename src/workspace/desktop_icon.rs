use std::{
    ffi::OsString,
    fs,
    mem::ManuallyDrop,
    os::windows::{ffi::OsStringExt, fs::MetadataExt},
    path::{Path, PathBuf},
    ptr, thread,
    time::{Duration, Instant},
};

use windows::{
    Win32::{
        Foundation::{POINT, S_OK},
        Storage::FileSystem::WIN32_FIND_DATAW,
        System::{
            Com::{
                CLSCTX_LOCAL_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoTaskMemFree, CoUninitialize, IServiceProvider,
            },
            Variant::{VARIANT, VARIANT_0, VARIANT_0_0, VT_I4},
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
    core::{GUID, Interface, PWSTR},
};

use crate::files::{FileTarget, FileVersion};

pub(super) struct DesktopIconPosition(POINT);

impl DesktopIconPosition {
    pub(super) fn capture(path: &Path) -> Option<Self> {
        let parent = path.parent()?;
        if ![FOLDERID_Desktop, FOLDERID_PublicDesktop]
            .iter()
            .filter_map(known_folder)
            .any(|folder| folder == parent)
        {
            return None;
        }
        in_desktop_apartment(|| {
            let view = manual_desktop_view()?;
            let (item, _) = find_item(&view, path)?;
            unsafe { view.GetItemPosition(item.0) }.ok().map(Self)
        })
    }

    pub(super) fn restore(self, saved: &FileTarget) {
        in_desktop_apartment(|| {
            let metadata = fs::metadata(&saved.path).ok()?;
            let stamp = (metadata.file_size(), metadata.last_write_time());
            let view = manual_desktop_view()?;
            let deadline = Instant::now() + Duration::from_secs(3);
            // Explorer updates its cached item asynchronously. Only position
            // the icon after the cache describes the published file.
            while Instant::now() < deadline && auto_arrange(&view) == Some(false) {
                if FileVersion::at(&saved.path).ok() != Some(saved.version) {
                    break;
                }
                if self.try_restore(&view, &saved.path, stamp).is_some() {
                    break;
                }
                thread::sleep(Duration::from_millis(125));
            }
            Some(())
        });
    }

    fn try_restore(&self, view: &IFolderView2, path: &Path, stamp: (u64, u64)) -> Option<()> {
        let (item, info) = find_item(view, path)?;
        let size = (u64::from(info.nFileSizeHigh) << 32) | u64::from(info.nFileSizeLow);
        let time = info.ftLastWriteTime;
        let write_time = (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime);
        if (size, write_time) != stamp {
            return None;
        }
        let position = unsafe { view.GetItemPosition(item.0) }.ok()?;
        if position != self.0 {
            let selection = unsafe { view.GetSelectionState(item.0) }.ok()?;
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
    fs::canonicalize(owned_shell_path(path)?).ok()
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

fn find_item(view: &IFolderView2, path: &Path) -> Option<(Pidl, WIN32_FIND_DATAW)> {
    let name = path.file_name()?;
    let folder: IShellFolder = unsafe { view.GetFolder() }.ok()?;
    let items: IEnumIDList = unsafe { view.Items(SVGIO_ALLVIEW) }.ok()?;
    loop {
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

fn in_desktop_apartment<T: Send>(action: impl FnOnce() -> Option<T> + Send) -> Option<T> {
    thread::scope(|scope| {
        thread::Builder::new()
            .name("rusttext-desktop-icon".to_owned())
            .spawn_scoped(scope, || {
                let _apartment = Apartment::enter()?;
                action()
            })
            .ok()?
            .join()
            .ok()?
    })
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
