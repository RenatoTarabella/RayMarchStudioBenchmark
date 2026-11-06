// La scena preparata per il calcolo (gscene.h, gscene.c, shapes.c, parti
// di wire.c): matrici, valori derivati, ingombri, materiali e il programma
// a pila di RISC OS (alberi di unioni con I_SKIP).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RmBench;

public static class I
{
    public const int Prim = 0, Op = 1, Mod = 2, Min = 3, Skip = 5, Lazy = 6;
}

public struct Ins { public int Code, Obj, Cull, N; }

/* il materiale come lo usa il renderer, mescolato nei raccordi */
public struct GMat
{
    public V3 Col;
    public double Spec, Shin, Dif, Sel;
    public double O0, O1, O2, O3;       /* riflesso, ruvidita', trasparenza, ior */
    public double Metal;
    public double S0, S1, S2, S3;       /* sheen: colore * quanto, ruvidita' delle fibre */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GMat Default() => new()
    {
        Col = new V3(0.75, 0.3, 0.15), Spec = 1, Shin = 24, Dif = 1, O3 = 1.5, S3 = 0.5
    };

    /* a + (b - a) * (1 - wa): wa = peso di a */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mix(ref GMat o, in GMat a, in GMat b, double wa)
    {
        GMat r;
        r.Col = V3.Lerp(b.Col, a.Col, wa);
        r.Spec = R.Mix(b.Spec, a.Spec, wa);
        r.Shin = R.Mix(b.Shin, a.Shin, wa);
        r.Dif = R.Mix(b.Dif, a.Dif, wa);
        r.Sel = R.Mix(b.Sel, a.Sel, wa);
        r.O0 = R.Mix(b.O0, a.O0, wa);
        r.O1 = R.Mix(b.O1, a.O1, wa);
        r.O2 = R.Mix(b.O2, a.O2, wa);
        r.O3 = R.Mix(b.O3, a.O3, wa);
        r.Metal = R.Mix(b.Metal, a.Metal, wa);
        r.S0 = R.Mix(b.S0, a.S0, wa);
        r.S1 = R.Mix(b.S1, a.S1, wa);
        r.S2 = R.Mix(b.S2, a.S2, wa);
        r.S3 = R.Mix(b.S3, a.S3, wa);
        o = r;
    }
}

public struct GObj
{
    public int Type, Op;
    public M34 Inv;
    public double P0;                   /* il raggio della sfera */
    public double C0, C1, C2, C3;       /* i valori derivati (prim_prepare) */
    public double DScale, K, Round, Shell;
    public GMat Mat;
    public V3 Bc;
    public double Br;                   /* < 0: niente */

    public double GetC(int i) => i == 0 ? C0 : i == 1 ? C1 : i == 2 ? C2 : C3;
    public void SetC(int i, double v) { if (i == 0) C0 = v; else if (i == 1) C1 = v; else if (i == 2) C2 = v; else C3 = v; }
}

/* i dati di preparazione che il caso caldo non usa */
public struct Prep { public M34 World; public bool Baked; }

public sealed class GScene
{
    Prep[] prep;
    public Obj[] Objs;
    public GObj[] Obj;
    public int NObj;
    public Ins[] Prog;
    public int NProg;
    public double[] Bound;              /* le sfere degli I_SKIP, 4 l'una */
    public int NBound, MaxStack;
    public V3 Bc;
    public double Br = -1;              /* < 0: niente da rendere */
    public bool Inexact, Cavity, HasGlass;

    readonly List<Ins> prog = new();
    readonly List<double> bound = new();

    // ---- i valori derivati e gli ingombri (shapes.c)

    static void PrimPrepare(int t, double[] p, ref GObj f)
    {
        f.C0 = f.C1 = f.C2 = f.C3 = 0;
        switch (t)
        {
            case T.Box:
            {
                double r = R.Min(p[3], R.Min(p[0] / 2, R.Min(p[1] / 2, p[2] / 2)));
                f.C0 = p[0] / 2 - r; f.C1 = p[1] / 2 - r; f.C2 = p[2] / 2 - r; f.C3 = r;
                break;
            }
            case T.Cylinder:
            {
                double b = R.Min(p[2], R.Min(p[0], p[1] / 2));
                f.C0 = p[0] - b; f.C1 = p[1] / 2 - b; f.C2 = b;
                break;
            }
        }
        f.P0 = p[0];
    }

