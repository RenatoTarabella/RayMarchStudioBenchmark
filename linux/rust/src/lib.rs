// RayMarch Studio Benchmark in Rust: il motore (gli stessi moduli del port
// .NET) e le parti comuni alla riga di comando e alla finestra
// (bench/benchlib.c, src/job.c): la scena, la macchina, il controllo
// dell'immagine, il PNG e i thread.
pub mod envmap;
pub mod eval;
pub mod gscene;
pub mod render;
pub mod scene;
pub mod vec;

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicUsize, Ordering};
use std::sync::Arc;

use eval::Ctx;
use gscene::GScene;
use render::Render;
use scene::Scene;

pub const VERSION: &str = "1.0.2";
pub const W: usize = 800;
pub const H: usize = 600;
const REF: [f64; 3] = [167.13, 146.51, 150.26];
const REF_TOL: f64 = 1.5;
const REF_SUM: u32 = 0x977e953f;

/* bench/benchmark.txt, come bench/benchscene.h */
pub fn bench_scene() -> String {
    let mut s = String::from(
        "RayMarchStudio 1\n\
         camera 2.55 2.55 3.15 0 0.87 0 2.6\n\
         light dir -0.4472 0.7155 0.5367 1.3 1.15 0.95 1 1 8\n\
         world 1 0.0015 1\n\
         #gpu floor checker 0.2 0.2 0.21 0.08 0.08 0.09 1 0.25\n\
         #gpu env studio \"Light tent\" 1 -30 0 0.82 0.82 0.83 1 \"\"\n\
         #gpu tone neutral 0\n\
         snap 0 0.25\n\
         object -1 group union 0 3 \"Carved cube\"\n\
         pos 0 0.9 0\nrot 0 12 0\nscale 1 1 1\nmaterial 0.7 0.03 0.02 1 64\n\
         object 0 box union 0 3 \"Plastic cube\"\n\
         pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 0.7 0.03 0.02 1 64\n\
         #gpu material 0.08 0.04 0 1.5 0\nparam 1.6 1.6 1.6 0.16\n\
         object 0 sphere subtract 0.15 3 \"Hollow\"\n\
         pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 1 0.78 0.34 1 96\n\
         #gpu material 1 0.06 0 1.5 1\nparam 0.64\n\
         object 0 cylinder subtract 0.2 3 \"Hole Y\"\n\
         pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 1 0.78 0.34 1 96\n\
         #gpu material 1 0.06 0 1.5 1\nparam 0.42 2.2 0\n\
         object 0 cylinder subtract 0.2 3 \"Hole Z\"\n\
         pos 0 0 0\nrot 90 0 0\nscale 1 1 1\nmaterial 0.92 0.92 0.94 1 128\n\
         #gpu material 1 0 0 1.5 1\nparam 0.42 2.2 0\n\
         object 0 cylinder subtract 0.2 3 \"Hole X\"\n\
         pos 0 0 0\nrot 0 0 90\nscale 1 1 1\nmaterial 0.1 0.3 0.85 1 64\n\
         #gpu material 0.1 0.05 0 1.5 0\nparam 0.42 2.2 0\n",
    );
    let corners = ["-0.74 0.74 -0.74", "0.74 0.74 -0.74", "-0.74 0.74 0.74", "0.74 0.74 0.74",
                   "-0.74 -0.74 -0.74", "0.74 -0.74 -0.74", "-0.74 -0.74 0.74", "0.74 -0.74 0.74"];
    for (i, p) in corners.iter().enumerate() {
        s += &format!("object 0 sphere union 0.1 3 \"Corner {}\"\npos {}\nrot 0 0 0\nscale 1 1 1\nmaterial 0.22 0.3 0.38 1 128\n\
                       #gpu material 0 0 1 1.5 0\nparam 0.16\n", i + 1, p);
    }
    s += "object -1 sphere union 0 3 \"Glass core\"\n\
          pos 0 0.9 0\nrot 0 0 0\nscale 1 1 1\nmaterial 0.85 0.93 1 1 128\n\
          #gpu material 0 0 1 1.5 0\nparam 0.46\n";
    s
}

// ---- la macchina: il registro di Windows senza dipendenze

