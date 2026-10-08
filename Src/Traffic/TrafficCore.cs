using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetCarLaneFlags = Game.Net.CarLaneFlags;
using NetParkingLane = Game.Net.ParkingLane;
using NetPedestrianLane = Game.Net.PedestrianLane;
using NetSubLane = Game.Net.SubLane;
using NetTrackLane = Game.Net.TrackLane;

// Traffic module: closure levels on the lanes of works edges.
// Ported from the traffic prototype (verified in game with real traffic), keyed on registry edges.
//
// Model: the Director decides RoadWorksRuntime.m_ClosureTarget (+ m_OpenLanes / m_SoftZones); TrafficRequestSystem (Mod1)
// turns it into an EdgeClosure here and refreshes the edge's lanes (PathfindUpdated; Updated on track-only lanes). Vanilla
// LaneDataSystem / ParkingLaneDataSystem then reset those lanes to vanilla values at ModificationEnd and our
// LaneClosure / ParkingClosure systems, right after them, snapshot the vanilla values and write the closure.
// LanesModifiedSystem pushes the result into the pathfind graph in the same frame. Opening = drop the closure and
// refresh: vanilla restores the lanes by itself (except CarLane.Forbidden, which we restore ourselves).
//
// Every lane of a Closed edge belongs to a lane GROUP (RoadZones bits: the direction halves split at
// EdgeSection.DirSplit, SidewalkLeft/Right, ParkingLeft/Right) and carries one LaneState: OPEN (m_OpenLanes), SOFT
// (m_SoftZones: closing, drained), CLOSED-S (sentinel) or CLOSED-B (blockage 0..255 after the drain, car groups of visible
// edges with buildings; experimental switch RRWGates.ClosedB).
//
// Everything here is runtime-only. The sentinel has no PrefabRef (never saved); lane fields are neutralised around
// every save (TrafficSaveGuard/TrafficSaveRestore). OnGamePreload clears all of it.
namespace RealisticRoadWorks.V3.Traffic
{
    // What one lane of a closure carries. Vanilla = untouched (dev closures: lanes outside the filter).
    // Slow = carries traffic: car lanes at work-zone speed with caution, pedestrian / track / parking lanes vanilla.
    public enum LaneState : byte { Vanilla = 0, Slow = 1, Soft = 2, ClosedS = 3, ClosedB = 4 }

    // Lane groups of ONE edge, cached per (GeometryRevision, chain direction, left-hand traffic, arc).
    //  * car / track lanes: RoadZoneMath.OfDirectedLane (travel direction decides, geometry cross-checks against DirSplit;
    //    mismatch / no split / a lane across the edge -> Carriageway). Master / slave groups share ONE zone = the union of the
    //    zones of their lanes (SubLane indices m_MinIndex..m_MaxIndex): vanilla CarLaneSelectIterator moves vehicles between
    //    the slaves of a group without looking at access restrictions, and paths run over the master lane;
    //  * parking lanes: OfLane(.., Parking, ..) -> ParkingLeft / ParkingRight;
    //  * pedestrian lanes: OfLane(.., Pedestrian, ..) -> SidewalkLeft / SidewalkRight, or the half(s) of a path on the carriageway.
    public sealed class LaneGroups
    {
        public Entity Edge;
        public EdgeArc Arc;
        public EdgeSection Section;
        public bool ChainReversed;
        public bool LeftHandTraffic;
        public int Revision = int.MinValue;
        public bool Ready;
        public int Recomputes;              // cache drops (stats / dev)
        private readonly Dictionary<Entity, RoadZones> m_Map = new Dictionary<Entity, RoadZones>(32);
        private readonly Dictionary<Entity, LaneProbe> m_Probe = new Dictionary<Entity, LaneProbe>(32);

        // Returns true when the cache was dropped (groups may differ from before).
        public bool Sync(Entity edge, int revision, bool chainReversed, bool leftHandTraffic, EdgeArc arc, in EdgeSection sec)
        {
            if (Ready && Edge == edge && Revision == revision && ChainReversed == chainReversed && LeftHandTraffic == leftHandTraffic &&
                ReferenceEquals(Arc, arc)) return false;
            Edge = edge;
            Revision = revision;
            ChainReversed = chainReversed;
            LeftHandTraffic = leftHandTraffic;
            Arc = arc;
            Section = sec;
            Ready = true;
            Recomputes++;
            m_Map.Clear();
            m_Probe.Clear();
            return true;
        }

        // Registry edge: EdgeRecord geometry (Director-measured section). Before the Director built the arc, the edge curve
        // is used with the registry section (a later arc build drops the cache: ReferenceEquals).
        public bool SyncFromRecord(EntityManager em, Entity edge, EdgeRecord rec, bool chainReversed, bool leftHandTraffic)
        {
            var arc = rec.Arc;
            if (arc == null)
            {
                if (Ready && Edge == edge && Arc != null && Revision == -2 - rec.GeometryRevision && ChainReversed == chainReversed &&
                    LeftHandTraffic == leftHandTraffic) return false;
                if (!em.Exists(edge) || !em.HasComponent<Curve>(edge)) return false;
                arc = new EdgeArc(DeferredNet.WorksCurve(em, edge));
                return Sync(edge, -2 - rec.GeometryRevision, chainReversed, leftHandTraffic, arc, rec.Section);
            }
            return Sync(edge, rec.GeometryRevision, chainReversed, leftHandTraffic, arc, rec.Section);
        }

        // Non-registry edge (dev closures, probes of vanilla roads): measure the section from the edge itself (edge frame = chain).
        public bool SyncFromEdge(EntityManager em, Entity edge, bool leftHandTraffic)
        {
            if (Ready && Edge == edge && LeftHandTraffic == leftHandTraffic && Revision == -1) return false;
            if (!em.Exists(edge) || !em.HasComponent<Curve>(edge)) return false;
            var arc = new EdgeArc(em.GetComponentData<Curve>(edge).m_Bezier);
            var sec = EcsUtil.MeasureSection(em, edge, arc);
            return Sync(edge, -1, false, leftHandTraffic, arc, sec);
        }

        public RoadZones Of(EntityManager em, Entity lane)
        {
            if (m_Map.TryGetValue(lane, out var g)) return g;
            g = Compute(em, lane);
            if (m_Map.Count >= 512) { m_Map.Clear(); m_Probe.Clear(); }
            m_Map[lane] = g;
            return g;
        }

        // The lane measured from its own geometry against the edge frame at its own projected points (LaneSection.Probe: the same
        // measurement EcsUtil.MeasureSection used for DirSplit, so a lane's side and direction agree with the split on curved,
        // S-curved and reversed edges). Bits are not needed here (lane kinds come from the components).
        private bool ProbeOf(EntityManager em, Entity lane, out LaneProbe probe)
        {
            if (m_Probe.TryGetValue(lane, out probe)) return true;
            probe = default;
            if (Arc == null || !em.Exists(lane) || !em.HasComponent<Curve>(lane)) return false;
            probe = LaneSection.Probe(Arc, LaneShiftRegistry.MeasureCurve(em, lane), 0f, LaneBits.None);
            if (m_Probe.Count >= 512) m_Probe.Clear();
            m_Probe[lane] = probe;
            return true;
        }

