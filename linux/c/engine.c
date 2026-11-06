/* Il motore del benchmark in C, allineato ai port .NET e Rust: la lettura
 * della scena (sceneio.c), la preparazione (gscene.c), l'interprete a pila
 * (eval.c, sdf.c), l'ambiente studio (envmap.c) e il rendering (render.c).
 * Le stesse formule del motore completo, nello stesso ordine: l'immagine e'
 * identica bit per bit (checksum 977e953f a 800 x 600). */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "engine.h"

static void *xalloc(size_t n)
{
    void *p = calloc(1, n ? n : 1);
    if (!p) { fprintf(stderr, "out of memory\n"); exit(2); }
    return p;
}

static void *xrealloc(void *p, size_t n)
{
    void *q = realloc(p, n ? n : 1);
    if (!q) { fprintf(stderr, "out of memory\n"); exit(2); }
    return q;
}

/* ---- le matrici (vec.c) */

static const M34 identity = { { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 } };

static M34 m34_compose(V3 pos, V3 rot, V3 scl)
{
    double ax = rot.x * RM_PI / 180, ay = rot.y * RM_PI / 180, az = rot.z * RM_PI / 180;
    double sx = sin(ax), cx = cos(ax), sy = sin(ay), cy = cos(ay), sz = sin(az), cz = cos(az);
    double rx[3][3] = { { 1, 0, 0 }, { 0, cx, -sx }, { 0, sx, cx } };
    double ry[3][3] = { { cy, 0, sy }, { 0, 1, 0 }, { -sy, 0, cy } };
    double rz[3][3] = { { cz, -sz, 0 }, { sz, cz, 0 }, { 0, 0, 1 } };
    double t[3][3], r[3][3], s[3] = { scl.x, scl.y, scl.z };
    M34 o;
    int i, j;
    for (i = 0; i < 3; i++)
        for (j = 0; j < 3; j++) t[i][j] = ry[i][0] * rx[0][j] + ry[i][1] * rx[1][j] + ry[i][2] * rx[2][j];
    for (i = 0; i < 3; i++)
        for (j = 0; j < 3; j++) r[i][j] = rz[i][0] * t[0][j] + rz[i][1] * t[1][j] + rz[i][2] * t[2][j];
    for (i = 0; i < 3; i++)
        for (j = 0; j < 3; j++) o.m[i * 4 + j] = r[i][j] * s[j];
    o.m[3] = pos.x; o.m[7] = pos.y; o.m[11] = pos.z;
    return o;
}

static double m34_collen(const M34 *a, int j)
{
    const double *m = a->m;
    return sqrt(m[j] * m[j] + m[4 + j] * m[4 + j] + m[8 + j] * m[8 + j]);
}

static double m34_maxscale(const M34 *a) { return rmax(m34_collen(a, 0), rmax(m34_collen(a, 1), m34_collen(a, 2))); }

static M34 m34_mul(const M34 *a, const M34 *b)
{
    M34 t;
    int i, j;
    const double *x = a->m, *y = b->m;
    for (i = 0; i < 3; i++) {
        for (j = 0; j < 3; j++) t.m[i * 4 + j] = x[i * 4] * y[j] + x[i * 4 + 1] * y[4 + j] + x[i * 4 + 2] * y[8 + j];
        t.m[i * 4 + 3] = x[i * 4] * y[3] + x[i * 4 + 1] * y[7] + x[i * 4 + 2] * y[11] + x[i * 4 + 3];
    }
    return t;
}

static int m34_inverse(const M34 *s, M34 *inv)
{
    const double *m = s->m;
    double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6];
    double g = m[8], h = m[9], k = m[10];
    double A = e * k - f * h, B = f * g - d * k, C = d * h - e * g;
    double det = a * A + b * B + c * C, id;
    double *r = inv->m;
    int i;
    *inv = identity;
    if (det == 0 || det != det) return 0;
    id = 1 / det;
    r[0] = A * id; r[1] = (c * h - b * k) * id; r[2] = (b * f - c * e) * id;
    r[4] = B * id; r[5] = (a * k - c * g) * id; r[6] = (c * d - a * f) * id;
    r[8] = C * id; r[9] = (b * g - a * h) * id; r[10] = (a * e - b * d) * id;
    for (i = 0; i < 3; i++)
        r[i * 4 + 3] = -(r[i * 4] * m[3] + r[i * 4 + 1] * m[7] + r[i * 4 + 2] * m[11]);
    return 1;
}

static V3 m34_trans(const M34 *a) { return v3(a->m[3], a->m[7], a->m[11]); }

static void m34_decompose(const M34 *a, V3 *pos, V3 *rot, V3 *scl)
{
    double s0 = m34_collen(a, 0), s1 = m34_collen(a, 1), s2 = m34_collen(a, 2), d = 180 / RM_PI, ax, ay, az;
    double r00 = a->m[0] / s0, r10 = a->m[4] / s0, r20 = a->m[8] / s0;
    double r11 = a->m[5] / s1, r21 = a->m[9] / s1;
    double r12 = a->m[6] / s2, r22 = a->m[10] / s2;
    ay = asin(rclamp(-r20, -1, 1));
    if (fabs(cos(ay)) > 1e-9) { ax = atan2(r21, r22); az = atan2(r10, r00); }
    else { ax = atan2(-r12, r11); az = 0; }
    *pos = m34_trans(a);
    *rot = v3(ax * d, ay * d, az * d);
    *scl = v3(s0, s1, s2);
}

/* a meta' verso il pari, come rround del C */
static double rround(double x)
{
    double r = floor(x + 0.5);
    if (r - x == 0.5 && fmod(r, 2) != 0) r -= 1;
    return r;
}

/* ---- la scena (scene.c, sceneio.c): oggetti in un vettore, per indice */

enum { T_GROUP = -1, T_SPHERE = 0, T_BOX = 1, T_CYLINDER = 3 };
enum { OP_UNION, OP_SUB, OP_INTER };
enum { I_PRIM, I_OP, I_MOD, I_MIN, I_SKIP = 5, I_LAZY = 6 };
enum { FLOOR_CHECKER, FLOOR_SOLID, FLOOR_GRID };
enum { ENV_SKY, ENV_STUDIO, ENV_HDRI };
#define OF_WIRE 1u
#define OF_RENDER 2u
#define OF_LOCK 4u
#define OF_OFF 8u
#define MAX_LIGHTS 8
#define MAX_OBJECTS 256

typedef struct {
    V3 color;
    double diffuse, specular, shininess, reflect, roughness, transparency, ior;
    int metal;
    double sheen;
    V3 sheen_color;
    double sheen_rough;
} Material;

typedef struct { int *v, n, cap; } IList;

typedef struct {
    int type;
    double par[10];
    V3 pos, rot, scl;
    int op;
    double k, round, shell;
    Material mat;
    int inherit_mat;
    unsigned flags;
    int parent;         /* -1: nessuno */
    IList kids;
    int index;
} Obj;

typedef struct { int dir; V3 pos, color; double intensity; int shadow; double softness; int on; } Light;

typedef struct {
    int floor;
    V3 floor_a, floor_b;
    double floor_scale;
    int floor_type;
    double floor_reflect;
    V3 sky_top, sky_horizon, sky_light, ambient;
    double fog;
    V3 fog_color;
    int ao, env;
    char env_preset[64];
    double env_intensity, env_rotation;
    int env_background;
    V3 bg_color;
    int env_lights, tone_map;
    double exposure;
} World;

typedef struct {
    Obj obj[MAX_OBJECTS];
    int nobj;
    IList roots;
    Light light[MAX_LIGHTS];
    int nlight;
    V3 cam_pos, cam_target;
    double cam_focal;
    World world;
} Scene;

static void il_insert(IList *l, int at, int v)
{
    int i;
    if (l->n >= l->cap) { l->cap = l->cap ? l->cap * 2 : 8; l->v = (int *)xrealloc(l->v, l->cap * sizeof(int)); }
    for (i = l->n; i > at; i--) l->v[i] = l->v[i - 1];
    l->v[at] = v;
    l->n++;
}

static void il_add(IList *l, int v) { il_insert(l, l->n, v); }

static Material material_default(void)
{
    Material m;
    memset(&m, 0, sizeof m);
    m.color = v3(0.75, 0.3, 0.15);
    m.diffuse = 1; m.specular = 1; m.shininess = 24; m.ior = 1.5;
    m.sheen_color = v3(1, 1, 1);
    m.sheen_rough = 0.5;
    return m;
}

/* i parametri di partenza delle forme che il benchmark usa */
static int defaults(int type, double *p)
{
    switch (type) {
    case T_SPHERE: p[0] = 0.5; return 1;
    case T_BOX: p[0] = 1; p[1] = 1; p[2] = 1; p[3] = 0.05; return 4;
    case T_CYLINDER: p[0] = 0.5; p[1] = 1; p[2] = 0.05; return 3;
    }
    return 0;
}

static int scene_add(Scene *s, int type, int parent)
{
    int i = s->nobj++;
    Obj *o = &s->obj[i];
    memset(o, 0, sizeof *o);
    o->type = type;
    defaults(type, o->par);
    o->scl = v3(1, 1, 1);
    o->mat = material_default();
    o->flags = OF_WIRE | OF_RENDER;
    o->parent = parent;
    il_add(parent >= 0 ? &s->obj[parent].kids : &s->roots, i);
    return i;
}

static void scene_init(Scene *s)
{
    World *w = &s->world;
    memset(s, 0, sizeof *s);
    s->light[0].dir = 1; s->light[0].pos = v3(-0.5, 0.8, 0.6); s->light[0].color = v3(1.3, 1.15, 0.95);
    s->light[0].intensity = 1; s->light[0].shadow = 1; s->light[0].softness = 8; s->light[0].on = 1;
    s->nlight = 1;
    s->cam_pos = v3(2.9, 2.3, 3.5); s->cam_target = v3(0, 0.6, 0); s->cam_focal = 2.2;
    w->floor = 1; w->floor_a = v3(0.42, 0.4, 0.38); w->floor_b = v3(0.2, 0.2, 0.22); w->floor_scale = 1.5;
    w->floor_type = FLOOR_CHECKER;
    w->sky_top = v3(0.55, 0.7, 1); w->sky_horizon = v3(0.9, 0.95, 1); w->sky_light = v3(0.25, 0.3, 0.45);
    w->ambient = v3(0.08, 0.07, 0.06); w->fog = 0.0015; w->fog_color = v3(0.8, 0.85, 1); w->ao = 1;
    w->env = ENV_SKY; strcpy(w->env_preset, "Softbox studio"); w->env_intensity = 1; w->env_background = 1;
    w->bg_color = v3(0.85, 0.85, 0.86); w->env_lights = 1;
}

static void walk(const Scene *s, int o, IList *out)
{
    int i;
    il_add(out, o);
    for (i = 0; i < s->obj[o].kids.n; i++) walk(s, s->obj[o].kids.v[i], out);
}

static IList walk_all(const Scene *s)
{
    IList l = { 0 };
    int i;
    for (i = 0; i < s->roots.n; i++) walk(s, s->roots.v[i], &l);
    return l;
}

static M34 obj_world(const Scene *s, int o)
{
    const Obj *ob = &s->obj[o];
    M34 pw = ob->parent >= 0 ? obj_world(s, ob->parent) : identity, l = m34_compose(ob->pos, ob->rot, ob->scl);
    return m34_mul(&pw, &l);
}

/* la lettura */
typedef struct { const char *t; int p, len, line; char err[128]; int failed; } Reader;

static int is_space(int c) { return c == ' ' || c == '\t' || c == '\r'; }

static void fail(Reader *r, const char *msg)
{
    if (!r->failed) snprintf(r->err, sizeof r->err, "line %d: %.100s", r->line, msg);
    r->failed = 1;
}

