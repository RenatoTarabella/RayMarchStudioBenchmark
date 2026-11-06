// RayMarch Studio Benchmark in Rust, a riga di comando, allineato alle
// versioni C (c/) e .NET (net/): la stessa scena, gli stessi tile 16 x 16
// distribuiti da un contatore atomico, lo stesso controllo dell'immagine e
// la stessa modalita' benchmark.
//   rmbench-rs [-t thread] [-s scala] [-b [ripetizioni]] [-o immagine.png] [-n] [-f scena.txt]
// -t: quanti thread (tutte le CPU); -s: risoluzione 800s x 600s; -b: un
// render di riscaldamento, poi N render (10) con mediana e minimo, a 1
// thread e a tutti i thread (o solo a -t); -o: dove salvare l'immagine;
// -f: un'altra scena da un file al posto di quella del benchmark (con i
// materiali misti anche dentro il vetro).
use std::io::Write;
use std::sync::Arc;
use std::time::{Duration, Instant};

use rmbench_rs::*;

/* il rendering su n thread, il tempo in secondi; la percentuale ogni 20 ms */
fn run(r: &'static render::Render<'static>, buf: &Arc<Pixels>, nthreads: usize) -> f64 {
    let t0 = Instant::now();
    let job = Job::start(r, buf.clone(), nthreads);
    let mut last = usize::MAX;
    loop {
        let (busy, pc) = job.busy();
        if !busy { break; }
        if pc != last { eprint!("\r  {:3}%", pc); let _ = std::io::stderr().flush(); last = pc; }
        std::thread::sleep(Duration::from_millis(20));
    }
    job.wait();
    eprint!("\r      \r");
    t0.elapsed().as_secs_f64()
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let (mut out, mut nthreads, mut reps, mut scale, mut save) = ("rmbench-rs.png".to_string(), 0usize, 0usize, 1.0f64, true);
    let mut file: Option<String> = None;
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "-t" if i + 1 < args.len() => { i += 1; nthreads = args[i].parse().unwrap_or(0); }
            "-s" if i + 1 < args.len() => { i += 1; scale = args[i].parse().unwrap_or(1.0); }
            "-o" if i + 1 < args.len() => { i += 1; out = args[i].clone(); }
            "-f" if i + 1 < args.len() => { i += 1; file = Some(args[i].clone()); }
            "-n" => save = false,
            "-b" => {
                reps = 10;
                if let Some(n) = args.get(i + 1).and_then(|a| a.parse::<usize>().ok()).filter(|&n| n > 0) { reps = n; i += 1; }
            }
            _ => {
                eprintln!("usage: rmbench-rs [-t threads] [-s scale] [-b [repeats]] [-o image.png] [-n] [-f scene.txt]\n  \
                           -t  how many threads (default: all CPUs)\n  \
                           -s  resolution 800*scale x 600*scale (default 1)\n  \
                           -b  benchmark: a warm-up render, then N renders (default 10), median and minimum,\n      \
                           at 1 thread and at all threads (or only at -t)\n  \
                           -o  where to save the image (default rmbench-rs.png); -n  do not save it\n  \
                           -f  render the scene in this file instead of the benchmark scene");
                std::process::exit(1);
            }
        }
        i += 1;
    }
    let all = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1);
    let (w, h) = ((800.0 * scale + 0.5) as usize, (600.0 * scale + 0.5) as usize);
    let (sys, cpu) = machine();
    println!("RayMarch Studio Benchmark {} (Rust)", VERSION);
    println!("System:   {}", sys);
    println!("CPU:      {}", cpu);
    println!("Program:  {}", compiler());
    if let Some(f) = &file { println!("Scene:    {}", f); }
    println!("Image:    {} x {} = {} pixels, 1 sample per pixel, up to 6 bounces\n", w, h, w * h);
    let _ = std::io::stdout().flush();

    /* la scena: il testo, poi la preparazione (anche l'ambiente studio) */
    let text = file.as_ref().map(|f| std::fs::read_to_string(f).unwrap_or_else(|_| { eprintln!("could not read {}", f); std::process::exit(1) }));
    let t0 = Instant::now();
    let r = match &text { Some(t) => setup_text(t, w, h), None => setup(w, h) }
        .unwrap_or_else(|e| { eprintln!("scene: {}", e); std::process::exit(1) });
    let t_setup = t0.elapsed().as_secs_f64();
    let buf = Arc::new(Pixels::new(w * h));

    if reps > 0 {
        if nthreads > 0 { benchmark(r, &buf, nthreads, reps); } else { benchmark(r, &buf, 1, reps); benchmark(r, &buf, all, reps); }
        return;
    }
    if nthreads < 1 { nthreads = all; }
    let t_render = run(r, &buf, nthreads);
    let mut rgb = vec![0u8; w * h * 3];
    let (chk, mean, sum) = check(&buf.to_vec(), w, h, Some(&mut rgb));
    println!("Threads:       {:9}", nthreads);
    println!("Scene setup:   {:9.2} s", t_setup);
    println!("Rendering:     {:9.2} s   ({:.0} pixels/s)", t_render, (w * h) as f64 / t_render);
    println!("Total:         {:9.2} s\n", t_setup + t_render);
    let (vd, short) = if file.is_some() { ("no reference for this scene", "image not checked") } else { (verdict(chk), short_verdict(chk)) };
    println!("Image check:   {} (mean {:.2} {:.2} {:.2}, checksum {:08x})", vd, mean[0], mean[1], mean[2], sum);
    if save {
        match write_png(&out, &rgb, w, h) {
            Ok(()) => println!("Image saved:   {}", out),
            Err(_) => println!("could not save {}", out),
        }
    }
    println!("\nRESULT | rmbench-rs {} | {} | {} | {} | {} threads | setup {:.2} s | render {:.2} s | total {:.2} s | {}",
             VERSION, sys, cpu, compiler(), nthreads, t_setup, t_render, t_setup + t_render, short);
}
