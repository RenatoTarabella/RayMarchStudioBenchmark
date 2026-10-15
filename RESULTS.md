# Results

RayMarch Studio Benchmark 1.0, 800 x 600. Lower times are better; compare
the **Rendering** column.

| Machine | Processors | Program | Threads | Rendering | Setup | Total | Pixels/s | Image check |
|---|---|---|---|---|---|---|---|---|
| Octane, **emulated** (SGIEmu), IRIX 6.5 | 2 x R10000 at 1000 MIPS | rmbenchx, gcc 2.8, MIPS IV n32 | 2 | 43.43 s | 0.53 s | 43.95 s | 11053 | identical (977e953f) |

About the emulated machine: inside an emulator, IRIX's clock advances with
the emulated instructions. The emulated times therefore show how many
instructions the test needs at the emulated speed (here 1000 MIPS per
processor). They do not show how fast the host computer is, and they are
not the times of a real Octane. On the emulator `hinv` reports "600 MHZ",
a value derived from the emulated timer, not a real clock speed.

Real machines: send your `RESULT` line (see the [README](README.md)) and
it will be added here.
