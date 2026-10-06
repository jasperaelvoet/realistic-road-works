using System;
using Unity.Entities;
using Unity.Mathematics;

// Shared enums and unsaved runtime components of Realistic Road Works v3.
// Everything in this file is runtime-only (no ISerializable): it is never written to a save.
// The ONLY saved type is RoadWorksSite (RoadWorksSite.cs).
namespace RealisticRoadWorks.V3
{
    public enum WorksKind : byte
    {
        Construction = 0,
        Demolition = 1,
    }

    // Phase of one works site. Construction uses Survey..Finishing, demolition BreakUp..Restore.
    public enum WorksPhase : byte
    {
        Survey = 0,       // C0 survey & clearing (topsoil strip)
        Excavation = 1,   // C1 excavation / earthworks
        Foundation = 2,   // C2 sub-base + base course
        Paving = 3,       // C3 paving (road revealed under the gravel cover)
        Finishing = 4,    // C4 markings & finishing
        BreakUp = 5,      // D0 break-up (road visible, wear ramps up)
        Removal = 6,      // D1 removal (road hidden, trench)
        Restore = 7,      // D2 restore (trench fills back to natural)
        Complete = 8,     // p >= 1: Director finalises
        None = 255,       // "no phase" (RoadWorksSite.m_CancelPhase when the site was never cancelled)
    }

    // Visual treatment chosen once when the works start (saved in RoadWorksSite.m_Mode).
    public enum VisualMode : byte
    {
        FullDig = 0,      // mode A: hidden road, real trench, decals, machines
        // 1 is unused.
        HalfWidth = 2,    // mode H: visible-road band works (partial upgrade works, RoadWorksSite plan kind Upgrade). Created only by
                          // SiteFactory.CreateUpgradeProjects with a saved upgrade tail; never chosen by PhasePlan.ChooseMode. The road
                          // stays visible; machines work only in machine-safe sub-strips. The view rules of PhasePlan key their mode H
                          // branch on ProjectView.IsUpgrade (a HalfWidth view without upgrade data behaves like before). The gameplay
                          // rules that take a mode instead of a view (Closure, MachinesHoldRoad, Wear) treat EVERY HalfWidth project as
                          // mode H, on the safe side: slow zone without the primitive, the release holds for machines, vanilla wear.
        Minimal = 3,      // mode D: road stays visible; props + beacons + closure + timer only
    }

    public enum ClosureLevel : byte
    {
        Open = 0,
        SlowZone = 1,
        Closed = 2,
    }

    // Settings enums (dropdowns in the options UI).
    public enum QualityPreset : byte { Low = 0, Medium = 1, High = 2 }
    public enum ShiftWindow : byte { Day = 0, Extended = 1, AllDay = 2 }
    public enum ClosurePolicy : byte { Realistic = 0, AlwaysSlowZone = 1, VisualOnly = 2 }

