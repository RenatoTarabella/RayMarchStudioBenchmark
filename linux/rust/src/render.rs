// Il rendering (render.c): raymarching nella sfera d'ingombro, pavimento
// esatto, luci con ombra morbida, speculare, ambiente con l'occlusione,
// nebbia, riflessi e vetro, tone mapping. Solo la vista finale con un
// campione per pixel, come nel benchmark (niente vista solida, niente sheen).
use crate::envmap::*;
use crate::eval::*;
use crate::gscene::*;
use crate::scene::*;
use crate::vec::*;

const EPS_N: f64 = 0.002;
const HARD: f64 = 60.0;
const MAXSTEP: usize = 256;
const RQ_SHADOWS: i32 = 1;
const RQ_AO: i32 = 2;
const RQ_SPEC: i32 = 4;
const RQ_ALL: i32 = 7;

pub struct Render<'a> {
    pub s: &'a Scene,
    pub g: &'a GScene,
    pub w: usize,
    pub h: usize,
    ro: V3,
    fw: V3,
    rt: V3,
    up: V3,
    focal: f64,
    quality: i32,
    bounces: usize,
    sample: u32,
    nl: usize,
    lpos: [V3; MAX_LIGHTS],
    lcol: [V3; MAX_LIGHTS],
    lkind: [f64; MAX_LIGHTS],
    lshadow: [bool; MAX_LIGHTS],
    lsoft: [f64; MAX_LIGHTS],
    env: Option<EnvMap>,
    env_cos: f64,
    env_sin: f64,
    env_gain: f64,
    env_levels: f64,
    sh: [V3; 9],
    /* i materiali misti anche dentro il vetro (le scene caricate da file; la
     * scena del benchmark resta col calcolo di RISC OS e della versione SGI) */
    pub mix: bool,
}

/* il raggio o + d*t e la sfera (c, r): t d'entrata e d'uscita */
#[inline(always)]
fn hit_sphere(o: V3, d: V3, c: V3, r: f64) -> Option<(f64, f64)> {
    let oc = o - c;
    let b = dot(oc, d);
    let q = len(oc - d * b);
    if q >= r { return None; }
    let hh = ((r - q) * (r + q)).sqrt();
    let (t0, t1) = (-b - hh, -b + hh);
    if t1 > 0.0 { Some((t0, t1)) } else { None }
}

/* numeri a caso (PCG) */
#[inline(always)]
fn rnd(c: &mut Ctx) -> f64 {
    c.rng = c.rng.wrapping_mul(747796405).wrapping_add(2891336453);
    let w = ((c.rng >> ((c.rng >> 28) + 4)) ^ c.rng).wrapping_mul(277803737);
    ((w >> 22) ^ w) as f64 / 4294967296.0
}

fn vpow(a: V3, e: f64) -> V3 { v3(a.x.powf(e), a.y.powf(e), a.z.powf(e)) }

/* materiali misti: quanto passa in un tratto lungo l del materiale m (il
 * colore per la trasparenza: dove il vetro sfuma nell'opaco assorbe di piu') */
fn mix_absorb(m: &GMat, l: f64) -> V3 {
    let a = clamp(m.opt[2], 0.0, 1.0);
    vpow(v3(rmax(m.col.x * a, 0.001), rmax(m.col.y * a, 0.001), rmax(m.col.z * a, 0.001)), l)
}

fn reflect(d: V3, n: V3) -> V3 { d - n * (2.0 * dot(n, d)) }

/* GLSL refract: (0,0,0) nella riflessione totale */
fn refract(i: V3, n: V3, eta: f64) -> V3 {
    let ni = dot(n, i);
    let k = 1.0 - eta * eta * (1.0 - ni * ni);
    if k < 0.0 { return v3(0.0, 0.0, 0.0); }
    i * eta - n * (eta * ni + k.sqrt())
}

fn normal_at(c: &Ctx, p: V3, en: f64) -> V3 {
    let mut n = v3(0.0, 0.0, 0.0);
    for i in 0..4i32 {
        let e = v3(2.0 * (((i + 3) >> 1) & 1) as f64 - 1.0, 2.0 * ((i >> 1) & 1) as f64 - 1.0, 2.0 * (i & 1) as f64 - 1.0);
        n = n + e * c.map_d(p + e * en);
    }
    norm(n)
}

