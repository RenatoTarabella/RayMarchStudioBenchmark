// La base comune (rm.h, vec.c): vettori e matrici in double. Le stesse
// formule del C nello stesso ordine, perche' l'immagine venga identica.
use std::ops::{Add, Mul, Neg, Sub};

pub const PI: f64 = 3.14159265358979323846;
pub const TP: f64 = 6.28318530717958647692;

#[derive(Clone, Copy, Default, Debug)]
pub struct V3 {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

#[inline(always)]
pub const fn v3(x: f64, y: f64, z: f64) -> V3 {
    V3 { x, y, z }
}

impl Add for V3 {
    type Output = V3;
    #[inline(always)]
    fn add(self, b: V3) -> V3 { v3(self.x + b.x, self.y + b.y, self.z + b.z) }
}
impl Sub for V3 {
    type Output = V3;
    #[inline(always)]
    fn sub(self, b: V3) -> V3 { v3(self.x - b.x, self.y - b.y, self.z - b.z) }
}
impl Neg for V3 {
    type Output = V3;
    #[inline(always)]
    fn neg(self) -> V3 { v3(-self.x, -self.y, -self.z) }
}
impl Mul<f64> for V3 {
    type Output = V3;
    #[inline(always)]
    fn mul(self, s: f64) -> V3 { v3(self.x * s, self.y * s, self.z * s) }
}
/* componente per componente (v3mulv) */
impl Mul<V3> for V3 {
    type Output = V3;
    #[inline(always)]
    fn mul(self, b: V3) -> V3 { v3(self.x * b.x, self.y * b.y, self.z * b.z) }
}

impl V3 {
    #[inline(always)]
    pub fn get(self, i: usize) -> f64 { if i == 0 { self.x } else if i == 1 { self.y } else { self.z } }
}

#[inline(always)]
pub fn dot(a: V3, b: V3) -> f64 { a.x * b.x + a.y * b.y + a.z * b.z }
#[inline(always)]
pub fn cross(a: V3, b: V3) -> V3 { v3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x) }
#[inline(always)]
pub fn len(a: V3) -> f64 { dot(a, a).sqrt() }
/* a + (b - a) * t */
#[inline(always)]
pub fn lerp(a: V3, b: V3, t: f64) -> V3 { v3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t) }
#[inline(always)]
pub fn vabs(a: V3) -> V3 { v3(a.x.abs(), a.y.abs(), a.z.abs()) }
#[inline(always)]
pub fn maxc(a: V3) -> f64 {
    if a.x > a.y { if a.x > a.z { a.x } else { a.z } } else if a.y > a.z { a.y } else { a.z }
}
/* versore; (0,0,0) resta tale */
#[inline(always)]
pub fn norm(a: V3) -> V3 {
    let l = len(a);
    if l > 0.0 { a * (1.0 / l) } else { a }
}

/* come il C: a < b ? a : b (f64::min tratta diversamente NaN) */
#[inline(always)]
pub fn rmin(a: f64, b: f64) -> f64 { if a < b { a } else { b } }
#[inline(always)]
pub fn rmax(a: f64, b: f64) -> f64 { if a > b { a } else { b } }
#[inline(always)]
pub fn clamp(v: f64, lo: f64, hi: f64) -> f64 { if v < lo { lo } else if v > hi { hi } else { v } }
#[inline(always)]
pub fn mix(a: f64, b: f64, t: f64) -> f64 { a + (b - a) * t }
#[inline(always)]
pub fn len2(x: f64, y: f64) -> f64 { (x * x + y * y).sqrt() }
#[inline(always)]
pub fn fract(x: f64) -> f64 { x - x.floor() }
/* a meta' verso il pari, come rround del C */
pub fn round_even(x: f64) -> f64 {
    let mut r = (x + 0.5).floor();
    /* r e' intero: r - 2 trunc(r / 2) e' esattamente fmod(r, 2). Scritto cosi'
     * perche' LLVM espande r % 2.0 con una FMA (stesso risultato, ma cosi'
     * nessuna delle tre versioni usa FMA nel proprio codice) */
    if r - x == 0.5 && r - 2.0 * (r / 2.0).trunc() != 0.0 { r -= 1.0; }
    r
}

/* Trasformazione affine: righe di [3x3 | traslazione] */
#[derive(Clone, Copy, Debug)]
pub struct M34 {
    pub m: [f64; 12],
}

pub const IDENTITY: M34 = M34 { m: [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0] };

impl Default for M34 {
    fn default() -> Self { IDENTITY }
}

impl M34 {
    #[inline(always)]
    pub fn point(&self, p: V3) -> V3 {
        let m = &self.m;
        v3(m[0] * p.x + m[1] * p.y + m[2] * p.z + m[3],
           m[4] * p.x + m[5] * p.y + m[6] * p.z + m[7],
           m[8] * p.x + m[9] * p.y + m[10] * p.z + m[11])
    }

