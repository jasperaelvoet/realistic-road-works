using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

// Pure-ish polygon geometry for the Surfaces module: junction-end classification and
// trims, mitred interior nodes, round dead-end caps, Roads-layer taper at visible junctions, adaptive curve sampling,
// convex-hull node caps and the decal vertex-Y rule. Main thread; reusable buffers, no per-call allocation.
namespace RealisticRoadWorks.V3.Surfaces
{
    // Lateral band of one strip: [Left, Right] plus the layer margin that is tapered away near visible junction ends.
    internal struct BandLat
    {
        public float Left, Right;    // full band including margin (EdgeSection.Band)
        public float Margin;         // part of each side that is the layer margin (removed in the taper zone)
        public bool TaperStart, TaperEnd;
        public float TaperS0, TaperS1; // trims (arc metres from each end) where the taper zone starts
        public float MinWidth;         // narrowest band drawn (0 = kMinBandWidth); TempMarking lines are 0.15 m wide by default
        // A C4 half band has no margin on its inner (split) side: only the outer side is tapered at visible junctions,
        // so the two halves never open a gap over the centre line there
        public bool NoMarginLeft, NoMarginRight;
    }

    internal static class SurfaceGeom
    {
        public const float kTaperFlat = 2f;     // Roads-layer bands use margin 0 for the first 2 m past a visible trim
        public const float kTaperRamp = 1f;     // then grow back to the full margin over 1 m (no notch)
        public const float kTouch = 0.01f;      // a logical span "touches" an edge end when it is this close
        public const float kMinGeomLength = 0.3f;
        public const float kMinBandWidth = 0.5f;
        public const float kVertexAboveFloor = 0.75f; // vertex Y = min(CPU terrain, floor + 0.75)

        private static readonly List<Entity> s_Conn = new List<Entity>(8);
        private static readonly List<float> s_Stations = new List<float>(128);
        private static readonly List<float3> s_Right = new List<float3>(128);
        private static readonly List<float2> s_Hull = new List<float2>(32);
        private static readonly List<float2> s_HullOut = new List<float2>(32);

        // ------------------------------------------------------------------ classification

        // Upgrade works (HalfWidth) are never mode-A works edges: their road stays visible, so their junction ends clip to the
        // edge geometry and never get a node cap.
        static bool IsModeAWorksEdge(EntityManager em, Entity e)
        {
            if (!SiteRegistry.Edges.ContainsKey(e) || !em.HasComponent<RoadWorksSite>(e)) return false;
            return em.GetComponentData<RoadWorksSite>(e).Mode == VisualMode.FullDig;
        }

        // Outward tangent of an edge at one of its nodes (pointing from the node into the edge), xz-normalised.
        public static float3 OutwardTangent(EntityManager em, Entity edge, Entity node)
        {
            if (!em.HasComponent<Curve>(edge) || !em.HasComponent<Edge>(edge)) return float3.zero;
            var c = em.GetComponentData<Curve>(edge).m_Bezier;
            var ed = em.GetComponentData<Edge>(edge);
            float3 t = ed.m_Start == node ? MathUtils.Tangent(c, 0f) : -MathUtils.Tangent(c, 1f);
            float2 d = math.normalizesafe(t.xz);
            return new float3(d.x, 0f, d.y);
        }

        // openRoad: the edge's own road stays visible and in use (upgrade works): a dead end is clipped to the edge geometry
        // (OpenDeadEnd) instead of getting the round cap a hidden works road needs over its cul-de-sac.
        public static EndKind ClassifyEnd(EntityManager em, Entity edge, Entity node, bool openRoad, out float3 cut)
        {
            cut = float3.zero;
            if (!EcsUtil.Alive(em, node)) return EndKind.Visible;
            EcsUtil.ConnectedEdges(em, node, s_Conn);
            int n = s_Conn.Count;
            if (n <= 1) return openRoad ? EndKind.OpenDeadEnd : EndKind.DeadEnd;
            bool allWorks = true;
            for (int i = 0; i < n; i++)
                if (!IsModeAWorksEdge(em, s_Conn[i])) { allWorks = false; break; }
            if (!allWorks) return EndKind.Visible;
            if (n >= 3) return EndKind.WorksJunction;
            Entity other = s_Conn[0] == edge ? s_Conn[1] : s_Conn[0];
            float3 tThis = OutwardTangent(em, edge, node);
            float3 tOther = OutwardTangent(em, other, node);
            // travel through the node: arrive along -tThis, leave along tOther; mitre = bisector of both right vectors
            float3 rA = EdgeArc.RightOf(-tThis), rB = EdgeArc.RightOf(tOther);
            float2 b = rA.xz + rB.xz;
            if (math.lengthsq(b) > 1e-4f) { b = math.normalize(b); cut = new float3(b.x, 0f, b.y); }
            return EndKind.Interior;
        }

