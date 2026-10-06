using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

// Props module state. Everything here is runtime-only: the components have no ISerializable and every prop
// carries LivePath (EcsUtil.TagDerived), so nothing of this module is ever written to a save.
namespace RealisticRoadWorks.V3.Props
{
    // Module tag on every prop entity PropSystem spawns (Overridden self-heal query, dev dumps).
    public struct RRWProp : IComponentData
    {
        public uint m_ProjectId;
        public int m_Key;          // PropKeys.Make(kind, sub, k)
    }

    // Target Quantity.m_Fullness (percent, 0..100) of a QuantityObject heap we own. HeapFillSystem writes it right after
    // QuantityUpdateSystem (which resets every Updated/BatchesUpdated quantity object to its owner's stock = 0 for us).
    public struct RRWHeapFill : IComponentData
    {
        public byte m_Fullness;
    }

    // Slot families. The first seven mirror PhasePlan.PropGroup (same numeric values); barriers are laid out by this module.
    // PropGroup.Divider (7) / Signs (8) are NOT slot groups (PhasePlan.SlotCount returns 0 for them and the slot loop
    // skips them explicitly): PropKind 7/8 stay BarrierStart/End; the staged-traffic devices use the kinds below.
    internal enum PropKind : byte
    {
        SurveyCone = 0,
        EdgeCone = 1,
        SpoilHeap = 2,
        DumpHeap = 3,
        Windrow = 4,
        Rubble = 5,
        Crew = 6,
        BarrierStart = 7,
        BarrierEnd = 8,
        // fences, divider, signal
        Fence = 9,          // site fence panels (FenceStyle.Footprint, sub 0/1) / kerb fence panels (FenceSides, sub 4 + side)
        CentreCone = 10,    // divider between the open and the works half (sub 1 = cones / cone lamps, sub 2 = dev barrier divider)
        Signal = 11,        // flashing-amber head at the open-direction entry (sub = end), unowned
        // staged-traffic devices
        FenceCone = 12,     // SafetyCone02 at each kerb fence run end and at each side of a driveway gap (sub = side)
        Sign = 13,          // one-way (sub 0) / speed plate (sub 1) at the open-direction entry, no-entry (sub 2) at the closed one (k = end)
        ConePair = 14,      // 45-degree cone pair straddling the kerb end of each closed-end line (sub = end, k = 0/1)
        // crew props of crews 1..n-1 at their section starts (PropCrewLayout). sub = depot id * 2
        // (+1 = the two tall cones), k = slot 0..2. The depot id is the reduced fraction of the boundary, so a boundary two
        // crew layouts share keeps its entities when the crew count is re-latched.
        CrewDepot = 15,
        // upgrade works (mode H) band devices, laid out from the bands' sub-strips (PropUpgrade.cs). sub = chain band * 4 + role
        BandDivider = 16,   // cones (sub < 32) / barriers (sub >= 32) along the inner edge of a band's closed part, next to an open lane
        BandTaper = 17,     // diagonal cones at the approach end of a band's closed part
        BandEnd = 18,       // barrier line across a band's closed part at each end
        BandFence = 19,     // fence panels: kerb fence beside an open sidewalk, verge fence on the new road edge of a removed strip
        BandFenceCone = 20, // a cone at each band fence run end and at each side of an access gap
        BandCone = 21,      // cones along the lane edges of a band that carries traffic (sub < 32), centre-line cones (sub 32)
        Count = 22,
    }

    // Traffic state of one strip of an upgrade works edge for the band devices (PropUpgrade.cs).
    internal enum StripState : byte
    {
        Open = 0,       // carries traffic (or pedestrians) now
        Draining = 1,   // closed (group closed or lane dropped) but not ready yet: vehicles may still be on it
        Ready = 2,      // closed and drained (group in WorkZonesReady, or a dropped lane with registered blockers)
        Free = 3,       // no lane at all: verge, median, terrain outside the new road
    }

