// L'ambiente (envmap.c): lo studio procedurale, i mipmap, le sorgenti forti
// come luci e l'irradianza in armoniche sferiche. Le immagini sono in
// float come nel C (le somme dei mipmap in float, nello stesso ordine).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RmBench;

public sealed class HdrImage
{
    public int W, H;
    public float[] Rgb;
}

public sealed class EnvMap
{
    public const int MaxLights = 3, MaxLevels = 16;
    const int SMALL_W = 256;

    public HdrImage[] Lev = new HdrImage[MaxLevels];
    public int NLev;
    public int NLights;
    public V3[] LDir = new V3[MaxLights], LCol = new V3[MaxLights];
    public double[] LRad = new double[MaxLights];
    HdrImage small, residual;

    static V3 EnvDir(double u, double v)
    {
        double phi = (u - 0.5) * R.TP, th = v * R.PI;
        return new V3(Math.Sin(phi) * Math.Sin(th), Math.Cos(th), -Math.Cos(phi) * Math.Sin(th));
    }

    static V3 FromAngles(double az, double el)
    {
        double a = az * R.PI / 180, e = el * R.PI / 180;
        return new V3(Math.Sin(a) * Math.Cos(e), Math.Sin(e), Math.Cos(a) * Math.Cos(e));
    }

    public static V3 RotY(V3 d, double deg)
    {
        double a = deg * R.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return new V3(c * d.X + s * d.Z, d.Y, -s * d.X + c * d.Z);
    }

    static double Lum(float r, float g, float b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    // ---- lo studio procedurale

    record struct Panel(double Az, double El, double Hw, double Hh, double L, V3 Col, double Soft);
    record Studio(string Name, V3 Top, V3 Horizon, V3 Bottom, Panel[] P);

    static readonly V3 W1 = new(1, 1, 1);
    static readonly Studio[] studios =
    {
        new("Light tent", new(1.15, 1.15, 1.15), new(0.85, 0.85, 0.85), new(0.6, 0.6, 0.6), new Panel[] {
            new(0, 75, 45, 45, 2.2, W1, 0.3), new(-85, 12, 18, 28, 0.03, W1, 0.1), new(85, 12, 18, 28, 0.03, W1, 0.1) }),
        new("Softbox studio", new(0.07, 0.07, 0.075), new(0.045, 0.045, 0.05), new(0.03, 0.03, 0.03), new Panel[] {
            new(-45, 35, 22, 22, 9, W1, 0.15), new(60, 20, 22, 30, 2.5, W1, 0.15), new(0, 80, 30, 30, 3, W1, 0.15),
            new(160, 30, 10, 25, 6, W1, 0.15) }),
        new("Strip lights", new(0.012, 0.012, 0.012), new(0.008, 0.008, 0.008), new(0.006, 0.006, 0.006), new Panel[] {
            new(-100, 15, 5, 40, 12, W1, 0.1), new(100, 15, 5, 40, 12, W1, 0.1), new(180, 30, 28, 4, 7, W1, 0.1),
            new(0, 72, 4, 35, 7, W1, 0.1) }),
        new("High key", new(1.05, 1.05, 1.05), new(1, 1, 1), new(0.85, 0.85, 0.85), new Panel[] {
            new(0, 30, 50, 30, 3, W1, 0.3), new(0, 80, 40, 40, 3, W1, 0.3) }),
        new("Window", new(0.3, 0.27, 0.24), new(0.22, 0.19, 0.16), new(0.12, 0.1, 0.08), new Panel[] {
            new(-60, 18, 30, 25, 7, new(1, 0.98, 0.95), 0.05) }),
    };

    static double Edge(double x, double soft)
    {
        double t = R.Clamp((1 - x) / soft, 0, 1);
        return t * t * (3 - 2 * t);
    }

    static HdrImage StudioRender(Studio s, int w)
    {
        int h = w / 2, np = s.P.Length;
        var img = new HdrImage { W = w, H = h, Rgb = new float[w * h * 3] };
        var c = new V3[np]; var rt = new V3[np]; var up = new V3[np];
        var tw = new double[np]; var th = new double[np];
        for (int k = 0; k < np; k++)
        {
            c[k] = FromAngles(s.P[k].Az, s.P[k].El);
            rt[k] = Math.Abs(c[k].Y) > 0.99 ? new V3(1, 0, 0) : V3.Norm(V3.Cross(new V3(0, 1, 0), c[k]));
            up[k] = V3.Cross(c[k], rt[k]);
            tw[k] = Math.Tan(s.P[k].Hw * R.PI / 180);
            th[k] = Math.Tan(s.P[k].Hh * R.PI / 180);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                V3 d = EnvDir((x + 0.5) / w, (y + 0.5) / h);
                V3 col = d.Y >= 0 ? s.Horizon + (s.Top - s.Horizon) * Math.Sqrt(d.Y)
                                  : s.Horizon + (s.Bottom - s.Horizon) * Math.Sqrt(-d.Y);
                for (int k = 0; k < np; k++)
                {
                    double z = V3.Dot(d, c[k]);
                    if (z <= 0) continue;
                    double px = Math.Abs(V3.Dot(d, rt[k]) / z) / tw[k];
                    double py = Math.Abs(V3.Dot(d, up[k]) / z) / th[k];
                    double soft = R.Max(s.P[k].Soft, 1e-3);
                    double m = Edge(px, soft) * Edge(py, soft);
                    if (m > 0) col = col + (s.P[k].Col * s.P[k].L - col) * m;
                }
                int o = (y * w + x) * 3;
                img.Rgb[o] = (float)col.X; img.Rgb[o + 1] = (float)col.Y; img.Rgb[o + 2] = (float)col.Z;
            }
        return img;
    }

    static HdrImage Shrink(HdrImage a, int k)
    {
        int w = Math.Max(a.W / k, 1), h = Math.Max(a.H / k, 1);
        float inv = 1.0f / (k * k);
        var r = new HdrImage { W = w, H = h, Rgb = new float[w * h * 3] };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float s = 0;
                    for (int j = 0; j < k; j++)
                        for (int i = 0; i < k; i++)
                        {
                            int yy = y * k + j < a.H ? y * k + j : a.H - 1, xx = x * k + i < a.W ? x * k + i : a.W - 1;
                            s += a.Rgb[(yy * a.W + xx) * 3 + c];
                        }
                    r.Rgb[(y * w + x) * 3 + c] = s * inv;
                }
        return r;
    }

