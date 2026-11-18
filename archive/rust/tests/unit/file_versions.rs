use super::*;

fn store(dir: &Path, max_file_bytes: u64, total_bytes: u64) -> VersionStore {
    let store = VersionStore::open(dir.join("versions")).unwrap();
    store
        .set_settings(VersionSettings {
            max_file_bytes,
            total_bytes,
        })
        .unwrap();
    store
}

#[test]
fn deduplicates_unchanged_content_and_survives_restart_with_settings() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 1024, 4096);
    let path = dir.path().join("document.txt");
    fs::write(&path, b"first\r\n").unwrap();
    let first = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    assert_eq!(
        store
            .capture(&path, TextEncoding::Utf8)
            .unwrap()
            .unwrap()
            .id,
        first.id
    );
    fs::write(&path, b"second\n").unwrap();
    let second = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    let reopened = VersionStore::open(store.directory.clone()).unwrap();
    assert_eq!(
        reopened.settings().unwrap(),
        VersionSettings {
            max_file_bytes: 1024,
            total_bytes: 4096
        }
    );
    let list = reopened.list(&path).unwrap();
    assert_eq!(list.len(), 2);
    assert_eq!(list[0].id, second.id);
    assert_eq!(list[1].id, first.id);
    assert!(list[0].captured_millis > 0);
}

#[test]
fn keeps_exact_encoding_and_newlines_and_decodes_for_preview() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 1024, 4096);
    let path = dir.path().join("document.txt");
    for (bytes, encoding, expected) in [
        (
            vec![0xff, 0xfe, b'A', 0, 13, 0, 10, 0],
            TextEncoding::Utf16Le,
            "A\r\n",
        ),
        (
            vec![0xef, 0xbb, 0xbf, b'B', 13, 10],
            TextEncoding::Utf8Bom,
            "B\r\n",
        ),
        (
            vec![0xcf, 0xf0, 13, 10],
            TextEncoding::Windows1251,
            "Пр\r\n",
        ),
    ] {
        fs::write(&path, &bytes).unwrap();
        let info = store.capture(&path, encoding).unwrap().unwrap();
        let content = store.load_version(&path, &info.id).unwrap();
        assert_eq!(content.bytes, bytes);
        assert_eq!(content.info.id, info.id);
        let (text, format) = content.decode().unwrap();
        assert_eq!(text, expected);
        assert_eq!(format.encode(&text).unwrap(), bytes);
    }
}

#[test]
fn prunes_oldest_versions_but_keeps_the_newest_of_each_file() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 10, 8);
    let first = dir.path().join("a.txt");
    let second = dir.path().join("b.txt");
    fs::write(&first, b"aaaa").unwrap();
    store.capture(&first, TextEncoding::Utf8).unwrap();
    fs::write(&second, b"bbbb").unwrap();
    let b = store.capture(&second, TextEncoding::Utf8).unwrap().unwrap();
    fs::write(&first, b"cccc").unwrap();
    let c = store.capture(&first, TextEncoding::Utf8).unwrap().unwrap();
    assert_eq!(store.list(&first).unwrap().len(), 1);
    assert_eq!(store.list(&second).unwrap().len(), 1);
    assert_eq!(store.load_version(&first, &c.id).unwrap().bytes, b"cccc");
    assert_eq!(store.load_version(&second, &b.id).unwrap().bytes, b"bbbb");
    assert_eq!(
        fs::read_dir(store.directory.join("blobs")).unwrap().count(),
        2
    );
}

#[test]
fn failed_quota_capture_and_settings_leave_previous_snapshots_intact() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 10, 4);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"aaaa").unwrap();
    let first = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    fs::write(&path, b"bbbbb").unwrap();
    assert!(store.capture(&path, TextEncoding::Utf8).is_err());
    assert!(
        store
            .set_settings(VersionSettings {
                max_file_bytes: 10,
                total_bytes: 3
            })
            .is_err()
    );
    assert_eq!(store.settings().unwrap().total_bytes, 4);
    assert_eq!(store.list(&path).unwrap()[0].id, first.id);
    assert_eq!(store.load_version(&path, &first.id).unwrap().bytes, b"aaaa");
    assert_eq!(fs::read(&path).unwrap(), b"bbbbb");
}

