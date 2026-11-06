// Il rendering (render.c): raymarching nella sfera d'ingombro, pavimento
// esatto, luci con ombra morbida, speculare, ambiente con l'occlusione,
// nebbia, riflessi e vetro, tone mapping. Solo la vista finale con un
// campione per pixel, come nel benchmark (niente vista solida, niente sheen).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RmBench;

public sealed class Render
{
    const double EPS_N = 0.002, HARD = 60.0;
    const int MAXSTEP = 256;
    const int RQ_SHADOWS = 1, RQ_AO = 2, RQ_SPEC = 4, RQ_ALL = 7;

    public readonly Scene S;
    public readonly GScene G;
    readonly World w;
    public int Wd, Ht;
    V3 ro, fw, rt, up;
    double focal;
    int quality = RQ_ALL, bounces = 6, sample = 0;
    int nl;
    readonly V3[] lpos = new V3[Scene.MaxLights], lcol = new V3[Scene.MaxLights];
    readonly double[] lkind = new double[Scene.MaxLights], lsoft = new double[Scene.MaxLights];
    readonly bool[] lshadow = new bool[Scene.MaxLights];
    readonly EnvMap env;
    double envCos, envSin, envGain, envLevels;
    readonly V3[] sh = new V3[9];
    /* i materiali misti anche dentro il vetro (le scene caricate da file; la
     * scena del benchmark resta col calcolo di RISC OS e della versione SGI) */
    public bool Mix;

