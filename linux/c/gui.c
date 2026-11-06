/* RayMarch Studio Benchmark in C con la finestra (SDL3), come gui.c di Windows:
 * il rendering 800 x 600 si vede formarsi mentre i thread lavorano; alla fine
 * i tempi, il controllo dell'immagine e i bottoni Run again (tutti i thread),
 * 1 thread, Runs - / + (quanti render di fila: poi il migliore e la mediana),
 * Copy result (la riga RESULT negli appunti) e Quit. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <SDL3/SDL.h>
#include <ft2build.h>
#include FT_FREETYPE_H
#include "bench.h"

#define W 800
#define H 600
#define PANEL 262
#define MAX_RUNS 99

typedef struct { float x, y, w, h; const char *label; } Button;
enum { B_AGAIN, B_ONE, B_MINUS, B_PLUS, B_COPY, B_QUIT, NB };
static Button bt[NB] = {
    { 14, 0, 150, 40, "Run again" },    { 174, 0, 140, 40, "1 thread" },
    { 400, 0, 40, 40, "-" },            { 490, 0, 40, 40, "+" },
    { 556, 0, 140, 40, "Copy result" }, { 706, 0, 80, 40, "Quit" },
};

/* i caratteri: FreeType, un atlante di texture per ogni corpo (DejaVu Sans) */
typedef struct { SDL_Texture *t; int w, h, left, top, adv; } Glyph;
typedef struct { int asc, lh; Glyph g[95]; } Font;
enum { F_SMALL, F_MID, F_BIG, F_BTN, NF };
static Font fnt[NF];
static const int fsize[NF] = { 19, 24, 34, 22 };
static SDL_Renderer *rr;

static int load_fonts(void)
{
    static const char *paths[] = { "/usr/share/fonts/TTF/DejaVuSans.ttf", "/usr/share/fonts/noto/NotoSans-Regular.ttf",
                                   "/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" };
    FT_Library lib;
    FT_Face face = NULL;
    int i, c, k;
    if (FT_Init_FreeType(&lib)) return 0;
    for (i = 0; i < 4 && !face; i++) if (FT_New_Face(lib, paths[i], 0, &face)) face = NULL;
    if (!face) return 0;
    for (k = 0; k < NF; k++) {
        Font *f = &fnt[k];
        FT_Set_Pixel_Sizes(face, 0, fsize[k]);
        f->asc = (int)(face->size->metrics.ascender >> 6);
        f->lh = (int)(face->size->metrics.height >> 6);
        for (c = 32; c < 127; c++) {
            Glyph *g = &f->g[c - 32];
            FT_Bitmap *bm;
            if (FT_Load_Char(face, (FT_ULong)c, FT_LOAD_RENDER)) continue;
            bm = &face->glyph->bitmap;
            g->w = (int)bm->width; g->h = (int)bm->rows;
            g->left = face->glyph->bitmap_left; g->top = face->glyph->bitmap_top;
            g->adv = (int)(face->glyph->advance.x >> 6);
            if (g->w && g->h) {
                unsigned char *px = (unsigned char *)malloc((size_t)g->w * g->h * 4);
                int x, y;
                for (y = 0; y < g->h; y++)
                    for (x = 0; x < g->w; x++) {
                        unsigned char *d = px + ((size_t)y * g->w + x) * 4;
                        d[0] = d[1] = d[2] = 255;
                        d[3] = bm->buffer[(size_t)y * bm->pitch + x];
                    }
                g->t = SDL_CreateTexture(rr, SDL_PIXELFORMAT_RGBA32, SDL_TEXTUREACCESS_STATIC, g->w, g->h);
                SDL_UpdateTexture(g->t, NULL, px, g->w * 4);
                SDL_SetTextureBlendMode(g->t, SDL_BLENDMODE_BLEND);
                free(px);
            }
        }
    }
    return 1;
}

static int text_w(int k, const char *s)
{
    int w = 0;
    for (; *s; s++) if (*s >= 32 && *s < 127) w += fnt[k].g[*s - 32].adv;
    return w;
}