static int word(Reader *r, char *w)
{
    int n = 0;
    while (r->p < r->len && is_space(r->t[r->p])) r->p++;
    w[0] = 0;
    if (r->p >= r->len || r->t[r->p] == '\n') return 0;
    if (r->t[r->p] == '"') {
        r->p++;
        while (r->p < r->len && r->t[r->p] != '"' && r->t[r->p] != '\n') { if (n < 63) w[n++] = r->t[r->p]; r->p++; }
        if (r->p < r->len && r->t[r->p] == '"') r->p++;
    } else
        while (r->p < r->len && !is_space(r->t[r->p]) && r->t[r->p] != '\n') { if (n < 63) w[n++] = r->t[r->p]; r->p++; }
    w[n] = 0;
    return 1;
}

static int has_more(Reader *r)
{
    while (r->p < r->len && is_space(r->t[r->p])) r->p++;
    return r->p < r->len && r->t[r->p] != '\n';
}

static void next_line(Reader *r)
{
    while (r->p < r->len && r->t[r->p] != '\n') r->p++;
    if (r->p < r->len) r->p++;
    r->line++;
}

/* il numero come lo accetta fx_parse di RISC OS: [+-]cifre[.cifre] */
static double num(Reader *r)
{
    char w[64];
    int i = 0, digits = 0;
    long ip = 0;
    double v;
    if (!word(r, w)) { fail(r, "number expected"); return 0; }
    if (w[i] == '-' || w[i] == '+') i++;
    while (w[i] >= '0' && w[i] <= '9') {
        ip = ip * 10 + (w[i++] - '0');
        digits++;
        if (ip > 32767) { fail(r, "number expected"); return 0; }
    }
    if (w[i] == '.') { i++; while (w[i] >= '0' && w[i] <= '9') { i++; digits++; } }
    if (w[i] || !digits) { fail(r, "number expected"); return 0; }
    v = strtod(w, NULL);
    return v == 0 ? 0 : v;
}

static int inum(Reader *r) { return (int)floor(num(r)); }
static V3 vec(Reader *r) { double x = num(r), y = num(r), z = num(r); return v3(x, y, z); }

static void read_gpu(Reader *r, Scene *s, int cur)
{
    char w[64], tw[64];
    word(r, w);
    if (!strcmp(w, "material")) {
        Material *m;
        if (cur < 0) { fail(r, "object expected"); return; }
        m = &s->obj[cur].mat;
        m->reflect = rclamp(num(r), 0, 1);
        m->roughness = rclamp(num(r), 0, 1);
        m->transparency = rclamp(num(r), 0, 1);
        m->ior = rclamp(num(r), 1, 3);
        m->metal = num(r) != 0;
        if (has_more(r)) {
            double sr, sg, sb;
            m->sheen = rclamp(num(r), 0, 1);
            sr = num(r); sg = num(r); sb = num(r);
            m->sheen_color = v3(rclamp(sr, 0, 1), rclamp(sg, 0, 1), rclamp(sb, 0, 1));
            m->sheen_rough = rclamp(num(r), 0, 1);
        }
    } else if (!strcmp(w, "floor")) {
        World *f = &s->world;
        word(r, tw);
        f->floor_type = !strcmp(tw, "solid") ? FLOOR_SOLID : !strcmp(tw, "grid") ? FLOOR_GRID : FLOOR_CHECKER;
        f->floor_a = vec(r);
        f->floor_b = vec(r);
        f->floor_scale = rclamp(num(r), 0.01, 100);
        f->floor_reflect = rclamp(num(r), 0, 1);
    } else if (!strcmp(w, "env")) {
        World *e = &s->world;
        word(r, tw);
        e->env = !strcmp(tw, "studio") ? ENV_STUDIO : !strcmp(tw, "hdri") ? ENV_HDRI : ENV_SKY;
        word(r, e->env_preset);
        e->env_intensity = rclamp(num(r), 0, 100);
        e->env_rotation = num(r);
        e->env_background = num(r) != 0;
        e->bg_color = vec(r);
        e->env_lights = num(r) != 0;
        word(r, tw);
        if (e->env == ENV_HDRI) fail(r, "HDRI environments are not supported by this version");
    } else if (!strcmp(w, "light")) {
        if (s->nlight == 0) { fail(r, "light expected"); return; }
        word(r, tw);
        s->light[s->nlight - 1].on = num(r) != 0;
        s->light[s->nlight - 1].intensity = num(r);
    } else if (!strcmp(w, "matparent")) {
        if (cur < 0) { fail(r, "object expected"); return; }
        s->obj[cur].inherit_mat = 1;
    } else if (!strcmp(w, "tone")) {
        word(r, tw);
        s->world.tone_map = !strcmp(tw, "neutral");
        s->world.exposure = rclamp(num(r), -10, 10);
    } else if (!strcmp(w, "displace") || !strcmp(w, "symmetry") || !strcmp(w, "profile") || !strcmp(w, "cable") ||
               !strcmp(w, "cablepts") || !strcmp(w, "cablecurve") || !strcmp(w, "instance") || !strcmp(w, "cloner") ||
               !strcmp(w, "deformer"))
        fail(r, "#gpu line not supported by this version");
}

static int scene_read(Scene *s, const char *text, char *err, int errlen)
{
    Reader r;
    char w[64];
    int infile[MAX_OBJECTS], ninfile = 0, file_lights = 0, cur = -1, i;
    memset(&r, 0, sizeof r);
    r.t = text;
    r.len = (int)strlen(text);
    r.line = 1;
    scene_init(s);
    if (!word(&r, w) || strcmp(w, "RayMarchStudio")) { snprintf(err, errlen, "line 1: not a RayMarch Studio scene"); return 1; }
    next_line(&r);
    while (r.p < r.len && !r.failed) {
        int got = word(&r, w);
        if (got && !strcmp(w, "#gpu")) { read_gpu(&r, s, cur); next_line(&r); continue; }
        if (!got || w[0] == '#') { next_line(&r); continue; }
        if (!strcmp(w, "camera")) {
            s->cam_pos = vec(&r); s->cam_target = vec(&r); s->cam_focal = num(&r);
        } else if (!strcmp(w, "light")) {
            Light *l;
            char tw[64];
            if (!file_lights) { s->nlight = 0; file_lights = 1; }
            if (s->nlight >= MAX_LIGHTS) { fail(&r, "too many lights"); break; }
            l = &s->light[s->nlight++];
            memset(l, 0, sizeof *l);
            l->on = 1;
            word(&r, tw);
            l->dir = strcmp(tw, "point") != 0;
            l->pos = vec(&r); l->color = vec(&r); l->intensity = num(&r);
            l->shadow = num(&r) != 0; l->softness = num(&r);
        } else if (!strcmp(w, "snap")) {
            num(&r); num(&r);
        } else if (!strcmp(w, "world")) {
            s->world.floor = num(&r) != 0; s->world.fog = num(&r); s->world.ao = num(&r) != 0;
        } else if (!strcmp(w, "object")) {
            int parent = inum(&r), type, op;
            char tw[64], ow[64], nw[64];
            word(&r, tw);
            if (!strcmp(tw, "group")) type = T_GROUP;
            else if (!strcmp(tw, "sphere")) type = T_SPHERE;
            else if (!strcmp(tw, "box")) type = T_BOX;
            else if (!strcmp(tw, "cylinder")) type = T_CYLINDER;
            else { fail(&r, "object type not supported by this version"); break; }
            word(&r, ow);
            op = !strcmp(ow, "subtract") ? OP_SUB : !strcmp(ow, "intersect") ? OP_INTER : OP_UNION;
            if (parent >= ninfile || ninfile >= MAX_OBJECTS) { fail(&r, "bad parent"); break; }
            cur = scene_add(s, type, parent < 0 ? -1 : infile[parent]);
            infile[ninfile++] = cur;
            s->obj[cur].op = op;
            s->obj[cur].k = num(&r);
            s->obj[cur].flags = (unsigned)inum(&r) & (OF_WIRE | OF_RENDER | OF_LOCK | OF_OFF);
            word(&r, nw);
        } else {
            Obj *o;
            if (cur < 0) { fail(&r, "object expected"); break; }
            o = &s->obj[cur];
            if (!strcmp(w, "pos")) o->pos = vec(&r);
            else if (!strcmp(w, "rot")) o->rot = vec(&r);
            else if (!strcmp(w, "scale")) o->scl = vec(&r);
            else if (!strcmp(w, "material")) { o->mat.color = vec(&r); o->mat.specular = num(&r); o->mat.shininess = num(&r); }
            else if (!strcmp(w, "modify")) { o->round = num(&r); o->shell = num(&r); }
            else if (!strcmp(w, "param")) {
                double tmp[10];
                int np = defaults(o->type, tmp), j;
                for (j = 0; j < np; j++) o->par[j] = num(&r);
            } else { fail(&r, "unknown keyword"); break; }
        }
        next_line(&r);
    }
    /* Off vale solo per i generatori, che qui non ci sono */
    for (i = 0; i < ninfile; i++) s->obj[infile[i]].flags &= ~OF_OFF;
    if (r.failed) { snprintf(err, errlen, "%s", r.err); return 1; }
    return 0;
}

/* ---- la preparazione (gscene.c, shapes.c, wire.c) */

/* i dati di preparazione che il caso caldo non usa */
typedef struct { M34 world; int baked; } Prep;

typedef struct {
    const Scene *s;
    GObj *obj;
    Prep *prep;
    Ins *prog;
    int nprog, capprog;
    double *bound;
    int nbound, capbound;
} Builder;

static void prim_prepare(int t, const double *p, GObj *f)
{
    f->c[0] = f->c[1] = f->c[2] = f->c[3] = 0;
    if (t == T_BOX) {
        double r = rmin(p[3], rmin(p[0] / 2, rmin(p[1] / 2, p[2] / 2)));
        f->c[0] = p[0] / 2 - r; f->c[1] = p[1] / 2 - r; f->c[2] = p[2] / 2 - r; f->c[3] = r;
    } else if (t == T_CYLINDER) {
        double b = rmin(p[2], rmin(p[0], p[1] / 2));
        f->c[0] = p[0] - b; f->c[1] = p[1] / 2 - b; f->c[2] = b;
    }
    f->p0 = p[0];
}

static double prim_bound(int t, const double *p)
{
    if (t == T_SPHERE) return p[0];
    if (t == T_BOX) return sqrt(p[0] / 2 * (p[0] / 2) + p[1] / 2 * (p[1] / 2) + p[2] / 2 * (p[2] / 2));
    return rlen2(p[0], p[1] / 2);
}

/* il wireframe delle forme: solo i punti, per extend_cutters */
typedef struct { V3 *p; int n, cap; } Pts;

static void pts_add(Pts *l, V3 p)
{
    if (l->n >= l->cap) { l->cap = l->cap ? l->cap * 2 : 64; l->p = (V3 *)xrealloc(l->p, l->cap * sizeof(V3)); }
    l->p[l->n++] = p;
}

static double rad(double d) { return d * RM_PI / 180; }

static void arc(Pts *b, V3 c, V3 u, V3 v, int a0, int a1)
{
    int a;
    for (a = a0; a <= a1; a += 360 / 24) pts_add(b, v3add(c, v3add(v3mul(u, cos(rad(a))), v3mul(v, sin(rad(a))))));
}

static void hcircle(Pts *b, double y, double r) { arc(b, v3(0, y, 0), v3(r, 0, 0), v3(0, 0, r), 0, 360); }

