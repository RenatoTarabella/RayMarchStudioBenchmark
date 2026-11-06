// La scena (scene.h, scene.c, prims.c, sceneio.c): solo quello che serve
// alla scena del benchmark. Le forme e i contenitori che la scena non usa
// (istanze, clonatori, deformatori, Lathe, cavi, displacement) fermano la
// lettura con un messaggio invece di essere ignorati.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RmBench;

public static class T
{
    public const int Group = -1, Sphere = 0, Box = 1, Cylinder = 3;
}

public static class Op
{
    public const int Union = 0, Sub = 1, Inter = 2;
}

public static class OF
{
    public const uint Wire = 1, Render = 2, Lock = 4, Off = 8;
}

public struct Material
{
    public V3 Color;
    public double Diffuse, Specular, Shininess;
    public double Reflect, Roughness, Transparency, Ior;
    public bool Metal;
    public double Sheen;
    public V3 SheenColor;
    public double SheenRough;

    public static Material Default() => new()
    {
        Color = new V3(0.75, 0.3, 0.15), Diffuse = 1, Specular = 1, Shininess = 24, Ior = 1.5,
        SheenColor = new V3(1, 1, 1), SheenRough = 0.5
    };
}

public sealed class Obj
{
    public string Name;
    public int Type;
    public double[] Par = new double[10];
    public V3 Pos, Rot, Scl;
    public int Op;
    public double K, Round, Shell;
    public Material Mat;
    public bool InheritMat;
    public uint Flags;
    public Obj Parent;
    public List<Obj> Kids = new();
    public int Index;

    public bool IsContainer => Type < 0;
    public bool Enabled => (Flags & OF.Off) == 0;
}

public struct Light
{
    public bool Dir;
    public V3 Pos, Color;
    public double Intensity;
    public bool Shadow;
    public double Softness;
    public bool On;
}

public struct Camera { public V3 Pos, Target; public double Focal; }

public static class Floor { public const int Checker = 0, Solid = 1, Grid = 2; }
public static class Env { public const int Sky = 0, Studio = 1, Hdri = 2; }

public sealed class World
{
    public bool Floor = true;
    public V3 FloorA = new(0.42, 0.4, 0.38), FloorB = new(0.2, 0.2, 0.22);
    public double FloorScale = 1.5;
    public int FloorType = RmBench.Floor.Checker;
    public double FloorReflect;
    public V3 SkyTop = new(0.55, 0.7, 1), SkyHorizon = new(0.9, 0.95, 1), SkyLight = new(0.25, 0.3, 0.45), Ambient = new(0.08, 0.07, 0.06);
    public double Fog = 0.0015;
    public V3 FogColor = new(0.8, 0.85, 1);
    public bool Ao = true;
    public int Env = RmBench.Env.Sky;
    public string EnvPreset = "Softbox studio", EnvFile = "";
    public double EnvIntensity = 1, EnvRotation;
    public bool EnvBackground = true;
    public V3 BgColor = new(0.85, 0.85, 0.86);
    public bool EnvLights = true;
    public bool ToneMap;
    public double Exposure;
}

public sealed class Scene
{
    public const int MaxLights = 8;
    public List<Obj> Roots = new();
    public Light[] Lights = new Light[MaxLights];
    public int NLight;
    public Camera Cam;
    public World World = new();

    public Scene()
    {
        Lights[0] = new Light { Dir = true, Pos = new V3(-0.5, 0.8, 0.6), Color = new V3(1.3, 1.15, 0.95), Intensity = 1, Shadow = true, Softness = 8, On = true };
        NLight = 1;
        Cam = new Camera { Pos = new V3(2.9, 2.3, 3.5), Target = new V3(0, 0.6, 0), Focal = 2.2 };
    }

    /* i parametri di partenza delle forme che il benchmark usa */
    static double[] Defaults(int type) => type switch
    {
        T.Sphere => new[] { 0.5 },
        T.Box => new[] { 1, 1, 1, 0.05 },
        T.Cylinder => new[] { 0.5, 1, 0.05 },
        _ => Array.Empty<double>()
    };

    static int TypeFromKey(string k) => k switch
    {
        "group" => T.Group,
        "sphere" => T.Sphere,
        "box" => T.Box,
        "cylinder" => T.Cylinder,
        _ => throw new FormatException("object type not supported by the .NET port: " + k)
    };

    static int OpFromKey(string k) => k switch { "subtract" => Op.Sub, "intersect" => Op.Inter, _ => Op.Union };

