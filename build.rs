fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows")
        && std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc")
    {
        // GPUI's unoptimized view construction can exceed the default 1 MiB stack.
        println!("cargo:rustc-link-arg-bin=rusttext=/STACK:8388608");
    }
}