static void wire_build(const Obj *o, Pts *b)
{
    const double *p = o->par;
    int i, s;
    if (o->type == T_SPHERE) {
        arc(b, v3(0, 0, 0), v3(p[0], 0, 0), v3(0, p[0], 0), 0, 360);
        arc(b, v3(0, 0, 0), v3(0, p[0], 0), v3(0, 0, p[0]), 0, 360);
        hcircle(b, 0, p[0]);
    } else if (o->type == T_BOX) {
        double hx = p[0] / 2, hy = p[1] / 2, hz = p[2] / 2;
        for (i = 0; i < 4; i++) {
            double sx = (i & 1) ? hx : -hx, sz = (i & 2) ? hz : -hz;
            pts_add(b, v3(sx, -hy, sz)); pts_add(b, v3(sx, hy, sz));
        }
        for (s = -1; s <= 1; s += 2) {
            double y = s * hy;
            pts_add(b, v3(-hx, y, -hz)); pts_add(b, v3(hx, y, -hz)); pts_add(b, v3(hx, y, hz));
            pts_add(b, v3(-hx, y, hz)); pts_add(b, v3(-hx, y, -hz));
        }
    } else if (o->type == T_CYLINDER) {
        double y0 = -p[1] / 2, y1 = p[1] / 2, r = p[0];
        hcircle(b, y0, r);
        hcircle(b, y1, r);
        pts_add(b, v3(r, y0, 0)); pts_add(b, v3(r, y1, 0)); pts_add(b, v3(-r, y0, 0)); pts_add(b, v3(-r, y1, 0));
        pts_add(b, v3(0, y0, r)); pts_add(b, v3(0, y1, r)); pts_add(b, v3(0, y0, -r)); pts_add(b, v3(0, y1, -r));
    }
}

static int visible(const Scene *s, int o)
{
    const Obj *ob = &s->obj[o];
    int i;
    if (!(ob->flags & OF_RENDER)) return 0;
    if (ob->type >= 0) return 1;
    for (i = 0; i < ob->kids.n; i++) if (visible(s, ob->kids.v[i])) return 1;
    return 0;
}

static void merge(V3 *c, double *r, V3 c2, double r2)
{
    double d, nr;
    if (*r < 0) { *c = c2; *r = r2; return; }
    d = v3len(v3sub(c2, *c));
    if (d + r2 <= *r) return;
    if (d + *r <= r2) { *c = c2; *r = r2; return; }
    nr = (d + *r + r2) / 2;
    *c = v3add(*c, v3mul(v3sub(c2, *c), (nr - *r) / d));
    *r = nr;
}

static void list_bound(Builder *b, const IList *list, V3 *c, double *r)
{
    int i, first = 1;
    *c = v3(0, 0, 0);
    *r = -1;
    for (i = 0; i < list->n; i++) {
        const Obj *o = &b->s->obj[list->v[i]];
        const GObj *f = &b->obj[o->index];
        if (!(o->flags & OF_RENDER) || f->br < 0) continue;
        if (first || o->op == OP_UNION) merge(c, r, f->bc, f->br);
        first = 0;
    }
}

/* un box o un cilindro scalato non uniforme lungo i propri assi: la stessa
 * forma con le misure scalate (distanza esatta) */
static int try_bake(const Obj *o, GObj *f, Prep *pr)
{
    const double *w = pr->world.m;
    V3 col[3];
    double c0, c1, c2, cmax, cmin, tol, p[10];
    M34 u, inv;
    int i;
    if (o->type != T_BOX && o->type != T_CYLINDER) return 0;
    for (i = 0; i < 3; i++) col[i] = v3(w[i], w[4 + i], w[8 + i]);
    c0 = v3len(col[0]); c1 = v3len(col[1]); c2 = v3len(col[2]);
    cmax = rmax(c0, rmax(c1, c2));
    if (cmax <= 0 || rmin(c0, rmin(c1, c2)) <= 0) return 0;
    tol = 1e-9 * cmax;
    if (fabs(c0 - c1) <= tol && fabs(c1 - c2) <= tol) return 0;
    if (fabs(v3dot(col[0], col[1])) > tol * cmax || fabs(v3dot(col[1], col[2])) > tol * cmax ||
        fabs(v3dot(col[0], col[2])) > tol * cmax) return 0;
    if (o->type == T_CYLINDER && fabs(c0 - c2) > tol) return 0;
    u.m[0] = w[0] / c0; u.m[1] = w[1] / c1; u.m[2] = w[2] / c2; u.m[3] = w[3];
    u.m[4] = w[4] / c0; u.m[5] = w[5] / c1; u.m[6] = w[6] / c2; u.m[7] = w[7];
    u.m[8] = w[8] / c0; u.m[9] = w[9] / c1; u.m[10] = w[10] / c2; u.m[11] = w[11];
    if (!m34_inverse(&u, &inv)) return 0;
    cmin = rmin(c0, rmin(c1, c2));
    memcpy(p, o->par, sizeof p);
    if (o->type == T_BOX) { p[0] *= c0; p[1] *= c1; p[2] *= c2; p[3] *= cmin; }
    else { p[0] *= c0; p[1] *= c1; p[2] *= cmin; }
    f->inv = inv;
    f->dscale = 1;
    prim_prepare(o->type, p, f);
    pr->baked = 1;
    return 1;
}

static void update_list(Builder *b, const IList *list, const M34 *pw, double pscale)
{
    int i;
    for (i = 0; i < list->n; i++) {
        const Obj *o = &b->s->obj[list->v[i]];
        GObj *f = &b->obj[o->index];
        Prep *pr = &b->prep[o->index];
        double smin = rmin(fabs(o->scl.x), rmin(fabs(o->scl.y), fabs(o->scl.z)));
        M34 l = m34_compose(o->pos, o->rot, o->scl);
        pr->world = m34_mul(pw, &l);
        if (!m34_inverse(&pr->world, &f->inv)) f->inv = identity;
        f->dscale = pscale * smin;
        f->br = -1;
        pr->baked = 0;
        if (o->type >= 0) {
            prim_prepare(o->type, o->par, f);
            try_bake(o, f, pr);
            f->bc = m34_trans(&pr->world);
            f->br = prim_bound(o->type, o->par) * m34_maxscale(&pr->world);
        } else {
            update_list(b, &o->kids, &pr->world, f->dscale);
            list_bound(b, &o->kids, &f->bc, &f->br);
        }
        if (f->br >= 0) f->br += o->k + o->round + o->shell;
    }
}

/* Un box o un cilindro che scava o interseca: le facce che stanno tutte
 * oltre l'ingombro dei fratelli prima si spingono lontanissimo */
static void extend_cutters(Builder *b, const IList *list)
{
    const Scene *s = b->s;
    Pts loc = { 0 };
    int i, j, k, a, q;
    for (i = 0; i < list->n; i++) {
        const Obj *o = &s->obj[list->v[i]];
        GObj *f;
        V3 lo = v3(1e300, 1e300, 1e300), hi = v3(-1e300, -1e300, -1e300);
        double margin, sc, size, m, far, shift[3] = { 0, 0, 0 };
        int n = 0, axes, changed = 0;
        if (o->type < 0) { extend_cutters(b, &o->kids); continue; }
        if (i == 0 || o->op == OP_UNION || (o->type != T_BOX && o->type != T_CYLINDER) || !visible(s, list->v[i])) continue;
        f = &b->obj[o->index];
        margin = o->k;
        for (j = 0; j < i; j++) {
            IList all = { 0 };
            if (!visible(s, list->v[j])) continue;
            walk(s, list->v[j], &all);
            for (k = 0; k < all.n; k++) {
                const Obj *d = &s->obj[all.v[k]];
                M34 w;
                margin = rmax(margin, o->k + d->k + fabs(d->round) + d->shell);
                if (d->type < 0 || !visible(s, all.v[k])) continue;
                w = obj_world(s, all.v[k]);
                loc.n = 0;
                wire_build(d, &loc);
                for (q = 0; q < loc.n; q++) {
                    V3 pt = m34_point(&f->inv, m34_point(&w, loc.p[q]));
                    lo = v3(rmin(lo.x, pt.x), rmin(lo.y, pt.y), rmin(lo.z, pt.z));
                    hi = v3(rmax(hi.x, pt.x), rmax(hi.y, pt.y), rmax(hi.z, pt.z));
                    n++;
                }
            }
            free(all.v);
        }
        if (n == 0) continue;
        sc = m34_maxscale(&f->inv);
        size = v3len(v3sub(hi, lo));
        m = margin * sc + 0.01 * size + 1e-6;
        axes = o->type == T_BOX ? 3 : 1;
        far = 100 * rmax(size, 1e-3);
        for (a = 0; a < axes; a++) {
            int kk = o->type == T_BOX ? a : 1;
            double r = o->type == T_BOX ? f->c[3] : f->c[2];
            double h = f->c[kk] + r, fLo = -h, fHi = h;
            if (v3get(hi, kk) + m < fHi) fHi = far + h;
            if (v3get(lo, kk) - m > fLo) fLo = -far - h;
            if (fHi == h && fLo == -h) continue;
            shift[kk] = (fLo + fHi) / 2;
            f->c[kk] = (fHi - fLo) / 2 - r;
            changed = 1;
        }
        if (!changed) continue;
        {
            M34 t = { { 1, 0, 0, -shift[0], 0, 1, 0, -shift[1], 0, 0, 1, -shift[2] } };
            f->inv = m34_mul(&t, &f->inv);
        }
        f->br = 1e9;
    }
    free(loc.p);
}

/* ---- il programma (compile_list, compile_obj, emit_union) */

static int emit(Builder *b, int code, int obj, int cull, int n)
{
    if (b->nprog >= b->capprog) { b->capprog = b->capprog ? b->capprog * 2 : 64; b->prog = (Ins *)xrealloc(b->prog, b->capprog * sizeof(Ins)); }
    b->prog[b->nprog].code = code; b->prog[b->nprog].obj = obj; b->prog[b->nprog].cull = cull; b->prog[b->nprog].n = n;
    return b->nprog++;
}

static void set_n(Builder *b, int at) { b->prog[at].n = b->nprog - at - 1; }

static int compile_list(Builder *b, const IList *list, int neg);

static void compile_obj(Builder *b, int oi, int on)
{
    const Obj *o = &b->s->obj[oi];
    if (o->type == T_GROUP) compile_list(b, &o->kids, on);
    else { emit(b, I_PRIM, o->index, on ? 0 : 1, o->type); return; }
    if (o->round != 0 || o->shell != 0) emit(b, I_MOD, o->index, 0, 0);
}

static double fxv(double v) { return rround(v * 65536); }

#define BC(oi) (b->obj[b->s->obj[(oi)].index].bc)

static void emit_union(Builder *b, int *list, int at, int m)
{
    int si, k, j, ax, h;
    V3 c = v3(0, 0, 0);
    double r = -1, lo[3], hi[3];
    if (m <= 2) {
        compile_obj(b, list[at], 0);
        if (m == 2) { compile_obj(b, list[at + 1], 0); emit(b, I_MIN, b->s->obj[list[at + 1]].index, 0, 0); }
        return;
    }
    si = emit(b, I_SKIP, b->nbound, 0, 0);
    for (k = 0; k < m; k++) {
        const GObj *f = &b->obj[b->s->obj[list[at + k]].index];
        merge(&c, &r, f->bc, f->br);
        for (j = 0; j < 3; j++) {
            double q = fxv(v3get(f->bc, j));
            if (k == 0 || q < lo[j]) lo[j] = q;
            if (k == 0 || q > hi[j]) hi[j] = q;
        }
    }
    if (b->nbound >= b->capbound) { b->capbound = b->capbound ? b->capbound * 2 : 32; b->bound = (double *)xrealloc(b->bound, b->capbound * 4 * sizeof(double)); }
    b->bound[b->nbound * 4] = c.x; b->bound[b->nbound * 4 + 1] = c.y; b->bound[b->nbound * 4 + 2] = c.z; b->bound[b->nbound * 4 + 3] = r;
    b->nbound++;
    ax = 0;
    if (hi[1] - lo[1] > hi[ax] - lo[ax]) ax = 1;
    if (hi[2] - lo[2] > hi[ax] - lo[ax]) ax = 2;
    for (k = 1; k < m; k++) {
        int v = list[at + k];
        double key = fxv(v3get(BC(v), ax));
        j = k;
        while (j > 0 && fxv(v3get(BC(list[at + j - 1]), ax)) > key) { list[at + j] = list[at + j - 1]; j--; }
        list[at + j] = v;
    }
    h = m / 2;
    emit_union(b, list, at, h);
    emit_union(b, list, at + h, m - h);
    emit(b, I_MIN, b->s->obj[list[at + h]].index, 0, 0);
    set_n(b, si);
}

