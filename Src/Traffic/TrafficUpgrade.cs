using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetParkingLane = Game.Net.ParkingLane;
using NetPedestrianLane = Game.Net.PedestrianLane;
using NetTrackLane = Game.Net.TrackLane;
using NetMasterLane = Game.Net.MasterLane;
using ObjTransform = Game.Objects.Transform;
using ObjectGeometryData = Game.Prefabs.ObjectGeometryData;
using PrefabRef = Game.Prefabs.PrefabRef;
using ParkedCar = Game.Vehicles.ParkedCar;
using Vehicle = Game.Vehicles.Vehicle;

// Traffic part of upgrade works (a road replaced by another type: only the changed strip is built, the rest stays in use).
//
// The Director picks one traffic primitive per window (PhasePlan.WindowPrimitive) and turns Half / Carriageway / Sidewalk into
// the usual lane-group closure (m_ClosureTarget Closed + m_OpenLanes); those run through the existing closure path. This file adds
// what upgrade works need on top of it, per edge of a mode H project:
//  * lane drops: while the APPLIED window's primitive is Drop (UpgradeView.AppliedWindow: the previous window while its machines
//    leave at a window start, as for the lane groups), the car lanes whose centre lies inside a build band of that window
//    (UpgradeEdgeState.DropBandOf) get lane closure markers (TrafficLaneClosures.cs). Traffic is the only writer of the edge's
//    drop report (DropLanes + DropLaneCentres + key, DropApplied, BlockersRegistered, DropIntrusion, DropCleanChecks,
//    DropSafeU0/U1, DropReportUpdate); Machines stand in a dropped lane only while UpgradeEdgeState.DropReadyFor holds, and only
//    between the first and the last marker (DropSafeRange). Never next to connected buildings (cars enter a dropped lane from
//    parking and driveways there), never when a travel direction would lose its last lane.
//    A lane that is no longer dropped keeps its markers until no machine body comes near it (or the release cap), so a lane
//    never opens with a machine in it. When the edge moves to another project (re-upgrade, undo, split), the markers of the
//    old project wait for its machines the same way, also while their lane group is closed, and for rollers its last
//    machine report showed on that side when the project is gone.
//  * new parking: EMPTY parking lanes inside build bands are switched off (ParkingDisabled only) until their band's window
//    ended; a lane holding a parked car is left alone. Only a switched-off lane that is still empty is listed for the machines
//    (the parking-off list, Traffic its only writer); a car that parks before the switch-off took effect takes it off the list.
//    A lane that comes back into use is first taken off the list and stays switched off until no machine body is near it.
//  * the detour verdict per project (TrafficDetour.cs).
// The soft visual drop (Forbidden on band lanes) is never used: it did not keep cars out in game.
namespace RealisticRoadWorks.V3.Traffic
{
    public enum DropStage : byte
    {
        Pending = 0,     // wanted, no markers yet (prefab not ready)
        Placed = 1,      // markers created this update; the game registers them in this frame
        Refreshed = 2,   // registered on this lane only; lanes refreshed, waiting for the lane's blockage
        Active = 3,      // the lane carries the blockage of its markers
        Failed = 4,      // no position keeps clear of the open lanes, or the game never registered the markers (retried on a new key)
        Releasing = 5,   // no longer dropped: the markers stay until no machine stands near the lane
    }

    // The markers of one dropped lane.
    public sealed class LaneDrop
    {
        public Entity Lane;
        public float Centre;              // EDGE-frame lateral of the lane centre
        public float HalfWidth;
        public RoadZones Half;            // chain-frame car half of the lane
        public uint ProjectId;            // the project that dropped it
        public readonly List<Entity> Markers = new List<Entity>(8);
        public readonly List<float> Stations = new List<float>(8);      // EDGE-frame arc distance each marker was placed for
                                                                        // (same order and count as Markers)
        public readonly List<Entity> Registered = new List<Entity>(4);  // lanes the markers registered on (refreshed after removal)
        public DropStage Stage;
        public uint StageUpdate;          // RRWClock.UpdateIndex the stage was entered
        public int Tries;
        public uint ReleaseUpdate, ReleaseSim;
        public string Why = "";           // why it failed / is being released
        // Released because the edge moved to another project: the markers wait for the old project's machines even while the
        // lane's group is closed (the new project may open it before those machines have left).
        public bool HandOver;
        // Roller watch (rollers have no entity, only the project's machine report shows them). SimFrame of the last fresh
        // machine report of ProjectId, and of the last one that had machines in the lane's half while rollers were out
        // (0 = never). Kept for the project that dropped the lane before, when another project dropped it again.
        public uint SeenSim, RollersSim;
        public uint PrevProjectId, PrevSeenSim, PrevRollersSim;
    }

    // Upgrade-works traffic state of one edge. Global (TrafficState.Upgrade, keyed by edge): released markers outlive the registry
    // entry, and a registry rebuild must never lose track of live marker entities.
    public sealed class EdgeUpgradeTraffic
    {
        public Entity Edge;
        public uint ProjectId;
        public readonly List<LaneDrop> Lanes = new List<LaneDrop>(4);   // dropped lanes and lanes being released
        // key the drop set was resolved for
        public bool Resolved, Want;
        public int Geo = int.MinValue, Window = -3, Tail = -1;
        public bool AllAtOnce;
        public string Why = "";           // why no lane is dropped (empty while drops are wanted)
        public uint NextCheck;            // next drain check (UpdateIndex)
        public bool LoggedReady;
        public EdgeArc OwnArc;            // the edge curve's arc when the registry has none (EDGE frame)
        public int OwnArcRevision = int.MinValue;
        // new parking switched off: lane -> EDGE-frame lateral centre
        public readonly HashSet<Entity> ParkingOff = new HashSet<Entity>();
        public readonly Dictionary<Entity, float> ParkingOffLat = new Dictionary<Entity, float>();
        public int ParkingKept;           // candidate lanes left in use because they held a parked car (last check)
        public uint NextParkingCheck;
        public int ParkWindow = int.MinValue, ParkGeo = int.MinValue, ParkTail = -1;
        // switched-off lanes that are NOT listed for the machines: a car stands in it (listed again once it is empty) ...
        public readonly HashSet<Entity> ParkingHeld = new HashSet<Entity>();
        // ... or the lane comes back into use and stays switched off until no machine is near it (value: since when)
        public readonly Dictionary<Entity, ParkingReturn> ParkingReturning = new Dictionary<Entity, ParkingReturn>();
        // EDGE-frame arc range between the first and the last marker over every dropped lane (NaN until every dropped lane
        // carries its markers); the drain check and DropSafeU0/U1 use it
        public float SafeS0 = float.NaN, SafeS1 = float.NaN;
        public string SafeWhy = "";       // why no range is written while the drop is applied (empty otherwise)

        public bool HasAnything => Lanes.Count > 0 || ParkingOff.Count > 0 || Want;

        // A switched-off lane listed for the machines (switched off, empty, not on its way back into use).
        public bool ParkingListed(Entity lane) => ParkingOff.Contains(lane) && !ParkingHeld.Contains(lane) && !ParkingReturning.ContainsKey(lane);

