using System.Diagnostics;
using Unity.Burst;
using Unity.Entities;

// Fine-grained timing of the Machines module (dev "rrw.perf" / "rrw.mx.perf", budgets: Machines < 0.1 ms per active
// project, no single-frame spike > 2 ms). DEVTOOLS only: every entry point is [Conditional("DEVTOOLS")], so release builds
// compile the calls (and their arguments) away. Storage, nesting rules and the report live in Core RRWPerfDetail (group "mx").
// Sections: the root is MxT.Update (MachineDirectorSystem.OnUpdate); the step sections (A tracks, B projects, C gone, G guard,
// D per puppet, E housekeeping, R report) partition it; the X sections are cross-cutting inner operations (nested in the step
// that called them, so they are NOT additive with the steps). Counters count inner operations (motion evaluations, box
// tests, planner attempts, re-plan triggers, ECS structural changes...).
namespace RealisticRoadWorks.V3.Machines
{
    public enum MxT
    {
        Update,             // root: MachineDirectorSystem.OnUpdate (= PerfSlot.Machines without the Tag system)
        SyncProbe,          // dev rrw.mx.perf syncprobe=1: EntityManager.CompleteAllTrackedJobs() first (job work pending at Mod1)
        SyncReaders,        // MachineTrackStore.CompleteReaders (mover job of the last frame)
        Validate,           // dead puppets, dev respawns
        A_Tracks,           // step A: TrackBuilder.Update of every project + Flush
        A_TrackBuild,       //   layout rebuilds (incl. their full Y refresh)
        A_TrackRefreshY,    //   floor / verge Y refreshes (CPU terrain samples)
        A_TrackSlice,       //   time-sliced Y refresh of queued edges (<= kTrackSliceSamples per update)
        P_Budget,           // step A2: crew LOD (crew front distances, ranking, LOD flags)
        B_Projects,         // step B: ProcessProject (rosters, spawns, re-plans, truck cycles)
        B_Context,          //   PlanContext build (crew, lane policy, others list); also the guard's contexts
        B_SpawnFits,        //   spawn gate of not-yet-spawned roles (verge search, lane fit)
        B_Spawn,            //   Spawn() incl. deferred attempts and the entity creation
        B_Replan,           //   non-truck role plans (Replan dispatch + planners)
        B_TruckOut,         //   Choreo.PlanTruckOutbound (all attempts)
        B_TruckReturn,      //   Choreo.PlanTruckReturn (all attempts)
        B_GraderGuard,      //   excavator <-> grader guard
        B_ReleaseCheck,     //   guard-held puppet release check
        B_HandOver,         //   hand-over between crew layouts (keep / re-map / leave)
        C_Gone,             // step C: puppets of vanished projects
        G_Guard,            // step G: separation guard total
        G_Boxes,            //   boxes now + broad-phase path samples
        G_Hash,             //   spatial hash of the swept bounds (candidate pairs)
        G_Pairs,            //   pair loop (narrow phase FirstContact + resolve)
        G_Resolve,          //   yields / brakes / back-outs of predicted contacts
        G_Deadlocks,
        G_RetryLeavers,
        G_Stuck,
        M_Monitor,          // step M: speed / acceleration invariant (MxSpeed.Observe)
        D_PerPuppet,        // step D: visibility, despawn, loads, lights, plan writes
        D_ReadTransform,    //   EntityManager reads of Transform / CullingInfo (sync with vanilla jobs writing them)
        D_Loads,            //   truck load steps (SetAmount / respawn)
        D_PlanWrite,        //   SetComponentData(MachinePlan)
        D_Budget,           //   global budget eviction
        DevWatch,           // dev rrw.mx.check watch=1 overlap count
        E_Housekeeping,     // step E
        R_Report,           // step R: machine report (MachineReport.Write)
        R_ReportCache,      //   plan sample cache rebuilds
        P_Cache,            // plan sample cache fills (State + Pose of a committed plan on the grid)
        X_FindConflict,     // Choreo.FindConflict (planner / release / leave / back-out checks)
        X_PairConflict,     // Choreo.PairConflict (grader guard, yield candidates)
        X_PlanYield,
        X_BackOut,
        X_PlanLeave,
        X_PostSite,         // MakePostSite (leave plan + retag)
        X_VergeClear,       // Choreo.VergeClear (verge slot validation)
        X_SearchTreeSync,   //   GetStaticSearchTree / GetNetSearchTree + dependency.Complete()
        X_EcsStructural,    // every structural change (CreateEntity / AddComponent / Deleted): the first one in a frame completes ALL jobs
        Tag,                // MachineTagSystem (Mod4, outside Update)
        // ---- rollers, IK and dig events: outside the Update root like Tag
        R_Roller,           // RollerRenderSystem (PreCulling): pose / LOD / lights of every roller unit (target <= 0.02 ms per roller)
        B_Ik,               // MachineBoneSystem (Rendering): IK solves of the dig / grader rig puppets (target <= 0.01 ms per IK puppet)
        E_Events,           // inside Update: the Mod1 dig event pass (truck loads, dust puffs, puff expiry)
        B_Rollers,          // inside Update: roller roster (spawn / plan / hand-over) of every crew
        Count,
    }

