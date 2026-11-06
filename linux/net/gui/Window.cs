// RayMarch Studio Benchmark in C# con la finestra (SDL3), come c/gui.c: il
// rendering 800 x 600 si vede formarsi mentre i thread lavorano; alla fine i
// tempi, il controllo dell'immagine e i bottoni Run again (tutti i thread),
// 1 thread, Runs - / +, Copy result e Quit. SDL3 si chiama con P/Invoke.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace RmBench;

static unsafe class Sdl
{
    const string L = "libSDL3.so.0";
    [StructLayout(LayoutKind.Sequential)] public struct FRect { public float x, y, w, h; }
    [DllImport(L)] public static extern byte SDL_Init(uint flags);
    [DllImport(L)] public static extern IntPtr SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int w, int h, ulong flags);
    [DllImport(L)] public static extern IntPtr SDL_CreateRenderer(IntPtr win, IntPtr name);
    [DllImport(L)] public static extern IntPtr SDL_CreateTexture(IntPtr r, uint format, int access, int w, int h);
    [DllImport(L)] public static extern byte SDL_UpdateTexture(IntPtr tex, IntPtr rect, void* pixels, int pitch);
    [DllImport(L)] public static extern byte SDL_PollEvent(byte* ev);
    [DllImport(L)] public static extern byte SDL_SetRenderDrawColor(IntPtr r, byte R, byte G, byte B, byte A);
    [DllImport(L)] public static extern byte SDL_RenderClear(IntPtr r);
    [DllImport(L)] public static extern byte SDL_RenderFillRect(IntPtr r, ref FRect rc);
    [DllImport(L)] public static extern byte SDL_RenderRect(IntPtr r, ref FRect rc);
    [DllImport(L)] public static extern byte SDL_RenderTexture(IntPtr r, IntPtr tex, IntPtr src, ref FRect dst);
    [DllImport(L)] public static extern byte SDL_RenderPresent(IntPtr r);
    [DllImport(L)] public static extern byte SDL_SetRenderScale(IntPtr r, float x, float y);
    [DllImport(L)] public static extern byte SDL_RenderDebugText(IntPtr r, float x, float y, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(L)] public static extern byte SDL_SetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(L)] public static extern void SDL_Delay(uint ms);
    [DllImport(L)] public static extern void SDL_Quit();
    [DllImport(L)] public static extern IntPtr SDL_GetError();
    [DllImport(L)] public static extern byte SDL_SetTextureBlendMode(IntPtr tex, uint mode);
    [DllImport(L)] public static extern byte SDL_SetTextureColorMod(IntPtr tex, byte r, byte g, byte b);
}

/* FreeType (libfreetype.so.6 del sistema); i campi si leggono a offset fissi (x64 Linux) */
static unsafe class Ft
{
    const string L = "libfreetype.so.6";
    [DllImport(L)] public static extern int FT_Init_FreeType(out IntPtr lib);
    [DllImport(L)] public static extern int FT_New_Face(IntPtr lib, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, long index, out IntPtr face);
    [DllImport(L)] public static extern int FT_Set_Pixel_Sizes(IntPtr face, uint w, uint h);
    [DllImport(L)] public static extern int FT_Load_Char(IntPtr face, ulong c, int flags);
}

sealed unsafe class Glyph { public IntPtr T; public int W, H, Left, Top, Adv; }

sealed unsafe class Font
{
    public int Asc, Lh;
    public Glyph[] G = new Glyph[95];

    public static Font Load(IntPtr r, string path, int size)
    {
        Ft.FT_Init_FreeType(out IntPtr lib);
        if (Ft.FT_New_Face(lib, path, 0, out IntPtr face) != 0) return null;
        Ft.FT_Set_Pixel_Sizes(face, 0, (uint)size);
        byte* sz = *(byte**)((byte*)face + 160);
        var f = new Font { Asc = (int)(*(long*)(sz + 48) >> 6), Lh = (int)(*(long*)(sz + 64) >> 6) };
        for (int c = 32; c < 127; c++)
        {
            if (Ft.FT_Load_Char(face, (ulong)c, 4) != 0) continue;   /* FT_LOAD_RENDER */
            byte* slot = *(byte**)((byte*)face + 152);
            var g = new Glyph { Adv = (int)(*(long*)(slot + 128) >> 6), Left = *(int*)(slot + 192), Top = *(int*)(slot + 196) };
            g.H = (int)*(uint*)(slot + 152); g.W = (int)*(uint*)(slot + 156);
            int pitch = *(int*)(slot + 160);
            byte* src = *(byte**)(slot + 168);
            if (g.W > 0 && g.H > 0)
            {
                var px = new byte[g.W * g.H * 4];
                for (int y = 0; y < g.H; y++)
                    for (int x = 0; x < g.W; x++)
                    {
                        int o = (y * g.W + x) * 4;
                        px[o] = px[o + 1] = px[o + 2] = 255;
                        px[o + 3] = src[y * pitch + x];
                    }
                g.T = Sdl.SDL_CreateTexture(r, 0x16762004, 0, g.W, g.H);   /* RGBA32 (byte order R,G,B,A), static */
                fixed (byte* p = px) Sdl.SDL_UpdateTexture(g.T, IntPtr.Zero, p, g.W * 4);
                Sdl.SDL_SetTextureBlendMode(g.T, 1);   /* SDL_BLENDMODE_BLEND */
            }
            f.G[c - 32] = g;
        }
        return f;
    }