    static HdrImage Copy(HdrImage a) => new() { W = a.W, H = a.H, Rgb = (float[])a.Rgb.Clone() };

    // ---- le sorgenti forti come luci

    static double SolidAngle(HdrImage s, int y) => R.TP / s.W * R.PI / s.H * Math.Sin((y + 0.5) / s.H * R.PI);

    void Extract()
    {
        HdrImage sm = small;
        int w = sm.W, h = sm.H;
        var lm = new double[w * h];
        var taken = new bool[w * h];
        var stack = new int[w * h];
        double mean = 0, area = 0, first = 0;
        for (int y = 0; y < h; y++)
        {
            double da = SolidAngle(sm, y);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                lm[i] = Lum(sm.Rgb[i * 3], sm.Rgb[i * 3 + 1], sm.Rgb[i * 3 + 2]);
                mean += lm[i] * da;
                area += da;
            }
        }
        mean /= area;
        Span<int> nb = stackalloc int[8];
        for (int n = 0; n < 3; n++)
        {
            int best = -1, top = 0;
            double omega = 0;
            V3 ev = new(0, 0, 0), dir = new(0, 0, 0);
            for (int i = 0; i < w * h; i++) if (!taken[i] && (best < 0 || lm[i] > lm[best])) best = i;
            if (best < 0) break;
            double peak = lm[best];
            if (n == 0) first = peak;
            if (peak < 4 * mean || peak < 0.25 * first) break;
            double thr = R.Max(2 * mean, 0.25 * peak);
            stack[top++] = best;
            taken[best] = true;
            while (top > 0)
            {
                int ii = stack[--top];
                int x = ii % w, y = ii / w;
                double da = SolidAngle(sm, y);
                double keep = R.Min(1, 2 * mean / R.Max(lm[ii], 1e-9));
                V3 c = new(sm.Rgb[ii * 3], sm.Rgb[ii * 3 + 1], sm.Rgb[ii * 3 + 2]);
                V3 ex = c * (1 - keep);
                ev = ev + ex * da;
                double exl = Lum((float)ex.X, (float)ex.Y, (float)ex.Z);
                dir = dir + EnvDir((x + 0.5) / w, (y + 0.5) / h) * (exl * da);
                omega += da;
                residual.Rgb[ii * 3] = (float)(c.X * keep);
                residual.Rgb[ii * 3 + 1] = (float)(c.Y * keep);
                residual.Rgb[ii * 3 + 2] = (float)(c.Z * keep);
                nb[0] = x - 1; nb[1] = y; nb[2] = x + 1; nb[3] = y;
                nb[4] = x; nb[5] = y - 1; nb[6] = x; nb[7] = y + 1;
                for (int k = 0; k < 4; k++)
                {
                    int ny = nb[k * 2 + 1];
                    if (ny < 0 || ny >= h) continue;
                    int j = ny * w + (nb[k * 2] + w) % w;
                    if (taken[j] || lm[j] < thr) continue;
                    taken[j] = true;
                    stack[top++] = j;
                }
            }
            if (V3.Len(dir) < 1e-12) continue;
            LDir[NLights] = V3.Norm(dir);
            LCol[NLights] = ev * (1 / R.PI);
            LRad[NLights] = Math.Sqrt(omega / R.PI);
            NLights++;
        }
    }

    EnvMap(HdrImage img)
    {
        int k = img.W / SMALL_W > 1 ? img.W / SMALL_W : 1;
        Lev[0] = img;
        NLev = 1;
        /* i mipmap: meta' per meta' fino a 1 (come glGenerateMipmap) */
        while (NLev < MaxLevels && (Lev[NLev - 1].W > 1 || Lev[NLev - 1].H > 1))
        {
            Lev[NLev] = Shrink(Lev[NLev - 1], 2);
            NLev++;
        }
        small = k > 1 ? Shrink(img, k) : Copy(img);
        residual = Copy(small);
        Extract();
    }

    /* l'ambiente del mondo, null per il cielo */
    public static EnvMap Of(World w)
    {
        if (w.Env != Env.Studio) return null;
        int sel = 0;
        for (int i = 0; i < studios.Length; i++) if (w.EnvPreset == studios[i].Name) sel = i;
        return new EnvMap(StudioRender(studios[sel], 1024));
    }

    // ---- il campionamento: bilineare, u si ripete, v si ferma ai bordi

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static V3 Texel(HdrImage m, int x, int y)
    {
        x %= m.W;
        if (x < 0) x += m.W;
        if (y < 0) y = 0;
        if (y >= m.H) y = m.H - 1;
        int p = (y * m.W + x) * 3;
        float[] a = m.Rgb;
        return new V3(a[p], a[p + 1], a[p + 2]);
    }

    static V3 Bilinear(HdrImage m, double u, double v)
    {
        double x = u * m.W - 0.5, y = v * m.H - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        return V3.Lerp(V3.Lerp(Texel(m, x0, y0), Texel(m, x0 + 1, y0), fx),
                       V3.Lerp(Texel(m, x0, y0 + 1), Texel(m, x0 + 1, y0 + 1), fx), fy);
    }

    public V3 Sample(V3 d, double lod)
    {
        double u = Math.Atan2(d.X, -d.Z) / R.TP + 0.5, v = Math.Acos(R.Clamp(d.Y, -1, 1)) / R.PI;
        lod = R.Clamp(lod, 0, NLev - 1);
        int l0 = (int)Math.Floor(lod);
        double f = lod - l0;
        /* 0 <= l0 < NLev <= MaxLevels: senza controllo dei limiti */
        ref HdrImage lev = ref MemoryMarshal.GetArrayDataReference(Lev);
        if (f <= 0 || l0 + 1 >= NLev) return Bilinear(Unsafe.Add(ref lev, l0), u, v);
        return V3.Lerp(Bilinear(Unsafe.Add(ref lev, l0), u, v), Bilinear(Unsafe.Add(ref lev, l0 + 1), u, v), f);
    }

    // ---- l'irradianza in armoniche sferiche

    public void Irradiance(double rotation, double intensity, bool withLights, V3[] c)
    {
        double[] band = { 1, 2.0 / 3, 2.0 / 3, 2.0 / 3, 0.25, 0.25, 0.25, 0.25, 0.25 };
        HdrImage src = withLights ? residual : small;
        int w = src.W, h = src.H;
        var y = new double[9];
        for (int k = 0; k < 9; k++) c[k] = new V3(0, 0, 0);
        for (int j = 0; j < h; j++)
        {
            double da = SolidAngle(src, j);
            for (int i = 0; i < w; i++)
            {
                V3 d = RotY(EnvDir((i + 0.5) / w, (j + 0.5) / h), rotation);
                int o = (j * w + i) * 3;
                V3 L = new(src.Rgb[o] * da, src.Rgb[o + 1] * da, src.Rgb[o + 2] * da);
                double x = d.X, yy = d.Y, z = d.Z;
                y[0] = 0.282095;
                y[1] = 0.488603 * yy; y[2] = 0.488603 * z; y[3] = 0.488603 * x;
                y[4] = 1.092548 * x * yy; y[5] = 1.092548 * yy * z; y[6] = 0.315392 * (3 * z * z - 1);
                y[7] = 1.092548 * x * z; y[8] = 0.546274 * (x * x - yy * yy);
                for (int k = 0; k < 9; k++) c[k] = c[k] + L * y[k];
            }
        }
        for (int k = 0; k < 9; k++) c[k] = c[k] * (band[k] * intensity);
    }

    public int Lights(double rotation, double intensity, V3[] dir, V3[] col, double[] soft)
    {
        for (int i = 0; i < NLights; i++)
        {
            dir[i] = RotY(LDir[i], rotation);
            col[i] = LCol[i] * intensity;
            soft[i] = R.Clamp(0.5 / R.Max(LRad[i], 1e-3), 2, 64);
        }
        return NLights;
    }
}
