// La scena (scene.h, scene.c, prims.c, sceneio.c): solo quello che serve
// alla scena del benchmark. Le forme e i contenitori che la scena non usa
// fermano la lettura con un messaggio. Gli oggetti stanno in un vettore e
// si riferiscono per indice (padre, figli).
use crate::vec::*;

pub const T_GROUP: i32 = -1;
pub const T_SPHERE: i32 = 0;
pub const T_BOX: i32 = 1;
pub const T_CYLINDER: i32 = 3;

pub const OP_UNION: i32 = 0;
pub const OP_SUB: i32 = 1;
pub const OP_INTER: i32 = 2;

pub const OF_WIRE: u32 = 1;
pub const OF_RENDER: u32 = 2;
pub const OF_LOCK: u32 = 4;
pub const OF_OFF: u32 = 8;

pub const MAX_LIGHTS: usize = 8;

#[derive(Clone, Copy)]
pub struct Material {
    pub color: V3,
    pub diffuse: f64,
    pub specular: f64,
    pub shininess: f64,
    pub reflect: f64,
    pub roughness: f64,
    pub transparency: f64,
    pub ior: f64,
    pub metal: bool,
    pub sheen: f64,
    pub sheen_color: V3,
    pub sheen_rough: f64,
}

impl Default for Material {
    fn default() -> Self {
        Material {
            color: v3(0.75, 0.3, 0.15), diffuse: 1.0, specular: 1.0, shininess: 24.0,
            reflect: 0.0, roughness: 0.0, transparency: 0.0, ior: 1.5, metal: false,
            sheen: 0.0, sheen_color: v3(1.0, 1.0, 1.0), sheen_rough: 0.5,
        }
    }
}

pub struct Obj {
    pub name: String,
    pub typ: i32,
    pub par: [f64; 10],
    pub pos: V3,
    pub rot: V3,
    pub scl: V3,
    pub op: i32,
    pub k: f64,
    pub round: f64,
    pub shell: f64,
    pub mat: Material,
    pub inherit_mat: bool,
    pub flags: u32,
    pub parent: Option<usize>,
    pub kids: Vec<usize>,
    pub index: usize,
}

impl Obj {
    pub fn is_container(&self) -> bool { self.typ < 0 }
}

#[derive(Clone, Copy, Default)]
pub struct Light {
    pub dir: bool,
    pub pos: V3,
    pub color: V3,
    pub intensity: f64,
    pub shadow: bool,
    pub softness: f64,
    pub on: bool,
}

#[derive(Clone, Copy, Default)]
pub struct Camera {
    pub pos: V3,
    pub target: V3,
    pub focal: f64,
}

pub const FLOOR_CHECKER: i32 = 0;
pub const FLOOR_SOLID: i32 = 1;
pub const FLOOR_GRID: i32 = 2;
pub const ENV_SKY: i32 = 0;
pub const ENV_STUDIO: i32 = 1;
pub const ENV_HDRI: i32 = 2;

pub struct World {
    pub floor: bool,
    pub floor_a: V3,
    pub floor_b: V3,
    pub floor_scale: f64,
    pub floor_type: i32,
    pub floor_reflect: f64,
    pub sky_top: V3,
    pub sky_horizon: V3,
    pub sky_light: V3,
    pub ambient: V3,
    pub fog: f64,
    pub fog_color: V3,
    pub ao: bool,
    pub env: i32,
    pub env_preset: String,
    pub env_intensity: f64,
    pub env_rotation: f64,
    pub env_background: bool,
    pub bg_color: V3,
    pub env_lights: bool,
    pub tone_map: bool,
    pub exposure: f64,
}

impl Default for World {
    fn default() -> Self {
        World {
            floor: true, floor_a: v3(0.42, 0.4, 0.38), floor_b: v3(0.2, 0.2, 0.22), floor_scale: 1.5,
            floor_type: FLOOR_CHECKER, floor_reflect: 0.0,
            sky_top: v3(0.55, 0.7, 1.0), sky_horizon: v3(0.9, 0.95, 1.0), sky_light: v3(0.25, 0.3, 0.45),
            ambient: v3(0.08, 0.07, 0.06), fog: 0.0015, fog_color: v3(0.8, 0.85, 1.0), ao: true,
            env: ENV_SKY, env_preset: "Softbox studio".into(), env_intensity: 1.0, env_rotation: 0.0,
            env_background: true, bg_color: v3(0.85, 0.85, 0.86), env_lights: true, tone_map: false, exposure: 0.0,
        }
    }
}