        // Edge geometry trims measured on our arc (same as EcsUtil.MeasureSection, re-measured here because a road drawn
        // into a works node changes the trims without changing the Director's geometry hash).
        public static uint CornerHash(EntityManager em, Entity edge)
        {
            if (!em.HasComponent<EdgeGeometry>(edge)) return 0u;
            var g = em.GetComponentData<EdgeGeometry>(edge);
            return math.hash(new float4x3(new float4(g.m_Start.m_Left.a.xz, g.m_Start.m_Right.a.xz),
                                          new float4(g.m_End.m_Left.d.xz, g.m_End.m_Right.d.xz),
                                          new float4(g.m_Start.m_Left.a.y, g.m_End.m_Left.d.y, 0f, 0f)));
        }

        public static void MeasureTrims(EntityManager em, Entity edge, EdgeArc arc, out float trimStart, out float trimEnd)
        {
            trimStart = trimEnd = 0f;
            if (arc == null || !em.HasComponent<EdgeGeometry>(edge)) return;
            var g = em.GetComponentData<EdgeGeometry>(edge);
            float a = math.max(arc.Project(g.m_Start.m_Left.a), arc.Project(g.m_Start.m_Right.a));
            float d = math.min(arc.Project(g.m_End.m_Left.d), arc.Project(g.m_End.m_Right.d));
            trimStart = math.clamp(a, 0f, arc.Length * 0.5f);
            trimEnd = math.clamp(arc.Length - d, 0f, arc.Length * 0.5f);
        }

        public static uint HashEnds(in EdgeEnds e)
        {
            return math.hash(new float4(e.TrimStart, e.TrimEnd, (float)e.Start + 8f * (float)e.End, e.CutStart.x + 3f * e.CutStart.z))
                 ^ math.hash(new float2(e.CutEnd.x, e.CutEnd.z)) * 31u;
        }

        // ------------------------------------------------------------------ geometric range of a strip

        // Maps a logical edge-local span [ls0, ls1] to the s range actually drawn, per the junction rules.
        // Returns false when nothing is left to draw (e.g. the whole piece lies under a visible junction).
        public static bool GeometricRange(SurfaceLayer layer, float ls0, float ls1, float L, in EdgeEnds ends,
                                          out float gs0, out float gs1, out float3 cutStart, out float3 cutEnd,
                                          out bool roundStart, out bool roundEnd, out bool taperStart, out bool taperEnd)
        {
            bool roads = PhasePlan.IsRoadsLayer(layer);
            gs0 = ls0; gs1 = ls1;
            cutStart = cutEnd = float3.zero;
            roundStart = roundEnd = taperStart = taperEnd = false;
            bool touch0 = ls0 <= kTouch, touch1 = ls1 >= L - kTouch;
            switch (ends.Start)
            {
                case EndKind.Visible:
                case EndKind.OpenDeadEnd:
                    // never paint on a finished intersection (or the cul-de-sac of a road that stays in use): clip every piece to
                    // the edge geometry, fronts included
                    gs0 = math.max(ls0, ends.TrimStart);
                    taperStart = roads;
                    break;
                case EndKind.Interior:
                    if (touch0) { gs0 = roads ? -RRWConst.kNodeOverlap : 0f; cutStart = ends.CutStart; }
                    break;
                case EndKind.DeadEnd:
                    if (touch0) { gs0 = 0f; roundStart = true; }
                    break;
                case EndKind.WorksJunction:
                    if (roads) gs0 = math.max(ls0, math.max(0f, ends.TrimStart - RRWConst.kNodeOverlap));
                    else if (touch0) gs0 = 0f;
                    break;
            }
            switch (ends.End)
            {
                case EndKind.Visible:
                case EndKind.OpenDeadEnd:
                    gs1 = math.min(ls1, L - ends.TrimEnd);
                    taperEnd = roads;
                    break;
                case EndKind.Interior:
                    if (touch1) { gs1 = roads ? L + RRWConst.kNodeOverlap : L; cutEnd = ends.CutEnd; }
                    break;
                case EndKind.DeadEnd:
                    if (touch1) { gs1 = L; roundEnd = true; }
                    break;
                case EndKind.WorksJunction:
                    if (roads) gs1 = math.min(ls1, math.min(L, L - ends.TrimEnd + RRWConst.kNodeOverlap));
                    else if (touch1) gs1 = L;
                    break;
            }
            return gs1 - gs0 >= kMinGeomLength;
        }

