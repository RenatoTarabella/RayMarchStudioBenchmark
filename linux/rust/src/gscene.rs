// La scena preparata per il calcolo (gscene.h, gscene.c, shapes.c, parti
// di wire.c): matrici, valori derivati, ingombri, materiali e il programma
// a pila di RISC OS (alberi di unioni con I_SKIP).
use crate::scene::*;
use crate::vec::*;

pub const I_PRIM: i32 = 0;
pub const I_OP: i32 = 1;
pub const I_MOD: i32 = 2;
pub const I_MIN: i32 = 3;
pub const I_SKIP: i32 = 5;
pub const I_LAZY: i32 = 6;

#[derive(Clone, Copy, Default)]
pub struct Ins {
    pub code: i32,
    pub obj: i32,
    pub cull: i32,
    pub n: i32,
}

/* il materiale come lo usa il renderer, mescolato nei raccordi */
#[derive(Clone, Copy, Default)]
pub struct GMat {
    pub col: V3,
    pub spec: f64,
    pub shin: f64,
    pub dif: f64,
    pub sel: f64,
    pub opt: [f64; 4],      /* riflesso, ruvidita', trasparenza, ior */
    pub metal: f64,
    pub sheen: [f64; 4],    /* colore * quanto, ruvidita' delle fibre */
}

impl GMat {
    pub fn default_mat() -> GMat {
        GMat { col: v3(0.75, 0.3, 0.15), spec: 1.0, shin: 24.0, dif: 1.0, sel: 0.0,
               opt: [0.0, 0.0, 0.0, 1.5], metal: 0.0, sheen: [0.0, 0.0, 0.0, 0.5] }
    }

    /* a + (b - a) * (1 - wa): wa = peso di a */
    #[inline(always)]
    pub fn mix(a: &GMat, b: &GMat, wa: f64) -> GMat {
        let mut r = GMat::default();
        r.col = lerp(b.col, a.col, wa);
        r.spec = mix(b.spec, a.spec, wa);
        r.shin = mix(b.shin, a.shin, wa);
        r.dif = mix(b.dif, a.dif, wa);
        r.sel = mix(b.sel, a.sel, wa);
        for i in 0..4 { r.opt[i] = mix(b.opt[i], a.opt[i], wa); }
        r.metal = mix(b.metal, a.metal, wa);
        for i in 0..4 { r.sheen[i] = mix(b.sheen[i], a.sheen[i], wa); }
        r
    }
}

#[derive(Clone, Copy, Default)]
pub struct GObj {
    pub typ: i32,
    pub op: i32,
    pub inv: M34,
    pub p0: f64,            /* il raggio della sfera */
    pub c: [f64; 4],        /* i valori derivati (prim_prepare) */
    pub dscale: f64,
    pub k: f64,
    pub round: f64,
    pub shell: f64,
    pub mat: GMat,
    pub bc: V3,
    pub br: f64,            /* < 0: niente */
}

/* i dati di preparazione che il caso caldo non usa */
#[derive(Clone, Copy, Default)]
struct Prep {
    world: M34,
    baked: bool,
}

pub struct GScene {
    pub obj: Vec<GObj>,
    pub prog: Vec<Ins>,
    pub bound: Vec<f64>,    /* le sfere degli I_SKIP, 4 l'una */
    pub max_stack: usize,
    pub bc: V3,
    pub br: f64,            /* < 0: niente da rendere */
    pub inexact: bool,
    pub cavity: bool,
    pub has_glass: bool,
}

// ---- i valori derivati e gli ingombri (shapes.c)

fn prim_prepare(t: i32, p: &[f64; 10], f: &mut GObj) {
    f.c = [0.0; 4];
    match t {
        T_BOX => {
            let r = rmin(p[3], rmin(p[0] / 2.0, rmin(p[1] / 2.0, p[2] / 2.0)));
            f.c = [p[0] / 2.0 - r, p[1] / 2.0 - r, p[2] / 2.0 - r, r];
        }
        T_CYLINDER => {
            let b = rmin(p[2], rmin(p[0], p[1] / 2.0));
            f.c = [p[0] - b, p[1] / 2.0 - b, b, 0.0];
        }
        _ => {}
    }
    f.p0 = p[0];
}

