using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetMasterLane = Game.Net.MasterLane;

// Temporary lanes of upgrade works: car lanes moved sideways onto the drivable asphalt of a window (UpgradeTempLanes plan, read
// through UpgradeEdgeState.PlanFor). The game drives cars along a lane's Curve, so writing the curve moves the traffic: open
// lanes onto their temporary lane, dropped lanes into the closed strip (their markers then stand in the works).
//  * Each lane piece is moved by its plan shift along the edge's right vector, eased in from both edge ends over
//    kTaperFrom..kTaperTo (the lanes meet the junction lanes where they are), refit as one cubic through 4 points.
//  * Moves ramp in over kRampSimFrames so cars glide across instead of jumping; a dropped lane's markers are placed only once
//    the edge settled (TrafficUpgrade waits for Sync = true).
//  * A lane that carries markers keeps its curve until they are gone (they stay registered on it); an open lane does not move
//    onto a strip where markers still stand.
//  * The game regenerates lanes when the edge changes: a lane whose curve is no longer the one we wrote is measured again
//    (that curve is the new original) and moved again.
//  * LaneShiftRegistry keeps the original of every moved lane, so every module keeps measuring the road layout's lanes.
//  * Saves never contain a moved curve: NeutraliseForSave / RestoreAfterSave around SerializerSystem.
namespace RealisticRoadWorks.V3.Traffic
{
    public static class TrafficLaneShift
    {
        public const float kTaperFrom = RRWConst.kUwLaneTaperFrom, kTaperTo = RRWConst.kUwLaneTaperTo;   // metres from each edge end
        public const uint kRampSimFrames = 300;               // ~5 s at normal speed
        public const float kSettle = 0.02f;                    // metres: applied counts as reached

        private sealed class LaneShiftRec
        {
            public Entity Lane, Edge;
            public Bezier4x3 Original;
            public float OriginalLength;
            public float Target;          // wanted shift (m, EDGE frame +right)
            public float From, Applied;   // shift at the ramp start / written last
            public uint RampStart;
            public Bezier4x3 Written;
            public bool Frozen;           // markers on it: keeps its curve
        }

        private sealed class EdgeShift
        {
            public Entity Edge;
            public EdgeArc Arc;
            public readonly Dictionary<Entity, LaneShiftRec> Lanes = new Dictionary<Entity, LaneShiftRec>();
            public bool Wanted;
        }

        private static readonly Dictionary<Entity, EdgeShift> s_Edges = new Dictionary<Entity, EdgeShift>();
        private static readonly List<Entity> s_Tmp = new List<Entity>(16), s_Keys = new List<Entity>(16);
        private static readonly List<float3> s_MarkerPos = new List<float3>(32);
        private static readonly Dictionary<Entity, Bezier4x3> s_Saved = new Dictionary<Entity, Bezier4x3>();

        public static int EdgeCount => s_Edges.Count;
        public static void Reset() { s_Edges.Clear(); s_Saved.Clear(); LaneShiftRegistry.Clear(); }