    // Saved per-site flags (RoadWorksSite.m_Flags).
    [Flags]
    public enum SiteFlags : ushort
    {
        None = 0,
        Rushed = 1 << 0,          // 24 h shift and x1.5 work rate (paid)
        // The Pause/Resume feature was REMOVED. The bit is only read to CLEAR it: the Director strips it from
        // every saved site on its first-frame rebuild (and Tools/Persistence never copy it). Use LegacyPaused for that.
        LegacyPaused = 1 << 1,
        CancelledBuild = 1 << 2,  // demolition that came from cancelling a construction (m_CancelPhase/m_CancelFront valid)
        Migrated = 1 << 3,        // converted from a v2 save
        Incompatible = 1 << 4,    // read from an unknown save layout: Director finishes it immediately
        NaturalSampled = 1 << 5,  // m_NatAbove/m_NatBelow are valid
        Dependants = 1 << 6,      // buildings/stops/cut-edge at start (closure SlowZone, mode D)
        Replaced = 1 << 7,        // construction that replaced an existing road (starts at C1 from the old road bed)
        CalledOff = 1 << 8,       // demolition called off in D0 whose machines are still leaving. Saved so a
                                  // game saved in that window reopens the road after load instead of resuming the demolition;
                                  // the Director re-enters the call-off on rebuild. The site is removed once the gate clears.
        // Crew count of the CANCELLED construction phase minus 1 (bits 9..11, 0 = one crew = every save made before
        // crew sections existed). Written by the Director with CancelledBuild (PhasePlan.EncodeCancelCrews); the cancel hand-over spans rebuild the
        // per-section cancel fronts from it and m_CancelFront (= U x the unit sweep at cancel). Never a separate flag: use the helpers.
        CancelCrewsMask = 7 << 9,
        // The LATCHED crew count of the running phase (bits 12..14 = n, 0 = not saved = every
        // older save) and the parity of the phase it was latched for (bit 15). Written by the Director on every edge at every
        // latch; read back only on the first latch of a record (load / registry rebuild) and on "Rebuild visuals", so a save /
        // load or a rebuild keeps the layout (and the work already done) exactly. Never a separate flag: use the PhasePlan helpers.
        LatchedCrewsMask = 7 << 12,
        LatchedCrewsPhaseOdd = 1 << 15,
    }

    // Runtime flags (RoadWorksRuntime.m_Flags), recomputed by the Director every frame.
    [Flags]
    public enum RuntimeFlags : ushort
    {
        None = 0,
        Hidden = 1 << 0,          // Director APPLIED Hidden on the edge this frame
        Working = 1 << 1,         // crews are working now (not complete, traffic cleared; crews work 24/7)
        // 1 << 2 is unused (was Paused; pausing works was removed). Runtime flags are never saved.
        Rushed = 1 << 3,
        Revealed = 1 << 4,        // first frame the road became visible again (C3 start) - informational
        HideFrame = 1 << 5,       // first frame the road became hidden (creation / D1 start) - informational
        Completing = 1 << 6,      // p >= 1: visual modules tear down; Director removes the site once Ground is settled
        NeedsRebuild = 1 << 7,    // "Rebuild all work-site visuals" / after load: modules respawn their layer
        Machines = 1 << 8,        // this project is allowed machines this frame (camera distance + global budget)
        Dependants = 1 << 9,
        HideWanted = 1 << 10,     // timeline wants the road hidden (Ground clones on this; Hidden follows it)
        ModelReset = 1 << 11,     // ProgressModel was hard-reset this frame (jump, load, rebuild): Machines re-plan
        ClearingTraffic = 1 << 12,// demolition D0 waits for vehicles to leave the edge (no accrual, no machines yet)
        Releasing = 1 << 13,      // the project hands the road back (completion frame N on, or a D0 call-off):
                                  // Props tear down, Machines leave (no new spawns), the road opens once Machines report
                                  // the carriageway clear (ProjectRecord.ReleaseSince / PhasePlan.MachinesHoldRoad)
        StageSwitch = 1 << 14,    // p is held at a stage boundary (ProjectRecord.Switch != None: C4a -> C4b side swap, or
                                  // the D0 sidewalk drain before the hide) until the switch completes
    }

    // Steps of a stage switch (ProjectRecord.Switch, Director writes; runtime only, never saved:
    // after a load the switch re-runs from p). Every step is idempotent.
    public enum StageSwitch : byte
    {
        None = 0,      // no switch running
        Vacate = 1,    // p held at SwitchP; machines leave the old works band (old layout kept)
        Swap = 2,      // fresh report shows the old works band clear: old band opens, new band goes SOFT in the same update
        Drain = 3,     // Traffic drains the new band (SOFT) and reports ZonesDrained
        Ready = 4,     // new band drained and hard-closed: SoftZones = None, machines may enter once it is in WorkZonesReady
    }