pub struct Scene {
    pub objs: Vec<Obj>,
    pub roots: Vec<usize>,
    pub lights: [Light; MAX_LIGHTS],
    pub nlight: usize,
    pub cam: Camera,
    pub world: World,
}

/* i parametri di partenza delle forme che il benchmark usa */
fn defaults(typ: i32) -> &'static [f64] {
    match typ {
        T_SPHERE => &[0.5],
        T_BOX => &[1.0, 1.0, 1.0, 0.05],
        T_CYLINDER => &[0.5, 1.0, 0.05],
        _ => &[],
    }
}

/* la lettura (sceneio.c) */
struct Reader<'a> {
    t: &'a [u8],
    p: usize,
    line: i32,
}

fn is_space(c: u8) -> bool { c == b' ' || c == b'\t' || c == b'\r' }

impl<'a> Reader<'a> {
    fn word(&mut self) -> Option<String> {
        let t = self.t;
        while self.p < t.len() && is_space(t[self.p]) { self.p += 1; }
        if self.p >= t.len() || t[self.p] == b'\n' { return None; }
        let s;
        let w;
        if t[self.p] == b'"' {
            self.p += 1;
            s = self.p;
            while self.p < t.len() && t[self.p] != b'"' && t[self.p] != b'\n' { self.p += 1; }
            w = String::from_utf8_lossy(&t[s..s + (self.p - s).min(63)]).into_owned();
            if self.p < t.len() && t[self.p] == b'"' { self.p += 1; }
            return Some(w);
        }
        s = self.p;
        while self.p < t.len() && !is_space(t[self.p]) && t[self.p] != b'\n' { self.p += 1; }
        Some(String::from_utf8_lossy(&t[s..s + (self.p - s).min(63)]).into_owned())
    }

    fn has_more(&mut self) -> bool {
        while self.p < self.t.len() && is_space(self.t[self.p]) { self.p += 1; }
        self.p < self.t.len() && self.t[self.p] != b'\n'
    }

    fn next_line(&mut self) {
        while self.p < self.t.len() && self.t[self.p] != b'\n' { self.p += 1; }
        if self.p < self.t.len() { self.p += 1; }
        self.line += 1;
    }

    fn fail(&self, msg: &str) -> String { format!("line {}: {}", self.line, msg) }

    /* il numero come lo accetta fx_parse di RISC OS: [+-]cifre[.cifre] */
    fn num(&mut self) -> Result<f64, String> {
        let w = self.word().ok_or_else(|| self.fail("number expected"))?;
        let b = w.as_bytes();
        let (mut i, mut digits, mut ip) = (0usize, 0, 0i64);
        if i < b.len() && (b[i] == b'-' || b[i] == b'+') { i += 1; }
        while i < b.len() && b[i].is_ascii_digit() {
            ip = ip * 10 + (b[i] - b'0') as i64;
            i += 1;
            digits += 1;
            if ip > 32767 { return Err(self.fail("number expected")); }
        }
        if i < b.len() && b[i] == b'.' {
            i += 1;
            while i < b.len() && b[i].is_ascii_digit() { i += 1; digits += 1; }
        }
        if i < b.len() || digits == 0 { return Err(self.fail("number expected")); }
        let v: f64 = w.parse().map_err(|_| self.fail("number expected"))?;
        Ok(if v == 0.0 { 0.0 } else { v })
    }

    fn inum(&mut self) -> Result<i32, String> { Ok(self.num()?.floor() as i32) }
    fn vec(&mut self) -> Result<V3, String> { let x = self.num()?; let y = self.num()?; let z = self.num()?; Ok(v3(x, y, z)) }
}

impl Scene {
    fn new() -> Scene {
        let mut lights = [Light::default(); MAX_LIGHTS];
        lights[0] = Light { dir: true, pos: v3(-0.5, 0.8, 0.6), color: v3(1.3, 1.15, 0.95), intensity: 1.0, shadow: true, softness: 8.0, on: true };
        Scene {
            objs: Vec::new(), roots: Vec::new(), lights, nlight: 1,
            cam: Camera { pos: v3(2.9, 2.3, 3.5), target: v3(0.0, 0.6, 0.0), focal: 2.2 },
            world: World::default(),
        }
    }

