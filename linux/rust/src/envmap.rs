// L'ambiente (envmap.c): lo studio procedurale, i mipmap, le sorgenti forti
// come luci e l'irradianza in armoniche sferiche. Le immagini sono in f32
// come nel C (le somme dei mipmap in f32, nello stesso ordine).
use crate::scene::*;
use crate::vec::*;

pub const ENV_MAXLIGHTS: usize = 3;
const MAX_LEVELS: usize = 16;
const SMALL_W: usize = 256;

#[derive(Clone)]
pub struct HdrImage {
    pub w: usize,
    pub h: usize,
    pub rgb: Vec<f32>,
}

pub struct EnvMap {
    pub lev: Vec<HdrImage>,
    pub nlights: usize,
    pub ldir: [V3; ENV_MAXLIGHTS],
    pub lcol: [V3; ENV_MAXLIGHTS],
    pub lrad: [f64; ENV_MAXLIGHTS],
    small: HdrImage,
    residual: HdrImage,
}

fn env_dir(u: f64, v: f64) -> V3 {
    let (phi, th) = ((u - 0.5) * TP, v * PI);
    v3(phi.sin() * th.sin(), th.cos(), -phi.cos() * th.sin())
}

fn from_angles(az: f64, el: f64) -> V3 {
    let (a, e) = (az * PI / 180.0, el * PI / 180.0);
    v3(a.sin() * e.cos(), e.sin(), a.cos() * e.cos())
}

pub fn rot_y(d: V3, deg: f64) -> V3 {
    let a = deg * PI / 180.0;
    let (c, s) = (a.cos(), a.sin());
    v3(c * d.x + s * d.z, d.y, -s * d.x + c * d.z)
}

fn lum(r: f32, g: f32, b: f32) -> f64 { 0.2126 * r as f64 + 0.7152 * g as f64 + 0.0722 * b as f64 }

// ---- lo studio procedurale

struct Panel { az: f64, el: f64, hw: f64, hh: f64, l: f64, col: V3, soft: f64 }
struct Studio { name: &'static str, top: V3, horizon: V3, bottom: V3, p: &'static [Panel] }

const W1: V3 = v3(1.0, 1.0, 1.0);
const fn pn(az: f64, el: f64, hw: f64, hh: f64, l: f64, col: V3, soft: f64) -> Panel { Panel { az, el, hw, hh, l, col, soft } }

static STUDIOS: [Studio; 5] = [
    Studio { name: "Light tent", top: v3(1.15, 1.15, 1.15), horizon: v3(0.85, 0.85, 0.85), bottom: v3(0.6, 0.6, 0.6), p: &[
        pn(0.0, 75.0, 45.0, 45.0, 2.2, W1, 0.3), pn(-85.0, 12.0, 18.0, 28.0, 0.03, W1, 0.1), pn(85.0, 12.0, 18.0, 28.0, 0.03, W1, 0.1)] },
    Studio { name: "Softbox studio", top: v3(0.07, 0.07, 0.075), horizon: v3(0.045, 0.045, 0.05), bottom: v3(0.03, 0.03, 0.03), p: &[
        pn(-45.0, 35.0, 22.0, 22.0, 9.0, W1, 0.15), pn(60.0, 20.0, 22.0, 30.0, 2.5, W1, 0.15), pn(0.0, 80.0, 30.0, 30.0, 3.0, W1, 0.15),
        pn(160.0, 30.0, 10.0, 25.0, 6.0, W1, 0.15)] },
    Studio { name: "Strip lights", top: v3(0.012, 0.012, 0.012), horizon: v3(0.008, 0.008, 0.008), bottom: v3(0.006, 0.006, 0.006), p: &[
        pn(-100.0, 15.0, 5.0, 40.0, 12.0, W1, 0.1), pn(100.0, 15.0, 5.0, 40.0, 12.0, W1, 0.1), pn(180.0, 30.0, 28.0, 4.0, 7.0, W1, 0.1),
        pn(0.0, 72.0, 4.0, 35.0, 7.0, W1, 0.1)] },
    Studio { name: "High key", top: v3(1.05, 1.05, 1.05), horizon: v3(1.0, 1.0, 1.0), bottom: v3(0.85, 0.85, 0.85), p: &[
        pn(0.0, 30.0, 50.0, 30.0, 3.0, W1, 0.3), pn(0.0, 80.0, 40.0, 40.0, 3.0, W1, 0.3)] },
    Studio { name: "Window", top: v3(0.3, 0.27, 0.24), horizon: v3(0.22, 0.19, 0.16), bottom: v3(0.12, 0.1, 0.08), p: &[
        pn(-60.0, 18.0, 30.0, 25.0, 7.0, v3(1.0, 0.98, 0.95), 0.05)] },
];