    public enum MxC
    {
        StateEvals,         // MachineMotion.State on the main thread (Director update only)
        StateFrontRel,      //   ... of front-relative legs (3 leg evaluations each: finite-difference speed)
        PoseEvals,          // MachineMotion.Pose (5 track lookups each)
        FrontEvals,         // MachineMotion.Front (PhasePlan.MainFront)
        BoxAt,              // Choreo.BoxAt (State + Pose)
        ObbOverlap,         // Choreo.Overlap (SAT box pair tests)
        FcNear,             // FindConflict: neighbours kept by the prefilter (sum)
        FcSteps,            // FindConflict: time steps
        FcPairTests,        // FindConflict: box pair tests
        FcConflicts,        // FindConflict: conflicts found
        PcSteps,            // PairConflict time steps
        GuardItems,         // separation guard: puppets boxed
        GuardBroadSamples,  //   broad-phase path samples
        GuardPairs,         //   pairs considered (n(n-1)/2)
        GuardBroadPass,     //   pairs whose swept bounds meet (FirstContact runs)
        GuardContactSteps,  //   FirstContact time steps (2 BoxAt each)
        GuardContacts,      //   predicted contacts
        GuardBrakes,        //   brake splices committed
        GuardBackOutTries,
        ReplanCalls,        // Replan() (non-truck roles, also from spawns / non-work truck activities)
        ReplanForced,       // non-truck re-plan because force was set
        ReplanTimer,        // non-truck re-plan because now >= ReplanAt
        ReplanMinimal,      // non-truck re-plan because of a mode-D span switch
        ForcePhase,         // force reasons (counted per live role and update; one update can count several)
        ForceModelReset,
        ForceWorking,
        ForceTrackRev,
        ForcePlannedPhase,
        ForceFrontModel,    //   FrontA.Swap / FrontA.Floor differs from the project's front
        ForceIntent,        //   IntentKey changed
        ModelReanchors,     // front model updated in place on the role plans (ProgressModel re-anchor; invalidates report caches)
        TruckOutAttempts,
        TruckReturnAttempts,
        StandStills,
        ReportPuppets,
        ReportNowSamples,
        ReportCacheRebuilds,
        ReportCacheSamples,
        ReportOpenSamples,  // open-ended front-relative legs, sampled every update
        TrackRebuilds,
        TrackRefreshEdges,
        TrackRefreshAll,
        TerrainSamples,     // CPU terrain height samples (RefreshY)
        TrackFlushLayout,
        TrackFlushData,
        Spawns,
        SpawnDeferred,      // Spawn() attempts that returned without a puppet (retried next update)
        Despawns,
        Respawns,
        PostSites,
        EcsCreate,
        EcsDeleted,         // entities marked Deleted (puppet + descendants)
        EcsEffectsUpdated,
        EcsBatchesUpdated,
        EcsRetag,
        PlanWrites,
        SetAmounts,
        VergeClears,
        SearchTreeSyncs,
        SlotFreeTests,      // SlotFree / SpotFree (resting / current boxes of every other puppet)
        AllocBytes,         // approx. managed bytes allocated during the Director update (GC.GetTotalMemory delta, >= 0)
        TagScanned,         // MachineTagSystem: Created+Owner entities walked
        TagTagged,
        // ---- plan sample caches, planning budget, time-sliced tracks
        SampleRebuilds,     // plan sample cache invalidated (plan / front model / track / clock changed)
        SampleFills,        // grid samples evaluated (State + Pose)
        SampleHits,         // grid samples served from the cache
        PlanDeferred,       // truck planning attempts deferred to the next update (global budget)
        ModelReplans,       // front-relative roles re-planned because a model re-anchor changed the front speed
        GuardStaticPairs,   // guard pairs of two puppets at rest (one overlap test instead of the horizon)
        TrackSliceQueued,   // edges queued for a time-sliced Y refresh
        TrackSpawnRefresh,  // pending edges refreshed at once for a spawn point
        TrackCarryOver,     // rebuilds that carried floor / verge heights over
        // ---- rollers, IK and dig events
        RollerPoses,        // roller units posed (RollerRenderSystem)
        RollerPlans,        // roller compaction / park / approach plans
        IkSolves,           // IK solves in the bone pass (dig + grader)
        IkRefines,          // ... that ran the Gauss-Newton refinement (> 5 mm residual)
        DigCycleBuilds,     // dig cycle target builds (Mod1, one per cycle boundary per IK excavator)
        DigEvents,          // Breakout / Dump events processed
        DustPuffs,          // dust puff emitters spawned
        DustPuffsSkipped,   // ... skipped (budget, interval, distance, culled)
        // ---- re-anchor cache reuse, report cache rebuilds, resumable conflict scans
        ReanchorKept,       // in-place re-anchor: no remaining leg reads FrontA, or no cached sample moved -> every cache kept
        ReanchorPartial,    // ... the samples past the kept part are dropped (refilled on demand)
        ReanchorFull,       // ... every sample dropped (the new model becomes the cache reference)
        SamplePartialDrops, // plan sample caches whose far part was dropped (SampleRebuilds counts the full rebuilds only)
        ReportSoftRebuilds, // report caches rebuilt after a re-anchor (<= kReportSoftRebuilds per update)
        ReportSoftDeferred, // ... deferred to a later update (old zones kept meanwhile)
        ScanStarts,         // resumable conflict scans started (truck trips, leavers, synchronous checks)
        ScanSlices,         // ... scan slices that stopped on the time budget (continued next update)
        ScanRestarts,       // ... resumed scans restarted (a neighbour's committed plan changed)
        ScanExpired,        // ... candidates dropped because their scan outlived them (rebuilt with a departure lead)
        ScanAborted,        // synchronous checks cut by the per-update check budget (treated as a conflict, retried)
        YieldSteps,         // grader yield candidates evaluated (one per update)
        Count,
    }

