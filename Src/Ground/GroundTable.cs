using System.Collections.Generic;
using Game.Net;
using Unity.Entities;

// Ground module: composition clones + TerrainComposition profiles for works edges.
// Port of the terrain prototype (verified in game, including the reveal/hide flicker rule).
//
// GroundTable is the module-static clone table keyed by EDGE entity. It is deliberately independent of the
// SiteRegistry so a reveal hold / restore finishes even after the Director dropped the edge's record (lost site,
// completed construction, cancel). It is forgotten on OnGamePreload; LoadFixup cleans the clone entities left behind.
namespace RealisticRoadWorks.V3.Ground
{
    // Per-edge state machine.
    public enum GroundStage : byte
    {
        Vanilla = 0,     // no clones referenced by the edge
        Cloned = 1,      // Composition points at our clones; target profile applied at triggers
        RevealHold = 2,  // road visible again: clones kept at <= Bed(kRevealBedY) for kRevealHoldUpdates (verified reveal rule)
        Restoring = 3,   // vanilla TerrainComposition values written on the clones; repoint to the sources next update
    }

    public sealed class GroundEdgeState
    {
        public Entity Edge;
        public GroundStage Stage;

        // Slot 0 = edge, 1 = start node end, 2 = end node end (Composition.m_Edge / m_StartNode / m_EndNode).
        public readonly Entity[] Src = new Entity[3];          // vanilla composition the clone was made from
        public readonly Entity[] Clone = new Entity[3];        // our clone (Entity.Null = slot not cloned)
        public readonly TerrainProfile[] Applied = new TerrainProfile[3];  // values on the clone (valid when Written)
        public readonly bool[] Written = new bool[3];
        public readonly TerrainProfile[] Desired = new TerrainProfile[3];  // values to write at the next trigger
        public bool Pending;              // Desired differs from Applied (or not written yet)
        public bool Bypass;               // this update's write ignores the global trigger cap (hide frames, hold entry, rebuild, adoption)
        public bool VanillaPending;       // RevealHold: vanilla values requested; becomes Restoring once written
        public float AppliedT, AppliedY;  // edge slot MORPH t / y as applied (RoadWorksGround.m_AppliedT/Y)
        public float DesiredT, DesiredY;

        public uint StepUpdate;           // UpdateIndex of the last applied change
        public uint HoldStartUpdate;      // UpdateIndex the reveal hold began
        public uint RestoreWrittenUpdate; // UpdateIndex the vanilla values were written

        // Target memo (computed lazily, at most once per update; neighbours read it for node-end arbitration).
        public uint TargetUpdate = uint.MaxValue;
        public bool Wanted;               // registry edge, HideWanted, mode A, eligible, not faulted
        public bool HideWantedRaw;        // registry edge, HideWanted, mode A (ignores Ineligible / Faulted)
        public bool Rebuild;              // RuntimeFlags.NeedsRebuild this update (load / "Rebuild visuals"): bypass + rewrite
        public bool HideFrameFlag;        // RuntimeFlags.HideFrame this update (Director: creation / D1 hide frame)
        public WorksKind SiteKind;
        public WorksPhase Phase;
        public TerrainProfile Target;
        public float TargetT, TargetY;
        // Crew sections of the edge's project (RoadWorksRuntime.m_Crews, 0/1 = one front) the target
        // was evaluated with, and the edge's done fraction over those sections (PhasePlan.EdgeDoneFraction; dev / dump). A change
        // of m_Crews re-targets like a progress change (written at the next trigger update, same global cap).
        public int Crews = -1;
        public float DoneFraction;
        public int CrewChanges;
        public uint PendingSinceUpdate;   // UpdateIndex the edge became Pending (0 = not pending); rrw.check: trigger-cap starvation

        // Registry presence this update.
        public uint SeenUpdate = uint.MaxValue;
        public EdgeRecord Record;

        // Hide-frame bookkeeping (creation-frame hit / miss log).
        public bool PrevHideWanted;
        public uint WantedSinceUpdate;
        public bool MissLogged;

        // Eligibility: set when an adopted composition is no longer eligible for mode A.
        public bool Ineligible;
        public uint IneligibleCheckUpdate;
        public bool IneligibleLogged;

        // Orphan bookkeeping (non-site edges that still reference a clone).
        public uint OrphanSinceUpdate;
        public bool IsOrphan;

        // Natural ground sampling.
        public uint NaturalSinceUpdate;   // UpdateIndex Natural was first applied on the edge slot (0 = never)
        public bool NatCpuDone;           // construction CPU sample done (or skipped for good)
        public bool NatD0Done, NatD1Done; // demolition verge + GPU requests per phase
        public bool GpuInFlight;