static int compile_list(Builder *b, const IList *list, int neg)
{
    const Scene *s = b->s;
    int n = 0, i = 0;
    while (i < list->n) {
        const Obj *o = &s->obj[list->v[i]];
        int on, li = -1;
        if (!visible(s, list->v[i])) { i++; continue; }
        if (!neg && (n == 0 || (o->op == OP_UNION && o->k == 0))) {
            int *run = (int *)xalloc((list->n + 1) * sizeof(int)), nrun = 0, ok = 1, k, first = list->v[i];
            for (; i < list->n; i++) {
                const Obj *q = &s->obj[list->v[i]];
                if (!visible(s, list->v[i])) continue;
                if (nrun > 0 && (q->op != OP_UNION || q->k != 0)) break;
                ok &= b->obj[q->index].br >= 0;
                run[nrun++] = list->v[i];
            }
            if (ok) emit_union(b, run, 0, nrun);
            else for (k = 0; k < nrun; k++) { compile_obj(b, run[k], 0); if (k > 0) emit(b, I_MIN, s->obj[run[k]].index, 0, 0); }
            if (n > 0) emit(b, I_MIN, s->obj[first].index, 0, 0);
            free(run);
            n++;
            continue;
        }
        on = neg ^ (n > 0 && o->op == OP_SUB);
        if (n > 0 && o->op == OP_SUB && b->obj[o->index].br >= 0) li = emit(b, I_LAZY, o->index, 0, 0);
        compile_obj(b, list->v[i], on);
        if (n > 0) emit(b, o->op == OP_UNION && o->k == 0 ? I_MIN : I_OP, o->index, 0, 0);
        if (li >= 0) set_n(b, li);
        n++;
        i++;
    }
    return n;
}

/* profondita' massima della pila dei valori: l'interprete la usa senza controlli */
static int measure(const Ins *prog, int n)
{
    int sp = 0, mx = 0, i;
    for (i = 0; i < n; i++) {
        if (prog[i].code == I_PRIM || prog[i].code == I_SKIP) sp++;
        else if (prog[i].code == I_MIN || prog[i].code == I_OP) sp--;
        if (sp > mx) mx = sp;
    }
    return mx;
}

static int nonuniform(const M34 *w)
{
    double a = m34_collen(w, 0), b = m34_collen(w, 1), c = m34_collen(w, 2), m = rmax(a, rmax(b, c));
    return fabs(a - b) > 1e-9 * m || fabs(b - c) > 1e-9 * m;
}

/* i figli delle primitive salgono di un livello (Hierarchy.Flatten, lift) */
static void lift(Scene *s, int owner)
{
    IList *list = owner < 0 ? &s->roots : &s->obj[owner].kids;
    int i, j;
    for (i = 0; i < list->n; i++) {
        int o = list->v[i];
        Obj *ob = &s->obj[o];
        IList kids;
        M34 m;
        int at;
        if (ob->type < 0) { lift(s, o); list = owner < 0 ? &s->roots : &s->obj[owner].kids; continue; }
        if (ob->kids.n == 0) continue;
        m = m34_compose(ob->pos, ob->rot, ob->scl);
        kids = ob->kids;
        memset(&ob->kids, 0, sizeof ob->kids);
        at = i + 1;
        for (j = 0; j < kids.n; j++) {
            Obj *c = &s->obj[kids.v[j]];
            M34 l = m34_compose(c->pos, c->rot, c->scl), t = m34_mul(&m, &l);
            m34_decompose(&t, &c->pos, &c->rot, &c->scl);
            c->parent = owner;
            il_insert(list, at++, kids.v[j]);
        }
        free(kids.v);
    }
}

static int gscene_build(GScene *g, Scene *s, char *err, int errlen)
{
    Builder b;
    IList all;
    int i;
    memset(g, 0, sizeof *g);
    memset(&b, 0, sizeof b);
    all = walk_all(s);
    for (i = 0; i < all.n; i++) {
        Obj *o = &s->obj[all.v[i]];
        if (o->inherit_mat) {
            int x = all.v[i], n;
            for (n = 0; s->obj[x].inherit_mat && s->obj[x].parent >= 0 && n < 64; n++) x = s->obj[x].parent;
            o->mat = s->obj[x].mat;
        }
    }
    for (i = 0; i < all.n; i++) s->obj[all.v[i]].inherit_mat = 0;
    free(all.v);
    lift(s, -1);
    all = walk_all(s);
    for (i = 0; i < all.n; i++) s->obj[all.v[i]].index = i;
    b.s = s;
    b.obj = (GObj *)xalloc((all.n + 1) * sizeof(GObj));
    b.prep = (Prep *)xalloc((all.n + 1) * sizeof(Prep));
    update_list(&b, &s->roots, &identity, 1);
    extend_cutters(&b, &s->roots);
    compile_list(&b, &s->roots, 0);
    list_bound(&b, &s->roots, &g->bc, &g->br);
    for (i = 0; i < all.n; i++) {
        const Obj *o = &s->obj[all.v[i]];
        const Material *m = &o->mat;
        GObj *f = &b.obj[o->index];
        double sh = rclamp(m->sheen, 0, 1);
        if (sh > 0 && o->type >= 0) { snprintf(err, errlen, "sheen is not supported by this version"); return 1; }
        f->type = o->type; f->op = o->op; f->k = o->k; f->round = o->round; f->shell = o->shell;
        f->mat.col = m->color; f->mat.spec = m->specular; f->mat.shin = m->shininess; f->mat.dif = m->diffuse; f->mat.sel = 0;
        f->mat.opt[0] = m->reflect; f->mat.opt[1] = m->roughness; f->mat.opt[2] = m->transparency; f->mat.opt[3] = m->ior;
        f->mat.metal = m->metal ? 1 : 0;
        f->mat.sheen[0] = m->sheen_color.x * sh; f->mat.sheen[1] = m->sheen_color.y * sh; f->mat.sheen[2] = m->sheen_color.z * sh;
        f->mat.sheen[3] = rclamp(m->sheen_rough, 0.07, 1);
    }
    for (i = 0; i < all.n; i++) {
        const Obj *o = &s->obj[all.v[i]];
        int j, vis = 0;
        if (o->mat.transparency > 0 && !o->mat.metal) g->has_glass = 1;
        if (o->type >= 0 && !b.prep[o->index].baked && nonuniform(&b.prep[o->index].world)) g->inexact = 1;
        if (o->type < 0 && o->shell > 0) {
            for (j = 0; j < o->kids.n; j++) if (visible(s, o->kids.v[j])) vis++;
            if (vis > 1) g->cavity = 1;
        }
    }
    g->obj = b.obj;
    g->nobj = all.n;
    g->prog = b.prog;
    g->nprog = b.nprog;
    g->max_stack = measure(b.prog, b.nprog);
    g->bound = b.bound ? b.bound : (double *)xalloc(4 * sizeof(double));
    free(b.prep);
    free(all.v);
    if (g->max_stack > 128) { snprintf(err, errlen, "scene too deep"); return 1; }
    return 0;
}

/* ---- l'interprete (eval.c) e le primitive (sdf.c) */

#define MAXST 128
#define FAR_D 1000.0
#define CULL_D 1.0

RM_INLINE double sd_box(const GObj *f, V3 p)
{
    const double *c = f->c;
    double qx = fabs(p.x) - c[0], qy = fabs(p.y) - c[1], qz = fabs(p.z) - c[2];
    return v3len(v3(rmax(qx, 0), rmax(qy, 0), rmax(qz, 0))) + rmin(rmax(qx, rmax(qy, qz)), 0) - c[3];
}

RM_INLINE double sd_cylinder(const GObj *f, V3 p)
{
    const double *c = f->c;
    double dx = rlen2(p.x, p.z) - c[0], dy = fabs(p.y) - c[1];
    return rmin(rmax(dx, dy), 0) + rlen2(rmax(dx, 0), rmax(dy, 0)) - c[2];
}

RM_INLINE double modify(const GObj *f, double d)
{
    d -= f->round;
    return f->shell > 0 ? fabs(d) - f->shell : d;
}

RM_INLINE double prim_dist(const GObj *f, V3 p)
{
    V3 q = m34_point(&f->inv, p);
    double d = f->type == T_SPHERE ? v3len(q) - f->p0 : f->type == T_BOX ? sd_box(f, q) : sd_cylinder(f, q);
    return modify(f, d * f->dscale);
}

RM_INLINE double cull_d(const GObj *f, V3 p) { return v3maxc(v3abs(v3sub(p, f->bc))) - f->br; }

/* l'operazione dell'oggetto: a = i fratelli precedenti, b = lui; *wa il peso di a */
RM_INLINE double combine(const GObj *f, double a, double b, double *wa)
{
    double k = f->k, h;
    if (f->op == OP_SUB) {
        if (k <= 0) { *wa = a > -b ? 1 : 0; return rmax(a, -b); }
        h = rclamp(0.5 - (b + a) * 0.5 / k, 0, 1);
        *wa = 1 - h;
        return a + (-b - a) * h + k * h * (1 - h);
    }
    if (f->op == OP_INTER) {
        if (k <= 0) { *wa = a > b ? 1 : 0; return rmax(a, b); }
        h = rclamp(0.5 - (b - a) * 0.5 / k, 0, 1);
        *wa = h;
        return b + (a - b) * h + k * h * (1 - h);
    }
    if (k <= 0) { *wa = a < b ? 1 : 0; return rmin(a, b); }
    h = rclamp(0.5 + (b - a) * 0.5 / k, 0, 1);
    *wa = h;
    return b + (a - b) * h - k * h * (1 - h);
}

static void mat_default(GMat *m)
{
    memset(m, 0, sizeof *m);
    m->col = v3(0.75, 0.3, 0.15);
    m->spec = 1; m->shin = 24; m->dif = 1;
    m->opt[3] = 1.5;
    m->sheen[3] = 0.5;
}

RM_INLINE void mat_mix(GMat *o, const GMat *a, const GMat *b, double wa)
{
    GMat r;
    int i;
    r.col = v3lerp(b->col, a->col, wa);
    r.spec = rmix(b->spec, a->spec, wa);
    r.shin = rmix(b->shin, a->shin, wa);
    r.dif = rmix(b->dif, a->dif, wa);
    r.sel = rmix(b->sel, a->sel, wa);
    for (i = 0; i < 4; i++) r.opt[i] = rmix(b->opt[i], a->opt[i], wa);
    r.metal = rmix(b->metal, a->metal, wa);
    for (i = 0; i < 4; i++) r.sheen[i] = rmix(b->sheen[i], a->sheen[i], wa);
    *o = r;
}

/* la distanza (con le scorciatoie delle sfere): il caso caldo, senza materiali */
static double map_d(const Ctx *c, V3 p)
{
    const GScene *g = c->g;
    const Ins *prog = g->prog;
    const GObj *obj = g->obj;
    double st[MAXST];
    int sp = 0, pc = 0, end = g->nprog;
    while (pc < end) {
        const Ins *ins = &prog[pc++];
        switch (ins->code) {
        case I_MIN: sp--; st[sp - 1] = rmin(st[sp - 1], st[sp]); break;
        case I_SKIP: {
            const double *b = g->bound + ins->obj * 4;
            double db = v3maxc(v3abs(v3sub(p, v3(b[0], b[1], b[2])))) - b[3];
            if (db > CULL_D) { st[sp++] = db; pc += ins->n; }
            break;
        }
        case I_LAZY: {
            const GObj *f = &obj[ins->obj];
            if (cull_d(f, p) >= f->k - st[sp - 1]) pc += ins->n;
            break;
        }
        case I_PRIM: {
            const GObj *f = &obj[ins->obj];
            if (ins->cull) {
                double db = cull_d(f, p);
                if (db > CULL_D) { st[sp++] = db; break; }
            }
            st[sp++] = prim_dist(f, p);
            break;
        }
        case I_MOD: st[sp - 1] = modify(&obj[ins->obj], st[sp - 1]); break;
        default: { double wa; sp--; st[sp - 1] = combine(&obj[ins->obj], st[sp - 1], st[sp], &wa); }
        }
    }
    return sp > 0 ? st[0] : FAR_D;
}