    // How a prop's Y is found.
    internal enum YMode : byte
    {
        Ground = 0,     // CPU terrain at spawn, NO Elevation: vanilla GroundHeightSystem keeps it on the terrain (verge props)
        Curve = 1,      // road curve Y (visible road surface), Elevation{0, 0}
        Floor = 2,      // GroundMath.FloorWorldY (trench floor while hidden, curve Y when visible), Elevation{0, 0}
        Verge = 3,      // max(TerrainY, grade), Elevation{0, 0} (D1/D2 edge cones)
        Sidewalk = 4,   // curve Y + composition surface max (mode-D crew props on the sidewalk), Elevation{0, 0}
        // chord-placed panels. Y = min(curve Y at both feet) - kPanelDrop (+ sidewalk lift), vertical, Elevation{0, 0}
        Panel = 5,      // on the carriageway (FenceLateral.Inset)
        PanelSidewalk = 6, // on the sidewalk (FenceLateral.Sidewalk)
    }

    internal enum RotMode : byte
    {
        Tangent = 0,    // yaw along the curve, + long-axis rule (a prefab longer in X than Z is turned so its long side runs along the road)
        Across = 1,     // yaw along the curve WITHOUT the long-axis rule (barrier lines across the carriageway)
        Random = 2,     // random yaw (cones)
        Facing = 3,     // local +Z along the CHAIN direction (+u) without the long-axis rule, then YawExtra (signs, signal head)
        Chord = 4,      // world placement (Want.World / WorldDir): long axis along the chord, pivot-corrected (fence panels)
    }

    // A building access point on a chain edge (Building.m_RoadEdge = the edge, m_CurvePosition), chain frame.
    internal struct AccessPoint
    {
        public Entity Building;
        public Entity Edge;
        public float U;              // chain u of the access point
        public byte Side;            // 0 = chain-left, 1 = chain-right
    }

    internal static class PropKeys
    {
        public static int Make(PropKind kind, int sub, int k) => ((int)kind << 24) | ((sub & 0xFF) << 16) | (k & 0xFFFF);
        public static PropKind Kind(int key) => (PropKind)((key >> 24) & 0xFF);
        public static int Sub(int key) => (key >> 16) & 0xFF;
        public static int K(int key) => key & 0xFFFF;
        public static string Name(int key) => Kind(key) + "/" + Sub(key) + "/" + K(key);
    }

    // One fixed chain slot (project, kind, sub, k) and the entity currently standing in it.
    internal sealed class PropSlot
    {
        public int Key;
        public Entity Entity;
        public Entity Prefab;
        public Entity SiteEdge;        // works edge the prop is placed on / tagged with (RRWDerived.m_Site)
        public Entity Owner;           // late Owner node requested for it (Entity.Null = unowned)
        public uint GeoStamp;          // edge geometry stamp at the last placement
        public uint YStamp;            // edge floor stamp at the last placement (explicit-Y props only)
        public byte Variant;           // placement variant (cone road / verge, ...): a change re-places
        public YMode Y;
        public bool Explicit;          // carries Elevation{0, 0}: Props owns its Y
        public bool Heap;              // QuantityObject: RRWHeapFill drives the mesh step
        public bool Dust;              // dust-emitting heap variant (fixed for the entity's lifetime)
        public byte Fullness;          // last written RRWHeapFill percent
        public ushort Seed;            // PseudoRandomSeed (beacon blink phase), deterministic per slot
        public float3 Pos;
        public quaternion Rot;
        public uint Seen;              // pass id of the last diff that wanted this slot
        public float U;                // chain u of the slot at the last placement (footprint check)
        public bool AllowOutside;      // the only slots allowed outside [TrimU0, TrimU1]: the barrier line beyond
                                       // the excavated cul-de-sac cap of a HIDDEN dead end (natural ground, no road there)
        // re-placement inputs
        public uint PlaceKey;          // hash of the slot's placement inputs (lateral, yaw, rot mode, world chord): a change re-places
        public float3 FootA, FootB;    // Panel Y modes: XZ of both feet at the last placement (rrw.props.check re-derives the Y there)
        public float Lift;             // Panel Y modes: sidewalk lift used at the last placement
        // self-heal bookkeeping
        public int Index = -1;         // position in ProjectProps.SlotList (O(1) removal, time-sliced self-heal scan)
    }