    Obj Add(int type, Obj parent, string name)
    {
        var o = new Obj { Type = type, Name = name, Scl = new V3(1, 1, 1), Mat = Material.Default(), Flags = OF.Wire | OF.Render, Parent = parent };
        Defaults(type).CopyTo(o.Par, 0);
        (parent != null ? parent.Kids : Roots).Add(o);
        return o;
    }

    /* tutti gli oggetti in ordine d'albero (padre, poi figli) */
    public static void Walk(Obj o, List<Obj> out_)
    {
        out_.Add(o);
        foreach (var k in o.Kids) Walk(k, out_);
    }

    public List<Obj> WalkAll()
    {
        var l = new List<Obj>();
        foreach (var o in Roots) Walk(o, l);
        return l;
    }

    public static M34 ParentWorld(Obj p) => p == null ? M34.Identity : ObjWorld(p);

    public static M34 ObjWorld(Obj o)
    {
        M34 pw = ParentWorld(o.Parent), l = M34.Compose(o.Pos, o.Rot, o.Scl);
        return M34.Mul(pw, l);
    }

    // ---- la lettura (sceneio.c)

    sealed class Reader
    {
        public string T;
        public int P, Line = 1;

        static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r';

        public string Word()
        {
            while (P < T.Length && IsSpace(T[P])) P++;
            if (P >= T.Length || T[P] == '\n') return null;
            int s;
            if (T[P] == '"')
            {
                s = ++P;
                while (P < T.Length && T[P] != '"' && T[P] != '\n') P++;
                string w = T.Substring(s, Math.Min(P - s, 63));
                if (P < T.Length && T[P] == '"') P++;
                return w;
            }
            s = P;
            while (P < T.Length && !IsSpace(T[P]) && T[P] != '\n') P++;
            return T.Substring(s, Math.Min(P - s, 63));
        }

        public bool HasMore()
        {
            while (P < T.Length && IsSpace(T[P])) P++;
            return P < T.Length && T[P] != '\n';
        }

        public void NextLine()
        {
            while (P < T.Length && T[P] != '\n') P++;
            if (P < T.Length) P++;
            Line++;
        }

        public Exception Fail(string msg) => new FormatException($"line {Line}: {msg}");

        /* il numero come lo accetta fx_parse di RISC OS: [+-]cifre[.cifre] */
        public double Num()
        {
            string w = Word();
            if (w == null) throw Fail("number expected");
            int i = 0, digits = 0;
            long ip = 0;
            if (i < w.Length && (w[i] == '-' || w[i] == '+')) i++;
            while (i < w.Length && w[i] >= '0' && w[i] <= '9')
            {
                ip = ip * 10 + (w[i++] - '0');
                digits++;
                if (ip > 32767) throw Fail("number expected");
            }
            if (i < w.Length && w[i] == '.')
            {
                i++;
                while (i < w.Length && w[i] >= '0' && w[i] <= '9') { i++; digits++; }
            }
            if (i < w.Length || digits == 0) throw Fail("number expected");
            double v = double.Parse(w, NumberStyles.Float, CultureInfo.InvariantCulture);
            return v == 0 ? 0 : v;
        }

        public int INum() => (int)Math.Floor(Num());
        public V3 Vec() { double x = Num(), y = Num(), z = Num(); return new V3(x, y, z); }
    }

