use gpui_kit::*;

use super::Workspace;

#[cfg(any(windows, test))]
const REGISTERED_APP_NAME: &str = "RustText";
#[cfg(any(windows, test))]
const CAPABILITIES_PATH: &str = "Software\\RustText\\Capabilities";
#[cfg(any(windows, test))]
const ASSOCIATIONS: [(&str, &str); 4] = [
    (".log", "RustText.Log"),
    (".txt", "RustText.Text"),
    (".json", "RustText.Json"),
    (".xml", "RustText.Xml"),
];

#[cfg(any(windows, test))]
struct RegistrationPlan {
    executable_name: String,
    executable_path: String,
    open_command: String,
    context_menu: Vec<(String, String)>,
}

#[cfg(any(windows, test))]
impl RegistrationPlan {
    fn new(executable_path: &str) -> Self {
        let executable_name = executable_path
            .rsplit(['\\', '/'])
            .next()
            .unwrap_or("rusttext.exe")
            .to_owned();
        let open_command = format!("{} \"%1\"", quote_windows_argument(executable_path));
        let context_menu = ASSOCIATIONS
            .iter()
            .flat_map(|(extension, _)| {
                let verb = format!(
                    "Software\\Classes\\SystemFileAssociations\\{extension}\\shell\\RustText"
                );
                [
                    (verb.clone(), "Открыть в RustText".to_owned()),
                    (format!("{verb}\\command"), open_command.clone()),
                ]
            })
            .collect();
        Self {
            executable_name,
            executable_path: executable_path.to_owned(),
            open_command,
            context_menu,
        }
    }
}

#[cfg(any(windows, test))]
fn quote_windows_argument(argument: &str) -> String {
    let mut quoted = String::with_capacity(argument.len() + 2);
    quoted.push('"');

    let mut backslashes = 0;
    for character in argument.chars() {
        match character {
            '\\' => backslashes += 1,
            '"' => {
                quoted.extend(std::iter::repeat_n('\\', backslashes * 2 + 1));
                quoted.push('"');
                backslashes = 0;
            }
            _ => {
                quoted.extend(std::iter::repeat_n('\\', backslashes));
                backslashes = 0;
                quoted.push(character);
            }
        }
    }
    quoted.extend(std::iter::repeat_n('\\', backslashes * 2));
    quoted.push('"');
    quoted
}

impl Workspace {
    pub(super) fn register_file_associations(
        &mut self,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match register_and_open_settings() {
            Ok(()) => self.error = None,
            Err(error) => self.error = Some(error.to_string().into()),
        }
        cx.notify();
    }
}

#[cfg(windows)]
fn register_and_open_settings() -> std::io::Result<()> {
    register_for_current_user()?;
    open_default_apps_settings()
}

#[cfg(windows)]
pub(crate) fn register_for_current_user() -> std::io::Result<()> {
    let executable = std::env::current_exe()?;
    let executable = executable.to_str().ok_or_else(|| {
        std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "Путь к RustText содержит неподдерживаемые символы.",
        )
    })?;
    register_application(&RegistrationPlan::new(executable))
}

#[cfg(not(windows))]
pub(crate) fn register_for_current_user() -> std::io::Result<()> {
    Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "Регистрация типов файлов доступна только в Windows.",
    ))
}

#[cfg(not(windows))]
fn register_and_open_settings() -> std::io::Result<()> {
    register_for_current_user()
}