        // ------------------------------------------------------------------ strip polygon

        static float TaperK(in BandLat lat, float s, float L)
        {
            float k = 1f;
            if (lat.TaperStart) k = math.min(k, math.saturate((s - (lat.TaperS0 + kTaperFlat)) / kTaperRamp));
            if (lat.TaperEnd) k = math.min(k, math.saturate(((L - lat.TaperS1 - kTaperFlat) - s) / kTaperRamp));
            return k;
        }

        static void LatAt(in BandLat lat, float s, float L, out float left, out float right)
        {
            float cut = lat.Margin * (1f - TaperK(lat, s, L));
            left = lat.Left + (lat.NoMarginLeft ? 0f : cut);
            right = lat.Right - (lat.NoMarginRight ? 0f : cut);
        }

        // Point at arc distance s and lateral offset (port of EdgeArc.Point: inner-curve clamp to 0.85 R, optional mitre).
        static float3 Point(EdgeArc arc, float s, float lateral, float3 cut)
        {
            float sc = math.clamp(s, 0f, arc.Length);
            float r = arc.SignedRadius(sc);
            if (!float.IsInfinity(r))
            {
                float lim = 0.85f * math.abs(r);
                if (r > 0f && lateral > lim) lateral = lim;
                if (r < 0f && lateral < -lim) lateral = -lim;
            }
            if (math.lengthsq(cut.xz) < 1e-6f) return arc.Offset(s, lateral);
            float3 centre = arc.Offset(s, 0f);
            float3 right = arc.Right(sc);
            float2 c = math.normalizesafe(cut.xz, right.xz);
            float d = math.dot(c, right.xz);
            if (math.abs(d) < 0.3f) return arc.Offset(s, lateral);   // degenerate mitre (> ~72 deg bend): perpendicular
            float k = lateral / d;
            return new float3(centre.x + c.x * k, centre.y, centre.z + c.y * k);
        }

        static void AddStation(List<float> ss, float s, float s0, float s1)
        {
            if (s > s0 + 0.05f && s < s1 - 0.05f) ss.Add(s);
        }

        // Closed polygon (no duplicate closing point), same winding as the verified-in-game prototype:
        // left side s0 -> s1, [end cap], right side s1 -> s0, [start cap]. Y = curve Y (caller sets the decal Y).
        public static void Strip(EdgeArc arc, float s0, float s1, in BandLat lat, float3 cutStart, float3 cutEnd,
                                 bool roundStart, bool roundEnd, List<float3> output)
        {
            output.Clear();
            if (arc == null || s1 - s0 < kMinGeomLength) return;
            float L = arc.Length;
            float maxLat = math.max(math.abs(lat.Left), math.abs(lat.Right));
            var ss = s_Stations;
            ss.Clear();
            ss.Add(s0);
            float s = s0;
            while (s < s1 - 1e-3f)
            {
                float st = arc.AdaptiveStep(math.clamp(s, 0f, L), maxLat, RRWConst.kAreaNodeStep);
                float next = math.min(s1, s + st);
                if (s1 - next < RRWConst.kAreaMinNodeGap) next = s1;
                ss.Add(next);
                s = next;
            }
            // taper breakpoints so the narrowed zone is exact
            if (lat.TaperStart)
            {
                AddStation(ss, lat.TaperS0 + kTaperFlat, s0, s1);
                AddStation(ss, lat.TaperS0 + kTaperFlat + kTaperRamp, s0, s1);
            }
            if (lat.TaperEnd)
            {
                AddStation(ss, L - lat.TaperS1 - kTaperFlat, s0, s1);
                AddStation(ss, L - lat.TaperS1 - kTaperFlat - kTaperRamp, s0, s1);
            }
            if (lat.TaperStart || lat.TaperEnd)
            {
                ss.Sort();
                // drop stations closer than the minimum gap (keep both ends)
                for (int i = ss.Count - 2; i >= 1; i--)
                    if (ss[i + 1] - ss[i] < RRWConst.kAreaMinNodeGap || ss[i] - ss[i - 1] < RRWConst.kAreaMinNodeGap * 0.5f) ss.RemoveAt(i);
            }
            int n = ss.Count;
            if (n < 2) return;
            var right = s_Right;
            right.Clear();
            for (int i = 0; i < n; i++)
            {
                float si = ss[i];
                LatAt(lat, si, L, out float l, out float r);
                float minW = lat.MinWidth > 0f ? lat.MinWidth : kMinBandWidth;
                if (r - l < minW) { float m = (l + r) * 0.5f; l = m - minW * 0.5f; r = m + minW * 0.5f; }
                float3 c = i == 0 ? cutStart : i == n - 1 ? cutEnd : float3.zero;
                output.Add(Point(arc, si, l, c));
                right.Add(Point(arc, si, r, c));
            }
            if (roundEnd && math.lengthsq(cutEnd.xz) < 1e-6f) AddCap(arc, ss[n - 1], lat, L, true, output);
            for (int i = n - 1; i >= 0; i--) output.Add(right[i]);
            if (roundStart && math.lengthsq(cutStart.xz) < 1e-6f) AddCap(arc, ss[0], lat, L, false, output);
            Cleanup(output);
        }