    /* l'elemento i senza controllo dei limiti: solo dove i e' garantito
     * (i < nl <= MaxLights, i coefficienti 0..8, un oggetto dato da PickObj) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref T At<T>(T[] a, int i) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), i);

    public Render(Scene s, GScene g, int width, int height)
    {
        S = s; G = g; w = s.World;
        Wd = width; Ht = height;
        fw = V3.Norm(s.Cam.Target - s.Cam.Pos);
        rt = V3.Norm(new V3(-fw.Z, 0, fw.X));
        up = V3.Cross(rt, fw);
        ro = s.Cam.Pos;
        focal = s.Cam.Focal;
        for (int i = 0; i < s.NLight && nl < Scene.MaxLights; i++)
        {
            ref Light l = ref s.Lights[i];
            if (!l.On) continue;
            lpos[nl] = l.Dir ? V3.Norm(l.Pos) : l.Pos;
            lkind[nl] = l.Dir ? 0 : 1;
            lcol[nl] = l.Color * l.Intensity;
            lshadow[nl] = l.Shadow;
            lsoft[nl] = l.Softness;
            nl++;
        }
        env = EnvMap.Of(w);
        if (env != null)
        {
            double a = w.EnvRotation * R.PI / 180;
            var dir = new V3[EnvMap.MaxLights]; var col = new V3[EnvMap.MaxLights];
            var soft = new double[EnvMap.MaxLights];
            envCos = Math.Cos(a); envSin = Math.Sin(a);
            envGain = w.EnvIntensity;
            envLevels = env.NLev - 1;
            env.Irradiance(w.EnvRotation, w.EnvIntensity, w.EnvLights, sh);
            /* le sorgenti forti dell'ambiente come luci (tipo 2), dopo quelle della scena */
            if (w.EnvLights)
            {
                int n = env.Lights(w.EnvRotation, w.EnvIntensity, dir, col, soft);
                for (int k = 0; k < n && nl < Scene.MaxLights; k++)
                {
                    lpos[nl] = dir[k];
                    lkind[nl] = 2;
                    lcol[nl] = col[k];
                    lshadow[nl] = true;
                    lsoft[nl] = soft[k];
                    nl++;
                }
            }
        }
    }

    /* l'ambiente nella direzione d (lineare); lod: sfocato per la ruvidita' */
    V3 EnvLin(V3 d, double lod)
    {
        V3 q = new(envCos * d.X - envSin * d.Z, d.Y, envSin * d.X + envCos * d.Z);
        return env.Sample(q, lod) * envGain;
    }

    /* l'irradianza dell'ambiente / pi verso la normale n */
    V3 EnvIrradiance(V3 n)
    {
        V3[] c = sh;
        V3 v = At(c, 0) * 0.282095;
        v = v + ((At(c, 1) * n.Y + At(c, 2) * n.Z) + At(c, 3) * n.X) * 0.488603;
        v = v + ((At(c, 4) * (n.X * n.Y) + At(c, 5) * (n.Y * n.Z)) + At(c, 7) * (n.X * n.Z)) * 1.092548;
        v = v + (At(c, 6) * (0.315392 * (3 * n.Z * n.Z - 1)) + At(c, 8) * (0.546274 * (n.X * n.X - n.Y * n.Y)));
        return new V3(R.Max(v.X, 0), R.Max(v.Y, 0), R.Max(v.Z, 0));
    }

    /* il raggio o + d*t e la sfera (c, r): t d'entrata e d'uscita */
    static bool HitSphere(V3 o, V3 d, V3 c, double r, out double t0, out double t1)
    {
        V3 oc = o - c;
        double b = V3.Dot(oc, d), q = V3.Len(oc - d * b);
        t0 = t1 = 0;
        if (q >= r) return false;
        double hh = Math.Sqrt((r - q) * (r + q));
        t0 = -b - hh;
        t1 = -b + hh;
        return t1 > 0;
    }

    /* numeri a caso (PCG) */
    static double Rnd(Ctx c)
    {
        c.Rng = c.Rng * 747796405u + 2891336453u;
        uint x = ((c.Rng >> (int)((c.Rng >> 28) + 4u)) ^ c.Rng) * 277803737u;
        return ((x >> 22) ^ x) / 4294967296.0;
    }

    double Shadow(Ctx c, V3 p, V3 l, double tmax, double k)
    {
        double res = 1;
        if (G.Br < 0 || !HitSphere(p, l, G.Bc, G.Br + 0.3, out double t0, out double t1)) return 1;
        double t = R.Max(t0, 0.02);
        tmax = R.Min(tmax, t1);
        double st = G.Cavity ? 0.5 : 1, sm = G.Cavity ? 0.01 : 0.02;
        int nmax = G.Cavity ? 256 : 128;
        for (int n = 0; n < nmax && t < tmax; n++)
        {
            double h = c.MapD(p + l * t) * st;
            if (k * h < res * t)
            {
                res = k * h / t;
                if (res < 0.01) break;
            }
            t += R.Max(h, sm);
        }
        res = R.Clamp(res, 0, 1);
        return res * res * (3 - 2 * res);
    }

    static V3 VPow(V3 a, double e) => new(Math.Pow(a.X, e), Math.Pow(a.Y, e), Math.Pow(a.Z, e));

    /* materiali misti: quanto passa in un tratto lungo len del materiale m (il
     * colore per la trasparenza: dove il vetro sfuma nell'opaco assorbe di piu') */
    static V3 MixAbsorb(in GMat m, double len)
    {
        double a = R.Clamp(m.O2, 0, 1);
        return VPow(new V3(R.Max(m.Col.X * a, 0.001), R.Max(m.Col.Y * a, 0.001), R.Max(m.Col.Z * a, 0.001)), len);
    }

    V3 ShadowGlass(Ctx c, V3 p, V3 l, double tmax, double k)
    {
        double res = 1;
        V3 tr = new(1, 1, 1);
        if (G.Br < 0 || !HitSphere(p, l, G.Bc, G.Br + 0.3, out double t0, out double t1)) return tr;
        double t = R.Max(t0, 0.02);
        tmax = R.Min(tmax, t1);
        for (int n = 0; n < 160 && t < tmax; n++)
        {
            double h = c.MapD(p + l * t);
            if (k * h < res * t)
            {
                int o = -1;
                double a = 0;
                GMat mm = default;
                if (Mix)
                {
                    /* materiali misti: il materiale mescolato in questo punto */
                    c.MapM(p + l * t, out mm);
                    a = mm.Metal > 0.5 ? 0 : R.Clamp(mm.O2, 0, 1);
                }
                else
                {
                    o = c.PickObj(p + l * t);
                    if (o >= 0)
                    {
                        ref GMat m = ref At(G.Obj, o).Mat;
                        a = m.Metal > 0.5 ? 0 : R.Clamp(m.O2, 0, 1);
                    }
                }
                if (a <= 0)
                {
                    res = k * h / t;
                    if (res < 0.01) break;
                }
                else if (h < 0.001 && Mix)
                {
                    /* l'assorbimento tratto per tratto col materiale di ogni punto */
                    V3 ta = MixAbsorb(mm, 0.003);
                    t += 0.003;
                    for (int i = 0; i < 96 && t < tmax; i++)
                    {
                        V3 q = p + l * t;
                        double hi = c.MapD(q);
                        if (hi > 0.0005) break;
                        double st = R.Max(-hi, 0.004);
                        c.MapM(q, out GMat mi);
                        ta = ta * MixAbsorb(mi, st);
                        t += st;
                    }
                    tr = tr * (ta * a);
                    if (R.Max(tr.X, R.Max(tr.Y, tr.Z)) < 0.01) { res = 0; break; }
                    t += 0.02;
                    continue;
                }
                else if (h < 0.001)
                {
                    double te = t;
                    V3 col = At(G.Obj, o).Mat.Col;
                    t += 0.003;
                    for (int i = 0; i < 96 && t < tmax; i++)
                    {
                        double hi = c.MapD(p + l * t);
                        if (hi > 0.0005) break;
                        t += R.Max(-hi, 0.004);
                    }
                    tr = tr * (VPow(new V3(R.Max(col.X, 0.001), R.Max(col.Y, 0.001), R.Max(col.Z, 0.001)), t - te) * a);
                    if (R.Max(tr.X, R.Max(tr.Y, tr.Z)) < 0.01) { res = 0; break; }
                    t += 0.02;
                    continue;
                }
            }
            t += G.Cavity ? R.Max(h * 0.5, 0.01) : R.Max(h, 0.02);
        }
        res = R.Clamp(res, 0, 1);
        return tr * (res * res * (3 - 2 * res));
    }

    double AmbientOcclusion(Ctx c, V3 p, V3 n)
    {
        double occ = 0, wt = 1;
        for (int i = 0; i < 5; i++)
        {
            double e = 0.14 + i * 0.12;
            V3 q = p + n * e;
            double d = c.MapD(q);
            if (w.Floor) d = R.Min(d, q.Y);
            occ += (e - d) * wt;
            wt *= 0.85;
        }
        return R.Clamp(1 - occ * 2.5, 0, 1);
    }

    V3 LightDir(Ctx c, V3 l, double soft)
    {
        double th = 0.5 / R.Max(soft, 0.5);
        V3 t = V3.Norm(Math.Abs(l.Y) < 0.9 ? V3.Cross(l, new V3(0, 1, 0)) : V3.Cross(l, new V3(1, 0, 0))), b = V3.Cross(l, t);
        double cc = 1 - Rnd(c) * (1 - Math.Cos(th));
        double s = Math.Sqrt(R.Max(1 - cc * cc, 0));
        double a = R.TP * Rnd(c);
        return V3.Norm(t * (s * Math.Cos(a)) + b * (s * Math.Sin(a)) + l * cc);
    }

    /* con l'ambiente: quanto cielo vede il punto */
    double EnvVisibility(Ctx c, V3 p, V3 n)
    {
        V3 t = V3.Norm(Math.Abs(n.Y) < 0.9 ? V3.Cross(n, new V3(0, 1, 0)) : V3.Cross(n, new V3(1, 0, 0))), b = V3.Cross(n, t);
        double occ = 0, wgt = 1, tot = 0;
        if (sample > 0)
        {
            double r1 = Rnd(c), a = R.TP * Rnd(c), rr = Math.Sqrt(r1);
            V3 d = V3.Norm(t * (rr * Math.Cos(a)) + b * (rr * Math.Sin(a)) + n * Math.Sqrt(R.Max(1 - r1, 0)));
            return Shadow(c, p + n * 0.002, d, 1e3, HARD);
        }
        double sc = R.Max(G.Br, 0.01) * 0.35;
        for (int i = 0; i < 6; i++)
        {
            double h = sc * (0.04 + 0.19 * i), d = c.MapD(p + n * h);
            if (w.Floor) d = R.Min(d, p.Y + n.Y * h);
            occ += R.Max(h - d, 0) * wgt;
            tot += h * wgt;
            wgt *= 0.75;
        }
        return R.Clamp(1 - 1.3 * occ / tot, 0, 1);
    }

    /* la luce diretta: la parte diffusa e la speculare a parte */
    void Shade(Ctx c, V3 p, V3 n, V3 d, in GMat m, bool isFloor, double wd, out V3 acc, out V3 spec)
    {
        V3 alb = m.Col * m.Dif, shc = isFloor ? new V3(0, 0, 0) : new V3(m.S0, m.S1, m.S2);
        double smax = R.Max(shc.X, R.Max(shc.Y, shc.Z));
        double eV = 0, occ = 1, e;
        acc = spec = new V3(0, 0, 0);
        if (env == null && (quality & RQ_AO) != 0 && w.Ao && G.Br >= 0 && V3.Len(p - G.Bc) < G.Br + 0.8) occ = AmbientOcclusion(c, p, n);
        for (int i = 0; i < nl; i++)
        {
            V3 ld, lc = At(lcol, i), shv = new(1, 1, 1);
            double tmax, dif;
            double kind = At(lkind, i);
            bool fromEnv = kind > 1.5;
            if (kind < 0.5 || fromEnv) { ld = At(lpos, i); tmax = 100; }
            else { V3 v = At(lpos, i) - p; tmax = V3.Len(v); ld = v * (1 / tmax); }
            dif = V3.Dot(n, ld);
            if (dif <= 0) continue;
            if ((quality & RQ_SHADOWS) != 0 && At(lshadow, i))
            {
                V3 sd = sample > 0 ? LightDir(c, ld, At(lsoft, i)) : ld;
                double k = sample > 0 ? HARD : At(lsoft, i);
                if (G.HasGlass) shv = ShadowGlass(c, p, sd, tmax, k);
                else dif *= Shadow(c, p, sd, tmax, k);
            }
            acc = acc + alb * lc * shv * dif;
            /* chi riflette vede gia' la sorgente dell'ambiente nel riflesso: niente Blinn in piu' */
            if ((quality & RQ_SPEC) != 0 && !isFloor && m.Spec > 0 &&
                !(fromEnv && (m.O0 > 0 || m.O2 > 0 || m.Metal > 0.5)))
            {
                V3 h = V3.Norm(ld - d);
                double sp = Math.Pow(R.Max(V3.Dot(n, h), 0), Math.Floor(m.Shin)) * m.Spec * dif;
                spec = spec + lc * shv * sp;
            }
        }
        if (env != null)
        {
            double vis = wd > 0 && (quality & RQ_AO) != 0 && w.Ao ? EnvVisibility(c, p, n) : 1;
            acc = acc + (alb * (1 - smax * eV) + shc * eV) * EnvIrradiance(n) * vis;
            return;
        }
        e = (0.5 + n.Y * 0.5) * occ;
        acc = acc + (alb * (1 - smax * eV) + shc * eV) * (w.SkyLight * e + w.Ambient * occ);
    }

    static V3 ToneNeutral(V3 col)
    {
        const double start = 0.8 - 0.04, desat = 0.15, dd = 1 - start;
        double x = R.Min(col.X, R.Min(col.Y, col.Z)), offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
        col = col - new V3(offset, offset, offset);
        double peak = R.Max(col.X, R.Max(col.Y, col.Z));
        if (peak < start) return col;
        double np = 1 - dd * dd / (peak + dd - start);
        col = col * (np / peak);
        double gg = 1 - 1 / (desat * (peak - np) + 1);
        return V3.Lerp(col, new V3(np, np, np), gg);
    }

    V3 Tone(V3 col)
    {
        col = col * Math.Pow(2, w.Exposure);
        return w.ToneMap ? ToneNeutral(col) : col;
    }

    static uint Pack(V3 col, bool gamma)
    {
        static uint B(double v, bool gamma)
        {
            double x = gamma ? Math.Sqrt(v) : v;
            int b = (int)Math.Floor(x * 255);
            return (uint)(b > 255 ? 255 : b);
        }
        return B(R.Clamp(col.X, 0, 1), gamma) << 16 | B(R.Clamp(col.Y, 0, 1), gamma) << 8 | B(R.Clamp(col.Z, 0, 1), gamma);
    }

    static V3 NormalAt(Ctx c, V3 p, double en)
    {
        V3 n = new(0, 0, 0);
        for (int i = 0; i < 4; i++)
        {
            V3 e = new(2.0 * (((i + 3) >> 1) & 1) - 1, 2.0 * ((i >> 1) & 1) - 1, 2.0 * (i & 1) - 1);
            n = n + e * c.MapD(p + e * en);
        }
        return V3.Norm(n);
    }

    double ConfirmHit(Ctx c, V3 ro, V3 d, ref double t, double h, double eps)
    {
        double tt = t;
        if (!G.Inexact) return t;
        for (int k = 0; k < 64; k++)
        {
            if (h < eps * 0.02) return t;
            tt += h;
            h = c.MapD(ro + d * tt);
            if (h > eps) { t = tt; return 0; }
        }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    double HitEps(double t) => t / (focal * Ht) + 0.000004;

    double MarchObjects(Ctx c, V3 ro, V3 d, double tmax)
    {
        if (G.Br < 0 || !HitSphere(ro, d, G.Bc, G.Br, out double t0, out double t1)) return -1;
        tmax = R.Min(t1, tmax);
        double t = R.Max(t0, 0);
        for (int i = 0; i < MAXSTEP && t < tmax; i++)
        {
            double h = c.MapD(ro + d * t), eps = HitEps(t);
            if (h < eps)
            {
                double th = ConfirmHit(c, ro, d, ref t, h, eps);
                if (th > 0) return th;
                continue;
            }
            t += h;
        }
        return -1;
    }

    V3 Sky(V3 d)
    {
        double k = R.Clamp(1 - d.Y, 0, 1);
        return w.SkyTop + (w.SkyHorizon - w.SkyTop) * k;
    }

    V3 FloorColour(V3 p, double t)
    {
        double cx = p.X * w.FloorScale, cz = p.Z * w.FloorScale;
        if (w.FloorType == Floor.Solid) return w.FloorA;
        if (w.FloorType == Floor.Grid)
        {
            double dx = Math.Abs(R.Fract(cx + 0.5) - 0.5) / w.FloorScale, dz = Math.Abs(R.Fract(cz + 0.5) - 0.5) / w.FloorScale;
            double width = 0.012 + t * 0.0012;
            return R.Min(dx, dz) < width ? w.FloorB : w.FloorA;
        }
        double s = Math.Floor(cx) + Math.Floor(cz);
        return s - 2 * Math.Floor(s / 2) > 0.5 ? w.FloorA : w.FloorB;
    }

    V3 SkyLin(V3 d)
    {
        if (env != null) return EnvLin(d, 0);
        V3 c = Sky(d);
        return c * c;
    }

    static V3 RndDir(Ctx c)
    {
        double z = Rnd(c) * 2 - 1, a = Rnd(c) * R.TP, rr = Math.Sqrt(R.Max(1 - z * z, 0));
        return new V3(rr * Math.Cos(a), z, rr * Math.Sin(a));
    }

    V3 Roughen(Ctx c, V3 d, V3 n, double rough, double s)
    {
        if (sample == 0 || rough <= 0) return d;
        V3 q = V3.Norm(d + RndDir(c) * rough);
        return V3.Dot(q, n) * s > 0 ? q : d;
    }

    static double MarchInside(Ctx c, V3 ro, V3 d)
    {
        double t = 0;
        for (int i = 0; i < MAXSTEP; i++)
        {
            double h = -c.MapD(ro + d * t);
            if (h < 0.0002) break;
            t += h;
        }
        return t;
    }

    /* come MarchInside, con i materiali misti: thr assorbe tratto per tratto
     * col materiale mescolato di ogni punto */
    static double MarchInsideMix(Ctx c, V3 ro, V3 d, ref V3 thr)
    {
        double t = 0;
        for (int i = 0; i < MAXSTEP; i++)
        {
            V3 p = ro + d * t;
            double h = -c.MapD(p);
            if (h < 0.0002) break;
            c.MapM(p, out GMat m);
            thr = thr * MixAbsorb(m, h);
            t += h;
        }
        return t;
    }

    double MarchScene(Ctx c, V3 ro, V3 d, out bool onFloor)
    {
        double tf = -1;
        if (w.Floor && d.Y < -0.001 && ro.Y > 0) tf = ro.Y / -d.Y;
        double t = MarchObjects(c, ro, d, tf >= 0 ? tf : 1e9);
        onFloor = t < 0 && tf >= 0;
        return onFloor ? tf : t;
    }

    static V3 Reflect(V3 d, V3 n) => d - n * (2 * V3.Dot(n, d));

    /* GLSL refract: (0,0,0) nella riflessione totale */
    static V3 Refract(V3 i, V3 n, double eta)
    {
        double ni = V3.Dot(n, i), k = 1 - eta * eta * (1 - ni * ni);
        if (k < 0) return new V3(0, 0, 0);
        return i * eta - n * (eta * ni + Math.Sqrt(k));
    }

    uint RenderRay(Ctx c, V3 ro, V3 d)
    {
        double t = MarchScene(c, ro, d, out bool onFloor), lod = 0;
        V3 col = new(0, 0, 0), thr = new(1, 1, 1);
        if (t < 0)
        {
            if (env != null) return w.EnvBackground ? Pack(Tone(EnvLin(d, 0)), true) : Pack(w.BgColor, false);
            return Pack(Sky(d), false);
        }
        double tFirst = t;
        for (int b = 0; b < 16; b++)
        {
            V3 p = ro + d * t, n, tint = new(1, 1, 1), rd;
            GMat m;
            double wt = 0;
            if (!onFloor)
            {
                n = NormalAt(c, p, EPS_N);
                c.MapM(p, out m);
            }
            else
            {
                n = new V3(0, 1, 0);
                m = GMat.Default();
                m.Col = FloorColour(p, t);
                m.Spec = 0; m.Shin = 1; m.Dif = 1; m.Sel = 0;
                m.O0 = w.FloorReflect; m.O1 = 0; m.O2 = 0; m.O3 = 1;
                m.Metal = 0;
                m.S0 = m.S1 = m.S2 = 0; m.S3 = 0.5;
            }
            double wr = R.Clamp(m.O0, 0, 1), tr = R.Clamp(m.O2, 0, 1);
            double ior = R.Max(m.O3, 1), rough = R.Clamp(m.O1, 0, 1);
            if (m.Metal > 0.5) tint = m.Col;
            else if (!onFloor && (wr > 0 || tr > 0))
            {
                double f0 = (ior - 1) / (ior + 1);
                f0 *= f0;
                double cc = R.Clamp(1 + V3.Dot(d, n), 0, 1), c2 = cc * cc;
                double fr = f0 + (1 - f0) * c2 * c2 * cc;
                wr += (1 - wr) * fr;
                wt = tr * (1 - wr);
            }
            double wd = R.Max(1 - wr - wt, 0);
            Shade(c, p, n, d, m, onFloor, wd, out V3 dif, out V3 spec);
            col = col + thr * (dif * wd + spec * tint);
            if (wr + wt <= 0) break;
            rd = Roughen(c, Reflect(d, n), n, rough, 1);
            bool through = wt > 0;
            if (wr > 0 && wt > 0)
            {
                if (sample > 0)
                {
                    through = Rnd(c) * (wr + wt) < wt;
                    thr = thr * (wr + wt);
                    if (!through) thr = thr * tint;
                }
                else
                {
                    through = wt >= wr;
                    if (through) { col = col + thr * tint * SkyLin(rd) * wr; thr = thr * wt; }
                    else thr = thr * tint * wr;
                }
            }
            else thr = through ? thr * wt : thr * tint * wr;
            if (through)
            {
                V3 q = p - n * (R.Max(c.MapD(p), 0) + 0.003);
                V3 id = Roughen(c, Refract(d, n, 1 / ior), n, rough, -1);
                V3 absorb = new(R.Max(m.Col.X, 0.001), R.Max(m.Col.Y, 0.001), R.Max(m.Col.Z, 0.001));
                bool out_ = false;
                for (int k = 0; k < bounces + 2; k++)
                {
                    double ti = Mix ? MarchInsideMix(c, q, id, ref thr) : MarchInside(c, q, id);
                    V3 e = q + id * ti, ne = NormalAt(c, e, EPS_N);
                    if (!Mix) thr = thr * VPow(absorb, ti);
                    V3 od = Refract(id, -ne, ior);
                    if (V3.Dot(od, od) > 0) { ro = e + ne * 0.003; d = od; out_ = true; break; }
                    id = Reflect(id, ne);
                    q = e - ne * 0.003;
                }
                if (!out_) break;
            }
            else
            {
                ro = p + n * 0.003;
                d = rd;
            }
            if (env != null && sample == 0) lod = rough * envLevels * 0.7;
            if (b >= bounces || R.Max(thr.X, R.Max(thr.Y, thr.Z)) < 0.02)
            {
                col = col + thr * (env != null ? EnvLin(d, lod) : SkyLin(d));
                break;
            }
            t = MarchScene(c, ro, d, out onFloor);
            if (t < 0) { col = col + thr * (env != null ? EnvLin(d, lod) : SkyLin(d)); break; }
        }
        tFirst = R.Min(tFirst, 40);
        double f = tFirst * tFirst * w.Fog;
        f = f / (f + 1);
        col = col + (w.FogColor - col) * f;
        return Pack(Tone(col), true);
    }

    /* il colore del pixel (x, y), y dall'alto: 0x00RRGGBB */
    public uint Pixel(Ctx c, int x, int y)
    {
        /* u da -w/h a w/h, v da -1 (in basso) a 1, al centro del pixel */
        double u = (2 * (x + 0.5) - Wd) / Ht, v = (2 * (Ht - 1 - y + 0.5) - Ht) / Ht;
        V3 rd = V3.Norm(fw * focal + rt * u + up * v);
        c.Rng = (uint)x * 1973u + (uint)(Ht - 1 - y) * 9277u + (uint)sample * 26699u;
        Rnd(c);
        return RenderRay(c, ro, rd);
    }
}