fn march_inside(c: &Ctx, ro: V3, d: V3) -> f64 {
    let mut t = 0.0;
    for _ in 0..MAXSTEP {
        let h = -c.map_d(ro + d * t);
        if h < 0.0002 { break; }
        t += h;
    }
    t
}

/* come march_inside, con i materiali misti: thr assorbe tratto per tratto
 * col materiale mescolato di ogni punto */
fn march_inside_mix(c: &mut Ctx, ro: V3, d: V3, thr: &mut V3) -> f64 {
    let mut t = 0.0;
    for _ in 0..MAXSTEP {
        let p = ro + d * t;
        let h = -c.map_d(p);
        if h < 0.0002 { break; }
        let m = c.map_m(p);
        *thr = *thr * mix_absorb(&m, h);
        t += h;
    }
    t
}

fn tone_neutral(col: V3) -> V3 {
    let (start, desat) = (0.8 - 0.04, 0.15);
    let dd = 1.0 - start;
    let x = rmin(col.x, rmin(col.y, col.z));
    let offset = if x < 0.08 { x - 6.25 * x * x } else { 0.04 };
    let col = col - v3(offset, offset, offset);
    let peak = rmax(col.x, rmax(col.y, col.z));
    if peak < start { return col; }
    let np = 1.0 - dd * dd / (peak + dd - start);
    let col = col * (np / peak);
    let gg = 1.0 - 1.0 / (desat * (peak - np) + 1.0);
    lerp(col, v3(np, np, np), gg)
}

fn pack(col: V3, gamma: bool) -> u32 {
    let b = |v: f64| {
        let x = if gamma { v.sqrt() } else { v };
        let b = (x * 255.0).floor() as i32;
        (if b > 255 { 255 } else { b }) as u32
    };
    b(clamp(col.x, 0.0, 1.0)) << 16 | b(clamp(col.y, 0.0, 1.0)) << 8 | b(clamp(col.z, 0.0, 1.0))
}