static void text(int k, float x, float y, int R, int G, int B, const char *s)
{
    for (; *s; s++) {
        Glyph *g;
        if (*s < 32 || *s > 126) continue;
        g = &fnt[k].g[*s - 32];
        if (g->t) {
            SDL_FRect d = { x + g->left, y + fnt[k].asc - g->top, (float)g->w, (float)g->h };
            SDL_SetTextureColorMod(g->t, (Uint8)R, (Uint8)G, (Uint8)B);
            SDL_RenderTexture(rr, g->t, NULL, &d);
        }
        x += g->adv;
    }
}

static const Render *rnd;
static Job job;
static unsigned *buf;
static char sys[128], cpu[256], result[700], status[200] = "Starting...";
static double times[MAX_RUNS], t_setup, t_start, t_median, t_best;
static int runs = 1, runs_done, running, series_threads, all_threads, check = -1;
static unsigned sum;
static double mean[3];

static int cmpd(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : x > y;
}

static void start_run(void)
{
    job_start(&job, rnd, buf, series_threads);
    t_start = bench_now();
    running = 1;
}

static void start_series(int threads)
{
    if (running || !rnd) return;
    series_threads = threads;
    runs_done = 0;
    check = -1;
    memset(buf, 0, (size_t)W * H * 4);
    snprintf(status, sizeof status, "Rendering on %d thread%s...", threads, threads > 1 ? "s" : "");
    start_run();
}

static void finish_series(void)
{
    double s[MAX_RUNS];
    memcpy(s, times, sizeof(double) * runs);
    qsort(s, runs, sizeof(double), cmpd);
    t_best = s[0];
    t_median = runs % 2 ? s[runs / 2] : (s[runs / 2 - 1] + s[runs / 2]) / 2;
    sum = bench_checksum(buf, W, H, NULL, mean);
    check = sum == REF_SUM;
    snprintf(status, sizeof status, "Done.");
    snprintf(result, sizeof result,
             "RESULT | rmbench-c-gui %s | %s | %s | %s | %d threads | setup %.2f s | render %.4f s (best of %d, median %.4f s) | %s",
             VERSION, sys, cpu, bench_compiler(), series_threads, t_setup, t_best, runs, t_median,
             check ? "image identical" : "image DIFFERENT");
}

static void poll_job(void)
{
    if (!running || job_busy(&job, NULL)) return;
    times[runs_done] = bench_now() - t_start;
    job_wait(&job);
    running = 0;
    if (++runs_done < runs) {
        snprintf(status, sizeof status, "Run %d of %d done (%.4f s)...", runs_done, runs, times[runs_done - 1]);
        start_run();
    } else finish_series();
}

static void draw_button(SDL_Renderer *r, const Button *b, int enabled)
{
    SDL_FRect f = { b->x, b->y, b->w, b->h };
    int c = enabled ? 255 : 130;
    SDL_SetRenderDrawColor(r, enabled ? 60 : 40, enabled ? 70 : 44, enabled ? 90 : 50, 255);
    SDL_RenderFillRect(r, &f);
    SDL_SetRenderDrawColor(r, 120, 130, 150, 255);
    SDL_RenderRect(r, &f);
    text(F_BTN, b->x + (b->w - text_w(F_BTN, b->label)) / 2, b->y + (b->h - fnt[F_BTN].lh) / 2, c, c, c, b->label);
}

static int hit(const Button *b, float x, float y)
{
    return x >= b->x && x < b->x + b->w && y >= b->y && y < b->y + b->h;
}

