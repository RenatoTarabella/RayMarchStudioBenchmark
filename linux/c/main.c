/* RayMarch Studio Benchmark in C, a riga di comando, allineato ai port .NET
 * e Rust: la stessa scena, gli stessi tile 16 x 16 distribuiti da un
 * contatore atomico, lo stesso controllo dell'immagine e la stessa modalita'
 * benchmark.
 *   rmbench-c [-t thread] [-s scala] [-b [ripetizioni]] [-o immagine.png] [-n] [-f scena.txt]
 * -t: quanti thread (tutte le CPU); -s: risoluzione 800s x 600s; -b: un
 * render di riscaldamento, poi N render (10) con mediana e minimo, a 1
 * thread e a tutti i thread (o solo a -t); -o: dove salvare l'immagine;
 * -f: un'altra scena da un file al posto di quella del benchmark. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "bench.h"

/* tutto il file in memoria, con lo zero in fondo; NULL se non si legge */
static char *read_file(const char *path)
{
    FILE *f = fopen(path, "rb");
    long n;
    char *t;
    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) || (n = ftell(f)) < 0 || fseek(f, 0, SEEK_SET)) { fclose(f); return NULL; }
    t = (char *)malloc((size_t)n + 1);
    if (t && fread(t, 1, (size_t)n, f) != (size_t)n) { free(t); t = NULL; }
    if (t) t[n] = 0;
    fclose(f);
    return t;
}

int main(int argc, char **argv)
{
    char err[256], sys[128], cpu[256];
    const char *out = "rmbench-c.png", *file = NULL;
    char *text = NULL;
    int i, n = 0, save = 1, reps = 0, w, h, all;
    double scale = 1, t0, t_setup, t_render, mean[3];
    unsigned *buf, sum;
    unsigned char *rgb;
    const Render *r;
    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "-t") && i + 1 < argc) n = atoi(argv[++i]);
        else if (!strcmp(argv[i], "-s") && i + 1 < argc) scale = atof(argv[++i]);
        else if (!strcmp(argv[i], "-o") && i + 1 < argc) out = argv[++i];
        else if (!strcmp(argv[i], "-n")) save = 0;
        else if (!strcmp(argv[i], "-f") && i + 1 < argc) file = argv[++i];
        else if (!strcmp(argv[i], "-b")) reps = i + 1 < argc && atoi(argv[i + 1]) > 0 ? atoi(argv[++i]) : 10;
        else {
            fprintf(stderr, "usage: rmbench-c [-t threads] [-s scale] [-b [repeats]] [-o image.png] [-n] [-f scene.txt]\n"
                            "  -t  how many threads (default: all CPUs)\n"
                            "  -s  resolution 800*scale x 600*scale (default 1)\n"
                            "  -b  benchmark: a warm-up render, then N renders (default 10), median and minimum,\n"
                            "      at 1 thread and at all threads (or only at -t)\n"
                            "  -o  where to save the image (default rmbench-c.png); -n  do not save it\n"
                            "  -f  render the scene in this file instead of the benchmark scene\n");
            return 1;
        }
    }
    all = bench_cpus();
    if (n > MAX_THREADS) n = MAX_THREADS;
    w = (int)(800 * scale + 0.5);
    h = (int)(600 * scale + 0.5);
    bench_machine(sys, sizeof sys, cpu, sizeof cpu);
    printf("RayMarch Studio Benchmark %s (C)\nSystem:   %s\nCPU:      %s\nProgram:  %s\n", VERSION, sys, cpu, bench_compiler());
    if (file) printf("Scene:    %s\n", file);
    printf("Image:    %d x %d = %d pixels, 1 sample per pixel, up to 6 bounces\n\n", w, h, w * h);
    fflush(stdout);
    if (file && !(text = read_file(file))) { fprintf(stderr, "could not read %s\n", file); return 1; }
    t0 = bench_now();
    r = text ? bench_setup_text(text, w, h, err, sizeof err) : bench_setup(w, h, err, sizeof err);
    if (!r) { fprintf(stderr, "scene: %s\n", err); return 1; }
    t_setup = bench_now() - t0;
    buf = bench_pixels(w * h);
    if (reps > 0) {
        if (n > 0) bench_repeat(r, buf, n, reps);
        else { bench_repeat(r, buf, 1, reps); bench_repeat(r, buf, all, reps); }
        return 0;
    }
    if (n < 1) n = all;
    t_render = job_run(r, buf, n, 1);
    rgb = (unsigned char *)malloc((size_t)w * h * 3);
    sum = bench_checksum(buf, w, h, rgb, mean);
    printf("Threads:       %9d\nScene setup:   %9.2f s\nRendering:     %9.2f s   (%.0f pixels/s)\nTotal:         %9.2f s\n\n",
           n, t_setup, t_render, w * h / t_render, t_setup + t_render);
    printf("Image check:   %s (mean %.2f %.2f %.2f, checksum %08x)\n", file ? "no reference for this scene" : bench_verdict(w, h, sum), mean[0], mean[1], mean[2], sum);
    if (save) printf(write_png(out, rgb, w, h) ? "could not save %s\n" : "Image saved:   %s\n", out);
    printf("\nRESULT | rmbench-c %s | %s | %s | %s | %d threads | setup %.2f s | render %.2f s | total %.2f s | %s\n", VERSION,
           sys, cpu, bench_compiler(), n, t_setup, t_render, t_setup + t_render,
           !file && w == 800 && h == 600 && sum == REF_SUM ? "image identical" : "image not checked");
    return 0;
}
