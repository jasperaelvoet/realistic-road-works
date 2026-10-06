using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Attached = Game.Objects.Attached;
using ConnectedBuilding = Game.Buildings.ConnectedBuilding;
using Moving = Game.Objects.Moving;
using NetCarLane = Game.Net.CarLane;
using NetEdge = Game.Net.Edge;
using NetOutsideConnection = Game.Net.OutsideConnection;
using NetSubLane = Game.Net.SubLane;
using ParkedCar = Game.Vehicles.ParkedCar;
using PrefabRef = Game.Prefabs.PrefabRef;
using RoadData = Game.Prefabs.RoadData;
using PrefabRoadFlags = Game.Prefabs.RoadFlags;
using TransportStop = Game.Routes.TransportStop;
using ObjTransform = Game.Objects.Transform;
using NetTrackLane = Game.Net.TrackLane;
using Unity.Mathematics;

// Dependants classification and drain tests (ported from the traffic prototype: classification, BFS and
// VehiclesOnCarLanes). Main thread; no per-call allocations except the stop map refresh (Temp arrays).
namespace RealisticRoadWorks.V3.Traffic
{
    public sealed class TrafficSide
    {
        public int Nodes, Edges, Buildings, Stops, OutsideConnections, OwnerEdges;
        public bool BudgetHit, ReachedOther;
        public bool HasDependants => BudgetHit || Buildings > 0 || Stops > 0 || OutsideConnections > 0 || OwnerEdges > 0;

        public void Reset() { Nodes = Edges = Buildings = Stops = OutsideConnections = OwnerEdges = 0; BudgetHit = ReachedOther = false; }

        public override string ToString() =>
            "nodes=" + Nodes + (BudgetHit ? "+" : "") + " edges=" + Edges + " bld=" + Buildings + " stops=" + Stops +
            " oc=" + OutsideConnections + " ownerEdges=" + OwnerEdges + " dependants=" + HasDependants;
    }

    public sealed class TrafficClassResult
    {
        public Entity Edge;
        public int ConnectedBuildings, Stops;
        public bool OwnerEdgeAtNode, Highway, Cut, CutUnknown, EndSearched;
        public Entity FirstNode;                                  // node whose side was searched first (SideStart)
        public readonly TrafficSide SideStart = new TrafficSide();
        public readonly TrafficSide SideEnd = new TrafficSide();
        public readonly List<string> Reasons = new List<string>(6);
        // Design decision: the highway reason is ignored for the product (highways under works close like any road).
        public bool Dependants;
        public string ReasonText => Reasons.Count == 0 ? "none" : string.Join("+", Reasons);

        public void Reset(Entity edge)
        {
            Edge = edge;
            ConnectedBuildings = Stops = 0;
            OwnerEdgeAtNode = Highway = Cut = CutUnknown = EndSearched = Dependants = false;
            FirstNode = Entity.Null;
            SideStart.Reset();
            SideEnd.Reset();
            Reasons.Clear();
        }

        public string Describe() =>
            "edge=" + RRWLog.E(Edge) + " dependants=" + Dependants + " reasons=" + ReasonText + " connectedBuildings=" + ConnectedBuildings +
            " stops=" + Stops + " ownerEdgeAtNode=" + OwnerEdgeAtNode + " highway=" + Highway +
            " cut=" + (CutUnknown ? "unknown(budget)" : Cut.ToString()) +
            " firstSide(n" + FirstNode.Index + ": " + SideStart + ") secondSide(" + (EndSearched ? SideEnd.ToString() : "not searched: first side decided it") + ")";
    }

    // Zone classification of one edge; chain frame.
    public struct TrafficZoneClass
    {
        public bool HasTrack;
        public RoadZones StopZones;         // car half of each transport stop (by the stop's side of DirSplit)
        public RoadZones BuildingZones;     // SidewalkLeft / SidewalkRight of the sides with a ConnectedBuilding
        public int BuildingCount;
        public int Stops;
    }