    static double PrimBound(int t, double[] p) => t switch
    {
        T.Sphere => p[0],
        T.Box => Math.Sqrt(p[0] / 2 * (p[0] / 2) + p[1] / 2 * (p[1] / 2) + p[2] / 2 * (p[2] / 2)),
        T.Cylinder => R.Len2(p[0], p[1] / 2),
        _ => throw new NotSupportedException()
    };

    // ---- il wireframe delle forme (wire.c): solo i punti, per extend_cutters

    const int SEG = 24;
    static double Rad(double d) => d * R.PI / 180;

    static void Arc(List<V3> b, V3 c, V3 u, V3 v, int a0, int a1)
    {
        for (int a = a0; a <= a1; a += 360 / SEG)
            b.Add(c + (u * Math.Cos(Rad(a)) + v * Math.Sin(Rad(a))));
    }

    static void HCircle(List<V3> b, double y, double r) => Arc(b, new V3(0, y, 0), new V3(r, 0, 0), new V3(0, 0, r), 0, 360);

    static void Seg(List<V3> b, V3 a, V3 c) { b.Add(a); b.Add(c); }

    static void WireBuild(Obj o, List<V3> b)
    {
        var p = o.Par;
        switch (o.Type)
        {
            case T.Sphere:
                Arc(b, new V3(0, 0, 0), new V3(p[0], 0, 0), new V3(0, p[0], 0), 0, 360);
                Arc(b, new V3(0, 0, 0), new V3(0, p[0], 0), new V3(0, 0, p[0]), 0, 360);
                HCircle(b, 0, p[0]);
                break;
            case T.Box:
            {
                double hx = p[0] / 2, hy = p[1] / 2, hz = p[2] / 2;
                for (int i = 0; i < 4; i++)
                {
                    double sx = (i & 1) != 0 ? hx : -hx, sz = (i & 2) != 0 ? hz : -hz;
                    Seg(b, new V3(sx, -hy, sz), new V3(sx, hy, sz));
                }
                for (int s = -1; s <= 1; s += 2)
                {
                    double y = s * hy;
                    b.Add(new V3(-hx, y, -hz)); b.Add(new V3(hx, y, -hz)); b.Add(new V3(hx, y, hz));
                    b.Add(new V3(-hx, y, hz)); b.Add(new V3(-hx, y, -hz));
                }
                break;
            }
            case T.Cylinder:
            {
                double y0 = -p[1] / 2, y1 = p[1] / 2, r = p[0];
                HCircle(b, y0, r);
                HCircle(b, y1, r);
                Seg(b, new V3(r, y0, 0), new V3(r, y1, 0));
                Seg(b, new V3(-r, y0, 0), new V3(-r, y1, 0));
                Seg(b, new V3(0, y0, r), new V3(0, y1, r));
                Seg(b, new V3(0, y0, -r), new V3(0, y1, -r));
                break;
            }
        }
    }

    /* i punti del wireframe nel mondo (wire_world_lines senza clonatori ne' deformatori) */
    static void WorldPoints(Obj o, List<V3> out_)
    {
        var loc = new List<V3>();
        M34 w = Scene.ObjWorld(o);
        out_.Clear();
        WireBuild(o, loc);
        foreach (var q in loc) out_.Add(w.Point(q));
    }

    // ---- matrici e ingombri (update_list)

    static bool Visible(Obj o)
    {
        if ((o.Flags & OF.Render) == 0) return false;
        if (!o.IsContainer) return true;
        foreach (var k in o.Kids) if (Visible(k)) return true;
        return false;
    }

    static void Merge(ref V3 c, ref double r, V3 c2, double r2)
    {
        if (r < 0) { c = c2; r = r2; return; }
        double d = V3.Len(c2 - c);
        if (d + r2 <= r) return;
        if (d + r <= r2) { c = c2; r = r2; return; }
        double nr = (d + r + r2) / 2;
        c = c + (c2 - c) * ((nr - r) / d);
        r = nr;
    }

