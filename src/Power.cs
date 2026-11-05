// The "Simmer" power plan and the processor settings written into it. Nothing here ever writes to
// another plan: every write goes through Plan.Write, which refuses any GUID but our own plan.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerCap
{
    static class G
    {
        public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        // Index = efficiency class / cluster (0, 1, 2). Windows accepts all three on this SoC.
        public static readonly Guid[] FreqMax = { new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100"), new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101"), new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e102") };
        public static readonly Guid[] ThrottleMax = { new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed"), new Guid("bc5038f7-23e0-4960-96da-33abaf5935ee") };
        public static readonly Guid[] Epp = { new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6865") };
        public static readonly Guid BoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        public static readonly Guid BoostPolicy = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b59");
        public static readonly Guid MaxCores = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");
        public static readonly Guid MaxCores1 = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029");
        // Heterogeneous scheduling policy (long / short running threads): 2 prefer performant (Windows default), 3 efficient only
        public static readonly Guid Hetero = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
        public static readonly Guid HeteroShort = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");
    }

    // What PowerCap writes into its plan. Mhz 0 = no cap for that cluster.
    class Caps
    {
        public int[] Mhz = new int[3];
        public int MaxState = 100;   // % (PROCTHROTTLEMAX, all clusters)
        public int Cores = 100;      // % of cores unparked
        public int Epp = 50;         // 0 = performance .. 100 = efficiency
        public int Boost = 2;        // PERFBOOSTMODE: 0 off, 2 aggressive, 3 efficient enabled, 4 efficient aggressive
        public int Hetero = 2;       // scheduling policy: 2 prefer fast cores (default), 3 efficient cores only
        public Caps Clone() { var c = (Caps)MemberwiseClone(); c.Mhz = (int[])Mhz.Clone(); return c; }
        public bool SameAs(Caps o)
        {
            return o != null && Mhz[0] == o.Mhz[0] && Mhz[1] == o.Mhz[1] && Mhz[2] == o.Mhz[2] && MaxState == o.MaxState
                && Cores == o.Cores && Epp == o.Epp && Boost == o.Boost && Hetero == o.Hetero;
        }
        public override string ToString()
        {
            return string.Format("mhz {0}/{1}/{2} state {3}% cores {4}% epp {5} boost {6}", Mhz[0], Mhz[1], Mhz[2], MaxState, Cores, Epp, Boost);
        }
    }

    static class Plan
    {
        public const string Name = "Simmer", OldName = "PowerCap";
        // Safety floors, whatever the UI, a preset or the governor asks for.
        public const int MinState = 20, MinCores = 25, MinMhz = 700;

        static Guid _guid;
        static readonly object Lock = new object();
        static Caps _written;   // last caps written (the hidden settings can be read back, but this avoids rewrites)

        public static Guid Guid { get { lock (Lock) { if (_guid == Guid.Empty) _guid = FindOrCreate(); return _guid; } } }

        static Guid FindOrCreate()
        {
            var buf = new byte[16];
            for (uint i = 0; ; i++)
            {
                uint sz = 16;
                if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /*ACCESS_SCHEME*/, i, buf, ref sz) != 0) break;
                var g = new Guid(buf);
                var fn = FriendlyName(g);
                if (fn == Name) return g;
                if (fn == OldName)   // the plan from before the rename: keep it, new name
                {
                    Native.PowerWriteFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, Name, (uint)((Name.Length + 1) * 2));
                    return g;
                }
            }
            var bal = G.Balanced;
            IntPtr p;
            if (Native.PowerDuplicateScheme(IntPtr.Zero, ref bal, out p) != 0) throw new Exception("could not create the Simmer plan");
            var ng = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            Native.LocalFree(p);
            Native.PowerWriteFriendlyName(IntPtr.Zero, ref ng, IntPtr.Zero, IntPtr.Zero, Name, (uint)((Name.Length + 1) * 2));
            return ng;
        }

        static string FriendlyName(Guid g)
        {
            uint sz = 0;
            Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref sz);
            if (sz == 0) return "";
            var b = new byte[sz];
            if (Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, b, ref sz) != 0) return "";
            return Encoding.Unicode.GetString(b).TrimEnd('\0');
        }

        public static Guid Active()
        {
            IntPtr p;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return Guid.Empty;
            var g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            Native.LocalFree(p);
            return g;
        }
        public static bool IsActive { get { return Active() == Guid; } }
        public static string ActiveName { get { var a = Active(); return a == Guid ? Name : a == G.Balanced ? "Balanced" : FriendlyName(a); } }

        static void Write(Guid setting, int value)
        {
            var g = Guid;
            if (g == G.Balanced || g == Guid.Empty) throw new InvalidOperationException("refusing to write outside the Simmer plan");
            var sub = G.SubProcessor;
            Native.PowerWriteACValueIndex(IntPtr.Zero, ref g, ref sub, ref setting, (uint)value);
            Native.PowerWriteDCValueIndex(IntPtr.Zero, ref g, ref sub, ref setting, (uint)value);
        }

        public static int Read(Guid scheme, Guid setting, bool ac)
        {
            var sub = G.SubProcessor; uint v;
            uint rc = ac ? Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v)
                         : Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
            return rc == 0 ? (int)v : -1;
        }

        public static Caps Clamp(Caps c)
        {
            var r = c.Clone();
            for (int i = 0; i < 3; i++) if (r.Mhz[i] != 0) r.Mhz[i] = Math.Max(MinMhz, Math.Min(5000, r.Mhz[i]));
            r.MaxState = Math.Max(MinState, Math.Min(100, r.MaxState));
            r.Cores = Math.Max(MinCores, Math.Min(100, r.Cores));
            r.Epp = Math.Max(0, Math.Min(100, r.Epp));
            if (r.Boost < 0 || r.Boost > 6) r.Boost = 2;
            if (r.Hetero != 3) r.Hetero = 2;
            return r;
        }

        // Writes only the settings that changed, then activates the plan so they take effect.
        public static void Apply(Caps c)
        {
            lock (Lock)
            {
                c = Clamp(c);
                var w = _written;
                for (int i = 0; i < 3; i++)
                {
                    if (w == null || w.Mhz[i] != c.Mhz[i]) Write(G.FreqMax[i], c.Mhz[i]);
                    if (w == null || w.MaxState != c.MaxState) Write(G.ThrottleMax[i], c.MaxState);
                    if (w == null || w.Epp != c.Epp) Write(G.Epp[i], c.Epp);
                }
                if (w == null || w.Boost != c.Boost) { Write(G.BoostMode, c.Boost); Write(G.BoostPolicy, 100); }
                if (w == null || w.Hetero != c.Hetero) { Write(G.Hetero, c.Hetero); Write(G.HeteroShort, c.Hetero); }
                if (w == null || w.Cores != c.Cores) { Write(G.MaxCores, c.Cores); Write(G.MaxCores1, c.Cores); }
                _written = c;
                var g = Guid;
                Native.PowerSetActiveScheme(IntPtr.Zero, ref g);
            }
        }

        public static void Forget() { lock (Lock) _written = null; }

        public static void ActivateBalanced()
        {
            var b = G.Balanced;
            Native.PowerSetActiveScheme(IntPtr.Zero, ref b);
        }
    }
}