#[cfg(windows)]
fn register_application(plan: &RegistrationPlan) -> std::io::Result<()> {
    use windows::{
        Win32::System::Registry::{
            HKEY_CURRENT_USER, KEY_SET_VALUE, REG_NONE, REG_OPTION_NON_VOLATILE, REG_SZ,
            RegCloseKey, RegCreateKeyExW, RegSetValueExW,
        },
        Win32::UI::Shell::{SHCNE_ASSOCCHANGED, SHCNF_IDLIST, SHChangeNotify},
        core::PCWSTR,
    };

    struct Key(windows::Win32::System::Registry::HKEY);
    impl Drop for Key {
        fn drop(&mut self) {
            unsafe {
                let _ = RegCloseKey(self.0);
            }
        }
    }

    fn wide(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn wide_bytes(value: &str) -> Vec<u8> {
        value
            .encode_utf16()
            .chain(std::iter::once(0))
            .flat_map(u16::to_le_bytes)
            .collect()
    }

    fn key(path: &str) -> std::io::Result<Key> {
        let path = wide(path);
        let mut handle = Default::default();
        let status = unsafe {
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(path.as_ptr()),
                None,
                PCWSTR::null(),
                REG_OPTION_NON_VOLATILE,
                KEY_SET_VALUE,
                None,
                &mut handle,
                None,
            )
        };
        if status.0 != 0 {
            return Err(std::io::Error::from_raw_os_error(status.0 as i32));
        }
        Ok(Key(handle))
    }

    fn set_string(key: &Key, name: &str, value: &str) -> std::io::Result<()> {
        let name = wide(name);
        let value = wide_bytes(value);
        let status =
            unsafe { RegSetValueExW(key.0, PCWSTR(name.as_ptr()), None, REG_SZ, Some(&value)) };
        if status.0 != 0 {
            return Err(std::io::Error::from_raw_os_error(status.0 as i32));
        }
        Ok(())
    }

    fn set_default_string(key: &Key, value: &str) -> std::io::Result<()> {
        let value = wide_bytes(value);
        let status = unsafe { RegSetValueExW(key.0, PCWSTR::null(), None, REG_SZ, Some(&value)) };
        if status.0 != 0 {
            return Err(std::io::Error::from_raw_os_error(status.0 as i32));
        }
        Ok(())
    }

    let app_path = format!("Software\\Classes\\Applications\\{}", plan.executable_name);
    let supported_types = key(&format!("{app_path}\\SupportedTypes"))?;
    for (extension, _) in ASSOCIATIONS {
        set_string(&supported_types, extension, "")?;
    }
    let app_command = key(&format!("{app_path}\\shell\\open\\command"))?;
    set_default_string(&app_command, &plan.open_command)?;

    for (extension, prog_id) in ASSOCIATIONS {
        let prog_id_key = key(&format!("Software\\Classes\\{prog_id}"))?;
        set_default_string(
            &prog_id_key,
            match extension {
                ".log" => "RustText log file",
                ".json" => "RustText JSON file",
                ".xml" => "RustText XML file",
                _ => "RustText text file",
            },
        )?;
        let command = key(&format!(
            "Software\\Classes\\{prog_id}\\shell\\open\\command"
        ))?;
        set_default_string(&command, &plan.open_command)?;

        let open_with = key(&format!("Software\\Classes\\{extension}\\OpenWithProgids"))?;
        let prog_id_name = wide(prog_id);
        let status = unsafe {
            RegSetValueExW(
                open_with.0,
                PCWSTR(prog_id_name.as_ptr()),
                None,
                REG_NONE,
                Some(&[]),
            )
        };
        if status.0 != 0 {
            return Err(std::io::Error::from_raw_os_error(status.0 as i32));
        }
    }

    for (path, value) in &plan.context_menu {
        set_default_string(&key(path)?, value)?;
    }

    let capabilities = key(CAPABILITIES_PATH)?;
    set_string(&capabilities, "ApplicationName", "RustText")?;
    set_string(
        &capabilities,
        "ApplicationDescription",
        "Просмотрщик и редактор логов RustText",
    )?;
    set_string(
        &capabilities,
        "ApplicationIcon",
        &format!("{},0", quote_windows_argument(&plan.executable_path)),
    )?;

    let file_associations = key(&format!("{CAPABILITIES_PATH}\\FileAssociations"))?;
    for (extension, prog_id) in ASSOCIATIONS {
        set_string(&file_associations, extension, prog_id)?;
    }

    let registered = key("Software\\RegisteredApplications")?;
    set_string(&registered, REGISTERED_APP_NAME, CAPABILITIES_PATH)?;
    unsafe {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, None, None);
    }
    Ok(())
}

#[cfg(windows)]
fn open_default_apps_settings() -> std::io::Result<()> {
    use windows::{
        Win32::{
            Foundation::HINSTANCE,
            UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL},
        },
        core::PCWSTR,
    };

    fn launch(uri: &str) -> HINSTANCE {
        let uri: Vec<_> = uri.encode_utf16().chain(std::iter::once(0)).collect();
        let open: Vec<_> = "open\0".encode_utf16().collect();
        unsafe {
            ShellExecuteW(
                None,
                PCWSTR(open.as_ptr()),
                PCWSTR(uri.as_ptr()),
                PCWSTR::null(),
                PCWSTR::null(),
                SW_SHOWNORMAL,
            )
        }
    }

    let result = launch("ms-settings:defaultapps?registeredAppUser=RustText");
    if result.0 as usize > 32 {
        return Ok(());
    }
    let fallback = launch("ms-settings:defaultapps");
    if fallback.0 as usize > 32 {
        return Ok(());
    }
    Err(std::io::Error::other(format!(
        "Не удалось открыть параметры приложений Windows (код {}).",
        fallback.0 as usize
    )))
}

#[cfg(test)]
#[path = "../../tests/unit/file_associations.rs"]
mod tests;