    // A permanent road that is NOT a works road and lies next to the chain (connected at one of its
    // nodes, or found under an Overridden prop). No prop may stand inside it.
    internal sealed class ForeignEdge
    {
        public Entity Edge;
        public Colossal.Mathematics.Bezier4x3 Curve;
        public float HalfWidth;
        public bool Connected;        // found through a chain node's ConnectedEdge buffer (rebuilt on topology changes)
    }

    // Everything Props tracks for one project. Kept in a module-static dictionary (not in the registry slot) so props of
    // a project whose record vanished (completion, instant cancel) are still torn down the same frame.
    internal sealed class ProjectProps
    {
        public uint Id;
        public readonly Dictionary<int, PropSlot> Slots = new Dictionary<int, PropSlot>(64);
        public readonly List<Entity> OwnerNodes = new List<Entity>(4);   // distinct late owners in use (death check)
        public uint LastKey;          // process trigger hash (0 = never processed / dirty)
        public uint Pass;             // diff pass counter
        public bool Far;              // camera farther than kPropFarDistance from the NEAREST point of the chain (hysteresis)
        public float CamDist = -1f;   // XZ camera pivot -> nearest chain point this update (-1 = unknown: falls back to the front distance)
        public int FarChanges;        // far on/off transitions (dev)
        public int Thin = -1;         // spacing thinning level (budget <= kPropBudgetPerSite), sticky per layout
        public uint ThinLayout;       // layout hash Thin was computed for
        public int Estimate;          // estimated prop count at Thin (global budget)
        public int ChainRevision = int.MinValue;   // ProjectRecord.Revision the chain cache was built for
        public int RegistryRevision = int.MinValue;
        public readonly List<ChainEdge> Chain = new List<ChainEdge>(8);
        public bool RespawnAll;       // NeedsRebuild / dev respawn: replace every entity (new first, then delete old)
        public uint LastRebuildUpdate;// last update a NeedsRebuild flag was seen (respawn only on its first update)
        public bool OwnersDirty;      // an owner node died: re-own this update
        public int Spawned, Deleted, Replaced, Moved;   // counters (dev)

        // ---- End topology, foreign roads, fences
        // Chain-end node topology (hash of the node + its NON-works connected edges) and of every chain node.
        public bool TopoKnown;
        public uint TopoStart, TopoEnd, TopoAll;
        public int TopoAllRevision = int.MinValue;   // p.Revision TopoAll was hashed for (re-hashed on self-heal passes)
        public uint PendingStart, PendingEnd;   // update a start / end topology change was seen (0 = none): respawn that end's
                                                // group once the Director's StartIsJunction / EndIsJunction agrees (or after a timeout)
        public uint RespawnMask;      // PropKind bits to replace (new entity first, old deleted) in the next diff
        public readonly List<ForeignEdge> Foreign = new List<ForeignEdge>(4);
        public bool ForeignDirty = true;   // rebuild the connected part of Foreign before the next diff
        public bool Released;         // Releasing / Complete: everything torn down, nothing spawns any more
        public bool FencesFit = true; // fences fit the per-site budget (else they are the first thing dropped)
        public uint FenceLayout;      // layout hash FencesFit was computed for
        public int FenceEstimate;
        public uint BuildingsKey;     // hash of "edge has buildings" over the chain (footprint fences)
        public int EndRespawns, HealDeleted, SkippedForeign, SkippedOutside;   // counters (dev)
        public float Trim0, Trim1;    // trimmed chain of the current diff (footprint rule)
        public BarrierStyle LastBarriers; // styles of the last diff (dev)
        public FenceStyle LastFence;