    // Why no car half opens on a project (ProjectRecord.StageBlockReason, Director writes; UI text).
    public enum StageBlockReason : byte
    {
        None = 0,
        OneWay = 1,    // an edge whose through car / track lanes all travel the same way (EdgeSection.Verdict OneDirection)
        Track = 2,     // an edge has a track lane (tram)
        Stop = 3,      // a transport stop on the works half (EdgeRecord.StopZones)
        Narrow = 4,    // a works half (drive lanes + parking of that half) narrower than RRWConst.kMinWorksHalfWidth
        DeadEnd = 5,   // the chain is a dead end (EdgeRecord.CutEdge): one-direction traffic would be trapped
        Detour = 6,    // RESERVED for later: no directed detour for the closed direction
        Parking = 7,   // RESERVED for later: band parking lane not cleared in time
        LaneLayout = 8,// both directions exist but the lanes do not split into two halves: only shared two-way lanes, interleaved
                       // directions, no lane along the edge (EdgeSection.Verdict), or Traffic found no whole car group in a half
        Drop = 9,      // upgrade works: single lanes cannot be closed in this version (no lane blockers)
        Houses = 10,   // upgrade works: buildings along the road, a direction cannot be closed there in this version
        Waiting = 11,  // upgrade works: the window waits for its inputs (Traffic's classification / detour verdict, band data)
    }

    // How Traffic drains a closing group (experimental switch). Runtime selector RRWGates.Soft.
    public enum SoftMode : byte
    {
        Sentinel = 0,  // verified in game: access-restriction sentinel + lane-keyed invalidation (default)
        Forbidden = 1, // experimental: CarLane Forbidden + caution + invalidation (vanilla Forbidden snapshotted before our first write)
    }

    // Kind of a lane for RoadZoneMath.OfLane (geometry grouping).
    public enum LaneKind : byte
    {
        Car = 0,
        Track = 1,
        Parking = 2,
        Pedestrian = 3,
    }

    // Lateral zones (= lane groups) of a works road, in the CHAIN frame (+ = right of the chain direction). Used with
    // the same meaning by:
    //   * machine occupancy (ProjectRecord.MachineZones, Machines writes): zones a puppet's box touches now or will touch
    //     within its committed plan (RoadZoneMath.OfLateral);
    //   * staged opening (RoadWorksRuntime.m_OpenLanes / ProjectRecord.OpenLanes, Director writes): lane groups of a Closed
    //     works road that carry traffic anyway (car halves at work-zone speed, sidewalks vanilla); m_SoftZones (closing);
    //   * Traffic's lane groups (RoadZoneMath.OfDirectedLane / OfLane) and its drain report (EdgeRecord.ZonesDrained).
    // The halves are DIRECTION halves split at EdgeSection.DirSplit (not the curve centre); a
    // one-direction edge has no halves (every car lane is Carriageway); sidewalks and parking are per side. ushort (was byte);
    // runtime only, never saved. The old single Sidewalks bit (1 << 2) is now SidewalkLeft; `Sidewalks` is both sides.
    // Geometry: RoadZoneMath (ChainGeometry.cs).
    [Flags]
    public enum RoadZones : ushort
    {
        None = 0,
        LeftHalf = 1 << 0,        // car / track lanes of the travel direction on the chain-left side of EdgeSection.DirSplit (the
                                  // DIRECTION decides, geometry cross-checks); for boxes: chain lateral < split + kZoneCentreTolerance
        RightHalf = 1 << 1,       // same, chain-right
        SidewalkLeft = 1 << 2,    // pedestrian lanes between the chain-left carriageway edge and the composition edge
        Outside = 1 << 3,         // machines only: outside the trimmed chain [TrimU0, TrimU1] (junction nodes, finished roads)
        SidewalkRight = 1 << 4,
        ParkingLeft = 1 << 5,     // parking lanes on the chain-left side (Traffic group); boxes reaching into the strip between the
                                  // carriageway edge and the outer drive lane edge (EdgeSection.DriveLo/Hi) get it in ADDITION to the half
        ParkingRight = 1 << 6,
        // RESERVED for later (mode H lane bands): geometry only (Props / Surfaces / Machines clip to them); Traffic never uses them
        // as lane groups (it closes single slave lanes with Traffic-owned blockers).
        LeftOuter = 1 << 7,
        LeftInner = 1 << 8,
        RightOuter = 1 << 9,
        RightInner = 1 << 10,
        // Upgrade works (mode H): machine zones on the verge / outside the new road edge (no lane group): Verge and Terrain
        // sub-strips (UpgradeZones.OfSubStrip, RoadZoneMath.OfBand). Geometry only, never a Traffic group; always machine-safe
        // outside driveway keep-outs (UpgradeView.MachineSafe includes them).
        VergeLeft = 1 << 11,
        VergeRight = 1 << 12,
        Sidewalks = SidewalkLeft | SidewalkRight,
        Parking = ParkingLeft | ParkingRight,
        Carriageway = LeftHalf | RightHalf,
        AllLanes = Carriageway | Sidewalks | Parking,
    }