    void ListBound(List<Obj> list, ref V3 c, ref double r)
    {
        bool first = true;
        foreach (var o in list)
        {
            ref GObj f = ref Obj[o.Index];
            if ((o.Flags & OF.Render) == 0 || f.Br < 0) continue;
            if (first || o.Op == RmBench.Op.Union) Merge(ref c, ref r, f.Bc, f.Br);
            first = false;
        }
    }

    /* un box o un cilindro scalato non uniforme lungo i propri assi: la
     * stessa forma con le misure scalate (distanza esatta) */
    static bool TryBake(Obj o, ref GObj f, ref Prep pr)
    {
        if (o.Type != T.Box && o.Type != T.Cylinder) return false;
        ref M34 w = ref pr.World;
        var col = new V3[3];
        for (int i = 0; i < 3; i++) col[i] = new V3(w[i], w[4 + i], w[8 + i]);
        double c0 = V3.Len(col[0]), c1 = V3.Len(col[1]), c2 = V3.Len(col[2]);
        double cmax = R.Max(c0, R.Max(c1, c2));
        if (cmax <= 0 || R.Min(c0, R.Min(c1, c2)) <= 0) return false;
        double tol = 1e-9 * cmax;
        if (Math.Abs(c0 - c1) <= tol && Math.Abs(c1 - c2) <= tol) return false;
        if (Math.Abs(V3.Dot(col[0], col[1])) > tol * cmax || Math.Abs(V3.Dot(col[1], col[2])) > tol * cmax ||
            Math.Abs(V3.Dot(col[0], col[2])) > tol * cmax) return false;
        if (o.Type == T.Cylinder && Math.Abs(c0 - c2) > tol) return false;
        M34 u = M34.Rows(w[0] / c0, w[1] / c1, w[2] / c2, w[3],
                         w[4] / c0, w[5] / c1, w[6] / c2, w[7],
                         w[8] / c0, w[9] / c1, w[10] / c2, w[11]);
        if (!M34.Inverse(u, out M34 inv)) return false;
        double cmin = R.Min(c0, R.Min(c1, c2));
        var p = (double[])o.Par.Clone();
        if (o.Type == T.Box) { p[0] *= c0; p[1] *= c1; p[2] *= c2; p[3] *= cmin; }
        else { p[0] *= c0; p[1] *= c1; p[2] *= cmin; }
        f.Inv = inv;
        f.DScale = 1;
        PrimPrepare(o.Type, p, ref f);
        pr.Baked = true;
        return true;
    }

    void UpdateList(List<Obj> list, in M34 pw, double pscale)
    {
        foreach (var o in list)
        {
            ref GObj f = ref Obj[o.Index];
            ref Prep pr = ref prep[o.Index];
            double smin = R.Min(Math.Abs(o.Scl.X), R.Min(Math.Abs(o.Scl.Y), Math.Abs(o.Scl.Z)));
            M34 l = M34.Compose(o.Pos, o.Rot, o.Scl);
            pr.World = M34.Mul(pw, l);
            if (!M34.Inverse(pr.World, out f.Inv)) f.Inv = M34.Identity;
            f.DScale = pscale * smin;
            f.Br = -1;
            pr.Baked = false;
            if (!o.IsContainer)
            {
                PrimPrepare(o.Type, o.Par, ref f);
                TryBake(o, ref f, ref pr);
                f.Bc = pr.World.Trans;
                f.Br = PrimBound(o.Type, o.Par) * pr.World.MaxScale();
            }
            else
            {
                UpdateList(o.Kids, pr.World, f.DScale);
                ListBound(o.Kids, ref f.Bc, ref f.Br);
            }
            if (f.Br >= 0) f.Br += o.K + o.Round + o.Shell;
        }
    }