#[cfg(windows)]
fn reg_string(key: &str, value: &str) -> Option<String> {
    #[link(name = "advapi32")]
    extern "system" {
        fn RegGetValueA(hkey: isize, sub: *const u8, value: *const u8, flags: u32, typ: *mut u32, data: *mut u8, len: *mut u32) -> i32;
    }
    const HKEY_LOCAL_MACHINE: isize = 0x80000002u32 as i32 as isize;
    const RRF_RT_REG_SZ: u32 = 2;
    let k = format!("{}\0", key);
    let v = format!("{}\0", value);
    let mut buf = [0u8; 256];
    let mut n = buf.len() as u32;
    let r = unsafe { RegGetValueA(HKEY_LOCAL_MACHINE, k.as_ptr(), v.as_ptr(), RRF_RT_REG_SZ, std::ptr::null_mut(), buf.as_mut_ptr(), &mut n) };
    if r != 0 { return None; }
    let s = String::from_utf8_lossy(&buf[..(n as usize).saturating_sub(1)]).trim().to_string();
    Some(s)
}

#[cfg(not(windows))]
fn reg_string(_: &str, _: &str) -> Option<String> { None }

/* sistema ("Windows 11 24H2 (build 26100)") e processore */
pub fn machine() -> (String, String) {
    let mut sys = std::env::consts::OS.to_string();
    let mut cpu = std::env::var("PROCESSOR_IDENTIFIER").unwrap_or_else(|_| "unknown CPU".into());
    let nt = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    if let Some(build) = reg_string(nt, "CurrentBuildNumber") {
        /* il nome del prodotto nel registro dice ancora "Windows 10" anche sull'11: conta la build */
        let disp = reg_string(nt, "DisplayVersion").unwrap_or_default();
        let v = if build.parse::<u32>().unwrap_or(0) >= 22000 { "11" } else { "10" };
        sys = format!("Windows {}{}{} (build {})", v, if disp.is_empty() { "" } else { " " }, disp, build);
    }
    if let Some(name) = reg_string(r"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") { cpu = name; }
    #[cfg(target_os = "linux")]
    {
        let field = |file: &str, key: &str| std::fs::read_to_string(file).ok().and_then(|t| {
            t.lines().find(|l| l.starts_with(key)).and_then(|l| l.split_once('=').or_else(|| l.split_once(':')))
                .map(|(_, v)| v.trim().trim_matches('"').to_string())
        });
        if let Some(n) = field("/etc/os-release", "PRETTY_NAME") {
            let k = std::fs::read_to_string("/proc/sys/kernel/osrelease").unwrap_or_default();
            sys = format!("{} (kernel {})", n, k.trim());
        }
        if let Some(n) = field("/proc/cpuinfo", "model name") { cpu = n; }
    }
    (sys, cpu)
}

pub fn compiler() -> String {
    format!("Rust {}, {}", env!("RUSTC_VERSION_TEXT"), env!("RMBENCH_VARIANT"))
}

// ---- il controllo dell'immagine: la media dei colori e il checksum (FNV-1a dei byte RGB)

/* il controllo: 0 sbagliata, 1 giusta, 2 identica al riferimento, -1 nessun
 * riferimento a questa risoluzione; la media dei colori e il checksum */
pub fn check(buf: &[u32], w: usize, h: usize, mut rgb: Option<&mut [u8]>) -> (i32, [f64; 3], u32) {
    let mut s = 2166136261u32;
    let mut mean = [0.0f64; 3];
    for (i, &px) in buf.iter().enumerate() {
        let c = [(px >> 16) as u8, (px >> 8) as u8, px as u8];
        for k in 0..3 {
            if let Some(rgb) = rgb.as_deref_mut() { rgb[i * 3 + k] = c[k]; }
            mean[k] += c[k] as f64;
            s = (s ^ c[k] as u32).wrapping_mul(16777619);
        }
    }
    for m in &mut mean { *m /= (w * h) as f64; }
    if w != W || h != H { return (-1, mean, s); }
    let ok = (0..3).all(|k| mean[k] >= REF[k] - REF_TOL && mean[k] <= REF[k] + REF_TOL);
    (if !ok { 0 } else if s == REF_SUM { 2 } else { 1 }, mean, s)
}

pub fn verdict(c: i32) -> &'static str {
    match c {
        2 => "OK, identical to the reference",
        1 => "OK (last bits differ: compiler rounding)",
        -1 => "no reference at this size: compare with the other versions",
        _ => "WRONG IMAGE",
    }
}
pub fn short_verdict(c: i32) -> &'static str {
    match c { 2 => "image identical", 1 => "image OK", -1 => "image not checked", _ => "WRONG IMAGE" }
}