impl<'a> Render<'a> {
    pub fn new(s: &'a Scene, g: &'a GScene, w: usize, h: usize) -> Render<'a> {
        let fw = norm(s.cam.target - s.cam.pos);
        let rt = norm(v3(-fw.z, 0.0, fw.x));
        let mut r = Render {
            s, g, w, h, ro: s.cam.pos, fw, rt, up: cross(rt, fw), focal: s.cam.focal,
            quality: RQ_ALL, bounces: 6, sample: 0, nl: 0,
            lpos: [V3::default(); MAX_LIGHTS], lcol: [V3::default(); MAX_LIGHTS], lkind: [0.0; MAX_LIGHTS],
            lshadow: [false; MAX_LIGHTS], lsoft: [0.0; MAX_LIGHTS],
            env: EnvMap::of(&s.world), env_cos: 0.0, env_sin: 0.0, env_gain: 0.0, env_levels: 0.0, sh: [V3::default(); 9],
            mix: false,
        };
        for l in &s.lights[..s.nlight] {
            if r.nl >= MAX_LIGHTS { break; }
            if !l.on { continue; }
            r.lpos[r.nl] = if l.dir { norm(l.pos) } else { l.pos };
            r.lkind[r.nl] = if l.dir { 0.0 } else { 1.0 };
            r.lcol[r.nl] = l.color * l.intensity;
            r.lshadow[r.nl] = l.shadow;
            r.lsoft[r.nl] = l.softness;
            r.nl += 1;
        }
        if let Some(env) = &r.env {
            let wd = &s.world;
            let a = wd.env_rotation * PI / 180.0;
            r.env_cos = a.cos();
            r.env_sin = a.sin();
            r.env_gain = wd.env_intensity;
            r.env_levels = (env.nlev() - 1) as f64;
            r.sh = env.irradiance(wd.env_rotation, wd.env_intensity, wd.env_lights);
            /* le sorgenti forti dell'ambiente come luci (tipo 2), dopo quelle della scena */
            if wd.env_lights {
                for (dir, col, soft) in env.lights(wd.env_rotation, wd.env_intensity) {
                    if r.nl >= MAX_LIGHTS { break; }
                    r.lpos[r.nl] = dir;
                    r.lkind[r.nl] = 2.0;
                    r.lcol[r.nl] = col;
                    r.lshadow[r.nl] = true;
                    r.lsoft[r.nl] = soft;
                    r.nl += 1;
                }
            }
        }
        r
    }

    /* l'ambiente nella direzione d (lineare); lod: sfocato per la ruvidita' */
    fn env_lin(&self, d: V3, lod: f64) -> V3 {
        let q = v3(self.env_cos * d.x - self.env_sin * d.z, d.y, self.env_sin * d.x + self.env_cos * d.z);
        self.env.as_ref().unwrap().sample(q, lod) * self.env_gain
    }

    /* l'irradianza dell'ambiente / pi verso la normale n */
    fn env_irradiance(&self, n: V3) -> V3 {
        let c = &self.sh;
        let mut v = c[0] * 0.282095;
        v = v + ((c[1] * n.y + c[2] * n.z) + c[3] * n.x) * 0.488603;
        v = v + ((c[4] * (n.x * n.y) + c[5] * (n.y * n.z)) + c[7] * (n.x * n.z)) * 1.092548;
        v = v + (c[6] * (0.315392 * (3.0 * n.z * n.z - 1.0)) + c[8] * (0.546274 * (n.x * n.x - n.y * n.y)));
        v3(rmax(v.x, 0.0), rmax(v.y, 0.0), rmax(v.z, 0.0))
    }

    fn shadow(&self, c: &Ctx, p: V3, l: V3, tmax: f64, k: f64) -> f64 {
        let g = self.g;
        let mut res = 1.0;
        if g.br < 0.0 { return 1.0; }
        let Some((t0, t1)) = hit_sphere(p, l, g.bc, g.br + 0.3) else { return 1.0 };
        let mut t = rmax(t0, 0.02);
        let tmax = rmin(tmax, t1);
        let (st, sm, nmax) = if g.cavity { (0.5, 0.01, 256) } else { (1.0, 0.02, 128) };
        let mut n = 0;
        while n < nmax && t < tmax {
            let h = c.map_d(p + l * t) * st;
            if k * h < res * t {
                res = k * h / t;
                if res < 0.01 { break; }
            }
            t += rmax(h, sm);
            n += 1;
        }
        let res = clamp(res, 0.0, 1.0);
        res * res * (3.0 - 2.0 * res)
    }

    fn shadow_glass(&self, c: &mut Ctx, p: V3, l: V3, tmax: f64, k: f64) -> V3 {
        let g = self.g;
        let mut res = 1.0;
        let mut tr = v3(1.0, 1.0, 1.0);
        if g.br < 0.0 { return tr; }
        let Some((t0, t1)) = hit_sphere(p, l, g.bc, g.br + 0.3) else { return tr };
        let mut t = rmax(t0, 0.02);
        let tmax = rmin(tmax, t1);
        let mut n = 0;
        while n < 160 && t < tmax {
            n += 1;
            let h = c.map_d(p + l * t);
            if k * h < res * t {
                let mut o = -1;
                let mut a = 0.0;
                let mut mm = GMat::default();
                /* o viene da un I_PRIM: GScene::build ha verificato che ogni
                 * istruzione indica un oggetto che esiste */
                let obj = |o: i32| unsafe { g.obj.get_unchecked(o as usize) };
                if self.mix {
                    /* materiali misti: il materiale mescolato in questo punto */
                    mm = c.map_m(p + l * t);
                    a = if mm.metal > 0.5 { 0.0 } else { clamp(mm.opt[2], 0.0, 1.0) };
                } else {
                    o = c.pick_obj(p + l * t);
                    if o >= 0 {
                        let m = &obj(o).mat;
                        a = if m.metal > 0.5 { 0.0 } else { clamp(m.opt[2], 0.0, 1.0) };
                    }
                }
                if a <= 0.0 {
                    res = k * h / t;
                    if res < 0.01 { break; }
                } else if h < 0.001 && self.mix {
                    /* l'assorbimento tratto per tratto col materiale di ogni punto */
                    let mut ta = mix_absorb(&mm, 0.003);
                    t += 0.003;
                    let mut i = 0;
                    while i < 96 && t < tmax {
                        let q = p + l * t;
                        let hi = c.map_d(q);
                        if hi > 0.0005 { break; }
                        let st = rmax(-hi, 0.004);
                        let mi = c.map_m(q);
                        ta = ta * mix_absorb(&mi, st);
                        t += st;
                        i += 1;
                    }
                    tr = tr * (ta * a);
                    if rmax(tr.x, rmax(tr.y, tr.z)) < 0.01 { res = 0.0; break; }
                    t += 0.02;
                    continue;
                } else if h < 0.001 {
                    let te = t;
                    let col = obj(o).mat.col;
                    t += 0.003;
                    let mut i = 0;
                    while i < 96 && t < tmax {
                        let hi = c.map_d(p + l * t);
                        if hi > 0.0005 { break; }
                        t += rmax(-hi, 0.004);
                        i += 1;
                    }
                    tr = tr * (vpow(v3(rmax(col.x, 0.001), rmax(col.y, 0.001), rmax(col.z, 0.001)), t - te) * a);
                    if rmax(tr.x, rmax(tr.y, tr.z)) < 0.01 { res = 0.0; break; }
                    t += 0.02;
                    continue;
                }
            }
            t += if g.cavity { rmax(h * 0.5, 0.01) } else { rmax(h, 0.02) };
        }
        let res = clamp(res, 0.0, 1.0);
        tr * (res * res * (3.0 - 2.0 * res))
    }

    fn ambient_occlusion(&self, c: &Ctx, p: V3, n: V3) -> f64 {
        let (mut occ, mut wt) = (0.0, 1.0);
        for i in 0..5 {
            let e = 0.14 + i as f64 * 0.12;
            let q = p + n * e;
            let mut d = c.map_d(q);
            if self.s.world.floor { d = rmin(d, q.y); }
            occ += (e - d) * wt;
            wt *= 0.85;
        }
        clamp(1.0 - occ * 2.5, 0.0, 1.0)
    }

    fn light_dir(&self, c: &mut Ctx, l: V3, soft: f64) -> V3 {
        let th = 0.5 / rmax(soft, 0.5);
        let t = norm(if l.y.abs() < 0.9 { cross(l, v3(0.0, 1.0, 0.0)) } else { cross(l, v3(1.0, 0.0, 0.0)) });
        let b = cross(l, t);
        let cc = 1.0 - rnd(c) * (1.0 - th.cos());
        let s = rmax(1.0 - cc * cc, 0.0).sqrt();
        let a = TP * rnd(c);
        norm(t * (s * a.cos()) + b * (s * a.sin()) + l * cc)
    }

    /* con l'ambiente: quanto cielo vede il punto */
    fn env_visibility(&self, c: &mut Ctx, p: V3, n: V3) -> f64 {
        let t = norm(if n.y.abs() < 0.9 { cross(n, v3(0.0, 1.0, 0.0)) } else { cross(n, v3(1.0, 0.0, 0.0)) });
        let b = cross(n, t);
        if self.sample > 0 {
            let (r1, a) = (rnd(c), TP * rnd(c));
            let rr = r1.sqrt();
            let d = norm(t * (rr * a.cos()) + b * (rr * a.sin()) + n * rmax(1.0 - r1, 0.0).sqrt());
            return self.shadow(c, p + n * 0.002, d, 1e3, HARD);
        }
        let sc = rmax(self.g.br, 0.01) * 0.35;
        let (mut occ, mut wgt, mut tot) = (0.0, 1.0, 0.0);
        for i in 0..6 {
            let h = sc * (0.04 + 0.19 * i as f64);
            let mut d = c.map_d(p + n * h);
            if self.s.world.floor { d = rmin(d, p.y + n.y * h); }
            occ += rmax(h - d, 0.0) * wgt;
            tot += h * wgt;
            wgt *= 0.75;
        }
        clamp(1.0 - 1.3 * occ / tot, 0.0, 1.0)
    }

    /* la luce diretta: la parte diffusa e la speculare a parte */
    fn shade(&self, c: &mut Ctx, p: V3, n: V3, d: V3, m: &GMat, is_floor: bool, wd: f64) -> (V3, V3) {
        let w = &self.s.world;
        let g = self.g;
        let alb = m.col * m.dif;
        let shc = if is_floor { v3(0.0, 0.0, 0.0) } else { v3(m.sheen[0], m.sheen[1], m.sheen[2]) };
        let smax = rmax(shc.x, rmax(shc.y, shc.z));
        let ev = 0.0;
        let mut occ = 1.0;
        let (mut acc, mut spec) = (v3(0.0, 0.0, 0.0), v3(0.0, 0.0, 0.0));
        if self.env.is_none() && self.quality & RQ_AO != 0 && w.ao && g.br >= 0.0 && len(p - g.bc) < g.br + 0.8 {
            occ = self.ambient_occlusion(c, p, n);
        }
        /* nl <= MAX_LIGHTS: col min il compilatore lo vede e non controlla gli indici */
        for i in 0..self.nl.min(MAX_LIGHTS) {
            let lc = self.lcol[i];
            let mut shv = v3(1.0, 1.0, 1.0);
            let from_env = self.lkind[i] > 1.5;
            let (ld, tmax) = if self.lkind[i] < 0.5 || from_env { (self.lpos[i], 100.0) } else {
                let v = self.lpos[i] - p;
                let tm = len(v);
                (v * (1.0 / tm), tm)
            };
            let mut dif = dot(n, ld);
            if dif <= 0.0 { continue; }
            if self.quality & RQ_SHADOWS != 0 && self.lshadow[i] {
                let sd = if self.sample > 0 { self.light_dir(c, ld, self.lsoft[i]) } else { ld };
                let k = if self.sample > 0 { HARD } else { self.lsoft[i] };
                if g.has_glass { shv = self.shadow_glass(c, p, sd, tmax, k); } else { dif *= self.shadow(c, p, sd, tmax, k); }
            }
            acc = acc + alb * lc * shv * dif;
            /* chi riflette vede gia' la sorgente dell'ambiente nel riflesso: niente Blinn in piu' */
            if self.quality & RQ_SPEC != 0 && !is_floor && m.spec > 0.0 &&
               !(from_env && (m.opt[0] > 0.0 || m.opt[2] > 0.0 || m.metal > 0.5)) {
                let hv = norm(ld - d);
                let sp = rmax(dot(n, hv), 0.0).powf(m.shin.floor()) * m.spec * dif;
                spec = spec + lc * shv * sp;
            }
        }
        if self.env.is_some() {
            let vis = if wd > 0.0 && self.quality & RQ_AO != 0 && w.ao { self.env_visibility(c, p, n) } else { 1.0 };
            acc = acc + (alb * (1.0 - smax * ev) + shc * ev) * self.env_irradiance(n) * vis;
            return (acc, spec);
        }
        let e = (0.5 + n.y * 0.5) * occ;
        acc = acc + (alb * (1.0 - smax * ev) + shc * ev) * (w.sky_light * e + w.ambient * occ);
        (acc, spec)
    }

    fn tone(&self, col: V3) -> V3 {
        let w = &self.s.world;
        let col = col * 2.0f64.powf(w.exposure);
        if w.tone_map { tone_neutral(col) } else { col }
    }

    #[inline(always)]
    fn hit_eps(&self, t: f64) -> f64 { t / (self.focal * self.h as f64) + 0.000004 }

    fn confirm_hit(&self, c: &Ctx, ro: V3, d: V3, t: &mut f64, mut h: f64, eps: f64) -> f64 {
        let mut tt = *t;
        if !self.g.inexact { return *t; }
        for _ in 0..64 {
            if h < eps * 0.02 { return *t; }
            tt += h;
            h = c.map_d(ro + d * tt);
            if h > eps { *t = tt; return 0.0; }
        }
        *t
    }

    fn march_objects(&self, c: &Ctx, ro: V3, d: V3, tmax: f64) -> f64 {
        let g = self.g;
        if g.br < 0.0 { return -1.0; }
        let Some((t0, t1)) = hit_sphere(ro, d, g.bc, g.br) else { return -1.0 };
        let tmax = rmin(t1, tmax);
        let mut t = rmax(t0, 0.0);
        let mut i = 0;
        while i < MAXSTEP && t < tmax {
            i += 1;
            let h = c.map_d(ro + d * t);
            let eps = self.hit_eps(t);
            if h < eps {
                let th = self.confirm_hit(c, ro, d, &mut t, h, eps);
                if th > 0.0 { return th; }
                continue;
            }
            t += h;
        }
        -1.0
    }

    fn sky(&self, d: V3) -> V3 {
        let w = &self.s.world;
        let k = clamp(1.0 - d.y, 0.0, 1.0);
        w.sky_top + (w.sky_horizon - w.sky_top) * k
    }

    fn floor_colour(&self, p: V3, t: f64) -> V3 {
        let w = &self.s.world;
        let (cx, cz) = (p.x * w.floor_scale, p.z * w.floor_scale);
        if w.floor_type == FLOOR_SOLID { return w.floor_a; }
        if w.floor_type == FLOOR_GRID {
            let dx = (fract(cx + 0.5) - 0.5).abs() / w.floor_scale;
            let dz = (fract(cz + 0.5) - 0.5).abs() / w.floor_scale;
            let width = 0.012 + t * 0.0012;
            return if rmin(dx, dz) < width { w.floor_b } else { w.floor_a };
        }
        let s = cx.floor() + cz.floor();
        if s - 2.0 * (s / 2.0).floor() > 0.5 { w.floor_a } else { w.floor_b }
    }

    fn sky_lin(&self, d: V3) -> V3 {
        if self.env.is_some() { return self.env_lin(d, 0.0); }
        let c = self.sky(d);
        c * c
    }

    fn rnd_dir(c: &mut Ctx) -> V3 {
        let z = rnd(c) * 2.0 - 1.0;
        let a = rnd(c) * TP;
        let rr = rmax(1.0 - z * z, 0.0).sqrt();
        v3(rr * a.cos(), z, rr * a.sin())
    }

    fn roughen(&self, c: &mut Ctx, d: V3, n: V3, rough: f64, s: f64) -> V3 {
        if self.sample == 0 || rough <= 0.0 { return d; }
        let q = norm(d + Self::rnd_dir(c) * rough);
        if dot(q, n) * s > 0.0 { q } else { d }
    }

    fn march_scene(&self, c: &Ctx, ro: V3, d: V3) -> (f64, bool) {
        let mut tf = -1.0;
        if self.s.world.floor && d.y < -0.001 && ro.y > 0.0 { tf = ro.y / -d.y; }
        let t = self.march_objects(c, ro, d, if tf >= 0.0 { tf } else { 1e9 });
        let on_floor = t < 0.0 && tf >= 0.0;
        (if on_floor { tf } else { t }, on_floor)
    }

    fn render_ray(&self, c: &mut Ctx, mut ro: V3, mut d: V3) -> u32 {
        let w = &self.s.world;
        let (mut t, mut on_floor) = self.march_scene(c, ro, d);
        let mut lod = 0.0;
        let (mut col, mut thr) = (v3(0.0, 0.0, 0.0), v3(1.0, 1.0, 1.0));
        if t < 0.0 {
            if self.env.is_some() {
                return if w.env_background { pack(self.tone(self.env_lin(d, 0.0)), true) } else { pack(w.bg_color, false) };
            }
            return pack(self.sky(d), false);
        }
        let t_first = t;
        for b in 0..16 {
            let p = ro + d * t;
            let n;
            let mut m;
            let mut tint = v3(1.0, 1.0, 1.0);
            let mut wt = 0.0;
            if !on_floor {
                n = normal_at(c, p, EPS_N);
                m = c.map_m(p);
            } else {
                n = v3(0.0, 1.0, 0.0);
                m = GMat::default_mat();
                m.col = self.floor_colour(p, t);
                m.spec = 0.0; m.shin = 1.0; m.dif = 1.0; m.sel = 0.0;
                m.opt = [w.floor_reflect, 0.0, 0.0, 1.0];
                m.metal = 0.0;
                m.sheen = [0.0, 0.0, 0.0, 0.5];
            }
            let mut wr = clamp(m.opt[0], 0.0, 1.0);
            let tr = clamp(m.opt[2], 0.0, 1.0);
            let ior = rmax(m.opt[3], 1.0);
            let rough = clamp(m.opt[1], 0.0, 1.0);
            if m.metal > 0.5 { tint = m.col; } else if !on_floor && (wr > 0.0 || tr > 0.0) {
                let mut f0 = (ior - 1.0) / (ior + 1.0);
                f0 *= f0;
                let cc = clamp(1.0 + dot(d, n), 0.0, 1.0);
                let c2 = cc * cc;
                let fr = f0 + (1.0 - f0) * c2 * c2 * cc;
                wr += (1.0 - wr) * fr;
                wt = tr * (1.0 - wr);
            }
            let wd = rmax(1.0 - wr - wt, 0.0);
            let (dif, spec) = self.shade(c, p, n, d, &m, on_floor, wd);
            col = col + thr * (dif * wd + spec * tint);
            if wr + wt <= 0.0 { break; }
            let rd = self.roughen(c, reflect(d, n), n, rough, 1.0);
            let mut through = wt > 0.0;
            if wr > 0.0 && wt > 0.0 {
                if self.sample > 0 {
                    through = rnd(c) * (wr + wt) < wt;
                    thr = thr * (wr + wt);
                    if !through { thr = thr * tint; }
                } else {
                    through = wt >= wr;
                    if through { col = col + thr * tint * self.sky_lin(rd) * wr; thr = thr * wt; } else { thr = thr * tint * wr; }
                }
            } else {
                thr = if through { thr * wt } else { thr * tint * wr };
            }
            if through {
                let mut q = p - n * (rmax(c.map_d(p), 0.0) + 0.003);
                let mut id = self.roughen(c, refract(d, n, 1.0 / ior), n, rough, -1.0);
                let absorb = v3(rmax(m.col.x, 0.001), rmax(m.col.y, 0.001), rmax(m.col.z, 0.001));
                let mut out = false;
                for _ in 0..self.bounces + 2 {
                    let ti = if self.mix { march_inside_mix(c, q, id, &mut thr) } else { march_inside(c, q, id) };
                    let e = q + id * ti;
                    let ne = normal_at(c, e, EPS_N);
                    if !self.mix { thr = thr * vpow(absorb, ti); }
                    let od = refract(id, -ne, ior);
                    if dot(od, od) > 0.0 { ro = e + ne * 0.003; d = od; out = true; break; }
                    id = reflect(id, ne);
                    q = e - ne * 0.003;
                }
                if !out { break; }
            } else {
                ro = p + n * 0.003;
                d = rd;
            }
            if self.env.is_some() && self.sample == 0 { lod = rough * self.env_levels * 0.7; }
            if b >= self.bounces || rmax(thr.x, rmax(thr.y, thr.z)) < 0.02 {
                col = col + thr * (if self.env.is_some() { self.env_lin(d, lod) } else { self.sky_lin(d) });
                break;
            }
            let (tn, fl) = self.march_scene(c, ro, d);
            t = tn;
            on_floor = fl;
            if t < 0.0 {
                col = col + thr * (if self.env.is_some() { self.env_lin(d, lod) } else { self.sky_lin(d) });
                break;
            }
        }
        let t_first = rmin(t_first, 40.0);
        let f = t_first * t_first * w.fog;
        let f = f / (f + 1.0);
        col = col + (w.fog_color - col) * f;
        pack(self.tone(col), true)
    }

    /* il colore del pixel (x, y), y dall'alto: 0x00RRGGBB */
    pub fn pixel(&self, c: &mut Ctx, x: usize, y: usize) -> u32 {
        /* u da -w/h a w/h, v da -1 (in basso) a 1, al centro del pixel */
        let (wf, hf) = (self.w as f64, self.h as f64);
        let u = (2.0 * (x as f64 + 0.5) - wf) / hf;
        let v = (2.0 * ((self.h - 1 - y) as f64 + 0.5) - hf) / hf;
        let rd = norm(self.fw * self.focal + self.rt * u + self.up * v);
        c.rng = (x as u32).wrapping_mul(1973).wrapping_add(((self.h - 1 - y) as u32).wrapping_mul(9277)).wrapping_add(self.sample.wrapping_mul(26699));
        rnd(c);
        self.render_ray(c, self.ro, rd)
    }
}