    /* Un box o un cilindro che scava o interseca: le facce che stanno tutte
     * oltre l'ingombro dei fratelli prima si spingono lontanissimo */
    void ExtendCutters(List<Obj> list)
    {
        var l = new List<V3>();
        for (int i = 0; i < list.Count; i++)
        {
            Obj o = list[i];
            if (o.IsContainer) { ExtendCutters(o.Kids); continue; }
            if (i == 0 || o.Op == RmBench.Op.Union || (o.Type != T.Box && o.Type != T.Cylinder) || !Visible(o)) continue;
            ref GObj f = ref Obj[o.Index];
            V3 lo = new(1e300, 1e300, 1e300), hi = new(-1e300, -1e300, -1e300);
            double margin = o.K;
            int n = 0;
            for (int j = 0; j < i; j++)
            {
                if (!Visible(list[j])) continue;
                var all = new List<Obj>();
                Scene.Walk(list[j], all);
                foreach (var d in all)
                {
                    margin = R.Max(margin, o.K + d.K + Math.Abs(d.Round) + d.Shell);
                    if (d.IsContainer || !Visible(d)) continue;
                    WorldPoints(d, l);
                    foreach (var q in l)
                    {
                        V3 pt = f.Inv.Point(q);
                        lo = new V3(R.Min(lo.X, pt.X), R.Min(lo.Y, pt.Y), R.Min(lo.Z, pt.Z));
                        hi = new V3(R.Max(hi.X, pt.X), R.Max(hi.Y, pt.Y), R.Max(hi.Z, pt.Z));
                        n++;
                    }
                }
            }
            if (n == 0) continue;
            double sc = f.Inv.MaxScale();
            double size = V3.Len(hi - lo);
            double m = margin * sc + 0.01 * size + 1e-6;
            int axes = o.Type == T.Box ? 3 : 1;
            var shift = new double[3];
            double far = 100 * R.Max(size, 1e-3);
            bool changed = false;
            for (int a = 0; a < axes; a++)
            {
                int k = o.Type == T.Box ? a : 1, ci = o.Type == T.Box ? a : 1;
                double r = o.Type == T.Box ? f.C3 : f.C2;
                double h = f.GetC(ci) + r, fLo = -h, fHi = h;
                if (hi.Get(k) + m < fHi) fHi = far + h;
                if (lo.Get(k) - m > fLo) fLo = -far - h;
                if (fHi == h && fLo == -h) continue;
                shift[k] = (fLo + fHi) / 2;
                f.SetC(ci, (fHi - fLo) / 2 - r);
                changed = true;
            }
            if (!changed) continue;
            M34 t = M34.Rows(1, 0, 0, -shift[0], 0, 1, 0, -shift[1], 0, 0, 1, -shift[2]);
            f.Inv = M34.Mul(t, f.Inv);
            f.Br = 1e9;
        }
    }

    // ---- il programma (compile_list, compile_obj, emit_union)

    int Emit(int code, int obj, int cull, int n)
    {
        prog.Add(new Ins { Code = code, Obj = obj, Cull = cull, N = n });
        return prog.Count - 1;
    }

    void SetN(int at)
    {
        var x = prog[at];
        x.N = prog.Count - at - 1;
        prog[at] = x;
    }

    void CompileObj(Obj o, bool on)
    {
        if (o.Type == T.Group) CompileList(o.Kids, on);
        else { Emit(I.Prim, o.Index, on ? 0 : 1, o.Type); return; }
        if (o.Round != 0 || o.Shell != 0) Emit(I.Mod, o.Index, 0, 0);
    }

    /* il valore come lo vede RISC OS (1/65536): le scelte dell'albero */
    static double Fxv(double v) => R.Round(v * 65536);

    void EmitUnion(Obj[] list, int at, int m)
    {
        if (m <= 2)
        {
            CompileObj(list[at], false);
            if (m == 2) { CompileObj(list[at + 1], false); Emit(I.Min, list[at + 1].Index, 0, 0); }
            return;
        }
        int si = Emit(I.Skip, bound.Count / 4, 0, 0);
        V3 c = new(0, 0, 0);
        double r = -1;
        var lo = new double[3];
        var hi = new double[3];
        for (int k = 0; k < m; k++)
        {
            ref GObj f = ref Obj[list[at + k].Index];
            Merge(ref c, ref r, f.Bc, f.Br);
            for (int j = 0; j < 3; j++)
            {
                double q = Fxv(f.Bc.Get(j));
                if (k == 0 || q < lo[j]) lo[j] = q;
                if (k == 0 || q > hi[j]) hi[j] = q;
            }
        }
        bound.Add(c.X); bound.Add(c.Y); bound.Add(c.Z); bound.Add(r);
        int ax = 0;
        if (hi[1] - lo[1] > hi[ax] - lo[ax]) ax = 1;
        if (hi[2] - lo[2] > hi[ax] - lo[ax]) ax = 2;
        for (int k = 1; k < m; k++)
        {
            Obj v = list[at + k];
            double key = Fxv(Obj[v.Index].Bc.Get(ax));
            int j = k;
            while (j > 0 && Fxv(Obj[list[at + j - 1].Index].Bc.Get(ax)) > key) { list[at + j] = list[at + j - 1]; j--; }
            list[at + j] = v;
        }
        int h = m / 2;
        EmitUnion(list, at, h);
        EmitUnion(list, at + h, m - h);
        Emit(I.Min, list[at + h].Index, 0, 0);
        SetN(si);
    }