        public int MarkerCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Lanes.Count; i++) n += Lanes[i].Markers.Count;
                return n;
            }
        }

        public LaneDrop Find(Entity lane, bool releasing)
        {
            for (int i = 0; i < Lanes.Count; i++)
                if (Lanes[i].Lane == lane && (Lanes[i].Stage == DropStage.Releasing) == releasing) return Lanes[i];
            return null;
        }
    }

    // A switched-off parking lane on its way back into use.
    public struct ParkingReturn
    {
        public uint Update, Sim;          // RRWClock.UpdateIndex / SimFrame it was taken off the list
        public string Why;
    }

    public static class UpgradeTraffic
    {
        private struct DriveLane { public Entity Lane; public float Centre, HalfWidth; public sbyte Dir; public bool Dropped; }
        private struct OtherLane { public Bezier4x3 Curve; public float HalfWidth; }
        private struct MachinePos { public float3 Pos; public quaternion Rot; public bool HasBox; public Bounds3 Box; public uint ProjectId; }
        private sealed class ViewCache { public uint Update; public bool Upgrade; public UpgradeView View; }

        private static readonly List<Entity> s_Lanes = new List<Entity>(32);
        private static readonly List<Entity> s_Tmp = new List<Entity>(8);
        private static readonly List<Entity> s_Keys = new List<Entity>(16);
        private static readonly List<DriveLane> s_Drive = new List<DriveLane>(16);
        private static readonly List<OtherLane> s_Others = new List<OtherLane>(24);
        private static readonly List<float> s_Stations = new List<float>(16);
        private static readonly List<MachinePos> s_Machines = new List<MachinePos>(32);
        private static readonly Dictionary<uint, ViewCache> s_Views = new Dictionary<uint, ViewCache>();
        private static readonly StringBuilder s_Sb = new StringBuilder(256);
        private static readonly List<float3> s_Hulls = new List<float3>(8);   // per dropped lateral: (centre, first, last) station
        private static EntityQuery s_MachineQuery;
        private static World s_MachineWorld;
        private static uint s_MachinesUpdate = uint.MaxValue;

        // Something to do this update (tracked edges, released markers, pending refreshes).
        public static bool Busy => TrafficState.Upgrade.Count > 0 || TrafficState.UpgradeRefresh.Count > 0;

        public static bool HasMarkers(Entity edge) => TrafficState.Upgrade.TryGetValue(edge, out var et) && et.Lanes.Count > 0;

        public static void Reset()
        {
            s_Views.Clear();
            s_Machines.Clear();
            s_MachinesUpdate = uint.MaxValue;
        }

        // ---------------------------------------------------------------------------------------------- the per-update step (Mod1)
        public static void Step(EntityManager em, World world, uint upd, in TrafficSettings s)
        {
            RunRefreshes(em, upd);
            foreach (var kv in SiteRegistry.Edges) SyncEdge(em, world, kv.Key, kv.Value, s, upd);
            // edges that left the registry: release what they still carry
            s_Keys.Clear();
            foreach (var kv in TrafficState.Upgrade) s_Keys.Add(kv.Key);
            for (int i = 0; i < s_Keys.Count; i++)
            {
                var et = TrafficState.Upgrade[s_Keys[i]];
                if (!SiteRegistry.Edges.ContainsKey(et.Edge) && (et.Want || HasActive(et) || ParkingInUse(et)))
                    ReleaseAll(em, et, upd, "site removed");
                for (int k = 0; k < et.Lanes.Count; k++) WatchRollers(et.Lanes[k], upd);
                ProcessReleases(em, et, upd);
                ProcessParkingReturns(em, et, upd);
                if (et.Lanes.Count == 0 && et.ParkingOff.Count == 0 && !SiteRegistry.Edges.ContainsKey(et.Edge)) TrafficState.Upgrade.Remove(et.Edge);
            }
            TrafficDetour.Step(em, upd);
        }

        // Upgrade view of a project this update (cached per project; ProjectRecord.View, so AppliedWindow / AppliedTraffic agree with
        // the Director's stage). False for every project that is not mode H with upgrade data.
        public static bool ViewOf(ProjectRecord proj, uint upd, out UpgradeView uv)
        {
            uv = default;
            if (proj == null || proj.Mode != VisualMode.HalfWidth || proj.Upgrade == null) return false;
            if (!s_Views.TryGetValue(proj.Id, out var c)) { c = new ViewCache { Update = upd - 1 }; s_Views[proj.Id] = c; }
            if (c.Update != upd)
            {
                c.Update = upd;
                // the project view: its Applied* fields follow the switch step (the previous window stays applied while its machines leave)
                c.View = proj.View().Upgrade;
                c.Upgrade = c.View.WindowCount > 0;
            }
            uv = c.View;
            return c.Upgrade;
        }

        private static EdgeArc ArcOf(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec)
        {
            if (rec != null && rec.Arc != null) return rec.Arc;
            int revision = rec != null ? rec.GeometryRevision : -1;
            if (et.OwnArc == null || et.OwnArcRevision != revision)
            {
                if (!em.Exists(et.Edge) || !em.HasComponent<Curve>(et.Edge)) return et.OwnArc;
                et.OwnArc = new EdgeArc(em.GetComponentData<Curve>(et.Edge).m_Bezier);
                et.OwnArcRevision = revision;
            }
            return et.OwnArc;
        }

        private static void SyncEdge(EntityManager em, Unity.Entities.World world, Entity edge, EdgeRecord rec, in TrafficSettings s, uint upd)
        {
            var st = rec.Upgrade;
            TrafficState.Upgrade.TryGetValue(edge, out var et);
            if (st == null)
            {
                if (et != null && (et.Want || HasActive(et) || ParkingInUse(et))) ReleaseAll(em, et, upd, "the edge is no longer an upgrade site");
                return;
            }
            if (!TrafficUtil.Alive(em, edge) || !em.HasComponent<RoadWorksRuntime>(edge)) return;
            if (et == null)
            {
                et = new EdgeUpgradeTraffic { Edge = edge, ProjectId = rec.ProjectId };
                TrafficState.Upgrade[edge] = et;
            }
            if (et.ProjectId != rec.ProjectId) ProjectChanged(em, et, rec, upd);
            SiteRegistry.TryGetProject(rec.ProjectId, out var proj);
            bool h = ViewOf(proj, upd, out var uv);
            var slot = rec.Get<TrafficEdgeSlot>(ModuleSlot.Traffic);
            var rt = em.GetComponentData<RoadWorksRuntime>(edge);
            var arc = ArcOf(em, et, rec);
            if (arc == null) return;

            SyncDrops(em, world, et, rec, st, proj, h, uv, slot, rt, arc, s, upd);
            SyncParking(em, et, rec, st, proj, h, uv, slot, rt, arc, s, upd);
            if (TrafficState.Closures.TryGetValue(edge, out var cl)) cl.ParkingOff = et.ParkingOff;
        }

        // ---------------------------------------------------------------------------------------------- lane drops
        private static string DropWhy(EdgeRecord rec, ProjectRecord proj, bool h, in UpgradeView uv, TrafficEdgeSlot slot, in RoadWorksRuntime rt, in TrafficSettings s)
        {
            if (!h) return "not an upgrade project";
            if (!s.ClosureLayer) return "closure layer off";
            if (!RRWGates.UpgradeDrop) return "lane drops switched off";
            if (proj.Releasing || proj.Phase == WorksPhase.Complete) return "works complete";
            if (rt.m_ClosureTarget == ClosureLevel.Open) return "road open";
            if (uv.AppliedTraffic != BandTraffic.Drop && !(uv.AllAtOnce && !uv.InTeardown && LaterDrop(uv))) return "window runs as " + uv.AppliedTraffic;
            int buildings = Math.Max(slot != null ? slot.Buildings : 0, rec.BuildingCount);
            if (buildings > 0)
            {
                RRWLog.Once("traffic-drop-buildings-p" + rec.ProjectId + "-e" + rec.Edge.Index,
                            "WARN traffic p" + rec.ProjectId + " e" + rec.Edge.Index + ": a lane drop was asked for next to " + buildings +
                            " connected building(s); lanes stay open there (cars enter a dropped lane from parking and driveways)");
                return "buildings along the edge";
            }
            return null;
        }

        private static bool LaterDrop(in UpgradeView uv)
        {
            for (int w = uv.AppliedWindow; w < uv.WindowCount; w++) if (uv.Prim(w) == BandTraffic.Drop) return true;
            return false;
        }

        private static bool HasActive(EdgeUpgradeTraffic et)
        {
            for (int i = 0; i < et.Lanes.Count; i++) if (et.Lanes[i].Stage != DropStage.Releasing) return true;
            return false;
        }

        private static void SyncDrops(EntityManager em, World world, EdgeUpgradeTraffic et, EdgeRecord rec, UpgradeEdgeState st, ProjectRecord proj,
                                      bool h, in UpgradeView uv, TrafficEdgeSlot slot, in RoadWorksRuntime rt, EdgeArc arc, in TrafficSettings s, uint upd)
        {
            string why = DropWhy(rec, proj, h, uv, slot, rt, s);
            bool want = why == null;
            int lw = want ? uv.AppliedWindow : -2;
            bool aao = h && uv.AllAtOnce;
            bool lanesDied = false;
            for (int i = 0; i < st.DropLanes.Count && !lanesDied; i++) lanesDied = !TrafficUtil.Alive(em, st.DropLanes[i]);
            bool keyChanged = !et.Resolved || et.Want != want || et.Geo != rec.GeometryRevision || et.Window != lw || et.AllAtOnce != aao ||
                              et.Tail != st.TailRevision || lanesDied || (want && !st.DropCurrent(rec.GeometryRevision, lw, aao));
            if (!want)
            {
                if (keyChanged || st.DropWindow != -2 || st.DropLanes.Count > 0)
                {
                    bool had = HasActive(et);
                    ReleaseActive(em, et, upd, why);
                    ResetReport(st, upd);
                    ClearSafeRange(et, st);
                    if (had) RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drops released (" + why + "): markers stay until no machine is near the lanes");
                }
                et.Resolved = true;
                et.Want = false;
                et.Geo = rec.GeometryRevision;
                et.Window = -2;
                et.AllAtOnce = aao;
                et.Tail = st.TailRevision;
                et.Why = why;
                return;
            }
            if (keyChanged) Resolve(em, et, rec, st, slot, arc, lw, aao, upd);
            Maintain(em, world, et, rec, st, arc, rt, upd);
            Report(em, et, rec, st, proj, arc, uv, upd);
        }

        // Re-resolves the dropped lanes for the current key and syncs the lane records (new lanes pending, lanes no longer dropped
        // released). Writes the key; a changed lane set resets DropApplied, BlockersRegistered and DropCleanChecks.
        private static void Resolve(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, UpgradeEdgeState st, TrafficEdgeSlot slot, EdgeArc arc,
                                    int lw, bool aao, uint upd)
        {
            s_Drive.Clear();
            TrafficUtil.LanesInto(em, et.Edge, s_Lanes);
            bool hasF = false, hasB = false, keepF = false, keepB = false;
            for (int i = 0; i < s_Lanes.Count; i++)
            {
                var lane = s_Lanes[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane)) continue;
                // measured as the section and every other module measure lanes, so all agree on which band a lane lies in
                if (!EcsUtil.ProbeLane(em, lane, arc, out var probe)) continue;
                var bits = probe.Bits;
                if ((bits & LaneBits.Car) == 0 || (bits & (LaneBits.Master | LaneBits.Parking | LaneBits.BikeOnly | LaneBits.Twoway | LaneBits.Point)) != 0) continue;
                if (!probe.IsDrive) continue;
                bool dropped = st.DropBandOf(probe.Centre, lw, aao) >= 0;
                s_Drive.Add(new DriveLane { Lane = lane, Centre = probe.Centre, HalfWidth = probe.HalfWidth, Dir = probe.Dir, Dropped = dropped });
                if (probe.Dir > 0) { hasF = true; keepF |= !dropped; }
                else if (probe.Dir < 0) { hasB = true; keepB |= !dropped; }
            }
            string none = null;
            if ((hasF && !keepF) || (hasB && !keepB))
            {
                none = "a travel direction would lose its last lane";
                RRWLog.Once("traffic-drop-keep-p" + rec.ProjectId + "-e" + rec.Edge.Index + "-w" + lw,
                            "WARN traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " window " + lw + ": dropping the band's lanes would close the last lane of a " +
                            "travel direction; no lane is dropped on this edge (machines stay out of its lanes)");
            }
            bool changed = false;
            for (int i = et.Lanes.Count - 1; i >= 0; i--)
            {
                var ld = et.Lanes[i];
                if (ld.Stage == DropStage.Releasing) continue;
                if (none == null && IsDropped(ld.Lane))
                {
                    if (ld.Stage == DropStage.Failed) { ld.Stage = DropStage.Pending; ld.Tries = 0; ld.Why = ""; }
                    continue;
                }
                changed = true;
                if (!TrafficUtil.Alive(em, ld.Lane)) RemoveNow(em, et, ld, upd, "lane regenerated", false);
                else Release(et, ld, upd, "no longer in a dropped band (window " + lw + ")");
            }
            st.DropLanes.Clear();
            st.DropLaneCentres.Clear();
            if (none == null)
            {
                for (int i = 0; i < s_Drive.Count; i++)
                {
                    var d = s_Drive[i];
                    if (!d.Dropped) continue;
                    st.DropLanes.Add(d.Lane);
                    st.DropLaneCentres.Add(d.Centre);
                    if (et.Find(d.Lane, false) != null) continue;
                    changed = true;
                    var back = et.Find(d.Lane, true);
                    if (back != null)
                    {
                        // dropped again while its markers were waiting for the machines to leave: check them again
                        back.Stage = DropStage.Placed;
                        back.StageUpdate = upd;
                        back.Tries = 0;
                        if (back.ProjectId != rec.ProjectId)
                        {
                            // another project drops it now: keep watching the rollers of the one that dropped it before
                            back.PrevProjectId = back.ProjectId;
                            back.PrevSeenSim = back.SeenSim;
                            back.PrevRollersSim = back.RollersSim;
                            back.SeenSim = back.RollersSim = 0;
                            back.ProjectId = rec.ProjectId;
                        }
                        back.HandOver = false;
                        back.Why = "";
                        continue;
                    }
                    var half = slot != null ? slot.Groups.Of(em, d.Lane) & RoadZones.Carriageway : RoadZones.None;
                    et.Lanes.Add(new LaneDrop
                    {
                        Lane = d.Lane, Centre = d.Centre, HalfWidth = d.HalfWidth, Half = half == RoadZones.None ? RoadZones.Carriageway : half,
                        ProjectId = rec.ProjectId, Stage = DropStage.Pending, StageUpdate = upd,
                    });
                }
            }
            if (changed)
            {
                st.DropApplied = false;
                st.BlockersRegistered = false;
                st.DropCleanChecks = 0;
                st.DropIntrusion = false;
                ClearSafeRange(et, st);
                et.LoggedReady = false;
            }
            st.DropLanesRevision = rec.GeometryRevision;
            st.DropWindow = lw;
            st.DropAllAtOnce = aao;
            st.DropTail = st.TailRevision;
            st.DropReportUpdate = upd;
            et.Resolved = true;
            et.Want = true;
            et.Geo = rec.GeometryRevision;
            et.Window = lw;
            et.AllAtOnce = aao;
            et.Tail = st.TailRevision;
            et.Why = none ?? "";
            et.NextCheck = upd + (uint)RRWConst.kDrainCheckUpdates;
            if (changed)
            {
                s_Sb.Clear();
                for (int i = 0; i < s_Drive.Count; i++) if (s_Drive[i].Dropped) s_Sb.Append(s_Sb.Length > 0 ? "," : "").Append(RRWLog.F(s_Drive[i].Centre));
                RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop window " + lw + (aao ? " (all at once)" : "") + ": " +
                            (none != null ? "none (" + none + ")" : st.DropLanes.Count + " lane(s) at lateral [" + s_Sb + "] of " + s_Drive.Count + " drive lanes") +
                            " geo=" + rec.GeometryRevision + " tail=" + st.TailRevision);
            }
        }

        private static bool IsDropped(Entity lane)
        {
            for (int i = 0; i < s_Drive.Count; i++) if (s_Drive[i].Lane == lane) return s_Drive[i].Dropped;
            return false;
        }

        // A lane of this edge that is dropped now and gets markers of its own (markers may register on it as well). A lane that
        // could not be dropped, or is being released, counts as open.
        private static bool IsDropLane(EdgeUpgradeTraffic et, Entity lane)
        {
            for (int i = 0; i < et.Lanes.Count; i++)
            {
                var ld = et.Lanes[i];
                if (ld.Lane == lane && ld.Stage != DropStage.Releasing && ld.Stage != DropStage.Failed) return true;
            }
            return false;
        }

        private static void ResetReport(UpgradeEdgeState st, uint upd)
        {
            st.DropLanes.Clear();
            st.DropLaneCentres.Clear();
            st.DropSafeU0 = st.DropSafeU1 = float.NaN;
            st.DropWindow = -2;
            st.DropApplied = false;
            st.BlockersRegistered = false;
            st.DropIntrusion = false;
            st.DropCleanChecks = 0;
            st.DropReportUpdate = upd;
        }

        // Stage machine of the dropped lanes.
        private static void Maintain(EntityManager em, World world, EdgeUpgradeTraffic et, EdgeRecord rec, UpgradeEdgeState st, EdgeArc arc,
                                     in RoadWorksRuntime rt, uint upd)
        {
            bool othersReady = false;
            for (int i = 0; i < et.Lanes.Count; i++)
            {
                var ld = et.Lanes[i];
                switch (ld.Stage)
                {
                    case DropStage.Pending:
                        if (!othersReady) { CollectOthers(em, et, arc); othersReady = true; }
                        Place(em, world, et, rec, ld, arc, upd);
                        break;
                    case DropStage.Placed:
                        if (ld.StageUpdate != upd) CheckRegistration(em, et, rec, ld, upd);
                        break;
                    case DropStage.Refreshed:
                        if (ld.StageUpdate != upd) CheckBlockage(em, et, rec, ld, upd);
                        break;
                    case DropStage.Active:
                        int lost = 0;
                        for (int k = 0; k < ld.Markers.Count; k++) if (!LaneClosureMarkers.Alive(em, ld.Markers[k])) lost++;
                        if (lost > 0)
                        {
                            RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop at lateral " + RRWLog.F(ld.Centre) + ": " + lost +
                                        " marker(s) were removed from outside; placing them again");
                            DeleteMarkers(em, et, ld, upd);
                            ld.Stage = DropStage.Pending;
                            ld.StageUpdate = upd;
                        }
                        break;
                }
            }
            // edge flags: applied = every dropped lane carries markers, registered = every one of them is active
            bool applied = st.DropLanes.Count > 0, registered = applied;
            for (int i = 0; i < st.DropLanes.Count; i++)
            {
                var ld = et.Find(st.DropLanes[i], false);
                if (ld == null || ld.Stage == DropStage.Pending || ld.Stage == DropStage.Failed) { applied = false; registered = false; }
                else if (ld.Stage != DropStage.Active) registered = false;
            }
            if (applied != st.DropApplied || registered != st.BlockersRegistered) st.DropReportUpdate = upd;
            st.DropApplied = applied;
            if (!registered) st.DropCleanChecks = 0;
            if (registered && !st.BlockersRegistered)
                RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop window " + et.Window + ": markers registered on " + st.DropLanes.Count +
                            " dropped lane(s) only (" + et.MarkerCount + " markers)");
            st.BlockersRegistered = registered;
            WriteSafeRange(em, et, rec, st, arc, rt, applied, upd);
        }

        private static void ClearSafeRange(EdgeUpgradeTraffic et, UpgradeEdgeState st)
        {
            et.SafeS0 = et.SafeS1 = float.NaN;
            et.SafeWhy = "";
            st.DropSafeU0 = st.DropSafeU1 = float.NaN;
        }

        private static void RemoveMarkerAt(LaneDrop ld, int k)
        {
            ld.Markers.RemoveAt(k);
            if (k < ld.Stations.Count) ld.Stations.RemoveAt(k);
        }

        // Arc distance (EDGE frame) of marker k of a lane: the station it was placed for; the projection of its position when
        // the station is not known.
        private static bool MarkerStation(EntityManager em, LaneDrop ld, int k, EdgeArc arc, out float s)
        {
            s = float.NaN;
            var m = ld.Markers[k];
            if (!LaneClosureMarkers.Alive(em, m)) return false;
            if (ld.Stations.Count == ld.Markers.Count) s = ld.Stations[k];
            else if (em.HasComponent<ObjTransform>(m)) s = arc.Project(em.GetComponentData<ObjTransform>(m).m_Position);
            return !float.IsNaN(s);
        }

        // The stretch every dropped lane of the edge is blocked over: from the innermost first marker to the innermost last
        // marker (a station that was skipped or a marker that was removed moves it inward). Cars may use a dropped lane up to
        // its first marker, so machines keep inside this range (DropSafeU0/U1, chain frame: the same chain coordinates the
        // machines' track uses) and the drain check looks only there. NaN while some dropped lane has no markers, or when the
        // dropped lanes share no blocked stretch (et.SafeWhy says why; Report then never counts the drop ready).
        private static void WriteSafeRange(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, UpgradeEdgeState st, EdgeArc arc,
                                           in RoadWorksRuntime rt, bool applied, uint upd)
        {
            float s0 = float.NegativeInfinity, s1 = float.PositiveInfinity;
            string why = null;
            if (!applied || arc == null) why = "";
            // A long lane is made of several lane pieces one after the other, each with its own markers. Every piece of a dropped
            // lane carries markers, so the route planner avoids all of them: per lateral the lane is blocked over the hull of its
            // pieces' marker stations. The edge's range is where every dropped lateral is blocked.
            s_Hulls.Clear();
            for (int i = 0; i < st.DropLanes.Count && why == null; i++)
            {
                var ld = et.Find(st.DropLanes[i], false);
                if (ld == null) { why = ""; break; }
                float a = float.PositiveInfinity, b = float.NegativeInfinity;
                for (int k = 0; k < ld.Markers.Count; k++)
                {
                    if (!MarkerStation(em, ld, k, arc, out float sm)) continue;
                    a = math.min(a, sm);
                    b = math.max(b, sm);
                }
                if (!(b >= a)) { why = "the lane at lateral " + RRWLog.F(ld.Centre) + " has no live marker"; break; }
                int h = 0;
                while (h < s_Hulls.Count && math.abs(s_Hulls[h].x - ld.Centre) > RRWConst.kUwLaneTol) h++;
                if (h == s_Hulls.Count) s_Hulls.Add(new float3(ld.Centre, a, b));
                else s_Hulls[h] = new float3(s_Hulls[h].x, math.min(s_Hulls[h].y, a), math.max(s_Hulls[h].z, b));
            }
            for (int h = 0; h < s_Hulls.Count && why == null; h++)
            {
                s0 = math.max(s0, s_Hulls[h].y);
                s1 = math.min(s1, s_Hulls[h].z);
            }
            if (why == null && !(s1 > s0))
            {
                s_Sb.Clear();
                for (int i = 0; i < st.DropLanes.Count; i++)
                {
                    var ld = et.Find(st.DropLanes[i], false);
                    if (ld == null) continue;
                    float a = float.PositiveInfinity, b = float.NegativeInfinity;
                    for (int k = 0; k < ld.Markers.Count; k++)
                        if (MarkerStation(em, ld, k, arc, out float sm)) { a = math.min(a, sm); b = math.max(b, sm); }
                    s_Sb.Append(s_Sb.Length > 0 ? " " : "").Append(RRWLog.F(ld.Centre)).Append(":[").Append(RRWLog.F(a)).Append(',').Append(RRWLog.F(b)).Append(']');
                }
                why = "the dropped lanes share no stretch between their first and last markers (lateral:[first,last] m " + s_Sb + ")";
            }
            // chain coordinates of the edge: the saved site, else the runtime copy of them
            float cu0 = float.NaN, cu1 = float.NaN;
            if (why == null)
            {
                if (em.HasComponent<RoadWorksSite>(et.Edge))
                {
                    var site = em.GetComponentData<RoadWorksSite>(et.Edge);
                    cu0 = site.m_ChainU0;
                    cu1 = site.m_ChainU1;
                }
                if (float.IsNaN(cu0) || float.IsNaN(cu1) || math.abs(cu1 - cu0) < 1e-3f)
                {
                    cu0 = rt.m_ChainU0;
                    cu1 = rt.m_ChainU1;
                }
                if (float.IsNaN(cu0) || float.IsNaN(cu1) || math.abs(cu1 - cu0) < 1e-3f)
                    why = "the edge has no chain coordinates (u0=" + RRWLog.F(cu0) + " u1=" + RRWLog.F(cu1) + ")";
            }
            if (why != null)
            {
                bool had = !float.IsNaN(st.DropSafeU0);
                ClearSafeRange(et, st);
                et.SafeWhy = why;
                if (had) st.DropReportUpdate = upd;
                if (why.Length > 0 && st.BlockersRegistered)
                    RRWLog.Once("traffic-drop-range-p" + rec.ProjectId + "-e" + rec.Edge.Index + "-w" + et.Window + "-g" + rec.GeometryRevision + "-t" + st.TailRevision,
                                "WARN traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop window " + et.Window + ": no blocked range for the machines: " + why +
                                "; the drop is not reported ready (machines stay out of the dropped lanes)");
                return;
            }
            float u0 = PhasePlan.ChainU(s0, cu0, cu1, arc.Length);
            float u1 = PhasePlan.ChainU(s1, cu0, cu1, arc.Length);
            float lo = math.min(u0, u1), hi = math.max(u0, u1);
            bool moved = !(math.abs(lo - st.DropSafeU0) < 0.01f && math.abs(hi - st.DropSafeU1) < 0.01f);
            et.SafeS0 = s0;
            et.SafeS1 = s1;
            et.SafeWhy = "";
            st.DropSafeU0 = lo;
            st.DropSafeU1 = hi;
            if (!moved) return;
            st.DropReportUpdate = upd;
            float setback = RRWConst.kUwBlockerNodeSetback;
            RRWLog.Verbose("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop blocked over [" + RRWLog.F(s0) + ", " + RRWLog.F(s1) + "] m of " +
                           RRWLog.F(arc.Length) + " m = chain u [" + RRWLog.F(lo) + ", " + RRWLog.F(hi) + "]" +
                           (s0 > setback + 1f || s1 < arc.Length - setback - 1f ? " (a marker station near a node was skipped or removed): machines keep inside it" : ""));
        }

        // Lanes of the edge that must stay clear of the markers: every lane that is not dropped now (car, parking, pedestrian, track).
        private static void CollectOthers(EntityManager em, EdgeUpgradeTraffic et, EdgeArc arc)
        {
            s_Others.Clear();
            TrafficUtil.LanesInto(em, et.Edge, s_Lanes);
            for (int i = 0; i < s_Lanes.Count; i++)
            {
                var lane = s_Lanes[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane) || !em.HasComponent<Curve>(lane)) continue;
                if (IsDropLane(et, lane) || em.HasComponent<NetMasterLane>(lane)) continue;
                bool car = em.HasComponent<NetCarLane>(lane), park = em.HasComponent<NetParkingLane>(lane);
                bool ped = em.HasComponent<NetPedestrianLane>(lane), track = em.HasComponent<NetTrackLane>(lane);
                if (!car && !park && !ped && !track) continue;
                float width = park ? 2.5f : ped ? 2f : 3f;
                if (em.HasComponent<Game.Prefabs.PrefabRef>(lane))
                {
                    var lp = em.GetComponentData<Game.Prefabs.PrefabRef>(lane).m_Prefab;
                    if (em.HasComponent<Game.Prefabs.NetLaneData>(lp)) width = em.GetComponentData<Game.Prefabs.NetLaneData>(lp).m_Width;
                }
                s_Others.Add(new OtherLane { Curve = em.GetComponentData<Curve>(lane).m_Bezier, HalfWidth = width * 0.5f });
            }
        }

        // Marker position at a lane point: the lane centre, shifted away from an open lane when the marker would come closer than
        // r + kDropOpenLaneClear to its edge; false when no such position stays on the dropped lane.
        private static bool ClearPosition(float3 lanePoint, float3 right, float halfWidth, out float3 pos)
        {
            float r = RRWConst.kBlockerRadius, need = r + TrafficConst.kDropOpenLaneClear;
            float off = 0f;
            pos = lanePoint;
            for (int iter = 0; iter < 3; iter++)
            {
                float worst = 0f, sign = 0f;
                for (int k = 0; k < s_Others.Count; k++)
                {
                    float d = MathUtils.Distance(s_Others[k].Curve, pos, out float t);
                    float gap = d - s_Others[k].HalfWidth;
                    if (gap >= need || need - gap <= worst) continue;
                    worst = need - gap;
                    float3 near = MathUtils.Position(s_Others[k].Curve, t);
                    sign = math.dot((near - pos).xz, right.xz) > 0f ? -1f : 1f;
                }
                if (worst <= 0f) return true;
                off += sign * (worst + 0.05f);
                if (math.abs(off) > halfWidth - r) return false;
                pos = lanePoint + right * off;
            }
            return false;
        }

        // Arc distances of the markers on one lane: from the node setback to length - setback, evenly spaced at most kBlockerSpacing
        // apart (both ends included); none on an edge shorter than twice the setback.
        public static void Stations(float length, float setback, float spacing, List<float> output)
        {
            output.Clear();
            float s0 = setback, s1 = length - setback;
            if (s1 < s0) return;
            if (s1 - s0 < 0.5f) { output.Add((s0 + s1) * 0.5f); return; }
            int n = (int)math.ceil((s1 - s0) / math.max(1f, spacing)) + 1;
            for (int k = 0; k < n; k++) output.Add(math.lerp(s0, s1, k / (float)(n - 1)));
        }

        private static void Place(EntityManager em, World world, EdgeUpgradeTraffic et, EdgeRecord rec, LaneDrop ld, EdgeArc arc, uint upd)
        {
            if (!TrafficUtil.Alive(em, ld.Lane) || !em.HasComponent<Curve>(ld.Lane)) return;    // the next resolve drops it
            var prefab = LaneClosurePrefab.Ensure(world, em, out string why, out bool missing);
            if (prefab == Entity.Null)
            {
                if (missing) { Fail(em, et, rec, ld, upd, why); return; }
                if (ld.Why != why) RRWLog.Verbose("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop waits: " + why);
                ld.Why = why;
                return;
            }
            var bez = em.GetComponentData<Curve>(ld.Lane).m_Bezier;
            float laneLen = MathUtils.Length(bez);
            Stations(arc.Length, RRWConst.kUwBlockerNodeSetback, RRWConst.kBlockerSpacing, s_Stations);
            int tooClose = 0, made = 0;
            for (int k = 0; k < s_Stations.Count; k++)
            {
                float3 ap = arc.Position(s_Stations[k]);
                MathUtils.Distance(bez, ap, out float t);
                if (t <= 0.001f || t >= 0.999f) continue;
                float d0 = MathUtils.Length(bez, new Bounds1(0f, t));
                if (d0 < RRWConst.kBlockerNodeClear || laneLen - d0 < RRWConst.kBlockerNodeClear) continue;
                float3 lp = MathUtils.Position(bez, t);
                float3 right = arc.Right(arc.Project(lp));
                if (!ClearPosition(lp, right, ld.HalfWidth, out float3 pos)) { tooClose++; continue; }
                float3 tan = MathUtils.Tangent(bez, t);
                tan.y = 0f;
                var rot = quaternion.LookRotationSafe(math.normalizesafe(tan, new float3(0f, 0f, 1f)), math.up());
                ld.Markers.Add(LaneClosureMarkers.Create(em, prefab, pos, rot, et.Edge, ld.Lane, rec.ProjectId));
                ld.Stations.Add(s_Stations[k]);
                made++;
            }
            if (made == 0)
            {
                Fail(em, et, rec, ld, upd, s_Stations.Count == 0
                    ? "edge shorter than twice the node setback (" + RRWLog.F(arc.Length) + " m)"
                    : "no marker position keeps clear of the open lanes (" + tooClose + " of " + s_Stations.Count + " too close)");
                return;
            }
            TrafficState.MarkersPlaced += made;
            ld.Registered.Clear();
            ld.Stage = DropStage.Placed;
            ld.StageUpdate = upd;
            ld.Tries = 0;
            ld.Why = "";
            RRWLog.Verbose("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop: " + made + " marker(s) on lane " + RRWLog.E(ld.Lane) +
                           " lateral " + RRWLog.F(ld.Centre) + (tooClose > 0 ? " (" + tooClose + " position(s) too close to an open lane skipped)" : ""));
        }

        // The update after placing: every marker must be registered on its lane, and on no lane that stays open (that would close the
        // whole direction: such a marker is removed at once). Then the lanes are refreshed so the game computes their blockage.
        private static void CheckRegistration(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, LaneDrop ld, uint upd)
        {
            int onTarget = 0, bad = 0;
            for (int k = ld.Markers.Count - 1; k >= 0; k--)
            {
                var m = ld.Markers[k];
                if (!LaneClosureMarkers.Alive(em, m)) { RemoveMarkerAt(ld, k); continue; }
                s_Tmp.Clear();
                LaneClosureMarkers.RegisteredLanes(em, m, s_Tmp);
                bool hit = false, wrong = false;
                for (int j = 0; j < s_Tmp.Count; j++)
                {
                    var l = s_Tmp[j];
                    if (!ld.Registered.Contains(l)) ld.Registered.Add(l);
                    if (l == ld.Lane) hit = true;
                    else if (!IsDropLane(et, l)) wrong = true;
                }
                if (wrong)
                {
                    LaneClosureMarkers.Delete(em, m);
                    RemoveMarkerAt(ld, k);
                    bad++;
                }
                else if (hit) onTarget++;
            }
            if (bad > 0)
            {
                TrafficState.MarkersMisregistered += bad;
                ScheduleRefresh(ld.Registered, et.Edge, upd);
                RRWLog.Warn("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop at lateral " + RRWLog.F(ld.Centre) + ": " + bad +
                            " marker(s) also registered on a lane that stays open (that would close the whole direction): removed");
            }
            if (onTarget == 0)
            {
                ld.Tries++;
                if (ld.Tries >= TrafficConst.kDropRegisterTries) { Fail(em, et, rec, ld, upd, "the game did not register the markers on the lane"); return; }
                if (ld.Markers.Count == 0) { ld.Stage = DropStage.Pending; ld.StageUpdate = upd; return; }
                if ((ld.Tries & 1) == 0)
                    for (int k = 0; k < ld.Markers.Count; k++)
                        if (LaneClosureMarkers.Alive(em, ld.Markers[k]) && !em.HasComponent<Updated>(ld.Markers[k])) em.AddComponent<Updated>(ld.Markers[k]);
                return;
            }
            for (int j = 0; j < ld.Registered.Count; j++)
                if (TrafficUtil.Alive(em, ld.Registered[j]) && !em.HasComponent<PathfindUpdated>(ld.Registered[j])) em.AddComponent<PathfindUpdated>(ld.Registered[j]);
            TrafficUtil.RefreshEdge(em, et.Edge, true, out _);
            ld.Stage = DropStage.Refreshed;
            ld.StageUpdate = upd;
            ld.Tries = 0;
        }

        // The update after the refresh: the dropped lane must now carry the blockage of its markers.
        private static void CheckBlockage(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, LaneDrop ld, uint upd)
        {
            if (!TrafficUtil.Alive(em, ld.Lane) || !em.HasComponent<NetCarLane>(ld.Lane)) return;
            var v = LaneVals.Of(em.GetComponentData<NetCarLane>(ld.Lane));
            if (v.Blocked)
            {
                ld.Stage = DropStage.Active;
                ld.StageUpdate = upd;
                ld.Tries = 0;
                RRWLog.Verbose("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop at lateral " + RRWLog.F(ld.Centre) + " active: " + v.Text +
                               " markers=" + ld.Markers.Count + " registeredLanes=" + ld.Registered.Count);
                return;
            }
            ld.Tries++;
            if (ld.Tries >= TrafficConst.kDropBlockageTries) { Fail(em, et, rec, ld, upd, "the lane never showed the blockage of its markers"); return; }
            if ((ld.Tries & 3) == 0 && !em.HasComponent<PathfindUpdated>(ld.Lane)) em.AddComponent<PathfindUpdated>(ld.Lane);
        }

        private static void Fail(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, LaneDrop ld, uint upd, string why)
        {
            DeleteMarkers(em, et, ld, upd);
            ld.Stage = DropStage.Failed;
            ld.StageUpdate = upd;
            ld.Why = why;
            TrafficState.DropFailures++;
            RRWLog.Once("traffic-drop-fail-e" + rec.Edge.Index + "-" + RRWLog.E(ld.Lane),
                        "WARN traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane at lateral " + RRWLog.F(ld.Centre) + " cannot be dropped: " + why +
                        " (it stays open; machines stay out of it)");
        }

        private static void DeleteMarkers(EntityManager em, EdgeUpgradeTraffic et, LaneDrop ld, uint upd)
        {
            int n = 0;
            for (int k = 0; k < ld.Markers.Count; k++)
            {
                var m = ld.Markers[k];
                if (!em.Exists(m)) continue;
                LaneClosureMarkers.RegisteredLanes(em, m, ld.Registered);
                LaneClosureMarkers.Delete(em, m);
                n++;
            }
            ld.Markers.Clear();
            ld.Stations.Clear();
            TrafficState.MarkersRemoved += n;
            if (!ld.Registered.Contains(ld.Lane)) ld.Registered.Add(ld.Lane);
            ScheduleRefresh(ld.Registered, et.Edge, upd);
            ld.Registered.Clear();
        }

        // Drain check every kDrainCheckUpdates: any vehicle on a dropped lane inside the blocked range (the node setback at both
        // ends while there is none) is an intrusion; clean checks count only while the markers are registered and the blocked
        // range is written (DropSafeU0/U1: machines must know where the dropped lanes are free of cars).
        private static void Report(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, UpgradeEdgeState st, ProjectRecord proj, EdgeArc arc,
                                   in UpgradeView uv, uint upd)
        {
            if ((int)(upd - et.NextCheck) < 0) return;
            et.NextCheck = upd + (uint)RRWConst.kDrainCheckUpdates;
            int n = Intruders(em, arc, st.DropLanes, et.SafeS0, et.SafeS1, out string first);
            bool was = st.DropIntrusion;
            st.DropIntrusion = n > 0;
            bool range = st.DropSafeRange(out _, out _);
            st.DropCleanChecks = st.BlockersRegistered && range && n == 0 ? Math.Min(st.DropCleanChecks + 1, 1 << 20) : 0;
            st.DropReportUpdate = upd;
            if (n > 0 && !was)
                RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop intrusion: " + n + " vehicle(s) on a dropped lane past the node setback (" + first +
                            "); machines hold until two clean checks");
            else if (n == 0 && was) RRWLog.Verbose("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop clean again");
            bool ready = st.DropReadyFor(rec.GeometryRevision, uv);
            if (ready && !et.LoggedReady)
            {
                et.LoggedReady = true;
                RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " lane drop ready for machines: " + st.DropLanes.Count + " lane(s), " + et.MarkerCount +
                            " markers, " + st.DropCleanChecks + " clean checks (window " + et.Window + ", blocked over [" + RRWLog.F(et.SafeS0) + ", " +
                            RRWLog.F(et.SafeS1) + "] m of the edge = chain u [" + RRWLog.F(st.DropSafeU0) + ", " + RRWLog.F(st.DropSafeU1) + "])");
            }
            else if (!ready && et.LoggedReady && !st.BlockersRegistered) et.LoggedReady = false;
        }

        public static int Intruders(EntityManager em, EdgeArc arc, List<Entity> lanes, out string first) =>
            Intruders(em, arc, lanes, float.NaN, float.NaN, out first);

        // Vehicles on the lanes between arc distances s0 and s1 (NaN: the node setback from both ends).
        public static int Intruders(EntityManager em, EdgeArc arc, List<Entity> lanes, float s0, float s1, out string first)
        {
            first = null;
            int n = 0;
            if (arc == null) return 0;
            float len = arc.Length;
            float lo = float.IsNaN(s0) ? RRWConst.kUwBlockerNodeSetback : s0;
            float hi = float.IsNaN(s1) ? len - RRWConst.kUwBlockerNodeSetback : s1;
            for (int l = 0; l < lanes.Count; l++)
            {
                var lane = lanes[l];
                if (!TrafficUtil.Alive(em, lane) || !em.HasBuffer<LaneObject>(lane) || !em.HasComponent<Curve>(lane)) continue;
                var bez = em.GetComponentData<Curve>(lane).m_Bezier;
                var buf = em.GetBuffer<LaneObject>(lane, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var o = buf[i].m_LaneObject;
                    if (!em.Exists(o) || !em.HasComponent<Vehicle>(o) || em.HasComponent<ParkedCar>(o) || em.HasComponent<RRWMachine>(o)) continue;
                    float s = arc.Project(MathUtils.Position(bez, math.saturate(buf[i].m_CurvePosition.x)));
                    if (s < lo || s > hi) continue;
                    if (n == 0) first = RRWLog.E(o) + " at " + RRWLog.F(s) + " m";
                    n++;
                }
            }
            return n;
        }

        // ---------------------------------------------------------------------------------------------- releasing
        private static void Release(EdgeUpgradeTraffic et, LaneDrop ld, uint upd, string why)
        {
            if (ld.Markers.Count == 0) { et.Lanes.Remove(ld); return; }
            ld.Stage = DropStage.Releasing;
            ld.ReleaseUpdate = upd;
            ld.ReleaseSim = RRWClock.SimFrame;
            ld.Why = why;
        }

        private static void ReleaseActive(EntityManager em, EdgeUpgradeTraffic et, uint upd, string why)
        {
            for (int i = et.Lanes.Count - 1; i >= 0; i--)
            {
                var ld = et.Lanes[i];
                if (ld.Stage == DropStage.Releasing) continue;
                if (!TrafficUtil.Alive(em, ld.Lane)) RemoveNow(em, et, ld, upd, "lane regenerated", false);
                else Release(et, ld, upd, why);
            }
        }

        private static void ReleaseAll(EntityManager em, EdgeUpgradeTraffic et, uint upd, string why)
        {
            bool had = HasActive(et);
            ReleaseActive(em, et, upd, why);
            ReturnAllParking(em, et, upd, why);
            et.SafeS0 = et.SafeS1 = float.NaN;
            if (SiteRegistry.TryGetEdge(et.Edge, out var rec) && rec.Upgrade != null) ResetReport(rec.Upgrade, upd);
            et.Resolved = false;
            et.Want = false;
            et.Why = why;
            if (had) RRWLog.Info("traffic p" + et.ProjectId + " e" + et.Edge.Index + " lane drops released (" + why + "): markers stay until no machine is near the lanes");
        }

        // The edge moved to another project (re-upgrade merge, the works ended and a demolition started, a split): what the old
        // project dropped is released (its markers wait for its machines), the new project resolves its own drops.
        private static void ProjectChanged(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, uint upd)
        {
            uint old = et.ProjectId;
            ReleaseAll(em, et, upd, "the edge moved from p" + old + " to p" + rec.ProjectId);
            int kept = 0;
            for (int i = 0; i < et.Lanes.Count; i++)
            {
                var ld = et.Lanes[i];
                if (ld.Stage != DropStage.Releasing) continue;
                ld.HandOver = true;
                kept++;
            }
            et.ProjectId = rec.ProjectId;
            RRWLog.Info("traffic e" + et.Edge.Index + " moved from p" + old + " to p" + rec.ProjectId + ": lane drops and switched-off parking of p" + old + " released" +
                        (kept > 0 ? "; the markers of " + kept + " lane(s) stay until no machine of p" + old + " is near them (also while their lane group is closed)" : ""));
        }

        private static void ProcessReleases(EntityManager em, EdgeUpgradeTraffic et, uint upd)
        {
            for (int i = et.Lanes.Count - 1; i >= 0; i--)
            {
                var ld = et.Lanes[i];
                if (ld.Stage != DropStage.Releasing) continue;
                string done = ReleaseDone(em, et, ld, upd, out bool capped);
                if (done != null) RemoveNow(em, et, ld, upd, done, capped);
            }
        }

        private static string ReleaseDone(EntityManager em, EdgeUpgradeTraffic et, LaneDrop ld, uint upd, out bool capped)
        {
            capped = false;
            if (!TrafficUtil.Alive(em, et.Edge)) return "edge removed";
            if (!TrafficUtil.Alive(em, ld.Lane)) return "lane regenerated";
            // a closed lane group keeps cars out by itself, except after a hand-over: the new project may open the group while the
            // old project's machines are still in it
            if (!ld.HandOver && TrafficState.Closures.TryGetValue(et.Edge, out var cl) && !cl.IsDev && cl.Level == ClosureLevel.Closed && !cl.LaneIsOpen(em, ld.Lane))
                return "its lane group is closed";
            SiteRegistry.TryGetEdge(et.Edge, out var rec);
            var arc = ArcOf(em, et, rec);
            if (!MachinesNear(em, et, ld, arc, upd)) return "no machine near the lane";
            uint simAge = unchecked(RRWClock.SimFrame - ld.ReleaseSim);
            uint updAge = unchecked(upd - ld.ReleaseUpdate);
            if (simAge >= (uint)RRWConst.kCompletionMachineWaitSimFrames || updAge >= (uint)RRWConst.kCompletionMachineWaitUpdatesHard)
            {
                capped = true;
                return "machines still near the lane " + simAge + " sim frames after the release";
            }
            return null;
        }

        // A machine of any project stands on or next to the lane (puppet positions), or the report of the project that dropped the
        // lane (or dropped it before another project did) has rollers in the lane's half (rollers are not entities). When that
        // project is gone, or has no fresh report, rollers its last report showed there may still be leaving: they count until
        // kPostSiteMaxSimFrames after that report.
        private static bool MachinesNear(EntityManager em, EdgeUpgradeTraffic et, LaneDrop ld, EdgeArc arc, uint upd)
        {
            if (RollersMayBeNear(ld.ProjectId, ld.Half, ld.SeenSim, ld.RollersSim, upd, et, ld)) return true;
            // the earlier project's chain may run the other way: either half
            if (ld.PrevProjectId != 0 && ld.PrevProjectId != ld.ProjectId &&
                RollersMayBeNear(ld.PrevProjectId, RoadZones.Carriageway, ld.PrevSeenSim, ld.PrevRollersSim, upd, et, ld)) return true;
            return MachineBodyNear(em, arc, ld.Centre, ld.HalfWidth, upd);
        }

        private static bool RollersIn(ProjectRecord proj, RoadZones half) =>
            proj.RollersSpawned > 0 && (proj.MachineZones & half) != RoadZones.None;

        private static bool RollersMayBeNear(uint projectId, RoadZones half, uint seenSim, uint rollersSim, uint upd, EdgeUpgradeTraffic et, LaneDrop ld)
        {
            if (projectId == 0) return false;
            if (SiteRegistry.TryGetProject(projectId, out var proj) && proj.MachinesReportFresh(upd)) return RollersIn(proj, half);
            // gone (or no fresh report): only rollers that were there in its last report, and only while they may still be leaving
            if (seenSim == 0 || rollersSim != seenSim) return false;
            uint age = unchecked(RRWClock.SimFrame - seenSim);
            if (age >= (uint)RRWConst.kPostSiteMaxSimFrames) return false;
            RRWLog.Once("traffic-drop-rollers-e" + et.Edge.Index + "-" + RRWLog.E(ld.Lane) + "-p" + projectId,
                        "traffic p" + projectId + " e" + et.Edge.Index + " lane at lateral " + RRWLog.F(ld.Centre) + ": p" + projectId +
                        (proj == null ? " is gone" : " has no fresh machine report") + " and its last report had rollers on this side; the markers stay while they leave " +
                        "(at most " + (RRWConst.kPostSiteMaxSimFrames / 60) + " game seconds)");
            return true;
        }

        // Remembers when the project that dropped the lane (and the one that dropped it before) last reported, and whether its
        // rollers were on the lane's side then.
        private static void WatchRollers(LaneDrop ld, uint upd)
        {
            uint now = math.max(1u, RRWClock.SimFrame);
            if (ld.ProjectId != 0 && SiteRegistry.TryGetProject(ld.ProjectId, out var p) && p.MachinesReportFresh(upd))
            {
                ld.SeenSim = now;
                if (RollersIn(p, ld.Half)) ld.RollersSim = now;
            }
            if (ld.PrevProjectId != 0 && ld.PrevProjectId != ld.ProjectId && SiteRegistry.TryGetProject(ld.PrevProjectId, out var q) && q.MachinesReportFresh(upd))
            {
                ld.PrevSeenSim = now;
                if (RollersIn(q, RoadZones.Carriageway)) ld.PrevRollersSim = now;
            }
        }

        // The body of a machine of any project reaches within kDropMachineClear of the lane strip [centre - halfWidth,
        // centre + halfWidth] (EDGE frame). The body is the puppet's box across the road: its prefab's footprint turned with the
        // puppet, never wider than kDropMachineMaxHalf on either side of its root (puppets may be drawn scaled down; the cap
        // covers the widest machine, arm included); a puppet without geometry counts with the cap.
        private static bool MachineBodyNear(EntityManager em, EdgeArc arc, float centre, float halfWidth, uint upd)
        {
            if (arc == null) return false;
            CollectMachines(em, upd);
            float lo = centre - halfWidth - TrafficConst.kDropMachineClear, hi = centre + halfWidth + TrafficConst.kDropMachineClear;
            for (int i = 0; i < s_Machines.Count; i++)
            {
                var m = s_Machines[i];
                float s = arc.Project(m.Pos);
                if (s < -5f || s > arc.Length + 5f) continue;
                float3 at = arc.Position(s), right = arc.Right(s);
                float lat = math.dot((m.Pos - at).xz, right.xz);
                float a = lat - TrafficConst.kDropMachineMaxHalf, b = lat + TrafficConst.kDropMachineMaxHalf;
                if (m.HasBox)
                {
                    float ba = float.PositiveInfinity, bb = float.NegativeInfinity;
                    for (int c = 0; c < 4; c++)
                    {
                        float3 local = new float3((c & 1) == 0 ? m.Box.min.x : m.Box.max.x, 0f, (c & 2) == 0 ? m.Box.min.z : m.Box.max.z);
                        float cl = math.dot((m.Pos + math.mul(m.Rot, local) - at).xz, right.xz);
                        ba = math.min(ba, cl);
                        bb = math.max(bb, cl);
                    }
                    a = math.max(a, math.min(ba, lat));
                    b = math.min(b, math.max(bb, lat));
                }
                if (b > lo && a < hi) return true;
            }
            return false;
        }

        private static void CollectMachines(EntityManager em, uint upd)
        {
            if (s_MachinesUpdate == upd) return;
            s_MachinesUpdate = upd;
            s_Machines.Clear();
            if (s_MachineWorld != em.World)
            {
                s_MachineWorld = em.World;
                s_MachineQuery = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<RRWMachine>(), ComponentType.ReadOnly<ObjTransform>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>() },
                });
            }
            if (s_MachineQuery.IsEmptyIgnoreFilter) return;
            var ents = s_MachineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var tr = em.GetComponentData<ObjTransform>(ents[i]);
                    var mp = new MachinePos { Pos = tr.m_Position, Rot = tr.m_Rotation, ProjectId = em.GetComponentData<RRWMachine>(ents[i]).m_ProjectId };
                    if (em.HasComponent<PrefabRef>(ents[i]))
                    {
                        var prefab = em.GetComponentData<PrefabRef>(ents[i]).m_Prefab;
                        if (prefab != Entity.Null && em.Exists(prefab) && em.HasComponent<ObjectGeometryData>(prefab))
                        {
                            var box = em.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
                            if (math.all(box.max >= box.min) && math.all(math.abs(box.min) < 50f) && math.all(math.abs(box.max) < 50f))
                            {
                                mp.Box = box;
                                mp.HasBox = true;
                            }
                        }
                    }
                    s_Machines.Add(mp);
                }
            }
            finally { ents.Dispose(); }
        }

        private static void RemoveNow(EntityManager em, EdgeUpgradeTraffic et, LaneDrop ld, uint upd, string why, bool capped)
        {
            int n = ld.Markers.Count;
            DeleteMarkers(em, et, ld, upd);
            et.Lanes.Remove(ld);
            if (n == 0) return;
            string line = "traffic p" + ld.ProjectId + " e" + et.Edge.Index + " lane at lateral " + RRWLog.F(ld.Centre) + " open again: " + n + " marker(s) removed (" + why +
                          (ld.Why.Length > 0 ? "; released: " + ld.Why : "") + ")";
            if (capped) RRWLog.Warn(line); else RRWLog.Info(line);
        }

        // Lanes / edges refreshed one update after their markers were removed (the game unregisters a deleted marker in the frame
        // it is deleted; the refresh after that recomputes the lane's blockage without it).
        private static void ScheduleRefresh(List<Entity> lanes, Entity edge, uint upd)
        {
            for (int i = 0; i < lanes.Count; i++) TrafficState.UpgradeRefresh[lanes[i]] = upd + 1;
            if (edge != Entity.Null) TrafficState.UpgradeRefresh[edge] = upd + 1;
        }

        private static void RunRefreshes(EntityManager em, uint upd)
        {
            if (TrafficState.UpgradeRefresh.Count == 0) return;
            s_Keys.Clear();
            foreach (var kv in TrafficState.UpgradeRefresh) if ((int)(upd - kv.Value) >= 0) s_Keys.Add(kv.Key);
            for (int i = 0; i < s_Keys.Count; i++)
            {
                var e = s_Keys[i];
                TrafficState.UpgradeRefresh.Remove(e);
                if (!TrafficUtil.Alive(em, e)) continue;
                if (em.HasComponent<Edge>(e)) TrafficUtil.RefreshEdge(em, e, true, out _);
                else if (!em.HasComponent<PathfindUpdated>(e)) em.AddComponent<PathfindUpdated>(e);
            }
        }

        // ---------------------------------------------------------------------------------------------- new parking switched off
        private static void SyncParking(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec, UpgradeEdgeState st, ProjectRecord proj, bool h,
                                        in UpgradeView uv, TrafficEdgeSlot slot, in RoadWorksRuntime rt, EdgeArc arc, in TrafficSettings s, uint upd)
        {
            bool want = h && RRWGates.UpgradeNewParkingOff && s.ClosureLayer && rt.m_ClosureTarget != ClosureLevel.Open && !proj.Releasing &&
                        proj.Phase != WorksPhase.Complete;
            if (!want)
            {
                if (ParkingInUse(et))
                    ReturnAllParking(em, et, upd, !h ? "not an upgrade project" : !RRWGates.UpgradeNewParkingOff ? "switched off" : !s.ClosureLayer ? "closure layer off" :
                                                  proj.Releasing || proj.Phase == WorksPhase.Complete ? "works complete" : "road open");
                WriteParking(et, st, rec.GeometryRevision);
                return;
            }
            // a car that parked in a listed lane before the switch-off took effect takes the lane off the list at once (every update:
            // machines may be planned into a listed lane); a held lane that is empty again is listed again
            bool listChanged = RecheckListed(em, et, rec);
            // the band the lanes are judged by: the applied window (the previous one while its machines leave); setup: every
            // band, teardown: none
            int pw = uv.InTeardown ? uv.Window : uv.AppliedWindow;
            bool due = (int)(upd - et.NextParkingCheck) >= 0 || et.ParkWindow != pw || et.ParkGeo != rec.GeometryRevision || et.ParkTail != st.TailRevision;
            if (!due)
            {
                if (listChanged) WriteParking(et, st, rec.GeometryRevision);
                return;
            }
            et.NextParkingCheck = upd + TrafficConst.kParkingOffCheckInterval;
            et.ParkWindow = pw;
            et.ParkGeo = rec.GeometryRevision;
            et.ParkTail = st.TailRevision;
            int before = ListedCount(et), added = 0, returning = 0, back = 0, kept = 0;
            TrafficUtil.LanesInto(em, et.Edge, s_Lanes);
            s_Tmp.Clear();
            for (int i = 0; i < s_Lanes.Count; i++)
            {
                var lane = s_Lanes[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane) || !em.HasComponent<NetParkingLane>(lane) || em.HasComponent<NetCarLane>(lane)) continue;
                if (!LateralOf(em, arc, lane, out float lat)) continue;
                s_Tmp.Add(lane);
                bool inBand = NewParkingBand(st, lat, pw);
                bool off = et.ParkingOff.Contains(lane);
                if (inBand && et.ParkingReturning.Remove(lane))
                {
                    // back in a working band before it came back into use: still switched off, listed again while empty
                    et.ParkingOffLat[lane] = lat;
                    if (ParkingEmpty(em, lane)) et.ParkingHeld.Remove(lane);
                    else et.ParkingHeld.Add(lane);
                    back++;
                }
                else if (inBand && !off)
                {
                    if (!ParkingEmpty(em, lane)) { kept++; continue; }
                    et.ParkingOff.Add(lane);
                    et.ParkingOffLat[lane] = lat;
                    if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
                    added++;
                }
                else if (!inBand && off && !et.ParkingReturning.ContainsKey(lane))
                {
                    StartReturn(et, lane, upd, "its band's window ended (window " + pw + ")");
                    returning++;
                }
            }
            // lanes that left the edge (regenerated): nothing left to switch back on
            int gone = 0;
            if (et.ParkingOff.Count > 0)
            {
                s_Keys.Clear();
                foreach (var lane in et.ParkingOff) if (!s_Tmp.Contains(lane)) s_Keys.Add(lane);
                for (int i = 0; i < s_Keys.Count; i++) ForgetParking(et, s_Keys[i]);
                gone = s_Keys.Count;
            }
            et.ParkingKept = kept;
            WriteParking(et, st, rec.GeometryRevision);
            int after = ListedCount(et);
            if (added > 0 || returning > 0 || back > 0 || (gone > 0 && before != after))
                RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " new parking switched off: " + before + " -> " + after + " lane(s) listed (window " + pw +
                            ", +" + added + (back > 0 ? " +" + back + " back in a band" : "") + (returning > 0 ? ", " + returning + " back in use once no machine is near" : "") +
                            (et.ParkingHeld.Count > 0 ? ", " + et.ParkingHeld.Count + " switched off but a car stands in it" : "") +
                            (kept > 0 ? ", " + kept + " lane(s) with a parked car stay in use" : "") + ")");
        }

        // Listed lanes with a car in them are held (taken off the list); held lanes that are empty again are listed. True when
        // the list changed.
        private static bool RecheckListed(EntityManager em, EdgeUpgradeTraffic et, EdgeRecord rec)
        {
            if (et.ParkingOff.Count == 0) return false;
            bool changed = false;
            foreach (var lane in et.ParkingOff)
            {
                if (et.ParkingReturning.ContainsKey(lane) || !TrafficUtil.Alive(em, lane)) continue;
                bool empty = ParkingEmpty(em, lane);
                if (!empty && et.ParkingHeld.Add(lane))
                {
                    changed = true;
                    RRWLog.Info("traffic p" + rec.ProjectId + " e" + rec.Edge.Index + " switched-off new parking at lateral " +
                                RRWLog.F(et.ParkingOffLat.TryGetValue(lane, out float l) ? l : float.NaN) + ": a car stands in it, no longer listed for the machines");
                }
                else if (empty && et.ParkingHeld.Remove(lane)) changed = true;
            }
            return changed;
        }

        // Switched-off lanes that are not yet on their way back into use (listed or held).
        private static bool ParkingInUse(EdgeUpgradeTraffic et) => et.ParkingOff.Count > et.ParkingReturning.Count;

        // Lanes listed for the machines (switched off, empty, not on the way back into use).
        public static int ListedCount(EdgeUpgradeTraffic et)
        {
            int n = 0;
            foreach (var lane in et.ParkingOff) if (et.ParkingListed(lane)) n++;
            return n;
        }

        private static void StartReturn(EdgeUpgradeTraffic et, Entity lane, uint upd, string why)
        {
            et.ParkingHeld.Remove(lane);
            et.ParkingReturning[lane] = new ParkingReturn { Update = upd, Sim = RRWClock.SimFrame, Why = why };
        }

        private static void ForgetParking(EdgeUpgradeTraffic et, Entity lane)
        {
            et.ParkingOff.Remove(lane);
            et.ParkingOffLat.Remove(lane);
            et.ParkingHeld.Remove(lane);
            et.ParkingReturning.Remove(lane);
        }

        // Every switched-off lane goes back into use once no machine is near it (taken off the list now, so machines leave it).
        private static void ReturnAllParking(EntityManager em, EdgeUpgradeTraffic et, uint upd, string why)
        {
            if (et.ParkingOff.Count == 0) return;
            int n = 0;
            s_Tmp.Clear();
            foreach (var lane in et.ParkingOff) if (!et.ParkingReturning.ContainsKey(lane)) s_Tmp.Add(lane);
            for (int i = 0; i < s_Tmp.Count; i++) { StartReturn(et, s_Tmp[i], upd, why); n++; }
            et.ParkWindow = int.MinValue;
            if (SiteRegistry.TryGetEdge(et.Edge, out var rec) && rec.Upgrade != null) WriteParking(et, rec.Upgrade, rec.GeometryRevision);
            if (n > 0)
                RRWLog.Info("traffic p" + et.ProjectId + " e" + et.Edge.Index + " new parking taken off the machines' list (" + why + "): " + n +
                            " lane(s) back in use once no machine is near");
        }

        // Switched-off lanes on their way back into use: switched on once no machine body is near the lane and the project's
        // report has no roller in its parking strip, or after the release cap (Warn).
        private static void ProcessParkingReturns(EntityManager em, EdgeUpgradeTraffic et, uint upd)
        {
            if (et.ParkingReturning.Count == 0) return;
            s_Tmp.Clear();
            foreach (var kv in et.ParkingReturning) s_Tmp.Add(kv.Key);
            SiteRegistry.TryGetEdge(et.Edge, out var rec);
            var arc = ArcOf(em, et, rec);
            var slot = rec?.Get<TrafficEdgeSlot>(ModuleSlot.Traffic);
            SiteRegistry.TryGetProject(et.ProjectId, out var proj);
            for (int i = 0; i < s_Tmp.Count; i++)
            {
                var lane = s_Tmp[i];
                var r = et.ParkingReturning[lane];
                string done = null;
                bool capped = false;
                if (!TrafficUtil.Alive(em, et.Edge)) done = "edge removed";
                else if (!TrafficUtil.Alive(em, lane)) done = "lane regenerated";
                else
                {
                    bool near = false;
                    var side = slot != null ? slot.Groups.Of(em, lane) & RoadZones.Parking : RoadZones.Parking;
                    if (proj != null && proj.RollersSpawned > 0 && proj.MachinesReportFresh(upd) && (proj.MachineZones & side) != RoadZones.None) near = true;
                    if (!near)
                    {
                        float lat = et.ParkingOffLat.TryGetValue(lane, out float l) ? l : 0f;
                        float hw = EcsUtil.LaneBitsOf(em, lane, out _, out float width) ? width * 0.5f : 1.25f;
                        near = !et.ParkingOffLat.ContainsKey(lane) || MachineBodyNear(em, arc, lat, hw, upd);
                    }
                    if (!near) done = "no machine near the lane";
                    else
                    {
                        uint simAge = unchecked(RRWClock.SimFrame - r.Sim), updAge = unchecked(upd - r.Update);
                        if (simAge >= (uint)RRWConst.kCompletionMachineWaitSimFrames || updAge >= (uint)RRWConst.kCompletionMachineWaitUpdatesHard)
                        {
                            capped = true;
                            done = "machines still near the lane " + simAge + " sim frames after it was taken off the list";
                        }
                    }
                }
                if (done == null) continue;
                float at = et.ParkingOffLat.TryGetValue(lane, out float c) ? c : float.NaN;
                ForgetParking(et, lane);
                if (TrafficUtil.Alive(em, lane) && !em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
                string line = "traffic p" + et.ProjectId + " e" + et.Edge.Index + " new parking at lateral " + RRWLog.F(at) + " back in use (" + done + "; " + r.Why + ")";
                if (capped) RRWLog.Warn(line); else RRWLog.Info(line);
            }
        }

        private static bool LateralOf(EntityManager em, EdgeArc arc, Entity lane, out float lat) => EcsUtil.LaneCentre(em, lane, arc, out lat);

        // A parking lane with this EDGE-frame centre lies in a build / rebuild band whose window has not ended (setup: every band;
        // teardown: none).
        private static bool NewParkingBand(UpgradeEdgeState st, float lat, int window)
        {
            for (int i = 0; i < st.BandCount; i++)
            {
                var b = st.Bands[i];
                if (b.Kind != BandKind.Build && b.Kind != BandKind.Rebuild) continue;
                if (window > b.Window) continue;
                if (UpgradeLanes.InBand(lat, b.Lo, b.Hi)) return true;
            }
            return false;
        }

        // No parked car and no vehicle (our machines aside) in the lane.
        private static bool ParkingEmpty(EntityManager em, Entity lane)
        {
            if (!em.HasBuffer<LaneObject>(lane)) return true;
            var buf = em.GetBuffer<LaneObject>(lane, true);
            for (int i = 0; i < buf.Length; i++)
            {
                var o = buf[i].m_LaneObject;
                if (em.Exists(o) && !em.HasComponent<RRWMachine>(o) && (em.HasComponent<ParkedCar>(o) || em.HasComponent<Vehicle>(o))) return false;
            }
            return true;
        }

        // At once, no machine check (Traffic fault fail-safe and full resets only).
        private static void ClearParking(EntityManager em, EdgeUpgradeTraffic et, uint upd)
        {
            if (et.ParkingOff.Count == 0) return;
            int n = et.ParkingOff.Count;
            foreach (var lane in et.ParkingOff)
                if (TrafficUtil.Alive(em, lane) && !em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
            et.ParkingOff.Clear();
            et.ParkingOffLat.Clear();
            et.ParkingHeld.Clear();
            et.ParkingReturning.Clear();
            et.ParkWindow = int.MinValue;
            if (SiteRegistry.TryGetEdge(et.Edge, out var rec) && rec.Upgrade != null) WriteParking(et, rec.Upgrade, rec.GeometryRevision);
            RRWLog.Info("traffic p" + et.ProjectId + " e" + et.Edge.Index + " new parking back in use: " + n + " lane(s)");
        }

        // The list for the machines and Surfaces: switched-off lanes that are empty and not on their way back into use.
        private static void WriteParking(EdgeUpgradeTraffic et, UpgradeEdgeState st, int geometryRevision)
        {
            st.ParkingOffLanes.Clear();
            st.ParkingOffCentres.Clear();
            foreach (var kv in et.ParkingOffLat)
            {
                if (!et.ParkingListed(kv.Key)) continue;
                st.ParkingOffLanes.Add(kv.Key);
                st.ParkingOffCentres.Add(kv.Value);
            }
            st.ParkingOffRevision = geometryRevision;
        }

        // ---------------------------------------------------------------------------------------------- fail-safe
        // Traffic faulted: every marker goes at once (the lanes are refreshed in the same frame), switched-off parking comes back, and
        // every drop report says "no drops" (machines leave the lanes).
        public static int RemoveEverything(EntityManager em)
        {
            int n = 0;
            foreach (var et in TrafficState.Upgrade.Values)
            {
                for (int i = 0; i < et.Lanes.Count; i++)
                {
                    var ld = et.Lanes[i];
                    for (int k = 0; k < ld.Markers.Count; k++)
                    {
                        if (!em.Exists(ld.Markers[k])) continue;
                        LaneClosureMarkers.RegisteredLanes(em, ld.Markers[k], ld.Registered);
                        LaneClosureMarkers.Delete(em, ld.Markers[k]);
                        n++;
                    }
                    for (int k = 0; k < ld.Registered.Count; k++)
                        if (TrafficUtil.Alive(em, ld.Registered[k]) && !em.HasComponent<PathfindUpdated>(ld.Registered[k])) em.AddComponent<PathfindUpdated>(ld.Registered[k]);
                }
                foreach (var lane in et.ParkingOff)
                    if (TrafficUtil.Alive(em, lane) && !em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
                if (TrafficUtil.Alive(em, et.Edge)) TrafficUtil.RefreshEdge(em, et.Edge, true, out _);
            }
            TrafficState.Upgrade.Clear();
            TrafficState.UpgradeRefresh.Clear();
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                if (rec.Upgrade == null) continue;
                ResetReport(rec.Upgrade, RRWClock.UpdateIndex);
                rec.Upgrade.ParkingOffLanes.Clear();
                rec.Upgrade.ParkingOffCentres.Clear();
            }
            return n;
        }

        // ---------------------------------------------------------------------------------------------- machines vs lanes that carry traffic
        // Halves (and the parking strip on the same side) where machines may stand although the group carries traffic: a dropped or
        // released lane of the project with markers on it. A switched-off new parking lane alone does not count (machines stay out).
        public static RoadZones MarkedZones(EntityManager em, ProjectRecord proj)
        {
            var z = RoadZones.None;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                if (!TrafficState.Upgrade.TryGetValue(proj.Edges[i], out var et)) continue;
                for (int k = 0; k < et.Lanes.Count; k++)
                {
                    var ld = et.Lanes[k];
                    if (ld.Markers.Count == 0 || (ld.Stage != DropStage.Active && ld.Stage != DropStage.Releasing)) continue;
                    z |= ld.Half;
                    if ((ld.Half & RoadZones.LeftHalf) != 0) z |= RoadZones.ParkingLeft;
                    if ((ld.Half & RoadZones.RightHalf) != 0) z |= RoadZones.ParkingRight;
                }
            }
            return z;
        }

        // Machine rule of a mode H project: a fresh machine report with a machine in a lane group that carries traffic now (the target's open
        // groups; every group under a slow zone), apart from the halves of its dropped lanes.
        // Null when nothing is wrong.
        public static string MachinesInOpenLanes(EntityManager em, ProjectRecord proj, ClosureLevel target, uint upd)
        {
            if (proj.ReleaseCapped(upd) || !proj.MachinesReportFresh(upd)) return null;
            RoadZones open = target == ClosureLevel.Closed ? proj.OpenLanes & RoadZones.AllLanes : RoadZones.AllLanes;
            var inOpen = proj.MachineZones & open & RoadZones.AllLanes;
            if (inOpen == RoadZones.None) return null;
            inOpen &= ~MarkedZones(em, proj);
            if (inOpen == RoadZones.None) return null;
            return proj.MachinesOnCarriageway + " machine(s) in lane group(s) that carry traffic " + TrafficUtil.Bits(inOpen) + " (target " + target + ", open " +
                   TrafficUtil.Bits(open) + ", machine zones " + proj.MachineZones + ", report age " + unchecked(upd - proj.MachinesReportUpdate) + ")";
        }

        // ---------------------------------------------------------------------------------------------- dev text
        public static string StageText(LaneDrop ld) =>
            ld.Stage + (ld.Why.Length > 0 ? "(" + ld.Why + ")" : "");

        public static string DescribeEdge(EntityManager em, Entity edge)
        {
            var sb = new StringBuilder(256);
            SiteRegistry.TryGetEdge(edge, out var rec);
            var st = rec?.Upgrade;
            TrafficState.Upgrade.TryGetValue(edge, out var et);
            if (st == null && et == null) return "";
            if (st != null)
            {
                sb.Append(" drop(key geo=").Append(st.DropLanesRevision).Append(" window=").Append(st.DropWindow).Append(" allAtOnce=").Append(st.DropAllAtOnce)
                  .Append(" tail=").Append(st.DropTail).Append(" lanes=").Append(st.DropLanes.Count).Append(" applied=").Append(st.DropApplied)
                  .Append(" registered=").Append(st.BlockersRegistered).Append(" intrusion=").Append(st.DropIntrusion).Append(" clean=").Append(st.DropCleanChecks)
                  .Append(" reportAge=").Append(st.DropReportUpdate == 0 ? "never" : unchecked(RRWClock.UpdateIndex - st.DropReportUpdate).ToString())
                  .Append(" centres=").Append(st.DropCentresWritten ? "written" : "MISSING")
                  .Append(" safeU=").Append(st.DropSafeRange(out float su0, out float su1) ? "[" + RRWLog.F(su0) + "," + RRWLog.F(su1) + "]" : "-").Append(')')
                  .Append(" parkingOff=").Append(st.ParkingOffLanes.Count);
            }
            if (et != null)
            {
                sb.Append(" laneDrops[");
                for (int i = 0; i < et.Lanes.Count; i++)
                {
                    var ld = et.Lanes[i];
                    sb.Append(i > 0 ? " " : "").Append(RRWLog.F(ld.Centre)).Append(':').Append(ld.Stage).Append(':').Append(ld.Markers.Count);
                }
                sb.Append(']').Append(et.Want ? "" : " noDrops(" + et.Why + ")");
                if (et.SafeWhy.Length > 0) sb.Append(" noRange(").Append(et.SafeWhy).Append(')');
                if (et.ParkingHeld.Count > 0 || et.ParkingReturning.Count > 0)
                    sb.Append(" parkingHeld=").Append(et.ParkingHeld.Count).Append(" parkingReturning=").Append(et.ParkingReturning.Count);
            }
            return sb.ToString();
        }
    }
}