/* la distanza e il materiale (mode 1) o la primitiva piu' vicina (mode 2) */
static double run_m(const Ctx *c, int mode, V3 p, GMat *mout, double *bd, int *best)
{
    const GScene *g = c->g;
    double st[MAXST];
    GMat ms[MAXST];
    int sp = 0, pc = 0;
    while (pc < g->nprog) {
        const Ins *ins = &g->prog[pc++];
        const GObj *f = &g->obj[ins->obj];
        switch (ins->code) {
        case I_MIN:
            sp--;
            if (mode == 1) { if (st[sp] < st[sp - 1]) { st[sp - 1] = st[sp]; ms[sp - 1] = ms[sp]; } }
            else st[sp - 1] = rmin(st[sp - 1], st[sp]);
            break;
        case I_SKIP: case I_LAZY: break;
        case I_PRIM:
            if (mode == 1) ms[sp] = f->mat;
            st[sp] = prim_dist(f, p);
            if (mode == 2 && fabs(st[sp]) < *bd) { *bd = fabs(st[sp]); *best = ins->obj; }
            sp++;
            break;
        case I_MOD: st[sp - 1] = modify(f, st[sp - 1]); break;
        default: {
            double wa;
            sp--;
            st[sp - 1] = combine(f, st[sp - 1], st[sp], &wa);
            if (mode == 1) mat_mix(&ms[sp - 1], &ms[sp - 1], &ms[sp], wa);
        }
        }
    }
    if (mout) { if (mode == 1 && sp > 0) *mout = ms[0]; else mat_default(mout); }
    return sp > 0 ? st[0] : FAR_D;
}

static void map_m(const Ctx *c, V3 p, GMat *m) { double bd = 0; int best = 0; run_m(c, 1, p, m, &bd, &best); }
static int pick_obj(const Ctx *c, V3 p) { double bd = 1e30; int best = -1; run_m(c, 2, p, NULL, &bd, &best); return best; }

/* ---- l'ambiente (envmap.c) */

typedef struct { int w, h; float *rgb; } HdrImage;

#define ENV_MAXLIGHTS 3
#define ENV_MAXLEVELS 16

typedef struct {
    HdrImage lev[ENV_MAXLEVELS];
    int nlev, nlights;
    V3 ldir[ENV_MAXLIGHTS], lcol[ENV_MAXLIGHTS];
    double lrad[ENV_MAXLIGHTS];
    HdrImage small, residual;
} EnvMap;

static V3 env_dir(double u, double v)
{
    double phi = (u - 0.5) * RM_TP, th = v * RM_PI;
    return v3(sin(phi) * sin(th), cos(th), -cos(phi) * sin(th));
}

static V3 from_angles(double az, double el)
{
    double a = az * RM_PI / 180, e = el * RM_PI / 180;
    return v3(sin(a) * cos(e), sin(e), cos(a) * cos(e));
}

static V3 env_rot_y(V3 d, double deg)
{
    double a = deg * RM_PI / 180, c = cos(a), s = sin(a);
    return v3(c * d.x + s * d.z, d.y, -s * d.x + c * d.z);
}

static double lum(const float *c) { return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]; }

typedef struct { double az, el, hw, hh, l; V3 col; double soft; } Panel;
typedef struct { const char *name; V3 top, horizon, bottom; int np; Panel p[4]; } Studio;

#define W1 { 1, 1, 1 }
static const Studio studios[5] = {
    { "Light tent", { 1.15, 1.15, 1.15 }, { 0.85, 0.85, 0.85 }, { 0.6, 0.6, 0.6 }, 3, {
        { 0, 75, 45, 45, 2.2, W1, 0.3 }, { -85, 12, 18, 28, 0.03, W1, 0.1 }, { 85, 12, 18, 28, 0.03, W1, 0.1 } } },
    { "Softbox studio", { 0.07, 0.07, 0.075 }, { 0.045, 0.045, 0.05 }, { 0.03, 0.03, 0.03 }, 4, {
        { -45, 35, 22, 22, 9, W1, 0.15 }, { 60, 20, 22, 30, 2.5, W1, 0.15 }, { 0, 80, 30, 30, 3, W1, 0.15 },
        { 160, 30, 10, 25, 6, W1, 0.15 } } },
    { "Strip lights", { 0.012, 0.012, 0.012 }, { 0.008, 0.008, 0.008 }, { 0.006, 0.006, 0.006 }, 4, {
        { -100, 15, 5, 40, 12, W1, 0.1 }, { 100, 15, 5, 40, 12, W1, 0.1 }, { 180, 30, 28, 4, 7, W1, 0.1 },
        { 0, 72, 4, 35, 7, W1, 0.1 } } },
    { "High key", { 1.05, 1.05, 1.05 }, { 1, 1, 1 }, { 0.85, 0.85, 0.85 }, 2, {
        { 0, 30, 50, 30, 3, W1, 0.3 }, { 0, 80, 40, 40, 3, W1, 0.3 } } },
    { "Window", { 0.3, 0.27, 0.24 }, { 0.22, 0.19, 0.16 }, { 0.12, 0.1, 0.08 }, 1, {
        { -60, 18, 30, 25, 7, { 1, 0.98, 0.95 }, 0.05 } } },
};

static double edge(double x, double soft)
{
    double t = rclamp((1 - x) / soft, 0, 1);
    return t * t * (3 - 2 * t);
}

static void studio_render(const Studio *s, int w, HdrImage *img)
{
    int h = w / 2, x, y, k;
    V3 c[4], rt[4], up[4];
    double tw[4], th[4];
    img->w = w; img->h = h;
    img->rgb = (float *)xalloc((size_t)w * h * 3 * sizeof(float));
    for (k = 0; k < s->np; k++) {
        c[k] = from_angles(s->p[k].az, s->p[k].el);
        rt[k] = fabs(c[k].y) > 0.99 ? v3(1, 0, 0) : v3norm(v3cross(v3(0, 1, 0), c[k]));
        up[k] = v3cross(c[k], rt[k]);
        tw[k] = tan(s->p[k].hw * RM_PI / 180);
        th[k] = tan(s->p[k].hh * RM_PI / 180);
    }
    for (y = 0; y < h; y++)
        for (x = 0; x < w; x++) {
            V3 d = env_dir((x + 0.5) / w, (y + 0.5) / h), col;
            float *o = img->rgb + ((size_t)y * w + x) * 3;
            col = d.y >= 0 ? v3add(s->horizon, v3mul(v3sub(s->top, s->horizon), sqrt(d.y)))
                           : v3add(s->horizon, v3mul(v3sub(s->bottom, s->horizon), sqrt(-d.y)));
            for (k = 0; k < s->np; k++) {
                double z = v3dot(d, c[k]), px, py, m, soft;
                if (z <= 0) continue;
                px = fabs(v3dot(d, rt[k]) / z) / tw[k];
                py = fabs(v3dot(d, up[k]) / z) / th[k];
                soft = rmax(s->p[k].soft, 1e-3);
                m = edge(px, soft) * edge(py, soft);
                if (m > 0) col = v3add(col, v3mul(v3sub(v3mul(s->p[k].col, s->p[k].l), col), m));
            }
            o[0] = (float)col.x; o[1] = (float)col.y; o[2] = (float)col.z;
        }
}

static void shrink(const HdrImage *a, int k, HdrImage *r)
{
    int w = a->w / k, h = a->h / k, x, y, c, i, j;
    float inv = 1.0f / (k * k);
    if (w < 1) w = 1;
    if (h < 1) h = 1;
    r->w = w; r->h = h;
    r->rgb = (float *)xalloc((size_t)w * h * 3 * sizeof(float));
    for (y = 0; y < h; y++)
        for (x = 0; x < w; x++)
            for (c = 0; c < 3; c++) {
                float s = 0;
                for (j = 0; j < k; j++)
                    for (i = 0; i < k; i++) {
                        int yy = y * k + j < a->h ? y * k + j : a->h - 1, xx = x * k + i < a->w ? x * k + i : a->w - 1;
                        s += a->rgb[((size_t)yy * a->w + xx) * 3 + c];
                    }
                r->rgb[((size_t)y * w + x) * 3 + c] = s * inv;
            }
}

static void copy_img(const HdrImage *a, HdrImage *r)
{
    size_t n = (size_t)a->w * a->h * 3 * sizeof(float);
    r->w = a->w; r->h = a->h;
    r->rgb = (float *)xalloc(n);
    memcpy(r->rgb, a->rgb, n);
}

static double solid_angle(const HdrImage *s, int y) { return RM_TP / s->w * RM_PI / s->h * sin((y + 0.5) / s->h * RM_PI); }

static void extract(EnvMap *e)
{
    const HdrImage *sm = &e->small;
    int w = sm->w, h = sm->h, n, i, x, y;
    double *lm = (double *)xalloc((size_t)w * h * sizeof(double)), mean = 0, area = 0, first = 0;
    unsigned char *taken = (unsigned char *)xalloc((size_t)w * h);
    int *stack = (int *)xalloc((size_t)w * h * sizeof(int));
    for (y = 0; y < h; y++) {
        double da = solid_angle(sm, y);
        for (x = 0; x < w; x++) {
            i = y * w + x;
            lm[i] = lum(sm->rgb + i * 3);
            mean += lm[i] * da;
            area += da;
        }
    }
    mean /= area;
    for (n = 0; n < 3; n++) {
        int best = -1, top = 0;
        double peak, thr, omega = 0;
        V3 ev = v3(0, 0, 0), dir = v3(0, 0, 0);
        for (i = 0; i < w * h; i++) if (!taken[i] && (best < 0 || lm[i] > lm[best])) best = i;
        if (best < 0) break;
        peak = lm[best];
        if (n == 0) first = peak;
        if (peak < 4 * mean || peak < 0.25 * first) break;
        thr = rmax(2 * mean, 0.25 * peak);
        stack[top++] = best;
        taken[best] = 1;
        while (top > 0) {
            int ii = stack[--top], k, nb[4][2];
            double da, keep, exl;
            float exf[3];
            V3 c, ex;
            x = ii % w; y = ii / w;
            da = solid_angle(sm, y);
            keep = rmin(1, 2 * mean / rmax(lm[ii], 1e-9));
            c = v3(sm->rgb[ii * 3], sm->rgb[ii * 3 + 1], sm->rgb[ii * 3 + 2]);
            ex = v3mul(c, 1 - keep);
            ev = v3add(ev, v3mul(ex, da));
            exf[0] = (float)ex.x; exf[1] = (float)ex.y; exf[2] = (float)ex.z;
            exl = lum(exf);
            dir = v3add(dir, v3mul(env_dir((x + 0.5) / w, (y + 0.5) / h), exl * da));
            omega += da;
            e->residual.rgb[ii * 3] = (float)(c.x * keep);
            e->residual.rgb[ii * 3 + 1] = (float)(c.y * keep);
            e->residual.rgb[ii * 3 + 2] = (float)(c.z * keep);
            nb[0][0] = x - 1; nb[0][1] = y; nb[1][0] = x + 1; nb[1][1] = y;
            nb[2][0] = x; nb[2][1] = y - 1; nb[3][0] = x; nb[3][1] = y + 1;
            for (k = 0; k < 4; k++) {
                int j, ny = nb[k][1];
                if (ny < 0 || ny >= h) continue;
                j = ny * w + (nb[k][0] + w) % w;
                if (taken[j] || lm[j] < thr) continue;
                taken[j] = 1;
                stack[top++] = j;
            }
        }
        if (v3len(dir) < 1e-12) continue;
        e->ldir[e->nlights] = v3norm(dir);
        e->lcol[e->nlights] = v3mul(ev, 1 / RM_PI);
        e->lrad[e->nlights] = sqrt(omega / RM_PI);
        e->nlights++;
    }
    free(lm);
    free(taken);
    free(stack);
}