fn prim_bound(t: i32, p: &[f64; 10]) -> f64 {
    match t {
        T_SPHERE => p[0],
        T_BOX => (p[0] / 2.0 * (p[0] / 2.0) + p[1] / 2.0 * (p[1] / 2.0) + p[2] / 2.0 * (p[2] / 2.0)).sqrt(),
        T_CYLINDER => len2(p[0], p[1] / 2.0),
        _ => unreachable!(),
    }
}

// ---- il wireframe delle forme (wire.c): solo i punti, per extend_cutters

const SEG: i32 = 24;
fn rad(d: f64) -> f64 { d * PI / 180.0 }

fn arc(b: &mut Vec<V3>, c: V3, u: V3, v: V3, a0: i32, a1: i32) {
    let mut a = a0;
    while a <= a1 {
        b.push(c + (u * rad(a as f64).cos() + v * rad(a as f64).sin()));
        a += 360 / SEG;
    }
}

fn hcircle(b: &mut Vec<V3>, y: f64, r: f64) { arc(b, v3(0.0, y, 0.0), v3(r, 0.0, 0.0), v3(0.0, 0.0, r), 0, 360); }

fn wire_build(o: &Obj, b: &mut Vec<V3>) {
    let p = &o.par;
    match o.typ {
        T_SPHERE => {
            arc(b, v3(0.0, 0.0, 0.0), v3(p[0], 0.0, 0.0), v3(0.0, p[0], 0.0), 0, 360);
            arc(b, v3(0.0, 0.0, 0.0), v3(0.0, p[0], 0.0), v3(0.0, 0.0, p[0]), 0, 360);
            hcircle(b, 0.0, p[0]);
        }
        T_BOX => {
            let (hx, hy, hz) = (p[0] / 2.0, p[1] / 2.0, p[2] / 2.0);
            for i in 0..4 {
                let sx = if i & 1 != 0 { hx } else { -hx };
                let sz = if i & 2 != 0 { hz } else { -hz };
                b.push(v3(sx, -hy, sz));
                b.push(v3(sx, hy, sz));
            }
            for s in [-1.0, 1.0] {
                let y = s * hy;
                b.extend_from_slice(&[v3(-hx, y, -hz), v3(hx, y, -hz), v3(hx, y, hz), v3(-hx, y, hz), v3(-hx, y, -hz)]);
            }
        }
        T_CYLINDER => {
            let (y0, y1, r) = (-p[1] / 2.0, p[1] / 2.0, p[0]);
            hcircle(b, y0, r);
            hcircle(b, y1, r);
            b.extend_from_slice(&[v3(r, y0, 0.0), v3(r, y1, 0.0), v3(-r, y0, 0.0), v3(-r, y1, 0.0),
                                  v3(0.0, y0, r), v3(0.0, y1, r), v3(0.0, y0, -r), v3(0.0, y1, -r)]);
        }
        _ => {}
    }
}

fn visible(s: &Scene, o: usize) -> bool {
    let ob = &s.objs[o];
    if ob.flags & OF_RENDER == 0 { return false; }
    if !ob.is_container() { return true; }
    ob.kids.iter().any(|&k| visible(s, k))
}

fn merge(c: &mut V3, r: &mut f64, c2: V3, r2: f64) {
    if *r < 0.0 { *c = c2; *r = r2; return; }
    let d = len(c2 - *c);
    if d + r2 <= *r { return; }
    if d + *r <= r2 { *c = c2; *r = r2; return; }
    let nr = (d + *r + r2) / 2.0;
    *c = *c + (c2 - *c) * ((nr - *r) / d);
    *r = nr;
}

/* il valore come lo vede RISC OS (1/65536): le scelte dell'albero */
fn fxv(v: f64) -> f64 { round_even(v * 65536.0) }

fn nonuniform(w: &M34) -> bool {
    let (a, b, c) = (w.col_len(0), w.col_len(1), w.col_len(2));
    let m = rmax(a, rmax(b, c));
    (a - b).abs() > 1e-9 * m || (b - c).abs() > 1e-9 * m
}

