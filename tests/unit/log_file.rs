use std::{fs, io::Write as _};

use super::{Change, LogBookmark, observe};

#[test]
fn observes_append_rewrite_truncation_and_replacement() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("worker.log");
    fs::write(&path, "first line\n").unwrap();
    let (first, _) = observe(&path, None).unwrap();
    assert_eq!(observe(&path, Some(&first)).unwrap().1, Change::Same);

    let mut writer = fs::OpenOptions::new().append(true).open(&path).unwrap();
    writer.write_all(b"new line\n").unwrap();
    drop(writer);
    let (grown, change) = observe(&path, Some(&first)).unwrap();
    assert_eq!(change, Change::Appended);
    assert_eq!(grown.stamp.len, 20);

    fs::write(&path, "rewritten without matching prefix\n").unwrap();
    let (rewritten, change) = observe(&path, Some(&grown)).unwrap();
    assert_eq!(change, Change::Rewritten);

    fs::write(&path, "").unwrap();
    let (cleared, change) = observe(&path, Some(&rewritten)).unwrap();
    assert_eq!(change, Change::Truncated);
    fs::rename(&path, dir.path().join("worker.old")).unwrap();
    assert!(observe(&path, Some(&cleared)).is_err());
    fs::write(&path, "replacement\n").unwrap();
    let (replacement, change) = observe(&path, Some(&cleared)).unwrap();
    assert_eq!(change, Change::Replaced);

    let bookmark = LogBookmark {
        identity: Some(first.stamp.identity),
        size: first.stamp.len,
        offset: 7,
        hidden: 5,
        follow_end: true,
        ..LogBookmark::default()
    };
    let restored: LogBookmark =
        serde_json::from_str(&serde_json::to_string(&bookmark).unwrap()).unwrap();
    assert_eq!(restored, bookmark);
    assert!(restored.can_restore(&grown.stamp));
    assert!(!restored.can_restore(&cleared.stamp));
    assert!(!restored.can_restore(&replacement.stamp));
}
