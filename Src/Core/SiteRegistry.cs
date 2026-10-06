using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // Per-module private state slots on edge/project records (extension point: modules store their own typed
    // objects here instead of changing Core). Each module only touches its own slot.
    public enum ModuleSlot
    {
        Director = 0,
        Ground = 1,
        Surfaces = 2,
        Props = 3,
        Machines = 4,
        Traffic = 5,
        UI = 6,
        Persistence = 7,
        Dev = 8,
        Count = 9,
    }

    // Runtime record of one works edge. Created/updated/removed ONLY by the Director (Mod1).
    public sealed class EdgeRecord
    {
        public Entity Edge;
        public uint ProjectId;
        public int ChainIndex;            // position in ProjectRecord.Edges
        public Entity StartNode, EndNode; // Edge.m_Start / m_End
        public EdgeArc Arc;               // rebuilt when GeometryHash changes
        public EdgeSection Section;
        public uint GeometryHash;         // hash of the curve control points + composition width
        public int GeometryRevision;      // ++ on every geometry change (modules re-layout when it differs from their copy)
        public float SubgradeDepth;
        public bool HiddenApplied;        // Director's last applied Hidden state (Hidden is removed only on the true->false transition)
        public bool Dependants;           // Traffic writes (GxAnalysis.Classify: buildings / stops / cut-edge); Director reads for the closure target
        public int BuildingsWaiting;      // Traffic writes: buildings connected to this edge while its target is Closed
                                          // (not only while hidden - a mode-A construction stays Closed for the whole works) (UI note)
        public ClosureLevel ClosureApplied;   // Traffic writes once the lanes carry the closure; UI reads
        public RoadZones OpenLanesApplied;// Traffic writes: lane groups of a Closed edge that carry traffic (staged opening),
                                          // once the lanes carry it; only groups that really have lanes on this edge. UI / Dev read
        // One-way roads: Traffic writes the chain-frame halves (LeftHalf / RightHalf) that hold at least one
        // WHOLE car-lane group (a master/slave group that straddles the centre line - one-way roads - belongs to neither), so
        // the staged opening only offers a half that traffic can really use. Valid while CarHalvesRevision == GeometryRevision.
        public RoadZones CarHalves;
        public int CarHalvesRevision = -1;
        public bool CarHalvesReversed;
        public bool TrafficDrained;       // Traffic writes: no Moving vehicle on the edge's car lanes (demolition mobilisation gate)
        public uint GeometryChangedUpdate;// RRWClock.UpdateIndex of the last geometry change (Surfaces/Props rewrite bypass)

        // ---- drain report (Traffic is the ONLY writer; Director, Machines, Props, UI, Dev read).
        // Written for every registry edge whose target is Closed, on EVERY Traffic request update (ZonesReportUpdate is stamped
        // every update even when the Moving scan itself runs only every kDrainCheckUpdates). Edges whose target is not Closed:
        // ZonesDrained = None, ZonesReportUpdate stamped too (nothing is closed there).
        public RoadZones ZonesPresent;    // lane groups that have at least one lane on this edge (car halves, sidewalks, parking per side)
        public RoadZones ZonesDrained;    // closed or SOFT groups present on this edge that carry the applied state (SOFT, CLOSED-S or
                                          // CLOSED-B) and had no Moving lane object in two consecutive scans kDrainCheckUpdates apart, or
                                          // whose drain timed out (kDrainTimeoutFrames, Warn once). Never contains an applied-open group.
        public uint ZonesReportUpdate;    // RRWClock.UpdateIndex of the report (0 = never). Stale after kZonesReportStaleUpdates
        // Traffic writes these with ZonesDrained; the Director keeps both OUT of WorkZonesReady:
        public RoadZones ZonesTimedOut;   // subset of ZonesDrained reported only because the drain timed out while the group still had
                                          // traffic; cleared after kDrainCleanScans clean re-scans or kDrainReadyCapFrames after it began draining
        public RoadZones ZonesParked;     // closed parking groups of a VISIBLE edge holding parked cars at the last scan (edges with
                                          // buildings keep vanilla parking, verified in game): machines keep their boxes on the drive lanes there
        public RoadZones SoftApplied;     // groups the lanes currently carry as SOFT (UI "switching sides", Dev age check)
        public RoadZones ClosedBApplied;  // car groups the lanes currently carry as CLOSED-B (Props: driveway gaps in the works-side fence)
        // ---- classification (Traffic Classify writes; Director reads for CarHalfAllowed / D0 sidewalks). Chain frame.
        public bool CutEdge;              // removing this edge disconnects a part of the network that has buildings (dead-end chain)
        public bool HasTrack;             // the edge has a track lane (tram)
        public RoadZones StopZones;       // car halves (LeftHalf / RightHalf, chain frame) that hold a transport stop (stop on that side)
        public RoadZones BuildingZones;   // SidewalkLeft / SidewalkRight of the sides that have a ConnectedBuilding (chain frame)
        public int BuildingCount;         // ConnectedBuilding entries (both sides) at the last classification
        public int ClassifyRevision = -1; // GeometryRevision the fields above were computed for (-1 = not classified yet)
        public bool ClassifyReversed;     // chain direction they were computed for (re-classify when it flips)
        public uint ClassifyUpdate;       // RRWClock.UpdateIndex of the last write (Director re-evaluates CarHalfAllowed when it changes)
        public bool Classified => ClassifyRevision == GeometryRevision;
        // ---- upgrade works (mode H). Null on every edge that is not a mode H site. The Director creates it from the saved tail
        // (UpgradeEdgeState.Load) and keeps its bands / sub-strips; Traffic writes the drop report, the Director the driveway
        // keep-outs (see UpgradeEdgeState for the writer of every field).
        public UpgradeEdgeState Upgrade;
        public readonly object[] Slots = new object[(int)ModuleSlot.Count];

        public T Get<T>(ModuleSlot s) where T : class => Slots[(int)s] as T;
        public void Set(ModuleSlot s, object value) => Slots[(int)s] = value;
        public T GetOrCreate<T>(ModuleSlot s) where T : class, new()
        {
            var v = Slots[(int)s] as T;
            if (v == null) { v = new T(); Slots[(int)s] = v; }
            return v;
        }
    }

    // Runtime record of one project (a chain of edges worked by one crew). ONLY the Director writes the public fields.
    public sealed class ProjectRecord
    {
        public uint Id;
        public WorksKind Kind;
        public VisualMode Mode;
        public readonly List<Entity> Edges = new List<Entity>();  // chain order (u increasing)
        public float ChainLength;
        public float Progress;
        public WorksPhase Phase;
        public float PhaseFraction;
        public ProgressModel Model;
        public bool Working;
        public bool AllowMachines;        // camera distance + global machine budget (Director decides once per frame)
        public float CameraDistance;      // XZ distance from the camera pivot to the main front
        public float CameraChainDistance; // XZ distance from the camera pivot to the NEAREST chain point (continuous over phase
                                          // boundaries, where the main front jumps from U back to 0); Director writes, step 14
        public float3 FrontPosition;      // world position of the main front (Focus, UI) = ChainMap.Point(FrontU)
        public float FrontU;              // main front F (unquantised chain u) this frame; Director writes. With several crews: the front of FocusCrew
                                          // (PhasePlan.CrewFront(View(), FocusCrew)); one crew = MainFront(View()) as before
        public uint Seed;
        public int Revision;              // ++ when edges join/leave the project or chain coordinates change
        public int Rerouted;              // Traffic writes: in-flight paths invalidated because of this project (UI)
        public int ParkedMoved;           // Traffic writes: parked cars relocated (UI)
        public int MachineCount;          // Machines writes: puppets currently spawned (UI / dev)
        // ---- machine report (Machines is the ONLY writer; Director, Traffic, Props, Surfaces, UI, Dev read).
        // Written for EVERY registry project on EVERY update the MachineDirectorSystem runs (also with machines switched off,
        // no puppets, or the project far away: then None / 0), counting post-site puppets that kept this ProjectId.
        public RoadZones MachineZones;    // union of RoadZones any puppet of this project touches now OR will touch within its
                                          // committed plan (until the end of its last finite motion leg); + Outside when a box
                                          // reaches outside [TrimU0 - 1, TrimU1 + 1]
        public int MachinesOnCarriageway; // puppets whose zones include LeftHalf/RightHalf/Outside (0 = carriageway clear)
        public uint MachinesReportUpdate; // RRWClock.UpdateIndex of the report (0 = never). Fresh = MachinesReportFresh(now)
        public bool MachinesReportFresh(uint now) =>
            MachinesReportUpdate != 0 && unchecked(now - MachinesReportUpdate) <= RRWConst.kMachinesReportStaleUpdates;
        // ---- Director writes.
        public RoadZones OpenLanes;       // staged opening decided this frame (PhasePlan.OpenLanes; sticky). Machines never plan
                                          // a puppet into these groups; Props / Surfaces dress them (fences, cones, yellow lines)
        public uint ReleaseSince;         // RRWClock.UpdateIndex (>= 1) when the project began handing the road back: construction
                                          // completion frame N, or a demolition call-off in D0 (Leaving). 0 = not releasing.
                                          // Machines: no new spawns, every puppet leaves and is gone <= kPostSiteMaxSimFrames later.
        public bool Releasing => ReleaseSince != 0;
        public uint ReleaseSimSince;      // RRWClock.SimFrame when the release began (the caps run on game time, see RRWConst)
        public RoadZones SeparableHalves; // Director writes each frame: halves that EVERY edge can open on its own (EdgeRecord.CarHalves,
                                          // all edges classified by Traffic); None = the staged half never opens (one-way road)

        // ---- staged traffic management (Director is the ONLY writer; all modules read).
        public RoadZones SoftZones;       // groups being drained (closing) this frame: = PhasePlan.Stage(...).Soft; never overlaps OpenLanes
        public RoadZones WorkZonesReady;  // closed groups drained on EVERY edge that has lanes of that group (EdgeRecord.ZonesDrained ∪
                                          // groups absent there), restricted to ~OpenLanes & ~SoftZones; None while any edge's drain report
                                          // is stale. Machines plan, move, back out and park ONLY into these
        public StageSwitch Switch;        // stage switch step; None when no switch runs
        public float SwitchP = float.NaN; // p of the running / last switch (NaN = none)
        public byte StageIndex, StageCount;   // committed stage (C4a = 0, C4b = 1; D0 = 0 of 1) for UI / dev; Stage() recomputes it
        public bool CarHalfAllowed;       // a car half may open on this project (evaluated at C3 start and on every geometry /
                                          // classification change; latched while a car half is open)
        public StageBlockReason StageBlockReason; // why not (None when allowed)
        public StageContext StageCtx;     // the context the Director passed to PhasePlan.Stage this frame (copied into View())
        public RoadZones BuildingSidewalks;   // union over edges of EdgeRecord.BuildingZones (D0 house-side sidewalks; chain frame)
        public bool ExitAtStart, ExitAtEnd;   // chain end node connects to the road network (a non-works car road, or a visible
                                              // works road of another project); leavers only drive to such an end (PhasePlan.ExitU)
        public bool ExitsKnown;               // the Director computed ExitAtStart / ExitAtEnd for this record (both
                                              // false + ExitsKnown = an isolated road; both false + !ExitsKnown = not computed yet)
        // Machines writes (part of the machine report, same update stamp): puppets STUCK (not moved for kMachineStuckSeconds while their
        // plan says move; deadlocks included) right now. rrw.check must show 0 for a report older than the resolution time.
        public int MachinesStuck;
        // Sim frames since the release began (0 when not releasing; never negative).
        public uint ReleaseSimAge => !Releasing ? 0u : (uint)Math.Max(0, unchecked((int)(RRWClock.SimFrame - ReleaseSimSince)));
        // Director safety cap of the release gate (PhasePlan.MachinesHoldRoad): game time, plus a last-resort rendered-update cap.
        public bool ReleaseCapped(uint nowUpdate) =>
            Releasing && (ReleaseSimAge >= (uint)RRWConst.kCompletionMachineWaitSimFrames
                          || unchecked(nowUpdate - ReleaseSince) >= (uint)RRWConst.kCompletionMachineWaitUpdatesHard);
        // ---- crew sections (Director is the ONLY writer; all modules read).
        public int Crews = 1;             // crew sections of the current phase: PhasePlan.CrewCount(View(), RRWSetting.CrewsMax), LATCHED
                                          // at a phase change, registry rebuild / load, ModelReset (jump, SetProgress, cancel) and a chain
                                          // Revision change; never changed mid-phase otherwise (a rush or a setting change waits for the next
                                          // phase). Copied to RoadWorksRuntime.m_Crews of every edge and to View().Crews
        public float WorkSeconds;         // machine seconds for p 0 -> 1 at the nominal rate: WorkRequired / WorkTime.Rate(rushed, 1) / 60
                                          // (dev time scales excluded). Updated every frame (rush applies at once to fronts / return anchors)
        public int FocusCrew;             // crew whose front is FrontU / FrontPosition: the crew nearest the camera (hysteresis 20 m)
        // Machines writes (part of the machine report, same stamp): crews of this project with at least one spawned role puppet (UI
        // "N crews working" shows Crews; Dev / perf show both).
        public int CrewsSpawned;
        // ---- road rollers. Director writes Rollers (latched with Crews: RRWSetting.RollersOn ? kMaxRollersPerCrew : 0, at the same
        // events; never saved, re-derived after a load). Machines writes RollersSpawned with the machine report stamp (live roller units of
        // this project; they are ALSO counted in MachineZones / MachinesOnCarriageway / MachinesStuck like puppets).
        public int Rollers;
        public int RollersSpawned;
        public SiteFlags Flags;           // copy of the (identical) saved flags of the project's edges
        public float TrimU0, TrimU1;      // chain u where the works geometry really starts / ends (node trims at visible junction ends;
                                          // 0 / U at dead ends and hidden nodes). Machines and props anchor inside [TrimU0, TrimU1].
        public bool StartIsJunction, EndIsJunction; // chain end at a node with a non-works (visible) edge
        public bool StartShared, EndShared;// chain end node shared with another active project (slots shift inward)
        public WorksPhase CancelPhase = WorksPhase.None;   // saved cancel data (CancelledBuild)
        public float CancelFront;
        public ClosureLevel Closure;      // closure target of the project this frame (max over its edges)
        public bool ClearingTraffic;      // demolition mobilisation: waiting for vehicles to leave
        // ---- upgrade works (mode H). Null unless Mode == HalfWidth with a saved upgrade tail. The Director builds it from the
        // edges' tails (UpgradeRuntime.Begin / AddEdge / Finish) and writes its per-window state every update; View() copies its
        // pure part into ProjectView.Upgrade (UpgradeRuntime.View).
        public UpgradeRuntime Upgrade;

        // Pure view for PhasePlan.SurfaceSpan / Props / Crew. Mode H: UpgradeView.Applied* follow the switch step and the stage
        // context (PhasePlan.FillUpgradeApplied).
        public ProjectView View()
        {
            var v = ViewRaw();
            PhasePlan.FillUpgradeApplied(ref v);
            return v;
        }

        private ProjectView ViewRaw() => new ProjectView
        {
            Kind = Kind,
            Mode = Mode,
            Phase = Phase,
            F = PhaseFraction,
            U = ChainLength,
            Trim0 = TrimU0,
            Trim1 = TrimU1 > 0f ? TrimU1 : ChainLength,
            Flags = Flags,
            CancelPhase = CancelPhase,
            CancelFront = CancelFront,
            Closure = Closure,
            OpenLanes = OpenLanes,
            SeparableHalves = SeparableHalves,
            SoftZones = SoftZones,
            WorkZonesReady = WorkZonesReady,
            Switch = Switch,
            Ctx = StageCtx,
            BuildingSidewalks = BuildingSidewalks,
            ExitAtStart = ExitAtStart,
            ExitAtEnd = ExitAtEnd,
            ExitsKnown = ExitsKnown,
            LeftHandTraffic = RRWCity.LeftHandTraffic,
            Crews = Crews,
            WorkSeconds = WorkSeconds,
            Rollers = Rollers,
            Upgrade = Mode == VisualMode.HalfWidth && Upgrade != null ? Upgrade.View(Progress) : default,
        };
        public readonly object[] Slots = new object[(int)ModuleSlot.Count];

        public T Get<T>(ModuleSlot s) where T : class => Slots[(int)s] as T;
        public void Set(ModuleSlot s, object value) => Slots[(int)s] = value;
        public T GetOrCreate<T>(ModuleSlot s) where T : class, new()
        {
            var v = Slots[(int)s] as T;
            if (v == null) { v = new T(); Slots[(int)s] = v; }
            return v;
        }
    }

    // The managed site registry. Rebuilt from the saved RoadWorksSite components after every load (Director),
    // cleared in OnGamePreload. Visual modules READ it and reconcile their own state against it each frame
    // ("pull" model): if an edge/project they track is no longer here, they tear their stuff down.
    // The neighbours of one project of a split upgrade drag (project ids, 0 = none).
    public struct CorridorLink
    {
        public uint Prev, Next;
    }

    public static class SiteRegistry
    {
        public static readonly Dictionary<Entity, EdgeRecord> Edges = new Dictionary<Entity, EdgeRecord>();
        public static readonly Dictionary<uint, ProjectRecord> Projects = new Dictionary<uint, ProjectRecord>();
        public static uint NextProjectId = 1;
        public static int Revision;       // ++ whenever any record is added or removed
        public static bool Loaded;        // true once the Director rebuilt the registry for the current city

        // Links between the projects of one split upgrade drag (SiteFactory.CreateUpgradeProjects writes them for every project
        // of a split chain; the Director takes them at adopt into UpgradeRuntime.CorridorPrev / CorridorNext). Runtime only.
        public static readonly Dictionary<uint, CorridorLink> PendingCorridors = new Dictionary<uint, CorridorLink>();

        public static bool TakeCorridor(uint projectId, out CorridorLink link)
        {
            if (!PendingCorridors.TryGetValue(projectId, out link)) return false;
            PendingCorridors.Remove(projectId);
            return true;
        }

        public static bool TryGetEdge(Entity e, out EdgeRecord r) => Edges.TryGetValue(e, out r);
        public static bool TryGetProject(uint id, out ProjectRecord p) => Projects.TryGetValue(id, out p);

        public static uint AllocateProjectId() => NextProjectId++;

        // Raises NextProjectId above an id that is already in use. Tools / Persistence call EnsureIdsFromSaved
        // before allocating while the Director has not rebuilt the registry yet (an apply in the first frame after a
        // load would otherwise reuse a saved project id).
        public static void EnsureIdAbove(uint usedId)
        {
            if (usedId >= NextProjectId) NextProjectId = usedId + 1;
        }

        // Scans the saved sites of `siteQuery` (any query with RoadWorksSite) when the registry is not loaded yet.
        public static void EnsureIdsFromSaved(EntityQuery siteQuery)
        {
            if (Loaded || siteQuery.IsEmptyIgnoreFilter) return;
            var sites = siteQuery.ToComponentDataArray<RoadWorksSite>(Unity.Collections.Allocator.Temp);
            try { for (int i = 0; i < sites.Length; i++) EnsureIdAbove(sites[i].m_ProjectId); }
            finally { sites.Dispose(); }
        }

        // Director only: an edge's nodes changed (modules keyed on the registry revision re-layout).
        public static void BumpRevision() => Revision++;

        // Director only.
        public static EdgeRecord AddEdge(Entity edge, uint projectId)
        {
            if (!Edges.TryGetValue(edge, out var r))
            {
                r = new EdgeRecord { Edge = edge };
                Edges.Add(edge, r);
                Revision++;
            }
            r.ProjectId = projectId;
            if (projectId >= NextProjectId) NextProjectId = projectId + 1;
            return r;
        }

        // Director only.
        public static void RemoveEdge(Entity edge)
        {
            if (Edges.Remove(edge)) Revision++;
        }

        // Director only.
        public static ProjectRecord GetOrAddProject(uint id)
        {
            if (!Projects.TryGetValue(id, out var p))
            {
                p = new ProjectRecord { Id = id };
                Projects.Add(id, p);
                Revision++;
                if (id >= NextProjectId) NextProjectId = id + 1;
            }
            return p;
        }

        // Director only.
        public static void RemoveProject(uint id)
        {
            if (Projects.Remove(id)) Revision++;
        }

        public static void Clear(string why)
        {
            if (Edges.Count > 0 || Projects.Count > 0) RRWLog.Info("registry cleared (" + why + "): " + Edges.Count + " edges, " + Projects.Count + " projects");
            Edges.Clear();
            Projects.Clear();
            PendingCorridors.Clear();
            NextProjectId = 1;
            Revision++;
            Loaded = false;
        }
    }

    // ---------------------------------------------------------------- requests (UI / settings / dev -> Director)

    public enum WorksRequestType : byte
    {
        Rush = 0,            // Project (or Edge's project): pay and switch to 24 h x1.5
        // 1 and 2 are unused (were Pause / Resume; pausing works was removed). The Director drops them.
        Cancel = 3,          // construction: refund + restore; demolition D0: reopen; later: refused
        Finish = 4,          // complete now (dev / "Finish all works now")
        SetProgress = 5,     // dev: Value = p
        JumpPhase = 6,       // dev: Phase + Value = fraction
        StartConstruction = 7, // dev: Edge = existing road -> construction works (new project per request batch)
        StartDemolition = 8,   // dev: Edge = existing road -> demolition works
        FinishAll = 9,       // settings button
        RebuildVisuals = 10, // settings button / dev: every module respawns its layer
        CancelInstant = 11,  // UI: project below kInstantCancelProgress -> Director deletes the edges + refunds paid
        CancelEdge = 12,     // Tools (bulldozer): cancel ONLY this edge: split it off into its own project, then Cancel that project
        CancelEdgeInstant = 13, // Tools (bulldozer) below kInstantCancelProgress: delete this edge + refund its paid
        SetCrews = 14,       // dev (rrw.crews): Value = crew count (0 = automatic latch); pinned until the next phase change, never below kMinSectionLength sections
        EndUpgrade = 15,     // Tools (bulldozer on a mode H upgrade site): Edge leaves its upgrade project Aux (the OLD project id) before the
                             // demolition site written in the same pass is adopted; Value = the refund already credited (log only). The other
                             // edges of the project keep working. Never a cancel: no money moves in the Director.
    }

    public struct WorksRequest
    {
        public WorksRequestType Type;
        public Entity Edge;          // any edge of the target project (Entity.Null = all, where meaningful)
        public uint ProjectId;       // alternative to Edge (0 = use Edge)
        public float Value;
        public WorksPhase Phase;
        public int Batch;            // dev Start*: requests with the same batch id form one project
        public uint Aux;             // EndUpgrade: the project id the edge belonged to before the bulldozer wrote its demolition site
    }

    // UI (UIUpdate), settings buttons, dev commands and Tools enqueue; the Director drains at Mod1 (the only
    // place where works state and structural changes happen). Main thread only.
    public static class WorksRequests
    {
        private static readonly List<WorksRequest> s_Queue = new List<WorksRequest>();
        public static int Count => s_Queue.Count;
        public static void Enqueue(WorksRequest r) => s_Queue.Add(r);
        public static void Enqueue(WorksRequestType type, Entity edge, float value = 0f) => s_Queue.Add(new WorksRequest { Type = type, Edge = edge, Value = value });
        public static void Drain(List<WorksRequest> into) { into.AddRange(s_Queue); s_Queue.Clear(); }
        public static void Clear() => s_Queue.Clear();
    }

    // ---------------------------------------------------------------- debug / dev extension points

    [Flags]
    public enum DebugLayers : uint
    {
        None = 0,
        Hide = 1 << 0,       // Director: Hidden on works edges (off = road revealed; Ground restores through the normal reveal hold, never in the un-hide frame)
        Terrain = 1 << 1,    // Ground
        Surfaces = 1 << 2,   // Surfaces
        Machines = 1 << 3,   // Machines (puppets)
        Bones = 1 << 4,      // Machines (bone animation)
        Props = 1 << 5,      // Props
        Closure = 1 << 6,    // Traffic
        Icons = 1 << 7,      // Director
        Wear = 1 << 8,       // Director
        PreviewHide = 1 << 9,// A/B aid: Director's PreviewHideSystem (off = Temp previews of hidden works roads are not hidden)
        All = 0xFFFFFFFF,
    }

    // Global switches the dev commands flip (always compiled; release builds simply never change them).
    public static class RRWDebug
    {
        public static DebugLayers Layers = DebugLayers.All;
        public static float WorkTimeScale = 1f;     // multiplies work accrual
        public static float MachineClockScale = 1f; // multiplies machine animation time (dev time-lapse; rrw.timescale sets it too)
        public static bool HideAfterGround;         // fallback: Director applies Hidden only after Ground reported a non-vanilla profile
        public static bool IgnoreShift;             // dev: work around the clock
        public static bool Verbose;                 // extra logging

        public static bool On(DebugLayers l) => (Layers & l) == l;
    }

    // Modules register text dumpers and invariant checkers in OnCreate; dev commands "rrw.dump"/"rrw.check" call them.
    public static class RRWIntrospection
    {
        public delegate string Dumper(EntityManager em, Entity edge);
        public delegate void Checker(EntityManager em, List<string> problems);

        public static readonly List<KeyValuePair<string, Dumper>> Dumpers = new List<KeyValuePair<string, Dumper>>();
        public static readonly List<KeyValuePair<string, Checker>> Checkers = new List<KeyValuePair<string, Checker>>();

        public static void RegisterDumper(string module, Dumper d)
        {
            Dumpers.RemoveAll(kv => kv.Key == module);
            Dumpers.Add(new KeyValuePair<string, Dumper>(module, d));
        }

        public static void RegisterChecker(string module, Checker c)
        {
            Checkers.RemoveAll(kv => kv.Key == module);
            Checkers.Add(new KeyValuePair<string, Checker>(module, c));
        }
    }

    // Per-module main-thread timing (dev "rrw.perf"). Usage: long t = RRWPerf.Start(); ...; RRWPerf.Stop(PerfSlot.Ground, t);
    public enum PerfSlot { Tools, Director, Ground, Surfaces, Props, Machines, MachineJobs, Traffic, UI, Persistence, Count }

    public static class RRWPerf
    {
        public static readonly long[] Ticks = new long[(int)PerfSlot.Count];
        public static readonly long[] MaxTicks = new long[(int)PerfSlot.Count];
        public static readonly int[] Calls = new int[(int)PerfSlot.Count];
        public static int Frames;

        public static long Start() => global::System.Diagnostics.Stopwatch.GetTimestamp();

        public static void Stop(PerfSlot s, long start)
        {
            long d = global::System.Diagnostics.Stopwatch.GetTimestamp() - start;
            int i = (int)s;
            Ticks[i] += d;
            Calls[i]++;
            if (d > MaxTicks[i]) MaxTicks[i] = d;
        }

        public static void Reset()
        {
            Array.Clear(Ticks, 0, Ticks.Length);
            Array.Clear(MaxTicks, 0, MaxTicks.Length);
            Array.Clear(Calls, 0, Calls.Length);
            Frames = 0;
            RRWPerfDetail.Reset();
        }

        public static double Ms(long ticks) => ticks * 1000.0 / global::System.Diagnostics.Stopwatch.Frequency;
    }

    // Dev detail of "rrw.perf" / "rrw.mx.perf" (DEVTOOLS only): named timing sections and counters INSIDE a module, one group
    // per module ("mx" Machines, "sx" Surfaces). Every entry point that a module calls per update is [Conditional("DEVTOOLS")],
    // so release builds compile the calls away, arguments included. Main thread only.
    //  * Register(group, sections, counters) once -> base id; section i = base + i, counter j = base + sections.Length + j.
    //    Section 0 is the group's ROOT (the module's whole update).
    //  * Begin(id) / End(id): Stopwatch ticks; only the outermost pair of a nested section is timed. The root's Begin resets
    //    the depth of every section of its group (a section left open by an exception heals on the next update).
    //  * Count(id, n): counters (calls of an inner operation, samples, structural changes, ...).
    //  * When the root ends, the per-update sums of the group are folded into per-update maxima, and the worst root update
    //    is kept as a full breakdown (spike attribution: which sections / how many inner operations made the spike).
    public static class RRWPerfDetail
    {
        const int kMax = 384;
        const int kMaxGroups = 8;
        static int s_Count, s_Groups;
        static readonly string[] s_Name = new string[kMax];
        static readonly int[] s_Group = new int[kMax];
        static readonly bool[] s_Counter = new bool[kMax];
        static readonly string[] s_GroupName = new string[kMaxGroups];
        static readonly int[] s_Lo = new int[kMaxGroups], s_Hi = new int[kMaxGroups], s_Root = new int[kMaxGroups];
        static readonly long[] s_Start = new long[kMax];
        static readonly int[] s_Depth = new int[kMax];
        // since the last Reset
        static readonly long[] s_Ticks = new long[kMax], s_MaxCall = new long[kMax], s_Calls = new long[kMax], s_Value = new long[kMax];
        static readonly long[] s_MaxFTicks = new long[kMax], s_MaxFCalls = new long[kMax], s_MaxFValue = new long[kMax];
        // the running root update
        static readonly long[] s_FTicks = new long[kMax], s_FCalls = new long[kMax], s_FValue = new long[kMax];
        // the worst root update
        static readonly long[] s_WTicks = new long[kMax], s_WCalls = new long[kMax], s_WValue = new long[kMax];
        static readonly long[] s_WorstRoot = new long[kMaxGroups];
        static readonly uint[] s_WorstAt = new uint[kMaxGroups];
        static readonly int[] s_RootUpdates = new int[kMaxGroups], s_Over1 = new int[kMaxGroups], s_Over2 = new int[kMaxGroups], s_Over5 = new int[kMaxGroups];

        // Registers a module's names once (a second call for the same group returns the first base). -1 when full.
        public static int Register(string group, string[] sections, string[] counters)
        {
            lock (s_Name) return RegisterLocked(group, sections, counters);   // static init of a module wrapper may run on any thread
        }

        static int RegisterLocked(string group, string[] sections, string[] counters)
        {
            for (int g = 0; g < s_Groups; g++) if (s_GroupName[g] == group) return s_Lo[g];
            int n = sections.Length + counters.Length;
            if (s_Groups >= kMaxGroups || s_Count + n > kMax || sections.Length == 0) return -1;
            int gi = s_Groups++;
            int b = s_Count;
            s_GroupName[gi] = group;
            s_Lo[gi] = b;
            s_Hi[gi] = b + n;
            s_Root[gi] = b;
            for (int i = 0; i < n; i++)
            {
                bool counter = i >= sections.Length;
                s_Name[b + i] = counter ? counters[i - sections.Length] : sections[i];
                s_Counter[b + i] = counter;
                s_Group[b + i] = gi;
            }
            s_Count = b + n;
            return b;
        }

        [global::System.Diagnostics.Conditional("DEVTOOLS")]
        public static void Begin(int id)
        {
            if ((uint)id >= (uint)s_Count) return;
            if (s_Depth[id]++ != 0) return;
            int g = s_Group[id];
            if (s_Root[g] == id)
                for (int i = s_Lo[g]; i < s_Hi[g]; i++) if (i != id) s_Depth[i] = 0;
            s_Start[id] = global::System.Diagnostics.Stopwatch.GetTimestamp();
        }

        [global::System.Diagnostics.Conditional("DEVTOOLS")]
        public static void End(int id)
        {
            if ((uint)id >= (uint)s_Count || s_Depth[id] <= 0) return;
            if (--s_Depth[id] != 0) return;
            long d = global::System.Diagnostics.Stopwatch.GetTimestamp() - s_Start[id];
            s_Ticks[id] += d;
            s_Calls[id]++;
            if (d > s_MaxCall[id]) s_MaxCall[id] = d;
            s_FTicks[id] += d;
            s_FCalls[id]++;
            int g = s_Group[id];
            if (s_Root[g] == id) EndRoot(g, d);
        }

        [global::System.Diagnostics.Conditional("DEVTOOLS")]
        public static void Count(int id, long n)
        {
            if ((uint)id >= (uint)s_Count) return;
            s_Value[id] += n;
            s_FValue[id] += n;
        }

        static void EndRoot(int g, long d)
        {
            s_RootUpdates[g]++;
            double ms = RRWPerf.Ms(d);
            if (ms > 1.0) s_Over1[g]++;
            if (ms > 2.0) s_Over2[g]++;
            if (ms > 5.0) s_Over5[g]++;
            bool worst = d > s_WorstRoot[g];
            if (worst) { s_WorstRoot[g] = d; s_WorstAt[g] = RRWClock.UpdateIndex; }
            for (int i = s_Lo[g]; i < s_Hi[g]; i++)
            {
                if (s_FTicks[i] > s_MaxFTicks[i]) s_MaxFTicks[i] = s_FTicks[i];
                if (s_FCalls[i] > s_MaxFCalls[i]) s_MaxFCalls[i] = s_FCalls[i];
                if (s_FValue[i] > s_MaxFValue[i]) s_MaxFValue[i] = s_FValue[i];
                if (worst) { s_WTicks[i] = s_FTicks[i]; s_WCalls[i] = s_FCalls[i]; s_WValue[i] = s_FValue[i]; }
                s_FTicks[i] = 0; s_FCalls[i] = 0; s_FValue[i] = 0;
            }
        }

        [global::System.Diagnostics.Conditional("DEVTOOLS")]
        public static void Reset()
        {
            int n = s_Count;
            Array.Clear(s_Ticks, 0, n); Array.Clear(s_MaxCall, 0, n); Array.Clear(s_Calls, 0, n); Array.Clear(s_Value, 0, n);
            Array.Clear(s_MaxFTicks, 0, n); Array.Clear(s_MaxFCalls, 0, n); Array.Clear(s_MaxFValue, 0, n);
            Array.Clear(s_FTicks, 0, n); Array.Clear(s_FCalls, 0, n); Array.Clear(s_FValue, 0, n);
            Array.Clear(s_WTicks, 0, n); Array.Clear(s_WCalls, 0, n); Array.Clear(s_WValue, 0, n);
            Array.Clear(s_WorstRoot, 0, kMaxGroups); Array.Clear(s_WorstAt, 0, kMaxGroups);
            Array.Clear(s_RootUpdates, 0, kMaxGroups); Array.Clear(s_Over1, 0, kMaxGroups); Array.Clear(s_Over2, 0, kMaxGroups); Array.Clear(s_Over5, 0, kMaxGroups);
        }

        // Report lines (prefix "perfd <group>") for every registered group whose name is in `groups` (null = all). frames =
        // rendered frames of the measuring window (the same divisor as rrw.perf, so section averages add up to the module's).
        //   perfd mx sections updates=U spikes>1ms=a >2ms=b >5ms=c worst=W ms (update #k)
        //   perfd mx <section> avg=<ms per frame> perCall=<ms> max=<worst single call ms> maxU=<worst per-update sum ms> calls=<n> /f=<per frame> callsMaxU=<worst update>
        //   perfd mx #<counter> /f=<per frame> maxU=<worst update> total=<n>
        //   perfd mx worst update #k W ms: <section>=<ms>x<calls> ...   and   perfd mx worst update #k counts: <counter>=<n> ...
        public static void Append(global::System.Text.StringBuilder sb, int frames, string[] groups)
        {
            var ci = global::System.Globalization.CultureInfo.InvariantCulture;
            frames = Math.Max(1, frames);
            for (int g = 0; g < s_Groups; g++)
            {
                string gn = s_GroupName[g];
                if (groups != null && Array.IndexOf(groups, gn) < 0) continue;
                string pre = "perfd " + gn + " ";
                sb.Append(pre).Append("sections updates=").Append(s_RootUpdates[g]).Append(" spikes>1ms=").Append(s_Over1[g])
                  .Append(" >2ms=").Append(s_Over2[g]).Append(" >5ms=").Append(s_Over5[g])
                  .Append(" worst=").Append(RRWPerf.Ms(s_WorstRoot[g]).ToString("0.00", ci)).Append(" ms (update #").Append(s_WorstAt[g]).Append(")\n");
                for (int i = s_Lo[g]; i < s_Hi[g]; i++)
                {
                    if (s_Counter[i])
                    {
                        if (s_Value[i] == 0) continue;
                        sb.Append(pre).Append('#').Append(s_Name[i]).Append(" /f=").Append(((double)s_Value[i] / frames).ToString("0.##", ci))
                          .Append(" maxU=").Append(s_MaxFValue[i]).Append(" total=").Append(s_Value[i]).Append('\n');
                        continue;
                    }
                    if (s_Calls[i] == 0) continue;
                    double ms = RRWPerf.Ms(s_Ticks[i]);
                    sb.Append(pre).Append(s_Name[i]).Append(" avg=").Append((ms / frames).ToString("0.0000", ci))
                      .Append(" perCall=").Append((ms / s_Calls[i]).ToString("0.0000", ci))
                      .Append(" max=").Append(RRWPerf.Ms(s_MaxCall[i]).ToString("0.000", ci))
                      .Append(" maxU=").Append(RRWPerf.Ms(s_MaxFTicks[i]).ToString("0.000", ci))
                      .Append(" calls=").Append(s_Calls[i]).Append(" /f=").Append(((double)s_Calls[i] / frames).ToString("0.##", ci))
                      .Append(" callsMaxU=").Append(s_MaxFCalls[i]).Append('\n');
                }
                if (s_WorstRoot[g] <= 0) continue;
                sb.Append(pre).Append("worst update #").Append(s_WorstAt[g]).Append(' ').Append(RRWPerf.Ms(s_WorstRoot[g]).ToString("0.00", ci)).Append(" ms:");
                for (int i = s_Lo[g]; i < s_Hi[g]; i++)
                    if (!s_Counter[i] && s_WCalls[i] > 0)
                        sb.Append(' ').Append(s_Name[i]).Append('=').Append(RRWPerf.Ms(s_WTicks[i]).ToString("0.000", ci)).Append('x').Append(s_WCalls[i]);
                sb.Append('\n').Append(pre).Append("worst update #").Append(s_WorstAt[g]).Append(" counts:");
                for (int i = s_Lo[g]; i < s_Hi[g]; i++)
                    if (s_Counter[i] && s_WValue[i] != 0) sb.Append(' ').Append(s_Name[i]).Append('=').Append(s_WValue[i]);
                sb.Append('\n');
            }
        }
    }
}