struct Builder<'a> {
    s: &'a Scene,
    obj: Vec<GObj>,
    prep: Vec<Prep>,
    prog: Vec<Ins>,
    bound: Vec<f64>,
}

impl<'a> Builder<'a> {
    fn list_bound(&self, list: &[usize]) -> (V3, f64) {
        let (mut c, mut r) = (V3::default(), -1.0);
        let mut first = true;
        for &o in list {
            let ob = &self.s.objs[o];
            let f = &self.obj[ob.index];
            if ob.flags & OF_RENDER == 0 || f.br < 0.0 { continue; }
            if first || ob.op == OP_UNION { merge(&mut c, &mut r, f.bc, f.br); }
            first = false;
        }
        (c, r)
    }

    /* un box o un cilindro scalato non uniforme lungo i propri assi: la
     * stessa forma con le misure scalate (distanza esatta) */
    fn try_bake(o: &Obj, f: &mut GObj, pr: &mut Prep) -> bool {
        if o.typ != T_BOX && o.typ != T_CYLINDER { return false; }
        let w = pr.world.m;
        let col = [v3(w[0], w[4], w[8]), v3(w[1], w[5], w[9]), v3(w[2], w[6], w[10])];
        let (c0, c1, c2) = (len(col[0]), len(col[1]), len(col[2]));
        let cmax = rmax(c0, rmax(c1, c2));
        if cmax <= 0.0 || rmin(c0, rmin(c1, c2)) <= 0.0 { return false; }
        let tol = 1e-9 * cmax;
        if (c0 - c1).abs() <= tol && (c1 - c2).abs() <= tol { return false; }
        if dot(col[0], col[1]).abs() > tol * cmax || dot(col[1], col[2]).abs() > tol * cmax || dot(col[0], col[2]).abs() > tol * cmax {
            return false;
        }
        if o.typ == T_CYLINDER && (c0 - c2).abs() > tol { return false; }
        let u = M34 { m: [w[0] / c0, w[1] / c1, w[2] / c2, w[3], w[4] / c0, w[5] / c1, w[6] / c2, w[7], w[8] / c0, w[9] / c1, w[10] / c2, w[11]] };
        let Some(inv) = M34::inverse(&u) else { return false };
        let cmin = rmin(c0, rmin(c1, c2));
        let mut p = o.par;
        if o.typ == T_BOX { p[0] *= c0; p[1] *= c1; p[2] *= c2; p[3] *= cmin; } else { p[0] *= c0; p[1] *= c1; p[2] *= cmin; }
        f.inv = inv;
        f.dscale = 1.0;
        prim_prepare(o.typ, &p, f);
        pr.baked = true;
        true
    }

    fn update_list(&mut self, list: &[usize], pw: &M34, pscale: f64) {
        for &o in list {
            let ob = &self.s.objs[o];
            let idx = ob.index;
            let smin = rmin(ob.scl.x.abs(), rmin(ob.scl.y.abs(), ob.scl.z.abs()));
            let l = M34::compose(ob.pos, ob.rot, ob.scl);
            let mut f = self.obj[idx];
            let mut pr = Prep { world: M34::mul(pw, &l), baked: false };
            f.inv = M34::inverse(&pr.world).unwrap_or(IDENTITY);
            f.dscale = pscale * smin;
            f.br = -1.0;
            if !ob.is_container() {
                prim_prepare(ob.typ, &ob.par, &mut f);
                Self::try_bake(ob, &mut f, &mut pr);
                f.bc = pr.world.trans();
                f.br = prim_bound(ob.typ, &ob.par) * pr.world.max_scale();
                self.obj[idx] = f;
                self.prep[idx] = pr;
            } else {
                self.obj[idx] = f;
                self.prep[idx] = pr;
                let kids = ob.kids.clone();
                self.update_list(&kids, &pr.world, f.dscale);
                let (c, r) = self.list_bound(&kids);
                self.obj[idx].bc = c;
                self.obj[idx].br = r;
            }
            let f = &mut self.obj[idx];
            if f.br >= 0.0 { f.br += ob.k + ob.round + ob.shell; }
        }
    }