        // +1 = lane tangent agrees with the edge tangent, -1 = against, 0 = unknown / across the edge.
        public sbyte DirOf(EntityManager em, Entity lane) => ProbeOf(em, lane, out var p) ? p.Dir : (sbyte)0;

        // Edge-frame lateral of the lane middle (median of the interior samples; false without a curve / arc).
        public bool LateralOf(EntityManager em, Entity lane, out float lateral)
        {
            lateral = 0f;
            if (!ProbeOf(em, lane, out var p)) return false;
            lateral = p.Centre;
            return true;
        }

        // Edge-frame lateral of a world point.
        public bool LateralOfPoint(float3 pt, out float lateral)
        {
            lateral = 0f;
            if (Arc == null) return false;
            float s = Arc.Project(pt);
            lateral = math.dot(pt - Arc.Position(s), Arc.Right(s));
            return true;
        }

        private RoadZones Compute(EntityManager em, Entity lane)
        {
            if (!em.Exists(lane)) return RoadZones.Carriageway;
            if (em.HasComponent<NetPedestrianLane>(lane))
            {
                if (!LateralOf(em, lane, out float pl)) return RoadZones.Sidewalks;      // unknown side: both (fail closed)
                return RoadZoneMath.OfLane(pl, LaneKind.Pedestrian, ChainReversed, Section);
            }
            bool car = em.HasComponent<NetCarLane>(lane), track = em.HasComponent<NetTrackLane>(lane);
            if (em.HasComponent<NetParkingLane>(lane) && !car)
            {
                if (!LateralOf(em, lane, out float kl)) return RoadZones.Parking;
                return RoadZoneMath.OfLane(kl, LaneKind.Parking, ChainReversed, Section);
            }
            if (!car && !track) return RoadZones.Carriageway;
            int lo = -1, hi = -1;
            if (em.HasComponent<SlaveLane>(lane)) { var sl = em.GetComponentData<SlaveLane>(lane); lo = sl.m_MinIndex; hi = sl.m_MaxIndex; }
            else if (em.HasComponent<MasterLane>(lane)) { var ml = em.GetComponentData<MasterLane>(lane); lo = ml.m_MinIndex; hi = ml.m_MaxIndex; }
            var own = Directed(em, lane);
            if (lo < 0 || hi < lo || !em.HasBuffer<NetSubLane>(Edge)) return own;
            var buf = em.GetBuffer<NetSubLane>(Edge, true);
            if (hi >= buf.Length) return own | RoadZones.Carriageway;      // inconsistent indices: fail closed
            var g = own;
            for (int i = lo; i <= hi; i++)
            {
                var other = buf[i].m_SubLane;
                if (other == lane || !em.Exists(other) || em.HasComponent<NetPedestrianLane>(other)) continue;
                if (!em.HasComponent<NetCarLane>(other) && !em.HasComponent<NetTrackLane>(other)) continue;
                g |= Directed(em, other);
            }
            return g;
        }

        // Half of one drive lane by its travel direction, cross-checked by geometry (no merging with its group). A shared two-way
        // lane and a connector across the road (no direction) belong to both halves (open only when both are open).
        private RoadZones Directed(EntityManager em, Entity lane)
        {
            if (!LateralOf(em, lane, out float lateral)) return RoadZones.Carriageway;
            sbyte d = DirOf(em, lane);
            if (d == 0) return RoadZones.Carriageway;
            if (em.HasComponent<NetCarLane>(lane) && (em.GetComponentData<NetCarLane>(lane).m_Flags & NetCarLaneFlags.Twoway) != 0) return RoadZones.Carriageway;
            if (em.HasComponent<NetTrackLane>(lane) && (em.GetComponentData<NetTrackLane>(lane).m_Flags & TrackLaneFlags.Twoway) != 0) return RoadZones.Carriageway;
            var kind = em.HasComponent<NetCarLane>(lane) ? LaneKind.Car : LaneKind.Track;
            return RoadZoneMath.OfDirectedLane(lateral, kind, d > 0, ChainReversed, LeftHandTraffic, Section);
        }

