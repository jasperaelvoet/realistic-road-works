using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetEdge = Game.Net.Edge;
using NetSubLane = Game.Net.SubLane;

// Detour verdict of upgrade works (Traffic is the only writer of UpgradeRuntime.DetourHalves / DetourRevision / DetourUpdate):
// for each car half of the project's chain, can the traffic of that half still get from where it enters the chain to where it
// leaves it when the half is closed? Directed search over the road network by the travel direction of the car lanes of each
// edge, never over the project's own edges and never over the edges of other projects that do not carry traffic in both
// directions (closed, hidden, or one half open: fails closed). Budget kBfsBudget nodes; a search that hits it finds no detour.
// One project per update, re-checked on every project revision and every kDetourRecheckUpdates. Until a verdict exists the
// Director never offers one-way operation (Half).
namespace RealisticRoadWorks.V3.Traffic
{
    public static class TrafficDetour
    {
        private static readonly HashSet<Entity> s_Avoid = new HashSet<Entity>();
        private static readonly HashSet<Entity> s_Visited = new HashSet<Entity>();
        private static readonly Queue<Entity> s_Queue = new Queue<Entity>();
        private static readonly Dictionary<Entity, byte> s_Dirs = new Dictionary<Entity, byte>();   // bit 0 start -> end, bit 1 end -> start
        private static readonly List<Entity> s_NodeEdges = new List<Entity>(16);

        // Picks the stalest mode H project whose verdict is missing or old and computes it.
        public static void Step(EntityManager em, uint upd)
        {
            ProjectRecord pick = null;
            uint pickAge = 0;
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                var rt = proj.Upgrade;
                if (rt == null || proj.Mode != VisualMode.HalfWidth || proj.Edges.Count == 0) continue;
                bool stale = rt.DetourUpdate == 0 || rt.DetourRevision != proj.Revision ||
                             unchecked(upd - rt.DetourUpdate) >= (uint)TrafficConst.kDetourRecheckUpdates;
                if (!stale) continue;
                uint age = rt.DetourUpdate == 0 ? uint.MaxValue : unchecked(upd - rt.DetourUpdate);
                if (pick == null || age > pickAge) { pick = proj; pickAge = age; }
            }
            if (pick != null) Update(em, pick, upd);
        }

        // Computes and writes the verdict of one project. Returns the halves with a detour.
        public static RoadZones Update(EntityManager em, ProjectRecord proj, uint upd)
        {
            var rt = proj.Upgrade;
            if (rt == null) return RoadZones.None;
            var halves = Compute(em, proj, out string detail);
            bool changed = rt.DetourUpdate == 0 || rt.DetourHalves != halves;
            rt.DetourHalves = halves;
            rt.DetourRevision = proj.Revision;
            rt.DetourUpdate = upd == 0 ? 1u : upd;
            string line = "traffic p" + proj.Id + " detour verdict: " + (halves == RoadZones.None ? "no half" : TrafficUtil.Bits(halves)) +
                          " can close with a detour (" + detail + ", revision " + proj.Revision + ")";
            if (changed) RRWLog.Info(line); else RRWLog.Verbose(line);
            return halves;
        }

        public static RoadZones Compute(EntityManager em, ProjectRecord proj, out string detail)
        {
            detail = "";
            if (!ChainEnds(em, proj, out Entity start, out Entity end, out RoadZones present))
            {
                detail = "chain ends unknown";
                return RoadZones.None;
            }
            if (start == end)
            {
                detail = "the chain is a loop";
                return RoadZones.None;
            }
            s_Avoid.Clear();
            for (int i = 0; i < proj.Edges.Count; i++) s_Avoid.Add(proj.Edges[i]);
            s_Dirs.Clear();
            // the half whose traffic travels towards +u (right-hand traffic: the chain-right half)
            var plus = RRWCity.LeftHandTraffic ? RoadZones.LeftHalf : RoadZones.RightHalf;
            var minus = RoadZones.Carriageway & ~plus;
            var r = RoadZones.None;
            if ((present & plus) != 0)
            {
                bool ok = Reach(em, proj.Id, start, end, out int nodes, out bool budget);
                if (ok) r |= plus;
                detail += TrafficUtil.Bits(plus) + "(+u) " + (ok ? "found" : budget ? "budget hit" : "none") + " after " + nodes + " nodes";
            }
            else detail += TrafficUtil.Bits(plus) + " has no lanes";
            if ((present & minus) != 0)
            {
                bool ok = Reach(em, proj.Id, end, start, out int nodes, out bool budget);
                if (ok) r |= minus;
                detail += ", " + TrafficUtil.Bits(minus) + "(-u) " + (ok ? "found" : budget ? "budget hit" : "none") + " after " + nodes + " nodes";
            }
            else detail += ", " + TrafficUtil.Bits(minus) + " has no lanes";
            s_Avoid.Clear();
            s_Dirs.Clear();
            return r;
        }