    pub fn trans(&self) -> V3 { v3(self.m[3], self.m[7], self.m[11]) }

    /* posizione, rotazione in gradi (prima X, poi Y, poi Z), scala: T * Rz * Ry * Rx * S */
    pub fn compose(pos: V3, rot: V3, scl: V3) -> M34 {
        let (ax, ay, az) = (rot.x * PI / 180.0, rot.y * PI / 180.0, rot.z * PI / 180.0);
        let (sx, cx, sy, cy, sz, cz) = (ax.sin(), ax.cos(), ay.sin(), ay.cos(), az.sin(), az.cos());
        let rx = [[1.0, 0.0, 0.0], [0.0, cx, -sx], [0.0, sx, cx]];
        let ry = [[cy, 0.0, sy], [0.0, 1.0, 0.0], [-sy, 0.0, cy]];
        let rz = [[cz, -sz, 0.0], [sz, cz, 0.0], [0.0, 0.0, 1.0]];
        let mul33 = |a: &[[f64; 3]; 3], b: &[[f64; 3]; 3]| {
            let mut r = [[0.0; 3]; 3];
            for i in 0..3 {
                for j in 0..3 {
                    r[i][j] = a[i][0] * b[0][j] + a[i][1] * b[1][j] + a[i][2] * b[2][j];
                }
            }
            r
        };
        let t = mul33(&ry, &rx);
        let r = mul33(&rz, &t);
        let s = [scl.x, scl.y, scl.z];
        let mut o = M34 { m: [0.0; 12] };
        for i in 0..3 {
            for j in 0..3 {
                o.m[i * 4 + j] = r[i][j] * s[j];
            }
        }
        o.m[3] = pos.x;
        o.m[7] = pos.y;
        o.m[11] = pos.z;
        o
    }

    pub fn col_len(&self, j: usize) -> f64 {
        let m = &self.m;
        (m[j] * m[j] + m[4 + j] * m[4 + j] + m[8 + j] * m[8 + j]).sqrt()
    }

    pub fn max_scale(&self) -> f64 { rmax(self.col_len(0), rmax(self.col_len(1), self.col_len(2))) }

    pub fn mul(a: &M34, b: &M34) -> M34 {
        let (x, y) = (&a.m, &b.m);
        let mut t = M34 { m: [0.0; 12] };
        for i in 0..3 {
            for j in 0..3 {
                t.m[i * 4 + j] = x[i * 4] * y[j] + x[i * 4 + 1] * y[4 + j] + x[i * 4 + 2] * y[8 + j];
            }
            t.m[i * 4 + 3] = x[i * 4] * y[3] + x[i * 4 + 1] * y[7] + x[i * 4 + 2] * y[11] + x[i * 4 + 3];
        }
        t
    }

    /* None se singolare */
    pub fn inverse(s: &M34) -> Option<M34> {
        let m = &s.m;
        let (a, b, c, d, e, f) = (m[0], m[1], m[2], m[4], m[5], m[6]);
        let (g, h, k) = (m[8], m[9], m[10]);
        let (aa, bb, cc) = (e * k - f * h, f * g - d * k, d * h - e * g);
        let det = a * aa + b * bb + c * cc;
        if det == 0.0 || det.is_nan() { return None; }
        let id = 1.0 / det;
        let mut r = IDENTITY;
        r.m[0] = aa * id; r.m[1] = (c * h - b * k) * id; r.m[2] = (b * f - c * e) * id;
        r.m[4] = bb * id; r.m[5] = (a * k - c * g) * id; r.m[6] = (c * d - a * f) * id;
        r.m[8] = cc * id; r.m[9] = (b * g - a * h) * id; r.m[10] = (a * e - b * d) * id;
        for i in 0..3 {
            r.m[i * 4 + 3] = -(r.m[i * 4] * m[3] + r.m[i * 4 + 1] * m[7] + r.m[i * 4 + 2] * m[11]);
        }
        Some(r)
    }

    pub fn decompose(&self) -> (V3, V3, V3) {
        let a = &self.m;
        let d = 180.0 / PI;
        let (s0, s1, s2) = (self.col_len(0), self.col_len(1), self.col_len(2));
        let (r00, r10, r20) = (a[0] / s0, a[4] / s0, a[8] / s0);
        let (r11, r21) = (a[5] / s1, a[9] / s1);
        let (r12, r22) = (a[6] / s2, a[10] / s2);
        let ay = clamp(-r20, -1.0, 1.0).asin();
        let (ax, az) = if ay.cos().abs() > 1e-9 { (r21.atan2(r22), r10.atan2(r00)) } else { ((-r12).atan2(r11), 0.0) };
        (self.trans(), v3(ax * d, ay * d, az * d), v3(s0, s1, s2))
    }
}