    /* Un box o un cilindro che scava o interseca: le facce che stanno tutte
     * oltre l'ingombro dei fratelli prima si spingono lontanissimo */
    fn extend_cutters(&mut self, list: &[usize]) {
        let s = self.s;
        let mut loc = Vec::new();
        for (i, &o) in list.iter().enumerate() {
            let ob = &s.objs[o];
            if ob.is_container() { let kids = ob.kids.clone(); self.extend_cutters(&kids); continue; }
            if i == 0 || ob.op == OP_UNION || (ob.typ != T_BOX && ob.typ != T_CYLINDER) || !visible(s, o) { continue; }
            let mut f = self.obj[ob.index];
            let mut lo = v3(1e300, 1e300, 1e300);
            let mut hi = v3(-1e300, -1e300, -1e300);
            let mut margin = ob.k;
            let mut n = 0;
            for &sib in &list[..i] {
                if !visible(s, sib) { continue; }
                let mut all = Vec::new();
                s.walk(sib, &mut all);
                for &d in &all {
                    let dd = &s.objs[d];
                    margin = rmax(margin, ob.k + dd.k + dd.round.abs() + dd.shell);
                    if dd.is_container() || !visible(s, d) { continue; }
                    let w = s.obj_world(d);
                    loc.clear();
                    wire_build(dd, &mut loc);
                    for &q in &loc {
                        let pt = f.inv.point(w.point(q));
                        lo = v3(rmin(lo.x, pt.x), rmin(lo.y, pt.y), rmin(lo.z, pt.z));
                        hi = v3(rmax(hi.x, pt.x), rmax(hi.y, pt.y), rmax(hi.z, pt.z));
                        n += 1;
                    }
                }
            }
            if n == 0 { continue; }
            let sc = f.inv.max_scale();
            let size = len(hi - lo);
            let m = margin * sc + 0.01 * size + 1e-6;
            let axes = if ob.typ == T_BOX { 3 } else { 1 };
            let mut shift = [0.0; 3];
            let far = 100.0 * rmax(size, 1e-3);
            let mut changed = false;
            for a in 0..axes {
                let k = if ob.typ == T_BOX { a } else { 1 };
                let ci = k;
                let r = if ob.typ == T_BOX { f.c[3] } else { f.c[2] };
                let h = f.c[ci] + r;
                let (mut f_lo, mut f_hi) = (-h, h);
                if hi.get(k) + m < f_hi { f_hi = far + h; }
                if lo.get(k) - m > f_lo { f_lo = -far - h; }
                if f_hi == h && f_lo == -h { continue; }
                shift[k] = (f_lo + f_hi) / 2.0;
                f.c[ci] = (f_hi - f_lo) / 2.0 - r;
                changed = true;
            }
            if !changed { continue; }
            let t = M34 { m: [1.0, 0.0, 0.0, -shift[0], 0.0, 1.0, 0.0, -shift[1], 0.0, 0.0, 1.0, -shift[2]] };
            f.inv = M34::mul(&t, &f.inv);
            f.br = 1e9;
            self.obj[ob.index] = f;
        }
    }

    // ---- il programma (compile_list, compile_obj, emit_union)

    fn emit(&mut self, code: i32, obj: usize, cull: i32, n: i32) -> usize {
        self.prog.push(Ins { code, obj: obj as i32, cull, n });
        self.prog.len() - 1
    }

    fn set_n(&mut self, at: usize) { self.prog[at].n = (self.prog.len() - at - 1) as i32; }

    fn compile_obj(&mut self, o: usize, on: bool) {
        let ob = &self.s.objs[o];
        if ob.typ == T_GROUP {
            let kids = ob.kids.clone();
            self.compile_list(&kids, on);
        } else {
            self.emit(I_PRIM, ob.index, if on { 0 } else { 1 }, ob.typ);
            return;
        }
        if ob.round != 0.0 || ob.shell != 0.0 { self.emit(I_MOD, ob.index, 0, 0); }
    }

    fn bc_of(&self, o: usize) -> V3 { self.obj[self.s.objs[o].index].bc }