        // The chain's end nodes (u = 0 and u = U) and the car halves its edges carry.
        private static bool ChainEnds(EntityManager em, ProjectRecord proj, out Entity start, out Entity end, out RoadZones present)
        {
            start = end = Entity.Null;
            present = RoadZones.None;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) continue;
                present |= rec.CarHalvesRevision == rec.GeometryRevision ? rec.CarHalves : rec.ZonesPresent & RoadZones.Carriageway;
            }
            if (present == RoadZones.None) present = RoadZones.Carriageway;
            var first = proj.Edges[0];
            var last = proj.Edges[proj.Edges.Count - 1];
            if (!TrafficUtil.Alive(em, first) || !TrafficUtil.Alive(em, last)) return false;
            if (!em.HasComponent<RoadWorksRuntime>(first) || !em.HasComponent<RoadWorksRuntime>(last)) return false;
            if (!em.HasComponent<NetEdge>(first) || !em.HasComponent<NetEdge>(last)) return false;
            var r0 = em.GetComponentData<RoadWorksRuntime>(first);
            var r1 = em.GetComponentData<RoadWorksRuntime>(last);
            var e0 = em.GetComponentData<NetEdge>(first);
            var e1 = em.GetComponentData<NetEdge>(last);
            start = r0.m_ChainU0 <= r0.m_ChainU1 ? e0.m_Start : e0.m_End;
            end = r1.m_ChainU1 >= r1.m_ChainU0 ? e1.m_End : e1.m_Start;
            return start != Entity.Null && end != Entity.Null;
        }

        // Directed breadth-first search from `from` to `to` over drivable road edges.
        private static bool Reach(EntityManager em, uint projectId, Entity from, Entity to, out int nodes, out bool budgetHit)
        {
            nodes = 0;
            budgetHit = false;
            s_Visited.Clear();
            s_Queue.Clear();
            s_Visited.Add(from);
            s_Queue.Enqueue(from);
            bool found = false;
            while (s_Queue.Count > 0 && !found)
            {
                var node = s_Queue.Dequeue();
                nodes++;
                if (!em.Exists(node) || !em.HasBuffer<ConnectedEdge>(node)) continue;
                var buf = em.GetBuffer<ConnectedEdge>(node, true);
                s_NodeEdges.Clear();
                for (int i = 0; i < buf.Length; i++) s_NodeEdges.Add(buf[i].m_Edge);
                for (int k = 0; k < s_NodeEdges.Count && !found; k++)
                {
                    var ed = s_NodeEdges[k];
                    if (!Passable(em, projectId, ed)) continue;
                    byte dirs = Dirs(em, ed);
                    if (dirs == 0) continue;
                    var e = em.GetComponentData<NetEdge>(ed);
                    if (e.m_Start == node) { if ((dirs & 1) != 0) found = Visit(e.m_End, to, ref budgetHit); }
                    else if (e.m_End == node) { if ((dirs & 2) != 0) found = Visit(e.m_Start, to, ref budgetHit); }
                    else
                    {
                        // a node in the middle of the edge
                        if ((dirs & 1) != 0) found = Visit(e.m_End, to, ref budgetHit);
                        if (!found && (dirs & 2) != 0) found = Visit(e.m_Start, to, ref budgetHit);
                    }
                }
            }
            s_Visited.Clear();
            s_Queue.Clear();
            return found;
        }

        private static bool Visit(Entity n, Entity to, ref bool budgetHit)
        {
            if (n == Entity.Null) return false;
            if (n == to) return true;
            if (s_Visited.Contains(n)) return false;
            if (s_Visited.Count >= TrafficConst.kBfsBudget) { budgetHit = true; return false; }
            s_Visited.Add(n);
            s_Queue.Enqueue(n);
            return false;
        }

        // A road edge traffic may use for the detour: not ours, not a building's own road, and when it is a works road of another
        // project, only while both of its car halves carry traffic.
        private static bool Passable(EntityManager em, uint projectId, Entity ed)
        {
            if (s_Avoid.Contains(ed) || !TrafficUtil.Alive(em, ed) || em.HasComponent<Temp>(ed)) return false;
            if (!em.HasComponent<NetEdge>(ed) || !em.HasComponent<Road>(ed) || em.HasComponent<Owner>(ed)) return false;
            if (SiteRegistry.TryGetEdge(ed, out var wr) && wr.ProjectId != projectId)
            {
                if (wr.HiddenApplied) return false;
                if (wr.ClosureApplied == ClosureLevel.Closed)
                {
                    var cars = wr.ZonesPresent & RoadZones.Carriageway;
                    if (cars == RoadZones.None || (wr.OpenLanesApplied & cars) != cars) return false;
                }
            }
            return true;
        }

        // Travel directions of an edge's car lanes (bit 0 start -> end, bit 1 end -> start), cached per search.
        private static byte Dirs(EntityManager em, Entity ed)
        {
            if (s_Dirs.TryGetValue(ed, out byte d)) return d;
            d = 0;
            if (em.HasBuffer<NetSubLane>(ed) && em.HasComponent<Curve>(ed))
            {
                var c = em.GetComponentData<Curve>(ed).m_Bezier;
                var buf = em.GetBuffer<NetSubLane>(ed, true);
                for (int i = 0; i < buf.Length && d != 3; i++)
                {
                    var lane = buf[i].m_SubLane;
                    if (!em.HasComponent<NetCarLane>(lane) || em.HasComponent<Temp>(lane)) continue;
                    if (!EcsUtil.LaneBitsOf(em, lane, out LaneBits bits, out _)) continue;
                    if ((bits & (LaneBits.Parking | LaneBits.Master | LaneBits.BikeOnly | LaneBits.Point)) != 0) continue;
                    if ((bits & LaneBits.Twoway) != 0) { d = 3; break; }
                    var l = em.GetComponentData<Curve>(lane).m_Bezier;
                    float with = math.distance(l.a, c.a) + math.distance(l.d, c.d);
                    float against = math.distance(l.a, c.d) + math.distance(l.d, c.a);
                    d |= with <= against ? (byte)1 : (byte)2;
                }
            }
            s_Dirs[ed] = d;
            return d;
        }
    }
}
