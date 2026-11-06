/* RayMarch Studio Benchmark in C, versione allineata ai port .NET (net/) e
 * Rust (rust/): la stessa logica, gli stessi dati compatti e lo stesso
 * ordine dei calcoli, per confrontare i tre linguaggi alla pari. Solo la
 * scena del benchmark (sfere, box, cilindri, gruppi, ambiente studio); il
 * motore completo dell'editor resta in src/. C99, per clang su Windows. */
#ifndef ENGINE_H
#define ENGINE_H

#include <math.h>
#include <stddef.h>

#define RM_PI 3.14159265358979323846
#define RM_TP 6.28318530717958647692
#define RM_INLINE static inline __attribute__((always_inline))

typedef struct { double x, y, z; } V3;

RM_INLINE V3 v3(double x, double y, double z) { V3 r = { x, y, z }; return r; }
RM_INLINE V3 v3add(V3 a, V3 b) { return v3(a.x + b.x, a.y + b.y, a.z + b.z); }
RM_INLINE V3 v3sub(V3 a, V3 b) { return v3(a.x - b.x, a.y - b.y, a.z - b.z); }
RM_INLINE V3 v3neg(V3 a) { return v3(-a.x, -a.y, -a.z); }
RM_INLINE V3 v3mul(V3 a, double s) { return v3(a.x * s, a.y * s, a.z * s); }
RM_INLINE V3 v3mulv(V3 a, V3 b) { return v3(a.x * b.x, a.y * b.y, a.z * b.z); }
RM_INLINE double v3dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
RM_INLINE V3 v3cross(V3 a, V3 b) { return v3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
RM_INLINE double v3len(V3 a) { return sqrt(v3dot(a, a)); }
RM_INLINE V3 v3lerp(V3 a, V3 b, double t) { return v3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
RM_INLINE V3 v3abs(V3 a) { return v3(fabs(a.x), fabs(a.y), fabs(a.z)); }
RM_INLINE double v3maxc(V3 a) { return a.x > a.y ? (a.x > a.z ? a.x : a.z) : (a.y > a.z ? a.y : a.z); }
RM_INLINE double v3get(V3 a, int i) { return i == 0 ? a.x : i == 1 ? a.y : a.z; }
RM_INLINE V3 v3norm(V3 a) { double l = v3len(a); return l > 0 ? v3mul(a, 1 / l) : a; }
RM_INLINE double rmin(double a, double b) { return a < b ? a : b; }
RM_INLINE double rmax(double a, double b) { return a > b ? a : b; }
RM_INLINE double rclamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
RM_INLINE double rmix(double a, double b, double t) { return a + (b - a) * t; }
RM_INLINE double rlen2(double x, double y) { return sqrt(x * x + y * y); }
RM_INLINE double rfract(double x) { return x - floor(x); }

/* Trasformazione affine: righe di [3x3 | traslazione] */
typedef struct { double m[12]; } M34;

RM_INLINE V3 m34_point(const M34 *a, V3 p)
{
    const double *m = a->m;
    return v3(m[0] * p.x + m[1] * p.y + m[2] * p.z + m[3],
              m[4] * p.x + m[5] * p.y + m[6] * p.z + m[7],
              m[8] * p.x + m[9] * p.y + m[10] * p.z + m[11]);
}

/* il materiale come lo usa il renderer, mescolato nei raccordi */
typedef struct {
    V3 col;
    double spec, shin, dif, sel;
    double opt[4];      /* riflesso, ruvidita', trasparenza, ior */
    double metal;
    double sheen[4];
} GMat;

/* un oggetto pronto per il calcolo: solo quello che serve al caso caldo */
typedef struct {
    int type, op;
    M34 inv;
    double p0;          /* il raggio della sfera */
    double c[4];        /* i valori derivati (prim_prepare) */
    double dscale, k, round, shell;
    GMat mat;
    V3 bc;
    double br;
} GObj;

typedef struct { int code, obj, cull, n; } Ins;

typedef struct {
    GObj *obj;
    int nobj;
    Ins *prog;
    int nprog, max_stack;
    double *bound;      /* le sfere degli I_SKIP, 4 l'una */
    V3 bc;
    double br;
    int inexact, cavity, has_glass;
} GScene;

/* lo stato di un calcolo (uno per thread) */
typedef struct {
    const GScene *g;
    unsigned rng;
} Ctx;

typedef struct Render Render;

/* la scena del benchmark pronta da rendere, w x h; NULL e il messaggio in err */
Render *bench_setup(int w, int h, char *err, int errlen);
/* la stessa cosa con il testo di un'altra scena (lo stesso formato di bench/benchmark.txt) */
Render *bench_setup_text(const char *text, int w, int h, char *err, int errlen);
int render_w(const Render *r);
int render_h(const Render *r);
const GScene *render_scene(const Render *r);
/* il colore del pixel (x, y), y dall'alto: 0x00RRGGBB */
unsigned render_pixel(const Render *r, Ctx *c, int x, int y);

#endif
