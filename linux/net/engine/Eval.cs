// L'interprete del programma a pila (eval.c) e le primitive (sdf.c).
using System;
using System.Runtime.CompilerServices;

namespace RmBench;

/* lo stato di un calcolo (uno per thread). I dati della scena sono in
 * memoria fissa (GScene.Pin): qui i puntatori, senza controlli dei limiti
 * (la pila non supera MaxStack, verificato quando si prepara la scena) */
public sealed unsafe class Ctx
{
    public const double FAR_D = 1000.0, CULL_D = 1.0;
    const int MAXST = 128;

    public readonly GScene G;
    public uint Rng = 1;

    readonly Ins* prog;
    readonly GObj* obj;
    readonly double* bound;
    readonly int nprog;

    public Ctx(GScene g)
    {
        G = g;
        prog = (Ins*)Unsafe.AsPointer(ref g.Prog[0]);
        obj = (GObj*)Unsafe.AsPointer(ref g.Obj[0]);
        bound = (double*)Unsafe.AsPointer(ref g.Bound[0]);
        nprog = g.NProg;
    }

    // ---- le primitive, nello spazio locale

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double SdBox(in GObj f, V3 p)
    {
        double qx = Math.Abs(p.X) - f.C0, qy = Math.Abs(p.Y) - f.C1, qz = Math.Abs(p.Z) - f.C2;
        return V3.Len(new V3(R.Max(qx, 0), R.Max(qy, 0), R.Max(qz, 0))) + R.Min(R.Max(qx, R.Max(qy, qz)), 0) - f.C3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double SdCylinder(in GObj f, V3 p)
    {
        double dx = R.Len2(p.X, p.Z) - f.C0, dy = Math.Abs(p.Y) - f.C1;
        return R.Min(R.Max(dx, dy), 0) + R.Len2(R.Max(dx, 0), R.Max(dy, 0)) - f.C2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Modify(in GObj f, double d)
    {
        d -= f.Round;
        return f.Shell > 0 ? Math.Abs(d) - f.Shell : d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double PrimDist(in GObj f, V3 p)
    {
        V3 q = f.Inv.Point(p);
        double d = f.Type switch
        {
            T.Sphere => V3.Len(q) - f.P0,
            T.Box => SdBox(f, q),
            _ => SdCylinder(f, q)
        };
        d *= f.DScale;
        return Modify(f, d);
    }

    /* distanza per difetto dalla sfera d'ingombro: il componente maggiore */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double CullD(in GObj f, V3 p) => V3.MaxC(V3.Abs(p - f.Bc)) - f.Br;

    /* l'operazione dell'oggetto: a = i fratelli precedenti, b = lui */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Combine(in GObj f, double a, double b, out double wa)
    {
        double k = f.K, h;
        if (f.Op == Op.Sub)
        {
            if (k <= 0) { wa = a > -b ? 1 : 0; return R.Max(a, -b); }
            h = R.Clamp(0.5 - (b + a) * 0.5 / k, 0, 1);
            wa = 1 - h;
            return a + (-b - a) * h + k * h * (1 - h);
        }
        if (f.Op == Op.Inter)
        {
            if (k <= 0) { wa = a > b ? 1 : 0; return R.Max(a, b); }
            h = R.Clamp(0.5 - (b - a) * 0.5 / k, 0, 1);
            wa = h;
            return b + (a - b) * h + k * h * (1 - h);
        }
        if (k <= 0) { wa = a < b ? 1 : 0; return R.Min(a, b); }
        h = R.Clamp(0.5 + (b - a) * 0.5 / k, 0, 1);
        wa = h;
        return b + (a - b) * h - k * h * (1 - h);
    }

    /* la distanza (con le scorciatoie delle sfere): il caso caldo, senza materiali */
    [SkipLocalsInit]
    public double MapD(V3 p)
    {
        double* st = stackalloc double[MAXST];
        int sp = 0, pc = 0, end = nprog;
        Ins* pr = prog;
        GObj* ob = obj;
        while (pc < end)
        {
            Ins* ins = pr + pc++;
            switch (ins->Code)
            {
                case I.Min:
                    sp--;
                    st[sp - 1] = R.Min(st[sp - 1], st[sp]);
                    break;
                case I.Skip:
                {
                    double* b = bound + ins->Obj * 4;
                    double db = V3.MaxC(V3.Abs(p - new V3(b[0], b[1], b[2]))) - b[3];
                    if (db > CULL_D) { st[sp++] = db; pc += ins->N; }
                    break;
                }
                case I.Lazy:
                {
                    GObj* f = ob + ins->Obj;
                    if (CullD(*f, p) >= f->K - st[sp - 1]) pc += ins->N;
                    break;
                }
                case I.Prim:
                {
                    GObj* f = ob + ins->Obj;
                    if (ins->Cull != 0)
                    {
                        double db = CullD(*f, p);
                        if (db > CULL_D) { st[sp++] = db; break; }
                    }
                    st[sp++] = PrimDist(*f, p);
                    break;
                }
                case I.Mod:
                    st[sp - 1] = Modify(ob[ins->Obj], st[sp - 1]);
                    break;
                default:
                    sp--;
                    st[sp - 1] = Combine(ob[ins->Obj], st[sp - 1], st[sp], out _);
                    break;
            }
        }
        return sp > 0 ? st[0] : FAR_D;
    }

    /* la distanza e il materiale (mode 1) o la primitiva piu' vicina (mode 2) */
    [SkipLocalsInit]
    double Run(int mode, V3 p, out GMat mout, ref double bd, ref int best)
    {
        double* st = stackalloc double[MAXST];
        GMat* ms = stackalloc GMat[MAXST];
        int sp = 0, pc = 0, end = nprog;
        while (pc < end)
        {
            Ins* ins = prog + pc++;
            GObj* f = obj + ins->Obj;
            switch (ins->Code)
            {
                case I.Min:
                    sp--;
                    if (mode == 1) { if (st[sp] < st[sp - 1]) { st[sp - 1] = st[sp]; ms[sp - 1] = ms[sp]; } }
                    else st[sp - 1] = R.Min(st[sp - 1], st[sp]);
                    break;
                case I.Skip:
                case I.Lazy:
                    break;
                case I.Prim:
                    if (mode == 1) ms[sp] = f->Mat;
                    st[sp] = PrimDist(*f, p);
                    if (mode == 2 && Math.Abs(st[sp]) < bd) { bd = Math.Abs(st[sp]); best = ins->Obj; }
                    sp++;
                    break;
                case I.Mod:
                    st[sp - 1] = Modify(*f, st[sp - 1]);
                    break;
                default:
                {
                    sp--;
                    st[sp - 1] = Combine(*f, st[sp - 1], st[sp], out double wa);
                    if (mode == 1) GMat.Mix(ref ms[sp - 1], ms[sp - 1], ms[sp], wa);
                    break;
                }
            }
        }
        if (mode == 1 && sp > 0) mout = ms[0]; else mout = GMat.Default();
        return sp > 0 ? st[0] : FAR_D;
    }

    public double MapM(V3 p, out GMat m)
    {
        double bd = 0;
        int best = 0;
        return Run(1, p, out m, ref bd, ref best);
    }

    /* la primitiva la cui superficie e' piu' vicina, -1 se nessuna */
    public int PickObj(V3 p)
    {
        double bd = 1e30;
        int best = -1;
        Run(2, p, out _, ref bd, ref best);
        return best;
    }
}