    // Surface (area decal) layers, in surface-priority order (the raised queue of the Cover layers wins over priority).
    public enum SurfaceLayer : byte
    {
        TopsoilStrip = 0,         // "RRW Topsoil": Ore Surface 01 untouched, RRWConst.kTopsoilTint (soil brown), alpha 35 %, Terrain, prio -94
        Subgrade = 1,             // "RRW Subgrade": Ore Surface 01 untouched, tint (.85,.75,.65), Terrain, prio -93 (verified in game)
        BaseCourseCover = 2,      // "RRW Base Course Cover": Sand -> GravelWorldspace, Terrain|Roads, prio -91, queue 2100
        FreshAsphalt = 3,         // "RRW Fresh Asphalt": Concrete -> RoadEUWorldspace, tint .45, Terrain|Roads, prio -90, NORMAL queue (under markings)
        FreshAsphaltCover = 4,    // "RRW Fresh Asphalt Cover": like FreshAsphalt but queue 2100 (over markings), prio -89
        TempMarking = 5,          // Staged opening: "RRW Temp Marking" yellow temporary lane lines on the half that is open
                                  // to traffic (band TempLines); roundness 0.01, raised queue +200 (2200), prio -87
                                  // (RRWConst.kTempMarking*; dev selectors RRWGates.TempLine*).
        MarkingBlackout = 6,      // RESERVED (queue 2100, prio -88): no product layer uses it; spans are always empty
        RoadDirt = 7,             // upgrade works: "RRW Road Dirt" dug strip on a visible road (raised queue, Terrain|Roads); only on
                                  // sub-strips without traffic (PhasePlan.UpgradeSpans)
        OldAsphalt = 8,           // upgrade works: "RRW Old Asphalt" on the removed strip outside the new road (Terrain only), only with
                                  // RRWGates.UpgradeOldAsphalt; Base Course Cover takes its place otherwise
        Count = 9,
    }

    // Lateral band a surface polygon covers.
    public enum SurfaceBand : byte
    {
        Footprint = 0,            // composition width + 2 * margin
        Carriageway = 1,          // EdgeSection carriage intervals (car + parking + on-road track lanes), one polygon per interval
        TempLines = 2,            // thin lines along the open half: EdgeSection.TempLine (TempLineCount lines)
        CarriageHalf = 3,         // FreshAsphalt / FreshAsphaltCover in C4 when the car halves are staged
                                  // (ProjectView.HalvesActive): i = 0 chain-left half, i = 1 chain-right half (EdgeSection.HalfBand),
                                  // spans from PhasePlan.SurfaceSpanHalf
        WorksBand = 4,            // mode H: one polygon per sub-strip of an upgrade band (UpgradeEdgeState.SubStrips), spans from
                                  // PhasePlan.UpgradeSpans per band and sub-strip cover
    }

