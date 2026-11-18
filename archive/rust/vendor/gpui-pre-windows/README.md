# Vendored GPUI Windows backend

Source is the [gpui-pre-windows 0.3.12 package](https://crates.io/crates/gpui-pre-windows/0.3.12), licensed under [Apache 2.0](LICENSE-APACHE). The package records [Zed commit d2a4fb6](https://github.com/zed-industries/zed/commit/d2a4fb6ded7c470be25d1541281da3882495b65b).

RustText [#31](https://github.com/Illson/RustText/issues/31) removes the Windows ICU import and uses DirectWrite and DXGI interfaces available in Windows Server 2016 (build 14393).

- [Jump lists](src/destination_list.rs) scan the UTF-16 buffer in Rust. This removes the call to the Windows ICU DLL.
- [DirectWrite](src/direct_write.rs) uses the [build 14393 factory](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/nn-dwrite_3-idwritefactory4) for text and color glyphs. When Factory5 is available, custom fonts still use the in-memory loader. Build 14393 uses temporary files and [adds each font face](https://learn.microsoft.com/en-us/windows/win32/directwrite/custom-font-sets-win10).
- [DirectX](src/directx_devices.rs) uses [IDXGIFactory2](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nn-dxgi1_2-idxgifactory2), which provides the adapter enumeration and swap-chain methods the backend uses.

Verify the Release executable with the import check in `tools/Check-ReleaseImports.ps1`, then launch the unpacked ZIP and open a Cyrillic text file over RDP on Windows Server 2016. Repeat the launch and file check on Windows 11. The legacy font-path test forces the Factory4 path on modern Windows.