    public int Width(string s) { int w = 0; foreach (char c in s) if (c >= 32 && c < 127) w += G[c - 32]?.Adv ?? 0; return w; }

    public void Draw(IntPtr r, float x, float y, int R, int Gc, int B, string s)
    {
        foreach (char c in s)
        {
            if (c < 32 || c > 126) continue;
            var g = G[c - 32];
            if (g == null) continue;
            if (g.T != IntPtr.Zero)
            {
                var d = new Sdl.FRect { x = x + g.Left, y = y + Asc - g.Top, w = g.W, h = g.H };
                Sdl.SDL_SetTextureColorMod(g.T, (byte)R, (byte)Gc, (byte)B);
                Sdl.SDL_RenderTexture(r, g.T, IntPtr.Zero, ref d);
            }
            x += g.Adv;
        }
    }

}

static unsafe class Program
{
    const int W = 800, H = 600, PANEL = 262, MAX_RUNS = 99;
    const int B_AGAIN = 0, B_ONE = 1, B_MINUS = 2, B_PLUS = 3, B_COPY = 4, B_QUIT = 5;
    static readonly string[] Labels = { "Run again", "1 thread", "-", "+", "Copy result", "Quit" };
    static readonly float[] Bx = { 14, 174, 400, 490, 556, 706 }, Bw = { 150, 140, 40, 40, 140, 80 };
    const float By = H + PANEL - 54, Bh = 40;

    static Render rnd;
    static Job job;
    static uint* buf;
    static string sys, cpu, result = "", status = "Starting...";
    static double[] times = new double[MAX_RUNS];
    static double tSetup, tStart, tMedian, tBest;
    static int runs = 1, runsDone, seriesThreads, allThreads, check = -1;
    static bool running;
    static uint sum;

    static void StartRun()
    {
        job = new Job(rnd, buf, seriesThreads);
        tStart = Bench.Now();
        running = true;
    }

    static void StartSeries(int threads)
    {
        if (running || rnd == null) return;
        seriesThreads = threads;
        runsDone = 0;
        check = -1;
        new Span<uint>(buf, W * H).Clear();
        status = $"Rendering on {threads} thread{(threads > 1 ? "s" : "")}...";
        StartRun();
    }

    static void FinishSeries()
    {
        var s = times.Take(runs).OrderBy(x => x).ToArray();
        tBest = s[0];
        tMedian = runs % 2 == 1 ? s[runs / 2] : (s[runs / 2 - 1] + s[runs / 2]) / 2;
        check = Bench.Check(buf, W, H, null, new double[3], out sum);
        status = "Done.";
        result = $"RESULT | rmbench-net-gui {Bench.Version} | {sys} | {cpu} | {Bench.Compiler()} | {seriesThreads} threads | setup {tSetup:F2} s | render {tBest:F4} s (best of {runs}, median {tMedian:F4} s) | {Bench.ShortVerdict(check)}";
    }

    static void Poll()
    {
        if (!running || job.Busy(out _)) return;
        times[runsDone] = Bench.Now() - tStart;
        job.Wait();
        running = false;
        if (++runsDone < runs)
        {
            status = $"Run {runsDone} of {runs} done ({times[runsDone - 1]:F4} s)...";
            StartRun();
        }
        else FinishSeries();
    }

    static Font fSmall, fMid, fBig, fBtn;

    static void DrawButton(IntPtr r, int i, bool enabled)
    {
        var f = new Sdl.FRect { x = Bx[i], y = By, w = Bw[i], h = Bh };
        Sdl.SDL_SetRenderDrawColor(r, (byte)(enabled ? 60 : 40), (byte)(enabled ? 70 : 44), (byte)(enabled ? 90 : 50), 255);
        Sdl.SDL_RenderFillRect(r, ref f);
        Sdl.SDL_SetRenderDrawColor(r, 120, 130, 150, 255);
        Sdl.SDL_RenderRect(r, ref f);
        int c = enabled ? 255 : 130;
        fBtn.Draw(r, Bx[i] + (Bw[i] - fBtn.Width(Labels[i])) / 2, By + (Bh - fBtn.Lh) / 2, c, c, c, Labels[i]);
    }

    static bool Hit(int i, float x, float y) => x >= Bx[i] && x < Bx[i] + Bw[i] && y >= By && y < By + Bh;