    // Groups of derived entities (RRWDerived.m_Group). Used for bookkeeping, GC and dev dumps.
    public enum DerivedGroup : byte
    {
        Unknown = 0,
        Area = 1,                 // surface decals (Surfaces)
        PropStatic = 2,           // barriers, beacons, survey cones (Props)
        PropWork = 3,             // heaps, rubble at the work front (Props)
        Machine = 4,              // puppet root (Machines)
        MachinePart = 5,          // puppet trailer / sub-objects (Machines)
        Scar = 6,                 // fading decals after the works (Surfaces; m_Site = Null)
        PostSite = 7,             // puppets leaving a completed site (Machines; m_Site = Null, m_ProjectId kept)
        LaneBlocker = 8,          // invisible "RRW Lane Closure" blockers on dropped lanes of upgrade works (Traffic-owned, LivePath)
        DustPuff = 9,             // short-lived "RRW Dust Puff" emitter props at the bucket (Machines; m_Site = edge under the
                                  // puff, deleted after RRWConst.kDustPuffLifeSeconds; LivePath like every derived entity)
    }

    // Crew roles. At most 6 puppets per project.
    public enum MachineRole : byte
    {
        Excavator = 0,            // "RRW Road Excavator Quiet" (MiningExcavator01 clone, no bucket dust), root scale 0.4
        Loader = 1,               // grader: "RRW Road Excavator Quiet" clone at 0.4; FrontendLoader01 only as the fallback.
                                  // never on fresh asphalt: it leaves at the C2 -> C3 hand-over (Crew: DriveOut in C3)
        TruckA = 2,               // CoalTruck01 dump truck
        TruckB = 3,               // CoalTruck01 dump truck
        CrewTruck = 4,            // RoadMaintenanceVehicle01 (amber beacons)
        Finisher = 5,             // RoadMaintenanceVehicle01 as paver proxy (C3) / line painter (C4)
        Count = 6,                // number of PUPPET roles (ECS puppets): arrays sized by Count never hold a Roller
        // Procedural tandem road roller. NOT an ECS puppet: a managed GameObject unit drawn by
        // Machines (port of the roller prototype, verified in game). It has MachineLimits, a plan, a report box and a crew like a puppet, but no entity,
        // so it is never saved. Never used as an index into a Count-sized array; CrewPlan holds up to two roller slots per crew.
        Roller = 7,
    }

    public enum MachineActivity : byte
    {
        None = 0,                 // role not used in this phase (despawn when off camera)
        Parked = 1,               // stands at the anchor, engine idle, beacons on
        Dig = 2,                  // excavator step-and-dig cycle at the face (bones)
        Break = 3,                // excavator breaker chops (bones, short cycle)
        LoadAtFront = 4,          // truck stands beside the digger and is loaded, then shuttles to its base slot
        Shuttle = 5,              // second truck: same as LoadAtFront / DumpAtFront, offset half a cycle
        DumpAtFront = 6,          // truck drives in, turns, reverses <= 35 m to the dump slot, tips, drives out
        Spread = 7,               // loader shuttles over [anchor-7, anchor+7] with the bucket low
        Scrape = 8,               // loader creeps along with the front (topsoil strip), short reverse every 25 s
        Pave = 9,                 // finisher creeps with the front (warning + work lights)
        Paint = 10,               // finisher creeps with the front, arrow board signal
        Follow = 11,              // crew truck / loader follow the front at a distance
        DriveOut = 12,            // drive to the chain end INSIDE the trimmed chain (in the works half), never stop on
                                  // open lanes, sidewalks or outside the footprint; despawn out of sight, or at the latest
                                  // RRWConst.kPostSiteMaxSimFrames after the drive-out (or the release) began, even if visible.
                                  // the end is the one that CONNECTS to the road network (PhasePlan.ExitU /
                                  // ExitFrom, ProjectRecord.ExitAtStart/End), never a dead end; the visible cap removal is a
                                  // last resort (the cap was raised, removal out of sight is the normal path)
        Wait = 13,                // truck waits in its lane for its turn at the front (TruckB in C3)
        Compact = 14,             // road roller back-and-forth compaction passes over a FRONT-RELATIVE window behind the
                                  // working machine (RollerSlot.WinLo/WinHi re-evaluated at every reversal), small lateral shift per pass
    }