static EnvMap *envmap_of(const World *w)
{
    EnvMap *e;
    HdrImage img;
    int i, sel = 0, k;
    if (w->env != ENV_STUDIO) return NULL;
    for (i = 0; i < 5; i++) if (!strcmp(w->env_preset, studios[i].name)) sel = i;
    studio_render(&studios[sel], 1024, &img);
    e = (EnvMap *)xalloc(sizeof *e);
    k = img.w / 256 > 1 ? img.w / 256 : 1;
    e->lev[0] = img;
    e->nlev = 1;
    /* i mipmap: meta' per meta' fino a 1 (come glGenerateMipmap) */
    while (e->nlev < ENV_MAXLEVELS && (e->lev[e->nlev - 1].w > 1 || e->lev[e->nlev - 1].h > 1)) {
        shrink(&e->lev[e->nlev - 1], 2, &e->lev[e->nlev]);
        e->nlev++;
    }
    if (k > 1) shrink(&img, k, &e->small); else copy_img(&img, &e->small);
    copy_img(&e->small, &e->residual);
    extract(e);
    return e;
}

RM_INLINE V3 texel(const HdrImage *m, int x, int y)
{
    const float *p;
    x %= m->w;
    if (x < 0) x += m->w;
    if (y < 0) y = 0;
    if (y >= m->h) y = m->h - 1;
    p = m->rgb + ((size_t)y * m->w + x) * 3;
    return v3(p[0], p[1], p[2]);
}

static V3 bilinear(const HdrImage *m, double u, double v)
{
    double x = u * m->w - 0.5, y = v * m->h - 0.5, fx, fy;
    int x0 = (int)floor(x), y0 = (int)floor(y);
    fx = x - x0; fy = y - y0;
    return v3lerp(v3lerp(texel(m, x0, y0), texel(m, x0 + 1, y0), fx),
                  v3lerp(texel(m, x0, y0 + 1), texel(m, x0 + 1, y0 + 1), fx), fy);
}

static V3 envmap_sample(const EnvMap *e, V3 d, double lod)
{
    double u = atan2(d.x, -d.z) / RM_TP + 0.5, v = acos(rclamp(d.y, -1, 1)) / RM_PI, f;
    int l0;
    lod = rclamp(lod, 0, e->nlev - 1);
    l0 = (int)floor(lod);
    f = lod - l0;
    if (f <= 0 || l0 + 1 >= e->nlev) return bilinear(&e->lev[l0], u, v);
    return v3lerp(bilinear(&e->lev[l0], u, v), bilinear(&e->lev[l0 + 1], u, v), f);
}

static void envmap_irradiance(const EnvMap *e, double rotation, double intensity, int with_lights, V3 *c)
{
    static const double band[9] = { 1, 2.0 / 3, 2.0 / 3, 2.0 / 3, 0.25, 0.25, 0.25, 0.25, 0.25 };
    const HdrImage *src = with_lights ? &e->residual : &e->small;
    int w = src->w, h = src->h, i, j, k;
    double y[9];
    for (k = 0; k < 9; k++) c[k] = v3(0, 0, 0);
    for (j = 0; j < h; j++) {
        double da = solid_angle(src, j);
        for (i = 0; i < w; i++) {
            V3 d = env_rot_y(env_dir((i + 0.5) / w, (j + 0.5) / h), rotation);
            const float *o = src->rgb + ((size_t)j * w + i) * 3;
            V3 L = v3(o[0] * da, o[1] * da, o[2] * da);
            double x = d.x, yy = d.y, z = d.z;
            y[0] = 0.282095;
            y[1] = 0.488603 * yy; y[2] = 0.488603 * z; y[3] = 0.488603 * x;
            y[4] = 1.092548 * x * yy; y[5] = 1.092548 * yy * z; y[6] = 0.315392 * (3 * z * z - 1);
            y[7] = 1.092548 * x * z; y[8] = 0.546274 * (x * x - yy * yy);
            for (k = 0; k < 9; k++) c[k] = v3add(c[k], v3mul(L, y[k]));
        }
    }
    for (k = 0; k < 9; k++) c[k] = v3mul(c[k], band[k] * intensity);
}

/* ---- il rendering (render.c) */

#define EPS_N 0.002
#define MAXSTEP 256
#define HARD 60.0
#define RQ_SHADOWS 1
#define RQ_AO 2
#define RQ_SPEC 4

struct Render {
    Scene s;
    GScene g;
    const World *w;
    int wd, ht;
    V3 ro, fw, rt, up;
    double focal;
    int quality, bounces;
    unsigned sample;
    int nl;
    V3 lpos[MAX_LIGHTS], lcol[MAX_LIGHTS];
    double lkind[MAX_LIGHTS], lsoft[MAX_LIGHTS];
    int lshadow[MAX_LIGHTS];
    EnvMap *env;
    double env_cos, env_sin, env_gain, env_levels;
    V3 sh[9];
    /* i materiali misti anche dentro il vetro (le scene caricate da file; la
     * scena del benchmark resta col calcolo di RISC OS e della versione SGI) */
    int mix;
};

int render_w(const Render *r) { return r->wd; }
int render_h(const Render *r) { return r->ht; }
const GScene *render_scene(const Render *r) { return &r->g; }

static V3 env_lin(const Render *r, V3 d, double lod)
{
    V3 q = v3(r->env_cos * d.x - r->env_sin * d.z, d.y, r->env_sin * d.x + r->env_cos * d.z);
    return v3mul(envmap_sample(r->env, q, lod), r->env_gain);
}

static V3 env_irradiance(const Render *r, V3 n)
{
    const V3 *c = r->sh;
    V3 v = v3mul(c[0], 0.282095);
    v = v3add(v, v3mul(v3add(v3add(v3mul(c[1], n.y), v3mul(c[2], n.z)), v3mul(c[3], n.x)), 0.488603));
    v = v3add(v, v3mul(v3add(v3add(v3mul(c[4], n.x * n.y), v3mul(c[5], n.y * n.z)), v3mul(c[7], n.x * n.z)), 1.092548));
    v = v3add(v, v3add(v3mul(c[6], 0.315392 * (3 * n.z * n.z - 1)), v3mul(c[8], 0.546274 * (n.x * n.x - n.y * n.y))));
    return v3(rmax(v.x, 0), rmax(v.y, 0), rmax(v.z, 0));
}

RM_INLINE int hit_sphere(V3 o, V3 d, V3 c, double r, double *t0, double *t1)
{
    V3 oc = v3sub(o, c);
    double b = v3dot(oc, d), q = v3len(v3sub(oc, v3mul(d, b))), hh;
    *t0 = *t1 = 0;
    if (q >= r) return 0;
    hh = sqrt((r - q) * (r + q));
    *t0 = -b - hh;
    *t1 = -b + hh;
    return *t1 > 0;
}

/* numeri a caso (PCG) */
RM_INLINE double rnd(Ctx *c)
{
    unsigned w;
    c->rng = c->rng * 747796405u + 2891336453u;
    w = ((c->rng >> ((c->rng >> 28u) + 4u)) ^ c->rng) * 277803737u;
    return (double)((w >> 22u) ^ w) / 4294967296.0;
}

static double shadow(const Render *r, const Ctx *c, V3 p, V3 l, double tmax, double k)
{
    double t0, t1, res = 1, t, st, sm;
    int n, nmax;
    const GScene *g = &r->g;
    if (g->br < 0 || !hit_sphere(p, l, g->bc, g->br + 0.3, &t0, &t1)) return 1;
    t = rmax(t0, 0.02);
    tmax = rmin(tmax, t1);
    st = g->cavity ? 0.5 : 1;
    sm = g->cavity ? 0.01 : 0.02;
    nmax = g->cavity ? 256 : 128;
    for (n = 0; n < nmax && t < tmax; n++) {
        double h = map_d(c, v3add(p, v3mul(l, t))) * st;
        if (k * h < res * t) {
            res = k * h / t;
            if (res < 0.01) break;
        }
        t += rmax(h, sm);
    }
    res = rclamp(res, 0, 1);
    return res * res * (3 - 2 * res);
}

static V3 vpow(V3 a, double e) { return v3(pow(a.x, e), pow(a.y, e), pow(a.z, e)); }

/* materiali misti: quanto passa in un tratto lungo len del materiale m (il
 * colore per la trasparenza: dove il vetro sfuma nell'opaco assorbe di piu') */
static V3 mix_absorb(const GMat *m, double len)
{
    double a = rclamp(m->opt[2], 0, 1);
    return vpow(v3(rmax(m->col.x * a, 0.001), rmax(m->col.y * a, 0.001), rmax(m->col.z * a, 0.001)), len);
}

static V3 shadow_glass(const Render *r, const Ctx *c, V3 p, V3 l, double tmax, double k)
{
    double t0, t1, res = 1, t;
    V3 tr = v3(1, 1, 1);
    int n;
    const GScene *g = &r->g;
    if (g->br < 0 || !hit_sphere(p, l, g->bc, g->br + 0.3, &t0, &t1)) return tr;
    t = rmax(t0, 0.02);
    tmax = rmin(tmax, t1);
    for (n = 0; n < 160 && t < tmax; n++) {
        double h = map_d(c, v3add(p, v3mul(l, t)));
        if (k * h < res * t) {
            int o = -1;
            double a = 0;
            GMat mm;
            if (r->mix) {
                /* materiali misti: il materiale mescolato in questo punto */
                map_m(c, v3add(p, v3mul(l, t)), &mm);
                a = mm.metal > 0.5 ? 0 : rclamp(mm.opt[2], 0, 1);
            } else {
                o = pick_obj(c, v3add(p, v3mul(l, t)));
                if (o >= 0) {
                    const GMat *m = &g->obj[o].mat;
                    a = m->metal > 0.5 ? 0 : rclamp(m->opt[2], 0, 1);
                }
            }
            if (a <= 0) {
                res = k * h / t;
                if (res < 0.01) break;
            } else if (h < 0.001 && r->mix) {
                /* l'assorbimento tratto per tratto col materiale di ogni punto */
                V3 ta = mix_absorb(&mm, 0.003);
                int i;
                t += 0.003;
                for (i = 0; i < 96 && t < tmax; i++) {
                    V3 q = v3add(p, v3mul(l, t));
                    double hi = map_d(c, q), st;
                    GMat mi;
                    if (hi > 0.0005) break;
                    st = rmax(-hi, 0.004);
                    map_m(c, q, &mi);
                    ta = v3mulv(ta, mix_absorb(&mi, st));
                    t += st;
                }
                tr = v3mulv(tr, v3mul(ta, a));
                if (rmax(tr.x, rmax(tr.y, tr.z)) < 0.01) { res = 0; break; }
                t += 0.02;
                continue;
            } else if (h < 0.001) {
                double te = t;
                int i;
                V3 col = g->obj[o].mat.col;
                t += 0.003;
                for (i = 0; i < 96 && t < tmax; i++) {
                    double hi = map_d(c, v3add(p, v3mul(l, t)));
                    if (hi > 0.0005) break;
                    t += rmax(-hi, 0.004);
                }
                tr = v3mulv(tr, v3mul(vpow(v3(rmax(col.x, 0.001), rmax(col.y, 0.001), rmax(col.z, 0.001)), t - te), a));
                if (rmax(tr.x, rmax(tr.y, tr.z)) < 0.01) { res = 0; break; }
                t += 0.02;
                continue;
            }
        }
        t += g->cavity ? rmax(h * 0.5, 0.01) : rmax(h, 0.02);
    }
    res = rclamp(res, 0, 1);
    return v3mul(tr, res * res * (3 - 2 * res));
}

