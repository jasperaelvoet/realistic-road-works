using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using Moving = Game.Objects.Moving;
using NetCarLane = Game.Net.CarLane;
using NetCarLaneFlags = Game.Net.CarLaneFlags;
using NetMasterLane = Game.Net.MasterLane;
using NetSubLane = Game.Net.SubLane;
using ParkedCar = Game.Vehicles.ParkedCar;
using Vehicle = Game.Vehicles.Vehicle;

// One-lane alternating operation of upgrade works ("shuttle"): when the works leave room for one temporary lane only, both
// directions share it (TrafficLaneShift moves both lanes onto it) and portable signals at the two ends of the section let one
// direction through at a time.
//  * The signals are the game's own lane signals: the junction lanes entering the section at each chain end get
//    CarLaneFlags.TrafficLights and a LaneSignal (Go / Stop), the lanes leading to them CarLaneFlags.Approach (cars only stop for
//    a signal they reach from an approach lane). Only chain ends without traffic lights of their own (the game's lights would
//    overrule ours) and sections up to RRWConst.kUwShuttleMaxLength (UpgradeShuttle.SignalsPossible).
//  * Cycle: A green, all red, B green, all red. Green kUwShuttleGreenSec; the all-red lasts at least kUwShuttleClearMinSec and
//    until no vehicle is left on the section (at most kUwShuttleClearMaxSec): actuated clearance.
//  * Lane flags we set are restored and our LaneSignals removed when the shuttle ends, before every save, and re-armed when the
//    game regenerates a lane.
namespace RealisticRoadWorks.V3.Traffic
{
    public static class TrafficShuttle
    {
        private sealed class Ctl
        {
            public uint ProjectId;
            public Entity NodeA, NodeB;
            public readonly List<Entity> SigA = new List<Entity>(4), SigB = new List<Entity>(4);
            public readonly Dictionary<Entity, NetCarLaneFlags> Orig = new Dictionary<Entity, NetCarLaneFlags>(16);
            public readonly HashSet<Entity> AddedSignal = new HashSet<Entity>();
            public bool Armed;
            public int Phase = 3;           // 0 A green, 1 all red, 2 B green, 3 all red
            public uint PhaseStart;
            public int OnSection;
            public string Why = "";
        }

        private static readonly Dictionary<uint, Ctl> s_Ctl = new Dictionary<uint, Ctl>();
        private static readonly List<uint> s_Keys = new List<uint>(8);
        private static readonly List<Entity> s_Lanes = new List<Entity>(32), s_Tmp = new List<Entity>(16);

        public static int ActiveCount => s_Ctl.Count;
        public static void Reset() { s_Ctl.Clear(); UpgradeShuttle.GreenAt.Clear(); }

        // Signals can run the project's section (end nodes without lights, short single chain).
        public static bool Allowed(EntityManager em, uint projectId) =>
            SiteRegistry.TryGetProject(projectId, out var p) && UpgradeShuttle.SignalsPossible(em, p.Edges);

        // Phase of a project's shuttle for props / UI: -1 none, 0 A green, 1 / 3 all red, 2 B green. nodeA / nodeB: the ends.
        public static int PhaseOf(uint projectId, out Entity nodeA, out Entity nodeB)
        {
            nodeA = nodeB = Entity.Null;
            if (!s_Ctl.TryGetValue(projectId, out var c) || !c.Armed) return -1;
            nodeA = c.NodeA; nodeB = c.NodeB;
            return c.Phase;
        }

        // Every update after the edges synced. wanted: project ids whose applied plan shares one lane on some edge, settled.
        // unsettled: projects whose lanes are still moving: a set of signals is armed only once they arrived (an armed one stays).
        public static void Step(EntityManager em, HashSet<uint> wanted, HashSet<uint> unsettled, uint simFrame)
        {
            s_Keys.Clear();
            foreach (var kv in s_Ctl) s_Keys.Add(kv.Key);
            for (int i = 0; i < s_Keys.Count; i++)
                if (!wanted.Contains(s_Keys[i])) { Disarm(em, s_Ctl[s_Keys[i]], "the works no longer share one lane"); s_Ctl.Remove(s_Keys[i]); }
            foreach (var id in wanted)
            {
                if (!SiteRegistry.TryGetProject(id, out var proj)) continue;
                if (!s_Ctl.TryGetValue(id, out var c))
                {
                    if (unsettled.Contains(id)) continue;
                    c = new Ctl { ProjectId = id, PhaseStart = simFrame };
                    s_Ctl[id] = c;
                }
                if (!c.Armed || !StillArmed(em, c)) Arm(em, c, proj, simFrame);
                if (c.Armed) Cycle(em, c, proj, simFrame);
            }
        }

        public static int NeutraliseForSave(EntityManager em)
        {
            int n = 0;
            foreach (var c in s_Ctl.Values) if (c.Armed) { Disarm(em, c, null); n++; }
            return n;   // re-armed by the next Step
        }

