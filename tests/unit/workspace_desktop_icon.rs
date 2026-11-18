use super::*;

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