static double ambient_occlusion(const Render *r, const Ctx *c, V3 p, V3 n)
{
    double occ = 0, w = 1;
    int i;
    for (i = 0; i < 5; i++) {
        double e = 0.14 + i * 0.12, d;
        V3 q = v3add(p, v3mul(n, e));
        d = map_d(c, q);
        if (r->w->floor) d = rmin(d, q.y);
        occ += (e - d) * w;
        w *= 0.85;
    }
    return rclamp(1 - occ * 2.5, 0, 1);
}

static V3 light_dir(Ctx *c, V3 l, double soft)
{
    double th = 0.5 / rmax(soft, 0.5), cc, s, a;
    V3 t = v3norm(fabs(l.y) < 0.9 ? v3cross(l, v3(0, 1, 0)) : v3cross(l, v3(1, 0, 0))), b = v3cross(l, t);
    cc = 1 - rnd(c) * (1 - cos(th));
    s = sqrt(rmax(1 - cc * cc, 0));
    a = RM_TP * rnd(c);
    return v3norm(v3add(v3add(v3mul(t, s * cos(a)), v3mul(b, s * sin(a))), v3mul(l, cc)));
}

static double env_visibility(const Render *r, Ctx *c, V3 p, V3 n)
{
    V3 t = v3norm(fabs(n.y) < 0.9 ? v3cross(n, v3(0, 1, 0)) : v3cross(n, v3(1, 0, 0))), b = v3cross(n, t);
    double sc, occ = 0, wgt = 1, tot = 0;
    int i;
    if (r->sample > 0) {
        double r1 = rnd(c), a = RM_TP * rnd(c), rr = sqrt(r1);
        V3 d = v3norm(v3add(v3add(v3mul(t, rr * cos(a)), v3mul(b, rr * sin(a))), v3mul(n, sqrt(rmax(1 - r1, 0)))));
        return shadow(r, c, v3add(p, v3mul(n, 0.002)), d, 1e3, HARD);
    }
    sc = rmax(r->g.br, 0.01) * 0.35;
    for (i = 0; i < 6; i++) {
        double h = sc * (0.04 + 0.19 * i), d = map_d(c, v3add(p, v3mul(n, h)));
        if (r->w->floor) d = rmin(d, p.y + n.y * h);
        occ += rmax(h - d, 0) * wgt;
        tot += h * wgt;
        wgt *= 0.75;
    }
    return rclamp(1 - 1.3 * occ / tot, 0, 1);
}

static void shade(const Render *r, Ctx *c, V3 p, V3 n, V3 d, const GMat *m, int is_floor, double wd, V3 *acc, V3 *spec)
{
    const World *w = r->w;
    const GScene *g = &r->g;
    V3 alb = v3mul(m->col, m->dif), shc = is_floor ? v3(0, 0, 0) : v3(m->sheen[0], m->sheen[1], m->sheen[2]);
    double smax = rmax(shc.x, rmax(shc.y, shc.z)), eV = 0, occ = 1, e;
    int i;
    *acc = *spec = v3(0, 0, 0);
    if (!r->env && (r->quality & RQ_AO) && w->ao && g->br >= 0 && v3len(v3sub(p, g->bc)) < g->br + 0.8) occ = ambient_occlusion(r, c, p, n);
    for (i = 0; i < r->nl; i++) {
        V3 ld, lc = r->lcol[i], sh = v3(1, 1, 1);
        double tmax, dif;
        int from_env = r->lkind[i] > 1.5;
        if (r->lkind[i] < 0.5 || from_env) { ld = r->lpos[i]; tmax = 100; }
        else { V3 v = v3sub(r->lpos[i], p); tmax = v3len(v); ld = v3mul(v, 1 / tmax); }
        dif = v3dot(n, ld);
        if (dif <= 0) continue;
        if ((r->quality & RQ_SHADOWS) && r->lshadow[i]) {
            V3 sd = r->sample > 0 ? light_dir(c, ld, r->lsoft[i]) : ld;
            double k = r->sample > 0 ? HARD : r->lsoft[i];
            if (g->has_glass) sh = shadow_glass(r, c, p, sd, tmax, k);
            else dif *= shadow(r, c, p, sd, tmax, k);
        }
        *acc = v3add(*acc, v3mul(v3mulv(v3mulv(alb, lc), sh), dif));
        if ((r->quality & RQ_SPEC) && !is_floor && m->spec > 0 &&
            !(from_env && (m->opt[0] > 0 || m->opt[2] > 0 || m->metal > 0.5))) {
            V3 h = v3norm(v3sub(ld, d));
            double sp = pow(rmax(v3dot(n, h), 0), floor(m->shin)) * m->spec * dif;
            *spec = v3add(*spec, v3mul(v3mulv(lc, sh), sp));
        }
    }
    if (r->env) {
        double vis = wd > 0 && (r->quality & RQ_AO) && w->ao ? env_visibility(r, c, p, n) : 1;
        *acc = v3add(*acc, v3mul(v3mulv(v3add(v3mul(alb, 1 - smax * eV), v3mul(shc, eV)), env_irradiance(r, n)), vis));
        return;
    }
    e = (0.5 + n.y * 0.5) * occ;
    *acc = v3add(*acc, v3mulv(v3add(v3mul(alb, 1 - smax * eV), v3mul(shc, eV)),
                              v3add(v3mul(w->sky_light, e), v3mul(w->ambient, occ))));
}

static V3 tone_neutral(V3 col)
{
    const double start = 0.8 - 0.04, desat = 0.15, dd = 1 - start;
    double x = rmin(col.x, rmin(col.y, col.z)), offset = x < 0.08 ? x - 6.25 * x * x : 0.04, peak, np, gg;
    col = v3sub(col, v3(offset, offset, offset));
    peak = rmax(col.x, rmax(col.y, col.z));
    if (peak < start) return col;
    np = 1 - dd * dd / (peak + dd - start);
    col = v3mul(col, np / peak);
    gg = 1 - 1 / (desat * (peak - np) + 1);
    return v3lerp(col, v3(np, np, np), gg);
}

static V3 tone(const Render *r, V3 col)
{
    col = v3mul(col, pow(2, r->w->exposure));
    return r->w->tone_map ? tone_neutral(col) : col;
}

static unsigned pack1(double v, int gamma)
{
    double x = gamma ? sqrt(v) : v;
    int b = (int)floor(x * 255);
    return (unsigned)(b > 255 ? 255 : b);
}

static unsigned pack(V3 col, int gamma)
{
    return pack1(rclamp(col.x, 0, 1), gamma) << 16 | pack1(rclamp(col.y, 0, 1), gamma) << 8 | pack1(rclamp(col.z, 0, 1), gamma);
}

static V3 normal_at(const Ctx *c, V3 p, double en)
{
    V3 n = v3(0, 0, 0);
    int i;
    for (i = 0; i < 4; i++) {
        V3 e = v3(2.0 * (((i + 3) >> 1) & 1) - 1, 2.0 * ((i >> 1) & 1) - 1, 2.0 * (i & 1) - 1);
        n = v3add(n, v3mul(e, map_d(c, v3add(p, v3mul(e, en)))));
    }
    return v3norm(n);
}

static double confirm_hit(const Render *r, const Ctx *c, V3 ro, V3 d, double *t, double h, double eps)
{
    double tt = *t;
    int k;
    if (!r->g.inexact) return *t;
    for (k = 0; k < 64; k++) {
        if (h < eps * 0.02) return *t;
        tt += h;
        h = map_d(c, v3add(ro, v3mul(d, tt)));
        if (h > eps) { *t = tt; return 0; }
    }
    return *t;
}

static double march_objects(const Render *r, const Ctx *c, V3 ro, V3 d, double tmax)
{
    double t0, t1, t;
    int i;
    const GScene *g = &r->g;
    if (g->br < 0 || !hit_sphere(ro, d, g->bc, g->br, &t0, &t1)) return -1;
    tmax = rmin(t1, tmax);
    t = rmax(t0, 0);
    for (i = 0; i < MAXSTEP && t < tmax; i++) {
        double h = map_d(c, v3add(ro, v3mul(d, t))), eps = t / (r->focal * r->ht) + 0.000004;
        if (h < eps) {
            double th = confirm_hit(r, c, ro, d, &t, h, eps);
            if (th > 0) return th;
            continue;
        }
        t += h;
    }
    return -1;
}

static V3 sky(const Render *r, V3 d)
{
    double k = rclamp(1 - d.y, 0, 1);
    return v3add(r->w->sky_top, v3mul(v3sub(r->w->sky_horizon, r->w->sky_top), k));
}

static V3 floor_colour(const Render *r, V3 p, double t)
{
    const World *w = r->w;
    double cx = p.x * w->floor_scale, cz = p.z * w->floor_scale, s;
    if (w->floor_type == FLOOR_SOLID) return w->floor_a;
    if (w->floor_type == FLOOR_GRID) {
        double dx = fabs(rfract(cx + 0.5) - 0.5) / w->floor_scale, dz = fabs(rfract(cz + 0.5) - 0.5) / w->floor_scale;
        return rmin(dx, dz) < 0.012 + t * 0.0012 ? w->floor_b : w->floor_a;
    }
    s = floor(cx) + floor(cz);
    return s - 2 * floor(s / 2) > 0.5 ? w->floor_a : w->floor_b;
}

static V3 sky_lin(const Render *r, V3 d)
{
    V3 c;
    if (r->env) return env_lin(r, d, 0);
    c = sky(r, d);
    return v3mulv(c, c);
}

static V3 rnd_dir(Ctx *c)
{
    double z = rnd(c) * 2 - 1, a = rnd(c) * RM_TP, rr = sqrt(rmax(1 - z * z, 0));
    return v3(rr * cos(a), z, rr * sin(a));
}

static V3 roughen(const Render *r, Ctx *c, V3 d, V3 n, double rough, double s)
{
    V3 q;
    if (r->sample == 0 || rough <= 0) return d;
    q = v3norm(v3add(d, v3mul(rnd_dir(c), rough)));
    return v3dot(q, n) * s > 0 ? q : d;
}

static double march_inside(const Ctx *c, V3 ro, V3 d)
{
    double t = 0;
    int i;
    for (i = 0; i < MAXSTEP; i++) {
        double h = -map_d(c, v3add(ro, v3mul(d, t)));
        if (h < 0.0002) break;
        t += h;
    }
    return t;
}

/* come march_inside, con i materiali misti: thr assorbe tratto per tratto
 * col materiale mescolato di ogni punto */
static double march_inside_mix(const Ctx *c, V3 ro, V3 d, V3 *thr)
{
    double t = 0;
    int i;
    for (i = 0; i < MAXSTEP; i++) {
        V3 p = v3add(ro, v3mul(d, t));
        double h = -map_d(c, p);
        GMat m;
        if (h < 0.0002) break;
        map_m(c, p, &m);
        *thr = v3mulv(*thr, mix_absorb(&m, h));
        t += h;
    }
    return t;
}

static double march_scene(const Render *r, const Ctx *c, V3 ro, V3 d, int *on_floor)
{
    double tf = -1, t;
    if (r->w->floor && d.y < -0.001 && ro.y > 0) tf = ro.y / -d.y;
    t = march_objects(r, c, ro, d, tf >= 0 ? tf : 1e9);
    *on_floor = t < 0 && tf >= 0;
    return *on_floor ? tf : t;
}

static V3 reflect3(V3 d, V3 n) { return v3sub(d, v3mul(n, 2 * v3dot(n, d))); }