    fn emit_union(&mut self, list: &mut [usize], at: usize, m: usize) {
        if m <= 2 {
            self.compile_obj(list[at], false);
            if m == 2 {
                self.compile_obj(list[at + 1], false);
                self.emit(I_MIN, self.s.objs[list[at + 1]].index, 0, 0);
            }
            return;
        }
        let si = self.emit(I_SKIP, self.bound.len() / 4, 0, 0);
        let (mut c, mut r) = (V3::default(), -1.0);
        let (mut lo, mut hi) = ([0.0; 3], [0.0; 3]);
        for k in 0..m {
            let f = &self.obj[self.s.objs[list[at + k]].index];
            merge(&mut c, &mut r, f.bc, f.br);
            for j in 0..3 {
                let q = fxv(f.bc.get(j));
                if k == 0 || q < lo[j] { lo[j] = q; }
                if k == 0 || q > hi[j] { hi[j] = q; }
            }
        }
        self.bound.extend_from_slice(&[c.x, c.y, c.z, r]);
        let mut ax = 0;
        if hi[1] - lo[1] > hi[ax] - lo[ax] { ax = 1; }
        if hi[2] - lo[2] > hi[ax] - lo[ax] { ax = 2; }
        for k in 1..m {
            let v = list[at + k];
            let key = fxv(self.bc_of(v).get(ax));
            let mut j = k;
            while j > 0 && fxv(self.bc_of(list[at + j - 1]).get(ax)) > key {
                list[at + j] = list[at + j - 1];
                j -= 1;
            }
            list[at + j] = v;
        }
        let h = m / 2;
        self.emit_union(list, at, h);
        self.emit_union(list, at + h, m - h);
        self.emit(I_MIN, self.s.objs[list[at + h]].index, 0, 0);
        self.set_n(si);
    }

    fn compile_list(&mut self, list: &[usize], neg: bool) -> i32 {
        let s = self.s;
        let (mut n, mut i) = (0, 0);
        while i < list.len() {
            let o = list[i];
            let ob = &s.objs[o];
            if !visible(s, o) { i += 1; continue; }
            if !neg && (n == 0 || (ob.op == OP_UNION && ob.k == 0.0)) {
                let mut run = Vec::new();
                let first = o;
                let mut ok = true;
                while i < list.len() {
                    let q = list[i];
                    let qo = &s.objs[q];
                    if visible(s, q) {
                        if !run.is_empty() && (qo.op != OP_UNION || qo.k != 0.0) { break; }
                        ok &= self.obj[qo.index].br >= 0.0;
                        run.push(q);
                    }
                    i += 1;
                }
                let m = run.len();
                if ok { self.emit_union(&mut run, 0, m); } else {
                    for k in 0..m {
                        self.compile_obj(run[k], false);
                        if k > 0 { self.emit(I_MIN, s.objs[run[k]].index, 0, 0); }
                    }
                }
                if n > 0 { self.emit(I_MIN, s.objs[first].index, 0, 0); }
                n += 1;
                continue;
            }
            let on = neg ^ (n > 0 && ob.op == OP_SUB);
            let mut li = None;
            if n > 0 && ob.op == OP_SUB && self.obj[ob.index].br >= 0.0 { li = Some(self.emit(I_LAZY, ob.index, 0, 0)); }
            self.compile_obj(o, on);
            if n > 0 { self.emit(if ob.op == OP_UNION && ob.k == 0.0 { I_MIN } else { I_OP }, ob.index, 0, 0); }
            if let Some(li) = li { self.set_n(li); }
            n += 1;
            i += 1;
        }
        n
    }

    // ---- i dati di ogni oggetto (fill)

    fn fill(&mut self, o: usize) -> Result<(), String> {
        let ob = &self.s.objs[o];
        let m = &ob.mat;
        let sh = clamp(m.sheen, 0.0, 1.0);
        if sh > 0.0 && !ob.is_container() { return Err("sheen is not supported by the Rust port".into()); }
        let f = &mut self.obj[ob.index];
        f.typ = ob.typ;
        f.op = ob.op;
        f.k = ob.k;
        f.round = ob.round;
        f.shell = ob.shell;
        f.mat = GMat {
            col: m.color, spec: m.specular, shin: m.shininess, dif: m.diffuse, sel: 0.0,
            opt: [m.reflect, m.roughness, m.transparency, m.ior],
            metal: if m.metal { 1.0 } else { 0.0 },
            sheen: [m.sheen_color.x * sh, m.sheen_color.y * sh, m.sheen_color.z * sh, clamp(m.sheen_rough, 0.07, 1.0)],
        };
        Ok(())
    }
}