// ---- il PNG: righe con filtro 0, zlib con blocchi non compressi (niente dipendenze)

pub fn write_png(path: &str, rgb: &[u8], w: usize, h: usize) -> std::io::Result<()> {
    fn crc(mut c: u32, d: &[u8]) -> u32 {
        for &b in d {
            c ^= b as u32;
            for _ in 0..8 { c = if c & 1 != 0 { 0xedb88320 ^ (c >> 1) } else { c >> 1 }; }
        }
        c
    }
    fn chunk(out: &mut Vec<u8>, typ: &[u8], data: &[u8]) {
        out.extend_from_slice(&(data.len() as u32).to_be_bytes());
        out.extend_from_slice(typ);
        out.extend_from_slice(data);
        out.extend_from_slice(&(crc(crc(0xffffffff, typ), data) ^ 0xffffffff).to_be_bytes());
    }
    let mut raw = Vec::with_capacity((w * 3 + 1) * h);
    for y in 0..h {
        raw.push(0);
        raw.extend_from_slice(&rgb[y * w * 3..(y + 1) * w * 3]);
    }
    let mut z = vec![0x78, 0x01];
    for (i, blk) in raw.chunks(65535).enumerate() {
        let last = (i + 1) * 65535 >= raw.len();
        z.push(last as u8);
        z.extend_from_slice(&(blk.len() as u16).to_le_bytes());
        z.extend_from_slice(&(!(blk.len() as u16)).to_le_bytes());
        z.extend_from_slice(blk);
    }
    let (mut a, mut b) = (1u32, 0u32);
    for &x in &raw { a = (a + x as u32) % 65521; b = (b + a) % 65521; }
    z.extend_from_slice(&(b << 16 | a).to_be_bytes());
    let mut ihdr = Vec::new();
    ihdr.extend_from_slice(&(w as u32).to_be_bytes());
    ihdr.extend_from_slice(&(h as u32).to_be_bytes());
    ihdr.extend_from_slice(&[8, 2, 0, 0, 0]);
    let mut out = vec![137, 80, 78, 71, 13, 10, 26, 10];
    chunk(&mut out, b"IHDR", &ihdr);
    chunk(&mut out, b"IDAT", &z);
    chunk(&mut out, b"IEND", &[]);
    std::fs::write(path, out)
}

/* la scena dentro il programma pronta da rendere, w x h; resta in memoria
 * fino alla fine (i thread della finestra la usano senza tempi di vita) */
pub fn setup(w: usize, h: usize) -> Result<&'static Render<'static>, String> { setup_scene(&bench_scene(), w, h, false) }

/* una scena caricata da file: i materiali misti anche dentro il vetro */
pub fn setup_text(text: &str, w: usize, h: usize) -> Result<&'static Render<'static>, String> { setup_scene(text, w, h, true) }

fn setup_scene(text: &str, w: usize, h: usize, mix: bool) -> Result<&'static Render<'static>, String> {
    let s: &'static mut Scene = Box::leak(Box::new(Scene::read(text)?));
    let g: &'static GScene = Box::leak(Box::new(GScene::build(s)?));
    let s: &'static Scene = s;
    let mut r = Render::new(s, g, w, h);
    r.mix = mix;
    Ok(Box::leak(Box::new(r)))
}

/* il buffer dei pixel (0x00RRGGBB) allineato a 64 byte: una riga di tile =
 * una riga di cache, nessun thread scrive nella riga di un altro */
pub struct Pixels {
    p: *mut AtomicU32,
    n: usize,
}

unsafe impl Send for Pixels {}
unsafe impl Sync for Pixels {}

impl Pixels {
    fn layout(n: usize) -> std::alloc::Layout { std::alloc::Layout::from_size_align(n.max(1) * 4, 64).unwrap() }
    pub fn new(n: usize) -> Pixels {
        let p = unsafe { std::alloc::alloc_zeroed(Self::layout(n)) } as *mut AtomicU32;
        assert!(!p.is_null());
        Pixels { p, n }
    }
    pub fn to_vec(&self) -> Vec<u32> { self.iter().map(|a| a.load(Ordering::Relaxed)).collect() }
}

impl std::ops::Deref for Pixels {
    type Target = [AtomicU32];
    fn deref(&self) -> &[AtomicU32] { unsafe { std::slice::from_raw_parts(self.p, self.n) } }
}

