// RayMarch Studio Benchmark in C# (.NET 10), a riga di comando, allineato
// alle versioni C (c/) e Rust (rust/): la stessa scena, gli stessi tile
// 16 x 16 distribuiti da un contatore atomico, lo stesso controllo
// dell'immagine e la stessa modalita' benchmark.
//   rmbench-net [-t thread] [-s scala] [-b [ripetizioni]] [-o immagine.png] [-n] [-f scena.txt]
// -t: quanti thread (tutte le CPU); -s: risoluzione 800s x 600s; -b: un
// render di riscaldamento, poi N render (10) con mediana e minimo, a 1
// thread e a tutti i thread (o solo a -t); -o: dove salvare l'immagine;
// -f: un'altra scena da un file al posto di quella del benchmark (con i
// materiali misti anche dentro il vetro).
using System;
using System.Globalization;
using System.Threading;

namespace RmBench;

static unsafe class Program
{
    /* il rendering su n thread, il tempo in secondi; la percentuale ogni 20 ms */
    static double Run(Render r, uint* buf, int nthreads)
    {
        double t0 = Bench.Now();
        var job = new Job(r, buf, nthreads);
        int pc, last = -1;
        while (job.Busy(out pc))
        {
            if (pc != last) { Console.Error.Write($"\r  {pc,3}%"); last = pc; }
            Thread.Sleep(20);
        }
        job.Wait();
        Console.Error.Write("\r      \r");
        return Bench.Now() - t0;
    }

    static int Main(string[] args)
    {
        string out_ = "rmbench-net.png", file = null;
        int nthreads = 0, reps = 0;
        double scale = 1;
        bool save = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-t" && i + 1 < args.Length) nthreads = int.Parse(args[++i]);
            else if (args[i] == "-s" && i + 1 < args.Length) scale = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "-o" && i + 1 < args.Length) out_ = args[++i];
            else if (args[i] == "-f" && i + 1 < args.Length) file = args[++i];
            else if (args[i] == "-n") save = false;
            else if (args[i] == "-b")
            {
                reps = 10;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n > 0) { reps = n; i++; }
            }
            else
            {
                Console.Error.WriteLine("usage: rmbench-net [-t threads] [-s scale] [-b [repeats]] [-o image.png] [-n] [-f scene.txt]\n" +
                                        "  -t  how many threads (default: all CPUs)\n" +
                                        "  -s  resolution 800*scale x 600*scale (default 1)\n" +
                                        "  -b  benchmark: a warm-up render, then N renders (default 10), median and minimum,\n" +
                                        "      at 1 thread and at all threads (or only at -t)\n" +
                                        "  -o  where to save the image (default rmbench-net.png); -n  do not save it\n" +
                                        "  -f  render the scene in this file instead of the benchmark scene");
                return 1;
            }
        }
        int all = Environment.ProcessorCount;
        int w = (int)(800 * scale + 0.5), h = (int)(600 * scale + 0.5);
        Bench.Machine(out string sys, out string cpu);
        Console.WriteLine($"RayMarch Studio Benchmark {Bench.Version} (.NET)");
        Console.WriteLine($"System:   {sys}");
        Console.WriteLine($"CPU:      {cpu}");
        Console.WriteLine($"Program:  {Bench.Compiler()}");
        if (file != null) Console.WriteLine($"Scene:    {file}");
        Console.WriteLine($"Image:    {w} x {h} = {w * h} pixels, 1 sample per pixel, up to 6 bounces\n");

        /* la scena: il testo, poi la preparazione (anche l'ambiente studio) */
        string text = null;
        if (file != null)
        {
            try { text = System.IO.File.ReadAllText(file); }
            catch (Exception) { Console.Error.WriteLine($"could not read {file}"); return 1; }
        }
        double t0 = Bench.Now();
        Render r;
        try { r = text != null ? Bench.SetupText(text, w, h) : Bench.Setup(w, h); }
        catch (Exception e) { Console.Error.WriteLine("scene: " + e.Message); return 1; }
        double tSetup = Bench.Now() - t0;
        uint* buf = Bench.AllocPixels(w * h);

        if (reps > 0)
        {
            if (nthreads > 0) Bench.Benchmark(r, buf, nthreads, reps);
            else { Bench.Benchmark(r, buf, 1, reps); Bench.Benchmark(r, buf, all, reps); }
            return 0;
        }
        if (nthreads < 1) nthreads = all;
        double tRender = Run(r, buf, nthreads);
        var rgb = new byte[w * h * 3];
        var mean = new double[3];
        int check = Bench.Check(buf, w, h, rgb, mean, out uint sum);
        Console.WriteLine($"Threads:       {nthreads,9}");
        Console.WriteLine($"Scene setup:   {tSetup,9:F2} s");
        Console.WriteLine($"Rendering:     {tRender,9:F2} s   ({w * h / tRender:F0} pixels/s)");
        Console.WriteLine($"Total:         {tSetup + tRender,9:F2} s\n");
        string verdict = file != null ? "no reference for this scene" : Bench.Verdict(check);
        string shortVerdict = file != null ? "image not checked" : Bench.ShortVerdict(check);
        Console.WriteLine($"Image check:   {verdict} (mean {mean[0]:F2} {mean[1]:F2} {mean[2]:F2}, checksum {sum:x8})");
        if (save)
        {
            try { Bench.WritePng(out_, rgb, w, h); Console.WriteLine($"Image saved:   {out_}"); }
            catch (Exception) { Console.WriteLine($"could not save {out_}"); }
        }
        Console.WriteLine($"\nRESULT | rmbench-net {Bench.Version} | {sys} | {cpu} | {Bench.Compiler()} | {nthreads} threads | setup {tSetup:F2} s | render {tRender:F2} s | total {tSetup + tRender:F2} s | {shortVerdict}");
        return 0;
    }
}
