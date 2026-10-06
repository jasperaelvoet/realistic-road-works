using System.Diagnostics;

// Fine-grained timing of PropSystem (dev "rrw.perf" detail lines "perfd px ..."; target
// < 0.05 ms per active project, no spike > 2 ms). DEVTOOLS only: every entry point is [Conditional("DEVTOOLS")] (calls and
// arguments compiled away in release). Storage and report: Core RRWPerfDetail, group "px". Root = PxT.Update
// (PropSystem.OnUpdate = PerfSlot.Props without HeapFillSystem).
namespace RealisticRoadWorks.V3.Props
{
    public enum PxT
    {
        Update,             // root: PropSystem.OnUpdate
        HealOverridden,     // Overridden query (empty almost always)
        CamDist,            // camera -> nearest chain point (cached samples)
        Projects,           // ProcessProject for every project
        Refresh,            //   chain edge stamps (runtime / ground reads)
        Topology,           //   chain-end node topology (+ all chain nodes / foreign roads on self-heal passes)
        HealScan,           //   time-sliced self-heal (alive check of 1/16 of the slots)
        FullDiff,           //   full diff (every slot group and device)
        FrontDiff,          //   front-only diff (slot groups with changed fills, crew depots, divider pick-up)
        Teardown,           // projects that left the registry
        Count,
    }

    public enum PxC
    {
        Projects,           // projects processed
        FullDiffs,
        FrontDiffs,
        FillEvals,          // PhasePlan.PropFill / depot fill evaluations
        FrontApplies,       // slots re-applied by front-only diffs (fill changed)
        Spawns,
        HealScanned,        // slots alive-checked by the self-heal
        HealMissing,        // dead / missing entities it found
        HealDeferred,       // heal-triggered full diffs postponed (one per update)
        AllocBytes,         // approx. managed bytes allocated during the update (GC.GetTotalMemory delta, >= 0)
        Count,
    }

    public static class PxPerf
    {
#if DEVTOOLS
        private static readonly int s_Base = RRWPerfDetail.Register("px", Names(typeof(PxT), (int)PxT.Count), Names(typeof(PxC), (int)PxC.Count));
        private static readonly int s_CBase = s_Base < 0 ? -1 : s_Base + (int)PxT.Count;
        private static long s_Mem;

        static string[] Names(global::System.Type t, int n)
        {
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = global::System.Enum.GetName(t, i) ?? ("s" + i);
            return names;
        }
#endif

        [Conditional("DEVTOOLS")]
        public static void Begin(PxT s)
        {
#if DEVTOOLS
            if (s_Base >= 0) RRWPerfDetail.Begin(s_Base + (int)s);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void End(PxT s)
        {
#if DEVTOOLS
            if (s_Base >= 0) RRWPerfDetail.End(s_Base + (int)s);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void Count(PxC c, long n = 1)
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
            RRWPerfDetail.Begin(s_Base + (int)PxT.Update);
            s_Mem = global::System.GC.GetTotalMemory(false);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void FrameEnd()
        {
#if DEVTOOLS
            if (s_Base < 0) return;
            long d = global::System.GC.GetTotalMemory(false) - s_Mem;
            if (d > 0) RRWPerfDetail.Count(s_CBase + (int)PxC.AllocBytes, d);
            RRWPerfDetail.End(s_Base + (int)PxT.Update);
#endif
        }
    }
}