        // Half-disc beyond the strip end (dead-end cul-de-sac). End: from the left point forward round to the right point;
        // start: from the right point backward round to the left point. Endpoints excluded (already in the polygon).
        static void AddCap(EdgeArc arc, float s, in BandLat lat, float L, bool atEnd, List<float3> output)
        {
            LatAt(lat, s, L, out float l, out float r);
            float rad = (r - l) * 0.5f;
            if (rad < 0.5f) return;
            float sc = math.clamp(s, 0f, L);
            float3 centre = arc.Offset(s, (l + r) * 0.5f);
            float3 dir = arc.Direction(sc);
            float2 fwd = math.normalizesafe(dir.xz, new float2(0f, 1f));
            float2 rgt = new float2(fwd.y, -fwd.x);
            int segs = math.clamp((int)math.ceil(math.PI * rad / 1.5f), 6, 16);
            for (int k = 1; k < segs; k++)
            {
                float th = math.PI * k / segs;
                float2 off = atEnd ? (-math.cos(th) * rgt + math.sin(th) * fwd) * rad
                                   : (math.cos(th) * rgt - math.sin(th) * fwd) * rad;
                output.Add(new float3(centre.x + off.x, centre.y, centre.z + off.y));
            }
        }

        // Removes consecutive near-duplicates (also across the closing seam).
        static void Cleanup(List<float3> pts)
        {
            for (int i = pts.Count - 1; i >= 1; i--)
                if (math.distancesq(pts[i].xz, pts[i - 1].xz) < 0.0025f) pts.RemoveAt(i);
            while (pts.Count > 3 && math.distancesq(pts[0].xz, pts[pts.Count - 1].xz) < 0.0025f) pts.RemoveAt(pts.Count - 1);
        }

        // ------------------------------------------------------------------ node caps

        // Convex hull (xz) of the trimmed end corners of every edge at the node, grown by kNodeOverlap, clockwise in (x, z)
        // like the strips. Y = node Y (caller sets the decal Y). Returns false when degenerate.
        private static readonly Comparison<float2> s_ByXZ = (p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y);
        // Cheap change detector for the cap polygon: the trimmed end corners of every connected edge.
        public static uint CapHash(EntityManager em, Entity node)
        {
            uint hash = 17u;
            if (!EcsUtil.Alive(em, node)) return hash;
            EcsUtil.ConnectedEdges(em, node, s_Conn);
            for (int i = 0; i < s_Conn.Count; i++)
            {
                Entity e = s_Conn[i];
                if (!em.HasComponent<EdgeGeometry>(e) || !em.HasComponent<Edge>(e)) continue;
                var g = em.GetComponentData<EdgeGeometry>(e);
                bool start = em.GetComponentData<Edge>(e).m_Start == node;
                float3 a = start ? g.m_Start.m_Left.a : g.m_End.m_Left.d;
                float3 b = start ? g.m_Start.m_Right.a : g.m_End.m_Right.d;
                hash = hash * 31u + math.hash(math.round(new float4(a.xz, b.xz) * 10f));
            }
            return hash;
        }

