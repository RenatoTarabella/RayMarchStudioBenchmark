/* Il benchmark C allineato: le parti comuni alla riga di comando e alla finestra */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include "bench.h"

#ifndef BUILD_VARIANT
#define BUILD_VARIANT "clang"
#endif

double bench_now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + (double)t.tv_nsec * 1e-9;
}

/* ---- il lavoro: tile 16 x 16 presi da un contatore atomico */

static void *worker(void *p)
{
    Job *j = (Job *)p;
    Ctx c;
    c.g = render_scene(j->r);
    c.rng = 1;
    while (!atomic_load(&j->abort)) {
        int t = atomic_fetch_add(&j->next, 1), x0, y0, x1, y1, x, y;
        if (t >= j->ntiles) break;
        x0 = (t % j->tx) * TILE; y0 = (t / j->tx) * TILE;
        x1 = x0 + TILE < j->w ? x0 + TILE : j->w;
        y1 = y0 + TILE < j->h ? y0 + TILE : j->h;
        for (y = y0; y < y1; y++)
            for (x = x0; x < x1; x++) j->buf[(size_t)y * j->w + x] = render_pixel(j->r, &c, x, y);
        atomic_fetch_add(&j->done, 1);
    }
    atomic_fetch_add(&j->finished, 1);
    return NULL;
}

void job_start(Job *j, const Render *r, unsigned *buf, int n)
{
    int k;
    pthread_attr_t at;
    memset(j, 0, sizeof *j);
    j->r = r; j->buf = buf; j->w = render_w(r); j->h = render_h(r);
    j->tx = (j->w + TILE - 1) / TILE;
    j->ntiles = j->tx * ((j->h + TILE - 1) / TILE);
    j->n = n < 1 ? 1 : n > MAX_THREADS ? MAX_THREADS : n;
    pthread_attr_init(&at);
    pthread_attr_setstacksize(&at, 4 << 20);
    for (k = 0; k < j->n; k++) pthread_create(&j->th[k], &at, worker, j);
    pthread_attr_destroy(&at);
}

int job_busy(Job *j, int *percent)
{
    if (percent) *percent = (int)(atomic_load(&j->done) * 100LL / j->ntiles);
    return atomic_load(&j->finished) < j->n;
}

void job_wait(Job *j)
{
    int k;
    if (!j->n) return;
    for (k = 0; k < j->n; k++) pthread_join(j->th[k], NULL);
    j->n = 0;
}

void job_stop(Job *j)
{
    atomic_store(&j->abort, 1);
    job_wait(j);
}

double job_run(const Render *r, unsigned *buf, int n, int progress)
{
    static Job j;
    double t0 = bench_now();
    int last = -1;
    job_start(&j, r, buf, n);
    if (progress)
        while (job_busy(&j, NULL)) {
            int pc = (int)(atomic_load(&j.done) * 100LL / j.ntiles);
            if (pc != last) { fprintf(stderr, "\r  %3d%%", pc); last = pc; }
            usleep(20000);
        }
    job_wait(&j);
    if (progress) fprintf(stderr, "\r      \r");
    return bench_now() - t0;
}

unsigned *bench_pixels(int n)
{
    unsigned *p = (unsigned *)aligned_alloc(64, ((size_t)n * sizeof(unsigned) + 63) & ~(size_t)63);
    if (!p) { fprintf(stderr, "out of memory\n"); exit(2); }
    memset(p, 0, (size_t)n * sizeof(unsigned));
    return p;
}

/* ---- il controllo: il checksum FNV-1a dei byte RGB e la media dei colori */

unsigned bench_checksum(const unsigned *buf, int w, int h, unsigned char *rgb, double mean[3])
{
    unsigned s = 2166136261u;
    int i, k;
    mean[0] = mean[1] = mean[2] = 0;
    for (i = 0; i < w * h; i++) {
        unsigned char c[3];
        c[0] = (unsigned char)(buf[i] >> 16); c[1] = (unsigned char)(buf[i] >> 8); c[2] = (unsigned char)buf[i];
        for (k = 0; k < 3; k++) {
            if (rgb) rgb[(size_t)i * 3 + k] = c[k];
            mean[k] += c[k];
            s = (s ^ c[k]) * 16777619u;
        }
    }
    for (k = 0; k < 3; k++) mean[k] /= (double)w * h;
    return s;
}

const char *bench_verdict(int w, int h, unsigned sum)
{
    if (w != 800 || h != 600) return "no reference at this size: compare with the other versions";
    return sum == REF_SUM ? "identical to the reference" : "DIFFERENT FROM THE REFERENCE";
}

/* ---- il PNG: righe con filtro 0, zlib con blocchi non compressi */

static unsigned crc32b(unsigned c, const unsigned char *d, size_t n)
{
    size_t i;
    int k;
    for (i = 0; i < n; i++) {
        c ^= d[i];
        for (k = 0; k < 8; k++) c = c & 1 ? 0xedb88320u ^ (c >> 1) : c >> 1;
    }
    return c;
}

static void put32(FILE *f, unsigned v) { fputc(v >> 24, f); fputc(v >> 16 & 255, f); fputc(v >> 8 & 255, f); fputc(v & 255, f); }

