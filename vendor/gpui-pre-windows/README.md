# Vendored GPUI Windows backend

Source is the [gpui-pre-windows 0.3.12 package](https://crates.io/crates/gpui-pre-windows/0.3.12), licensed under [Apache 2.0](LICENSE-APACHE). The package records [Zed commit d2a4fb6](https://github.com/zed-industries/zed/commit/d2a4fb6ded7c470be25d1541281da3882495b65b).

RustText [#31](https://github.com/Illson/RustText/issues/31) changes only the UTF-16 scan in [destination_list.rs](src/destination_list.rs). A bounded Rust scan replaces the call to the Windows ICU DLL. The `Win32_Globalization` bindings also provide `GetUserDefaultLocaleName` for DirectWrite.

When a compatible dependency version includes the fix, remove this copy and the Cargo patch and verify the Release executable with `tools/Check-ReleaseImports.ps1`.
