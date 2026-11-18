use super::*;

#[test]
fn recognizes_a_desktop_junction_after_the_editor_canonicalizes_the_file() {
    let dir = tempfile::tempdir().unwrap();
    let storage = dir.path().join("Storage");
    let desktop = dir.path().join("Desktop");
    fs::create_dir_all(storage.join("Nested")).unwrap();
    let junction = std::process::Command::new("powershell.exe")
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:RUSTTEXT_TEST_JUNCTION -Target $env:RUSTTEXT_TEST_STORAGE | Out-Null",
        ])
        .env("RUSTTEXT_TEST_JUNCTION", &desktop)
        .env("RUSTTEXT_TEST_STORAGE", &storage)
        .output()
        .unwrap();
    assert!(
        junction.status.success(),
        "{}",
        String::from_utf8_lossy(&junction.stderr)
    );

    for folder in [&desktop, &desktop.join("Nested")] {
        let path = folder.join("sample.txt");
        fs::write(&path, "sample").unwrap();
        let target = FileTarget::from_picker(path).unwrap();
        let parent = target.path.parent().unwrap();
        assert!(!same_shell_path(folder, parent));
        assert!(desktop_folder_matches(
            folder,
            parent,
            Instant::now() + Duration::from_secs(5)
        ));
    }
    fs::remove_dir(&desktop).unwrap();
}

#[test]
fn compares_local_and_redirected_desktop_paths_without_opening_them() {
    let equal_paths = [
        (r"C:\Users\Иван\Desktop", r"\\?\c:\users\ИВАН\desktop"),
        (
            r"\\offline-server\profiles\Desktop\",
            r"\\?\UNC\OFFLINE-SERVER\profiles\desktop",
        ),
    ];
    for (left, right) in equal_paths {
        assert!(same_shell_path(Path::new(left), Path::new(right)));
    }

    let local = Path::new(r"\\?\C:\Logs");
    assert!(!same_shell_path(
        local,
        Path::new(r"\\offline-server\profiles\Desktop")
    ));
    assert!(!same_shell_path(local, Path::new(r"C:\Logs\Desktop")));
    assert!(!same_shell_path(local, Path::new(r"D:\Logs")));
    let deadline = Instant::now() + Duration::from_secs(5);
    for remote in [
        r"\\offline-server\profiles\Desktop",
        r"\\?\UNC\offline-server\profiles\Desktop",
    ] {
        assert!(local_folder_link(Path::new(remote), deadline).is_none());
        assert!(!desktop_folder_matches(Path::new(remote), local, deadline));
    }
}

#[test]
fn a_stalled_shell_operation_times_out_and_prevents_more_workers() {
    static ACTIVE: AtomicBool = AtomicBool::new(false);
    let (release, stalled) = mpsc::channel::<()>();
    let (started, running) = mpsc::channel();
    let save = thread::spawn(move || {
        run_desktop_operation(&ACTIVE, Duration::from_millis(500), move |_| {
            started.send(()).unwrap();
            let _ = stalled.recv_timeout(Duration::from_secs(5));
            Some(())
        })
    });
    running.recv_timeout(Duration::from_secs(5)).unwrap();
    assert!(save.join().unwrap().is_none());
    assert!(ACTIVE.load(Ordering::Acquire));
    assert!(
        run_desktop_operation::<()>(&ACTIVE, Duration::from_secs(1), |_| {
            panic!("a second worker must not start until the first has finished");
        })
        .is_none()
    );
    release.send(()).unwrap();
}

#[test]
fn restoration_requires_a_current_file_and_an_unexpired_deadline() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("saved.txt");
    fs::write(&path, "saved text").unwrap();
    let saved = FileTarget::from_picker(path.clone()).unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    assert!(saved_file_is_current(&saved, deadline));
    assert!(!saved_file_is_current(&saved, Instant::now()));

    fs::write(path, "more text written externally").unwrap();
    assert!(!saved_file_is_current(&saved, deadline));
}

#[test]
fn a_responsive_shell_operation_returns_its_result_and_releases_the_slot() {
    static ACTIVE: AtomicBool = AtomicBool::new(false);
    assert_eq!(
        run_desktop_operation(&ACTIVE, Duration::from_secs(5), |_| Some(7)),
        Some(7)
    );
    assert!(!ACTIVE.load(Ordering::Acquire));
}
