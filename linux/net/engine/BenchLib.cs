// Il benchmark: le parti comuni alla riga di comando e alla finestra
// (bench/benchlib.c, src/job.c, src/image.c): l'ora, la macchina, il
// runtime, la scena, i thread, il controllo dell'immagine e il PNG.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RmBench;

public static class Bench
{
    public const string Version = "1.0.2";
    public const int W = 800, H = 600;
    const double REF_R = 167.13, REF_G = 146.51, REF_B = 150.26, REF_TOL = 1.5;
    const uint REF_SUM = 0x977e953f;

    /* bench/benchmark.txt, come bench/benchscene.h */
    static readonly string scene =
        "RayMarchStudio 1\n" +
        "camera 2.55 2.55 3.15 0 0.87 0 2.6\n" +
        "light dir -0.4472 0.7155 0.5367 1.3 1.15 0.95 1 1 8\n" +
        "world 1 0.0015 1\n" +
        "#gpu floor checker 0.2 0.2 0.21 0.08 0.08 0.09 1 0.25\n" +
        "#gpu env studio \"Light tent\" 1 -30 0 0.82 0.82 0.83 1 \"\"\n" +
        "#gpu tone neutral 0\n" +
        "snap 0 0.25\n" +
        "object -1 group union 0 3 \"Carved cube\"\n" +
        "pos 0 0.9 0\nrot 0 12 0\nscale 1 1 1\nmaterial 0.7 0.03 0.02 1 64\n" +
        "object 0 box union 0 3 \"Plastic cube\"\n" +
        "pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 0.7 0.03 0.02 1 64\n" +
        "#gpu material 0.08 0.04 0 1.5 0\nparam 1.6 1.6 1.6 0.16\n" +
        "object 0 sphere subtract 0.15 3 \"Hollow\"\n" +
        "pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 1 0.78 0.34 1 96\n" +
        "#gpu material 1 0.06 0 1.5 1\nparam 0.64\n" +
        "object 0 cylinder subtract 0.2 3 \"Hole Y\"\n" +
        "pos 0 0 0\nrot 0 0 0\nscale 1 1 1\nmaterial 1 0.78 0.34 1 96\n" +
        "#gpu material 1 0.06 0 1.5 1\nparam 0.42 2.2 0\n" +
        "object 0 cylinder subtract 0.2 3 \"Hole Z\"\n" +
        "pos 0 0 0\nrot 90 0 0\nscale 1 1 1\nmaterial 0.92 0.92 0.94 1 128\n" +
        "#gpu material 1 0 0 1.5 1\nparam 0.42 2.2 0\n" +
        "object 0 cylinder subtract 0.2 3 \"Hole X\"\n" +
        "pos 0 0 0\nrot 0 0 90\nscale 1 1 1\nmaterial 0.1 0.3 0.85 1 64\n" +
        "#gpu material 0.1 0.05 0 1.5 0\nparam 0.42 2.2 0\n" +
        Corner(1, "-0.74 0.74 -0.74") + Corner(2, "0.74 0.74 -0.74") + Corner(3, "-0.74 0.74 0.74") + Corner(4, "0.74 0.74 0.74") +
        Corner(5, "-0.74 -0.74 -0.74") + Corner(6, "0.74 -0.74 -0.74") + Corner(7, "-0.74 -0.74 0.74") + Corner(8, "0.74 -0.74 0.74") +
        "object -1 sphere union 0 3 \"Glass core\"\n" +
        "pos 0 0.9 0\nrot 0 0 0\nscale 1 1 1\nmaterial 0.85 0.93 1 1 128\n" +
        "#gpu material 0 0 1 1.5 0\nparam 0.46\n";

    static string Corner(int n, string pos) =>
        $"object 0 sphere union 0.1 3 \"Corner {n}\"\npos {pos}\nrot 0 0 0\nscale 1 1 1\nmaterial 0.22 0.3 0.38 1 128\n" +
        "#gpu material 0 0 1 1.5 0\nparam 0.16\n";

    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /* la scena dentro il programma pronta da rendere, w x h: il calcolo di
     * sempre, senza i materiali misti */
    public static Render Setup(int w = W, int h = H) => SetupText(scene, w, h, false);

