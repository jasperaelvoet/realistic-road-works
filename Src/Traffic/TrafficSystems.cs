using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.SceneFlow;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Controller = Game.Vehicles.Controller;
using FixParkingLocation = Game.Vehicles.FixParkingLocation;
using Moving = Game.Objects.Moving;
using NetCarLane = Game.Net.CarLane;
using NetLane = Game.Net.Lane;
using NetParkingLane = Game.Net.ParkingLane;
using NetPedestrianLane = Game.Net.PedestrianLane;
using NetTrackLane = Game.Net.TrackLane;
using ParkedCar = Game.Vehicles.ParkedCar;

// Traffic systems (update orders 220, 500, 510, 520, 905, 915). Ported from the traffic prototype,
// keyed on registry edges instead of dev commands.
namespace RealisticRoadWorks.V3.Traffic
{
    internal static class TrafficGate
    {
        public static bool InGame => GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game;
    }

    // ------------------------------------------------------------------------------------------------ Mod1, order 220
    // Every structural change of the module happens here: lane refresh flags, sentinel lifetime, FixParkingLocation.
    // Reconciles closures against RoadWorksRuntime.m_ClosureTarget (Director), writes the Traffic-owned registry
    // fields (ClosureApplied, TrafficDrained, BuildingsWaiting, Dependants, Rerouted/ParkedMoved), classifies
    // construction edges (1 per update), relocates parked cars on Closed building-free edges, verifies restores.
    [RegisterSystem(SystemUpdatePhase.Modification1, Before = typeof(Game.Tools.GenerateAreasSystem), Order = RRWOrder.TrafficRequests)]
    public partial class TrafficRequestSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("traffic requests");
        private EntityQuery m_ZoneQuery;
        private EntityQuery m_PathQuery;
        private readonly List<Entity> m_Remove = new List<Entity>(16);
        private readonly List<Entity> m_Lanes = new List<Entity>(32);
        private readonly List<Entity> m_Recheck = new List<Entity>(8);
        private readonly Queue<Entity> m_ClassifyQueue = new Queue<Entity>();
        private readonly Dictionary<Entity, uint> m_TmpLaneMap = new Dictionary<Entity, uint>();
        private readonly PathScanResult m_TmpScan = new PathScanResult();
        private bool m_FailSafeDone;
        private uint m_LastPrune;
        private int m_VerifyRetries;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ZoneQuery = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<RRWAccessZone>() } });
            m_PathQuery = TrafficPaths.CreateQuery(EntityManager);
            RRWIntrospection.RegisterDumper("Traffic", TrafficIntrospection.Dump);
            RRWIntrospection.RegisterChecker("Traffic", TrafficIntrospection.Check);
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            TrafficState.ResetAll("preload");
            m_Guard.Reset();
            m_ClassifyQueue.Clear();
            m_FailSafeDone = false;
            m_LastPrune = 0;
            m_VerifyRetries = 0;
        }

        protected override void OnUpdate()
        {
            if (!TrafficGate.InGame) return;
            if (TrafficState.PurgePending) PurgeStaleSentinels();
            if (TrafficState.Disabled || m_Guard.Faulted) { FailSafe(); return; }
            if (SiteRegistry.Edges.Count == 0 && TrafficState.Closures.Count == 0 && !TrafficState.VerifyPending && TrafficState.DevOps.Count == 0 &&
                !UpgradeTraffic.Busy) return;
            long t = RRWPerf.Start();
            try
            {
                RRWClock.Update(World);
                Step();
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
                if (m_Guard.Faulted) TrafficState.Disabled = true;
            }
            finally { RRWPerf.Stop(PerfSlot.Traffic, t); }
        }

        private void PurgeStaleSentinels()
        {
            TrafficState.PurgePending = false;
            try
            {
                // Lane closure markers are never saved (LivePath) and the tracking is reset on load: any left now is stale.
                var markers = LaneClosureMarkers.Query(EntityManager);
                if (!markers.IsEmptyIgnoreFilter)
                {
                    int m = markers.CalculateEntityCount();
                    EntityManager.AddComponent<Deleted>(markers);
                    RRWLog.Warn("traffic removed " + m + " stale lane closure marker(s) after load");
                }
            }
            catch (Exception e) { RRWLog.ErrorOnce("traffic marker purge", e); }
            try
            {
                if (m_ZoneQuery.IsEmptyIgnoreFilter) return;
                int n = m_ZoneQuery.CalculateEntityCount();
                EntityManager.DestroyEntity(m_ZoneQuery);
                TrafficState.Sentinel = Entity.Null;
                RRWLog.Info("traffic purged " + n + " stale access sentinel(s) after load");
            }
            catch (Exception e) { RRWLog.ErrorOnce("traffic sentinel purge", e); }
        }

        // A Traffic system failed repeatedly: never leave the city with stuck closures. Open everything once.
        private void FailSafe()
        {
            if (m_FailSafeDone) return;
            m_FailSafeDone = true;
            try
            {
                var em = EntityManager;
                int n = 0;
                var forb = new List<Entity>(TrafficState.ForbiddenSnap.Keys);
                for (int i = 0; i < forb.Count; i++) TrafficApply.RestoreForbiddenNow(em, forb[i]);
                foreach (var cl in TrafficState.Closures.Values)
                    if (TrafficUtil.Alive(em, cl.Edge)) { TrafficUtil.RefreshEdge(em, cl.Edge, false, out _); n++; }
                int markers = UpgradeTraffic.RemoveEverything(em);
                if (markers > 0) RRWLog.Error("traffic disabled: " + markers + " lane closure marker(s) removed, every dropped lane is open again");
                TrafficState.Closures.Clear();
                TrafficState.EventLanes.Clear();
                TrafficState.DevOps.Clear();
                TrafficState.Recount();
                TrafficState.InvalidationEventPending = false;
                foreach (var rec in SiteRegistry.Edges.Values)
                {
                    rec.ClosureApplied = ClosureLevel.Open;
                    rec.TrafficDrained = true;
                    rec.OpenLanesApplied = RoadZones.None;
                    rec.SoftApplied = RoadZones.None;
                    rec.ClosedBApplied = RoadZones.None;
                    rec.ZonesDrained = RoadZones.None;     // ZonesReportUpdate goes stale: machines plan nothing new (fail closed)
                    rec.ZonesTimedOut = RoadZones.None;
                    rec.ZonesParked = RoadZones.None;
                }
                RRWLog.Error("traffic disabled after repeated errors: " + n + " works roads opened to traffic until the next load (see the first error above)");
            }
            catch (Exception e) { RRWLog.ErrorOnce("traffic fail-safe", e); }
        }

        private void Step()
        {
            var em = EntityManager;
            uint upd = RRWClock.UpdateIndex;
            var s = TrafficSettings.Read();

            // -1. Dev commands (rrw.tr.close / open) queue their structural work for Mod1.
            if (TrafficState.DevOps.Count > 0) RunDevOps(em, upd);

            // Experimental switches (rrw.gate): every update recomputes the wanted lane states, so a change is applied with the
            // lane refresh rules within this update. Logged once per revision.
            if (TrafficState.GatesRevision != RRWGates.Revision)
            {
                if (TrafficState.GatesRevision >= 0)
                    RRWLog.Info("traffic gates rev " + RRWGates.Revision + ": closedb=" + RRWGates.ClosedB + " soft=" + RRWGates.Soft +
                                " (CLOSED-B " + (RRWGates.ClosedB ? "applies to drained car groups of visible edges with buildings" : "off: every closed car group is CLOSED-S") + ")");
                TrafficState.GatesRevision = RRWGates.Revision;
            }

            // 0. Self-heal requested by a ModEnd / Serialize system.
            if (TrafficState.SentinelMissing || TrafficState.RefreshAllPending)
            {
                bool all = TrafficState.RefreshAllPending;
                TrafficState.SentinelMissing = false;
                TrafficState.RefreshAllPending = false;
                if (TrafficState.HasClosed) TrafficState.EnsureSentinel(em);
                foreach (var cl in TrafficState.Closures.Values)
                    if ((all || cl.Level == ClosureLevel.Closed) && TrafficUtil.Alive(em, cl.Edge)) TrafficUtil.RefreshClosure(em, cl, false, upd);
                RRWLog.Verbose("traffic self-heal refresh (" + (all ? "all" : "closed") + ") of " + TrafficState.Closures.Count + " closures");
            }

            // 1. Reconcile every registry edge against the Director's closure target (+ open / soft lane groups) and write the
            //    drain report.
            foreach (var kv in SiteRegistry.Edges) ReconcileEdge(em, kv.Key, kv.Value, s, upd);
            LogOpenLanes(upd);
            foreach (var proj in SiteRegistry.Projects.Values) LogProjectZones(proj);

            // 2. Closures whose edge left the registry (completed, cancelled, split, deleted): open / drop. Dev closures
            //    (rrw.tr.close) live on non-works edges and are only dropped when the edge dies.
            m_Remove.Clear();
            foreach (var kv in TrafficState.Closures)
            {
                bool alive = TrafficUtil.Alive(em, kv.Key);
                if (kv.Value.IsDev) { if (!alive) m_Remove.Add(kv.Key); continue; }
                if (!SiteRegistry.Edges.ContainsKey(kv.Key) || !alive) m_Remove.Add(kv.Key);
            }
            for (int i = 0; i < m_Remove.Count; i++)
            {
                var e = m_Remove[i];
                bool alive = TrafficUtil.Alive(em, e);
                // U3: a works road handed back while its project still reports machines on the carriageway (the Director
                // must keep the site until PhasePlan.MachinesHoldRoad clears). Warn once per project.
                if (alive && TrafficState.Closures.TryGetValue(e, out var gone) && !gone.IsDev && SiteRegistry.TryGetProject(gone.ProjectId, out var gp) &&
                    PhasePlan.HasMachines(gp.Mode) && gp.MachinesReportFresh(upd) && gp.MachinesOnCarriageway > 0)
                    RRWLog.Once("traffic-u3-removed-p" + gp.Id, "WARN traffic p" + gp.Id + " U3: site of e" + e.Index + " removed (road opens) while " +
                                gp.MachinesOnCarriageway + " machine(s) are on the carriageway" + MachineText(gp, upd));
                TrafficUtil.OpenClosure(em, e, alive, upd, alive ? "site removed" : "edge deleted");
            }

            // 2b. Upgrade works: lane drops (markers), switched-off new parking, released markers, the detour verdict.
            UpgradeTraffic.Step(em, World, upd, s);

            // 3. Parked-car relocation (one update after ParkingDisabled landed) and the optional HardClose.
            bool scannedApproach = false;
            foreach (var cl in TrafficState.Closures.Values)
            {
                if (cl.IsDev) continue;
                if (cl.RelocatePending && (int)(upd - cl.RelocateDue) >= 0)
                {
                    cl.RelocatePending = false;
                    if (cl.Level == ClosureLevel.Closed && cl.ParkClosed && s.Relocate)
                    {
                        int moved = Relocate(em, cl.Edge);
                        TrafficState.RelocatedTotal += moved;
                        if (moved > 0 && SiteRegistry.TryGetProject(cl.ProjectId, out var proj)) proj.ParkedMoved += moved;
                    }
                }
                if (cl.HardRequested && !cl.HardActive && cl.Level == ClosureLevel.Closed && cl.LanesConfirmed && (int)(upd - cl.HardNextCheck) >= 0)
                {
                    cl.HardNextCheck = upd + TrafficConst.kHardCheckInterval;
                    if (TrafficAnalysis.VehiclesOnCarLanes(em, cl.Edge, cl, out _, out _) != 0 || scannedApproach) continue;
                    scannedApproach = true;
                    int approaching = CountApproaching(em, cl);
                    if (approaching == 0)
                    {
                        cl.HardActive = true;
                        TrafficUtil.RefreshClosure(em, cl, true, upd);
                        RRWLog.Info("traffic e" + cl.Edge.Index + " drained: lanes blocked completely (HardCloseAfterDrain)");
                    }
                }
            }

            // 4. Restore verification of opened edges.
            if (TrafficState.VerifyPending && (int)(upd - TrafficState.VerifyDue) >= 0) Verify(em, upd);

            // 5. Classification, time-sliced: one edge per update.
            ClassifyNext(em, upd);

            // 6. Housekeeping: snapshots of lanes that no longer exist.
            if (upd - m_LastPrune >= TrafficConst.kSnapPruneInterval)
            {
                m_LastPrune = upd;
                PruneSnaps(em);
            }
        }

        private static void RunDevOps(EntityManager em, uint upd)
        {
            var ops = new List<Action<EntityManager, uint>>(TrafficState.DevOps);
            TrafficState.DevOps.Clear();
            for (int i = 0; i < ops.Count; i++)
            {
                try { ops[i](em, upd); }
                catch (Exception e) { RRWLog.ErrorOnce("traffic dev op", e); }
            }
        }

        private void ReconcileEdge(EntityManager em, Entity edge, EdgeRecord rec, in TrafficSettings s, uint upd)
        {
            if (!TrafficUtil.Alive(em, edge) || !em.HasComponent<RoadWorksRuntime>(edge)) return;
            var rt = em.GetComponentData<RoadWorksRuntime>(edge);
            var slot = rec.GetOrCreate<TrafficEdgeSlot>(ModuleSlot.Traffic);
            SiteRegistry.TryGetProject(rec.ProjectId, out var proj);
            ClosureLevel target = s.ClosureLayer ? rt.m_ClosureTarget : ClosureLevel.Open;
            bool construction = rt.m_Kind == WorksKind.Construction;
            // Upgrade works (every HalfWidth project): parking that no window closes stays in use, closing groups drain with the
            // sentinel only (never Forbidden), and cars keep reaching buildings while a car half carries traffic.
            bool upgrade = proj != null && proj.Mode == VisualMode.HalfWidth;
            SoftMode softMode = upgrade ? SoftMode.Sentinel : s.Soft;

            // Buildings along the edge (zoned buildings may spawn along a works road: ZoneSpawnSystem ignores works).
            if (!slot.BuildingsChecked || upd - slot.BuildingsCheckUpdate >= TrafficConst.kBuildingsCheckInterval)
            {
                slot.BuildingsChecked = true;
                slot.BuildingsCheckUpdate = upd;
                slot.Buildings = TrafficAnalysis.BuildingCount(em, edge);
            }
            // U3 rule: a mode-A construction stays Closed for the whole works (machines on the lanes), so buildings wait
            // while the target is Closed, not only while the road is hidden. Open sidewalks (staged opening) let people walk in.
            bool carsReach = upgrade && (rt.m_OpenLanes & RoadZones.Carriageway) != RoadZones.None;
            rec.BuildingsWaiting = target == ClosureLevel.Closed && !carsReach ? slot.Buildings : 0;
            if (construction && target == ClosureLevel.Closed && slot.Buildings > 0 && !slot.BuildingsLogged && !carsReach)
            {
                slot.BuildingsLogged = true;
                RRWLog.Info("traffic e" + edge.Index + " (p" + rec.ProjectId + ", " + rt.m_Phase + "): " + slot.Buildings +
                            " buildings along a closed works road; vehicles wait until the machines have left (sidewalks / one half open " +
                            "early with staged opening), the road is NOT downgraded to a slow zone");
            }

            // Lane groups (direction halves, per-side sidewalks / parking), cached per geometry / chain direction / LHT.
            bool rev = RoadZoneMath.ChainReversed(rt.m_ChainU0, rt.m_ChainU1);
            bool regroup = slot.Groups.SyncFromRecord(em, edge, rec, rev, RRWCity.LeftHandTraffic);
            TrafficUtil.LanesInto(em, edge, m_Lanes);
            // Present groups: recomputed when the groups or the lane set change (lanes are regenerated on edge updates).
            int sig = m_Lanes.Count;
            for (int i = 0; i < m_Lanes.Count; i++) sig = unchecked(sig * 31 + m_Lanes[i].Index * 7 + m_Lanes[i].Version);
            if (regroup || !slot.PresentValid || slot.PresentSig != sig)
            {
                slot.Present = slot.Groups.Present(em, m_Lanes);
                slot.PresentSig = sig;
                slot.PresentValid = true;
            }
            RoadZones present = slot.Present;
            // Which halves can carry traffic on their own (one-way roads: none). Geometry only.
            if (regroup || rec.CarHalvesRevision != rec.GeometryRevision || rec.CarHalvesReversed != rev)
            {
                rec.CarHalves = slot.Groups.CarHalves(em, m_Lanes);
                rec.CarHalvesRevision = rec.GeometryRevision;
                rec.CarHalvesReversed = rev;
            }

            // Classification: full BFS on geometry / chain changes and every kClassifyInterval; the zone part (buildings,
            // stops, track) at least every kClassifyZonesInterval and whenever the building count changes. All works kinds.
            if (!slot.ClassifyQueued)
            {
                bool needFull = !slot.Classified || slot.ClassifiedRevision != rec.GeometryRevision || slot.ClassifiedReversed != rev ||
                                upd - slot.ClassifiedUpdate >= TrafficConst.kClassifyInterval;
                bool needZones = !rec.Classified || rec.ClassifyReversed != rev || slot.ZonesClassifiedBuildings != slot.Buildings ||
                                 upd - slot.ZonesClassifiedUpdate >= TrafficConst.kClassifyZonesInterval;
                if (needFull || needZones)
                {
                    slot.ClassifyQueued = true;
                    m_ClassifyQueue.Enqueue(edge);
                }
            }
            // Dependants (construction only). Only mode-D works (no machines) use them for a SlowZone start closure;
            // a mode-A project ignores them (PhasePlan.Closure, Director).
            rec.Dependants = construction && ((slot.Classified && slot.ClassifyDependants) || slot.Buildings > 0);
            slot.Seen = true;

            // U3 safety log: the design rule says a mode-A construction never carries traffic mid-works and the road opens only
            // after the machines have left. Traffic applies the Director's target as it is (the FailSafe and the dev Closure
            // layer are explicit overrides), but says so once per project when a target breaks the rule.
            if (proj != null && s.ClosureLayer) CheckU3(proj, rt, target, edge, upd);

            // Closure level.
            TrafficState.Closures.TryGetValue(edge, out var cl);
            if (cl != null && cl.IsDev)
            {
                // A dev experiment (rrw.tr.close) on an edge that became a works road: vanilla first, the works closure (if any)
                // re-applies in this same update.
                TrafficUtil.OpenClosure(em, edge, true, upd, "dev closure replaced by works of p" + rec.ProjectId);
                RRWLog.Info("traffic e" + edge.Index + " dev closure dropped: the edge is now a works road of p" + rec.ProjectId);
                cl = null;
            }
            if (target == ClosureLevel.Open)
            {
                if (cl != null)
                {
                    TrafficUtil.OpenClosure(em, edge, true, upd, "target Open (" + rt.m_Phase + ")");
                    LogProject(proj, ClosureLevel.Open, edge, null, upd);
                    cl = null;
                }
                rec.ClosureApplied = ClosureLevel.Open;
                rec.OpenLanesApplied = RoadZones.None;
                rec.SoftApplied = RoadZones.None;
                rec.ClosedBApplied = RoadZones.None;
            }
            else
            {
                bool closed = target == ClosureLevel.Closed;
                bool parkClosed = closed && slot.Buildings == 0;
                bool hardWanted = closed && s.HardClose;
                // SOFT and OPEN never overlap (closing wins: fail closed); parking never opens during works (design decision),
                // except on upgrade works, where parking that no window closes stays in use.
                RoadZones soft = closed ? rt.m_SoftZones & RoadZones.AllLanes : RoadZones.None;
                RoadZones open = closed ? rt.m_OpenLanes & RoadZones.AllLanes & ~ParkingKeptClosed(upgrade) : RoadZones.None;
                if ((soft & open) != RoadZones.None)
                {
                    var ps0 = proj?.GetOrCreate<TrafficProjectSlot>(ModuleSlot.Traffic);
                    if (ps0 != null && !ps0.OverlapWarned)
                    {
                        ps0.OverlapWarned = true;
                        RRWLog.Warn("traffic p" + rec.ProjectId + " e" + edge.Index + ": group(s) " + TrafficUtil.Bits(soft & open) +
                                    " are both open and soft (Director bug); Traffic drains them (soft wins)");
                    }
                    open &= ~soft;
                }
                bool isNew = cl == null;
                ClosureLevel prev = isNew ? ClosureLevel.Open : cl.Level;
                bool prevPark = !isNew && cl.ParkClosed;
                RoadZones prevOpen = isNew ? RoadZones.None : cl.Open;
                RoadZones prevSoft = isNew ? RoadZones.None : cl.Soft;
                RoadZones prevB = isNew ? RoadZones.None : cl.ClosedB;
                if (isNew)
                {
                    cl = new EdgeClosure { Edge = edge, Level = target, SoftModeApplied = softMode };
                    TrafficState.Closures[edge] = cl;
                }
                cl.ProjectId = rec.ProjectId;
                cl.Groups = slot.Groups;
                cl.ParkingOff = TrafficState.Upgrade.TryGetValue(edge, out var ut) ? ut.ParkingOff : null;

                // CLOSED-B (RRWGates.ClosedB): car groups of a visible edge with buildings, only once drained under the sentinel
                // and empty of stopped vehicles too (a stopped vehicle on a fully blocked lane can never get a path again).
                // Sticky while the group stays closed; dropped on a regroup (the groups are re-drained first).
                // Integration: CLOSED-B only where it keeps houses reachable from an OPEN car group of the same
                // carriageway (staged C4 works half) or in demolition D0; a fully closed visible carriageway in C3 stays CLOSED-S.
                bool bScope = BScope(rt.m_Kind, open);
                bool bEligible = closed && s.ClosedB && !rec.HiddenApplied && slot.Buildings > 0 && !regroup && bScope;
                RoadZones closedB = bEligible
                    ? (prevB | (slot.Drained & slot.ClearB)) & present & RoadZones.Carriageway & ~open & ~soft
                    : RoadZones.None;

                bool regroupApplied = !isNew && regroup && prev == ClosureLevel.Closed;
                RoadZones narrowed = prevOpen & ~open;
                RoadZones softAdded = soft & ~prevSoft;
                bool modeChange = !isNew && soft != RoadZones.None && cl.SoftModeApplied != softMode;
                bool narrowing = !isNew && prev == ClosureLevel.Closed && closed && ((narrowed | softAdded) != RoadZones.None || modeChange);
                bool change = isNew || prev != target || prevPark != parkClosed || prevOpen != open || prevSoft != soft || regroupApplied ||
                              modeChange || ((target == ClosureLevel.SlowZone || (open & RoadZones.Carriageway) != 0) && Math.Abs(cl.SlowSpeed - s.SlowSpeed) > 0.01f);
                bool hardChange = false;
                if (!hardWanted || prev != target || narrowing)
                {
                    // A narrowing (an open group closes again) follows the Open -> Closed rules: no blockage while Moving
                    // vehicles are on the newly closed lanes; the drain test runs again.
                    if (cl.HardActive) hardChange = true;
                    cl.HardActive = false;
                    cl.HardRequested = false;
                }
                if (hardWanted && !cl.HardRequested)
                {
                    cl.HardRequested = true;
                    cl.HardNextCheck = upd + TrafficConst.kHardCheckInterval;
                }
                if (!isNew && closed && prev == ClosureLevel.Closed)
                    FlipCheck(slot, rec, edge, (prevOpen ^ open) | (prevSoft ^ soft), upd);

                if (change)
                {
                    cl.Level = target;
                    cl.ParkClosed = parkClosed;
                    cl.SlowSpeed = s.SlowSpeed;
                    cl.Caution = true;
                    cl.Open = open;
                    cl.Soft = soft;
                    cl.SoftModeApplied = softMode;
                    cl.ClosedB = closedB;
                    if (closed) TrafficState.EnsureSentinel(em);
                    string detail = TrafficUtil.RefreshClosure(em, cl, false, upd);
                    TrafficUtil.LanesInto(em, edge, m_Lanes);
                    cl.OpenPresent = cl.ComputeOpenPresent(em, m_Lanes);
                    TrafficState.Recount();     // Revision++: the sweep lane map drops / adds the lanes of open groups
                    // Refresh rules: a new Closed edge or a narrowing (a group leaves m_OpenLanes or enters m_SoftZones)
                    // obsoletes the in-flight paths over exactly those lanes at ModEnd (after LanesModifiedSystem). Openings
                    // and SOFT -> closed need no event.
                    int ev = 0;
                    if (closed && prev != ClosureLevel.Closed) ev = TrafficState.QueueEventLanes(em, cl, m_Lanes, RoadZones.None);
                    else if (narrowing)
                        ev = TrafficState.QueueEventLanes(em, cl, m_Lanes, narrowed | softAdded | (modeChange ? soft : RoadZones.None));
                    if (closed && parkClosed && s.Relocate && (prev != ClosureLevel.Closed || !prevPark || narrowing))
                    {
                        cl.RelocatePending = true;
                        cl.RelocateDue = upd + 1;
                    }
                    if (!parkClosed) cl.RelocatePending = false;
                    RRWLog.Verbose("traffic e" + edge.Index + " (p" + rec.ProjectId + ") " + prev + (prevOpen != RoadZones.None ? "+open(" + TrafficUtil.Bits(prevOpen) + ")" : "") +
                                   (prevSoft != RoadZones.None ? "+soft(" + TrafficUtil.Bits(prevSoft) + ")" : "") +
                                   "->" + cl.State + " parking=" + (parkClosed ? "disabled" : "untouched") + " buildings=" + slot.Buildings +
                                   (target == ClosureLevel.SlowZone || (open & RoadZones.Carriageway) != 0 ? " speed=" + TrafficUtil.Kmh(cl.SlowSpeed) + "km/h" : "") +
                                   (cl.Open != RoadZones.None ? " openPresent=" + TrafficUtil.Bits(cl.OpenPresent) : "") +
                                   (narrowing ? " narrowed=" + TrafficUtil.Bits(narrowed | softAdded) : "") + (regroupApplied ? " regrouped" : "") +
                                   " eventLanes=" + ev + " refreshed " + detail);
                    if (prev != target || isNew) LogProject(proj, target, edge, detail, upd);
                }
                else if (closedB != prevB)
                {
                    // CLOSED-S <-> CLOSED-B: refresh only (car + parking lanes), no invalidation event.
                    cl.ClosedB = closedB;
                    TrafficUtil.RefreshClosure(em, cl, true, upd);
                    RRWLog.Info("traffic p" + rec.ProjectId + " e" + edge.Index + " CLOSED-B " + TrafficUtil.Bits(prevB) + " -> " + TrafficUtil.Bits(closedB) +
                                " (buildings=" + slot.Buildings + ", open=" + TrafficUtil.Bits(cl.Open) + ", hidden=" + rec.HiddenApplied + ", gate closedb=" + s.ClosedB + ")");
                }
                else if (hardChange)
                {
                    TrafficUtil.RefreshClosure(em, cl, true, upd);
                }

                // ClosureApplied / OpenLanesApplied / SoftApplied / ClosedBApplied: once LaneClosure wrote the lanes (or after a
                // short fallback for an edge without lanes). While a change is pending, report only what holds before AND after.
                if (cl.LanesConfirmed || upd - cl.ChangedUpdate >= TrafficConst.kConfirmFallbackUpdates)
                {
                    rec.ClosureApplied = cl.Level;
                    rec.OpenLanesApplied = cl.Level == ClosureLevel.Closed ? cl.OpenPresent : RoadZones.None;
                    rec.SoftApplied = cl.Level == ClosureLevel.Closed ? cl.Soft & present : RoadZones.None;
                    rec.ClosedBApplied = cl.Level == ClosureLevel.Closed ? cl.ClosedB & present : RoadZones.None;
                }
                else
                {
                    if (isNew) rec.ClosureApplied = ClosureLevel.Open;
                    bool c2 = !isNew && cl.Level == ClosureLevel.Closed;
                    rec.OpenLanesApplied = c2 ? rec.OpenLanesApplied & cl.OpenPresent : RoadZones.None;
                    rec.SoftApplied = c2 ? rec.SoftApplied & cl.Soft : RoadZones.None;
                    rec.ClosedBApplied = c2 ? rec.ClosedBApplied & cl.ClosedB : RoadZones.None;
                }
            }

            // Drain report: every update, every registry edge.
            UpdateDrain(em, edge, rec, slot, cl, present, regroup, proj, s, upd, cl != null && BScope(rt.m_Kind, cl.Open));
            rec.ZonesPresent = present;
            rec.ZonesReportUpdate = upd;

            // Demolition mobilisation gate: drained = no Moving vehicle on the (closed) car lanes.
            if (proj != null && proj.ClearingTraffic)
            {
                if (target != ClosureLevel.Closed)
                {
                    rec.TrafficDrained = true;      // nothing to wait for: traffic keeps using the road
                    slot.DrainValid = false;
                }
                else if (rec.ClosureApplied != ClosureLevel.Closed)
                {
                    rec.TrafficDrained = false;     // vehicles may still enter until the lanes carry the closure
                    slot.DrainValid = false;
                }
                else if (!slot.DrainValid || upd - slot.DrainCheckUpdate >= TrafficConst.kDrainCheckInterval)
                {
                    slot.DrainValid = true;
                    slot.DrainCheckUpdate = upd;
                    TrafficAnalysis.VehiclesOnCarLanes(em, edge, cl, out int moving, out _);
                    slot.LastMoving = moving;
                    bool was = rec.TrafficDrained;
                    rec.TrafficDrained = moving == 0;
                    if (rec.TrafficDrained && !was) RRWLog.Verbose("traffic e" + edge.Index + " (p" + rec.ProjectId + ") drained");
                }
            }
            else slot.DrainValid = false;
        }

        // Where CLOSED-B may replace CLOSED-S: beside an open car group of the same
        // carriageway (staged C4 works half: houses reached from the open lane, rrw.check P4) or in a demolition (D0). A visible
        // carriageway closed on both halves in a construction (C3, C4 without a car half) keeps the sentinel.
        private static bool BScope(WorksKind kind, RoadZones open) =>
            kind == WorksKind.Demolition || (open & RoadZones.Carriageway) != RoadZones.None;

        // Lane groups that never open while a road is Closed: parking (design decision), except on upgrade works (the Director
        // closes exactly the parking a window works on).
        public static RoadZones ParkingKeptClosed(bool upgrade) => upgrade ? RoadZones.None : RoadZones.Parking;

        // Drain report of one edge. A closed or SOFT group is DRAINED when its lanes carry the applied state and two
        // LaneObject scans kDrainCheckUpdates apart found no Moving object on them, or kDrainTimeoutFrames (sim frames) passed
        // since it started draining (Warn once per project and group). It stays drained while it stays closed (SOFT -> CLOSED-S
        // -> CLOSED-B keep it); it is re-armed when it opens and closes again, or when the lane groups are recomputed.
        private void UpdateDrain(EntityManager em, Entity edge, EdgeRecord rec, TrafficEdgeSlot slot, EdgeClosure cl, RoadZones present,
                                 bool regroup, ProjectRecord proj, in TrafficSettings s, uint upd, bool bScope)
        {
            if (cl == null || cl.IsDev || cl.Level != ClosureLevel.Closed)
            {
                slot.Armed = slot.Drained = slot.ClearB = slot.TimedOut = slot.Parked = RoadZones.None;
                slot.PendingBSince = 0;
                rec.ZonesDrained = RoadZones.None;
                rec.ZonesTimedOut = RoadZones.None;
                rec.ZonesParked = RoadZones.None;
                return;
            }
            uint sim = RRWClock.SimFrame;
            RoadZones closedBits = present & ~cl.Open & RoadZones.AllLanes;
            if (regroup && slot.Drained != RoadZones.None)
            {
                // The groups were recomputed: lanes may have moved between groups. Drain them again (fails closed).
                RoadZones redo = slot.Drained;
                slot.Drained = RoadZones.None;
                slot.ClearB = RoadZones.None;
                slot.TimedOut = RoadZones.None;
                for (int i = 0; i < TrafficBits.Count; i++)
                    if ((redo & TrafficBits.Bits[i]) != 0) { slot.Armed |= TrafficBits.Bits[i]; slot.Clean[i] = 0; slot.ArmSim[i] = sim; }
            }
            RoadZones newly = closedBits & ~(slot.Armed | slot.Drained);
            for (int i = 0; i < TrafficBits.Count; i++)
                if ((newly & TrafficBits.Bits[i]) != 0) { slot.Armed |= TrafficBits.Bits[i]; slot.Clean[i] = 0; slot.ArmSim[i] = sim; }
            slot.Armed &= closedBits;
            slot.Drained &= closedBits;
            slot.ClearB &= closedBits;
            slot.TimedOut &= closedBits & slot.Drained;
            slot.Parked &= closedBits;
            // closed parking groups of a visible edge are watched for parked cars (edges with buildings keep vanilla parking)
            RoadZones parkWatch = rec.HiddenApplied ? RoadZones.None : closedBits & RoadZones.Parking;

            bool confirmed = cl.LanesConfirmed || upd - cl.ChangedUpdate >= TrafficConst.kConfirmFallbackUpdates;
            bool bWanted = s.ClosedB && !rec.HiddenApplied && slot.Buildings > 0 && bScope;
            RoadZones pendingB = bWanted ? slot.Drained & RoadZones.Carriageway & ~cl.ClosedB & ~cl.Soft : RoadZones.None;
            if (pendingB != RoadZones.None) { if (slot.PendingBSince == 0) slot.PendingBSince = upd; }
            else slot.PendingBSince = 0;

            RoadZones newlyDrained = RoadZones.None;
            if (confirmed && (slot.Armed | pendingB | slot.TimedOut | parkWatch) != RoadZones.None && (int)(upd - slot.NextDrainScan) >= 0)
            {
                slot.NextDrainScan = upd + (uint)RRWConst.kDrainCheckUpdates;
                slot.LastScanUpdate = upd;
                ScanGroups(em, cl, m_Lanes, out RoadZones busy, out RoadZones occupied, out RoadZones parked);
                slot.LastBusy = busy;
                slot.LastOccupied = occupied;
                slot.Parked = parked & parkWatch;
                for (int i = 0; i < TrafficBits.Count; i++)
                {
                    var bit = TrafficBits.Bits[i];
                    if ((slot.TimedOut & bit) != 0)
                    {
                        // A group reported drained by the timeout is re-scanned; it becomes ready for machines
                        // only once it is really clean (kDrainCleanScans), or at kDrainReadyCapFrames (below)
                        if ((busy & bit) != 0) slot.Clean[i] = 0;
                        else if (++slot.Clean[i] >= TrafficConst.kDrainCleanScans)
                        {
                            slot.TimedOut &= ~bit;
                            RRWLog.Verbose("traffic p" + rec.ProjectId + " e" + edge.Index + " timed-out group " + TrafficUtil.Bits(bit) + " now clean");
                        }
                        continue;
                    }
                    if ((slot.Armed & bit) == 0) continue;
                    if ((busy & bit) != 0) { slot.Clean[i] = 0; continue; }
                    if (++slot.Clean[i] >= TrafficConst.kDrainCleanScans)
                    {
                        slot.Drained |= bit;
                        slot.Armed &= ~bit;
                        newlyDrained |= bit;
                    }
                }
                slot.ClearB = RoadZones.Carriageway & ~occupied & slot.Drained;
            }
            // Timeout (game time: a paused game never times out).
            for (int i = 0; i < TrafficBits.Count; i++)
            {
                var bit = TrafficBits.Bits[i];
                if ((slot.Armed & bit) == 0 || unchecked((int)(sim - slot.ArmSim[i])) < RRWConst.kDrainTimeoutFrames) continue;
                slot.Armed &= ~bit;
                slot.Drained |= bit;
                slot.TimedOut |= bit;
                slot.Clean[i] = 0;
                newlyDrained |= bit;
                var ps = proj?.GetOrCreate<TrafficProjectSlot>(ModuleSlot.Traffic);
                if (ps == null || (ps.TimeoutWarned & bit) == 0)
                {
                    if (ps != null) ps.TimeoutWarned |= bit;
                    RRWLog.Warn("traffic p" + rec.ProjectId + " e" + edge.Index + " group " + TrafficUtil.Bits(bit) + " drain timed out after " +
                                RRWConst.kDrainTimeoutFrames + " sim frames (" + ((cl.Soft & bit) != 0 ? "soft" : "closed") + "; moving objects at the last scan in " +
                                TrafficUtil.Bits(slot.LastBusy) + "); reported drained anyway");
                }
            }
            // A timed-out group that never scans clean is released to the machines at kDrainReadyCapFrames
            for (int i = 0; i < TrafficBits.Count; i++)
            {
                var bit = TrafficBits.Bits[i];
                if ((slot.TimedOut & bit) == 0 || unchecked((int)(sim - slot.ArmSim[i])) < RRWConst.kDrainReadyCapFrames) continue;
                slot.TimedOut &= ~bit;
                RRWLog.Info("traffic p" + rec.ProjectId + " e" + edge.Index + " group " + TrafficUtil.Bits(bit) + " still had traffic " + RRWConst.kDrainReadyCapFrames +
                            " sim frames after it began draining (busy at the last scan: " + TrafficUtil.Bits(slot.LastBusy) + "): released to the machines anyway");
            }
            rec.ZonesDrained = slot.Drained & closedBits;
            rec.ZonesTimedOut = slot.TimedOut & rec.ZonesDrained;
            rec.ZonesParked = rec.HiddenApplied ? RoadZones.None : slot.Parked & closedBits;   // visible road only
            if (newlyDrained != RoadZones.None)
                RRWLog.Verbose("traffic p" + rec.ProjectId + " e" + edge.Index + " drained " + TrafficUtil.Bits(newlyDrained) +
                               " (drained now " + TrafficUtil.Bits(rec.ZonesDrained) + ", armed " + TrafficUtil.Bits(slot.Armed) + ", soft " + TrafficUtil.Bits(cl.Soft) + ")");
        }

        // Groups (bits) whose closed lanes hold a Moving object or a stopped non-parked vehicle on a car lane (busy = occupied
        // today; kept apart for the dev dump). Lanes that carry traffic on purpose (open groups) are skipped.
        private static void ScanGroups(EntityManager em, EdgeClosure cl, List<Entity> lanes, out RoadZones busy, out RoadZones occupied, out RoadZones parked)
        {
            busy = RoadZones.None;
            occupied = RoadZones.None;
            parked = RoadZones.None;
            for (int l = 0; l < lanes.Count; l++)
            {
                var lane = lanes[l];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane) || !em.HasBuffer<LaneObject>(lane)) continue;
                if (cl.LaneIsOpen(em, lane)) continue;
                var g = cl.GroupOf(em, lane) & RoadZones.AllLanes;
                if (g == RoadZones.None) continue;
                bool car = em.HasComponent<NetCarLane>(lane);
                var buf = em.GetBuffer<LaneObject>(lane, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var o = buf[i].m_LaneObject;
                    if (!em.Exists(o) || em.HasComponent<TrafficLaneBlocker>(o)) continue;    // our lane closure markers never leave
                    // Parked cars do not keep a group busy (they never leave on their own), but a parking strip
                    // that holds them is reported (EdgeRecord.ZonesParked) so machines never plan into it
                    if (em.HasComponent<ParkedCar>(o)) { parked |= g & RoadZones.Parking; continue; }
                    // Moving objects keep the group busy; on car lanes a stopped (non-parked) vehicle does too (a bus at a stop,
                    // a service vehicle at work: machines must not plan into it; the drain timeout bounds a vehicle that never leaves).
                    if (em.HasComponent<Moving>(o)) { busy |= g; occupied |= g; }
                    else if (car) { busy |= g; occupied |= g; }
                }
            }
        }

        // P5 (Director anti-churn): Traffic applies every flip, but warns when a group flips again faster than kStageMinUpdates
        // on this edge (at most once per kStageMinUpdates per group).
        private static void FlipCheck(TrafficEdgeSlot slot, EdgeRecord rec, Entity edge, RoadZones flipped, uint upd)
        {
            if (flipped == RoadZones.None) return;
            for (int i = 0; i < TrafficBits.Count; i++)
            {
                if ((flipped & TrafficBits.Bits[i]) == 0) continue;
                uint last = slot.LastFlip[i];
                if (last != 0 && upd - last < (uint)RRWConst.kStageMinUpdates && upd - slot.LastFlipWarn[i] >= (uint)RRWConst.kStageMinUpdates)
                {
                    slot.LastFlipWarn[i] = upd;
                    RRWLog.Warn("traffic p" + rec.ProjectId + " group " + TrafficUtil.Bits(TrafficBits.Bits[i]) + " flipped after " + (upd - last) +
                                " updates on e" + edge.Index + " (P5: at most one flip per " + RRWConst.kStageMinUpdates + " updates; Director bug)");
                }
                slot.LastFlip[i] = upd;
            }
        }

        // Info once per project whenever the applied SOFT / CLOSED-B / drained groups change (union over its edges).
        private static void LogProjectZones(ProjectRecord proj)
        {
            if (proj == null || proj.Edges.Count == 0) return;
            RoadZones soft = RoadZones.None, b = RoadZones.None, drained = RoadZones.AllLanes;
            bool any = false;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var r)) continue;
                soft |= r.SoftApplied;
                b |= r.ClosedBApplied;
                drained &= r.ZonesDrained | (RoadZones.AllLanes & ~r.ZonesPresent);
                any = true;
            }
            if (!any) return;
            drained &= RoadZones.AllLanes;
            var ps = proj.GetOrCreate<TrafficProjectSlot>(ModuleSlot.Traffic);
            if (soft == ps.LoggedSoft && b == ps.LoggedClosedB && drained == ps.LoggedDrained) return;
            RRWLog.Info("traffic p" + proj.Id + " zones: soft=" + TrafficUtil.Bits(soft) + " closedB=" + TrafficUtil.Bits(b) +
                        " drainedOnEveryEdge=" + TrafficUtil.Bits(drained) + " (was soft=" + TrafficUtil.Bits(ps.LoggedSoft) + " closedB=" +
                        TrafficUtil.Bits(ps.LoggedClosedB) + " drained=" + TrafficUtil.Bits(ps.LoggedDrained) + ", phase " + proj.Phase + ")");
            ps.LoggedSoft = soft;
            ps.LoggedClosedB = b;
            ps.LoggedDrained = drained;
        }

        // U3 rule check, logged once per project and reason (Warn). Rules:
        //  * a mode-A (FullDig) project is Closed for the whole works (no SlowZone / Open before Complete);
        //  * while it releases the road, it stays Closed until a fresh Machines report shows the carriageway clear
        //    (PhasePlan.MachinesHoldRoad; after kCompletionMachineWaitSimFrames the Director opens anyway and warns itself);
        //  * a fresh report with machines on the carriageway and a target below Closed is always wrong (before the cap).
        //  * a mode H (upgrade) project: a fresh report with a machine in a lane group that carries traffic (dropped lanes with
        //    markers and switched-off new parking excepted).
        private void CheckU3(ProjectRecord proj, in RoadWorksRuntime rt, ClosureLevel target, Entity edge, uint upd)
        {
            if (proj.Mode != VisualMode.HalfWidth && (target == ClosureLevel.Closed || proj.Mode != VisualMode.FullDig)) return;
            var ps = proj.GetOrCreate<TrafficProjectSlot>(ModuleSlot.Traffic);
            if (ps.U3Warned) return;
            string why = U3Violation(EntityManager, proj, rt.m_Phase, target, upd);
            if (why == null) return;
            ps.U3Warned = true;
            RRWLog.Warn("traffic p" + proj.Id + " " + proj.Kind + " U3: target " + target + " on e" + edge.Index + " in " + rt.m_Phase + " but " + why +
                        " (Traffic applies the Director's target; fix the Director closure)");
        }

        // Null when the target is allowed; otherwise the broken rule. Shared with rrw.check (TrafficIntrospection).
        public static string U3Violation(EntityManager em, ProjectRecord proj, WorksPhase phase, ClosureLevel target, uint upd)
        {
            if (proj.Mode == VisualMode.HalfWidth) return UpgradeTraffic.MachinesInOpenLanes(em, proj, target, upd);
            if (target == ClosureLevel.Closed || proj.Mode != VisualMode.FullDig) return null;
            if (phase != WorksPhase.Complete && !proj.Releasing) return "a mode-A project must stay Closed for the whole works";
            // Director safety cap (MachinesHoldRoad gives up at ProjectRecord.ReleaseCapped; it warns itself).
            if (proj.ReleaseCapped(upd)) return null;
            if (proj.MachinesReportFresh(upd) && proj.MachinesOnCarriageway > 0)
                return proj.MachinesOnCarriageway + " machine(s) are on / bound for the carriageway (zones " + proj.MachineZones +
                       ", report age " + unchecked(upd - proj.MachinesReportUpdate) + (proj.Releasing ? "" : ", ReleaseSince not set") + ")";
            return null;
        }

        // Info once per project and level change (per-edge detail only under Verbose).
        private static void LogProject(ProjectRecord proj, ClosureLevel level, Entity edge, string detail, uint upd)
        {
            if (proj == null) return;
            var ps = proj.GetOrCreate<TrafficProjectSlot>(ModuleSlot.Traffic);
            if (ps.LoggedAny && ps.LoggedLevel == level) return;
            string from = ps.LoggedAny ? ps.LoggedLevel.ToString() : "Open";
            ps.LoggedAny = true;
            ps.LoggedLevel = level;
            RRWLog.Info("traffic p" + proj.Id + " " + proj.Kind + " mode=" + proj.Mode + " phase=" + proj.Phase + " closure " + from + "->" + level +
                        " (" + proj.Edges.Count + " edges, first e" + edge.Index + (detail != null ? " " + detail : "") + ")" + MachineText(proj, upd));
        }

        // " machinesOnCarriageway=N zones=Z reportAge=A releasing=R" for the U3 / staged-opening verification logs. With several
        // crews the report is ONE union over every crew of the project (Machines), so closures, the release gate and
        // the open-lane checks stay project-level; " crews=n/spawned=k" shows which layout the union covered. Road
        // rollers are managed GameObjects, not entities, but Machines counts them in the same report (MachineZones,
        // MachinesOnCarriageway, MachineCount), so Traffic needs no change; " rollers=k" shows how many of the counted machines are
        // rollers (ProjectRecord.RollersSpawned, same report stamp).
        public static string MachineText(ProjectRecord proj, uint upd)
        {
            string age = proj.MachinesReportUpdate == 0 ? "never" : unchecked(upd - proj.MachinesReportUpdate).ToString();
            return " machinesOnCarriageway=" + proj.MachinesOnCarriageway + " machineZones=" + proj.MachineZones + " reportAge=" + age +
                   (proj.Releasing ? " releasingFor=" + unchecked(upd - proj.ReleaseSince) : "") +
                   (proj.Crews > 1 || proj.CrewsSpawned > 1 ? " crews=" + proj.Crews + "/spawned=" + proj.CrewsSpawned : "") +
                   (proj.RollersSpawned > 0 ? " rollers=" + proj.RollersSpawned : "");
        }

        // Info once per project whenever the set of lane groups that really carry traffic changes.
        private static void LogOpenLanes(uint upd)
        {
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                var ps = proj.Get<TrafficProjectSlot>(ModuleSlot.Traffic);
                if (ps == null) continue;
                RoadZones union = RoadZones.None;
                int edges = 0;
                for (int i = 0; i < proj.Edges.Count; i++)
                    if (SiteRegistry.TryGetEdge(proj.Edges[i], out var r) && r.OpenLanesApplied != RoadZones.None) { union |= r.OpenLanesApplied; edges++; }
                if (union == ps.LoggedOpen) continue;
                RRWLog.Info("traffic p" + proj.Id + " staged opening: " + (union == RoadZones.None ? "no lane group open" : union + " carry traffic on " + edges + "/" + proj.Edges.Count + " edges") +
                            " (was " + ps.LoggedOpen + ", phase " + proj.Phase + ", decided " + proj.OpenLanes + ")" + MachineText(proj, upd));
                ps.LoggedOpen = union;
            }
        }

        // Restore check: no lane of an opened edge may still reference the sentinel.
        // The sentinel is destroyed only when no closure is left AND the check found no reference.
        private void Verify(EntityManager em, uint upd)
        {
            TrafficState.VerifyPending = false;
            var sentinel = TrafficState.Sentinel;
            int lanes = 0, refs = 0;
            m_Recheck.Clear();
            for (int i = 0; i < TrafficState.VerifyEdges.Count; i++)
            {
                var edge = TrafficState.VerifyEdges[i];
                if (TrafficState.Closures.ContainsKey(edge) || !TrafficUtil.Alive(em, edge)) continue;   // re-closed meanwhile / gone
                TrafficUtil.LanesInto(em, edge, m_Lanes);
                bool bad = false;
                for (int k = 0; k < m_Lanes.Count; k++)
                {
                    var lane = m_Lanes[k];
                    if (!TrafficUtil.Alive(em, lane)) continue;
                    lanes++;
                    if (TrafficApply.ReferencesSentinel(em, lane, sentinel)) { refs++; bad = true; }
                    else TrafficState.Snaps.Remove(lane);
                }
                if (bad) m_Recheck.Add(edge);
            }
            TrafficState.VerifyEdges.Clear();
            if (refs > 0)
            {
                // Self-heal: refresh again and re-check (vanilla had not reset those lanes yet).
                for (int i = 0; i < m_Recheck.Count; i++)
                {
                    TrafficUtil.RefreshEdge(em, m_Recheck[i], false, out _);
                    TrafficState.VerifyEdges.Add(m_Recheck[i]);
                }
                if (++m_VerifyRetries <= 5)
                {
                    TrafficState.VerifyDue = upd + TrafficConst.kVerifyDelayUpdates;
                    TrafficState.VerifyPending = true;
                }
                RRWLog.Once("traffic-verify-mismatch", "traffic restore check: " + refs + " lanes of " + m_Recheck.Count +
                            " opened edges still referenced the closure sentinel; refreshed again (retry " + m_VerifyRetries + ")");
                return;
            }
            m_VerifyRetries = 0;
            RRWLog.Verbose("traffic restore check: " + lanes + " lanes vanilla (VANILLA_OK), closures left " + TrafficState.Closures.Count);
            if (TrafficState.Closures.Count == 0)
            {
                TrafficState.Snaps.Clear();
                if (TrafficState.SentinelValid(em))
                {
                    em.DestroyEntity(TrafficState.Sentinel);
                    RRWLog.Verbose("traffic sentinel " + RRWLog.E(TrafficState.Sentinel) + " destroyed (no closures left)");
                }
                TrafficState.Sentinel = Entity.Null;
            }
        }

        // One edge per update. Full = the BFS (dependants, cut edge) + zones; light = zones only (buildings, stops, track).
        // Writes the classification fields; ClassifyUpdate changes only when a field value (or the stamp) changed, so the
        // Director re-evaluates CarHalfAllowed only on real changes.
        private void ClassifyNext(EntityManager em, uint upd)
        {
            while (m_ClassifyQueue.Count > 0)
            {
                var edge = m_ClassifyQueue.Dequeue();
                if (!SiteRegistry.TryGetEdge(edge, out var rec)) continue;
                var slot = rec.Get<TrafficEdgeSlot>(ModuleSlot.Traffic);
                if (slot == null) continue;
                slot.ClassifyQueued = false;
                if (!TrafficUtil.Alive(em, edge) || !em.HasComponent<RoadWorksRuntime>(edge)) continue;
                var rt = em.GetComponentData<RoadWorksRuntime>(edge);
                bool rev = RoadZoneMath.ChainReversed(rt.m_ChainU0, rt.m_ChainU1);
                slot.Groups.SyncFromRecord(em, edge, rec, rev, RRWCity.LeftHandTraffic);
                bool full = !slot.Classified || slot.ClassifiedRevision != rec.GeometryRevision || slot.ClassifiedReversed != rev ||
                            upd - slot.ClassifiedUpdate >= TrafficConst.kClassifyInterval;
                string fullText = null;
                if (full)
                {
                    var r = TrafficAnalysis.Classify(em, edge);
                    bool was = slot.Classified && slot.ClassifyDependants;
                    slot.Classified = true;
                    slot.ClassifyDependants = r.Dependants;
                    slot.ClassifyReasons = r.ReasonText;
                    slot.ClassifiedRevision = rec.GeometryRevision;
                    slot.ClassifiedReversed = rev;
                    slot.ClassifiedUpdate = upd;
                    slot.CutEdgeFull = TrafficAnalysis.CutEdgeOf(r);
                    fullText = r.Describe();
                    // Other projects' closed / hidden works roads count as cut (neighbouring staged sites)
                    if (!slot.CutEdgeFull && TrafficAnalysis.StagedCutEdge(em, edge, rec.ProjectId, out int skippedWorks) && skippedWorks > 0)
                    {
                        slot.CutEdgeFull = true;
                        fullText += " | dead end behind " + skippedWorks + " closed works edge(s) of other projects";
                    }
                    if (r.Dependants && !was && rt.m_Kind == WorksKind.Construction)
                        RRWLog.Info("traffic e" + edge.Index + " (p" + rec.ProjectId + ") has dependants (" + r.ReasonText +
                                    "): works without machines (mode D) keep a slow zone; roads with machines stay closed and the dependants wait");
                }
                var z = TrafficAnalysis.ClassifyZones(em, edge, slot.Groups);
                bool first = !rec.Classified;
                bool keyChange = rec.CutEdge != slot.CutEdgeFull || rec.HasTrack != z.HasTrack || rec.StopZones != z.StopZones;
                bool changed = first || rec.ClassifyReversed != rev || keyChange || rec.BuildingZones != z.BuildingZones || rec.BuildingCount != z.BuildingCount;
                rec.CutEdge = slot.CutEdgeFull;
                rec.HasTrack = z.HasTrack;
                rec.StopZones = z.StopZones;
                rec.BuildingZones = z.BuildingZones;
                rec.BuildingCount = z.BuildingCount;
                rec.ClassifyRevision = rec.GeometryRevision;
                rec.ClassifyReversed = rev;
                if (changed) rec.ClassifyUpdate = upd;
                slot.ZonesClassifiedUpdate = upd;
                slot.ZonesClassifiedBuildings = slot.Buildings;
                string line = "traffic classify e" + edge.Index + " (p" + rec.ProjectId + ") cutEdge=" + rec.CutEdge + " track=" + rec.HasTrack +
                              " stopZones=" + TrafficUtil.Bits(rec.StopZones) + " stops=" + z.Stops + " buildingZones=" + TrafficUtil.Bits(rec.BuildingZones) +
                              " buildings=" + rec.BuildingCount + " split=" + RRWLog.F(RoadZoneMath.DirSplitChain(slot.Groups.Section, rev)) +
                              "(" + slot.Groups.Section.Verdict + " " + slot.Groups.Section.ThroughF + ">" + slot.Groups.Section.ThroughB + "<)" +
                              (full ? " full: " + fullText : " (zones only)");
                if (first || keyChange) RRWLog.Info(line); else RRWLog.Verbose(line);
                return;     // one edge per update
            }
        }

        private void PruneSnaps(EntityManager em)
        {
            m_Remove.Clear();
            foreach (var kv in TrafficState.Snaps)
                if (!em.Exists(kv.Key) || em.HasComponent<Deleted>(kv.Key)) m_Remove.Add(kv.Key);
            for (int i = 0; i < m_Remove.Count; i++) TrafficState.Snaps.Remove(m_Remove[i]);
            if (m_Remove.Count > 0) RRWLog.Verbose("traffic pruned " + m_Remove.Count + " snapshots of removed lanes");
            m_Remove.Clear();
            foreach (var kv in TrafficState.ForbiddenSnap)
                if (!em.Exists(kv.Key) || em.HasComponent<Deleted>(kv.Key)) m_Remove.Add(kv.Key);
            for (int i = 0; i < m_Remove.Count; i++) TrafficState.ForbiddenSnap.Remove(m_Remove[i]);
            m_Remove.Clear();
        }

        // Verified in game: FixParkingLocation(Entity.Null, car) + Updated on every parked car of the edge's parking lanes.
        // Never (Null, Null): that removes the CarKeeper and unspawns the car. Building-free Closed edges only.
        private int Relocate(EntityManager em, Entity edge)
        {
            if (!TrafficUtil.Alive(em, edge)) return 0;
            TrafficUtil.LanesInto(em, edge, m_Lanes);
            m_Remove.Clear();   // parked cars (controllers) to fix
            int parkingLanes = 0, disabledLanes = 0;
            for (int k = 0; k < m_Lanes.Count; k++)
            {
                var lane = m_Lanes[k];
                if (!TrafficUtil.Alive(em, lane) || !em.HasComponent<NetParkingLane>(lane)) continue;
                if (TrafficState.Closures.TryGetValue(edge, out var rcl) && rcl.LaneIsOpen(em, lane)) continue;   // open half: untouched
                parkingLanes++;
                if ((em.GetComponentData<NetParkingLane>(lane).m_Flags & ParkingLaneFlags.ParkingDisabled) != 0) disabledLanes++;
                if (!em.HasBuffer<LaneObject>(lane)) continue;
                var buf = em.GetBuffer<LaneObject>(lane, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var car = buf[i].m_LaneObject;
                    if (!TrafficUtil.Alive(em, car) || !em.HasComponent<ParkedCar>(car)) continue;
                    var target = em.HasComponent<Controller>(car) ? em.GetComponentData<Controller>(car).m_Controller : car;
                    if (!TrafficUtil.Alive(em, target) || m_Remove.Contains(target)) continue;
                    m_Remove.Add(target);
                }
            }
            int queued = 0;
            for (int i = 0; i < m_Remove.Count; i++)
            {
                var target = m_Remove[i];
                var fix = new FixParkingLocation(Entity.Null, target);
                if (em.HasComponent<FixParkingLocation>(target)) em.SetComponentData(target, fix);
                else em.AddComponentData(target, fix);
                if (!em.HasComponent<Updated>(target)) em.AddComponent<Updated>(target);
                queued++;
            }
            m_Remove.Clear();
            if (queued > 0 || parkingLanes > 0)
                RRWLog.Verbose("traffic e" + edge.Index + " parked cars relocated=" + queued + " parkingLanes=" + parkingLanes + " disabled=" + disabledLanes);
            return queued;
        }

        // HardClose drain: vehicles whose navigation lanes (incl. reserved ones of obsolete paths) still lead onto
        // the edge would enter right after the blockage lands. Full scan, navigation lanes + 1 path element.
        private int CountApproaching(EntityManager em, EdgeClosure cl)
        {
            m_TmpLaneMap.Clear();
            TrafficUtil.LanesInto(em, cl.Edge, m_Lanes);
            for (int i = 0; i < m_Lanes.Count; i++)
                if (!cl.LaneIsOpen(em, m_Lanes[i])) m_TmpLaneMap[m_Lanes[i]] = 0u;     // open groups carry traffic on purpose
            if (m_TmpLaneMap.Count == 0) return 0;
            em.CompleteAllTrackedJobs();
            m_TmpScan.Reset();
            int cursor = 0;
            TrafficPaths.ScanSlice(em, m_PathQuery, m_TmpLaneMap, PathScanMode.Count, 1, ref cursor, 0, m_TmpScan, null);
            m_TmpLaneMap.Clear();
            return m_TmpScan.NavAny;
        }
    }

    // ------------------------------------------------------------------------------------------------ ModEnd, order 500
    // Right after LaneDataSystem: re-apply car / pedestrian / track fields of closed edges on every lane LaneDataSystem
    // just reset (Updated or PathfindUpdated, not Temp/Deleted). Snapshots the vanilla values first. No structural changes.
    [RegisterSystem(SystemUpdatePhase.ModificationEnd, After = typeof(Game.Pathfind.LaneDataSystem), Order = RRWOrder.LaneClosure)]
    public partial class LaneClosureSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("traffic lane closure");
        private readonly List<Entity> m_Lanes = new List<Entity>(32);
        private EntityQuery m_Changed;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Changed = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<NetLane>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<PathfindUpdated>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Guard.Reset();
        }

        protected override void OnUpdate()
        {
            if (TrafficState.Closures.Count == 0 || TrafficState.Disabled || m_Guard.Faulted) return;
            if (!TrafficGate.InGame || m_Changed.IsEmptyIgnoreFilter) return;
            long t = RRWPerf.Start();
            try
            {
                var em = EntityManager;
                bool sentinelOk = TrafficState.SentinelValid(em);
                Entity sentinel = sentinelOk ? TrafficState.Sentinel : Entity.Null;
                foreach (var cl in TrafficState.Closures.Values)
                {
                    if (!TrafficUtil.Alive(em, cl.Edge)) continue;
                    TrafficUtil.LanesInto(em, cl.Edge, m_Lanes);
                    int lanes = 0, writes = 0;
                    for (int i = 0; i < m_Lanes.Count; i++)
                    {
                        var lane = m_Lanes[i];
                        if (!em.Exists(lane) || em.HasComponent<Deleted>(lane) || em.HasComponent<Temp>(lane)) continue;
                        if (!em.HasComponent<Updated>(lane) && !em.HasComponent<PathfindUpdated>(lane)) continue;
                        lanes++;
                        writes += TrafficApply.ApplyLane(em, lane, cl, sentinel, snapshot: true);
                    }
                    if (lanes == 0) continue;
                    if (cl.Level == ClosureLevel.Closed && !sentinelOk)
                    {
                        // Lanes stay vanilla (open) this frame; Mod1 re-creates the sentinel and refreshes.
                        TrafficState.SentinelMissing = true;
                        continue;
                    }
                    cl.LanesConfirmed = true;
                    cl.LaneWrites += writes;
                }
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
                if (m_Guard.Faulted) TrafficState.Disabled = true;
            }
            finally { RRWPerf.Stop(PerfSlot.Traffic, t); }
        }
    }

    // ------------------------------------------------------------------------------------------------ ModEnd, order 510
    // Right after ParkingLaneDataSystem: ParkingDisabled + sentinel (no AllowEnter/Exit) on Closed building-free edges.
    // SlowZone and Closed edges with buildings keep vanilla parking (rule verified in game).
    [RegisterSystem(SystemUpdatePhase.ModificationEnd, After = typeof(Game.Pathfind.ParkingLaneDataSystem), Order = RRWOrder.ParkingClosure)]
    public partial class ParkingClosureSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("traffic parking closure");
        private readonly List<Entity> m_Lanes = new List<Entity>(32);
        private EntityQuery m_Changed;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Changed = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<NetParkingLane>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<PathfindUpdated>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Guard.Reset();
        }

        protected override void OnUpdate()
        {
            if (TrafficState.Closures.Count == 0 || TrafficState.Disabled || m_Guard.Faulted) return;
            if (!TrafficGate.InGame || m_Changed.IsEmptyIgnoreFilter) return;
            long t = RRWPerf.Start();
            try
            {
                var em = EntityManager;
                bool sentinelOk = TrafficState.SentinelValid(em);
                Entity sentinel = sentinelOk ? TrafficState.Sentinel : Entity.Null;
                foreach (var cl in TrafficState.Closures.Values)
                {
                    if (!TrafficUtil.Alive(em, cl.Edge)) continue;
                    TrafficUtil.LanesInto(em, cl.Edge, m_Lanes);
                    for (int i = 0; i < m_Lanes.Count; i++)
                    {
                        var lane = m_Lanes[i];
                        if (!em.Exists(lane) || !em.HasComponent<NetParkingLane>(lane)) continue;
                        if (em.HasComponent<Deleted>(lane) || em.HasComponent<Temp>(lane)) continue;
                        if (!em.HasComponent<Updated>(lane) && !em.HasComponent<PathfindUpdated>(lane)) continue;
                        if (cl.Level == ClosureLevel.Closed && !sentinelOk && TrafficApply.LaneParkClosed(em, lane, cl)) { TrafficState.SentinelMissing = true; continue; }
                        cl.LaneWrites += TrafficApply.ApplyParking(em, lane, cl, sentinel, snapshot: true);
                    }
                }
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
                if (m_Guard.Faulted) TrafficState.Disabled = true;
            }
            finally { RRWPerf.Stop(PerfSlot.Traffic, t); }
        }
    }

    // ------------------------------------------------------------------------------------------------ ModEnd, order 520
    // Right after LanesModifiedSystem (the graph update for this frame is already queued, so repaths requested in the
    // following simulation see the closure). Closed edges only, RerouteInFlight on. Event pass on a new closure plus
    // a periodic sweep every 64 updates; each pass is time-sliced (< kScanSliceMs per update).
    [RegisterSystem(SystemUpdatePhase.ModificationEnd, After = typeof(Game.Pathfind.LanesModifiedSystem), Order = RRWOrder.PathInvalidation)]
    public partial class PathInvalidationSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("traffic path invalidation");
        private readonly Dictionary<Entity, uint> m_LaneMap = new Dictionary<Entity, uint>();     // sweep: every closed lane
        private readonly Dictionary<Entity, uint> m_EventMap = new Dictionary<Entity, uint>();    // event: lanes that narrowed
        private readonly Dictionary<uint, int> m_Rerouted = new Dictionary<uint, int>();
        private readonly List<Entity> m_Lanes = new List<Entity>(32);
        private readonly PathScanResult m_Result = new PathScanResult();
        private EntityQuery m_Paths;
        private bool m_Active, m_Event;
        private int m_Cursor;
        private uint m_LastPassStart;
        private int m_MapRevision = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Paths = TrafficPaths.CreateQuery(EntityManager);
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Guard.Reset();
            m_Active = false;
            m_Event = false;
            m_Cursor = 0;
            m_LastPassStart = 0;
            m_MapRevision = -1;
            m_LaneMap.Clear();
            m_EventMap.Clear();
            m_Rerouted.Clear();
        }

        protected override void OnUpdate()
        {
            if (!TrafficGate.InGame) return;
            if (TrafficState.Disabled || m_Guard.Faulted) { TrafficState.InvalidationEventPending = false; m_Active = false; return; }
            var s = TrafficSettings.Read();
            if (!TrafficState.HasClosed || !s.Reroute || !TrafficState.InvalidationOn)
            {
                TrafficState.InvalidationEventPending = false;
                TrafficState.EventLanes.Clear();
                m_Active = false;
                return;
            }
            uint upd = RRWClock.UpdateIndex;
            if (TrafficState.InvalidationEventPending)
            {
                // Lane-keyed event pass over exactly the lanes that narrowed (new closure, a group leaving m_OpenLanes or
                // entering m_SoftZones); merged into a running event pass, it interrupts a running sweep (the next sweep
                // starts again kSweepIntervalUpdates later).
                TrafficState.InvalidationEventPending = false;
                if (TrafficState.EventLanes.Count > 0)
                {
                    if (!(m_Active && m_Event)) m_EventMap.Clear();
                    foreach (var kv in TrafficState.EventLanes) m_EventMap[kv.Key] = kv.Value;
                    TrafficState.EventLanes.Clear();
                    StartPass(true, upd);
                }
            }
            else if (!m_Active && upd - m_LastPassStart >= TrafficConst.kSweepIntervalUpdates) StartPass(false, upd);
            if (!m_Active) return;

            long t = RRWPerf.Start();
            try
            {
                var em = EntityManager;
                if (!m_Event && m_MapRevision != TrafficState.Revision) RebuildLaneMap(em);
                var map = m_Event ? m_EventMap : m_LaneMap;
                if (map.Count == 0) { m_Active = false; m_Event = false; return; }
                em.CompleteAllTrackedJobs();
                bool done = TrafficPaths.ScanSlice(em, m_Paths, map, PathScanMode.Invalidate, TrafficConst.kScanAhead,
                                                   ref m_Cursor, TrafficConst.kScanSliceMs, m_Result, m_Rerouted);
                if (m_Rerouted.Count > 0)
                {
                    foreach (var kv in m_Rerouted)
                        if (SiteRegistry.TryGetProject(kv.Key, out var proj)) proj.Rerouted += kv.Value;
                    m_Rerouted.Clear();
                }
                if (done) FinishPass();
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Active = false;
                m_Guard.Fail(e);
                if (m_Guard.Faulted) TrafficState.Disabled = true;
            }
            finally { RRWPerf.Stop(PerfSlot.Traffic, t); }
        }

        private void StartPass(bool evt, uint upd)
        {
            m_Event = evt;
            m_Active = true;
            m_Cursor = 0;
            m_LastPassStart = upd;
            m_MapRevision = -1;     // lanes may have been regenerated since the last pass
            m_Result.Reset();
        }

        private void RebuildLaneMap(EntityManager em)
        {
            m_LaneMap.Clear();
            foreach (var cl in TrafficState.Closures.Values)
            {
                if (cl.Level != ClosureLevel.Closed || !TrafficUtil.Alive(em, cl.Edge)) continue;
                TrafficUtil.LanesInto(em, cl.Edge, m_Lanes);
                for (int i = 0; i < m_Lanes.Count; i++)
                    if (!cl.LaneIsOpen(em, m_Lanes[i])) m_LaneMap[m_Lanes[i]] = cl.ProjectId;    // open groups (staged opening) keep their paths
            }
            m_MapRevision = TrafficState.Revision;
        }

        private void FinishPass()
        {
            m_Active = false;
            TrafficState.Passes++;
            TrafficState.InvalidatedTotal += m_Result.Invalidated;
            if (m_Event) { TrafficState.EventPasses++; }
            string line = "traffic reroute " + (m_Event ? "event lanes=" + m_EventMap.Count : "sweep lanes=" + m_LaneMap.Count) + " closedEdges=" + TrafficState.ClosedCount +
                          " invalidated=" + m_Result.Invalidated + " reinvalidated=" + m_Result.Reinvalidated +
                          " skippedOnLane=" + m_Result.SkippedOnLane + " skippedPendingFailedStuck=" + m_Result.SkippedPendingFailed +
                          " skippedDest=" + m_Result.SkippedDest + " totalInvalidated=" + TrafficState.InvalidatedTotal + " | " + m_Result.Summary();
            if (m_Event && m_Result.Invalidated > 0) RRWLog.Info(line);
            else RRWLog.Verbose(line);
            m_Event = false;
            m_EventMap.Clear();
        }
    }

    // ------------------------------------------------------------------------------------------------ Serialize, 905 / 915
    // Never let closure lane fields reach a save (autosave included): write the vanilla snapshot right before
    // SerializerSystem and re-apply the closure right after (Serialize is synchronous). Verified in game (80 lanes).
    [RegisterSystem(SystemUpdatePhase.Serialize, Before = typeof(Game.Serialization.SerializerSystem), Order = RRWOrder.TrafficSaveGuard)]
    public partial class TrafficSaveGuardSystem : GameSystemBase
    {
        private readonly List<Entity> m_Lanes = new List<Entity>(32);

        protected override void OnUpdate()
        {
            if (TrafficState.Closures.Count == 0 && TrafficState.Upgrade.Count == 0) return;
            // Set first: if neutralising throws half-way, the restore system still re-applies every closure.
            TrafficState.SaveNeutralised = true;
            TrafficState.SaveFreeSpace.Clear();
            int n = 0, missing = 0;
            try
            {
                var em = EntityManager;
                em.CompleteAllTrackedJobs();
                foreach (var cl in TrafficState.Closures.Values)
                {
                    TrafficUtil.LanesInto(em, cl.Edge, m_Lanes);
                    for (int i = 0; i < m_Lanes.Count; i++)
                    {
                        var lane = m_Lanes[i];
                        if (!em.Exists(lane)) continue;
                        if (!TrafficState.Snaps.ContainsKey(lane)) { missing++; continue; }
                        n += TrafficApply.WriteSnapshot(em, lane, cl);
                    }
                }
                // Lane closure markers of upgrade works: the lane data they cause, AFTER the closure snapshots were written.
                int blockLanes = LaneClosureMarkers.NeutraliseForSave(em, out int owners, out int markers, out int noLivePath);
                RRWLog.Info("traffic save-guard: neutralised " + n + " lane components (" + TrafficState.SaveFreeSpace.Count +
                            " parking free-space overrides, " + TrafficState.ForbiddenSnap.Count + " Forbidden bits, " + missing +
                            " lanes without snapshot) of " + TrafficState.Closures.Count + " closures; lane closure markers: " + blockLanes +
                            " lane(s) of " + owners + " owner(s) neutralised, " + markers + " marker(s), " + noLivePath + " without LivePath");
                if (noLivePath > 0) RRWLog.Error("traffic save-guard: " + noLivePath + " lane closure marker(s) without LivePath would be saved");
            }
            catch (Exception e) { RRWLog.ErrorOnce("traffic save-guard (after " + n + " writes)", e); }
        }
    }

    [RegisterSystem(SystemUpdatePhase.Serialize, After = typeof(Game.Serialization.SerializerSystem), Order = RRWOrder.TrafficSaveRestore)]
    public partial class TrafficSaveRestoreSystem : GameSystemBase
    {
        private readonly List<Entity> m_Lanes = new List<Entity>(32);

        protected override void OnUpdate()
        {
            if (!TrafficState.SaveNeutralised) return;
            TrafficState.SaveNeutralised = false;
            try
            {
                var em = EntityManager;
                em.CompleteAllTrackedJobs();
                // Marker lane values first (they were neutralised last, on top of the closure snapshots), then the closures.
                int markerLanes = LaneClosureMarkers.RestoreAfterSave(em);
                int n = 0;
                Entity sentinel = TrafficState.HasClosed ? TrafficState.EnsureSentinel(em) : TrafficState.Sentinel;
                foreach (var cl in TrafficState.Closures.Values)
                {
                    TrafficUtil.LanesInto(em, cl.Edge, m_Lanes);
                    for (int i = 0; i < m_Lanes.Count; i++)
                    {
                        var lane = m_Lanes[i];
                        if (!em.Exists(lane) || !TrafficState.Snaps.ContainsKey(lane)) continue;
                        n += TrafficApply.ApplyLane(em, lane, cl, sentinel, snapshot: false);
                        n += TrafficApply.ApplyParking(em, lane, cl, sentinel, snapshot: false);
                        TrafficApply.RestoreFreeSpace(em, lane);
                    }
                }
                TrafficState.SaveFreeSpace.Clear();
                RRWLog.Info("traffic save-guard: re-applied " + n + " lane components after serialization (" + markerLanes + " lane closure marker lane(s))");
            }
            catch (Exception e)
            {
                // Lanes may be left at vanilla values: refresh them so the closure systems re-apply next frame.
                RRWLog.ErrorOnce("traffic save-restore", e);
                TrafficState.SaveFreeSpace.Clear();
                LaneClosureMarkers.ClearSaveState();
                TrafficState.RefreshAllPending = true;
                // the game recomputes the marker lanes' blockage from the (still registered) markers on a refresh
                foreach (var et in TrafficState.Upgrade.Values) TrafficState.UpgradeRefresh[et.Edge] = RRWClock.UpdateIndex + 1;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ dev introspection
    // rrw.dump / rrw.check hooks (Core extension point; compiled in release too, only dev commands call them).
    public static class TrafficIntrospection
    {
        private static readonly List<Entity> s_Lanes = new List<Entity>(32);

        public static string Dump(EntityManager em, Entity edge)
        {
            var sb = new System.Text.StringBuilder(512);
            uint upd = RRWClock.UpdateIndex;
            TrafficState.Closures.TryGetValue(edge, out var cl);
            sb.Append("closure=").Append(cl != null ? cl.State : "Open");
            if (cl != null)
                sb.Append(" parking=").Append(cl.ParkClosed ? "disabled" : "untouched").Append(" confirmed=").Append(cl.LanesConfirmed)
                  .Append(" refreshes=").Append(cl.Refreshes).Append(" writes=").Append(cl.LaneWrites)
                  .Append(cl.Level == ClosureLevel.SlowZone || (cl.Open & RoadZones.Carriageway) != 0 ? " speed=" + TrafficUtil.Kmh(cl.SlowSpeed) + "km/h" : "");
            bool rev = false;
            if (em.HasComponent<RoadWorksRuntime>(edge))
            {
                var rt = em.GetComponentData<RoadWorksRuntime>(edge);
                rev = RoadZoneMath.ChainReversed(rt.m_ChainU0, rt.m_ChainU1);
                sb.Append(" target=").Append(rt.m_ClosureTarget).Append(" targetOpen=").Append(TrafficUtil.Bits(rt.m_OpenLanes))
                  .Append(" targetSoft=").Append(TrafficUtil.Bits(rt.m_SoftZones)).Append(" chainReversed=").Append(rev);
            }
            if (SiteRegistry.TryGetEdge(edge, out var rec))
            {
                sb.Append(" applied=").Append(rec.ClosureApplied).Append(" openApplied=").Append(TrafficUtil.Bits(rec.OpenLanesApplied))
                  .Append(" softApplied=").Append(TrafficUtil.Bits(rec.SoftApplied)).Append(" closedBApplied=").Append(TrafficUtil.Bits(rec.ClosedBApplied))
                  .Append(" present=").Append(TrafficUtil.Bits(rec.ZonesPresent)).Append(" drained=").Append(TrafficUtil.Bits(rec.ZonesDrained))
                  .Append(" reportAge=").Append(rec.ZonesReportUpdate == 0 ? "never" : unchecked(upd - rec.ZonesReportUpdate).ToString())
                  .Append(" carHalves=").Append(TrafficUtil.Bits(rec.CarHalves))
                  .Append(" split=").Append(RRWLog.F(RoadZoneMath.DirSplitChain(rec.Section, rev))).Append('(').Append(rec.Section.Verdict).Append(')')
                  .Append(" classified=").Append(rec.Classified).Append(" cutEdge=").Append(rec.CutEdge).Append(" track=").Append(rec.HasTrack)
                  .Append(" stopZones=").Append(TrafficUtil.Bits(rec.StopZones)).Append(" buildingZones=").Append(TrafficUtil.Bits(rec.BuildingZones))
                  .Append(" buildingCount=").Append(rec.BuildingCount).Append(" hidden=").Append(rec.HiddenApplied)
                  .Append(" dependants=").Append(rec.Dependants)
                  .Append(" buildingsWaiting=").Append(rec.BuildingsWaiting).Append(" trafficDrained=").Append(rec.TrafficDrained);
                var slot = rec.Get<TrafficEdgeSlot>(ModuleSlot.Traffic);
                if (slot != null)
                    sb.Append(" buildings=").Append(slot.Buildings).Append(" classify=").Append(slot.Classified ? slot.ClassifyReasons : "pending")
                      .Append(" armed=").Append(TrafficUtil.Bits(slot.Armed)).Append(" timedOut=").Append(TrafficUtil.Bits(slot.TimedOut))
                      .Append(" clearB=").Append(TrafficUtil.Bits(slot.ClearB)).Append(" lastScanBusy=").Append(TrafficUtil.Bits(slot.LastBusy))
                      .Append(" lastScanOccupied=").Append(TrafficUtil.Bits(slot.LastOccupied))
                      .Append(" lastScanAge=").Append(slot.LastScanUpdate == 0 ? "never" : unchecked(upd - slot.LastScanUpdate).ToString())
                      .Append(" pendingBFor=").Append(slot.PendingBSince == 0 ? "-" : unchecked(upd - slot.PendingBSince).ToString())
                      .Append(" movingAtLastDrainCheck=").Append(slot.LastMoving);
                if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj))
                    sb.Append(" projRerouted=").Append(proj.Rerouted).Append(" projParkedMoved=").Append(proj.ParkedMoved)
                      .Append(" projMode=").Append(proj.Mode).Append(" projOpenLanes=").Append(TrafficUtil.Bits(proj.OpenLanes))
                      .Append(TrafficRequestSystem.MachineText(proj, upd));
            }
            int lanes = 0, sentinelLanes = 0, openLanes = 0, soft = 0, closedS = 0, closedB = 0;
            var sentinel = TrafficState.Sentinel;
            TrafficUtil.LanesInto(em, edge, s_Lanes);
            for (int i = 0; i < s_Lanes.Count; i++)
            {
                var lane = s_Lanes[i];
                if (!TrafficUtil.Alive(em, lane)) continue;
                lanes++;
                if (TrafficApply.ReferencesSentinel(em, lane, sentinel)) sentinelLanes++;
                if (cl == null) continue;
                switch (cl.StateOf(em, lane))
                {
                    case LaneState.Slow: case LaneState.Vanilla: openLanes++; break;
                    case LaneState.Soft: soft++; break;
                    case LaneState.ClosedS: closedS++; break;
                    case LaneState.ClosedB: closedB++; break;
                }
            }
            sb.Append(" lanes=").Append(lanes).Append(" sentinelLanes=").Append(sentinelLanes);
            if (cl != null) sb.Append(" laneStates(open/soft/closedS/closedB)=").Append(openLanes).Append('/').Append(soft).Append('/').Append(closedS).Append('/').Append(closedB);
            sb.Append(UpgradeTraffic.DescribeEdge(em, edge));
            return sb.ToString();
        }

        public static void Check(EntityManager em, List<string> problems)
        {
            int start = problems.Count;
            uint upd = RRWClock.UpdateIndex;
            bool closureLayer = RRWDebug.On(DebugLayers.Closure);
            if (TrafficState.Disabled) problems.Add("traffic: disabled after repeated errors (all works roads open)");
            bool sentinelOk = TrafficState.SentinelValid(em);
            if (TrafficState.HasClosed && !sentinelOk) problems.Add("traffic: " + TrafficState.ClosedCount + " closed edges but no access sentinel");

            // Rules per project (U3 rule; M-open guarantee seen from the lanes).
            if (closureLayer && !TrafficState.Disabled)
            {
                foreach (var proj in SiteRegistry.Projects.Values)
                {
                    RoadZones openApplied = RoadZones.None;
                    for (int i = 0; i < proj.Edges.Count; i++)
                    {
                        var e = proj.Edges[i];
                        if (!SiteRegistry.TryGetEdge(e, out var r) || !TrafficUtil.Alive(em, e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                        openApplied |= r.OpenLanesApplied;
                        var rt = em.GetComponentData<RoadWorksRuntime>(e);
                        string why = TrafficRequestSystem.U3Violation(em, proj, rt.m_Phase, rt.m_ClosureTarget, upd);
                        if (why != null) { problems.Add("traffic: U3 p" + proj.Id + " e" + e.Index + " target " + rt.m_ClosureTarget + " in " + rt.m_Phase + ": " + why); break; }
                    }
                    var busyOpen = openApplied & proj.MachineZones & RoadZones.AllLanes;
                    // upgrade works: machines stand in dropped lanes (markers) inside open groups
                    if (busyOpen != RoadZones.None && proj.Mode == VisualMode.HalfWidth) busyOpen &= ~UpgradeTraffic.MarkedZones(em, proj);
                    if (busyOpen != RoadZones.None && proj.MachinesReportFresh(upd))
                        problems.Add("traffic: p" + proj.Id + " machines in open lane group(s) " + TrafficUtil.Bits(busyOpen) + " (open " + TrafficUtil.Bits(openApplied) +
                                     ", machine zones " + TrafficUtil.Bits(proj.MachineZones) + (proj.Crews > 1 ? ", union of " + proj.Crews + " crews" : "") + ")");
                }
            }

            foreach (var kv in SiteRegistry.Edges)
            {
                if (problems.Count - start >= 24) { problems.Add("traffic: ... (more problems not listed)"); return; }
                var edge = kv.Key;
                var rec = kv.Value;
                if (!TrafficUtil.Alive(em, edge) || !em.HasComponent<RoadWorksRuntime>(edge)) continue;
                var rtE = em.GetComponentData<RoadWorksRuntime>(edge);
                var target = closureLayer ? rtE.m_ClosureTarget : ClosureLevel.Open;
                TrafficState.Closures.TryGetValue(edge, out var cl);
                var level = cl != null ? cl.Level : ClosureLevel.Open;
                var slot = rec.Get<TrafficEdgeSlot>(ModuleSlot.Traffic);
                if (!TrafficState.Disabled && level != target)
                    problems.Add("traffic: e" + edge.Index + " closure " + level + " but target " + target);
                // m_SoftZones and m_OpenLanes never overlap (Director contract).
                var overlap = rtE.m_SoftZones & rtE.m_OpenLanes & RoadZones.AllLanes;
                if (overlap != RoadZones.None) problems.Add("traffic: e" + edge.Index + " group(s) " + TrafficUtil.Bits(overlap) + " both in m_OpenLanes and m_SoftZones");
                if (rec.HiddenApplied && ((rtE.m_OpenLanes | rtE.m_SoftZones) & RoadZones.AllLanes) != RoadZones.None)
                    problems.Add("traffic: e" + edge.Index + " hidden but open/soft groups " + TrafficUtil.Bits(rtE.m_OpenLanes) + "/" + TrafficUtil.Bits(rtE.m_SoftZones) + " (F10)");
                if (!TrafficState.Disabled && unchecked(upd - rec.ZonesReportUpdate) > (uint)RRWConst.kZonesReportStaleUpdates)
                    problems.Add("traffic: e" + edge.Index + " drain report stale (age " + (rec.ZonesReportUpdate == 0 ? "never" : unchecked(upd - rec.ZonesReportUpdate).ToString()) + ")");
                if ((rec.ZonesDrained & rec.OpenLanesApplied) != RoadZones.None)
                    problems.Add("traffic: e" + edge.Index + " drained groups " + TrafficUtil.Bits(rec.ZonesDrained & rec.OpenLanesApplied) + " are applied open");
                if (!TrafficState.Disabled && cl != null && !cl.IsDev && target == ClosureLevel.Closed)
                {
                    var soft = rtE.m_SoftZones & RoadZones.AllLanes;
                    bool upgrade = SiteRegistry.TryGetProject(rec.ProjectId, out var ep) && ep.Mode == VisualMode.HalfWidth;
                    var want = rtE.m_OpenLanes & RoadZones.AllLanes & ~TrafficRequestSystem.ParkingKeptClosed(upgrade) & ~soft;
                    if (cl.Open != want) problems.Add("traffic: e" + edge.Index + " open lane groups " + TrafficUtil.Bits(cl.Open) + " but target " + TrafficUtil.Bits(want));
                    if (cl.Soft != soft) problems.Add("traffic: e" + edge.Index + " soft groups " + TrafficUtil.Bits(cl.Soft) + " but target " + TrafficUtil.Bits(soft));
                    // P4: with CLOSED-B on, a drained car group of a visible building edge beside an open group must be CLOSED-B.
                    if (RRWGates.ClosedB && slot != null && slot.PendingBSince != 0 && upd - slot.PendingBSince > TrafficConst.kPendingBWarnUpdates &&
                        (rec.OpenLanesApplied & RoadZones.Carriageway) != RoadZones.None)
                        problems.Add("traffic: P4 e" + edge.Index + " drained car group(s) " + TrafficUtil.Bits(rec.ZonesDrained & RoadZones.Carriageway & ~rec.ClosedBApplied) +
                                     " still CLOSED-S for " + (upd - slot.PendingBSince) + " updates beside open " + TrafficUtil.Bits(rec.OpenLanesApplied) +
                                     " (buildings=" + slot.Buildings + ", occupied at last scan " + TrafficUtil.Bits(slot.LastOccupied) + ")");
                }
                if (cl == null || upd - cl.ChangedUpdate <= 2) continue;
                TrafficUtil.LanesInto(em, edge, s_Lanes);
                int mismatch = 0, sentinelOpen = 0, forbiddenOpen = 0;
                Entity first = Entity.Null;
                LaneState firstState = LaneState.Vanilla;
                for (int i = 0; i < s_Lanes.Count; i++)
                {
                    var lane = s_Lanes[i];
                    if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane)) continue;
                    var st = cl.StateOf(em, lane);
                    bool open = st == LaneState.Slow || st == LaneState.Vanilla;
                    if (open && TrafficApply.ReferencesSentinel(em, lane, TrafficState.Sentinel)) sentinelOpen++;
                    if (open && TrafficState.ForbiddenEverUsed && em.HasComponent<NetCarLane>(lane) &&
                        (em.GetComponentData<NetCarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0 &&
                        (!TrafficState.ForbiddenSnap.TryGetValue(lane, out bool vf) || !vf)) forbiddenOpen++;
                    if (!sentinelOk && TrafficApply.Closes(st)) continue;   // reported above
                    if (!TrafficApply.LaneMatches(em, lane, cl, TrafficState.Sentinel, null))
                    {
                        if (mismatch == 0) { first = lane; firstState = st; }
                        mismatch++;
                    }
                }
                if (sentinelOpen > 0) problems.Add("traffic: e" + edge.Index + " sentinel on " + sentinelOpen + " applied-open lane(s) (open " + TrafficUtil.Bits(cl.Open) + ")");
                if (forbiddenOpen > 0) problems.Add("traffic: e" + edge.Index + " Forbidden left on " + forbiddenOpen + " open lane(s) (soft mode was used; not vanilla)");
                if (mismatch > 0) problems.Add("traffic: e" + edge.Index + " " + mismatch + " lane(s) do not carry their group state (first " + RRWLog.E(first) +
                                               " should be " + firstState + "; rrw.tr.probe e" + edge.Index + " v)");
            }
            UpgradeChecks(em, problems, upd);

            // Opened edges are verified by the request system; report a pending mismatch here too.
            foreach (var kv in TrafficState.Closures)
                if (!kv.Value.IsDev && !SiteRegistry.Edges.ContainsKey(kv.Key) && TrafficUtil.Alive(em, kv.Key))
                    problems.Add("traffic: closure on e" + kv.Key.Index + " which is no longer a works edge");
        }

        // Upgrade works: markers only on dropped lanes of edges without buildings, the report agrees with the markers, released
        // markers do not linger past the cap, no Forbidden of ours anywhere on a mode H edge.
        private static void UpgradeChecks(EntityManager em, List<string> problems, uint upd)
        {
            foreach (var et in TrafficState.Upgrade.Values)
            {
                SiteRegistry.TryGetEdge(et.Edge, out var rec);
                int buildings = rec != null ? rec.BuildingCount : 0;
                int active = 0;
                for (int i = 0; i < et.Lanes.Count; i++)
                {
                    var ld = et.Lanes[i];
                    if (ld.Stage == DropStage.Releasing)
                    {
                        if (unchecked(upd - ld.ReleaseUpdate) > (uint)RRWConst.kCompletionMachineWaitUpdatesHard)
                            problems.Add("traffic: e" + et.Edge.Index + " released lane at lateral " + RRWLog.F(ld.Centre) + " still carries " + ld.Markers.Count +
                                         " marker(s) after " + unchecked(upd - ld.ReleaseUpdate) + " updates");
                        continue;
                    }
                    if (ld.Stage == DropStage.Active) active++;
                    if (ld.Stage == DropStage.Active)
                    {
                        int alive = 0;
                        for (int k = 0; k < ld.Markers.Count; k++) if (LaneClosureMarkers.Alive(em, ld.Markers[k])) alive++;
                        if (alive == 0) problems.Add("traffic: e" + et.Edge.Index + " dropped lane at lateral " + RRWLog.F(ld.Centre) + " is active without a live marker");
                    }
                }
                if (active > 0 && buildings > 0)
                    problems.Add("traffic: e" + et.Edge.Index + " lane drop next to " + buildings + " connected building(s)");
                if (rec?.Upgrade != null && rec.Upgrade.BlockersRegistered)
                {
                    var st = rec.Upgrade;
                    for (int i = 0; i < st.DropLanes.Count; i++)
                    {
                        var ld = et.Find(st.DropLanes[i], false);
                        if (ld == null || ld.Stage != DropStage.Active)
                        {
                            problems.Add("traffic: e" + et.Edge.Index + " drop report says registered but lane " + RRWLog.E(st.DropLanes[i]) + " is " + (ld == null ? "untracked" : ld.Stage.ToString()));
                            break;
                        }
                    }
                }
                foreach (var kv in et.ParkingReturning)
                    if (unchecked(upd - kv.Value.Update) > (uint)RRWConst.kCompletionMachineWaitUpdatesHard)
                    {
                        problems.Add("traffic: e" + et.Edge.Index + " new parking lane " + RRWLog.E(kv.Key) + " still switched off " + unchecked(upd - kv.Value.Update) +
                                     " updates after it was due back in use");
                        break;
                    }
                if (rec?.Upgrade != null && rec.Upgrade.DropLanes.Count > 0 && !rec.Upgrade.DropCentresWritten)
                    problems.Add("traffic: e" + et.Edge.Index + " drop report lists " + rec.Upgrade.DropLanes.Count + " lane(s) but " + rec.Upgrade.DropLaneCentres.Count + " centre(s)");
                if (rec?.Upgrade != null && rec.Upgrade.DropWindow >= 0 && rec.Upgrade.DropTail != rec.Upgrade.TailRevision && unchecked(upd - rec.Upgrade.DropReportUpdate) > 2)
                    problems.Add("traffic: e" + et.Edge.Index + " drop report key belongs to tail " + rec.Upgrade.DropTail + " (now " + rec.Upgrade.TailRevision + ")");
            }
            if (TrafficState.ForbiddenEverUsed)
                foreach (var kv in SiteRegistry.Projects)
                    if (kv.Value.Mode == VisualMode.HalfWidth)
                        for (int i = 0; i < kv.Value.Edges.Count; i++)
                        {
                            TrafficUtil.LanesInto(em, kv.Value.Edges[i], s_Lanes);
                            for (int k = 0; k < s_Lanes.Count; k++)
                                if (TrafficState.ForbiddenSnap.ContainsKey(s_Lanes[k]))
                                {
                                    problems.Add("traffic: p" + kv.Key + " upgrade works carry Forbidden of ours on e" + kv.Value.Edges[i].Index);
                                    k = s_Lanes.Count;
                                }
                        }
        }
    }
}
