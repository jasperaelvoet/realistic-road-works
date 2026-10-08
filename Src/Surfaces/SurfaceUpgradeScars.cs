using System.Collections.Generic;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

// SurfaceAreaSystem, sixth part: curing decals and scars under upgrade works.
//
// A road that gets upgrade works keeps its entity and is still in use: only its bands are worked on. A curing decal or scar
// on that road (or overlapping it) is therefore not removed like for a road that is rebuilt or replaced. It is clipped to
// what lies OUTSIDE the edge's build, rebuild and remove bands (those are dressed by the works themselves; the re-marking band
// only repaints the road it keeps, so it never clips), once per band layout:
//   * the scar polygon (as rendered) is taken into the edge's frame (arc distance s, lateral offset; beyond the ends along
//     the end tangents) and every such band rectangle [0, L] x [Lo, Hi] is subtracted from it;
//   * nothing inside the bands: the scar stays as it is; nothing outside: it is removed; otherwise the remaining pieces are
//     spawned first (same layer, fade step and age) and the old area goes once they are on screen.
// The anchor is re-read afterwards (the upgraded road has a new prefab and may have moved sideways), so the "road replaced"
// rule does not fire for it later.
namespace RealisticRoadWorks.V3.Surfaces
{
    public partial class SurfaceAreaSystem
    {
        const float kClipMinArea = 1f;          // m2: smaller pieces of a clipped scar are dropped
        const float kClipSameArea = 0.5f;       // m2: a clip that changes the area by less than this left the scar whole
        const int kClipMaxPieces = 8;

        private struct ClipV
        {
            public float S, Lat;    // edge frame of the upgraded road
            public float3 P;        // world position as rendered
        }

        private readonly List<ClipV> m_ClipIn = new List<ClipV>(64);

        // The edge carries upgrade works (its saved site has an upgrade plan with bands). Read from the site so a scar waits
        // for the clip even in the update the works are applied, before the registry has adopted them.
        internal static bool IsUpgradeSiteEdge(EntityManager em, Entity edge, out RoadWorksSite site)
        {
            site = default;
            if (!em.Exists(edge) || !em.HasComponent<RoadWorksSite>(edge)) return false;
            site = em.GetComponentData<RoadWorksSite>(edge);
            return site.Mode == VisualMode.HalfWidth && site.IsUpgrade && site.m_BandCount > 0;
        }

        // Does the clip rule apply to scar `sc` on / over `edge`? Upgrade works that are running (or applied this update and not
        // adopted yet), and never to the scar the same works left at their own completion (the topsoil of a removed strip).
        internal static bool UpgradeScarApplies(EntityManager em, Entity edge, ScarEntry sc)
        {
            if (!IsUpgradeSiteEdge(em, edge, out _)) return false;
            if (!SiteRegistry.TryGetEdge(edge, out var rec)) return true;
            if (sc.Area != null && sc.Area.ProjectId == rec.ProjectId) return false;
            return !SiteRegistry.TryGetProject(rec.ProjectId, out var p) || WorksActive(em, edge, p);
        }

        // Bands whose surface is dug, paved or broken out (build, rebuild, remove). The re-marking band spans the whole new
        // carriageway but only repaints it: a scar or curing decal there stays, so the part of the road that is kept never pops.
        static bool ClipsScars(in UpgradeBand band) => band.Kind != BandKind.Remark;

        static uint ClipSignature(in RoadWorksSite site, in Colossal.Mathematics.Bezier4x3 c) =>
            site.TailHash() ^ (math.hash(new float4x2(new float4(c.a.xz, c.b.xz), new float4(c.c.xz, c.d.xz))) * 31u);