int main(void)
{
    char err[256], line[300];
    SDL_Window *win;
    SDL_Renderer *r;
    SDL_Texture *tex;
    SDL_Event e;
    int quit = 0, i;
    double t0;
    if (!SDL_Init(SDL_INIT_VIDEO)) { fprintf(stderr, "SDL: %s\n", SDL_GetError()); return 1; }
    win = SDL_CreateWindow("RayMarch Studio Benchmark (C, Linux)", W, H + PANEL, 0);
    r = win ? SDL_CreateRenderer(win, NULL) : NULL;
    if (!r) { fprintf(stderr, "SDL: %s\n", SDL_GetError()); return 1; }
    tex = SDL_CreateTexture(r, SDL_PIXELFORMAT_XRGB8888, SDL_TEXTUREACCESS_STREAMING, W, H);
    rr = r;
    if (!load_fonts()) { fprintf(stderr, "no font found (DejaVu Sans or Noto Sans)\n"); return 1; }
    for (i = 0; i < NB; i++) bt[i].y = H + PANEL - 54;
    bench_machine(sys, sizeof sys, cpu, sizeof cpu);
    all_threads = bench_cpus();
    buf = bench_pixels(W * H);
    t0 = bench_now();
    rnd = bench_setup(W, H, err, sizeof err);
    if (!rnd) { fprintf(stderr, "scene: %s\n", err); return 1; }
    t_setup = bench_now() - t0;
    start_series(all_threads);
    while (!quit) {
        while (SDL_PollEvent(&e)) {
            if (e.type == SDL_EVENT_QUIT) quit = 1;
            else if (e.type == SDL_EVENT_KEY_DOWN && e.key.key == SDLK_ESCAPE) quit = 1;
            else if (e.type == SDL_EVENT_MOUSE_BUTTON_DOWN && e.button.button == SDL_BUTTON_LEFT) {
                float x = e.button.x, y = e.button.y;
                if (hit(&bt[B_QUIT], x, y)) quit = 1;
                else if (hit(&bt[B_AGAIN], x, y)) start_series(all_threads);
                else if (hit(&bt[B_ONE], x, y)) start_series(1);
                else if (hit(&bt[B_MINUS], x, y) && !running && runs > 1) runs--;
                else if (hit(&bt[B_PLUS], x, y) && !running && runs < MAX_RUNS) runs++;
                else if (hit(&bt[B_COPY], x, y) && result[0]) SDL_SetClipboardText(result);
            }
        }
        poll_job();
        SDL_UpdateTexture(tex, NULL, buf, W * 4);
        SDL_SetRenderDrawColor(r, 24, 26, 32, 255);
        SDL_RenderClear(r);
        SDL_FRect img = { 0, 0, W, H };
        SDL_RenderTexture(r, tex, NULL, &img);
        text(F_BIG, 14, H + 8, 255, 255, 255, status);
        if (!running && check >= 0) {
            snprintf(line, sizeof line, "Best %.4f s   median %.4f s   (%d run%s, %d thread%s)", t_best, t_median, runs,
                     runs > 1 ? "s" : "", series_threads, series_threads > 1 ? "s" : "");
            text(F_MID, 14, H + 52, 255, 255, 255, line);
            snprintf(line, sizeof line, "Image check: %s (checksum %08x)", check ? "OK, identical to the reference" : "DIFFERENT FROM THE REFERENCE", sum);
            if (check) text(F_MID, 14, H + 82, 120, 230, 120, line); else text(F_MID, 14, H + 82, 255, 200, 60, line);
        }
        text(F_SMALL, 14, H + 118, 200, 200, 200, cpu);
        text(F_SMALL, 14, H + 141, 200, 200, 200, sys);
        snprintf(line, sizeof line, "%s   -   setup %.2f s", bench_compiler(), t_setup);
        text(F_SMALL, 14, H + 164, 200, 200, 200, line);
        snprintf(line, sizeof line, "%d", runs);
        text(F_BTN, 330, bt[0].y + 8, 255, 255, 255, "Runs");
        text(F_BTN, 465 - text_w(F_BTN, line) / 2, bt[0].y + 8, 255, 255, 255, line);
        for (i = 0; i < NB; i++)
            draw_button(r, &bt[i], !(running && i != B_QUIT) && !(i == B_COPY && !result[0]));
        SDL_RenderPresent(r);
        SDL_Delay(running ? 16 : 30);
    }
    if (running) job_stop(&job);
    SDL_Quit();
    return 0;
}