/* i figli delle primitive salgono di un livello (Hierarchy.Flatten, lift) */
fn lift(s: &mut Scene, owner: Option<usize>) {
    let mut list = match owner { None => std::mem::take(&mut s.roots), Some(o) => std::mem::take(&mut s.objs[o].kids) };
    let mut i = 0;
    while i < list.len() {
        let o = list[i];
        if s.objs[o].is_container() { lift(s, Some(o)); i += 1; continue; }
        if s.objs[o].kids.is_empty() { i += 1; continue; }
        let m = M34::compose(s.objs[o].pos, s.objs[o].rot, s.objs[o].scl);
        let kids = std::mem::take(&mut s.objs[o].kids);
        let mut at = i + 1;
        for c in kids {
            let co = &mut s.objs[c];
            let l = M34::compose(co.pos, co.rot, co.scl);
            let (p, r, sc) = M34::mul(&m, &l).decompose();
            co.pos = p; co.rot = r; co.scl = sc;
            co.parent = owner;
            list.insert(at, c);
            at += 1;
        }
        i += 1;
    }
    match owner { None => s.roots = list, Some(o) => s.objs[o].kids = list }
}

impl GScene {
    /* la scena del file diventa quella del calcolo (la scena viene modificata) */
    pub fn build(s: &mut Scene) -> Result<GScene, String> {
        let all = s.walk_all();
        for &o in &all {
            if s.objs[o].inherit_mat {
                let mut x = o;
                let mut n = 0;
                while s.objs[x].inherit_mat && n < 64 {
                    match s.objs[x].parent { Some(p) => x = p, None => break }
                    n += 1;
                }
                s.objs[o].mat = s.objs[x].mat;
            }
        }
        for &o in &all { s.objs[o].inherit_mat = false; }
        lift(s, None);
        let all = s.walk_all();
        for (i, &o) in all.iter().enumerate() { s.objs[o].index = i; }
        let s: &Scene = s;
        let mut b = Builder { s, obj: vec![GObj::default(); all.len() + 1], prep: vec![Prep::default(); all.len() + 1], prog: Vec::new(), bound: Vec::new() };
        let roots = s.roots.clone();
        b.update_list(&roots, &IDENTITY, 1.0);
        b.extend_cutters(&roots);
        b.compile_list(&roots, false);
        let (bc, br) = b.list_bound(&roots);
        for &o in &all { b.fill(o)?; }
        let (mut has_glass, mut inexact, mut cavity) = (false, false, false);
        for &o in &all {
            let ob = &s.objs[o];
            let f = &b.prep[ob.index];
            if ob.mat.transparency > 0.0 && !ob.mat.metal { has_glass = true; }
            if !ob.is_container() && !f.baked && nonuniform(&f.world) { inexact = true; }
            if ob.is_container() && ob.shell > 0.0 && ob.kids.iter().filter(|&&k| visible(s, k)).count() > 1 { cavity = true; }
        }
        if b.bound.is_empty() { b.bound.extend_from_slice(&[0.0; 4]); }
        /* profondita' massima della pila dei valori: l'interprete la usa senza controlli */
        let (mut sp, mut max_stack) = (0i32, 0i32);
        for ins in &b.prog {
            if ins.code == I_PRIM || ins.code == I_SKIP { sp += 1; } else if ins.code == I_MIN || ins.code == I_OP { sp -= 1; }
            max_stack = max_stack.max(sp);
        }
        if max_stack > 128 { return Err("scene too deep".into()); }
        /* ogni istruzione indica un oggetto o una sfera che esiste: anche questo lo usa l'interprete senza controlli */
        for ins in &b.prog {
            let ok = if ins.code == I_SKIP { (ins.obj as usize + 1) * 4 <= b.bound.len() } else { (ins.obj as usize) < b.obj.len() };
            if !ok || ins.obj < 0 { return Err("bad program".into()); }
        }
        Ok(GScene { obj: b.obj, prog: b.prog, bound: b.bound, max_stack: max_stack as usize, bc, br, inexact, cavity, has_glass })
    }
}