static V3 refract3(V3 i, V3 n, double eta)
{
    double ni = v3dot(n, i), k = 1 - eta * eta * (1 - ni * ni);
    if (k < 0) return v3(0, 0, 0);
    return v3sub(v3mul(i, eta), v3mul(n, eta * ni + sqrt(k)));
}

static unsigned render_ray(const Render *r, Ctx *c, V3 ro, V3 d)
{
    const World *w = r->w;
    int on_floor, b;
    double t = march_scene(r, c, ro, d, &on_floor), t_first, f, lod = 0;
    V3 col = v3(0, 0, 0), thr = v3(1, 1, 1);
    if (t < 0) {
        if (r->env) return w->env_background ? pack(tone(r, env_lin(r, d, 0)), 1) : pack(w->bg_color, 0);
        return pack(sky(r, d), 0);
    }
    t_first = t;
    for (b = 0; b < 16; b++) {
        V3 p = v3add(ro, v3mul(d, t)), n, tint = v3(1, 1, 1), dif, spec, rd;
        GMat m;
        double wr, tr, wt = 0, ior, rough, wd;
        int through;
        if (!on_floor) {
            n = normal_at(c, p, EPS_N);
            map_m(c, p, &m);
        } else {
            n = v3(0, 1, 0);
            mat_default(&m);
            m.col = floor_colour(r, p, t);
            m.spec = 0; m.shin = 1; m.dif = 1; m.sel = 0;
            m.opt[0] = w->floor_reflect; m.opt[1] = 0; m.opt[2] = 0; m.opt[3] = 1;
            m.metal = 0;
            m.sheen[0] = m.sheen[1] = m.sheen[2] = 0; m.sheen[3] = 0.5;
        }
        wr = rclamp(m.opt[0], 0, 1); tr = rclamp(m.opt[2], 0, 1);
        ior = rmax(m.opt[3], 1); rough = rclamp(m.opt[1], 0, 1);
        if (m.metal > 0.5) tint = m.col;
        else if (!on_floor && (wr > 0 || tr > 0)) {
            double f0 = (ior - 1) / (ior + 1), cc, c2, fr;
            f0 *= f0;
            cc = rclamp(1 + v3dot(d, n), 0, 1); c2 = cc * cc;
            fr = f0 + (1 - f0) * c2 * c2 * cc;
            wr += (1 - wr) * fr;
            wt = tr * (1 - wr);
        }
        wd = rmax(1 - wr - wt, 0);
        shade(r, c, p, n, d, &m, on_floor, wd, &dif, &spec);
        col = v3add(col, v3mulv(thr, v3add(v3mul(dif, wd), v3mulv(spec, tint))));
        if (wr + wt <= 0) break;
        rd = roughen(r, c, reflect3(d, n), n, rough, 1);
        through = wt > 0;
        if (wr > 0 && wt > 0) {
            if (r->sample > 0) {
                through = rnd(c) * (wr + wt) < wt;
                thr = v3mul(thr, wr + wt);
                if (!through) thr = v3mulv(thr, tint);
            } else {
                through = wt >= wr;
                if (through) { col = v3add(col, v3mul(v3mulv(v3mulv(thr, tint), sky_lin(r, rd)), wr)); thr = v3mul(thr, wt); }
                else thr = v3mul(v3mulv(thr, tint), wr);
            }
        } else thr = through ? v3mul(thr, wt) : v3mul(v3mulv(thr, tint), wr);
        if (through) {
            V3 q = v3sub(p, v3mul(n, rmax(map_d(c, p), 0) + 0.003));
            V3 id = roughen(r, c, refract3(d, n, 1 / ior), n, rough, -1);
            V3 absorb = v3(rmax(m.col.x, 0.001), rmax(m.col.y, 0.001), rmax(m.col.z, 0.001));
            int out = 0, k;
            for (k = 0; k < r->bounces + 2; k++) {
                double ti = r->mix ? march_inside_mix(c, q, id, &thr) : march_inside(c, q, id);
                V3 e = v3add(q, v3mul(id, ti)), ne = normal_at(c, e, EPS_N), od;
                if (!r->mix) thr = v3mulv(thr, vpow(absorb, ti));
                od = refract3(id, v3neg(ne), ior);
                if (v3dot(od, od) > 0) { ro = v3add(e, v3mul(ne, 0.003)); d = od; out = 1; break; }
                id = reflect3(id, ne);
                q = v3sub(e, v3mul(ne, 0.003));
            }
            if (!out) break;
        } else {
            ro = v3add(p, v3mul(n, 0.003));
            d = rd;
        }
        if (r->env && r->sample == 0) lod = rough * r->env_levels * 0.7;
        if (b >= r->bounces || rmax(thr.x, rmax(thr.y, thr.z)) < 0.02) {
            col = v3add(col, v3mulv(thr, r->env ? env_lin(r, d, lod) : sky_lin(r, d)));
            break;
        }
        t = march_scene(r, c, ro, d, &on_floor);
        if (t < 0) { col = v3add(col, v3mulv(thr, r->env ? env_lin(r, d, lod) : sky_lin(r, d))); break; }
    }
    t_first = rmin(t_first, 40);
    f = t_first * t_first * w->fog;
    f = f / (f + 1);
    col = v3add(col, v3mul(v3sub(w->fog_color, col), f));
    return pack(tone(r, col), 1);
}

unsigned render_pixel(const Render *r, Ctx *c, int x, int y)
{
    double u = (2 * (x + 0.5) - r->wd) / r->ht, v = (2 * (r->ht - 1 - y + 0.5) - r->ht) / r->ht;
    V3 rd = v3norm(v3add(v3add(v3mul(r->fw, r->focal), v3mul(r->rt, u)), v3mul(r->up, v)));
    c->rng = (unsigned)x * 1973u + (unsigned)(r->ht - 1 - y) * 9277u + r->sample * 26699u;
    rnd(c);
    return render_ray(r, c, r->ro, rd);
}

/* ---- la scena del benchmark (bench/benchmark.txt) */

static const char *bench_scene =
    "RayMarchStudio 1\n"
    "camera 2.55 2.55 3.15 0 0.87 0 2.6\n"
    "light dir -0.4472 0.7155 0.5367 1.3 1.15 0.95 1 1 8\n"
    "world 1 0.0015 1\n"
    "#gpu floor checker 0.2 0.2 0.21 0.08 0.08 0.09 1 0.25\n"
    "#gpu env studio \"Light tent\" 1 -30 0 0.82 0.82 0.83 1 \"\"\n"
    "#gpu tone neutral 0\n"
    "snap 0 0.25\n"
    "object -1 group union 0 3 \"Carved cube\"\n"
    "pos 0 0.9 0\nrot 0 12 0\nscale 1 1 1\nmaterial 0.7 0.03 0.02 1 64\n"
    "object 0 box union 0 3 \"Plastic cube\"\n"
    "pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 0.7 0.03 0.02 1 64\n"
    "#gpu material 0.08 0.04 0 1.5 0\nparam 1.6 1.6 1.6 0.16\n"
    "object 0 sphere subtract 0.15 3 \"Hollow\"\n"
    "pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 1 0.78 0.34 1 96\n"
    "#gpu material 1 0.06 0 1.5 1\nparam 0.64\n"
    "object 0 cylinder subtract 0.2 3 \"Hole Y\"\n"
    "pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 1 0.78 0.34 1 96\n"
    "#gpu material 1 0.06 0 1.5 1\nparam 0.42 2.2 0\n"
    "object 0 cylinder subtract 0.2 3 \"Hole Z\"\n"
    "pos 0 0 0\nrot 90 0 0\nscale 1 1 1\nmaterial 0.92 0.92 0.94 1 128\n"
    "#gpu material 1 0 0 1.5 1\nparam 0.42 2.2 0\n"
    "object 0 cylinder subtract 0.2 3 \"Hole X\"\n"
    "pos 0 0 0\nrot 0 0 90\nscale 1 1 1\nmaterial 0.1 0.3 0.85 1 64\n"
    "#gpu material 0.1 0.05 0 1.5 0\nparam 0.42 2.2 0\n"
#define CORNER(n, p) "object 0 sphere union 0.1 3 \"Corner " n "\"\npos " p "\nrot 0 0 0\nscale 1 1 1\n" \
                     "material 0.22 0.3 0.38 1 128\n#gpu material 0 0 1 1.5 0\nparam 0.16\n"
    CORNER("1", "-0.74 0.74 -0.74") CORNER("2", "0.74 0.74 -0.74") CORNER("3", "-0.74 0.74 0.74") CORNER("4", "0.74 0.74 0.74")
    CORNER("5", "-0.74 -0.74 -0.74") CORNER("6", "0.74 -0.74 -0.74") CORNER("7", "-0.74 -0.74 0.74") CORNER("8", "0.74 -0.74 0.74")
    "object -1 sphere union 0 3 \"Glass core\"\n"
    "pos 0 0.9 0\nrot 0 0 0\nscale 1 1 1\nmaterial 0.85 0.93 1 1 128\n"
    "#gpu material 0 0 1 1.5 0\nparam 0.46\n";

/* la scena del benchmark: il calcolo di sempre, senza i materiali misti */
Render *bench_setup(int w, int h, char *err, int errlen)
{
    Render *r = bench_setup_text(bench_scene, w, h, err, errlen);
    if (r) r->mix = 0;
    return r;
}

Render *bench_setup_text(const char *text, int w, int h, char *err, int errlen)
{
    Render *r = (Render *)xalloc(sizeof *r);
    const Scene *s = &r->s;
    int i;
    if (scene_read(&r->s, text, err, errlen)) return NULL;
    if (gscene_build(&r->g, &r->s, err, errlen)) return NULL;
    r->w = &s->world;
    r->wd = w; r->ht = h;
    r->fw = v3norm(v3sub(s->cam_target, s->cam_pos));
    r->rt = v3norm(v3(-r->fw.z, 0, r->fw.x));
    r->up = v3cross(r->rt, r->fw);
    r->ro = s->cam_pos;
    r->focal = s->cam_focal;
    r->quality = RQ_SHADOWS | RQ_AO | RQ_SPEC;
    r->bounces = 6;
    r->sample = 0;
    for (i = 0; i < s->nlight && r->nl < MAX_LIGHTS; i++) {
        const Light *l = &s->light[i];
        if (!l->on) continue;
        r->lpos[r->nl] = l->dir ? v3norm(l->pos) : l->pos;
        r->lkind[r->nl] = l->dir ? 0 : 1;
        r->lcol[r->nl] = v3mul(l->color, l->intensity);
        r->lshadow[r->nl] = l->shadow;
        r->lsoft[r->nl] = l->softness;
        r->nl++;
    }
    r->env = envmap_of(&s->world);
    if (r->env) {
        const World *wd = &s->world;
        double a = wd->env_rotation * RM_PI / 180;
        int k;
        r->env_cos = cos(a); r->env_sin = sin(a);
        r->env_gain = wd->env_intensity;
        r->env_levels = r->env->nlev - 1;
        envmap_irradiance(r->env, wd->env_rotation, wd->env_intensity, wd->env_lights, r->sh);
        /* le sorgenti forti dell'ambiente come luci (tipo 2), dopo quelle della scena */
        if (wd->env_lights)
            for (k = 0; k < r->env->nlights && r->nl < MAX_LIGHTS; k++) {
                r->lpos[r->nl] = env_rot_y(r->env->ldir[k], wd->env_rotation);
                r->lkind[r->nl] = 2;
                r->lcol[r->nl] = v3mul(r->env->lcol[k], wd->env_intensity);
                r->lshadow[r->nl] = 1;
                r->lsoft[r->nl] = rclamp(0.5 / rmax(r->env->lrad[k], 1e-3), 2, 64);
                r->nl++;
            }
    }
    r->mix = 1;     /* una scena caricata: i materiali misti anche dentro il vetro */
    return r;
}