        // Demolition completion: the Director deletes the edge once the output reads
        // Morph with m_AppliedT == 0. CompleteSinceUpdate = first update seen in demolition Complete (0 = not there).
        public uint CompleteSinceUpdate;
        public bool DemoReadyLogged;

        // Robustness / stats.
        public int Fails;
        public bool Faulted;              // repeated errors: forced through the hold to vanilla, never cloned again
        public int Triggers, Repoints, Reclones, Adoptions;
        public uint CreatedUpdate;

        public bool HasClones => Clone[0] != Entity.Null || Clone[1] != Entity.Null || Clone[2] != Entity.Null;

        public void ClearSlots()
        {
            for (int s = 0; s < 3; s++)
            {
                Src[s] = Entity.Null;
                Clone[s] = Entity.Null;
                Written[s] = false;
                Applied[s] = TerrainProfile.Vanilla;
                Desired[s] = TerrainProfile.Vanilla;
            }
            Pending = false;
            Bypass = false;
            VanillaPending = false;
            AppliedT = AppliedY = DesiredT = DesiredY = 0f;
        }
    }

    // A natural-ground estimate produced off the update (GPU readback callback) and applied at the next ModEnd.
    public struct NaturalResult
    {
        public Entity Edge;
        public float Above, Below;      // metres above / below grade (>= 0); NaN = no data
        public byte Phase;              // WorksPhase the request was made in (demolition: only applied in D0/D1)
        public string Source;
    }

    public static class GroundTable
    {
        public static readonly Dictionary<Entity, GroundEdgeState> Edges = new Dictionary<Entity, GroundEdgeState>();
        // clone -> UpdateIndex it was queued for destruction
        public static readonly Dictionary<Entity, uint> PendingDestroy = new Dictionary<Entity, uint>();
        // filled by GPU readback callbacks (main thread, outside the ECS update), drained by ExcavationSystem
        public static readonly List<NaturalResult> NaturalResults = new List<NaturalResult>();
        public static readonly object NaturalLock = new object();
        // clones whose vanilla source vanished (never with vanilla): left referenced with vanilla values, never adopted again
        public static readonly HashSet<Entity> Abandoned = new HashSet<Entity>();

        public static bool LoadFixupPending;
        public static bool HaveTriggered;
        public static uint LastTriggerUpdate;

        // Stats (rrw.ground.status / dumper).
        public static int TriggerUpdates, TriggeredEdges, ClonesMade, ClonesDestroyed, CreationHits, CreationMisses, Adoptions;
        public static int GpuRequests, GpuResults, GpuFailures;
        public static int CrewChanges;            // m_Crews changes seen on registry edges (re-targets)
        public static int MaxBatchEdges;          // most edges written in one trigger update (n crew fronts deepen n edges at once)
        public static uint MaxPendingAge;         // longest a Cloned edge waited for its write (updates; cap = kTerrainTriggerMinUpdates)
        // demolition edges that reached the Director's delete condition (Morph t = 0) and the slowest one (updates after Complete)
        public static int DemolitionReady;
        public static uint DemolitionReadyMaxWait;

        public static bool TryGet(Entity edge, out GroundEdgeState s) => Edges.TryGetValue(edge, out s);

        public static void QueueDestroy(Entity clone, uint now)
        {
            if (clone == Entity.Null) return;
            if (!PendingDestroy.ContainsKey(clone)) PendingDestroy.Add(clone, now);
        }

        public static bool OwnedByState(Entity clone)
        {
            foreach (var s in Edges.Values)
                if (s.Clone[0] == clone || s.Clone[1] == clone || s.Clone[2] == clone) return true;
            return false;
        }

        public static void Clear()
        {
            Edges.Clear();
            PendingDestroy.Clear();
            lock (NaturalLock) NaturalResults.Clear();
            HaveTriggered = false;
            LastTriggerUpdate = 0;
        }

        public static Entity Slot(Composition c, int s) => s == 0 ? c.m_Edge : s == 1 ? c.m_StartNode : c.m_EndNode;

        public static void SetSlot(ref Composition c, int s, Entity v)
        {
            if (s == 0) c.m_Edge = v; else if (s == 1) c.m_StartNode = v; else c.m_EndNode = v;
        }

        public static bool Same(Composition a, Composition b) => a.m_Edge == b.m_Edge && a.m_StartNode == b.m_StartNode && a.m_EndNode == b.m_EndNode;

        public static string SlotName(int s) => s == 0 ? "edge" : s == 1 ? "start" : "end";
    }
}