        // Groups that have at least one lane on the edge (car / track / pedestrian / parking lanes).
        public RoadZones Present(EntityManager em, List<Entity> lanes)
        {
            var r = RoadZones.None;
            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane)) continue;
                if (!em.HasComponent<NetCarLane>(lane) && !em.HasComponent<NetPedestrianLane>(lane) &&
                    !em.HasComponent<NetTrackLane>(lane) && !em.HasComponent<NetParkingLane>(lane)) continue;
                r |= Of(em, lane);
            }
            return r & RoadZones.AllLanes;
        }

        // Halves (chain frame) that hold at least one WHOLE car-lane group of this edge (EdgeRecord.CarHalves).
        public RoadZones CarHalves(EntityManager em, List<Entity> lanes)
        {
            var r = RoadZones.None;
            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane) || !em.HasComponent<NetCarLane>(lane)) continue;
                var g = Of(em, lane) & RoadZones.Carriageway;
                if (g == RoadZones.LeftHalf) r |= RoadZones.LeftHalf;
                else if (g == RoadZones.RightHalf) r |= RoadZones.RightHalf;
            }
            return r;
        }
    }

    // One closed / slowed works edge (or a dev closure, rrw.tr.close). Level is never Open: an open edge has no EdgeClosure.
    public sealed class EdgeClosure
    {
        public Entity Edge;
        public uint ProjectId;
        public ClosureLevel Level;          // SlowZone or Closed
        public bool ParkClosed;             // Closed AND building-free: ParkingDisabled + sentinel on parking lanes (rule verified in game)
        public float SlowSpeed;             // m/s, SlowZone speed limit (min with the vanilla limit)
        public bool Caution = true;         // caution 0..255 on car lanes (SlowZone and Closed)
        public bool HardRequested;          // HardCloseAfterDrain setting on and Level == Closed (whole edge)
        public bool HardActive;             // blockage 0..255 written on every closed car lane (only after the drain)
        public uint HardNextCheck;          // UpdateIndex of the next drain test
        public uint ChangedUpdate;          // UpdateIndex of the last refresh (level or parameter change)
        public bool LanesConfirmed;         // LaneClosure wrote at least one lane since the last refresh
        public bool RelocatePending;        // relocate parked cars at Mod1 once RelocateDue is reached
        public uint RelocateDue;
        public int LaneWrites;              // components written (stats)
        public int Refreshes;               // refreshes requested (stats)

        // ---- staged opening. Only meaningful while Level == Closed.
        public RoadZones Open;              // groups that carry traffic (m_OpenLanes; never parking, never Soft)
        public RoadZones Soft;              // groups being drained (m_SoftZones)
        public SoftMode SoftModeApplied;    // RRWGates.Soft when the lanes were last refreshed
        public RoadZones ClosedB;           // car groups carrying CLOSED-B (blockage instead of the sentinel)
        public RoadZones OpenPresent;       // groups of the lanes that really carry traffic (car / pedestrian / track lanes)
        public LaneGroups Groups;           // shared with the edge's TrafficEdgeSlot (registry edges) or own (dev)
        // ---- upgrade works: empty new parking lanes switched off (ParkingDisabled only, no sentinel). Shared with the edge's
        //      EdgeUpgradeTraffic (null on every other edge).
        public HashSet<Entity> ParkingOff;

        // ---- dev closures (rrw.tr.close, Traffic dev commands only): explicit per-lane states, never reconciled.
        public Dictionary<Entity, LaneState> DevLanes;
        public bool IsDev => DevLanes != null;
        public bool DevBlocked;             // some dev lane is ClosedB

        // Blockage written on some lane of this closure (parking free space depends on it; save guard).
        public bool AnyBlockage => HardActive || ClosedB != RoadZones.None || DevBlocked;

        public string State =>
            IsDev ? "Dev(" + DevLanes.Count + " lanes)" :
            (HardActive ? "HardClose" : (HardRequested ? Level + "+hardPending" : Level.ToString())) +
            (Level == ClosureLevel.Closed && Open != RoadZones.None ? "+open(" + RoadZoneMath.Describe(Open) + ")" : "") +
            (Level == ClosureLevel.Closed && Soft != RoadZones.None ? "+soft(" + RoadZoneMath.Describe(Soft) + "," + SoftModeApplied + ")" : "") +
            (Level == ClosureLevel.Closed && ClosedB != RoadZones.None ? "+closedB(" + RoadZoneMath.Describe(ClosedB) + ")" : "");

        public RoadZones GroupOf(EntityManager em, Entity lane) => Groups != null ? Groups.Of(em, lane) : RoadZones.Carriageway;

        // The state this lane must carry now.
        public LaneState StateOf(EntityManager em, Entity lane)
        {
            if (IsDev) return DevLanes.TryGetValue(lane, out var ds) ? ds : LaneState.Vanilla;
            if (Level != ClosureLevel.Closed) return LaneState.Slow;
            if (LaneShiftRegistry.Relocated(lane)) return LaneState.Slow;   // moved onto a temporary lane of the open half
            if (LaneShiftRegistry.Retired(lane)) return LaneState.ClosedS;  // no temporary lane for it while a half is resurfaced
            var g = GroupOf(em, lane);
            if (RoadZoneMath.LaneOpen(g, Open)) return LaneState.Slow;
            if ((g & Soft) != 0 && (g & ~(Soft | Open) & RoadZones.AllLanes) == 0) return LaneState.Soft;
            if (ClosedB != RoadZones.None && (g & RoadZones.Carriageway) != 0 && (g & ~ClosedB & RoadZones.AllLanes) == 0 &&
                em.HasComponent<NetCarLane>(lane)) return LaneState.ClosedB;
            return LaneState.ClosedS;
        }

        // True when this lane carries traffic (its whole group is open; SlowZone edges; dev lanes left vanilla / slow).
        public bool LaneIsOpen(EntityManager em, Entity lane)
        {
            var st = StateOf(em, lane);
            return st == LaneState.Slow || st == LaneState.Vanilla;
        }

        // Groups of the lanes that carry traffic now (car, pedestrian and track lanes; parking lanes do not count).
        public RoadZones ComputeOpenPresent(EntityManager em, List<Entity> lanes)
        {
            if (IsDev || Level != ClosureLevel.Closed || Open == RoadZones.None) return RoadZones.None;
            var r = RoadZones.None;
            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane)) continue;
                if (!em.HasComponent<NetCarLane>(lane) && !em.HasComponent<NetPedestrianLane>(lane) && !em.HasComponent<NetTrackLane>(lane)) continue;
                var g = GroupOf(em, lane);
                if (RoadZoneMath.LaneOpen(g, Open)) r |= g;
            }
            return r & RoadZones.AllLanes;
        }

        public void RecountDevBlocked()
        {
            DevBlocked = false;
            if (DevLanes == null) return;
            foreach (var v in DevLanes.Values) if (v == LaneState.ClosedB) { DevBlocked = true; return; }
        }
    }

    // Vanilla values of a lane as LaneDataSystem / ParkingLaneDataSystem left them, captured right before we override.
    public struct LaneSnap
    {
        public bool HasCar, HasPed, HasTrack, HasParking;
        public NetCarLane Car;
        public NetPedestrianLane Ped;
        public NetTrackLane Track;
        public NetParkingLane Parking;
        // ParkingLaneDataSystem derives m_FreeSpace from the neighbouring car lane's blockage, so under HardClose / CLOSED-B the
        // freshly computed value is 0. Keep the last value computed WITHOUT our blockage for the save guard.
        public bool HasFreeSpace;
        public float FreeSpaceUnblocked;
    }

    // Per-edge Traffic bookkeeping in EdgeRecord.Slots[Traffic] (wiped with the registry on load).
    public sealed class TrafficEdgeSlot
    {
        public bool Seen;                   // first reconcile done (site start)
        public int Buildings;               // ConnectedBuilding count at the last check
        public uint BuildingsCheckUpdate;
        public bool BuildingsChecked;
        public bool BuildingsLogged;        // "buildings spawned along a closed works road" logged once
        public bool Classified;             // full (BFS) classification done
        public bool ClassifyDependants;     // TrafficAnalysis.Classify result (highway reason ignored by design)
        public string ClassifyReasons = "";
        public int ClassifiedRevision = -1; // EdgeRecord.GeometryRevision at the last full classification
        public bool ClassifiedReversed;
        public uint ClassifiedUpdate;
        public bool CutEdgeFull;            // CutEdge of the last full classification
        public uint ZonesClassifiedUpdate;  // last (light or full) zone classification
        public int ZonesClassifiedBuildings = -1;
        public bool ClassifyQueued;
        public bool DrainValid;             // TrafficDrained computed while the project is ClearingTraffic
        public uint DrainCheckUpdate;
        public int LastMoving;              // moving vehicles found at the last drain check

        // ---- lane groups + drain report
        public readonly LaneGroups Groups = new LaneGroups();
        public RoadZones Armed;             // closed / SOFT groups being drained (not yet drained)
        public RoadZones Drained;           // groups drained (kept while they stay closed / SOFT)
        public RoadZones ClearB;            // car groups with no vehicle at all (moving or stopped) at the last scan (CLOSED-B entry)
        public RoadZones LastBusy;          // groups with a Moving object at the last scan (dev)
        public RoadZones LastOccupied;      // groups with any non-parked object at the last scan (dev)
        public RoadZones TimedOut;          // groups drained by the timeout and not yet seen clean (EdgeRecord.ZonesTimedOut)
        public RoadZones Parked;            // closed parking groups with parked cars at the last scan (EdgeRecord.ZonesParked)
        public uint NextDrainScan;
        public uint LastScanUpdate;
        public readonly byte[] Clean = new byte[TrafficBits.Count];
        public readonly uint[] ArmSim = new uint[TrafficBits.Count];
        public readonly uint[] LastFlip = new uint[TrafficBits.Count];
        public readonly uint[] LastFlipWarn = new uint[TrafficBits.Count];
        public uint PendingBSince;          // update since a drained car group waits for CLOSED-B (0 = none; rrw.check P4)
        public RoadZones Present;           // cached LaneGroups.Present (lane set signature + regroup)
        public int PresentSig;
        public bool PresentValid;
    }

    // Per-project Traffic bookkeeping in ProjectRecord.Slots[Traffic].
    public sealed class TrafficProjectSlot
    {
        public ClosureLevel LoggedLevel;    // last project-level closure logged (Info, once per change)
        public bool LoggedAny;
        public bool LoggedDrain;
        public RoadZones LoggedOpen;        // union of OpenLanesApplied last logged (staged opening Info line)
        public bool U3Warned;               // "target below Closed against the U3 rule" warned once
        public RoadZones TimeoutWarned;     // drain timeout Warn once per project and group
        public bool OverlapWarned;          // m_SoftZones ∩ m_OpenLanes warned once
        public RoadZones LoggedSoft, LoggedClosedB, LoggedDrained;   // Info lines on change
    }

    // The six lane-group bits of RoadZones (Outside and the reserved lane-band bits are not lane groups).
    public static class TrafficBits
    {
        public const int Count = 6;
        public static readonly RoadZones[] Bits =
        {
            RoadZones.LeftHalf, RoadZones.RightHalf, RoadZones.SidewalkLeft, RoadZones.SidewalkRight, RoadZones.ParkingLeft, RoadZones.ParkingRight,
        };
    }

    // Effective settings for this frame (RRWSettings.Current may be missing very early; defaults then).
    public struct TrafficSettings
    {
        public float SlowSpeed;
        public bool Reroute;
        public bool Relocate;
        public bool HardClose;
        public bool ClosureLayer;
        public bool ClosedB;                // RRWGates.ClosedB (experimental switch)
        public SoftMode Soft;               // RRWGates.Soft (experimental switch)

        public static TrafficSettings Read()
        {
            var s = RRWSettings.Current;
            var t = new TrafficSettings
            {
                SlowSpeed = s != null ? s.WorkZoneSpeed : 30f / 3.6f,
                Reroute = s == null || s.RerouteInFlight,
                Relocate = s == null || s.RelocateParkedCars,
                HardClose = s != null && s.HardCloseAfterDrain,
                ClosureLayer = RRWDebug.On(DebugLayers.Closure),
                ClosedB = RRWGates.ClosedB,
                Soft = RRWGates.Soft,
            };
            if (!(t.SlowSpeed > 0.5f)) t.SlowSpeed = 30f / 3.6f;
            return t;
        }
    }

    public static class TrafficConst
    {
        public const int kSweepIntervalUpdates = 64;        // periodic in-flight path sweep while Closed edges exist
        public const int kScanAhead = 256;                  // path elements scanned beyond m_ElementIndex (verified in game)
        public const int kDestTail = 4;                     // last path elements that count as "destination on the edge"
        public const double kScanSliceMs = 1.5;             // main-thread budget per sweep slice (target: < 2 ms per slice)
        public const int kDrainCheckInterval = 16;          // updates between drain tests while ClearingTraffic
        public const int kBuildingsCheckInterval = 16;      // updates between ConnectedBuilding counts per edge
        public const int kClassifyInterval = 1024;          // full (BFS) re-classification of works edges this often
        public const int kClassifyZonesInterval = 256;      // zone classification (buildings, stops, track) at least this often
        public const int kConfirmFallbackUpdates = 4;       // ClosureApplied without lane confirmation (edge without lanes)
        public const int kVerifyDelayUpdates = 3;           // restore check after opening (verified in game: 3 frames suffice)
        public const int kHardCheckInterval = 16;           // HardClose drain test interval
        public const int kSnapPruneInterval = 512;          // drop snapshots of lanes that no longer exist
        public const int kInvalidatedTrackMax = 50000;      // cap of the per-entity invalidation counter map
        public const int kBfsBudget = 2000;                 // cut-edge BFS node budget per side
        public const int kDrainCleanScans = 2;              // two clean scans kDrainCheckUpdates apart = drained
        public const int kPendingBWarnUpdates = 600;        // rrw.check P4: a drained car group still not CLOSED-B after this
        // ---- upgrade works
        public const float kDropOpenLaneClear = 0.75f;      // lane closure marker edge (radius kBlockerRadius) at least this far from the
                                                            // edge of every lane that stays open (closer: the whole direction is blocked)
        public const int kDropRegisterTries = 8;            // updates new markers may wait for the game to register them on their lane
        public const int kDropBlockageTries = 16;           // updates a dropped lane may wait for its blockage after the refresh
        public const float kDropMachineClear = 1.0f;        // a machine body this close to a released lane's edge (or a switched-off parking lane
                                                            // on its way back into use) keeps the lane closed
        public const float kDropMachineMaxHalf = 3.0f;      // widest machine body across the road on either side of its root (the excavator's
                                                            // swept half width is about 2.9 m); caps the unscaled prefab box of a scaled puppet
        public const int kParkingOffCheckInterval = 16;     // updates between the new-parking checks of one edge
        public const int kDetourRecheckUpdates = 1024;      // detour verdict re-computed this often (and on every project revision)
    }

    // Shared static state. Main thread only. Reset on every game preload.
    public static class TrafficState
    {
        public static readonly Dictionary<Entity, EdgeClosure> Closures = new Dictionary<Entity, EdgeClosure>();
        public static readonly Dictionary<Entity, LaneSnap> Snaps = new Dictionary<Entity, LaneSnap>();
        public static Entity Sentinel = Entity.Null;
        public static int ClosedCount;                      // closures with Level == Closed (recount on every change)
        public static int Revision;                         // ++ whenever a closure is added / removed / changed
        public static bool InvalidationEventPending;        // lanes narrowed this frame: event pass at ModEnd over EventLanes
        public static readonly Dictionary<Entity, uint> EventLanes = new Dictionary<Entity, uint>();   // lane -> project (event pass)
        public static bool InvalidationOn = true;           // dev switch (rrw.tr.inval); the product switch is RerouteInFlight
        public static bool SentinelMissing;                 // set at ModEnd when a Closed lane could not be written
        public static bool RefreshAllPending;               // re-refresh every closure at the next Mod1 (self-heal)
        public static bool Disabled;                        // a Traffic system faulted: every closure is opened and Traffic stays off
        public static bool PurgePending = true;             // remove stale sentinels on the first game frame
        public static int GatesRevision = -1;               // RRWGates.Revision last seen by the request system

        // Soft = Forbidden (RRWGates.Soft): vanilla CarLane.Forbidden bit per lane entity, captured once before our first
        // write (LaneDataSystem never resets Forbidden). Restored on open, around every save, and when the lane stops
        // being SOFT. ForbiddenEverUsed: some lane got Forbidden from us this session (rrw.check, save guard).
        public static readonly Dictionary<Entity, bool> ForbiddenSnap = new Dictionary<Entity, bool>();
        public static bool ForbiddenEverUsed;

        // Dev commands queue structural work for Mod1 (TrafficRequestSystem runs them first). Dev builds only fill it.
        public static readonly List<Action<EntityManager, uint>> DevOps = new List<Action<EntityManager, uint>>();

        // Opened edges whose lanes are checked for left-over closure values a few updates later.
        public static readonly List<Entity> VerifyEdges = new List<Entity>();

        // Upgrade works: lane drops, switched-off new parking, released markers (TrafficUpgrade.cs), and the lanes / edges refreshed
        // one update after markers were removed (entity -> update due).
        public static readonly Dictionary<Entity, EdgeUpgradeTraffic> Upgrade = new Dictionary<Entity, EdgeUpgradeTraffic>();
        public static readonly Dictionary<Entity, uint> UpgradeRefresh = new Dictionary<Entity, uint>();
        public static int MarkersPlaced, MarkersRemoved, MarkersMisregistered, DropFailures;
        public static uint VerifyDue;
        public static bool VerifyPending;

        // In-flight invalidation statistics.
        public static readonly Dictionary<Entity, int> InvalidatedCount = new Dictionary<Entity, int>();
        public static int InvalidatedTotal, Reinvalidated, Passes, RelocatedTotal, EventPasses;

        // Save guard.
        public static bool SaveNeutralised;
        public static readonly Dictionary<Entity, float> SaveFreeSpace = new Dictionary<Entity, float>();

        public static bool HasClosed => ClosedCount > 0;

        public static void Recount()
        {
            int n = 0;
            foreach (var cl in Closures.Values) if (cl.Level == ClosureLevel.Closed) n++;
            ClosedCount = n;
            Revision++;
        }

        public static bool SentinelValid(EntityManager em) =>
            Sentinel != Entity.Null && em.Exists(Sentinel) && em.HasComponent<RRWAccessZone>(Sentinel);

        // Mod1 / Serialize only (structural change).
        public static Entity EnsureSentinel(EntityManager em)
        {
            if (SentinelValid(em)) return Sentinel;
            var arch = em.CreateArchetype(ComponentType.ReadWrite<RRWAccessZone>());
            var arr = em.CreateEntity(arch, 1, Allocator.Temp);
            Sentinel = arr[0];
            arr.Dispose();
            RRWLog.Verbose("traffic sentinel created " + RRWLog.E(Sentinel) + " (no PrefabRef, unsaved)");
            return Sentinel;
        }

        public static void ResetAll(string why)
        {
            int n = Closures.Count;
            Closures.Clear();
            Snaps.Clear();
            Sentinel = Entity.Null;
            ClosedCount = 0;
            Revision++;
            InvalidationEventPending = false;
            EventLanes.Clear();
            SentinelMissing = false;
            RefreshAllPending = false;
            Disabled = false;
            PurgePending = true;
            GatesRevision = -1;
            ForbiddenSnap.Clear();
            ForbiddenEverUsed = false;
            DevOps.Clear();
            VerifyEdges.Clear();
            VerifyPending = false;
            InvalidatedCount.Clear();
            InvalidatedTotal = 0;
            Reinvalidated = 0;
            Passes = 0;
            EventPasses = 0;
            RelocatedTotal = 0;
            SaveNeutralised = false;
            SaveFreeSpace.Clear();
            int u = Upgrade.Count;
            Upgrade.Clear();
            UpgradeRefresh.Clear();
            MarkersPlaced = MarkersRemoved = MarkersMisregistered = DropFailures = 0;
            UpgradeTraffic.Reset();
            LaneClosureMarkers.ClearSaveState();
            TrafficAnalysis.ClearCaches();
            if (n > 0 || u > 0) RRWLog.Info("traffic state reset (" + why + "), dropped " + n + " closures and " + u + " upgrade edges");
        }

        // Queue the lanes of `cl` that carry a closed state (Soft / ClosedS / ClosedB) and whose group touches `bits`
        // (None = every closed lane) for the next invalidation EVENT pass (lane-keyed).
        public static int QueueEventLanes(EntityManager em, EdgeClosure cl, List<Entity> lanes, RoadZones bits)
        {
            int n = 0;
            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane)) continue;
                var st = cl.StateOf(em, lane);
                if (st == LaneState.Slow || st == LaneState.Vanilla) continue;
                if (bits != RoadZones.None && (cl.GroupOf(em, lane) & bits) == 0) continue;
                EventLanes[lane] = cl.ProjectId;
                n++;
            }
            if (n > 0) InvalidationEventPending = true;
            return n;
        }
    }

    public static class TrafficUtil
    {
        // Reused lane list for main-thread iteration (never held across calls that could re-enter).
        private static readonly List<Entity> s_Lanes = new List<Entity>(32);

        public static bool Alive(EntityManager em, Entity e) => e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e);

        // Copies the edge's sublanes into a reused list (safe against buffer invalidation while we write lanes).
        public static List<Entity> Lanes(EntityManager em, Entity edge)
        {
            s_Lanes.Clear();
            if (!Alive(em, edge) || !em.HasBuffer<NetSubLane>(edge)) return s_Lanes;
            var buf = em.GetBuffer<NetSubLane>(edge, true);
            for (int i = 0; i < buf.Length; i++) s_Lanes.Add(buf[i].m_SubLane);
            return s_Lanes;
        }

        public static void LanesInto(EntityManager em, Entity edge, List<Entity> output)
        {
            output.Clear();
            if (!Alive(em, edge) || !em.HasBuffer<NetSubLane>(edge)) return;
            var buf = em.GetBuffer<NetSubLane>(edge, true);
            for (int i = 0; i < buf.Length; i++) output.Add(buf[i].m_SubLane);
        }

        // Refresh: PathfindUpdated on car, pedestrian and parking lanes; Updated on track-only lanes
        // (LanesModifiedSystem only reads PathfindUpdated for car/parking/pedestrian/connection lanes).
        // MUST run at Modification1: flags added later are stripped by CleanUp before the next modification phases.
        public static int RefreshEdge(EntityManager em, Entity edge, bool carOnly, out string detail)
        {
            int car = 0, ped = 0, park = 0, track = 0;
            var lanes = Lanes(em, edge);
            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                if (!Alive(em, lane) || em.HasComponent<Temp>(lane)) continue;
                bool isCar = em.HasComponent<NetCarLane>(lane);
                bool isPed = em.HasComponent<NetPedestrianLane>(lane);
                bool isPark = em.HasComponent<NetParkingLane>(lane);
                bool isTrack = em.HasComponent<NetTrackLane>(lane);
                if (carOnly && !isCar && !isPark) continue;
                if (isCar || isPed || isPark)
                {
                    if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
                    if (isCar) car++; else if (isPed) ped++; else park++;
                }
                else if (isTrack)
                {
                    if (!em.HasComponent<Updated>(lane)) em.AddComponent<Updated>(lane);
                    track++;
                }
            }
            detail = "car=" + car + " ped=" + ped + " park=" + park + " track=" + track;
            return car + ped + park + track;
        }

        // Refresh a closure's lanes (Mod1): LaneClosure / ParkingClosure re-apply them at ModEnd; ClosureApplied waits for that.
        public static string RefreshClosure(EntityManager em, EdgeClosure cl, bool carOnly, uint upd)
        {
            RefreshEdge(em, cl.Edge, carOnly, out string detail);
            cl.ChangedUpdate = upd;
            cl.LanesConfirmed = false;
            cl.Refreshes++;
            return detail;
        }

        // Drop a closure and give the lanes back to vanilla (Mod1): refresh (LaneDataSystem restores every field at ModEnd),
        // restore the Forbidden bits we set (vanilla never resets them), verify 3 updates later.
        public static void OpenClosure(EntityManager em, Entity edge, bool alive, uint upd, string why)
        {
            if (!TrafficState.Closures.TryGetValue(edge, out var cl)) return;
            TrafficState.Closures.Remove(edge);
            TrafficState.Recount();
            if (!alive)
            {
                RRWLog.Verbose("traffic e" + edge.Index + " closure dropped (" + why + ")");
                return;
            }
            int forb = 0;
            if (TrafficState.ForbiddenSnap.Count > 0)
            {
                var lanes = Lanes(em, edge);
                for (int i = 0; i < lanes.Count; i++) if (TrafficApply.RestoreForbiddenNow(em, lanes[i])) forb++;
            }
            RefreshEdge(em, edge, false, out string detail);
            if (!TrafficState.VerifyEdges.Contains(edge)) TrafficState.VerifyEdges.Add(edge);
            TrafficState.VerifyDue = upd + TrafficConst.kVerifyDelayUpdates;
            TrafficState.VerifyPending = true;
            RRWLog.Verbose("traffic e" + edge.Index + " " + cl.State + "->Open (" + why + ") refreshed " + detail +
                           (forb > 0 ? " forbiddenRestored=" + forb : ""));
        }

        public static string Kmh(float ms) => RRWLog.F(ms * 3.6f);

        public static string Bits(RoadZones z) => RoadZoneMath.Describe(z & RoadZones.AllLanes);
    }

    // Pure field transforms per lane state. Called on freshly reset (vanilla) values.
    //  Slow     car: min(limit, work-zone speed) + caution; pedestrian / track / parking: untouched
    //  Soft     sentinel (RRWGates.Soft = Sentinel, default) or CarLane.Forbidden + caution (Forbidden, experimental; car lanes only,
    //           pedestrian / track lanes use the sentinel); vehicles on it drive out, the drain report waits for them
    //  ClosedS  sentinel, no AllowEnter, caution (+ blockage with the whole-edge HardCloseAfterDrain)
    //  ClosedB  car: blockage 0..255 + caution, NO sentinel (destinations stay reachable from the open lane); track and
    //           pedestrian lanes of such a group (none in practice) use the sentinel
    public static class TrafficApply
    {
        public static bool Car(ref NetCarLane c, LaneState st, EdgeClosure cl, Entity sentinel)
        {
            switch (st)
            {
                case LaneState.Slow:
                    c.m_SpeedLimit = Math.Min(c.m_SpeedLimit, cl.SlowSpeed);
                    if (cl.Caution) { c.m_CautionStart = 0; c.m_CautionEnd = 255; }
                    return true;
                case LaneState.Soft:
                    if (cl.SoftModeApplied == SoftMode.Forbidden)
                    {
                        c.m_Flags |= NetCarLaneFlags.Forbidden;
                        c.m_CautionStart = 0; c.m_CautionEnd = 255;
                        return true;
                    }
                    if (sentinel == Entity.Null) return false;
                    c.m_AccessRestriction = sentinel;
                    c.m_Flags &= ~NetCarLaneFlags.AllowEnter;
                    c.m_CautionStart = 0; c.m_CautionEnd = 255;
                    return true;
                case LaneState.ClosedS:
                    if (sentinel == Entity.Null) return false;
                    c.m_AccessRestriction = sentinel;
                    c.m_Flags &= ~NetCarLaneFlags.AllowEnter;
                    if (cl.Caution) { c.m_CautionStart = 0; c.m_CautionEnd = 255; }
                    if (cl.HardActive) { c.m_BlockageStart = 0; c.m_BlockageEnd = 255; }
                    return true;
                case LaneState.ClosedB:
                    c.m_BlockageStart = 0; c.m_BlockageEnd = 255;
                    c.m_CautionStart = 0; c.m_CautionEnd = 255;
                    return true;
                default:
                    return false;
            }
        }

        public static bool Closes(LaneState st) => st == LaneState.Soft || st == LaneState.ClosedS || st == LaneState.ClosedB;

        public static bool Ped(ref NetPedestrianLane p, LaneState st, Entity sentinel)
        {
            if (!Closes(st) || sentinel == Entity.Null) return false; // open sidewalks / SlowZone: untouched
            p.m_AccessRestriction = sentinel;
            p.m_Flags &= ~(PedestrianLaneFlags.AllowEnter | PedestrianLaneFlags.AllowExit);
            return true;
        }

        public static bool Track(ref NetTrackLane t, LaneState st, Entity sentinel)
        {
            if (!Closes(st) || sentinel == Entity.Null) return false;
            t.m_AccessRestriction = sentinel;
            return true;
        }

        // Rule verified in game: SlowZone never touches parking; Closed disables parking only on building-free edges.
        // Parking groups never open during works (design decision), so on a building-free Closed edge every parking lane stays
        // disabled until the release, open half or not. Edges with buildings keep vanilla parking.
        public static bool Parking(ref NetParkingLane p, bool parkClosed, Entity sentinel)
        {
            if (!parkClosed || sentinel == Entity.Null) return false;
            p.m_Flags |= ParkingLaneFlags.ParkingDisabled;
            p.m_AccessRestriction = sentinel;
            p.m_Flags &= ~(ParkingLaneFlags.AllowEnter | ParkingLaneFlags.AllowExit);
            return true;
        }

        // Parking of this lane is disabled now.
        public static bool LaneParkClosed(EntityManager em, Entity lane, EdgeClosure cl)
        {
            if (cl.IsDev) return Closes(cl.StateOf(em, lane));
            return cl.Level == ClosureLevel.Closed && cl.ParkClosed && !cl.LaneIsOpen(em, lane);
        }

        // An empty new parking lane of upgrade works switched off (and not closed by the closure itself).
        public static bool LaneParkOff(EntityManager em, Entity lane, EdgeClosure cl) =>
            cl.ParkingOff != null && cl.ParkingOff.Contains(lane) && !LaneParkClosed(em, lane, cl);

        // Blockage of ours on some lane of the closure's edge (CLOSED-B / HardClose, or upgrade lane closure markers): the parking
        // free space computed now is not the vanilla one.
        public static bool EdgeBlocked(EdgeClosure cl) => cl.AnyBlockage || UpgradeTraffic.HasMarkers(cl.Edge);

        // Soft with Forbidden on this car lane now.
        private static bool WantsForbidden(LaneState st, EdgeClosure cl) => st == LaneState.Soft && cl.SoftModeApplied == SoftMode.Forbidden;

        // Car / pedestrian / track part (LaneDataSystem resets these). Returns the number of components written.
        public static int ApplyLane(EntityManager em, Entity lane, EdgeClosure cl, Entity sentinel, bool snapshot)
        {
            TrafficState.Snaps.TryGetValue(lane, out var snap);
            int writes = 0;
            bool hasCar = em.HasComponent<NetCarLane>(lane), hasPed = em.HasComponent<NetPedestrianLane>(lane);
            bool hasTrack = em.HasComponent<NetTrackLane>(lane);
            if (!hasCar && !hasPed && !hasTrack) return 0;
            var st = cl.StateOf(em, lane);
            if (hasCar)
            {
                var c = em.GetComponentData<NetCarLane>(lane);
                if (snapshot) { snap.HasCar = true; snap.Car = c; }
                bool wrote = false;
                if (WantsForbidden(st, cl))
                {
                    if (!TrafficState.ForbiddenSnap.ContainsKey(lane))
                    {
                        TrafficState.ForbiddenSnap[lane] = (c.m_Flags & NetCarLaneFlags.Forbidden) != 0;
                        TrafficState.ForbiddenEverUsed = true;
                    }
                }
                else if (TrafficState.ForbiddenSnap.TryGetValue(lane, out bool vanillaForbidden))
                {
                    // No longer SOFT-Forbidden: give the vanilla bit back (LaneDataSystem never resets it).
                    c.m_Flags = vanillaForbidden ? c.m_Flags | NetCarLaneFlags.Forbidden : c.m_Flags & ~NetCarLaneFlags.Forbidden;
                    TrafficState.ForbiddenSnap.Remove(lane);
                    if (snapshot) snap.Car = c;
                    wrote = true;
                }
                if (Car(ref c, st, cl, sentinel)) wrote = true;
                if (wrote) { em.SetComponentData(lane, c); writes++; }
            }
            if (hasPed)
            {
                var p = em.GetComponentData<NetPedestrianLane>(lane);
                if (snapshot) { snap.HasPed = true; snap.Ped = p; }
                if (Ped(ref p, st, sentinel)) { em.SetComponentData(lane, p); writes++; }
            }
            if (hasTrack)
            {
                var t = em.GetComponentData<NetTrackLane>(lane);
                if (snapshot) { snap.HasTrack = true; snap.Track = t; }
                if (Track(ref t, st, sentinel)) { em.SetComponentData(lane, t); writes++; }
            }
            if (snapshot) TrafficState.Snaps[lane] = snap;
            return writes;
        }

        // Parking part (ParkingLaneDataSystem resets these).
        public static int ApplyParking(EntityManager em, Entity lane, EdgeClosure cl, Entity sentinel, bool snapshot)
        {
            if (!em.HasComponent<NetParkingLane>(lane)) return 0;
            bool has = TrafficState.Snaps.TryGetValue(lane, out var snap);
            var p = em.GetComponentData<NetParkingLane>(lane);
            bool parkClosed = LaneParkClosed(em, lane, cl);
            bool parkOff = !parkClosed && LaneParkOff(em, lane, cl);
            if (snapshot)
            {
                bool dirty = false;
                // Free space computed without any blockage of ours (the save guard writes it back under HardClose / CLOSED-B /
                // lane closure markers).
                if (!EdgeBlocked(cl)) { snap.HasFreeSpace = true; snap.FreeSpaceUnblocked = p.m_FreeSpace; dirty = true; }
                if (parkClosed || parkOff) { snap.HasParking = true; snap.Parking = p; dirty = true; }
                else if (has && snap.HasParking)
                {
                    // Parking is vanilla again (SlowZone / building appeared / dev lane opened): never write an old snapshot back.
                    snap.HasParking = false;
                    dirty = true;
                }
                if (dirty) TrafficState.Snaps[lane] = snap;
            }
            if (parkOff)
            {
                // An empty new parking lane: no new car parks there until its band is built (no sentinel: nothing to drain).
                p.m_Flags |= ParkingLaneFlags.ParkingDisabled;
                em.SetComponentData(lane, p);
                return 1;
            }
            if (!Parking(ref p, parkClosed, sentinel)) return 0;
            em.SetComponentData(lane, p);
            return 1;
        }

        // Write the vanilla snapshot back (save guard). Returns components written.
        public static int WriteSnapshot(EntityManager em, Entity lane, EdgeClosure cl)
        {
            if (!TrafficState.Snaps.TryGetValue(lane, out var s)) return 0;
            int n = 0;
            if (s.HasCar && em.HasComponent<NetCarLane>(lane))
            {
                var c = em.GetComponentData<NetCarLane>(lane);
                c.m_AccessRestriction = s.Car.m_AccessRestriction;
                c.m_Flags = (c.m_Flags & ~NetCarLaneFlags.AllowEnter) | (s.Car.m_Flags & NetCarLaneFlags.AllowEnter);
                if (TrafficState.ForbiddenSnap.TryGetValue(lane, out bool vf))
                    c.m_Flags = vf ? c.m_Flags | NetCarLaneFlags.Forbidden : c.m_Flags & ~NetCarLaneFlags.Forbidden;
                c.m_SpeedLimit = s.Car.m_SpeedLimit;
                c.m_BlockageStart = s.Car.m_BlockageStart; c.m_BlockageEnd = s.Car.m_BlockageEnd;
                c.m_CautionStart = s.Car.m_CautionStart; c.m_CautionEnd = s.Car.m_CautionEnd;
                em.SetComponentData(lane, c); n++;
            }
            if (s.HasPed && em.HasComponent<NetPedestrianLane>(lane))
            {
                var p = em.GetComponentData<NetPedestrianLane>(lane);
                p.m_AccessRestriction = s.Ped.m_AccessRestriction;
                var mask = PedestrianLaneFlags.AllowEnter | PedestrianLaneFlags.AllowExit;
                p.m_Flags = (p.m_Flags & ~mask) | (s.Ped.m_Flags & mask);
                em.SetComponentData(lane, p); n++;
            }
            if (s.HasTrack && em.HasComponent<NetTrackLane>(lane))
            {
                var t = em.GetComponentData<NetTrackLane>(lane);
                t.m_AccessRestriction = s.Track.m_AccessRestriction;
                em.SetComponentData(lane, t); n++;
            }
            bool blocked = EdgeBlocked(cl);
            if (em.HasComponent<NetParkingLane>(lane) && (s.HasParking || blocked))
            {
                var p = em.GetComponentData<NetParkingLane>(lane);
                if (s.HasParking)
                {
                    p.m_AccessRestriction = s.Parking.m_AccessRestriction;
                    var mask = ParkingLaneFlags.ParkingDisabled | ParkingLaneFlags.AllowEnter | ParkingLaneFlags.AllowExit;
                    p.m_Flags = (p.m_Flags & ~mask) | (s.Parking.m_Flags & mask);
                }
                if (blocked)
                {
                    // m_FreeSpace is serialized and only recomputed on a lane refresh; a 0 written into a save would
                    // keep the kerb "full" forever without the mod. Write the last unblocked value (curve length if
                    // none) and remember the live value so the restore system can put it back.
                    TrafficState.SaveFreeSpace[lane] = p.m_FreeSpace;
                    p.m_FreeSpace = s.HasFreeSpace ? s.FreeSpaceUnblocked
                        : (em.HasComponent<Curve>(lane) ? em.GetComponentData<Curve>(lane).m_Length : p.m_FreeSpace);
                }
                em.SetComponentData(lane, p); n++;
            }
            return n;
        }

        // Undo the save-time free-space override (see WriteSnapshot).
        public static void RestoreFreeSpace(EntityManager em, Entity lane)
        {
            if (!TrafficState.SaveFreeSpace.TryGetValue(lane, out float live) || !em.HasComponent<NetParkingLane>(lane)) return;
            var p = em.GetComponentData<NetParkingLane>(lane);
            p.m_FreeSpace = live;
            em.SetComponentData(lane, p);
        }

        // Give the vanilla Forbidden bit back right now (open / fail-safe at Mod1; LaneDataSystem keeps it at ModEnd).
        public static bool RestoreForbiddenNow(EntityManager em, Entity lane)
        {
            if (!TrafficState.ForbiddenSnap.TryGetValue(lane, out bool vf)) return false;
            TrafficState.ForbiddenSnap.Remove(lane);
            if (!em.Exists(lane) || !em.HasComponent<NetCarLane>(lane)) return false;
            var c = em.GetComponentData<NetCarLane>(lane);
            c.m_Flags = vf ? c.m_Flags | NetCarLaneFlags.Forbidden : c.m_Flags & ~NetCarLaneFlags.Forbidden;
            em.SetComponentData(lane, c);
            return true;
        }

        // True when any lane field of this lane still references the sentinel (restore verification, rrw.check).
        public static bool ReferencesSentinel(EntityManager em, Entity lane, Entity sentinel)
        {
            if (sentinel == Entity.Null) return false;
            if (em.HasComponent<NetCarLane>(lane) && em.GetComponentData<NetCarLane>(lane).m_AccessRestriction == sentinel) return true;
            if (em.HasComponent<NetPedestrianLane>(lane) && em.GetComponentData<NetPedestrianLane>(lane).m_AccessRestriction == sentinel) return true;
            if (em.HasComponent<NetTrackLane>(lane) && em.GetComponentData<NetTrackLane>(lane).m_AccessRestriction == sentinel) return true;
            if (em.HasComponent<NetParkingLane>(lane) && em.GetComponentData<NetParkingLane>(lane).m_AccessRestriction == sentinel) return true;
            return false;
        }

        public static bool FullBlock(in NetCarLane c) => c.m_BlockageStart == 0 && c.m_BlockageEnd == 255;

        // Does the lane carry the state it must carry? (rrw.tr.probe, rrw.check). sb (optional) receives the field dump.
        public static bool LaneMatches(EntityManager em, Entity lane, EdgeClosure cl, Entity sentinel, System.Text.StringBuilder sb)
        {
            bool ok = true;
            var st = cl != null ? cl.StateOf(em, lane) : LaneState.Vanilla;
            if (em.HasComponent<NetCarLane>(lane))
            {
                var c = em.GetComponentData<NetCarLane>(lane);
                bool restr = sentinel != Entity.Null && c.m_AccessRestriction == sentinel;
                bool enter = (c.m_Flags & NetCarLaneFlags.AllowEnter) != 0;
                bool caution = c.m_CautionStart == 0 && c.m_CautionEnd == 255;
                bool block = FullBlock(c);
                bool forb = (c.m_Flags & NetCarLaneFlags.Forbidden) != 0;
                bool forbOurs = forb && (TrafficState.ForbiddenSnap.TryGetValue(lane, out bool vf) ? !vf : false);
                bool pass;
                switch (st)
                {
                    case LaneState.Slow: pass = !restr && !forbOurs && caution && c.m_SpeedLimit <= cl.SlowSpeed + 0.01f; break;
                    case LaneState.Soft:
                        pass = cl.SoftModeApplied == SoftMode.Forbidden ? !restr && forb && caution : restr && !enter && caution;
                        break;
                    case LaneState.ClosedS: pass = restr && !enter && caution && !forbOurs && (block == cl.HardActive); break;
                    case LaneState.ClosedB: pass = !restr && block && caution && !forbOurs; break;
                    default: pass = !restr && !forbOurs; break;     // vanilla (blockage may come from accidents / vanilla blockers)
                }
                if (!pass) ok = false;
                if (sb != null)
                    sb.Append(" car restr=").Append(restr ? "SENTINEL" : RRWLog.E(c.m_AccessRestriction)).Append(" allowEnter=").Append(enter)
                      .Append(" speed=").Append(TrafficUtil.Kmh(c.m_SpeedLimit)).Append(" caution=").Append(c.m_CautionStart).Append("..").Append(c.m_CautionEnd)
                      .Append(" block=").Append(c.m_BlockageEnd < c.m_BlockageStart ? "none" : c.m_BlockageStart + ".." + c.m_BlockageEnd)
                      .Append(" forbidden=").Append(forb).Append(forbOurs ? "(ours)" : "").Append(pass ? " ok" : " BAD");
            }
            if (em.HasComponent<NetPedestrianLane>(lane))
            {
                var p = em.GetComponentData<NetPedestrianLane>(lane);
                bool restr = sentinel != Entity.Null && p.m_AccessRestriction == sentinel;
                bool pass = Closes(st) ? restr : !restr;
                if (!pass) ok = false;
                if (sb != null) sb.Append(" ped restr=").Append(restr ? "SENTINEL" : RRWLog.E(p.m_AccessRestriction)).Append(pass ? " ok" : " BAD");
            }
            if (em.HasComponent<NetTrackLane>(lane))
            {
                var t = em.GetComponentData<NetTrackLane>(lane);
                bool restr = sentinel != Entity.Null && t.m_AccessRestriction == sentinel;
                bool pass = Closes(st) ? restr : !restr;
                if (!pass) ok = false;
                if (sb != null) sb.Append(" track restr=").Append(restr ? "SENTINEL" : RRWLog.E(t.m_AccessRestriction)).Append(pass ? " ok" : " BAD");
            }
            if (em.HasComponent<NetParkingLane>(lane))
            {
                var p = em.GetComponentData<NetParkingLane>(lane);
                bool restr = sentinel != Entity.Null && p.m_AccessRestriction == sentinel;
                bool disabled = (p.m_Flags & ParkingLaneFlags.ParkingDisabled) != 0;
                bool parkClosed = cl != null && LaneParkClosed(em, lane, cl);
                bool parkOff = cl != null && !parkClosed && LaneParkOff(em, lane, cl);
                bool pass = parkClosed ? restr && disabled : parkOff ? disabled && !restr : !restr;
                if (!pass) ok = false;
                if (sb != null)
                    sb.Append(" parking").Append(parkOff ? "(new, off)" : "").Append(" disabled=").Append(disabled).Append(" restr=").Append(restr ? "SENTINEL" : RRWLog.E(p.m_AccessRestriction))
                      .Append(" free=").Append(RRWLog.F(p.m_FreeSpace)).Append(pass ? " ok" : " BAD");
            }
            return ok;
        }
    }
}
