// L'interprete del programma a pila (eval.c) e le primitive (sdf.c).
use crate::gscene::*;
use crate::scene::*;
use crate::vec::*;
use std::mem::MaybeUninit;

pub const FAR_D: f64 = 1000.0;
const CULL_D: f64 = 1.0;
const MAXST: usize = 128;

/* lo stato di un calcolo (uno per thread) */
pub struct Ctx<'a> {
    pub g: &'a GScene,
    pub rng: u32,
}

/* la pila dei valori senza azzerarla e senza controlli dei limiti: la
 * preparazione ha verificato che il programma non supera max_stack <= MAXST
 * e che ogni istruzione indica un oggetto o una sfera che esiste */
struct Stack<T: Copy, const N: usize>([MaybeUninit<T>; N]);

impl<T: Copy, const N: usize> Stack<T, N> {
    #[inline(always)]
    fn new() -> Self { Stack([const { MaybeUninit::uninit() }; N]) }
    #[inline(always)]
    fn get(&self, i: usize) -> T { debug_assert!(i < N); unsafe { self.0.get_unchecked(i).assume_init() } }
    #[inline(always)]
    fn set(&mut self, i: usize, v: T) { debug_assert!(i < N); unsafe { self.0.get_unchecked_mut(i).write(v); } }
}

#[inline(always)]
fn sd_box(f: &GObj, p: V3) -> f64 {
    let c = &f.c;
    let (qx, qy, qz) = (p.x.abs() - c[0], p.y.abs() - c[1], p.z.abs() - c[2]);
    len(v3(rmax(qx, 0.0), rmax(qy, 0.0), rmax(qz, 0.0))) + rmin(rmax(qx, rmax(qy, qz)), 0.0) - c[3]
}

#[inline(always)]
fn sd_cylinder(f: &GObj, p: V3) -> f64 {
    let c = &f.c;
    let (dx, dy) = (len2(p.x, p.z) - c[0], p.y.abs() - c[1]);
    rmin(rmax(dx, dy), 0.0) + len2(rmax(dx, 0.0), rmax(dy, 0.0)) - c[2]
}

#[inline(always)]
fn modify(f: &GObj, d: f64) -> f64 {
    let d = d - f.round;
    if f.shell > 0.0 { d.abs() - f.shell } else { d }
}

#[inline(always)]
fn prim_dist(f: &GObj, p: V3) -> f64 {
    let q = f.inv.point(p);
    let d = match f.typ {
        T_SPHERE => len(q) - f.p0,
        T_BOX => sd_box(f, q),
        _ => sd_cylinder(f, q),
    };
    modify(f, d * f.dscale)
}

/* distanza per difetto dalla sfera d'ingombro: il componente maggiore */
#[inline(always)]
fn cull_d(f: &GObj, p: V3) -> f64 { maxc(vabs(p - f.bc)) - f.br }

/* l'operazione dell'oggetto: a = i fratelli precedenti, b = lui; il peso di a */
#[inline(always)]
fn combine(f: &GObj, a: f64, b: f64) -> (f64, f64) {
    let k = f.k;
    if f.op == OP_SUB {
        if k <= 0.0 { return (rmax(a, -b), if a > -b { 1.0 } else { 0.0 }); }
        let h = clamp(0.5 - (b + a) * 0.5 / k, 0.0, 1.0);
        return (a + (-b - a) * h + k * h * (1.0 - h), 1.0 - h);
    }
    if f.op == OP_INTER {
        if k <= 0.0 { return (rmax(a, b), if a > b { 1.0 } else { 0.0 }); }
        let h = clamp(0.5 - (b - a) * 0.5 / k, 0.0, 1.0);
        return (b + (a - b) * h + k * h * (1.0 - h), h);
    }
    if k <= 0.0 { return (rmin(a, b), if a < b { 1.0 } else { 0.0 }); }
    let h = clamp(0.5 + (b - a) * 0.5 / k, 0.0, 1.0);
    (b + (a - b) * h - k * h * (1.0 - h), h)
}