    // What a roller slot compacts (PhasePlan.Crew -> CrewPlan.Roller0/Roller1).
    public enum RollerDuty : byte
    {
        None = 0,
        BaseCourse = 1,           // C2: the gravel base course behind the grader (vibration on)
        Breakdown = 2,            // C3: first roller right behind the paver (>= ~6 m behind its screed; vibration on)
        Finish = 3,               // C3: second (finish) roller further back (static, no vibration)
        Backfill = 4,             // D2: restored ground behind the backfilling loader (optional, RRWConst.kRollerInRestore)
    }

    // Truck load shown on CoalTruck01 (DeliveryTruck.m_Resource) and on dumped heaps.
    public enum LoadKind : byte
    {
        None = 0,
        Ore = 1,                  // brown spoil / soil
        Stone = 2,                // grey gravel / rubble
        Coal = 3,                 // black hot mix
    }

    // Runtime state of a works edge. Added by the Director to every edge with RoadWorksSite, never saved.
    // SINGLE WRITER: the Director (Mod1). Writers never construct a fresh struct for an existing component:
    // GetComponentData -> change own fields -> SetComponentData.
    public struct RoadWorksRuntime : IComponentData
    {
        public uint m_ProjectId;
        public WorksKind m_Kind;
        public VisualMode m_Mode;
        public WorksPhase m_Phase;
        public RuntimeFlags m_Flags;
        public float m_Progress;          // p in [0,1] this frame
        public float m_PhaseFraction;     // f in [0,1] within m_Phase
        public float m_ChainU0, m_ChainU1;// chain coordinates of curve t=0 / t=1 (copy of the saved values)
        public float m_ChainLength;       // project chain length U
        public float m_EdgeLength;        // Curve.m_Length
        public float m_SubgradeDepth;     // dSub for this road class (0.75 / 1.0)
        public float m_GradeOffset;       // profile grade relative to curve Y (NetCompositionData.m_SurfaceHeight.min)
        public ProgressModel m_Model;     // p(sim frame) for continuous fronts in jobs (Machines)
        public ClosureLevel m_ClosureTarget;   // Director decides; Traffic applies (EdgeRecord.ClosureApplied)
        public RoadZones m_OpenLanes;     // Director: lane groups that carry traffic although m_ClosureTarget is Closed
                                          // (staged opening; car halves at work-zone speed, sidewalks vanilla). None on hidden
                                          // edges. Ignored by Traffic when the target is not Closed. Traffic reports
                                          // EdgeRecord.OpenLanesApplied once the lanes carry it.
        public RoadZones m_SoftZones;     // Director: groups of a Closed, visible works edge that are CLOSING (SOFT: no new
                                          // traffic, vehicles on them drive out; RRWGates.Soft). Never overlaps m_OpenLanes. None on
                                          // hidden edges. Traffic reports EdgeRecord.ZonesDrained once they are empty.
        public uint m_PhaseChangedFrame;  // sim frame of the last phase change
        public byte m_Crews;              // Director: crew sections of the project this phase (ProjectRecord.Crews; 0 = 1).
                                          // PlanInput.From copies it so Ground / Wear see the same per-section fronts

        public bool Hidden => (m_Flags & RuntimeFlags.Hidden) != 0;
        public bool HideWanted => (m_Flags & RuntimeFlags.HideWanted) != 0;
        public bool Working => (m_Flags & RuntimeFlags.Working) != 0;
        public bool Has(RuntimeFlags f) => (m_Flags & f) != 0;
    }

    [Flags]
    public enum GroundFlags : byte
    {
        None = 0,
        Clones = 1 << 0,          // the edge's Composition points at RRW clones
        RevealHold = 1 << 1,      // road visible again, clones kept with the last BED for kRevealHoldUpdates (reveal rule verified in game)
        Restoring = 1 << 2,       // vanilla TerrainComposition values written on the clones; repoint pending
    }