        // ---- Staged traffic devices
        public PropPlan LastPlan;     // device plan of the last diff (dev, rrw.check)
        public bool HavePlan;
        public RoadZones LastWorksHalf;   // car half the in-lane devices of the last diff marked as works half (rrw.check)
        public uint DeviceSig;        // signature of the device layout (log line on change)
        public readonly List<AccessPoint> Access = new List<AccessPoint>(8);   // building access points (driveway gaps)
        public uint AccessKey;        // hash of Access (fence layout input)
        public uint AccessUpdate;     // update Access was collected (0 = never)
        public uint ClosedBKey;       // hash of EdgeRecord.ClosedBApplied over the chain at the last collection
        public uint FenceKey;         // fence layout cache key (PropSystem m_FenceCache)
        public int FencePanels, FenceShorts, FenceCones, FenceGaps, FenceRuns;   // last kerb fence layout (dev)
        public float FenceDelta;      // last per-joint stretch (+) / overlap (-) of the fence layout (dev)
        public int DividerCones, DividerLamps, Signs, SignalAsserts, SignalWrites;   // dev counters
        public bool LampSite;         // this project shows divider cone lamps (kStagedLampSites nearest)
        public bool FenceFallback;    // the kerb fence uses the SafetyBarrier fallback (fence variant switch = -1 or panel missing)
        public string DeviceLine = "";// last device summary (rrw.props.list / log)

        // ---- Crews and performance
        // Every slot also sits in SlotList (same objects): the self-heal scans a slice of it per update instead of re-diffing
        // the whole project every kSelfHealInterval updates. Always add / remove through AddSlot / RemoveSlot / ClearSlots.
        public readonly List<PropSlot> SlotList = new List<PropSlot>(64);
        public int HealCursor;        // next SlotList index the time-sliced self-heal checks
        public int HealDeferStreak;   // updates a heal-only full diff has been postponed in a row (bounded by kHealMaxDefer)
        public bool HealPending;      // the self-heal found a missing / dead entity: full diff (at most kHealDiffsPerUpdate per update)
        public uint LastFrontKey;     // front component of the diff key (PropCrewLayout.FrontStep, phase fraction steps)
        public uint LastDividerKey;   // divider pick-up bucket (PropCrewLayout.DividerPickKey)
        // Fill of every slot-group slot at the last diff (index = PropGroup, then k): a front-only diff only re-applies slots
        // whose fill changed. -2 = skipped by thinning, -3 = group skipped (limited). Sized by the full diff.
        public readonly int[][] FillCache = new int[(int)PropGroup.Divider][];
        public readonly int[] DepotCache = new int[PropCrewLayout.kMaxDepots * PropCrewLayout.kDepotSlots];
        public bool FillCacheValid;   // set by a full diff; a front-only diff needs it (else it escalates to a full diff)
        public int Crews = 1;         // crew count of the last diff (log line on change)
        public int Depots;            // depots wanted at the last diff (dev)
        public int FullDiffs, FrontDiffs, HealScanned, HealMissing, HealDeferred, FrontApplied;   // counters (dev / perf)
        // camera distance cache: 17 chain sample points per edge, rebuilt when the chain / geometry changes
        public Unity.Mathematics.float2[] CamSamples = new Unity.Mathematics.float2[0];
        public int CamSampleCount;
        public uint CamSampleKey;

        // ---- Upgrade works (mode H): band devices and band heaps (PropUpgrade.cs)
        public uint DiffKey;          // structural diff key of this update (ProcessKey)
        public uint BandKey;          // band device layout cache key (PropSystem m_BandCache)
        public uint UpgradeKey;       // structural inputs of the band layout of this update (part of the diff key)
        // band heap fills of the last diff: index chain band * 4 + heap group (spoil, dump, windrow, rubble), then slot k
        public readonly int[][] BandFillCache = new int[RRWConst.kUwMaxChainBands * 4][];
        public bool BandFillValid;    // set by a full diff; a front-only diff needs it
        public int BandDividers, BandTapers, BandEnds, BandFencePanels, BandFenceGaps, BandVergePanels, BandEdgeCones, BandCentreCones, BandHeaps;
        public int CrewOnBand = -1;   // crew props on a free strip of the lead band (1), on the sidewalk (0), none (-1)
        public float RoadKmh;         // posted speed of the chain (highest default car lane speed), km/h; 0 = unknown
        public uint UpgradeSig;       // signature of the band device summary (log line on change)
        public string UpgradeLine = "";