impl<'a> Ctx<'a> {
    pub fn new(g: &'a GScene) -> Ctx<'a> {
        assert!(g.max_stack <= MAXST);
        Ctx { g, rng: 1 }
    }

    /* la distanza (con le scorciatoie delle sfere): il caso caldo, senza materiali */
    pub fn map_d(&self, p: V3) -> f64 {
        let g = self.g;
        let prog = &g.prog[..];
        let obj = &g.obj[..];
        let bound = &g.bound[..];
        let mut st: Stack<f64, MAXST> = Stack::new();
        let (mut sp, mut pc) = (0usize, 0usize);
        while pc < prog.len() {
            let ins = unsafe { prog.get_unchecked(pc) };
            pc += 1;
            match ins.code {
                I_MIN => {
                    sp -= 1;
                    st.set(sp - 1, rmin(st.get(sp - 1), st.get(sp)));
                }
                I_SKIP => {
                    let o = ins.obj as usize * 4;
                    let b = unsafe { bound.get_unchecked(o..o + 4) };
                    let db = maxc(vabs(p - v3(b[0], b[1], b[2]))) - b[3];
                    if db > CULL_D { st.set(sp, db); sp += 1; pc += ins.n as usize; }
                }
                I_LAZY => {
                    let f = unsafe { obj.get_unchecked(ins.obj as usize) };
                    if cull_d(f, p) >= f.k - st.get(sp - 1) { pc += ins.n as usize; }
                }
                I_PRIM => {
                    let f = unsafe { obj.get_unchecked(ins.obj as usize) };
                    if ins.cull != 0 {
                        let db = cull_d(f, p);
                        if db > CULL_D { st.set(sp, db); sp += 1; continue; }
                    }
                    st.set(sp, prim_dist(f, p));
                    sp += 1;
                }
                I_MOD => {
                    let f = unsafe { obj.get_unchecked(ins.obj as usize) };
                    st.set(sp - 1, modify(f, st.get(sp - 1)));
                }
                _ => {
                    let f = unsafe { obj.get_unchecked(ins.obj as usize) };
                    sp -= 1;
                    st.set(sp - 1, combine(f, st.get(sp - 1), st.get(sp)).0);
                }
            }
        }
        if sp > 0 { st.get(0) } else { FAR_D }
    }

    /* la distanza e il materiale (mode 1) o la primitiva piu' vicina (mode 2) */
    fn run(&self, mode: i32, p: V3, mout: &mut GMat, bd: &mut f64, best: &mut i32) -> f64 {
        let g = self.g;
        let prog = &g.prog[..];
        let obj = &g.obj[..];
        let mut st: Stack<f64, MAXST> = Stack::new();
        let mut ms: Stack<GMat, MAXST> = Stack::new();
        let (mut sp, mut pc) = (0usize, 0usize);
        while pc < prog.len() {
            let ins = unsafe { prog.get_unchecked(pc) };
            pc += 1;
            /* l'oggetto dell'istruzione (I_SKIP indica una sfera, non un oggetto: li' non si usa) */
            let f = || unsafe { obj.get_unchecked(ins.obj as usize) };
            match ins.code {
                I_MIN => {
                    sp -= 1;
                    if mode == 1 {
                        if st.get(sp) < st.get(sp - 1) { st.set(sp - 1, st.get(sp)); ms.set(sp - 1, ms.get(sp)); }
                    } else {
                        st.set(sp - 1, rmin(st.get(sp - 1), st.get(sp)));
                    }
                }
                I_SKIP | I_LAZY => {}
                I_PRIM => {
                    let f = f();
                    if mode == 1 { ms.set(sp, f.mat); }
                    let d = prim_dist(f, p);
                    st.set(sp, d);
                    if mode == 2 && d.abs() < *bd { *bd = d.abs(); *best = ins.obj; }
                    sp += 1;
                }
                I_MOD => st.set(sp - 1, modify(f(), st.get(sp - 1))),
                _ => {
                    sp -= 1;
                    let (d, wa) = combine(f(), st.get(sp - 1), st.get(sp));
                    st.set(sp - 1, d);
                    if mode == 1 { ms.set(sp - 1, GMat::mix(&ms.get(sp - 1), &ms.get(sp), wa)); }
                }
            }
        }
        *mout = if mode == 1 && sp > 0 { ms.get(0) } else { GMat::default_mat() };
        if sp > 0 { st.get(0) } else { FAR_D }
    }

    pub fn map_m(&mut self, p: V3) -> GMat {
        let mut m = GMat::default();
        let (mut bd, mut best) = (0.0, 0);
        self.run(1, p, &mut m, &mut bd, &mut best);
        m
    }

    /* la primitiva la cui superficie e' piu' vicina, -1 se nessuna */
    pub fn pick_obj(&mut self, p: V3) -> i32 {
        let mut m = GMat::default();
        let (mut bd, mut best) = (1e30, -1);
        self.run(2, p, &mut m, &mut bd, &mut best);
        best
    }
}