static void chunk(FILE *f, const char *type, const unsigned char *d, size_t n)
{
    put32(f, (unsigned)n);
    fwrite(type, 1, 4, f);
    if (n) fwrite(d, 1, n, f);
    put32(f, crc32b(crc32b(0xffffffffu, (const unsigned char *)type, 4), d, n) ^ 0xffffffffu);
}

int write_png(const char *path, const unsigned char *rgb, int w, int h)
{
    size_t rl = (size_t)w * 3 + 1, n = rl * h, zn = n + 6 + (n / 65535 + 1) * 5, i, o = 0;
    unsigned char *raw = (unsigned char *)malloc(n), *z = (unsigned char *)malloc(zn), ih[13];
    unsigned a = 1, b = 0;
    FILE *f = fopen(path, "wb");
    int y;
    if (!f || !raw || !z) { if (f) fclose(f); free(raw); free(z); return 1; }
    for (y = 0; y < h; y++) { raw[y * rl] = 0; memcpy(raw + y * rl + 1, rgb + (size_t)y * w * 3, (size_t)w * 3); }
    z[o++] = 0x78; z[o++] = 0x01;
    for (i = 0; i < n; i += 65535) {
        size_t len = n - i < 65535 ? n - i : 65535;
        z[o++] = i + len >= n;
        z[o++] = len & 255; z[o++] = len >> 8; z[o++] = ~len & 255; z[o++] = (~len >> 8) & 255;
        memcpy(z + o, raw + i, len);
        o += len;
    }
    for (i = 0; i < n; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
    z[o++] = b >> 8; z[o++] = b & 255; z[o++] = a >> 8; z[o++] = a & 255;
    fwrite("\x89PNG\r\n\x1a\n", 1, 8, f);
    ih[0] = w >> 24; ih[1] = w >> 16; ih[2] = w >> 8; ih[3] = w; ih[4] = h >> 24; ih[5] = h >> 16; ih[6] = h >> 8; ih[7] = h;
    ih[8] = 8; ih[9] = 2; ih[10] = ih[11] = ih[12] = 0;
    chunk(f, "IHDR", ih, 13);
    chunk(f, "IDAT", z, o);
    chunk(f, "IEND", NULL, 0);
    fclose(f);
    free(raw);
    free(z);
    return 0;
}

/* ---- la macchina */

/* il valore dopo i due punti (o l'uguale) della riga che comincia con key */
static int file_field(const char *path, const char *key, char *out, int n)
{
    char line[512];
    FILE *f = fopen(path, "r");
    int found = 0;
    if (!f) return 0;
    while (!found && fgets(line, sizeof line, f))
        if (!strncmp(line, key, strlen(key))) {
            char *v = strchr(line, ':'), *e;
            if (!v) v = strchr(line, '=');
            if (!v) continue;
            for (v++; *v == ' ' || *v == '\t' || *v == '"'; v++) ;
            for (e = v + strlen(v); e > v && (e[-1] == '\n' || e[-1] == ' ' || e[-1] == '"'); ) *--e = 0;
            snprintf(out, n, "%s", v);
            found = 1;
        }
    fclose(f);
    return found;
}

void bench_machine(char *sys, int ns, char *cpu, int nc)
{
    char name[128] = "", kernel[64] = "";
    snprintf(sys, ns, "Linux");
    snprintf(cpu, nc, "unknown CPU");
    if (file_field("/etc/os-release", "PRETTY_NAME", name, sizeof name)) {
        snprintf(sys, ns, "%s", name);
    }
    {
        FILE *f = fopen("/proc/sys/kernel/osrelease", "r");
        if (f) {
            if (fgets(kernel, sizeof kernel, f)) kernel[strcspn(kernel, "\n")] = 0;
            fclose(f);
            if (kernel[0]) snprintf(sys + strlen(sys), ns - (int)strlen(sys), " (kernel %s)", kernel);
        }
    }
    file_field("/proc/cpuinfo", "model name", cpu, nc);
}

const char *bench_compiler(void)
{
    static char s[96];
#ifdef __clang__
    snprintf(s, sizeof s, "C, clang %d.%d, %s", __clang_major__, __clang_minor__, BUILD_VARIANT);
#else
    snprintf(s, sizeof s, "C, %s", BUILD_VARIANT);
#endif
    return s;
}

int bench_cpus(void)
{
    long n = sysconf(_SC_NPROCESSORS_ONLN);
    return n < 1 ? 1 : n > MAX_THREADS ? MAX_THREADS : (int)n;
}

static int cmpd(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : x > y;
}

void bench_repeat(const Render *r, unsigned *buf, int n, int reps)
{
    double *t = (double *)malloc(reps * sizeof(double)), mean[3], med;
    unsigned sum;
    int i, w = render_w(r), h = render_h(r);
    job_run(r, buf, n, 0);
    for (i = 0; i < reps; i++) t[i] = job_run(r, buf, n, 0);
    qsort(t, reps, sizeof(double), cmpd);
    med = reps % 2 ? t[reps / 2] : (t[reps / 2 - 1] + t[reps / 2]) / 2;
    sum = bench_checksum(buf, w, h, NULL, mean);
    printf("BENCH | %s | %2d thread%s | %dx%d | median %8.4f s | min %8.4f s | checksum %08x (%s)\n", bench_compiler(), n,
           n > 1 ? "s" : " ", w, h, med, t[0], sum, bench_verdict(w, h, sum));
    fflush(stdout);
    free(t);
}
