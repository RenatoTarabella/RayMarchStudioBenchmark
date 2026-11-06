// La base comune (rm.h, vec.c): vettori e matrici in double. Le stesse
// formule del C nello stesso ordine, perche' l'immagine venga identica.
using System;
using System.Runtime.CompilerServices;

namespace RmBench;

public struct V3
{
    public double X, Y, Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    /* componente per componente (v3mulv) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 operator *(V3 a, V3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Len(V3 a) => Math.Sqrt(Dot(a, a));
    /* a + (b - a) * t */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 Lerp(V3 a, V3 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 Abs(V3 a) => new(Math.Abs(a.X), Math.Abs(a.Y), Math.Abs(a.Z));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double MaxC(V3 a) => a.X > a.Y ? (a.X > a.Z ? a.X : a.Z) : (a.Y > a.Z ? a.Y : a.Z);

    public double Get(int i) => i == 0 ? X : i == 1 ? Y : Z;

    /* versore; (0,0,0) resta tale */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V3 Norm(V3 a)
    {
        double l = Len(a);
        return l > 0 ? a * (1 / l) : a;
    }
}

public static class R
{
    public const double PI = 3.14159265358979323846;
    public const double TP = 6.28318530717958647692;

    /* come il C: a < b ? a : b (Math.Min tratta diversamente NaN e -0) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Min(double a, double b) => a < b ? a : b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Max(double a, double b) => a > b ? a : b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Mix(double a, double b, double t) => a + (b - a) * t;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Len2(double x, double y) => Math.Sqrt(x * x + y * y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Fract(double x) => x - Math.Floor(x);

    /* a meta' verso il pari, come rround del C */
    public static double Round(double x)
    {
        double r = Math.Floor(x + 0.5);
        if (r - x == 0.5 && r % 2 != 0) r -= 1;
        return r;
    }
}

/* Trasformazione affine: righe di [3x3 | traslazione] */
public struct M34
{
    public double M0, M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11;

    public static readonly M34 Identity = Rows(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);

    public static M34 Rows(double a, double b, double c, double d, double e, double f,
                           double g, double h, double i, double j, double k, double l)
    {
        M34 r;
        r.M0 = a; r.M1 = b; r.M2 = c; r.M3 = d;
        r.M4 = e; r.M5 = f; r.M6 = g; r.M7 = h;
        r.M8 = i; r.M9 = j; r.M10 = k; r.M11 = l;
        return r;
    }

    public double this[int i]
    {
        readonly get => i switch
        {
            0 => M0, 1 => M1, 2 => M2, 3 => M3, 4 => M4, 5 => M5,
            6 => M6, 7 => M7, 8 => M8, 9 => M9, 10 => M10, _ => M11
        };
        set
        {
            switch (i)
            {
                case 0: M0 = value; break; case 1: M1 = value; break; case 2: M2 = value; break;
                case 3: M3 = value; break; case 4: M4 = value; break; case 5: M5 = value; break;
                case 6: M6 = value; break; case 7: M7 = value; break; case 8: M8 = value; break;
                case 9: M9 = value; break; case 10: M10 = value; break; default: M11 = value; break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly V3 Point(V3 p) => new(M0 * p.X + M1 * p.Y + M2 * p.Z + M3,
                                          M4 * p.X + M5 * p.Y + M6 * p.Z + M7,
                                          M8 * p.X + M9 * p.Y + M10 * p.Z + M11);

    public readonly V3 Trans => new(M3, M7, M11);

    static void Mul33(double[,] a, double[,] b, double[,] r)
    {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
    }

    /* posizione, rotazione in gradi (prima X, poi Y, poi Z), scala: T * Rz * Ry * Rx * S */
    public static M34 Compose(V3 pos, V3 rot, V3 scl)
    {
        double ax = rot.X * R.PI / 180, ay = rot.Y * R.PI / 180, az = rot.Z * R.PI / 180;
        double sx = Math.Sin(ax), cx = Math.Cos(ax), sy = Math.Sin(ay), cy = Math.Cos(ay), sz = Math.Sin(az), cz = Math.Cos(az);
        var rx = new double[,] { { 1, 0, 0 }, { 0, cx, -sx }, { 0, sx, cx } };
        var ry = new double[,] { { cy, 0, sy }, { 0, 1, 0 }, { -sy, 0, cy } };
        var rz = new double[,] { { cz, -sz, 0 }, { sz, cz, 0 }, { 0, 0, 1 } };
        var t = new double[3, 3];
        var r = new double[3, 3];
        Mul33(ry, rx, t);
        Mul33(rz, t, r);
        double[] s = { scl.X, scl.Y, scl.Z };
        M34 o = default;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) o[i * 4 + j] = r[i, j] * s[j];
        o.M3 = pos.X; o.M7 = pos.Y; o.M11 = pos.Z;
        return o;
    }

    public readonly double ColLen(int j)
    {
        double a = this[j], b = this[4 + j], c = this[8 + j];
        return Math.Sqrt(a * a + b * b + c * c);
    }

    public readonly double MaxScale() => R.Max(ColLen(0), R.Max(ColLen(1), ColLen(2)));

    public static M34 Mul(in M34 a, in M34 b)
    {
        M34 t = default;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) t[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j];
            t[i * 4 + 3] = a[i * 4] * b[3] + a[i * 4 + 1] * b[7] + a[i * 4 + 2] * b[11] + a[i * 4 + 3];
        }
        return t;
    }

    /* false se singolare */
    public static bool Inverse(in M34 s, out M34 inv)
    {
        double a = s.M0, b = s.M1, c = s.M2, d = s.M4, e = s.M5, f = s.M6;
        double g = s.M8, h = s.M9, k = s.M10;
        double A = e * k - f * h, B = f * g - d * k, C = d * h - e * g;
        double det = a * A + b * B + c * C;
        inv = Identity;
        if (det == 0 || double.IsNaN(det)) return false;
        double id = 1 / det;
        inv.M0 = A * id; inv.M1 = (c * h - b * k) * id; inv.M2 = (b * f - c * e) * id;
        inv.M4 = B * id; inv.M5 = (a * k - c * g) * id; inv.M6 = (c * d - a * f) * id;
        inv.M8 = C * id; inv.M9 = (b * g - a * h) * id; inv.M10 = (a * e - b * d) * id;
        for (int i = 0; i < 3; i++)
            inv[i * 4 + 3] = -(inv[i * 4] * s.M3 + inv[i * 4 + 1] * s.M7 + inv[i * 4 + 2] * s.M11);
        return true;
    }
}