        public void AddSlot(PropSlot s)
        {
            if (Slots.TryGetValue(s.Key, out var old) && old != s) RemoveSlot(s.Key);
            Slots[s.Key] = s;
            s.Index = SlotList.Count;
            SlotList.Add(s);
        }

        public void RemoveSlot(int key)
        {
            if (!Slots.TryGetValue(key, out var s)) return;
            Slots.Remove(key);
            int i = s.Index, last = SlotList.Count - 1;
            if (i >= 0 && i <= last && SlotList[i] == s)
            {
                if (i != last) { SlotList[i] = SlotList[last]; SlotList[i].Index = i; }
                SlotList.RemoveAt(last);
            }
            else
            {
                int j = SlotList.IndexOf(s);   // never expected (index bookkeeping): stay consistent anyway
                if (j >= 0) { SlotList.RemoveAt(j); for (int k = j; k < SlotList.Count; k++) SlotList[k].Index = k; }
            }
            s.Index = -1;
        }

        public void ClearSlots()
        {
            Slots.Clear();
            SlotList.Clear();
            HealCursor = 0;
            FillCacheValid = false;
            BandFillValid = false;
            BandKey = 0;
        }
    }

    // One works edge of a project's chain (rebuilt when the project's revision or the registry changes).
    internal sealed class ChainEdge
    {
        public Entity Edge;
        public EdgeRecord Record;
        public int Index;             // position in ProjectProps.Chain (sorted by Lo)
        public float U0, U1;          // saved chain coordinates of curve t = 0 / t = 1
        public float Lo => math.min(U0, U1);
        public float Hi => math.max(U0, U1);
        public float DirSign => U1 >= U0 ? 1f : -1f;   // +1: curve direction = chain direction
        // per-update cache
        public uint GeoStamp, YStamp;
        public bool HaveRuntime;
        public RoadWorksRuntime Runtime;
        public RoadWorksGround Ground;
        public bool PlannedValid;
        public TerrainProfile Planned;
        public bool HasBuildings;     // buildings along this edge (Dependants / BuildingsWaiting / ConnectedBuilding)
        // inputs re-read on the slow path only (first refresh, GeometryRevision change, self-heal pass)
        public bool SlowKnown;
        public int SlowGeoRevision;
        public bool HasBuildingBuffer;
    }

    // Per-edge module slot on the registry record (EdgeRecord.Slots[Props]): floor-change bookkeeping.
    internal sealed class PropEdgeState
    {
        public uint LastStepUpdate;
        public bool LastHidden;
        public bool LastKnown;
        public uint ResnapAt;         // UpdateIndex at which a delayed re-snap (CPU heightmap readback lag) fires; 0 = none
        public uint ResnapEpoch;
        public int SpeedRevision = -1;// GeometryRevision SpeedKmh was read for
        public float SpeedKmh;        // highest default speed of the edge's car lanes (km/h; 0 = none)
    }

    // Late-owner requests handed from PropSystem (Mod1) to PropOwnerSystem (Mod4) of the same update (verified in game).
    internal struct OwnerRequest
    {
        public Entity Prop;
        public Entity Owner;
    }

    internal static class PropState
    {
        public static readonly Dictionary<uint, ProjectProps> Projects = new Dictionary<uint, ProjectProps>();
        public static readonly List<OwnerRequest> OwnerRequests = new List<OwnerRequest>(256);
        public static int TotalProps;
        public static bool DevRespawnAll;          // set by dev commands (MainLoop), consumed by PropSystem at Mod1
        public static bool DustReresolve;          // dust source changed (dev command / VFX slot check): PropSystem re-resolves its carriers
        public static uint DevRespawnProject;      // 0 = none

        public static void Clear()
        {
            Projects.Clear();
            OwnerRequests.Clear();
            TotalProps = 0;
            DevRespawnAll = false;
            DevRespawnProject = 0;
            DustReresolve = false;
        }
    }
}