    fn add(&mut self, typ: i32, parent: Option<usize>, name: &str) -> usize {
        let mut par = [0.0; 10];
        par[..defaults(typ).len()].copy_from_slice(defaults(typ));
        let i = self.objs.len();
        self.objs.push(Obj {
            name: name.into(), typ, par, pos: V3::default(), rot: V3::default(), scl: v3(1.0, 1.0, 1.0),
            op: OP_UNION, k: 0.0, round: 0.0, shell: 0.0, mat: Material::default(), inherit_mat: false,
            flags: OF_WIRE | OF_RENDER, parent, kids: Vec::new(), index: 0,
        });
        match parent {
            Some(p) => self.objs[p].kids.push(i),
            None => self.roots.push(i),
        }
        i
    }

    /* tutti gli oggetti in ordine d'albero (padre, poi figli) */
    pub fn walk(&self, o: usize, out: &mut Vec<usize>) {
        out.push(o);
        for &k in &self.objs[o].kids { self.walk(k, out); }
    }

    pub fn walk_all(&self) -> Vec<usize> {
        let mut l = Vec::new();
        for &o in &self.roots { self.walk(o, &mut l); }
        l
    }

    pub fn parent_world(&self, p: Option<usize>) -> M34 {
        match p { None => IDENTITY, Some(p) => self.obj_world(p) }
    }

    pub fn obj_world(&self, o: usize) -> M34 {
        let ob = &self.objs[o];
        let pw = self.parent_world(ob.parent);
        let l = M34::compose(ob.pos, ob.rot, ob.scl);
        M34::mul(&pw, &l)
    }

    pub fn read(text: &str) -> Result<Scene, String> {
        let mut s = Scene::new();
        let mut r = Reader { t: text.as_bytes(), p: 0, line: 1 };
        let mut infile: Vec<usize> = Vec::new();
        let mut file_lights = false;
        let mut cur: Option<usize> = None;
        if r.word().as_deref() != Some("RayMarchStudio") { return Err("line 1: not a RayMarch Studio scene".into()); }
        r.next_line();
        while r.p < r.t.len() {
            let w = r.word();
            match w.as_deref() {
                Some("#gpu") => { s.read_gpu(&mut r, cur)?; r.next_line(); continue; }
                None => { r.next_line(); continue; }
                Some(x) if x.starts_with('#') => { r.next_line(); continue; }
                _ => {}
            }
            let w = w.unwrap();
            match w.as_str() {
                "camera" => { s.cam.pos = r.vec()?; s.cam.target = r.vec()?; s.cam.focal = r.num()?; }
                "light" => {
                    if !file_lights { s.nlight = 0; file_lights = true; }
                    if s.nlight >= MAX_LIGHTS { return Err(r.fail("too many lights")); }
                    let dir = r.word().as_deref() != Some("point");
                    let pos = r.vec()?; let color = r.vec()?; let intensity = r.num()?;
                    let shadow = r.num()? != 0.0; let softness = r.num()?;
                    s.lights[s.nlight] = Light { dir, pos, color, intensity, shadow, softness, on: true };
                    s.nlight += 1;
                }
                "snap" => { r.num()?; r.num()?; }
                "world" => { s.world.floor = r.num()? != 0.0; s.world.fog = r.num()?; s.world.ao = r.num()? != 0.0; }
                "object" => {
                    let parent = r.inum()?;
                    let tw = r.word().unwrap_or_default();
                    let typ = match tw.as_str() {
                        "group" => T_GROUP, "sphere" => T_SPHERE, "box" => T_BOX, "cylinder" => T_CYLINDER,
                        _ => return Err(r.fail(&format!("object type not supported by the Rust port: {}", tw))),
                    };
                    let op = match r.word().as_deref() { Some("subtract") => OP_SUB, Some("intersect") => OP_INTER, _ => OP_UNION };
                    if parent >= infile.len() as i32 { return Err(r.fail("bad parent")); }
                    let pa = if parent < 0 { None } else { Some(infile[parent as usize]) };
                    let c = s.add(typ, pa, if typ == T_GROUP { "Group" } else { "Object" });
                    infile.push(c);
                    cur = Some(c);
                    s.objs[c].op = op;
                    s.objs[c].k = r.num()?;
                    s.objs[c].flags = (r.inum()? as u32) & (OF_WIRE | OF_RENDER | OF_LOCK | OF_OFF);
                    if let Some(nw) = r.word() { s.objs[c].name = nw; }
                }
                _ => {
                    let c = cur.ok_or_else(|| r.fail("object expected"))?;
                    match w.as_str() {
                        "pos" => s.objs[c].pos = r.vec()?,
                        "rot" => s.objs[c].rot = r.vec()?,
                        "scale" => s.objs[c].scl = r.vec()?,
                        "material" => {
                            s.objs[c].mat.color = r.vec()?;
                            s.objs[c].mat.specular = r.num()?;
                            s.objs[c].mat.shininess = r.num()?;
                        }
                        "modify" => { s.objs[c].round = r.num()?; s.objs[c].shell = r.num()?; }
                        "param" => {
                            for j in 0..defaults(s.objs[c].typ).len() { s.objs[c].par[j] = r.num()?; }
                        }
                        _ => return Err(r.fail("unknown keyword")),
                    }
                }
            }
            r.next_line();
        }
        /* Off vale solo per i generatori, che qui non ci sono */
        for &o in &infile { s.objs[o].flags &= !OF_OFF; }
        Ok(s)
    }