        // ------------------------------------------------------------------ arming
        private static void Arm(EntityManager em, Ctl c, ProjectRecord proj, uint simFrame)
        {
            if (c.Armed) Disarm(em, c, null);
            if (!UpgradeShuttle.ChainEnds(em, proj.Edges, out var a, out var ea, out var b, out var eb)) { c.Why = "no two chain ends"; return; }
            c.NodeA = a; c.NodeB = b;
            int sa = ArmEnd(em, c, a, ea, c.SigA), sb = ArmEnd(em, c, b, eb, c.SigB);
            if (sa == 0 || sb == 0)
            {
                Disarm(em, c, null);
                c.Why = "no entry lane at " + (sa == 0 ? "start" : "end");
                RRWLog.Once("traffic-shuttle-arm-p" + c.ProjectId, "WARN traffic p" + c.ProjectId + " one-lane signals not armed: " + c.Why);
                return;
            }
            c.Armed = true;
            c.Phase = 3;   // start with all red: whoever is on the section leaves first
            c.PhaseStart = simFrame;
            Apply(em, c);
            RRWLog.Info("traffic p" + c.ProjectId + " one-lane alternating operation: signals at node " + a.Index + " (" + sa + " lane(s)) and node " +
                        b.Index + " (" + sb + " lane(s))");
        }

        // The junction lanes of `node` that enter the section's edge `edge`: TrafficLights + LaneSignal; their feeding lanes Approach.
        private static int ArmEnd(EntityManager em, Ctl c, Entity node, Entity edge, List<Entity> sig)
        {
            sig.Clear();
            if (!em.HasBuffer<NetSubLane>(node) || !em.HasBuffer<NetSubLane>(edge)) return 0;
            // starts of the edge's car lane pieces at this node
            var starts = new List<(PathNode n, float3 p)>(8);
            var eb = em.GetBuffer<NetSubLane>(edge, true);
            for (int i = 0; i < eb.Length; i++)
            {
                var l = eb[i].m_SubLane;
                if (!IsCar(em, l)) continue;
                var bz = LaneShiftRegistry.MeasureCurve(em, l);
                starts.Add((em.GetComponentData<Lane>(l).m_StartNode, bz.a));
            }
            var nb = em.GetBuffer<NetSubLane>(node, true);
            s_Tmp.Clear();
            for (int i = 0; i < nb.Length; i++) s_Tmp.Add(nb[i].m_SubLane);
            foreach (var nl in s_Tmp)
            {
                if (!IsCar(em, nl)) continue;
                var ln = em.GetComponentData<Lane>(nl);
                float3 end = em.GetComponentData<Curve>(nl).m_Bezier.d;
                bool enters = false;
                foreach (var st in starts) if (ln.m_EndNode.EqualsIgnoreCurvePos(st.n) || math.distance(end, st.p) < 0.15f) { enters = true; break; }
                if (!enters) continue;
                SetFlag(em, c, nl, NetCarLaneFlags.TrafficLights);
                if (!em.HasComponent<LaneSignal>(nl)) { em.AddComponentData(nl, new LaneSignal()); c.AddedSignal.Add(nl); }
                sig.Add(nl);
                // feeding lanes on the node's other edges
                float3 start = em.GetComponentData<Curve>(nl).m_Bezier.a;
                if (!em.HasBuffer<ConnectedEdge>(node)) continue;
                var ce = em.GetBuffer<ConnectedEdge>(node, true);
                for (int k = 0; k < ce.Length; k++)
                {
                    var oe = ce[k].m_Edge;
                    if (oe == edge || !em.HasBuffer<NetSubLane>(oe)) continue;
                    var ob = em.GetBuffer<NetSubLane>(oe, true);
                    for (int j = 0; j < ob.Length; j++)
                    {
                        var pl = ob[j].m_SubLane;
                        if (!IsCar(em, pl)) continue;
                        bool feeds = em.GetComponentData<Lane>(pl).m_EndNode.EqualsIgnoreCurvePos(ln.m_StartNode)
                                     || math.distance(em.GetComponentData<Curve>(pl).m_Bezier.d, start) < 0.15f;
                        if (feeds) SetFlag(em, c, pl, NetCarLaneFlags.Approach);
                    }
                }
            }
            return sig.Count;
        }

        private static bool IsCar(EntityManager em, Entity l) =>
            TrafficUtil.Alive(em, l) && !em.HasComponent<Temp>(l) && em.HasComponent<NetCarLane>(l) && !em.HasComponent<NetMasterLane>(l)
            && em.HasComponent<Lane>(l) && em.HasComponent<Curve>(l);

        private static void SetFlag(EntityManager em, Ctl c, Entity lane, NetCarLaneFlags flag)
        {
            var cl = em.GetComponentData<NetCarLane>(lane);
            if (!c.Orig.ContainsKey(lane)) c.Orig[lane] = cl.m_Flags;
            if ((cl.m_Flags & flag) != 0) return;
            cl.m_Flags |= flag;
            em.SetComponentData(lane, cl);
        }