    public static class TrafficAnalysis
    {
        // Reused collections (main thread only).
        private static readonly Dictionary<Entity, int> s_StopsByEdge = new Dictionary<Entity, int>();
        private static readonly List<Entity> s_StopParents = new List<Entity>(64);
        private static readonly List<Entity> s_StopEntities = new List<Entity>(64);
        private static readonly HashSet<Entity> s_Visited = new HashSet<Entity>();
        private static readonly HashSet<Entity> s_VisitedEdges = new HashSet<Entity>();
        private static readonly Queue<Entity> s_Queue = new Queue<Entity>();
        private static readonly List<Entity> s_NodeEdges = new List<Entity>(16);
        private static readonly HashSet<Entity> s_Seen = new HashSet<Entity>();
        private static readonly List<Entity> s_Lanes = new List<Entity>(32);
        private static readonly TrafficClassResult s_Result = new TrafficClassResult();
        private static readonly TrafficClassResult s_StagedResult = new TrafficClassResult();
        // Staged CutEdge: while != 0, Bfs does not traverse works edges of OTHER projects that are closed or hidden
        // (their traffic cannot pass); s_SkippedWorks counts them.
        private static uint s_SkipProject;
        private static int s_SkippedWorks;
        private static EntityQuery s_StopQuery;
        private static World s_StopWorld;

        public static void ClearCaches()
        {
            s_StopsByEdge.Clear();
            s_StopParents.Clear();
            s_StopEntities.Clear();
            s_Visited.Clear();
            s_VisitedEdges.Clear();
            s_Queue.Clear();
            s_Seen.Clear();
            s_StopWorld = null;
        }

        // Classification: SlowZone-worthy dependants if ConnectedBuilding is non-empty, a TransportStop is
        // attached to the edge, an end node joins an Owner'd (building sub-net) edge, or the edge is a cut-edge with
        // dependants on both sides (cut unknown = conservative yes). The result object is reused: copy what you need.
        public static TrafficClassResult Classify(EntityManager em, Entity edge)
        {
            var r = s_Result;
            r.Reset(edge);
            if (!TrafficUtil.Alive(em, edge) || !em.HasComponent<NetEdge>(edge)) { r.Reasons.Add("dead"); return r; }
            RefreshStops(em);

            if (em.HasBuffer<ConnectedBuilding>(edge)) r.ConnectedBuildings = em.GetBuffer<ConnectedBuilding>(edge, true).Length;
            s_StopsByEdge.TryGetValue(edge, out r.Stops);

            var e = em.GetComponentData<NetEdge>(edge);
            r.OwnerEdgeAtNode = NodeHasOwnerEdge(em, e.m_Start, edge) || NodeHasOwnerEdge(em, e.m_End, edge);

            if (em.HasComponent<PrefabRef>(edge))
            {
                var prefab = em.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (em.HasComponent<RoadData>(prefab))
                    r.Highway = (em.GetComponentData<RoadData>(prefab).m_Flags & PrefabRoadFlags.UseHighwayRules) != 0;
            }

            // Search the smaller side first (fewer connected edges: a dead end exhausts at once). A side exhausted
            // without reaching the other end proves a cut, even when the other side (usually the whole city) would
            // blow the node budget. A cut whose first side has no dependants can never be a "cutEdge" reason, so the
            // expensive city-side search is skipped then.
            Entity first = e.m_Start, second = e.m_End;
            if (Degree(em, second) < Degree(em, first)) { first = e.m_End; second = e.m_Start; }
            r.FirstNode = first;
            Bfs(em, first, edge, second, r.SideStart);
            if (r.SideStart.ReachedOther) r.Cut = false;
            else if (!r.SideStart.BudgetHit && !r.SideStart.HasDependants) r.Cut = true;
            else
            {
                Bfs(em, second, edge, first, r.SideEnd);
                r.EndSearched = true;
                if (r.SideEnd.ReachedOther) r.Cut = false;
                else if (!r.SideStart.BudgetHit || !r.SideEnd.BudgetHit) r.Cut = true;
                else r.CutUnknown = true;
            }

            bool dep = false;
            if (r.ConnectedBuildings > 0) { r.Reasons.Add("buildings"); dep = true; }
            if (r.Stops > 0) { r.Reasons.Add("stop"); dep = true; }
            if (r.OwnerEdgeAtNode) { r.Reasons.Add("ownerEdgeAtNode"); dep = true; }
            if (r.Cut && r.SideStart.HasDependants && r.EndSearched && r.SideEnd.HasDependants) { r.Reasons.Add("cutEdge"); dep = true; }
            if (r.CutUnknown) { r.Reasons.Add("cutUnknown"); dep = true; }
            if (r.Highway) r.Reasons.Add("highway(ignored)");
            r.Dependants = dep;
            return r;
        }