    /* una scena caricata da file: i materiali misti anche dentro il vetro */
    public static Render SetupText(string text, int w = W, int h = H, bool mix = true)
    {
        Scene s = Scene.Read(text);
        GScene g = GScene.Build(s);
        return new Render(s, g, w, h) { Mix = mix };
    }

    /* il buffer dei pixel allineato a 64 byte (una riga di tile = una riga di cache) */
    public static unsafe uint* AllocPixels(int n)
    {
        uint* p = (uint*)NativeMemory.AlignedAlloc((nuint)n * 4, 64);
        new Span<uint>(p, n).Clear();
        return p;
    }

    /* sistema ("Windows 11 24H2 (build 26100)") e processore */
    public static void Machine(out string sys, out string cpu)
    {
        sys = RuntimeInformation.OSDescription;
        cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown CPU";
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string name = null;
                foreach (string l in File.ReadLines("/etc/os-release"))
                    if (l.StartsWith("PRETTY_NAME=")) name = l.Substring(12).Trim('"');
                if (name != null) sys = $"{name} (kernel {File.ReadAllText("/proc/sys/kernel/osrelease").Trim()})";
                foreach (string l in File.ReadLines("/proc/cpuinfo"))
                    if (l.StartsWith("model name")) { cpu = l.Substring(l.IndexOf(':') + 1).Trim(); break; }
            }
            catch (Exception) { }
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            /* il nome del prodotto nel registro dice ancora "Windows 10" anche sull'11: conta la build */
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k?.GetValue("CurrentBuildNumber") is string build)
            {
                string disp = k.GetValue("DisplayVersion") as string ?? "";
                sys = $"Windows {(int.TryParse(build, out int b) && b >= 22000 ? "11" : "10")}{(disp.Length > 0 ? " " : "")}{disp} (build {build})";
            }
            using var p = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (p?.GetValue("ProcessorNameString") is string name) cpu = name.Trim();
        }
        catch (Exception) { }
    }

    /* il runtime con cui gira il programma ("NET 10.0 JIT, x64") */
    public static string Compiler()
    {
        return $".NET {Environment.Version.Major}.{Environment.Version.Minor} JIT, {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }

    /* il controllo dell'immagine (0x00RRGGBB): la media dei colori e il
     * checksum (FNV-1a dei byte RGB); rgb (se non null) riceve i byte.
     * 0 sbagliata, 1 giusta, 2 identica a quella di riferimento, -1 nessun
     * riferimento a questa risoluzione */
    public static unsafe int Check(uint* buf, int w, int h, byte[] rgb, double[] mean, out uint sum)
    {
        uint s = 2166136261u;
        mean[0] = mean[1] = mean[2] = 0;
        for (int i = 0; i < w * h; i++)
        {
            byte r = (byte)(buf[i] >> 16), g = (byte)(buf[i] >> 8), b = (byte)buf[i];
            if (rgb != null) { rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; }
            mean[0] += r; mean[1] += g; mean[2] += b;
            s = (s ^ r) * 16777619u;
            s = (s ^ g) * 16777619u;
            s = (s ^ b) * 16777619u;
        }
        for (int k = 0; k < 3; k++) mean[k] /= (double)w * h;
        sum = s;
        if (w != W || h != H) return -1;
        if (mean[0] < REF_R - REF_TOL || mean[0] > REF_R + REF_TOL || mean[1] < REF_G - REF_TOL ||
            mean[1] > REF_G + REF_TOL || mean[2] < REF_B - REF_TOL || mean[2] > REF_B + REF_TOL) return 0;
        return s == REF_SUM ? 2 : 1;
    }

    public static string Verdict(int check) => check == 2 ? "OK, identical to the reference" : check == 1 ? "OK (last bits differ: compiler rounding)" :
        check < 0 ? "no reference at this size: compare with the other versions" : "WRONG IMAGE";
    public static string ShortVerdict(int check) => check == 2 ? "image identical" : check == 1 ? "image OK" : check < 0 ? "image not checked" : "WRONG IMAGE";

    /* la modalita' benchmark: un riscaldamento, poi reps render; mediana e minimo */
    public static unsafe void Benchmark(Render r, uint* buf, int n, int reps)
    {
        var t = new double[reps];
        var job = new Job(r, buf, n);
        job.Wait();
        for (int i = 0; i < reps; i++)
        {
            double t0 = Now();
            job = new Job(r, buf, n);
            job.Wait();
            t[i] = Now() - t0;
        }
        Array.Sort(t);
        double med = reps % 2 == 1 ? t[reps / 2] : (t[reps / 2 - 1] + t[reps / 2]) / 2;
        int c = Check(buf, r.Wd, r.Ht, null, new double[3], out uint sum);
        string v = c == 2 ? "identical to the reference" : c < 0 ? "no reference at this size: compare with the other versions" : "DIFFERENT FROM THE REFERENCE";
        Console.WriteLine($"BENCH | {Compiler()} | {n,2} thread{(n > 1 ? "s" : " ")} | {r.Wd}x{r.Ht} | median {med,8:F4} s | min {t[0],8:F4} s | checksum {sum:x8} ({v})");
    }

    // ---- il PNG (image.c): righe con filtro 0, zlib

    public static void WritePng(string path, byte[] rgb, int w, int h)
    {
        static void Chunk(Stream s, string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);
            s.Write(t);
            s.Write(data);
            uint crc = Crc(Crc(0xffffffffu, t), data) ^ 0xffffffffu;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, crc);
            s.Write(len);
        }
        static uint Crc(uint c, byte[] d)
        {
            foreach (byte b in d)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }
            return c;
        }
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * w * 3, w * 3);
            }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2;
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }
}