fn edge(x: f64, soft: f64) -> f64 {
    let t = clamp((1.0 - x) / soft, 0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

fn studio_render(s: &Studio, w: usize) -> HdrImage {
    let h = w / 2;
    let np = s.p.len();
    let mut img = HdrImage { w, h, rgb: vec![0.0; w * h * 3] };
    let mut c = [V3::default(); 4];
    let mut rt = [V3::default(); 4];
    let mut up = [V3::default(); 4];
    let mut tw = [0.0; 4];
    let mut th = [0.0; 4];
    for k in 0..np {
        c[k] = from_angles(s.p[k].az, s.p[k].el);
        rt[k] = if c[k].y.abs() > 0.99 { v3(1.0, 0.0, 0.0) } else { norm(cross(v3(0.0, 1.0, 0.0), c[k])) };
        up[k] = cross(c[k], rt[k]);
        tw[k] = (s.p[k].hw * PI / 180.0).tan();
        th[k] = (s.p[k].hh * PI / 180.0).tan();
    }
    for y in 0..h {
        for x in 0..w {
            let d = env_dir((x as f64 + 0.5) / w as f64, (y as f64 + 0.5) / h as f64);
            let mut col = if d.y >= 0.0 { s.horizon + (s.top - s.horizon) * d.y.sqrt() }
                          else { s.horizon + (s.bottom - s.horizon) * (-d.y).sqrt() };
            for k in 0..np {
                let z = dot(d, c[k]);
                if z <= 0.0 { continue; }
                let px = (dot(d, rt[k]) / z).abs() / tw[k];
                let py = (dot(d, up[k]) / z).abs() / th[k];
                let soft = rmax(s.p[k].soft, 1e-3);
                let m = edge(px, soft) * edge(py, soft);
                if m > 0.0 { col = col + (s.p[k].col * s.p[k].l - col) * m; }
            }
            let o = (y * w + x) * 3;
            img.rgb[o] = col.x as f32;
            img.rgb[o + 1] = col.y as f32;
            img.rgb[o + 2] = col.z as f32;
        }
    }
    img
}

fn shrink(a: &HdrImage, k: usize) -> HdrImage {
    let (w, h) = ((a.w / k).max(1), (a.h / k).max(1));
    let inv = 1.0f32 / (k * k) as f32;
    let mut r = HdrImage { w, h, rgb: vec![0.0; w * h * 3] };
    for y in 0..h {
        for x in 0..w {
            for c in 0..3 {
                let mut s = 0.0f32;
                for j in 0..k {
                    for i in 0..k {
                        let yy = if y * k + j < a.h { y * k + j } else { a.h - 1 };
                        let xx = if x * k + i < a.w { x * k + i } else { a.w - 1 };
                        s += a.rgb[(yy * a.w + xx) * 3 + c];
                    }
                }
                r.rgb[(y * w + x) * 3 + c] = s * inv;
            }
        }
    }
    r
}

fn solid_angle(s: &HdrImage, y: usize) -> f64 {
    TP / s.w as f64 * PI / s.h as f64 * ((y as f64 + 0.5) / s.h as f64 * PI).sin()
}

impl EnvMap {
    // ---- le sorgenti forti come luci
    fn extract(&mut self) {
        let sm = &self.small;
        let (w, h) = (sm.w, sm.h);
        let mut lm = vec![0.0f64; w * h];
        let mut taken = vec![false; w * h];
        let mut stack = vec![0usize; w * h];
        let (mut mean, mut area, mut first) = (0.0, 0.0, 0.0);
        for y in 0..h {
            let da = solid_angle(sm, y);
            for x in 0..w {
                let i = y * w + x;
                lm[i] = lum(sm.rgb[i * 3], sm.rgb[i * 3 + 1], sm.rgb[i * 3 + 2]);
                mean += lm[i] * da;
                area += da;
            }
        }
        mean /= area;
        for n in 0..3 {
            let mut best: Option<usize> = None;
            let mut top = 0;
            let mut omega = 0.0;
            let (mut ev, mut dir) = (V3::default(), V3::default());
            for i in 0..w * h {
                if !taken[i] && (best.is_none() || lm[i] > lm[best.unwrap()]) { best = Some(i); }
            }
            let Some(best) = best else { break };
            let peak = lm[best];
            if n == 0 { first = peak; }
            if peak < 4.0 * mean || peak < 0.25 * first { break; }
            let thr = rmax(2.0 * mean, 0.25 * peak);
            stack[top] = best;
            top += 1;
            taken[best] = true;
            while top > 0 {
                top -= 1;
                let ii = stack[top];
                let (x, y) = (ii % w, ii / w);
                let da = solid_angle(sm, y);
                let keep = rmin(1.0, 2.0 * mean / rmax(lm[ii], 1e-9));
                let c = v3(sm.rgb[ii * 3] as f64, sm.rgb[ii * 3 + 1] as f64, sm.rgb[ii * 3 + 2] as f64);
                let ex = c * (1.0 - keep);
                ev = ev + ex * da;
                let exl = lum(ex.x as f32, ex.y as f32, ex.z as f32);
                dir = dir + env_dir((x as f64 + 0.5) / w as f64, (y as f64 + 0.5) / h as f64) * (exl * da);
                omega += da;
                self.residual.rgb[ii * 3] = (c.x * keep) as f32;
                self.residual.rgb[ii * 3 + 1] = (c.y * keep) as f32;
                self.residual.rgb[ii * 3 + 2] = (c.z * keep) as f32;
                let nb = [(x as i64 - 1, y as i64), (x as i64 + 1, y as i64), (x as i64, y as i64 - 1), (x as i64, y as i64 + 1)];
                for (nx, ny) in nb {
                    if ny < 0 || ny >= h as i64 { continue; }
                    let j = ny as usize * w + ((nx + w as i64) % w as i64) as usize;
                    if taken[j] || lm[j] < thr { continue; }
                    taken[j] = true;
                    stack[top] = j;
                    top += 1;
                }
            }
            if len(dir) < 1e-12 { continue; }
            self.ldir[self.nlights] = norm(dir);
            self.lcol[self.nlights] = ev * (1.0 / PI);
            self.lrad[self.nlights] = (omega / PI).sqrt();
            self.nlights += 1;
        }
    }

    fn build(img: HdrImage) -> EnvMap {
        let k = if img.w / SMALL_W > 1 { img.w / SMALL_W } else { 1 };
        let small = if k > 1 { shrink(&img, k) } else { img.clone() };
        let mut lev = vec![img];
        /* i mipmap: meta' per meta' fino a 1 (come glGenerateMipmap) */
        while lev.len() < MAX_LEVELS && (lev.last().unwrap().w > 1 || lev.last().unwrap().h > 1) {
            let next = shrink(lev.last().unwrap(), 2);
            lev.push(next);
        }
        let residual = small.clone();
        let mut e = EnvMap { lev, nlights: 0, ldir: [V3::default(); 3], lcol: [V3::default(); 3], lrad: [0.0; 3], small, residual };
        e.extract();
        e
    }

    /* l'ambiente del mondo, None per il cielo */
    pub fn of(w: &World) -> Option<EnvMap> {
        if w.env != ENV_STUDIO { return None; }
        let sel = STUDIOS.iter().position(|s| s.name == w.env_preset).unwrap_or(0);
        Some(EnvMap::build(studio_render(&STUDIOS[sel], 1024)))
    }

    pub fn nlev(&self) -> usize { self.lev.len() }

    // ---- il campionamento: bilineare, u si ripete, v si ferma ai bordi

    #[inline(always)]
    fn texel(m: &HdrImage, x: i32, y: i32) -> V3 {
        let mut x = x % m.w as i32;
        if x < 0 { x += m.w as i32; }
        let y = y.clamp(0, m.h as i32 - 1);
        let p = (y as usize * m.w + x as usize) * 3;
        v3(m.rgb[p] as f64, m.rgb[p + 1] as f64, m.rgb[p + 2] as f64)
    }

    fn bilinear(m: &HdrImage, u: f64, v: f64) -> V3 {
        let (x, y) = (u * m.w as f64 - 0.5, v * m.h as f64 - 0.5);
        let (x0, y0) = (x.floor() as i32, y.floor() as i32);
        let (fx, fy) = (x - x0 as f64, y - y0 as f64);
        lerp(lerp(Self::texel(m, x0, y0), Self::texel(m, x0 + 1, y0), fx),
             lerp(Self::texel(m, x0, y0 + 1), Self::texel(m, x0 + 1, y0 + 1), fx), fy)
    }

    pub fn sample(&self, d: V3, lod: f64) -> V3 {
        let u = d.x.atan2(-d.z) / TP + 0.5;
        let v = clamp(d.y, -1.0, 1.0).acos() / PI;
        let lod = clamp(lod, 0.0, (self.lev.len() - 1) as f64);
        let l0 = lod.floor() as usize;
        let f = lod - l0 as f64;
        if f <= 0.0 || l0 + 1 >= self.lev.len() { return Self::bilinear(&self.lev[l0], u, v); }
        lerp(Self::bilinear(&self.lev[l0], u, v), Self::bilinear(&self.lev[l0 + 1], u, v), f)
    }

    // ---- l'irradianza in armoniche sferiche

    pub fn irradiance(&self, rotation: f64, intensity: f64, with_lights: bool) -> [V3; 9] {
        let band = [1.0, 2.0 / 3.0, 2.0 / 3.0, 2.0 / 3.0, 0.25, 0.25, 0.25, 0.25, 0.25];
        let src = if with_lights { &self.residual } else { &self.small };
        let (w, h) = (src.w, src.h);
        let mut c = [V3::default(); 9];
        let mut y = [0.0f64; 9];
        for j in 0..h {
            let da = solid_angle(src, j);
            for i in 0..w {
                let d = rot_y(env_dir((i as f64 + 0.5) / w as f64, (j as f64 + 0.5) / h as f64), rotation);
                let o = (j * w + i) * 3;
                let l = v3(src.rgb[o] as f64 * da, src.rgb[o + 1] as f64 * da, src.rgb[o + 2] as f64 * da);
                let (x, yy, z) = (d.x, d.y, d.z);
                y[0] = 0.282095;
                y[1] = 0.488603 * yy; y[2] = 0.488603 * z; y[3] = 0.488603 * x;
                y[4] = 1.092548 * x * yy; y[5] = 1.092548 * yy * z; y[6] = 0.315392 * (3.0 * z * z - 1.0);
                y[7] = 1.092548 * x * z; y[8] = 0.546274 * (x * x - yy * yy);
                for k in 0..9 { c[k] = c[k] + l * y[k]; }
            }
        }
        for k in 0..9 { c[k] = c[k] * (band[k] * intensity); }
        c
    }

    pub fn lights(&self, rotation: f64, intensity: f64) -> Vec<(V3, V3, f64)> {
        (0..self.nlights)
            .map(|i| (rot_y(self.ldir[i], rotation), self.lcol[i] * intensity, clamp(0.5 / rmax(self.lrad[i], 1e-3), 2.0, 64.0)))
            .collect()
    }
}
