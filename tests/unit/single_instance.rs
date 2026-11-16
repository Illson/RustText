use super::*;

fn request() -> LaunchRequest {
    LaunchRequest {
        cwd: PathBuf::from(r"C:\Журналы\Русский каталог"),
        paths: vec![
            PathBuf::from("日志 1.txt"),
            PathBuf::from(r"D:\Важное\résumé.txt"),
        ],
        activate: true,
    }
}

#[test]
fn resolves_relative_paths_against_the_launchers_working_directory() {
    let mut request = request();
    let cwd = std::env::current_dir().unwrap().join("Русский каталог");
    let absolute = std::env::current_dir().unwrap().join("已有文件.txt");
    request.cwd = cwd.clone();
    request.paths = vec![PathBuf::from("日志 1.txt"), absolute.clone()];
    assert_eq!(
        request.absolute_paths(),
        vec![cwd.join("日志 1.txt"), absolute]
    );
}

#[test]
fn wire_format_preserves_unicode_paths_and_activation_request() {
    let original = request();
    let encoded = serde_json::to_vec(&WireRequest::from(&original)).unwrap();
    let decoded: WireRequest = serde_json::from_slice(&encoded).unwrap();
    let decoded = LaunchRequest::try_from(decoded).unwrap();
    assert_eq!(decoded, original);
}

#[test]
fn primary_accepts_a_subsequent_launch_and_keeps_its_lock_until_drop() {
    let directory = tempfile::tempdir().unwrap();
    let primary = match SingleInstance::acquire_in(directory.path(), request()).unwrap() {
        InstanceRole::Primary(primary) => primary,
        InstanceRole::Forwarded => panic!("first process must become primary"),
    };

    let activation = LaunchRequest {
        paths: Vec::new(),
        ..request()
    };
    assert!(matches!(
        SingleInstance::acquire_in(directory.path(), activation.clone()).unwrap(),
        InstanceRole::Forwarded
    ));
    let forwarded = primary.try_receive().unwrap().unwrap();
    assert_eq!(forwarded, activation);

    drop(primary);
    assert!(matches!(
        SingleInstance::acquire_in(directory.path(), request()).unwrap(),
        InstanceRole::Primary(_)
    ));
}

#[test]
fn simultaneous_launches_converge_on_one_primary() {
    const PROCESSES: usize = 4;
    let directory = tempfile::tempdir().unwrap();
    let profile = directory.path().to_path_buf();
    let start = Arc::new(std::sync::Barrier::new(PROCESSES));
    let launches: Vec<_> = (0..PROCESSES)
        .map(|_| {
            let profile = profile.clone();
            let start = Arc::clone(&start);
            thread::spawn(move || {
                start.wait();
                SingleInstance::acquire_in(&profile, request()).unwrap()
            })
        })
        .collect();
    let roles: Vec<_> = launches
        .into_iter()
        .map(|launch| launch.join().unwrap())
        .collect();

    assert_eq!(
        roles
            .iter()
            .filter(|role| matches!(role, InstanceRole::Primary(_)))
            .count(),
        1
    );
    assert_eq!(
        roles
            .iter()
            .filter(|role| matches!(role, InstanceRole::Forwarded))
            .count(),
        PROCESSES - 1
    );
}

#[test]
fn rejects_too_many_or_oversized_paths_before_serializing() {
    let mut too_many = request();
    too_many.paths = (0..=MAX_PATHS)
        .map(|index| PathBuf::from(format!("{index}.txt")))
        .collect();
    assert_eq!(
        too_many.validate().unwrap_err().kind(),
        io::ErrorKind::InvalidInput
    );

    let mut too_long = request();
    too_long.paths = vec![PathBuf::from("x".repeat(MAX_PATH_BYTES + 1))];
    assert_eq!(
        too_long.validate().unwrap_err().kind(),
        io::ErrorKind::InvalidInput
    );
}

#[test]
fn authentication_comparison_checks_the_entire_capability() {
    assert!(constant_time_eq(b"same capability", b"same capability"));
    assert!(!constant_time_eq(b"same capability", b"same capabilitY"));
    assert!(!constant_time_eq(b"short", b"longer"));
}