        // Scar `sc` meets upgrade works on `edge` (anchorIndex >= 0: its anchor road). Returns the removal reason, or null when
        // the scar stays (whole or clipped).
        private string UpgradeScar(ScarEntry sc, Entity edge, int anchorIndex)
        {
            if (!IsUpgradeSiteEdge(m_Em, edge, out var site) || !m_Em.HasComponent<Curve>(edge)) return null;
            var curve = DeferredNet.WorksCurve(m_Em, edge);
            uint sig = ClipSignature(site, curve);
            int idx = sc.ClipEdges.IndexOf(edge);
            if (idx >= 0 && sc.ClipSigs[idx] == sig) return null;
            if (sc.Area == null || sc.Area.Pending || !sc.Area.Live(m_Em)) return null;   // not on screen yet: next update
            if (!CopyNodes(sc.Area.Area)) return null;
            if (anchorIndex >= 0) sc.Anchors[anchorIndex] = MakeAnchor(edge, false);
            if (idx >= 0) sc.ClipSigs[idx] = sig;
            else { sc.ClipEdges.Add(edge); sc.ClipSigs.Add(sig); }

            var arc = new EdgeArc(curve);
            float L = arc.Length;
            m_ClipIn.Clear();
            for (int i = 0; i < m_Poly.Count; i++) m_ClipIn.Add(ToEdgeFrame(arc, m_Poly[i]));
            float before = AreaOf(m_ClipIn);
            var pieces = new List<List<ClipV>> { new List<ClipV>(m_ClipIn) };
            for (int b = 0; b < site.m_BandCount && pieces.Count > 0; b++)
            {
                var band = site.Band(b);
                if (!ClipsScars(band)) continue;
                var next = new List<List<ClipV>>(pieces.Count * 2);
                for (int k = 0; k < pieces.Count; k++) SubtractRect(pieces[k], 0f, L, band.Lo, band.Hi, next);
                pieces = next;
            }
            pieces.RemoveAll(p => p.Count < 3 || AreaOf(p) < kClipMinArea);
            if (pieces.Count > kClipMaxPieces) pieces.RemoveRange(kClipMaxPieces, pieces.Count - kClipMaxPieces);
            float after = 0f;
            for (int k = 0; k < pieces.Count; k++) after += AreaOf(pieces[k]);
            string what = sc.Area.Layer + " a" + sc.Area.FadePct;
            if (math.abs(before - after) < kClipSameArea)
            {
                SurfaceState.ScarsKeptOutside++;
                RRWLog.Verbose("surfaces: scar " + what + " lies outside the upgrade bands of " + RRWLog.E(edge) + ": kept whole");
                return null;
            }
            if (pieces.Count == 0)
            {
                SurfaceState.ScarsInsideBands++;
                SurfaceState.LastScarClip = "u" + m_Now + " " + what + " inside the bands of " + RRWLog.E(edge) + ": removed";
                return "inside the upgrade works";
            }
            int spawned = 0;
            for (int k = 0; k < pieces.Count; k++)
            {
                var piece = pieces[k];
                m_Poly.Clear();
                for (int i = 0; i < piece.Count; i++) m_Poly.Add(piece[i].P);
                var n = new TrackedArea
                {
                    Layer = sc.Area.Layer, Band = sc.Area.Band, Prefab = sc.Area.Prefab, FadePct = sc.Area.FadePct,
                    Site = Entity.Null, ProjectId = sc.Area.ProjectId, Group = DerivedGroup.Scar,
                    LS0 = sc.Area.LS0, LS1 = sc.Area.LS1,
                };
                if (!SpawnArea(n, m_Poly)) continue;
                if (spawned++ == 0)
                {
                    // the fade step in flight restarts from the clipped polygon
                    if (sc.Next != null) { DeleteArea(sc.Next); sc.Next = null; }
                    Retire(sc.Area.Area, n);
                    sc.Area = n;
                    SetScarPolygon(sc, piece);
                    continue;
                }
                var e = new ScarEntry
                {
                    Area = n, Construction = sc.Construction, AgeFrames = sc.AgeFrames, Stage = sc.Stage,
                    Anchored = sc.Anchored, CreatedUpdate = sc.CreatedUpdate, WaitSince = sc.WaitSince,
                };
                e.Anchors.AddRange(sc.Anchors);
                e.ClipEdges.AddRange(sc.ClipEdges);
                e.ClipSigs.AddRange(sc.ClipSigs);
                SetScarPolygon(e, piece);
                SurfaceState.Scars.Add(e);   // appended: the caller's backwards loop does not visit it this update
            }
            if (spawned == 0) return null;   // could not spawn: the scar stays whole
            SurfaceState.ScarsClipped++;
            string line = "scar " + what + " clipped to outside the upgrade bands of " + RRWLog.E(edge) + ": " + spawned + " piece(s), "
                          + RRWLog.F(before) + " -> " + RRWLog.F(after) + " m2";
            SurfaceState.LastScarClip = "u" + m_Now + " " + line;
            RRWLog.Info("surfaces: " + line);
            return null;
        }

        static void SetScarPolygon(ScarEntry sc, List<ClipV> piece)
        {
            sc.PolyXZ.Clear();
            float2 lo = new float2(float.MaxValue), hi = new float2(float.MinValue);
            for (int i = 0; i < piece.Count; i++)
            {
                float2 p = piece[i].P.xz;
                sc.PolyXZ.Add(p);
                lo = math.min(lo, p);
                hi = math.max(hi, p);
            }
            sc.Bounds = sc.PolyXZ.Count >= 3 ? new float4(lo, hi) : new float4(1f, 1f, -1f, -1f);
            sc.ConflictRev = int.MinValue;   // re-scan the overlaps with the new polygon
        }