    int CompileList(List<Obj> list, bool neg)
    {
        int n = 0, i = 0;
        while (i < list.Count)
        {
            Obj o = list[i];
            int li = -1;
            if (!Visible(o)) { i++; continue; }
            if (!neg && (n == 0 || (o.Op == RmBench.Op.Union && o.K == 0)))
            {
                var run = new List<Obj>();
                Obj first = o;
                bool ok = true;
                for (; i < list.Count; i++)
                {
                    Obj q = list[i];
                    if (!Visible(q)) continue;
                    if (run.Count > 0 && (q.Op != RmBench.Op.Union || q.K != 0)) break;
                    ok &= Obj[q.Index].Br >= 0;
                    run.Add(q);
                }
                if (ok) EmitUnion(run.ToArray(), 0, run.Count);
                else for (int k = 0; k < run.Count; k++) { CompileObj(run[k], false); if (k > 0) Emit(I.Min, run[k].Index, 0, 0); }
                if (n > 0) Emit(I.Min, first.Index, 0, 0);
                n++;
                continue;
            }
            bool on = neg ^ (n > 0 && o.Op == RmBench.Op.Sub);
            if (n > 0 && o.Op == RmBench.Op.Sub && Obj[o.Index].Br >= 0) li = Emit(I.Lazy, o.Index, 0, 0);
            CompileObj(o, on);
            if (n > 0) Emit(o.Op == RmBench.Op.Union && o.K == 0 ? I.Min : I.Op, o.Index, 0, 0);
            if (li >= 0) SetN(li);
            n++;
            i++;
        }
        return n;
    }

    // ---- i dati di ogni oggetto (fill)

    void Fill(Obj o)
    {
        ref GObj f = ref Obj[o.Index];
        ref Material m = ref o.Mat;
        double sh = R.Clamp(m.Sheen, 0, 1);
        if (sh > 0 && !o.IsContainer) throw new NotSupportedException("sheen is not supported by the .NET port");
        f.Type = o.Type;
        f.Op = o.Op;
        f.K = o.K;
        f.Round = o.Round;
        f.Shell = o.Shell;
        f.Mat.Col = m.Color;
        f.Mat.Spec = m.Specular;
        f.Mat.Shin = m.Shininess;
        f.Mat.Dif = m.Diffuse;
        f.Mat.Sel = 0;
        f.Mat.O0 = m.Reflect; f.Mat.O1 = m.Roughness; f.Mat.O2 = m.Transparency; f.Mat.O3 = m.Ior;
        f.Mat.Metal = m.Metal ? 1 : 0;
        f.Mat.S0 = m.SheenColor.X * sh; f.Mat.S1 = m.SheenColor.Y * sh; f.Mat.S2 = m.SheenColor.Z * sh;
        f.Mat.S3 = R.Clamp(m.SheenRough, 0.07, 1);
    }

    static bool Nonuniform(in M34 w)
    {
        double a = w.ColLen(0), b = w.ColLen(1), c = w.ColLen(2), m = R.Max(a, R.Max(b, c));
        return Math.Abs(a - b) > 1e-9 * m || Math.Abs(b - c) > 1e-9 * m;
    }

    /* i figli delle primitive salgono di un livello (Hierarchy.Flatten, lift) */
    static void Lift(List<Obj> list, Obj owner)
    {
        for (int i = 0; i < list.Count; i++)
        {
            Obj o = list[i];
            if (o.IsContainer) { Lift(o.Kids, o); continue; }
            if (o.Kids.Count == 0) continue;
            M34 m = M34.Compose(o.Pos, o.Rot, o.Scl);
            var kids = o.Kids;
            o.Kids = new List<Obj>();
            int at = i + 1;
            foreach (var c in kids)
            {
                M34 l = M34.Compose(c.Pos, c.Rot, c.Scl), t = M34.Mul(m, l);
                Decompose(t, out c.Pos, out c.Rot, out c.Scl);
                c.Parent = owner;
                list.Insert(at++, c);
            }
        }
    }

