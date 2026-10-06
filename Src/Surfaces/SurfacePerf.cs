using System.Diagnostics;

// Fine-grained timing of SurfaceAreaSystem (dev "rrw.perf" / "rrw.mx.perf"; budget: Surfaces < 0.15 ms, no spike > 2 ms).
// DEVTOOLS only: every entry point is [Conditional("DEVTOOLS")] (calls and arguments compiled away in release). Storage and
// report: Core RRWPerfDetail, group "sx". Root = SxT.Update (SurfaceAreaSystem.OnUpdate = PerfSlot.Surfaces); the step
// sections partition it, BuildStrip / AreaEcs are nested in the steps that call them.
namespace RealisticRoadWorks.V3.Surfaces
{
    public enum SxT
    {
        Update,             // root: SurfaceAreaSystem.OnUpdate
        HeightData,         // TerrainSystem.GetHeightData
        Prologue,           // stale pending, style sync / re-batch, dev lines
        LostEdges,          // ProcessLostEdges (splits, combines)
        Projects,           // ProcessProjects (per edge: ends, layer diff, temp lines)
        RefreshEnds,        //   junction-end classification / trims
        BuildStrip,         //   strip polygon + vertex Y (CPU terrain samples)
        TempLines,          //   yellow temporary lines
        Caps,               // ProcessCaps (junction caps)
        ScarConflicts,
        Scars,
        Retiring,
        Housekeeping,       // orphan sweep (query over every RRW area) / project GC
        AreaEcs,            // structural changes: definition CreateEntity / Updated / Deleted (the first per frame completes ALL jobs)
        Count,
    }

    public enum SxC
    {
        Edges,              // edges processed
        Strips,             // strip polygons built
        Vertices,           // vertex Y samples
        Spawns,
        Rewrites,
        Deletes,
        EndsRefresh,
        OrphanSweeps,
        OrphanScanned,      // areas walked by the orphan sweep
        AllocBytes,         // approx. managed bytes allocated during the update (GC.GetTotalMemory delta, >= 0)
        Topology,           // piece-topology row rewrites (piece count / band kind changed)
        BypassDeferred,     // projects whose bypass work was spread to a later update
        Count,
    }

    public static class SxPerf
    {
#if DEVTOOLS
        private static readonly int s_Base = RRWPerfDetail.Register("sx", Names(typeof(SxT), (int)SxT.Count), Names(typeof(SxC), (int)SxC.Count));
        private static readonly int s_CBase = s_Base < 0 ? -1 : s_Base + (int)SxT.Count;
        private static long s_Mem;

        static string[] Names(global::System.Type t, int n)
        {
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = global::System.Enum.GetName(t, i) ?? ("s" + i);
            return names;
        }
#endif

        [Conditional("DEVTOOLS")]
        public static void Begin(SxT s)
        {
#if DEVTOOLS
            if (s_Base >= 0) RRWPerfDetail.Begin(s_Base + (int)s);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void End(SxT s)
        {
#if DEVTOOLS
            if (s_Base >= 0) RRWPerfDetail.End(s_Base + (int)s);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void Count(SxC c, long n = 1)
        {
#if DEVTOOLS
            if (s_CBase >= 0) RRWPerfDetail.Count(s_CBase + (int)c, n);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void FrameBegin()
        {
#if DEVTOOLS
            if (s_Base < 0) return;
            RRWPerfDetail.Begin(s_Base + (int)SxT.Update);
            s_Mem = global::System.GC.GetTotalMemory(false);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void FrameEnd()
        {
#if DEVTOOLS
            if (s_Base < 0) return;
            long d = global::System.GC.GetTotalMemory(false) - s_Mem;
            if (d > 0) RRWPerfDetail.Count(s_CBase + (int)SxC.AllocBytes, d);
            RRWPerfDetail.End(s_Base + (int)SxT.Update);
#endif
        }
    }
}