        // World point -> (arc distance, lateral) of the edge; beyond the ends along the end tangents.
        static ClipV ToEdgeFrame(EdgeArc arc, float3 p)
        {
            float s = arc.ProjectExtended(p);
            float3 o = arc.Offset(s, 0f);
            float3 r = arc.Right(math.clamp(s, 0f, arc.Length));
            return new ClipV { S = s, Lat = math.dot((p - o).xz, r.xz), P = p };
        }

        // Polygon minus the rectangle [s0, s1] x [lo, hi] (edge frame): the parts beside it (lateral below lo / above hi) and
        // the parts before / after it inside its lateral range. Pieces with fewer than 3 points are dropped.
        static void SubtractRect(List<ClipV> poly, float s0, float s1, float lo, float hi, List<List<ClipV>> output)
        {
            var a = new List<ClipV>(poly.Count + 4);
            var b = new List<ClipV>(poly.Count + 4);
            ClipHalf(poly, a, true, lo, true);
            if (a.Count >= 3) output.Add(a);
            a = new List<ClipV>(poly.Count + 4);
            ClipHalf(poly, a, true, hi, false);
            if (a.Count >= 3) output.Add(a);
            a = new List<ClipV>(poly.Count + 4);
            ClipHalf(poly, a, true, lo, false);
            ClipHalf(a, b, true, hi, true);
            if (b.Count < 3) return;
            a = new List<ClipV>(b.Count + 4);
            ClipHalf(b, a, false, s0, true);
            if (a.Count >= 3) output.Add(a);
            a = new List<ClipV>(b.Count + 4);
            ClipHalf(b, a, false, s1, false);
            if (a.Count >= 3) output.Add(a);
        }

        // One half-plane clip (Sutherland-Hodgman) on the lateral (or arc distance) axis: keep value <= v (below) or >= v.
        static void ClipHalf(List<ClipV> input, List<ClipV> output, bool lateral, float v, bool below)
        {
            output.Clear();
            int n = input.Count;
            if (n < 3) return;
            for (int i = 0; i < n; i++)
            {
                var cur = input[i];
                var prev = input[(i + n - 1) % n];
                float cv = lateral ? cur.Lat : cur.S, pv = lateral ? prev.Lat : prev.S;
                bool cin = below ? cv <= v : cv >= v;
                bool pin = below ? pv <= v : pv >= v;
                if (cin)
                {
                    if (!pin) output.Add(Cross(prev, cur, pv, cv, v));
                    output.Add(cur);
                }
                else if (pin) output.Add(Cross(prev, cur, pv, cv, v));
            }
        }

        static ClipV Cross(in ClipV a, in ClipV b, float av, float bv, float v)
        {
            float t = math.abs(bv - av) > 1e-6f ? math.saturate((v - av) / (bv - av)) : 0f;
            return new ClipV { S = math.lerp(a.S, b.S, t), Lat = math.lerp(a.Lat, b.Lat, t), P = math.lerp(a.P, b.P, t) };
        }

        static float AreaOf(List<ClipV> poly)
        {
            float a = 0f;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                float2 p = poly[i].P.xz, q = poly[(i + 1) % n].P.xz;
                a += p.x * q.y - q.x * p.y;
            }
            return math.abs(a) * 0.5f;
        }

        // For the checks: does the scar polygon reach more than `inset` into a build, rebuild or remove band of the upgrade works
        // on `edge` (the re-marking band keeps its scars)? Samples every 2 m along the band at its inset edges and centre.
        internal static bool ScarInsideUpgradeBands(EntityManager em, List<float2> poly, float4 bounds, Entity edge, float inset)
        {
            if (poly.Count < 3 || !IsUpgradeSiteEdge(em, edge, out var site) || !em.HasComponent<Curve>(edge)) return false;
            var arc = new EdgeArc(DeferredNet.WorksCurve(em, edge));
            float L = arc.Length;
            for (int b = 0; b < site.m_BandCount; b++)
            {
                var band = site.Band(b);
                if (!ClipsScars(band)) continue;
                float lo = band.Lo + inset, hi = band.Hi - inset;
                if (hi < lo) continue;
                for (float s = 1f; s <= L - 1f; s += 2f)
                    for (int j = 0; j < 3; j++)
                    {
                        float lat = j == 0 ? lo : j == 1 ? (lo + hi) * 0.5f : hi;
                        float2 q = arc.Offset(s, lat).xz;
                        if (q.x < bounds.x || q.x > bounds.z || q.y < bounds.y || q.y > bounds.w) continue;
                        if (PointInPolygon(poly, q)) return true;
                    }
            }
            return false;
        }
    }
}