    static void Decompose(in M34 a, out V3 pos, out V3 rot, out V3 scl)
    {
        double d = 180 / R.PI;
        double s0 = a.ColLen(0), s1 = a.ColLen(1), s2 = a.ColLen(2);
        double r00 = a.M0 / s0, r10 = a.M4 / s0, r20 = a.M8 / s0;
        double r11 = a.M5 / s1, r21 = a.M9 / s1;
        double r12 = a.M6 / s2, r22 = a.M10 / s2;
        double ax, ay = Math.Asin(R.Clamp(-r20, -1, 1)), az;
        if (Math.Abs(Math.Cos(ay)) > 1e-9) { ax = Math.Atan2(r21, r22); az = Math.Atan2(r10, r00); }
        else { ax = Math.Atan2(-r12, r11); az = 0; }
        pos = a.Trans;
        rot = new V3(ax * d, ay * d, az * d);
        scl = new V3(s0, s1, s2);
    }

    /* la scena del file diventa quella del calcolo (la scena viene
     * modificata: il benchmark la legge apposta) */
    public static GScene Build(Scene s)
    {
        var g = new GScene();
        var all = s.WalkAll();
        foreach (var o in all)
            if (o.InheritMat)
            {
                Obj x = o;
                for (int n = 0; x.InheritMat && x.Parent != null && n < 64; n++) x = x.Parent;
                o.Mat = x.Mat;
            }
        foreach (var o in all) o.InheritMat = false;
        Lift(s.Roots, null);
        all = s.WalkAll();
        g.NObj = all.Count;
        g.Objs = all.ToArray();
        g.Obj = new GObj[g.NObj + 1];
        g.prep = new Prep[g.NObj + 1];
        for (int i = 0; i < g.NObj; i++) g.Objs[i].Index = i;
        g.UpdateList(s.Roots, M34.Identity, 1);
        g.ExtendCutters(s.Roots);
        g.CompileList(s.Roots, false);
        g.Br = -1;
        g.ListBound(s.Roots, ref g.Bc, ref g.Br);
        for (int i = 0; i < g.NObj; i++) g.Fill(g.Objs[i]);
        for (int i = 0; i < g.NObj; i++)
        {
            Obj o = g.Objs[i];
            ref GObj f = ref g.Obj[i];
            int vis = 0;
            if (o.Mat.Transparency > 0 && !o.Mat.Metal) g.HasGlass = true;
            if (!o.IsContainer && !g.prep[i].Baked && Nonuniform(g.prep[i].World)) g.Inexact = true;
            if (o.IsContainer && o.Shell > 0)
            {
                foreach (var k in o.Kids) if (Visible(k)) vis++;
                if (vis > 1) g.Cavity = true;
            }
        }
        /* i dati del caso caldo in memoria fissa: l'interprete li legge coi puntatori */
        g.Obj = Pin(g.Obj);
        g.Prog = Pin(g.prog.ToArray());
        g.NProg = g.Prog.Length;
        g.Bound = Pin(g.bound.Count > 0 ? g.bound.ToArray() : new double[4]);
        g.NBound = g.bound.Count / 4;
        g.MaxStack = Measure(g.Prog);
        if (g.MaxStack > 128) throw new NotSupportedException("scene too deep");
        return g;
    }

    static T[] Pin<T>(T[] a)
    {
        var p = GC.AllocateUninitializedArray<T>(a.Length, pinned: true);
        a.CopyTo(p, 0);
        return p;
    }

    /* profondita' massima della pila dei valori: l'interprete la usa senza controlli */
    static int Measure(Ins[] prog)
    {
        int sp = 0, mx = 0;
        foreach (var ins in prog)
        {
            if (ins.Code == I.Prim || ins.Code == I.Skip) sp++;
            else if (ins.Code == I.Min || ins.Code == I.Op) sp--;
            if (sp > mx) mx = sp;
        }
        return mx;
    }
}
