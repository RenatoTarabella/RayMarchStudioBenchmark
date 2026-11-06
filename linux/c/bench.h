/* Il benchmark C allineato: le parti comuni alla riga di comando (main.c)
 * e alla finestra (gui.c), come benchlib.c e job.c del motore completo:
 * l'ora, il lavoro a tile, il controllo dell'immagine, il PNG, la macchina */
#ifndef BENCH_H
#define BENCH_H

#define _GNU_SOURCE
#include <pthread.h>
#include <stdatomic.h>
#include "engine.h"

#define VERSION "1.0.2"
#define TILE 16
#define REF_SUM 0x977e953fu
#define MAX_THREADS 256

double bench_now(void);

/* Un rendering in corso: n thread prendono tile di 16 x 16 pixel da un
 * contatore atomico (il carico si bilancia da solo); ognuno col suo Ctx */
typedef struct {
    const Render *r;
    unsigned *buf;
    int w, h, tx, ntiles, n;
    pthread_t th[MAX_THREADS];
    /* i contatori, scritti da tutti i thread, ognuno nella sua riga di cache */
    _Alignas(64) atomic_int next;         /* il prossimo tile */
    _Alignas(64) atomic_int done;         /* i tile finiti (per la percentuale) */
    _Alignas(64) atomic_int abort;
    _Alignas(64) atomic_int finished;   /* i thread usciti dal lavoro */
    char pad[60];
} Job;

void job_start(Job *j, const Render *r, unsigned *buf, int n);
/* 1 se e' in corso; la percentuale */
int  job_busy(Job *j, int *percent);
void job_wait(Job *j);
/* ferma i thread e aspetta che escano */
void job_stop(Job *j);
/* il rendering su n thread, il tempo in secondi; progress: la percentuale ogni 20 ms sullo stderr */
double job_run(const Render *r, unsigned *buf, int n, int progress);

/* il buffer dei pixel allineato a 64 byte (una riga di tile = una riga di cache) */
unsigned *bench_pixels(int n);

/* il checksum FNV-1a dei byte RGB e la media dei colori; rgb (se non NULL) riceve i byte */
unsigned bench_checksum(const unsigned *buf, int w, int h, unsigned char *rgb, double mean[3]);
const char *bench_verdict(int w, int h, unsigned sum);
int  write_png(const char *path, const unsigned char *rgb, int w, int h);
void bench_machine(char *sys, int ns, char *cpu, int nc);
const char *bench_compiler(void);
int  bench_cpus(void);
/* la modalita' benchmark: un riscaldamento, poi reps render; mediana e minimo */
void bench_repeat(const Render *r, unsigned *buf, int n, int reps);

#endif
