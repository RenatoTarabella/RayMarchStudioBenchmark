// la versione del compilatore e la variante della build nel programma
// ("1.86.0", "native PGO"), per la riga RESULT e la modalita' benchmark
fn main() {
    let rustc = std::env::var("RUSTC").unwrap_or_else(|_| "rustc".into());
    let out = std::process::Command::new(rustc).arg("--version").output().ok();
    let v = out.and_then(|o| String::from_utf8(o.stdout).ok()).unwrap_or_default();
    let v = v.split_whitespace().nth(1).unwrap_or("?").to_string();
    println!("cargo:rustc-env=RUSTC_VERSION_TEXT={}", v);
    println!("cargo:rerun-if-env-changed=RMBENCH_VARIANT");
    println!("cargo:rustc-env=RMBENCH_VARIANT={}", std::env::var("RMBENCH_VARIANT").unwrap_or_else(|_| "release".into()));
}
