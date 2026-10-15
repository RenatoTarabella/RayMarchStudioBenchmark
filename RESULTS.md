# Results

RayMarch Studio Benchmark 1.0, 800 x 600. Lower times are better; compare
the **Rendering** column.

| Machine | Processors | Program | Threads | Rendering | Setup | Total | Pixels/s | Image check |
|---|---|---|---|---|---|---|---|---|
| Octane, **emulated** (SGIEmu), IRIX 6.5 | 2 x R10000 at 1000 MIPS | rmbenchx, gcc 2.8, MIPS IV n32 | 2 | 43.43 s | 0.53 s | 43.95 s | 11053 | identical (977e953f) |
| Octane, **emulated** (SGIEmu), IRIX 6.5 | 2 x R10000 at 200 MIPS | rmbenchx, gcc 2.8, MIPS IV n32 | 2 | 234.79 s | 2.65 s | 237.44 s | 2044 | identical (977e953f) |

About the emulated machine: inside an emulator, IRIX's clock advances with
the emulated instructions. The emulated times therefore show how many
instructions the test needs at the emulated speed (1000 or 200 MIPS per
processor). They do not show how fast the host computer is. The 200 MIPS
run is the emulator's guess at a dual 200 MHz Octane (`hinv` says
"200 MHZ"): it executes one instruction per clock, while a real R10000
can do more or less than that depending on the code (caches, FPU
latencies), so a real dual R10000 Octane is the result we are waiting for.
At 1000 MIPS `hinv` reports "600 MHZ", a value derived from the emulated
timer, not a real clock speed.

![Emulated Octane (SGIEmu), 2 processors at 200 MIPS](docs/octane-2cpu-200mips.png)

*Emulated Octane (SGIEmu), 2 processors at 200 MIPS, the speed of a dual
200 MHz R10000. GNOME was starting while this run was rendering, so the
time is a little pessimistic.*

Real machines: send your `RESULT` line (see the [README](README.md)) and
it will be added here.