/* Un rendering in corso: n thread prendono tile di 16 x 16 pixel da un
 * contatore atomico (il carico si bilancia da solo); ogni thread ha il suo Ctx */
public sealed unsafe class Job
{
    public const int TILE = 16;

    readonly Render r;
    readonly uint* buf;
    readonly Thread[] th;
    readonly int tx, ntiles;
    readonly int[] counters = GC.AllocateArray<int>(48, pinned: true);  /* next e done, 64 byte l'uno dall'altro */
    readonly int* next, done;
    volatile bool abort;

    public int Threads => th.Length;

    public Job(Render r, uint* buf, int nthreads)
    {
        this.r = r;
        this.buf = buf;
        tx = (r.Wd + TILE - 1) / TILE;
        ntiles = tx * ((r.Ht + TILE - 1) / TILE);
        next = (int*)Unsafe.AsPointer(ref counters[16]);
        done = (int*)Unsafe.AsPointer(ref counters[32]);
        th = new Thread[nthreads];
        for (int k = 0; k < nthreads; k++) th[k] = new Thread(Worker, 4 << 20) { IsBackground = true };
        foreach (var t in th) t.Start();
    }

    void Worker()
    {
        var c = new Ctx(r.G);
        int w = r.Wd, h = r.Ht;
        while (!abort)
        {
            int t = Interlocked.Increment(ref *next) - 1;
            if (t >= ntiles) break;
            int x0 = t % tx * TILE, y0 = t / tx * TILE;
            int x1 = Math.Min(x0 + TILE, w), y1 = Math.Min(y0 + TILE, h);
            for (int y = y0; y < y1; y++)
            {
                uint* row = buf + (nint)y * w;
                for (int x = x0; x < x1; x++) row[x] = r.Pixel(c, x, y);
            }
            Interlocked.Increment(ref *done);
        }
    }

    /* true se e' in corso; la percentuale */
    public bool Busy(out int percent)
    {
        percent = (int)(Volatile.Read(ref *done) * 100L / ntiles);
        foreach (var t in th) if (t.IsAlive) return true;
        return false;
    }

    /* aspetta la fine */
    public void Wait()
    {
        foreach (var t in th) t.Join();
    }

    /* ferma i thread e aspetta che escano */
    public void Stop()
    {
        abort = true;
        Wait();
    }
}