impl Drop for Pixels {
    fn drop(&mut self) { unsafe { std::alloc::dealloc(self.p as *mut u8, Self::layout(self.n)) } }
}

pub const TILE: usize = 16;

/* un contatore nella sua riga di cache */
#[repr(align(64))]
#[derive(Default)]
struct Counter(AtomicUsize);

struct Shared {
    next: Counter,              /* il prossimo tile */
    done: Counter,              /* i tile finiti (per la percentuale) */
    alive: Counter,
    abort: AtomicBool,
}

/* Un rendering in corso: n thread prendono tile di 16 x 16 pixel da un
 * contatore atomico (il carico si bilancia da solo); ogni thread ha il suo Ctx */
pub struct Job {
    sh: Arc<Shared>,
    threads: Vec<std::thread::JoinHandle<()>>,
    ntiles: usize,
}

impl Job {
    pub fn start(r: &'static Render<'static>, buf: Arc<Pixels>, n: usize) -> Job {
        let tx = r.w.div_ceil(TILE);
        let ntiles = tx * r.h.div_ceil(TILE);
        let sh = Arc::new(Shared { next: Counter::default(), done: Counter::default(), alive: Counter(AtomicUsize::new(n)), abort: AtomicBool::new(false) });
        let threads = (0..n).map(|_| {
            let (sh, buf) = (sh.clone(), buf.clone());
            std::thread::Builder::new().stack_size(4 << 20).spawn(move || {
                let mut c = Ctx::new(r.g);
                while !sh.abort.load(Ordering::Relaxed) {
                    let t = sh.next.0.fetch_add(1, Ordering::Relaxed);
                    if t >= ntiles { break; }
                    let (x0, y0) = (t % tx * TILE, t / tx * TILE);
                    let (x1, y1) = ((x0 + TILE).min(r.w), (y0 + TILE).min(r.h));
                    for y in y0..y1 {
                        let row = &buf[y * r.w + x0..y * r.w + x1];
                        for (px, x) in row.iter().zip(x0..x1) { px.store(r.pixel(&mut c, x, y), Ordering::Relaxed); }
                    }
                    sh.done.0.fetch_add(1, Ordering::Relaxed);
                }
                sh.alive.0.fetch_sub(1, Ordering::Release);
            }).unwrap()
        }).collect();
        Job { sh, threads, ntiles }
    }

    /* true se e' in corso; la percentuale */
    pub fn busy(&self) -> (bool, usize) {
        (self.sh.alive.0.load(Ordering::Acquire) > 0, self.sh.done.0.load(Ordering::Relaxed) * 100 / self.ntiles)
    }

    pub fn threads(&self) -> usize { self.threads.len() }

    /* aspetta la fine */
    pub fn wait(self) {
        for t in self.threads { let _ = t.join(); }
    }

    /* ferma i thread e aspetta che escano */
    pub fn stop(self) {
        self.sh.abort.store(true, Ordering::Relaxed);
        self.wait();
    }

    pub fn probe(&self) -> Probe { Probe { sh: self.sh.clone() } }
}

/* chi aspetta la fine da un altro thread (la finestra) senza tenere il Job */
pub struct Probe {
    sh: Arc<Shared>,
}

impl Probe {
    pub fn busy(&self) -> bool { self.sh.alive.0.load(Ordering::Acquire) > 0 }
}

/* la modalita' benchmark: un riscaldamento, poi reps render; mediana e minimo */
pub fn benchmark(r: &'static Render<'static>, buf: &Arc<Pixels>, n: usize, reps: usize) {
    Job::start(r, buf.clone(), n).wait();
    let mut t: Vec<f64> = (0..reps).map(|_| {
        let t0 = std::time::Instant::now();
        Job::start(r, buf.clone(), n).wait();
        t0.elapsed().as_secs_f64()
    }).collect();
    t.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let med = if reps % 2 == 1 { t[reps / 2] } else { (t[reps / 2 - 1] + t[reps / 2]) / 2.0 };
    let (c, _, sum) = check(&buf.to_vec(), r.w, r.h, None);
    let v = match c { 2 => "identical to the reference", -1 => "no reference at this size: compare with the other versions", _ => "DIFFERENT FROM THE REFERENCE" };
    println!("BENCH | {} | {:2} thread{} | {}x{} | median {:8.4} s | min {:8.4} s | checksum {:08x} ({})",
             compiler(), n, if n > 1 { "s" } else { " " }, r.w, r.h, med, t[0], sum, v);
}