        private static void RefreshStops(EntityManager em)
        {
            if (s_StopWorld != em.World)
            {
                s_StopWorld = em.World;
                s_StopQuery = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<TransportStop>(), ComponentType.ReadOnly<Attached>() },
                    None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
                });
            }
            s_StopsByEdge.Clear();
            s_StopParents.Clear();
            s_StopEntities.Clear();
            if (s_StopQuery.IsEmptyIgnoreFilter) return;
            var att = s_StopQuery.ToComponentDataArray<Attached>(Allocator.Temp);
            var ents = s_StopQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < att.Length; i++)
            {
                var parent = att[i].m_Parent;
                if (parent == Entity.Null) continue;
                s_StopsByEdge.TryGetValue(parent, out int n);
                s_StopsByEdge[parent] = n + 1;
                s_StopParents.Add(parent);
                s_StopEntities.Add(ents[i]);
            }
            ents.Dispose();
            att.Dispose();
        }

        private static int Degree(EntityManager em, Entity node) =>
            node != Entity.Null && em.Exists(node) && em.HasBuffer<ConnectedEdge>(node) ? em.GetBuffer<ConnectedEdge>(node, true).Length : 0;

        private static bool NodeHasOwnerEdge(EntityManager em, Entity node, Entity self)
        {
            if (!TrafficUtil.Alive(em, node) || !em.HasBuffer<ConnectedEdge>(node)) return false;
            var buf = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < buf.Length; i++)
            {
                var other = buf[i].m_Edge;
                if (other == self || !TrafficUtil.Alive(em, other) || em.HasComponent<Temp>(other)) continue;
                if (em.HasComponent<Owner>(other)) return true;
            }
            return false;
        }

        // BFS over road edges (Edge + Road, no Temp/Deleted) from 'from', never crossing 'works'. Owner'd edges count
        // as dependants (building sub-nets) but are not traversed.
        private static void Bfs(EntityManager em, Entity from, Entity works, Entity target, TrafficSide side)
        {
            s_Visited.Clear();
            s_VisitedEdges.Clear();
            s_Queue.Clear();
            if (from == Entity.Null) return;
            s_Visited.Add(from);
            s_Queue.Enqueue(from);
            while (s_Queue.Count > 0)
            {
                var node = s_Queue.Dequeue();
                side.Nodes++;
                if (!em.Exists(node)) continue;
                if (em.HasComponent<NetOutsideConnection>(node)) side.OutsideConnections++;
                if (!em.HasBuffer<ConnectedEdge>(node)) continue;
                var buf = em.GetBuffer<ConnectedEdge>(node, true);
                s_NodeEdges.Clear();
                for (int i = 0; i < buf.Length; i++) s_NodeEdges.Add(buf[i].m_Edge);
                for (int k = 0; k < s_NodeEdges.Count; k++)
                {
                    var ed = s_NodeEdges[k];
                    if (ed == works || !TrafficUtil.Alive(em, ed) || em.HasComponent<Temp>(ed) || !em.HasComponent<NetEdge>(ed)) continue;
                    if (s_SkipProject != 0 && SiteRegistry.TryGetEdge(ed, out var wr) && wr.ProjectId != s_SkipProject
                        && (wr.HiddenApplied || wr.ClosureApplied == ClosureLevel.Closed))
                    {
                        if (s_VisitedEdges.Add(ed)) s_SkippedWorks++;
                        continue;
                    }
                    if (em.HasComponent<Owner>(ed)) { if (s_VisitedEdges.Add(ed)) side.OwnerEdges++; continue; }
                    if (!em.HasComponent<Road>(ed)) continue;
                    if (s_VisitedEdges.Add(ed))
                    {
                        side.Edges++;
                        if (em.HasBuffer<ConnectedBuilding>(ed)) side.Buildings += em.GetBuffer<ConnectedBuilding>(ed, true).Length;
                        if (s_StopsByEdge.TryGetValue(ed, out int s)) side.Stops += s;
                    }
                    var ee = em.GetComponentData<NetEdge>(ed);
                    if (ee.m_Start == node) Visit(ee.m_End, target, side);
                    else if (ee.m_End == node) Visit(ee.m_Start, target, side);
                    else { Visit(ee.m_Start, target, side); Visit(ee.m_End, target, side); } // middle-connected node
                }
                if (side.ReachedOther) break;
            }
            s_Queue.Clear();
        }

        private static void Visit(Entity n, Entity target, TrafficSide side)
        {
            if (n == Entity.Null) return;
            if (n == target) side.ReachedOther = true;
            if (s_Visited.Contains(n)) return;
            if (s_Visited.Count >= TrafficConst.kBfsBudget) { side.BudgetHit = true; return; }
            s_Visited.Add(n);
            s_Queue.Enqueue(n);
        }

        // Dead end next to other works: the CutEdge test of `edge` with the works edges of OTHER
        // projects that are Closed or hidden removed from the graph (a staged project must not open one direction towards / from a
        // road that another project keeps closed: that road carries nothing, whatever its own open half). Fails closed: another
        // project's open car half is ignored (it may run the other way). Classify's own Cut / Dependants are unchanged (mode
        // choice). skipped = other-works edges met (0 -> same verdict as CutEdgeOf(Classify)).
        public static bool StagedCutEdge(EntityManager em, Entity edge, uint projectId, out int skipped)
        {
            skipped = 0;
            var r = s_StagedResult;
            r.Reset(edge);
            if (projectId == 0 || !TrafficUtil.Alive(em, edge) || !em.HasComponent<NetEdge>(edge)) return false;
            RefreshStops(em);
            if (em.HasBuffer<ConnectedBuilding>(edge)) r.ConnectedBuildings = em.GetBuffer<ConnectedBuilding>(edge, true).Length;
            s_StopsByEdge.TryGetValue(edge, out r.Stops);
            var e = em.GetComponentData<NetEdge>(edge);
            Entity first = e.m_Start, second = e.m_End;
            if (Degree(em, second) < Degree(em, first)) { first = e.m_End; second = e.m_Start; }
            s_SkipProject = projectId;
            s_SkippedWorks = 0;
            try
            {
                Bfs(em, first, edge, second, r.SideStart);
                if (r.SideStart.ReachedOther) r.Cut = false;
                else
                {
                    // unlike Classify, always search the second side: the separated part may be the side beyond a closed neighbour
                    Bfs(em, second, edge, first, r.SideEnd);
                    r.EndSearched = true;
                    if (r.SideEnd.ReachedOther) r.Cut = false;
                    else if (!r.SideStart.BudgetHit || !r.SideEnd.BudgetHit) r.Cut = true;
                    else r.CutUnknown = true;
                }
            }
            finally
            {
                skipped = s_SkippedWorks;
                s_SkipProject = 0;
                s_SkippedWorks = 0;
            }
            return CutEdgeOf(r);
        }

        // CutEdge (dead end): the edge is a bridge of the road graph (removing it disconnects a part of
        // the network) AND a one-direction opening could trap someone: the separated part (a side whose search exhausted
        // without reaching the other end) has dependants (buildings, stops, outside connections, building sub-nets), or the
        // edge itself has buildings / stops. Unknown (both searches hit the budget) counts as a dead end (fails closed).
        public static bool CutEdgeOf(TrafficClassResult r)
        {
            if (r.CutUnknown) return true;
            if (!r.Cut) return false;
            bool sepDep = (!r.SideStart.BudgetHit && r.SideStart.HasDependants) || (r.EndSearched && !r.SideEnd.BudgetHit && r.SideEnd.HasDependants);
            return sepDep || r.ConnectedBuildings > 0 || r.Stops > 0;
        }

        // Zone classification (chain frame through the edge's lane groups): track lanes, the car half of each transport
        // stop (stop position against DirSplit; no split / no position -> both halves), the sidewalk side of each connected
        // building (position against the carriageway centre; no position -> both sides), building count.
        public static TrafficZoneClass ClassifyZones(EntityManager em, Entity edge, LaneGroups g)
        {
            var z = new TrafficZoneClass();
            if (!TrafficUtil.Alive(em, edge)) return z;
            TrafficUtil.LanesInto(em, edge, s_Lanes);
            for (int i = 0; i < s_Lanes.Count; i++)
                if (TrafficUtil.Alive(em, s_Lanes[i]) && em.HasComponent<NetTrackLane>(s_Lanes[i])) { z.HasTrack = true; break; }
            bool rev = g != null && g.ChainReversed;
            var sec = g != null ? g.Section : default;
            float split = RoadZoneMath.DirSplitChain(sec, rev);
            RoadZoneMath.CarriageChain(sec, rev, out float lo, out float hi);
            float mid = hi > lo ? (lo + hi) * 0.5f : 0f;

            RefreshStops(em);
            for (int i = 0; i < s_StopParents.Count; i++)
            {
                if (s_StopParents[i] != edge) continue;
                var stop = s_StopEntities[i];
                z.Stops++;
                if (g == null || float.IsNaN(split) || !em.Exists(stop) || !em.HasComponent<ObjTransform>(stop) ||
                    !g.LateralOfPoint(em.GetComponentData<ObjTransform>(stop).m_Position, out float sl))
                {
                    z.StopZones |= RoadZones.Carriageway;
                    continue;
                }
                z.StopZones |= RoadZoneMath.ToChain(sl, rev) < split ? RoadZones.LeftHalf : RoadZones.RightHalf;
            }

            if (em.HasBuffer<ConnectedBuilding>(edge))
            {
                var buf = em.GetBuffer<ConnectedBuilding>(edge, true);
                z.BuildingCount = buf.Length;
                for (int i = 0; i < buf.Length; i++)
                {
                    var b = buf[i].m_Building;
                    if (g == null || !em.Exists(b) || !em.HasComponent<ObjTransform>(b) ||
                        !g.LateralOfPoint(em.GetComponentData<ObjTransform>(b).m_Position, out float bl))
                    {
                        z.BuildingZones |= RoadZones.Sidewalks;
                        continue;
                    }
                    z.BuildingZones |= RoadZoneMath.ToChain(bl, rev) < mid ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
                }
            }
            return z;
        }

        // ConnectedBuilding count of the edge (0 when the buffer is missing).
        public static int BuildingCount(EntityManager em, Entity edge)
        {
            if (!em.HasBuffer<ConnectedBuilding>(edge)) return 0;
            return em.GetBuffer<ConnectedBuilding>(edge, true).Length;
        }

        // Vehicles in the LaneObject buffers of the edge's car lanes (parked cars and our lane closure markers excluded).
        // Moving = has Game.Objects.Moving (drain test); stopped = the rest (HardClose waits for both).
        public static int VehiclesOnCarLanes(EntityManager em, Entity edge, out int moving, out int stopped) =>
            VehiclesOnCarLanes(em, edge, null, out moving, out stopped);

        // With a closure, only the CLOSED car lanes count (lanes of an open group carry traffic on purpose).
        public static int VehiclesOnCarLanes(EntityManager em, Entity edge, EdgeClosure closedOnly, out int moving, out int stopped)
        {
            moving = 0; stopped = 0;
            s_Seen.Clear();
            TrafficUtil.LanesInto(em, edge, s_Lanes);
            for (int l = 0; l < s_Lanes.Count; l++)
            {
                var lane = s_Lanes[l];
                if (!TrafficUtil.Alive(em, lane) || !em.HasComponent<NetCarLane>(lane) || !em.HasBuffer<LaneObject>(lane)) continue;
                if (closedOnly != null && closedOnly.LaneIsOpen(em, lane)) continue;
                var buf = em.GetBuffer<LaneObject>(lane, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var o = buf[i].m_LaneObject;
                    if (!s_Seen.Add(o) || !em.Exists(o) || em.HasComponent<ParkedCar>(o) || em.HasComponent<TrafficLaneBlocker>(o)) continue;
                    if (em.HasComponent<Moving>(o)) moving++; else stopped++;
                }
            }
            s_Seen.Clear();
            return moving + stopped;
        }
    }
}
