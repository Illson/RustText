fn main() {
    println!("cargo:rerun-if-changed=assets/rusttext.rc");
    println!("cargo:rerun-if-changed=assets/rusttext.ico");

    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        // GPUI's Windows backend loads the application icon from resource ID 1.
        embed_resource::compile("assets/rusttext.rc", embed_resource::NONE)
            .manifest_optional()
            .expect("failed to embed the RustText Windows icon");
    }

    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows")
        && std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc")
    {
        // GPUI's unoptimized view construction can exceed the default 1 MiB stack.
        println!("cargo:rustc-link-arg-bin=rusttext=/STACK:8388608");
    }
}