#[test]
fn prepare_restore_records_current_disk_bytes_and_never_changes_the_source() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 10, 100);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"old").unwrap();
    let first = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    fs::write(&path, b"unsaved").unwrap();
    let content = store
        .prepare_restore(&path, &first.id, TextEncoding::Utf8)
        .unwrap();
    assert_eq!(content.bytes, b"old");
    let list = store.list(&path).unwrap();
    assert_eq!(list.len(), 2);
    assert_eq!(
        store.load_version(&path, &list[0].id).unwrap().bytes,
        b"unsaved"
    );
    assert_eq!(fs::read(&path).unwrap(), b"unsaved");
    fs::write(&path, b"too big for snapshot").unwrap();
    assert!(store.capture(&path, TextEncoding::Utf8).unwrap().is_none());
    assert!(
        store
            .prepare_restore(&path, &first.id, TextEncoding::Utf8)
            .is_err()
    );
    assert_eq!(store.list(&path).unwrap().len(), 2);
}

#[test]
fn corrupt_manifest_ids_and_checksums_never_escape_the_store() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"safe").unwrap();
    let info = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    let original = fs::read(store.directory.join("manifest.json")).unwrap();
    assert!(store.load_version(&path, "../a.txt").is_err());
    for field in ["id", "checksum"] {
        let mut value: serde_json::Value = serde_json::from_slice(&original).unwrap();
        value["versions"][0][field] = "../../a.txt".into();
        fs::write(
            store.directory.join("manifest.json"),
            serde_json::to_vec(&value).unwrap(),
        )
        .unwrap();
        assert!(store.capture(&path, TextEncoding::Utf8).is_err());
        assert!(store.load_version(&path, &info.id).is_err());
        assert_eq!(fs::read(&path).unwrap(), b"safe");
    }
    fs::write(store.directory.join("manifest.json"), &original).unwrap();
    assert_eq!(store.load_version(&path, &info.id).unwrap().bytes, b"safe");
}

#[test]
fn detects_partial_metadata_and_blob_writes_without_overwriting_them() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"first").unwrap();
    let info = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    let manifest = store.directory.join("manifest.json");
    let old_manifest = fs::read(&manifest).unwrap();
    fs::write(&manifest, b"{").unwrap();
    assert!(store.capture(&path, TextEncoding::Utf8).is_err());
    assert_eq!(fs::read(&manifest).unwrap(), b"{");
    fs::write(&manifest, old_manifest).unwrap();
    let blob = store.directory.join("blobs").join(info.checksum);
    fs::write(&blob, b"f").unwrap();
    assert!(store.load_version(&path, &info.id).is_err());
    assert!(store.capture(&path, TextEncoding::Utf8).is_err());
    assert_eq!(fs::read(&blob).unwrap(), b"f");
}

#[test]
fn a_blocked_blob_directory_preserves_metadata_and_existing_snapshots() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"first").unwrap();
    let info = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    let blobs = store.directory.join("blobs");
    let backup = store.directory.join("saved-blobs");
    fs::rename(&blobs, &backup).unwrap();
    fs::write(&blobs, b"blocked").unwrap();
    fs::write(&path, b"second").unwrap();
    assert!(store.capture(&path, TextEncoding::Utf8).is_err());
    fs::remove_file(&blobs).unwrap();
    fs::rename(&backup, &blobs).unwrap();
    assert_eq!(store.list(&path).unwrap().len(), 1);
    assert_eq!(store.load_version(&path, &info.id).unwrap().bytes, b"first");
}

#[test]
fn snapshots_of_store_files_are_rejected_and_missing_sources_remain_readable() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"first").unwrap();
    let info = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    assert!(
        store
            .capture(&store.directory.join("manifest.json"), TextEncoding::Utf8)
            .is_err()
    );
    fs::remove_file(&path).unwrap();
    assert_eq!(
        store.load_version(&info.path, &info.id).unwrap().bytes,
        b"first"
    );
}

