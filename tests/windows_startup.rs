#![cfg(windows)]

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};

#[test]
fn windows_executable_uses_gui_subsystem() -> std::io::Result<()> {
    const PE_HEADER_POINTER: u64 = 0x3c;
    // PE signature, COFF header, and the Subsystem field offset in the optional header.
    const SUBSYSTEM_OFFSET_FROM_PE_HEADER: u64 = 4 + 20 + 68;
    const IMAGE_SUBSYSTEM_WINDOWS_GUI: u16 = 2;

    let mut executable = File::open(env!("CARGO_BIN_EXE_rusttext"))?;
    executable.seek(SeekFrom::Start(PE_HEADER_POINTER))?;
    let mut pe_header = [0; 4];
    executable.read_exact(&mut pe_header)?;

    executable.seek(SeekFrom::Start(
        u64::from(u32::from_le_bytes(pe_header)) + SUBSYSTEM_OFFSET_FROM_PE_HEADER,
    ))?;
    let mut subsystem = [0; 2];
    executable.read_exact(&mut subsystem)?;

    assert_eq!(u16::from_le_bytes(subsystem), IMAGE_SUBSYSTEM_WINDOWS_GUI);
    Ok(())
}