    fn read_gpu(&mut self, r: &mut Reader, cur: Option<usize>) -> Result<(), String> {
        let w = r.word().unwrap_or_default();
        match w.as_str() {
            "material" => {
                let c = cur.ok_or_else(|| r.fail("object expected"))?;
                let m = &mut self.objs[c].mat;
                m.reflect = clamp(r.num()?, 0.0, 1.0);
                m.roughness = clamp(r.num()?, 0.0, 1.0);
                m.transparency = clamp(r.num()?, 0.0, 1.0);
                m.ior = clamp(r.num()?, 1.0, 3.0);
                m.metal = r.num()? != 0.0;
                if r.has_more() {
                    m.sheen = clamp(r.num()?, 0.0, 1.0);
                    let (sr, sg, sb) = (r.num()?, r.num()?, r.num()?);
                    m.sheen_color = v3(clamp(sr, 0.0, 1.0), clamp(sg, 0.0, 1.0), clamp(sb, 0.0, 1.0));
                    m.sheen_rough = clamp(r.num()?, 0.0, 1.0);
                }
            }
            "floor" => {
                let tw = r.word().unwrap_or_default();
                let wd = &mut self.world;
                wd.floor_type = match tw.as_str() { "solid" => FLOOR_SOLID, "grid" => FLOOR_GRID, _ => FLOOR_CHECKER };
                wd.floor_a = r.vec()?;
                wd.floor_b = r.vec()?;
                wd.floor_scale = clamp(r.num()?, 0.01, 100.0);
                wd.floor_reflect = clamp(r.num()?, 0.0, 1.0);
            }
            "env" => {
                let tw = r.word().unwrap_or_default();
                let wd = &mut self.world;
                wd.env = match tw.as_str() { "studio" => ENV_STUDIO, "hdri" => ENV_HDRI, _ => ENV_SKY };
                wd.env_preset = r.word().unwrap_or_default();
                wd.env_intensity = clamp(r.num()?, 0.0, 100.0);
                wd.env_rotation = r.num()?;
                wd.env_background = r.num()? != 0.0;
                wd.bg_color = r.vec()?;
                wd.env_lights = r.num()? != 0.0;
                r.word();
                if wd.env == ENV_HDRI { return Err(r.fail("HDRI environments are not supported by the Rust port")); }
            }
            "light" => {
                if self.nlight == 0 { return Err(r.fail("light expected")); }
                r.word();
                self.lights[self.nlight - 1].on = r.num()? != 0.0;
                self.lights[self.nlight - 1].intensity = r.num()?;
            }
            "matparent" => {
                let c = cur.ok_or_else(|| r.fail("object expected"))?;
                self.objs[c].inherit_mat = true;
            }
            "tone" => {
                self.world.tone_map = r.word().as_deref() == Some("neutral");
                self.world.exposure = clamp(r.num()?, -10.0, 10.0);
            }
            "displace" | "symmetry" | "profile" | "cable" | "cablepts" | "cablecurve" | "instance" | "cloner" | "deformer" => {
                return Err(r.fail(&format!("#gpu {} is not supported by the Rust port", w)));
            }
            _ => {}
        }
        Ok(())
    }
}
