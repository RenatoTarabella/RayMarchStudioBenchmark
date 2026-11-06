#!/bin/sh
# I benchmark per Linux: C (clang, LTO, PGO), Rust (LTO, PGO) e .NET 10 (ReadyToRun).
# Tutto in release, gli eseguibili vanno in linux/out.
#   sh build.sh          tutti e tre
#   sh build.sh c        solo uno (c, rust, net)
# Serve: clang con lld e llvm-profdata; per Rust rustc/cargo e llvm-tools
# (pacman -S rust, oppure rustup component add llvm-tools); .NET 10 SDK.
# Niente FMA in nessuna versione (cambierebbe l'immagine): -ffp-contract=off.
set -e
cd "$(dirname "$0")"
H=$PWD
O=$H/out
mkdir -p "$O"
WHAT=${1:-all}

build_c() {
    echo "== C"
    cd "$H/c"
    SRC="engine.c bench.c main.c"
    BASE="-std=gnu11 -O3 -march=native -ffp-contract=off -fno-math-errno -Wall"
    P=$H/c/pgo
    rm -rf "$P" && mkdir -p "$P"
    clang $BASE -flto -fuse-ld=lld -fprofile-generate="$P" -DBUILD_VARIANT='"PGO training"' -o "$P/rmbench-c-train" $SRC -lm -lpthread
    (cd "$P" && ./rmbench-c-train -n -t 1 >/dev/null 2>&1 && ./rmbench-c-train -n >/dev/null 2>&1)
    llvm-profdata merge -o "$P/c.profdata" "$P"/*.profraw
    clang $BASE -flto -fuse-ld=lld -fprofile-use="$P/c.profdata" -DBUILD_VARIANT='"O3 native LTO PGO"' -o "$O/rmbench-c" $SRC -lm -lpthread
    # la finestra (SDL3), con lo stesso profilo
    clang $BASE -flto -fuse-ld=lld -fprofile-use="$P/c.profdata" -Wno-profile-instr-unprofiled -DBUILD_VARIANT='"O3 native LTO PGO"' \
        -o "$O/RayMarchStudioBenchmark-c" engine.c bench.c gui.c $(pkg-config --cflags --libs sdl3 freetype2) -lm -lpthread
    rm -rf "$P"
}

build_rust() {
    echo "== Rust"
    cd "$H/rust"
    PROFDATA=$(ls "$(rustc --print sysroot)"/lib/rustlib/*/bin/llvm-profdata 2>/dev/null | head -1)
    [ -n "$PROFDATA" ] || PROFDATA=$(command -v llvm-profdata)
    PG=$H/rust/target/pgo-data
    RUSTFLAGS="-C target-cpu=native" RMBENCH_VARIANT="native" cargo build --release --target-dir target/native
    cp target/native/release/rmbench-rs "$O/rmbench-rs-native"
    rm -rf "$PG"
    RUSTFLAGS="-C target-cpu=native -C profile-generate=$PG" RMBENCH_VARIANT="PGO training" cargo build --release --target-dir target/pgo-gen
    target/pgo-gen/release/rmbench-rs -n -t 1 >/dev/null 2>&1
    target/pgo-gen/release/rmbench-rs -n >/dev/null 2>&1
    "$PROFDATA" merge -o "$PG/rs.profdata" "$PG"/*.profraw
    RUSTFLAGS="-C target-cpu=native -C profile-use=$PG/rs.profdata" RMBENCH_VARIANT="native PGO" cargo build --release --target-dir target/pgo-use
    cp target/pgo-use/release/rmbench-rs "$O/rmbench-rs-pgo"
}

build_net() {
    echo "== .NET 10"
    cd "$H/net/cli"
    dotnet publish -c Release -r linux-x64 -o "$O/net"
    cd "$H/net/gui"
    dotnet publish -c Release -r linux-x64 -o "$O/net"
}

case $WHAT in
    c) build_c ;;
    rust) build_rust ;;
    net) build_net ;;
    all) build_c; build_rust; build_net ;;
    *) echo "uso: sh build.sh [c|rust|net]"; exit 1 ;;
esac
echo; ls -l "$O"