#[cfg(windows)]
#[test]
fn failed_atomic_manifest_replace_keeps_previous_version_and_removes_new_blob() {
    use std::os::windows::fs::OpenOptionsExt;
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"first").unwrap();
    let info = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    let manifest = store.directory.join("manifest.json");
    let previous = fs::read(&manifest).unwrap();
    let guard = OpenOptions::new()
        .read(true)
        .share_mode(3)
        .open(&manifest)
        .unwrap();
    fs::write(&path, b"second").unwrap();
    assert!(store.capture(&path, TextEncoding::Utf8).is_err());
    assert_eq!(fs::read(&manifest).unwrap(), previous);
    assert_eq!(
        fs::read_dir(store.directory.join("blobs")).unwrap().count(),
        1
    );
    drop(guard);
    assert_eq!(store.load_version(&path, &info.id).unwrap().bytes, b"first");
    store.capture(&path, TextEncoding::Utf8).unwrap();
    assert_eq!(store.list(&path).unwrap().len(), 2);
}

#[test]
fn separate_store_handles_merge_and_shared_blobs_survive_pruning() {
    let dir = tempfile::tempdir().unwrap();
    let first = store(dir.path(), 100, 8);
    let second = VersionStore::open(first.directory.clone()).unwrap();
    let a = dir.path().join("a.txt");
    let b = dir.path().join("b.txt");
    fs::write(&a, b"same").unwrap();
    fs::write(&b, b"same").unwrap();
    first.capture(&a, TextEncoding::Utf8).unwrap();
    let original_b = second.capture(&b, TextEncoding::Utf8).unwrap().unwrap();
    assert_eq!(
        fs::read_dir(first.directory.join("blobs")).unwrap().count(),
        1
    );
    fs::write(&a, b"new!").unwrap();
    first.capture(&a, TextEncoding::Utf8).unwrap();
    assert_eq!(second.list(&a).unwrap().len(), 1);
    assert_eq!(
        second.load_version(&b, &original_b.id).unwrap().bytes,
        b"same"
    );
    assert_eq!(fs::read(&b).unwrap(), b"same");
}

#[test]
fn reducing_quota_prunes_old_versions_and_preserves_latest() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("a.txt");
    fs::write(&path, b"first").unwrap();
    store.capture(&path, TextEncoding::Utf8).unwrap();
    fs::write(&path, b"second").unwrap();
    let newest = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    store
        .set_settings(VersionSettings {
            max_file_bytes: 100,
            total_bytes: 6,
        })
        .unwrap();
    assert_eq!(store.list(&path).unwrap().len(), 1);
    assert_eq!(
        store.load_version(&path, &newest.id).unwrap().bytes,
        b"second"
    );
    assert_eq!(
        fs::read_dir(store.directory.join("blobs")).unwrap().count(),
        1
    );
}

#[test]
fn restores_captured_encoding_after_reopening_with_another_encoding() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 1024, 4096);
    let path = dir.path().join("document.txt");
    for (captured_encoding, current_encoding) in [
        (TextEncoding::Utf8, TextEncoding::Windows1251),
        (TextEncoding::Windows1251, TextEncoding::Utf8),
        (TextEncoding::Utf8Bom, TextEncoding::Windows1251),
    ] {
        let original = TextFormat::for_encoding(captured_encoding)
            .encode("Привет\r\n")
            .unwrap();
        fs::write(&path, &original).unwrap();
        let version = store.capture(&path, captured_encoding).unwrap().unwrap();
        let current = TextFormat::for_encoding(current_encoding)
            .encode("Текущее\n")
            .unwrap();
        fs::write(&path, &current).unwrap();
        let reopened_store = VersionStore::open(store.directory.clone()).unwrap();
        let restored = reopened_store
            .prepare_restore(&path, &version.id, current_encoding)
            .unwrap();
        let (text, format) = restored.decode().unwrap();
        assert_eq!(text, "Привет\r\n");
        assert!(format.encoding == captured_encoding);
        assert_eq!(format.encode(&text).unwrap(), original);
        let newest = reopened_store.list(&path).unwrap().remove(0);
        let backup = reopened_store.load_version(&path, &newest.id).unwrap();
        assert_eq!(backup.bytes, current);
        assert!(backup.decode().unwrap().1.encoding == current_encoding);
        assert_eq!(fs::read(&path).unwrap(), current);
    }
}