        // Moves the edge's car lanes toward the plan. markerLanes: lanes that carry markers now (frozen); markers: positions of
        // every marker of the edge (open lanes never move onto them). True when every lane reached its target (markers may be
        // placed).
        public static bool Sync(EntityManager em, Entity edge, EdgeArc arc, TempLanePlan plan, HashSet<Entity> markerLanes,
                                List<float3> markers, uint simFrame, bool relocate = false)
        {
            if (!s_Edges.TryGetValue(edge, out var es)) { es = new EdgeShift { Edge = edge }; s_Edges[edge] = es; }
            es.Arc = arc;
            es.Wanted = true;
            TrafficUtil.LanesInto(em, edge, s_Tmp);
            bool settled = true;
            for (int i = 0; i < s_Tmp.Count; i++)
            {
                var lane = s_Tmp[i];
                if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane) || !em.HasComponent<NetCarLane>(lane) || em.HasComponent<NetMasterLane>(lane)) continue;
                if (!em.HasComponent<Curve>(lane)) continue;
                var r = Rec(em, es, lane, edge);
                float centre = LaneSection.Probe(arc, r.Original, 0f, LaneBits.None).Centre;
                float want = plan != null ? plan.ShiftOf(centre) : 0f;
                // under a half closure a dropped lane stays where it is and is closed like a lane of the closed half
                bool retired = relocate && plan != null && plan.Dropped(centre);
                if (retired) want = 0f;
                if (LaneShiftRegistry.Retired(lane) != retired)
                {
                    LaneShiftRegistry.SetRetired(lane, retired);
                    if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
                }
                r.Frozen = markerLanes != null && markerLanes.Contains(lane);
                if (r.Frozen) { if (math.abs(r.Applied - r.Target) > kSettle) settled = false; continue; }
                if (want != 0f && math.abs(want - r.Applied) > kSettle && plan.LaneSlot[plan.LaneAt(centre)] >= 0
                    && MarkerUnder(arc, centre + want, 1.5f, markers)) { settled = false; continue; }
                if (math.abs(want - r.Target) > kSettle) { r.From = r.Applied; r.Target = want; r.RampStart = simFrame; }
                if (!Advance(em, es, r, simFrame)) settled = false;
                // under a half closure a lane that arrived on a temporary lane of the open half carries traffic (not closed)
                bool reloc = relocate && want != 0f && plan.LaneSlot[plan.LaneAt(centre)] >= 0 && math.abs(r.Applied - r.Target) <= kSettle;
                if (LaneShiftRegistry.Relocated(lane) != reloc)
                {
                    LaneShiftRegistry.SetRelocated(lane, reloc);
                    if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);   // the closure re-applies it
                }
            }
            return settled;
        }

        // Moves every lane of the edge back (markers on a lane keep it where it is until they are gone).
        public static void Release(EntityManager em, Entity edge, HashSet<Entity> markerLanes, uint simFrame)
        {
            if (!s_Edges.TryGetValue(edge, out var es)) return;
            es.Wanted = false;
            foreach (var r in es.Lanes.Values)
            {
                if (LaneShiftRegistry.Retired(r.Lane) || LaneShiftRegistry.Relocated(r.Lane))
                {
                    LaneShiftRegistry.SetRetired(r.Lane, false);
                    LaneShiftRegistry.SetRelocated(r.Lane, false);
                    if (TrafficUtil.Alive(em, r.Lane) && !em.HasComponent<PathfindUpdated>(r.Lane)) em.AddComponent<PathfindUpdated>(r.Lane);
                }
                r.Frozen = markerLanes != null && markerLanes.Contains(r.Lane);
                if (r.Frozen) continue;
                if (math.abs(r.Target) > kSettle) { r.From = r.Applied; r.Target = 0f; r.RampStart = simFrame; }
            }
        }

        // Every update after the edges synced: ramps, re-applies after regeneration, forgets lanes that are back and edges that
        // left the registry (moved back first).
        public static void Maintain(EntityManager em, uint simFrame)
        {
            if (s_Edges.Count == 0) return;
            s_Keys.Clear();
            foreach (var kv in s_Edges) s_Keys.Add(kv.Key);
            for (int k = 0; k < s_Keys.Count; k++)
            {
                var es = s_Edges[s_Keys[k]];
                if (!TrafficUtil.Alive(em, es.Edge))
                {
                    foreach (var r in es.Lanes.Values) LaneShiftRegistry.Remove(r.Lane);
                    s_Edges.Remove(es.Edge);
                    continue;
                }
                if (!SiteRegistry.Edges.ContainsKey(es.Edge)) Release(em, es.Edge, null, simFrame);
                s_Tmp.Clear();
                foreach (var r in es.Lanes.Values)
                {
                    if (!TrafficUtil.Alive(em, r.Lane)) { s_Tmp.Add(r.Lane); continue; }
                    if (!r.Frozen) Advance(em, es, r, simFrame);
                    if (!es.Wanted && math.abs(r.Applied) <= kSettle && math.abs(r.Target) <= kSettle) s_Tmp.Add(r.Lane);
                }
                for (int i = 0; i < s_Tmp.Count; i++)
                {
                    if (es.Lanes.TryGetValue(s_Tmp[i], out var r) && TrafficUtil.Alive(em, r.Lane) && math.abs(r.Applied) > 0f) Write(em, r, r.Original, r.OriginalLength);
                    es.Lanes.Remove(s_Tmp[i]);
                    LaneShiftRegistry.Remove(s_Tmp[i]);
                }
                if (!es.Wanted && es.Lanes.Count == 0) s_Edges.Remove(es.Edge);
            }
        }

        // Is the edge's lane with this original EDGE-frame centre moved now (beyond kMinShift)?
        public static bool Moved(Entity edge, Entity lane) =>
            s_Edges.TryGetValue(edge, out var es) && es.Lanes.TryGetValue(lane, out var r) && math.abs(r.Applied) >= UpgradeTempLanes.kMinShift;

        // Every lane of the edge reached its target.
        public static bool Settled(Entity edge)
        {
            if (!s_Edges.TryGetValue(edge, out var es)) return true;
            foreach (var r in es.Lanes.Values) if (math.abs(r.Applied - r.Target) > kSettle) return false;
            return true;
        }

        // ------------------------------------------------------------------ save guard
        public static int NeutraliseForSave(EntityManager em)
        {
            s_Saved.Clear();
            foreach (var es in s_Edges.Values)
                foreach (var r in es.Lanes.Values)
                {
                    if (!TrafficUtil.Alive(em, r.Lane) || math.abs(r.Applied) <= 0f) continue;
                    var live = em.GetComponentData<Curve>(r.Lane);
                    if (!LaneShiftRegistry.Same(live.m_Bezier, r.Written)) continue;
                    s_Saved[r.Lane] = r.Written;
                    live.m_Bezier = r.Original;
                    live.m_Length = r.OriginalLength;
                    em.SetComponentData(r.Lane, live);
                }
            return s_Saved.Count;
        }

        public static int RestoreAfterSave(EntityManager em)
        {
            int n = 0;
            foreach (var kv in s_Saved)
            {
                if (!TrafficUtil.Alive(em, kv.Key)) continue;
                var c = em.GetComponentData<Curve>(kv.Key);
                c.m_Bezier = kv.Value;
                c.m_Length = MathUtils.Length(kv.Value);
                em.SetComponentData(kv.Key, c);
                n++;
            }
            s_Saved.Clear();
            return n;
        }

        // ------------------------------------------------------------------ internals
        private static LaneShiftRec Rec(EntityManager em, EdgeShift es, Entity lane, Entity edge)
        {
            var live = em.GetComponentData<Curve>(lane);
            if (es.Lanes.TryGetValue(lane, out var r))
            {
                if (r.Applied == 0f) { r.Original = live.m_Bezier; r.OriginalLength = live.m_Length; r.Written = live.m_Bezier; return r; }
                // regenerated by the game (or moved by someone else): that curve is the new original
                if (math.abs(r.Applied) > 0f && !LaneShiftRegistry.Same(live.m_Bezier, r.Written))
                {
                    r.Original = live.m_Bezier;
                    r.OriginalLength = live.m_Length;
                    r.From = 0f;
                    r.Applied = 0f;
                    r.RampStart = RRWClock.SimFrame;
                    r.Written = live.m_Bezier;
                    LaneShiftRegistry.Remove(lane);
                }
                return r;
            }
            r = new LaneShiftRec { Lane = lane, Edge = edge, Original = live.m_Bezier, OriginalLength = live.m_Length, Written = live.m_Bezier };
            es.Lanes[lane] = r;
            return r;
        }

        // One ramp step. True when the lane reached its target.
        private static bool Advance(EntityManager em, EdgeShift es, LaneShiftRec r, uint simFrame)
        {
            if (math.abs(r.Applied - r.Target) <= kSettle && math.abs(r.Applied - r.Target) < 1e-4f) return true;
            float t = kRampSimFrames == 0 ? 1f : math.saturate((simFrame - r.RampStart) / (float)kRampSimFrames);
            if (simFrame < r.RampStart) t = 1f;
            t = t * t * (3f - 2f * t);
            float d = math.lerp(r.From, r.Target, t);
            if (t >= 1f) d = r.Target;
            if (math.abs(d - r.Applied) < 1e-3f && t < 1f) return false;
            if (math.abs(d) < 1e-4f) Write(em, r, r.Original, r.OriginalLength);
            else
            {
                var b = Shifted(r.Original, es.Arc, d);
                Write(em, r, b, MathUtils.Length(b));
            }
            r.Applied = d;
            if (math.abs(d) < 1e-4f) { LaneShiftRegistry.Remove(r.Lane); LaneShiftRegistry.SetRelocated(r.Lane, false); }
            else LaneShiftRegistry.Set(r.Lane, r.Original, r.Written);
            return math.abs(r.Applied - r.Target) <= kSettle;
        }

        private static void Write(EntityManager em, LaneShiftRec r, Bezier4x3 b, float length)
        {
            var c = em.GetComponentData<Curve>(r.Lane);
            c.m_Bezier = b;
            c.m_Length = length;
            em.SetComponentData(r.Lane, c);
            r.Written = b;
            if (!em.HasComponent<PathfindUpdated>(r.Lane)) em.AddComponent<PathfindUpdated>(r.Lane);
        }

        // The lane piece moved d metres along the edge's right vector, eased in from both edge ends; one cubic through the moved
        // points at t = 0, 1/3, 2/3, 1.
        public static Bezier4x3 Shifted(Bezier4x3 b, EdgeArc arc, float d)
        {
            float3 q0 = Moved(b, arc, d, 0f), q1 = Moved(b, arc, d, 1f / 3f), q2 = Moved(b, arc, d, 2f / 3f), q3 = Moved(b, arc, d, 1f);
            return new Bezier4x3(q0, (-5f * q0 + 18f * q1 - 9f * q2 + 2f * q3) / 6f, (2f * q0 - 9f * q1 + 18f * q2 - 5f * q3) / 6f, q3);
        }

        private static float3 Moved(Bezier4x3 b, EdgeArc arc, float d, float t)
        {
            float3 p = MathUtils.Position(b, t);
            float s = arc.Project(p);
            float L = arc.Length;
            float taperTo = math.min(kTaperTo, math.max(kTaperFrom + 1f, L * 0.5f - 1f));
            float w = math.smoothstep(kTaperFrom, taperTo, s) * math.smoothstep(kTaperFrom, taperTo, L - s);
            float3 r = arc.Right(s);
            r.y = 0f;
            return p + math.normalizesafe(r) * d * w;
        }

        private static bool MarkerUnder(EdgeArc arc, float lateral, float halfWidth, List<float3> markers)
        {
            if (markers == null) return false;
            float need = halfWidth + RRWConst.kBlockerRadius + TrafficConst.kDropOpenLaneClear;
            for (int i = 0; i < markers.Count; i++)
            {
                float s = arc.Project(markers[i]);
                float lat = math.dot((markers[i] - arc.Position(s)).xz, math.normalizesafe(arc.Right(s).xz));
                if (math.abs(lat - lateral) < need) return true;
            }
            return false;
        }
    }
}