        private static bool StillArmed(EntityManager em, Ctl c)
        {
            for (int k = 0; k < 2; k++)
                foreach (var l in k == 0 ? c.SigA : c.SigB)
                    if (!TrafficUtil.Alive(em, l) || !em.HasComponent<LaneSignal>(l)
                        || (em.GetComponentData<NetCarLane>(l).m_Flags & NetCarLaneFlags.TrafficLights) == 0) return false;
            return true;
        }

        private static void Disarm(EntityManager em, Ctl c, string why)
        {
            foreach (var kv in c.Orig)
            {
                if (!TrafficUtil.Alive(em, kv.Key) || !em.HasComponent<NetCarLane>(kv.Key)) continue;
                var cl = em.GetComponentData<NetCarLane>(kv.Key);
                cl.m_Flags = kv.Value;
                em.SetComponentData(kv.Key, cl);
            }
            foreach (var l in c.AddedSignal) if (TrafficUtil.Alive(em, l) && em.HasComponent<LaneSignal>(l)) em.RemoveComponent<LaneSignal>(l);
            bool was = c.Armed;
            if (why != null) UpgradeShuttle.GreenAt.Remove(c.ProjectId);
            c.Orig.Clear();
            c.AddedSignal.Clear();
            c.SigA.Clear();
            c.SigB.Clear();
            c.Armed = false;
            if (was && why != null) RRWLog.Info("traffic p" + c.ProjectId + " one-lane signals removed (" + why + ")");
        }

        // ------------------------------------------------------------------ cycle
        private static void Cycle(EntityManager em, Ctl c, ProjectRecord proj, uint now)
        {
            c.OnSection = OnSection(em, proj);
            int age = (int)(now - c.PhaseStart);
            int green = (int)(RRWConst.kUwShuttleGreenSec * 60f), clrMin = (int)(RRWConst.kUwShuttleClearMinSec * 60f), clrMax = (int)(RRWConst.kUwShuttleClearMaxSec * 60f);
            int next = c.Phase;
            if ((c.Phase == 0 || c.Phase == 2) && age >= green) next = c.Phase + 1;
            else if ((c.Phase == 1 || c.Phase == 3) && age >= clrMin && (c.OnSection == 0 || age >= clrMax)) next = c.Phase == 1 ? 2 : 0;
            if (next == c.Phase) return;
            if ((c.Phase == 1 || c.Phase == 3) && c.OnSection > 0)
                RRWLog.Once("traffic-shuttle-clear-p" + c.ProjectId, "WARN traffic p" + c.ProjectId + " one-lane all-red ended after the cap with " + c.OnSection + " vehicle(s) still on the section");
            c.Phase = next;
            c.PhaseStart = now;
            Apply(em, c);
        }

        private static void Apply(EntityManager em, Ctl c)
        {
            UpgradeShuttle.GreenAt[c.ProjectId] = c.Phase == 0 ? c.NodeA : c.Phase == 2 ? c.NodeB : Entity.Null;
            for (int k = 0; k < 2; k++)
            {
                bool go = (k == 0 && c.Phase == 0) || (k == 1 && c.Phase == 2);
                foreach (var l in k == 0 ? c.SigA : c.SigB)
                {
                    if (!TrafficUtil.Alive(em, l) || !em.HasComponent<LaneSignal>(l)) continue;
                    var s = em.GetComponentData<LaneSignal>(l);
                    s.m_Signal = go ? LaneSignalType.Go : LaneSignalType.Stop;
                    s.m_Default = (sbyte)s.m_Signal;
                    em.SetComponentData(l, s);
                }
            }
        }

        // Vehicles driving on the section's car lanes (parked cars and our machines do not count).
        private static int OnSection(EntityManager em, ProjectRecord proj)
        {
            int n = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                TrafficUtil.LanesInto(em, proj.Edges[i], s_Lanes);
                for (int k = 0; k < s_Lanes.Count; k++)
                {
                    var l = s_Lanes[k];
                    if (!IsCar(em, l) || !em.HasBuffer<LaneObject>(l)) continue;
                    var buf = em.GetBuffer<LaneObject>(l, true);
                    for (int j = 0; j < buf.Length; j++)
                    {
                        var o = buf[j].m_LaneObject;
                        if (!em.Exists(o) || !em.HasComponent<Vehicle>(o) || em.HasComponent<ParkedCar>(o) || em.HasComponent<RRWMachine>(o)) continue;
                        n++;
                    }
                }
            }
            return n;
        }

        public static string Describe()
        {
            var sb = new System.Text.StringBuilder("shuttles=" + s_Ctl.Count);
            foreach (var c in s_Ctl.Values)
                sb.Append(" | p").Append(c.ProjectId).Append(c.Armed ? " armed" : " not armed (" + c.Why + ")").Append(" phase=").Append(c.Phase)
                  .Append(" A=").Append(c.SigA.Count).Append(" B=").Append(c.SigB.Count).Append(" onSection=").Append(c.OnSection);
            return sb.ToString();
        }
    }
}