#[test]
fn equal_bytes_with_different_encoding_create_distinct_versions() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("document.txt");
    fs::write(&path, b"ASCII\r\n").unwrap();
    let utf8 = store.capture(&path, TextEncoding::Utf8).unwrap().unwrap();
    let cp1251 = store
        .capture(&path, TextEncoding::Windows1251)
        .unwrap()
        .unwrap();
    assert_ne!(utf8.id, cp1251.id);
    assert_eq!(
        store
            .capture(&path, TextEncoding::Windows1251)
            .unwrap()
            .unwrap()
            .id,
        cp1251.id
    );
    assert_eq!(store.list(&path).unwrap().len(), 2);
    assert!(
        store
            .load_version(&path, &utf8.id)
            .unwrap()
            .decode()
            .unwrap()
            .1
            .encoding
            == TextEncoding::Utf8
    );
    assert!(
        store
            .load_version(&path, &cp1251.id)
            .unwrap()
            .decode()
            .unwrap()
            .1
            .encoding
            == TextEncoding::Windows1251
    );
    assert_eq!(
        fs::read_dir(store.directory.join("blobs")).unwrap().count(),
        1
    );
}

#[test]
fn legacy_metadata_preserves_bytes_but_never_guesses_encoding() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 1024, 4096);
    let path = dir.path().join("document.txt");
    let bytes = TextFormat::for_encoding(TextEncoding::Windows1251)
        .encode("Привет\r\n")
        .unwrap();
    fs::write(&path, &bytes).unwrap();
    let info = store
        .capture(&path, TextEncoding::Windows1251)
        .unwrap()
        .unwrap();
    let manifest_path = store.directory.join("manifest.json");
    let mut manifest: serde_json::Value =
        serde_json::from_slice(&fs::read(&manifest_path).unwrap()).unwrap();
    manifest["versions"][0]
        .as_object_mut()
        .unwrap()
        .remove("encoding");
    let legacy_manifest = serde_json::to_vec(&manifest).unwrap();
    fs::write(&manifest_path, &legacy_manifest).unwrap();
    let reopened = VersionStore::open(store.directory.clone()).unwrap();
    assert_eq!(reopened.list(&path).unwrap().len(), 1);
    let legacy = reopened.load_version(&path, &info.id).unwrap();
    assert_eq!(legacy.bytes, bytes);
    assert!(
        legacy
            .decode()
            .err()
            .unwrap()
            .to_string()
            .contains("Кодировка старой версии неизвестна")
    );
    fs::write(&path, b"current UTF-8").unwrap();
    assert!(
        reopened
            .prepare_restore(&path, &info.id, TextEncoding::Utf8)
            .is_err()
    );
    assert_eq!(fs::read(&manifest_path).unwrap(), legacy_manifest);
    assert_eq!(fs::read(&path).unwrap(), b"current UTF-8");
    fs::write(&path, &bytes).unwrap();
    let new = reopened
        .capture(&path, TextEncoding::Windows1251)
        .unwrap()
        .unwrap();
    assert_ne!(new.id, info.id);
    assert_eq!(reopened.list(&path).unwrap().len(), 2);
    assert_eq!(
        reopened
            .load_version(&path, &new.id)
            .unwrap()
            .decode()
            .unwrap()
            .0,
        "Привет\r\n"
    );
    assert_eq!(reopened.load_version(&path, &info.id).unwrap().bytes, bytes);
}

#[test]
fn encoding_metadata_must_match_bom() {
    let dir = tempfile::tempdir().unwrap();
    let store = store(dir.path(), 100, 1000);
    let path = dir.path().join("document.txt");
    fs::write(&path, [0xef, 0xbb, 0xbf, b'A']).unwrap();
    let info = store
        .capture(&path, TextEncoding::Utf8Bom)
        .unwrap()
        .unwrap();
    let manifest_path = store.directory.join("manifest.json");
    let mut manifest: serde_json::Value =
        serde_json::from_slice(&fs::read(&manifest_path).unwrap()).unwrap();
    manifest["versions"][0]["encoding"] = "Utf8".into();
    fs::write(&manifest_path, serde_json::to_vec(&manifest).unwrap()).unwrap();
    assert!(
        store
            .load_version(&path, &info.id)
            .unwrap()
            .decode()
            .is_err()
    );
    assert!(
        store
            .prepare_restore(&path, &info.id, TextEncoding::Utf8Bom)
            .is_err()
    );
    assert_eq!(store.list(&path).unwrap().len(), 1);
}