    static int Main()
    {
        if (Sdl.SDL_Init(0x20) == 0) { Console.Error.WriteLine("SDL: " + Marshal.PtrToStringUTF8(Sdl.SDL_GetError())); return 1; }
        IntPtr win = Sdl.SDL_CreateWindow("RayMarch Studio Benchmark (C#, .NET 10, Linux)", W, H + PANEL, 0);
        IntPtr r = win != IntPtr.Zero ? Sdl.SDL_CreateRenderer(win, IntPtr.Zero) : IntPtr.Zero;
        if (r == IntPtr.Zero) { Console.Error.WriteLine("SDL: " + Marshal.PtrToStringUTF8(Sdl.SDL_GetError())); return 1; }
        IntPtr tex = Sdl.SDL_CreateTexture(r, 0x16161804, 1, W, H);
        string fp = new[] { "/usr/share/fonts/TTF/DejaVuSans.ttf", "/usr/share/fonts/noto/NotoSans-Regular.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }
            .FirstOrDefault(System.IO.File.Exists);
        if (fp == null) { Console.Error.WriteLine("no font found (DejaVu Sans or Noto Sans)"); return 1; }
        fSmall = Font.Load(r, fp, 19); fMid = Font.Load(r, fp, 24); fBig = Font.Load(r, fp, 34); fBtn = Font.Load(r, fp, 22);
        Bench.Machine(out sys, out cpu);
        allThreads = Environment.ProcessorCount;
        buf = Bench.AllocPixels(W * H);
        double t0 = Bench.Now();
        try { rnd = Bench.Setup(W, H); }
        catch (Exception e) { Console.Error.WriteLine("scene: " + e.Message); return 1; }
        tSetup = Bench.Now() - t0;
        StartSeries(allThreads);

        byte* ev = stackalloc byte[128];
        bool quit = false;
        while (!quit)
        {
            while (Sdl.SDL_PollEvent(ev) != 0)
            {
                uint type = *(uint*)ev;
                if (type == 0x100) quit = true;
                else if (type == 0x300 && *(uint*)(ev + 28) == 0x1b) quit = true;
                else if (type == 0x401 && ev[24] == 1)
                {
                    float x = *(float*)(ev + 28), y = *(float*)(ev + 32);
                    if (Hit(B_QUIT, x, y)) quit = true;
                    else if (Hit(B_AGAIN, x, y)) StartSeries(allThreads);
                    else if (Hit(B_ONE, x, y)) StartSeries(1);
                    else if (Hit(B_MINUS, x, y) && !running && runs > 1) runs--;
                    else if (Hit(B_PLUS, x, y) && !running && runs < MAX_RUNS) runs++;
                    else if (Hit(B_COPY, x, y) && result.Length > 0) Sdl.SDL_SetClipboardText(result);
                }
            }
            Poll();
            Sdl.SDL_UpdateTexture(tex, IntPtr.Zero, buf, W * 4);
            Sdl.SDL_SetRenderDrawColor(r, 24, 26, 32, 255);
            Sdl.SDL_RenderClear(r);
            var img = new Sdl.FRect { x = 0, y = 0, w = W, h = H };
            Sdl.SDL_RenderTexture(r, tex, IntPtr.Zero, ref img);
            fBig.Draw(r, 14, H + 8, 255, 255, 255, status);
            if (!running && check >= 0)
            {
                fMid.Draw(r, 14, H + 52, 255, 255, 255, $"Best {tBest:F4} s   median {tMedian:F4} s   ({runs} run{(runs > 1 ? "s" : "")}, {seriesThreads} thread{(seriesThreads > 1 ? "s" : "")})");
                string chk = $"Image check: {Bench.Verdict(check)} (checksum {sum:x8})";
                if (check >= 1) fMid.Draw(r, 14, H + 82, 120, 230, 120, chk); else fMid.Draw(r, 14, H + 82, 255, 200, 60, chk);
            }
            fSmall.Draw(r, 14, H + 118, 200, 200, 200, cpu);
            fSmall.Draw(r, 14, H + 141, 200, 200, 200, sys);
            fSmall.Draw(r, 14, H + 164, 200, 200, 200, $"{Bench.Compiler()}   -   setup {tSetup:F2} s");
            string n = runs.ToString();
            fBtn.Draw(r, 330, By + 8, 255, 255, 255, "Runs");
            fBtn.Draw(r, 465 - fBtn.Width(n) / 2, By + 8, 255, 255, 255, n);
            for (int i = 0; i < 6; i++)
                DrawButton(r, i, !(running && i != B_QUIT) && !(i == B_COPY && result.Length == 0));
            Sdl.SDL_RenderPresent(r);
            Sdl.SDL_Delay(running ? 16u : 30u);
        }
        if (running) job.Stop();
        Sdl.SDL_Quit();
        return 0;
    }
}