    public static Scene Read(string text)
    {
        var s = new Scene();
        var r = new Reader { T = text };
        var infile = new List<Obj>();
        bool fileLights = false;
        Obj cur = null;
        if (r.Word() != "RayMarchStudio") throw new FormatException("line 1: not a RayMarch Studio scene");
        r.NextLine();
        while (r.P < r.T.Length)
        {
            string w = r.Word();
            if (w == "#gpu") { s.ReadGpu(r, cur); r.NextLine(); continue; }
            if (w == null || w[0] == '#') { r.NextLine(); continue; }
            switch (w)
            {
                case "camera":
                    s.Cam.Pos = r.Vec(); s.Cam.Target = r.Vec(); s.Cam.Focal = r.Num();
                    break;
                case "light":
                {
                    if (!fileLights) { s.NLight = 0; fileLights = true; }
                    if (s.NLight >= MaxLights) throw r.Fail("too many lights");
                    var l = new Light { On = true };
                    l.Dir = r.Word() != "point";
                    l.Pos = r.Vec(); l.Color = r.Vec(); l.Intensity = r.Num();
                    l.Shadow = r.Num() != 0; l.Softness = r.Num();
                    s.Lights[s.NLight++] = l;
                    break;
                }
                case "snap":
                    r.Num(); r.Num();
                    break;
                case "world":
                    s.World.Floor = r.Num() != 0; s.World.Fog = r.Num(); s.World.Ao = r.Num() != 0;
                    break;
                case "object":
                {
                    int parent = r.INum();
                    int type = TypeFromKey(r.Word());
                    int op = OpFromKey(r.Word());
                    if (parent >= infile.Count) throw r.Fail("bad parent");
                    Obj pa = parent < 0 ? null : infile[parent];
                    cur = s.Add(type, pa, type == T.Group ? "Group" : "Object");
                    infile.Add(cur);
                    cur.Op = op;
                    cur.K = r.Num();
                    cur.Flags = (uint)r.INum() & (OF.Wire | OF.Render | OF.Lock | OF.Off);
                    string nw = r.Word();
                    if (nw != null) cur.Name = nw;
                    break;
                }
                default:
                    if (cur == null) throw r.Fail("object expected");
                    switch (w)
                    {
                        case "pos": cur.Pos = r.Vec(); break;
                        case "rot": cur.Rot = r.Vec(); break;
                        case "scale": cur.Scl = r.Vec(); break;
                        case "material":
                            cur.Mat.Color = r.Vec(); cur.Mat.Specular = r.Num(); cur.Mat.Shininess = r.Num();
                            break;
                        case "modify": cur.Round = r.Num(); cur.Shell = r.Num(); break;
                        case "param":
                            for (int j = 0; j < Defaults(cur.Type).Length; j++) cur.Par[j] = r.Num();
                            break;
                        default: throw r.Fail("unknown keyword");
                    }
                    break;
            }
            r.NextLine();
        }
        /* Off vale solo per i generatori, che qui non ci sono */
        foreach (var o in infile) o.Flags &= ~OF.Off;
        return s;
    }

    void ReadGpu(Reader r, Obj cur)
    {
        string w = r.Word();
        switch (w)
        {
            case "material":
            {
                if (cur == null) throw r.Fail("object expected");
                ref Material m = ref cur.Mat;
                m.Reflect = R.Clamp(r.Num(), 0, 1);
                m.Roughness = R.Clamp(r.Num(), 0, 1);
                m.Transparency = R.Clamp(r.Num(), 0, 1);
                m.Ior = R.Clamp(r.Num(), 1, 3);
                m.Metal = r.Num() != 0;
                if (r.HasMore())
                {
                    m.Sheen = R.Clamp(r.Num(), 0, 1);
                    double sr = r.Num(), sg = r.Num(), sb = r.Num();
                    m.SheenColor = new V3(R.Clamp(sr, 0, 1), R.Clamp(sg, 0, 1), R.Clamp(sb, 0, 1));
                    m.SheenRough = R.Clamp(r.Num(), 0, 1);
                }
                break;
            }
            case "floor":
            {
                string tw = r.Word();
                World.FloorType = tw == "solid" ? Floor.Solid : tw == "grid" ? Floor.Grid : Floor.Checker;
                World.FloorA = r.Vec();
                World.FloorB = r.Vec();
                World.FloorScale = R.Clamp(r.Num(), 0.01, 100);
                World.FloorReflect = R.Clamp(r.Num(), 0, 1);
                break;
            }
            case "env":
            {
                string tw = r.Word();
                World.Env = tw == "studio" ? Env.Studio : tw == "hdri" ? Env.Hdri : Env.Sky;
                World.EnvPreset = r.Word() ?? "";
                World.EnvIntensity = R.Clamp(r.Num(), 0, 100);
                World.EnvRotation = r.Num();
                World.EnvBackground = r.Num() != 0;
                World.BgColor = r.Vec();
                World.EnvLights = r.Num() != 0;
                World.EnvFile = r.Word() ?? "";
                if (World.Env == Env.Hdri) throw r.Fail("HDRI environments are not supported by the .NET port");
                break;
            }
            case "light":
            {
                if (NLight == 0) throw r.Fail("light expected");
                r.Word();
                Lights[NLight - 1].On = r.Num() != 0;
                Lights[NLight - 1].Intensity = r.Num();
                break;
            }
            case "matparent":
                if (cur == null) throw r.Fail("object expected");
                cur.InheritMat = true;
                break;
            case "tone":
                World.ToneMap = r.Word() == "neutral";
                World.Exposure = R.Clamp(r.Num(), -10, 10);
                break;
            case "displace": case "symmetry": case "profile": case "cable": case "cablepts": case "cablecurve":
            case "instance": case "cloner": case "deformer":
                throw r.Fail($"#gpu {w} is not supported by the .NET port");
        }
    }
}