    // Ground output of a works edge. Added by the Director together with RoadWorksRuntime (m_Kind = Unknown);
    // SINGLE WRITER afterwards: Ground (ModificationEnd, right before TerrainSystem). Never saved.
    public struct RoadWorksGround : IComponentData
    {
        public TerrainProfileKind m_Kind; // applied profile kind; Unknown until Ground's first write; Vanilla once settled
        public GroundFlags m_Flags;
        public float m_FloorMinRel;       // applied middle cut cap  relative to grade (Min.y), +inf if none
        public float m_FloorMaxRel;       // applied middle fill floor relative to grade (Max.y), -inf if none
        public float m_AppliedY;          // applied target y relative to grade (BED y / MORPH y), 0 when vanilla/natural
        public float m_AppliedT;          // applied MORPH t (1 for BED, 0 for NATURAL/VANILLA): cancel-restore input
        public uint m_StepUpdate;         // RRWClock.UpdateIndex of the last applied terrain change (re-snap trigger)
        public uint m_HoldStartUpdate;    // RRWClock.UpdateIndex when RevealHold began

        public static RoadWorksGround Initial => new RoadWorksGround
        {
            m_Kind = TerrainProfileKind.Unknown,
            m_FloorMinRel = float.PositiveInfinity,
            m_FloorMaxRel = float.NegativeInfinity,
        };

        public bool Known => m_Kind != TerrainProfileKind.Unknown;
        // Vanilla composition back in place, no clone referenced: the Director may remove the site / delete-finish.
        public bool Settled => m_Kind == TerrainProfileKind.Vanilla && (m_Flags & (GroundFlags.Clones | GroundFlags.RevealHold | GroundFlags.Restoring)) == 0;
    }

    // Floor heights for props / machines / decal vertices.
    public static class GroundMath
    {
        // World Y of the terrain at a point of the edge, given the curve Y there and the natural ground Y
        // (CPU TerrainY outside the edge, or any natural estimate). Visible road => road surface.
        // Ground output not known yet (first frame of a site / after load) => use the PLANNED profile
        // (PhasePlan.Terrain), which is exactly what Ground applies in the same frame (NeedsRebuild bypass).
        public static float FloorWorldY(in RoadWorksRuntime r, in RoadWorksGround g, in TerrainProfile planned, float curveY, float naturalY)
        {
            if (!r.Hidden) return curveY;
            float minRel, maxRel;
            if (g.Known && g.m_Kind != TerrainProfileKind.Vanilla) { minRel = g.m_FloorMinRel; maxRel = g.m_FloorMaxRel; }
            else { minRel = planned.MiddleCap; maxRel = planned.MiddleFloor; }
            return Clamp(curveY + r.m_GradeOffset, naturalY, minRel, maxRel);
        }

        // Same, for a pure profile (planning code, jobs).
        public static float FloorWorldY(in TerrainProfile p, float gradeOffset, float curveY, float naturalY) =>
            p.Kind == TerrainProfileKind.Vanilla ? curveY : Clamp(curveY + gradeOffset, naturalY, p.MiddleCap, p.MiddleFloor);

        static float Clamp(float grade, float naturalY, float minRel, float maxRel)
        {
            float rel = float.IsNaN(naturalY) ? 0f : naturalY - grade;
            if (rel > minRel) rel = minRel;
            if (rel < maxRel) rel = maxRel;
            return grade + rel;
        }
    }

    // p(frame) = P0 + PPerFrame * (frame - Frame0), clamped to [0,1].
    // DOMAIN: SIMULATION frames. The Director anchors at Frame0 = RRWClock.SimFrame (where the accrued p is exact);
    // machines evaluate At(RRWClock.RenderFrame, RRWClock.RenderFrameTime): RenderingSystem.frameIndex is the sim frame
    // index plus a smoothed offset (|offset| <= 15), i.e. the same domain.
    // Re-anchoring (rate change, every 256 frames) is CONTINUOUS: P0 = old.At(SimFrame); the drift to the accrued p is
    // absorbed by adjusting PPerFrame over the next window (clamped to +-10 %). Only jumps (SetProgress, JumpPhase,
    // load, rebuild, cancel) hard-reset and raise RuntimeFlags.ModelReset.
    public struct ProgressModel
    {
        public float P0;
        public uint Frame0;
        public float PPerFrame;

