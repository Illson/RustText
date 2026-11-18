use std::io::{Cursor, Write};

use super::{hex_sha256, parse_checksum, verify_pe_x64, write_verified_executable};

#[test]
fn parses_the_exact_detached_sha256_format() {
    let hash = "ce8d7c68b0067eda8b3fee4dbe9db65d5fae69504a17317945bc99bfa1e43f74";
    assert_eq!(
        parse_checksum(&format!("{hash}  RustText.zip\n")).unwrap(),
        hash
    );
    assert_eq!(
        parse_checksum(&format!("{hash} *RustText.zip\r\n")).unwrap(),
        hash
    );
}

#[test]
fn rejects_checksum_for_another_archive_or_invalid_digest() {
    let hash = "ce8d7c68b0067eda8b3fee4dbe9db65d5fae69504a17317945bc99bfa1e43f74";
    assert!(parse_checksum(&format!("{hash}  other.zip")).is_err());
    assert!(parse_checksum("bad-hash  RustText.zip").is_err());
    assert!(parse_checksum(hash).is_err());
}

#[test]
fn calculates_the_sha256_digest_deterministically() {
    assert_eq!(
        hex_sha256(b"RustText"),
        "3d3b95dc42892fc5078635be6fa36911f93505a306a223352ae345cffd2fa882"
    );
}

#[test]
fn accepts_only_windows_x64_pe_images() {
    let mut image = vec![0_u8; 256];
    image[..2].copy_from_slice(b"MZ");
    image[60..64].copy_from_slice(&128_u32.to_le_bytes());
    image[128..132].copy_from_slice(b"PE\0\0");
    image[132..134].copy_from_slice(&0x8664_u16.to_le_bytes());
    image[152..154].copy_from_slice(&0x20b_u16.to_le_bytes());
    assert!(verify_pe_x64(&mut Cursor::new(image.clone())).is_ok());

    image[132..134].copy_from_slice(&0x14c_u16.to_le_bytes());
    assert!(verify_pe_x64(&mut Cursor::new(image.clone())).is_err());
    image.truncate(63);
    assert!(verify_pe_x64(&mut Cursor::new(image)).is_err());
}

#[test]
fn extracts_only_the_verified_root_windows_executable() {
    let mut pe = vec![0_u8; 256];
    pe[..2].copy_from_slice(b"MZ");
    pe[60..64].copy_from_slice(&128_u32.to_le_bytes());
    pe[128..132].copy_from_slice(b"PE\0\0");
    pe[132..134].copy_from_slice(&0x8664_u16.to_le_bytes());
    pe[152..154].copy_from_slice(&0x20b_u16.to_le_bytes());

    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    zip.start_file("readme.txt", zip::write::SimpleFileOptions::default())
        .unwrap();
    zip.write_all(b"README").unwrap();
    zip.start_file("rusttext.exe", zip::write::SimpleFileOptions::default())
        .unwrap();
    zip.write_all(&pe).unwrap();
    let bytes = zip.finish().unwrap().into_inner();
    let mut archive = zip::ZipArchive::new(Cursor::new(bytes)).unwrap();
    let temporary = tempfile::tempdir().unwrap();
    let target = temporary.path().join("rusttext.exe");

    write_verified_executable(&mut archive, &target).unwrap();

    assert_eq!(std::fs::read(target).unwrap(), pe);
}

#[test]
fn rejects_zip_without_a_root_executable_without_creating_output() {
    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    zip.start_file(
        "folder/rusttext.exe",
        zip::write::SimpleFileOptions::default(),
    )
    .unwrap();
    zip.write_all(b"not a PE").unwrap();
    let bytes = zip.finish().unwrap().into_inner();
    let mut archive = zip::ZipArchive::new(Cursor::new(bytes)).unwrap();
    let temporary = tempfile::tempdir().unwrap();
    let target = temporary.path().join("rusttext.exe");

    assert!(write_verified_executable(&mut archive, &target).is_err());
    assert!(!target.exists());
}