    public static class MxPerf
    {
#if DEVTOOLS
        private static readonly int s_Base = RRWPerfDetail.Register("mx", Names(typeof(MxT), (int)MxT.Count), Names(typeof(MxC), (int)MxC.Count));
        private static readonly int s_CBase = s_Base < 0 ? -1 : s_Base + (int)MxT.Count;
        private static long s_Mem;

        // True while the Director update runs (after the mover job sync): gates the counters inside the job-shared motion code,
        // so evaluations of the mover job (worker threads) and of the bone system are never counted.
        public static bool Live;
        // dev rrw.mx.perf syncprobe=1: complete every tracked job at the start of the Director update in its own section
        public static bool SyncProbeOn;

        static string[] Names(global::System.Type t, int n)
        {
            var names = new string[n];
            for (int i = 0; i < n; i++)
            {
                string s = global::System.Enum.GetName(t, i) ?? ("s" + i);
                names[i] = s.Replace('_', '.');
            }
            return names;
        }
#endif

        [Conditional("DEVTOOLS")]
        public static void Begin(MxT s)
        {
#if DEVTOOLS
            if (s_Base >= 0) RRWPerfDetail.Begin(s_Base + (int)s);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void End(MxT s)
        {
#if DEVTOOLS
            if (s_Base >= 0) RRWPerfDetail.End(s_Base + (int)s);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void Count(MxC c, long n = 1)
        {
#if DEVTOOLS
            if (s_CBase >= 0) RRWPerfDetail.Count(s_CBase + (int)c, n);
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void CountIf(bool cond, MxC c)
        {
#if DEVTOOLS
            if (cond && s_CBase >= 0) RRWPerfDetail.Count(s_CBase + (int)c, 1);
#endif
        }

        // Counter for the job-shared pure motion code (MachineMotion): counts only on the main thread during the Director update.
        [Conditional("DEVTOOLS"), BurstDiscard]
        public static void Motion(MxC c)
        {
#if DEVTOOLS
            if (Live && s_CBase >= 0) RRWPerfDetail.Count(s_CBase + (int)c, 1);
#endif
        }

        // Root of one Director update (call right after the RRWPerf start; FrameEnd right before its stop).
        [Conditional("DEVTOOLS")]
        public static void FrameBegin(EntityManager em)
        {
#if DEVTOOLS
            if (s_Base < 0) return;
            RRWPerfDetail.Begin(s_Base + (int)MxT.Update);
            s_Mem = global::System.GC.GetTotalMemory(false);
            if (SyncProbeOn)
            {
                RRWPerfDetail.Begin(s_Base + (int)MxT.SyncProbe);
                em.CompleteAllTrackedJobs();
                RRWPerfDetail.End(s_Base + (int)MxT.SyncProbe);
            }
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void LiveOn()
        {
#if DEVTOOLS
            Live = true;
#endif
        }

        [Conditional("DEVTOOLS")]
        public static void FrameEnd()
        {
#if DEVTOOLS
            Live = false;
            if (s_Base < 0) return;
            long d = global::System.GC.GetTotalMemory(false) - s_Mem;
            if (d > 0) RRWPerfDetail.Count(s_CBase + (int)MxC.AllocBytes, d);
            RRWPerfDetail.End(s_Base + (int)MxT.Update);
#endif
        }
    }
}