        public float At(uint frame, float frameTime)
        {
            double df = unchecked((int)(frame - Frame0)) + (double)frameTime;
            double p = P0 + PPerFrame * df;
            return p < 0.0 ? 0f : p > 1.0 ? 1f : (float)p;
        }

        // Continuous re-anchor at `frame` towards the true progress `trueP` with nominal rate `rate` (p per frame).
        // The model reaches trueP + rate*window at frame + window (drift absorbed), slope clamped to rate*(1 +- 10 %).
        public static ProgressModel Reanchor(in ProgressModel old, uint frame, float trueP, float rate, uint window)
        {
            float p0 = old.At(frame, 0f);
            float slope = rate;
            if (rate > 0f && window > 0)
            {
                float target = trueP + rate * window;
                slope = (target - p0) / window;
                slope = math.clamp(slope, rate * 0.9f, rate * 1.1f);
            }
            return new ProgressModel { P0 = p0, Frame0 = frame, PPerFrame = slope };
        }

        public static ProgressModel Reset(uint frame, float p, float rate) => new ProgressModel { P0 = p, Frame0 = frame, PPerFrame = rate };
    }

    public enum TerrainProfileKind : byte
    {
        Vanilla = 0,     // no override: edge points at the vanilla compositions
        Natural = 1,     // clip moved underground, no clamp: original ground
        Morph = 2,       // MORPH(t, y) between natural brackets and BED(y)
        Bed = 3,         // flat floor at y, edges at grade
        ClipOnly = 4,    // vanilla flattening at grade, clip moved (node ends next to non-works edges)
        Unknown = 255,   // RoadWorksGround before Ground's first write
    }

    // Tag + back reference on every entity a module derives from a site (areas, props, puppets, puppet parts).
    // Always together with Game.Routes.LivePath (never saved). Use EcsUtil.TagDerived.
    public struct RRWDerived : IComponentData
    {
        public Entity m_Site;     // the works edge (props/areas); Null for scars and post-site puppets
        public uint m_ProjectId;  // owning project (machines and parts are GC'd by project, never by m_Site)
        public byte m_Group;      // DerivedGroup
    }

    // Tag on puppet roots and trailers (Machines).
    public struct RRWMachine : IComponentData
    {
        public uint m_ProjectId;
        public byte m_Role;       // MachineRole
        public byte m_IsTrailer;
        public byte m_Crew;       // crew (section) index 0..Crews-1 the puppet works for (Machines writes; re-mapped at hand-overs)
    }

    // Tag on every composition entity Ground clones. Composition entities are never saved
    // (SerializerSystem excludes NetCompositionData) but survive loads (ClearSystem keeps them): this tag finds orphans.
    public struct RRWCompositionClone : IComponentData
    {
        public Entity m_Source;   // vanilla composition it was instantiated from
        public Entity m_Edge;     // works edge using it
        public byte m_Slot;       // 0 edge, 1 start node end, 2 end node end
    }

    // Tag on the single global access-restriction sentinel (Traffic). No PrefabRef -> never saved.
    public struct RRWAccessZone : IComponentData { }

    // Chain span [A,B] in metres (chain coordinates). Empty when B - A <= 0.
    public struct Span
    {
        public float A, B;
        public Span(float a, float b) { A = a; B = b; }
        public static Span Empty => new Span(0f, 0f);
        public bool IsEmpty => B - A <= 1e-4f;
        public float Length => IsEmpty ? 0f : B - A;
        public bool Contains(float u) => !IsEmpty && u >= A && u <= B;
        public override string ToString() => IsEmpty ? "[]" : "[" + A.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," + B.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "]";
    }
}