        public static bool NodeCapPolygon(EntityManager em, Entity node, List<float3> output)
        {
            output.Clear();
            if (!EcsUtil.Alive(em, node) || !em.HasComponent<Game.Net.Node>(node)) return false;
            float3 np = em.GetComponentData<Game.Net.Node>(node).m_Position;
            EcsUtil.ConnectedEdges(em, node, s_Conn);
            var pts = s_Hull;
            pts.Clear();
            for (int i = 0; i < s_Conn.Count; i++)
            {
                Entity e = s_Conn[i];
                if (!em.HasComponent<EdgeGeometry>(e) || !em.HasComponent<Edge>(e)) continue;
                var g = em.GetComponentData<EdgeGeometry>(e);
                bool start = em.GetComponentData<Edge>(e).m_Start == node;
                float3 a = start ? g.m_Start.m_Left.a : g.m_End.m_Left.d;
                float3 b = start ? g.m_Start.m_Right.a : g.m_End.m_Right.d;
                pts.Add(a.xz);
                pts.Add(b.xz);
            }
            if (pts.Count < 3) return false;
            float2 cen = float2.zero;
            for (int i = 0; i < pts.Count; i++) cen += pts[i];
            cen /= pts.Count;
            for (int i = 0; i < pts.Count; i++)
            {
                float2 d = pts[i] - cen;
                float len = math.length(d);
                if (len > 1e-3f) pts[i] = pts[i] + d / len * RRWConst.kNodeOverlap;
            }
            ConvexHull(pts, s_HullOut);
            if (s_HullOut.Count < 3) return false;
            // signed area (shoelace in x, z); the strips are clockwise (negative)
            float area = 0f;
            for (int i = 0; i < s_HullOut.Count; i++)
            {
                float2 p = s_HullOut[i], q = s_HullOut[(i + 1) % s_HullOut.Count];
                area += p.x * q.y - q.x * p.y;
            }
            if (math.abs(area) < 1f) return false;
            if (area > 0f) s_HullOut.Reverse();
            for (int i = 0; i < s_HullOut.Count; i++) output.Add(new float3(s_HullOut[i].x, np.y, s_HullOut[i].y));
            return true;
        }

        // Andrew's monotone chain (counter-clockwise result), reusable buffers.
        static void ConvexHull(List<float2> pts, List<float2> hull)
        {
            hull.Clear();
            pts.Sort(s_ByXZ);
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int k = 0; k < pts.Count; k++)
                {
                    float2 p = pass == 0 ? pts[k] : pts[pts.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f) hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
        }

        static float Cross(float2 o, float2 a, float2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        // ------------------------------------------------------------------ decal vertex Y

        // Vertex Y = min(CPU terrain, floor + 0.75): the floor near an uphill cut wall stays inside the +-1.5 m decal box even
        // while the CPU heightmap readback still shows the undug ground. NaN terrain -> floor.
        public static float VertexY(ref TerrainHeightData hd, bool haveHd, in RoadWorksRuntime r, in RoadWorksGround g,
                                    in TerrainProfile planned, float3 p)
        {
            float t = haveHd ? TerrainUtils.SampleHeight(ref hd, p) : float.NaN;
            if (float.IsNaN(t) || float.IsInfinity(t))
                return GroundMath.FloorWorldY(r, g, planned, p.y, float.NaN);
            float floor = GroundMath.FloorWorldY(r, g, planned, p.y, t);
            return math.min(t, floor + kVertexAboveFloor);
        }

        // Ground "floor at natural = grade" metric for the re-snap rule: clamp(0, fill floor, cut cap) relative to grade.
        public static float FloorMetric(in RoadWorksGround g)
        {
            if (!g.Known || g.m_Kind == TerrainProfileKind.Vanilla) return 0f;
            float v = 0f;
            if (!float.IsInfinity(g.m_FloorMinRel) && !float.IsNaN(g.m_FloorMinRel)) v = math.min(v, g.m_FloorMinRel);
            if (!float.IsInfinity(g.m_FloorMaxRel) && !float.IsNaN(g.m_FloorMaxRel)) v = math.max(v, g.m_FloorMaxRel);
            return v;
        }
    }
}
