using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

// SurfaceAreaSystem, second part: node caps at all-works junctions, the scar / curing list, retiring replaced areas,
// and housekeeping (orphan sweep, stale project entries).
namespace RealisticRoadWorks.V3.Surfaces
{
    public partial class SurfaceAreaSystem
    {
        const int kScarStepsPerUpdate = 8;

        // ------------------------------------------------------------------ node caps

        // A junction whose every edge is a mode-A works edge gets a convex-hull cap over the intersection:
        //   Base Course Cover from the first connected project at C2 f >= .95,
        //   Fresh Asphalt Cover (+ Fresh Asphalt under it) once a project's asphalt front reaches the node
        //   (spawned first, the gravel cap is deleted a write later),
        //   once every connected project's painter has passed the node (or it is complete) the cover AND the
        //   fresh asphalt under it go in the same frame - the junction looks vanilla, exactly like the strips behind the
        //   painter (Core shrinks FreshAsphalt with the Cover in C4). The cap never becomes a curing scar.
        private void ProcessCaps()
        {
            foreach (var node in m_JunctionNodes)
            {
                try
                {
                    if (!SurfaceState.Caps.TryGetValue(node, out var cap))
                    {
                        cap = new NodeCap { Node = node };
                        SurfaceState.Caps.Add(node, cap);
                    }
                    cap.SeenUpdate = m_Now;
                    UpdateCap(cap);
                }
                catch (Exception e) { RRWLog.ErrorOnce("surfaces cap", e); }
            }
            if (SurfaceState.Caps.Count == 0) return;
            m_TmpEntities.Clear();
            foreach (var kv in SurfaceState.Caps)
                if (kv.Value.SeenUpdate != m_Now) m_TmpEntities.Add(kv.Key);
            for (int i = 0; i < m_TmpEntities.Count; i++)
            {
                var cap = SurfaceState.Caps[m_TmpEntities[i]];
                SurfaceState.Caps.Remove(m_TmpEntities[i]);
                EndCap(cap);
            }
        }

        // The node is no longer an all-works junction (a project finished / was cancelled) or every painter has passed it:
        // the cover and the fresh asphalt go in the same frame (vanilla node underneath, visible since the C3 reveal).
        // Never a curing scar on the junction (it showed as a dark junction after completion).
        private void EndCap(NodeCap cap)
        {
            DeleteArea(cap.Cover);
            cap.Cover = null;
            if (cap.Asphalt != null)
            {
                DeleteArea(cap.Asphalt);
                cap.Asphalt = null;
            }
            cap.Stage = 0;
        }

        private void UpdateCap(NodeCap cap)
        {
            Entity node = cap.Node;
            EcsUtil.ConnectedEdges(m_Em, node, m_Conn);
            bool demolition = false, anyBase = false, anyAsphalt = false, allPassed = true;
            Entity siteEdge = Entity.Null;
            uint projectId = 0;
            for (int i = 0; i < m_Conn.Count; i++)
            {
                Entity e = m_Conn[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec) || rec.Arc == null || !m_Em.HasComponent<RoadWorksSite>(e)) continue;
                if (!SiteRegistry.TryGetProject(rec.ProjectId, out var p)) continue;
                if (siteEdge == Entity.Null) { siteEdge = e; projectId = p.Id; }
                if (p.Kind != WorksKind.Construction) { demolition = true; continue; }
                var ph = p.Phase;
                var site = m_Em.GetComponentData<RoadWorksSite>(e);
                bool atStart = m_Em.HasComponent<Edge>(e) && m_Em.GetComponentData<Edge>(e).m_Start == node;
                float L = rec.Arc.Length;
                // gravel from C2 f >= .95, or as soon as this edge's gravel front has reached the junction
                // A layer is a set of per-crew pieces; "here" = one of its pieces reaches this node end
                var pv = p.View();
                bool baseHere = ph == WorksPhase.Foundation && PieceAtEnd(SurfaceLayer.BaseCourseCover, pv, site, L, atStart);
                if ((ph == WorksPhase.Foundation && p.PhaseFraction >= 0.95f) || ph >= WorksPhase.Paving || baseHere) anyBase = true;
                bool coverHere = PieceAtEnd(SurfaceLayer.FreshAsphaltCover, pv, site, L, atStart);
                if (coverHere || ph == WorksPhase.Finishing || ph == WorksPhase.Complete) anyAsphalt = true;
                bool passed = ph == WorksPhase.Complete || (ph == WorksPhase.Finishing && !coverHere);
                if (!passed) allPassed = false;
            }
            // No curing stage ("fresh asphalt only, cures"): all painters passed -> no cap at all (vanilla node)
            int desired = demolition || siteEdge == Entity.Null ? 0 : anyAsphalt ? (allPassed ? 0 : 2) : anyBase ? 1 : 0;
            if (desired == 0)
            {
                if (cap.Stage != 0 || cap.Cover != null || cap.Asphalt != null) EndCap(cap);
                return;
            }

            // NeedsRebuild ("Rebuild visuals") / rrw.surf.rebuild: replace both cap areas (spawn first, retire old)
            bool rebuild = m_Em.HasComponent<RoadWorksRuntime>(siteEdge) && m_Em.GetComponentData<RoadWorksRuntime>(siteEdge).Has(RuntimeFlags.NeedsRebuild);
            bool doRebuild = (rebuild && !cap.RebuildDone) || m_DevRebuild;
            cap.RebuildDone = rebuild;

            SurfaceLayer coverLayer = desired == 1 ? SurfaceLayer.BaseCourseCover : SurfaceLayer.FreshAsphaltCover;
            bool wantCover = desired <= 2;
            bool wantAsphalt = desired >= 2;

            // drop dead tracking (staggered, like the strips)
            bool heal = ((m_Now + (uint)node.Index) & (uint)(RRWConst.kSelfHealInterval - 1)) == 0u || doRebuild;
            if (heal && cap.Cover != null && !cap.Cover.Pending && !cap.Cover.Live(m_Em)) cap.Cover = null;
            if (heal && cap.Asphalt != null && !cap.Asphalt.Pending && !cap.Asphalt.Live(m_Em)) cap.Asphalt = null;

            // what has to be (re)written this update; the hull and its vertex Y are only built when needed
            uint hash = SurfaceGeom.CapHash(m_Em, node);
            bool geomChanged = hash != cap.PolyHash;
            bool needAsphaltSpawn = wantAsphalt && cap.Asphalt == null;
            bool needAsphaltWrite = wantAsphalt && cap.Asphalt != null && !cap.Asphalt.Pending && (geomChanged || doRebuild);
            bool needCoverSpawn = wantCover && (cap.Cover == null || (!cap.Cover.Pending && (cap.Cover.Layer != coverLayer || doRebuild)));
            bool needCoverWrite = wantCover && cap.Cover != null && !cap.Cover.Pending && !needCoverSpawn && geomChanged;
            if (needAsphaltSpawn || needAsphaltWrite || needCoverSpawn || needCoverWrite)
            {
                if (!SurfaceGeom.NodeCapPolygon(m_Em, node, m_Poly))
                {
                    EndCap(cap);
                    return;
                }
                ApplyCapY(siteEdge);
                cap.PolyHash = hash;
                // fresh asphalt under the cover (normal queue): spawned with the asphalt cover, deleted with it
                if (needAsphaltSpawn) cap.Asphalt = SpawnCap(SurfaceLayer.FreshAsphalt, siteEdge, projectId);
                else if (needAsphaltWrite) ReplaceOrRewriteCap(ref cap.Asphalt, doRebuild);
                if (needCoverSpawn)
                {
                    // gravel -> asphalt cover (or rebuild): spawn the new cap first, delete the old one a write later
                    var n = SpawnCap(coverLayer, siteEdge, projectId);
                    if (n != null)
                    {
                        if (cap.Cover != null) Retire(cap.Cover.Area, n);
                        cap.Cover = n;
                    }
                }
                else if (needCoverWrite) RewriteCap(cap.Cover);
            }
            if (!wantAsphalt && cap.Asphalt != null) { DeleteArea(cap.Asphalt); cap.Asphalt = null; }
            if (!wantCover && cap.Cover != null && !cap.Cover.Pending) { DeleteArea(cap.Cover); cap.Cover = null; }   // (stages 1-2 always want it)
            cap.Stage = desired;
        }

        // Does a piece of `layer` (PhasePlan.SurfaceSpans) reach the start / end node of this edge?
        private static bool PieceAtEnd(SurfaceLayer layer, in ProjectView v, in RoadWorksSite site, float L, bool atStart)
        {
            PhasePlan.SurfaceSpans(layer, v, out SpanSet set);
            for (int i = 0; i < set.Count; i++)
                if (PhasePlan.ToEdgeLocal(set[i], site.m_ChainU0, site.m_ChainU1, L, out float s0, out float s1)
                    && (atStart ? s0 <= SurfaceGeom.kTouch : s1 >= L - SurfaceGeom.kTouch))
                    return true;
            return false;
        }

        private TrackedArea SpawnCap(SurfaceLayer layer, Entity site, uint projectId)
        {
            var t = new TrackedArea
            {
                Layer = layer,
                Band = 0,
                Prefab = SurfaceState.Prefab(layer, 100),
                Site = site,
                ProjectId = projectId,
                Group = DerivedGroup.Area,
            };
            if (m_Poly.Count < 3 || !SpawnArea(t, m_Poly)) return null;
            SurfaceState.CapSpawns++;
            return t;
        }

        private void RewriteCap(TrackedArea t)
        {
            if (t != null && m_Poly.Count >= 3) RewriteArea(t, m_Poly);
        }

        private void ReplaceOrRewriteCap(ref TrackedArea t, bool replace)
        {
            if (!replace) { RewriteCap(t); return; }
            var n = SpawnCap(t.Layer, t.Site, t.ProjectId);
            if (n == null) return;
            Retire(t.Area, n);
            t = n;
        }

        // Vertex Y of the cap polygon (m_Poly holds node-Y points): same rule as the strips, using a connected edge's
        // runtime / ground output for the floor.
        private void ApplyCapY(Entity edge)
        {
            if (edge == Entity.Null || !m_Em.HasComponent<RoadWorksRuntime>(edge) || !m_Em.HasComponent<RoadWorksSite>(edge)) return;
            var rt = m_Em.GetComponentData<RoadWorksRuntime>(edge);
            var site = m_Em.GetComponentData<RoadWorksSite>(edge);
            var gr = m_Em.HasComponent<RoadWorksGround>(edge) ? m_Em.GetComponentData<RoadWorksGround>(edge) : RoadWorksGround.Initial;
            var planned = PhasePlan.Terrain(PlanInput.From(site, rt));
            for (int i = 0; i < m_Poly.Count; i++)
            {
                float3 p = m_Poly[i];
                p.y = SurfaceGeom.VertexY(ref m_Hd, m_HaveHd, rt, gr, planned, p);
                m_Poly[i] = p;
            }
        }

        // ------------------------------------------------------------------ scars / curing

        const float kAnchorMoveTol = 0.25f;     // an anchor edge whose curve moved more than this (XZ) was replaced in place
        const float kRemnantTol = 1.0f;         // split remnants lie on the lost curve within this (XZ), as Tools' CurveMatch
        const float kCoverTol = 0.5f;           // demolition base cover "covers the edge" when its settled span reaches both ends within this
        const float kOverlapEndSkip = 3f;       // overlap samples skip the works edge's ends (a neighbour sharing a node is no conflict)
        const float kOverlapStep = 2f;
        const int kOverlapMinStations = 3;      // >= 3 stations 2 m apart (>= 4 m of the works edge) inside the scar = real overlap

        private readonly Dictionary<string, int> m_DropLog = new Dictionary<string, int>(StringComparer.Ordinal);

        // Re-tags a live area as a scar (RRWDerived{site = Null, project, Scar}) and starts its fade clock.
        // anchor = what the curing decal lies on: the finished road edge (construction strips) or the junction node
        // (node-cap asphalt). Entity.Null for the demolition topsoil scar, which outlives its deleted edge on purpose.
        private void AddScar(TrackedArea t, bool construction, Entity anchor, bool anchorIsNode)
        {
            t.Site = Entity.Null;
            t.Group = DerivedGroup.Scar;
            EcsUtil.TagDerived(m_Em, t.Area, Entity.Null, t.ProjectId, DerivedGroup.Scar);
            var sc = new ScarEntry { Area = t, Construction = construction, Stage = 0, AgeFrames = 0.0, CreatedUpdate = m_Now };
            if (anchor != Entity.Null && EcsUtil.Alive(m_Em, anchor))
            {
                sc.Anchors.Add(MakeAnchor(anchor, anchorIsNode));
                sc.Anchored = true;
            }
            CapturePolygon(sc);
            SurfaceState.Scars.Add(sc);
        }

        private ScarAnchor MakeAnchor(Entity e, bool isNode)
        {
            var a = new ScarAnchor { Entity = e, IsNode = isNode };
            if (!isNode && m_Em.HasComponent<Curve>(e)) a.Curve = m_Em.GetComponentData<Curve>(e).m_Bezier;
            if (m_Em.HasComponent<PrefabRef>(e)) a.Prefab = m_Em.GetComponentData<PrefabRef>(e).m_Prefab;
            return a;
        }

        // XZ polygon + bounds of the scar as rendered (geometric overlap with later works).
        private void CapturePolygon(ScarEntry sc)
        {
            sc.PolyXZ.Clear();
            float2 lo = new float2(float.MaxValue), hi = new float2(float.MinValue);
            Entity area = sc.Area != null ? sc.Area.Area : Entity.Null;
            if (area != Entity.Null && m_Em.HasBuffer<AreaNode>(area))
            {
                var buf = m_Em.GetBuffer<AreaNode>(area, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    float2 p = buf[i].m_Position.xz;
                    sc.PolyXZ.Add(p);
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }
            }
            sc.Bounds = sc.PolyXZ.Count >= 3 ? new float4(lo, hi) : new float4(1f, 1f, -1f, -1f);
        }

        // A construction's curing decals (fresh asphalt, brown verges, node-cap asphalt) and every scar must never
        // outlive the road they were painted on, nor lie over new works:
        //   - their road becomes a works site again: construction -> removed now (the road is hidden in this same frame);
        //     demolition -> handed over without a pop: removed once the demolition's own Base Course Cover (raised queue,
        //     footprint + 2 m, drawn over the curing) has covered the whole edge for >= 1 update, and at the latest on the
        //     hide frame (D1) - so the curing never lies on the hidden road bed / trench;
        //   - their road was deleted -> removed in the same update; a split re-keys them to the remnants instead;
        //   - their road was replaced in place (other road type, moved curve) -> removed;
        //   - their junction node is hidden by works -> removed;
        //   - another works edge's footprint overlaps the scar polygon (new works over an old scar) -> same rules;
        //   - upgrade works on the road (or overlapping it): clipped to the outside of the bands, not removed (UpgradeScar).
        // The demolition topsoil scar has no anchor, so deleting the demolished edge keeps it (the intended scar).
        private void ProcessScarConflicts()
        {
            var list = SurfaceState.Scars;
            if (list.Count == 0) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var sc = list[i];
                string why;
                Entity at;
                try { why = ScarConflict(sc, out at); }
                catch (Exception e) { RRWLog.ErrorOnce("surfaces scar conflict", e); why = "error"; at = Entity.Null; }
                if (why == null) continue;
                DeleteArea(sc.Area);
                DeleteArea(sc.Next);
                list.RemoveAt(i);
                SurfaceState.ScarsDropped++;
                string key = why + " at " + (at == Entity.Null ? "-" : RRWLog.E(at));
                m_DropLog.TryGetValue(key, out int n);
                m_DropLog[key] = n + 1;
            }
            if (m_DropLog.Count == 0) return;
            foreach (var kv in m_DropLog)
            {
                RRWLog.Info("surfaces: removed " + kv.Value + " curing/scar area(s): " + kv.Key);
                SurfaceState.LastScarDrop = "u" + m_Now + " " + kv.Value + "x " + kv.Key;
            }
            m_DropLog.Clear();
        }

        // Reason to remove the scar now, or null.
        private string ScarConflict(ScarEntry sc, out Entity at)
        {
            at = Entity.Null;
            if (sc.Anchored)
            {
                for (int k = sc.Anchors.Count - 1; k >= 0; k--)
                {
                    var a = sc.Anchors[k];
                    at = a.Entity;
                    if (!EcsUtil.Alive(m_Em, a.Entity))
                    {
                        sc.Anchors.RemoveAt(k);
                        if (!a.IsNode) AddRemnants(sc, a);   // appended after k: checked next update
                        continue;
                    }
                    if (a.IsNode)
                    {
                        if (m_Em.HasComponent<Hidden>(a.Entity) && NodeHasActiveWorks(a.Entity)) return "junction hidden by works";
                        continue;
                    }
                    // upgrade works on the road: the road stays in use, the scar is clipped to the outside of the bands
                    if (UpgradeScarApplies(m_Em, a.Entity, sc))
                    {
                        string u = UpgradeScar(sc, a.Entity, k);
                        if (u != null) return u;
                        continue;
                    }
                    if (ReplacedInPlace(a)) return "road replaced";
                    string w = WorksConflict(a.Entity, sc);
                    if (w != null) return w;
                }
                if (sc.Anchors.Count == 0) return "road deleted";
            }
            if (sc.ConflictRev != SiteRegistry.Revision) ScanOverlaps(sc);
            for (int k = 0; k < sc.Conflicts.Count; k++)
            {
                at = sc.Conflicts[k];
                if (UpgradeScarApplies(m_Em, at, sc))
                {
                    string u = UpgradeScar(sc, at, -1);
                    if (u != null) return u + " (overlap)";
                    continue;
                }
                string w = WorksConflict(at, sc);
                if (w != null) return w + " (overlap)";
            }
            at = Entity.Null;
            return null;
        }

        private bool ReplacedInPlace(ScarAnchor a)
        {
            if (a.Prefab != Entity.Null && m_Em.HasComponent<PrefabRef>(a.Entity) && m_Em.GetComponentData<PrefabRef>(a.Entity).m_Prefab != a.Prefab)
                return true;
            if (!m_Em.HasComponent<Curve>(a.Entity)) return false;
            var b = m_Em.GetComponentData<Curve>(a.Entity).m_Bezier;
            return Moved(a.Curve.a, b.a) || Moved(a.Curve.b, b.b) || Moved(a.Curve.c, b.c) || Moved(a.Curve.d, b.d);
        }

        static bool Moved(float3 x, float3 y) => math.distancesq(x.xz, y.xz) > kAnchorMoveTol * kAnchorMoveTol;

        private static bool WorksActive(EntityManager em, Entity edge, ProjectRecord p)
        {
            if (p.Phase == WorksPhase.Complete) return false;
            if (em.HasComponent<RoadWorksRuntime>(edge) && em.GetComponentData<RoadWorksRuntime>(edge).Has(RuntimeFlags.Completing)) return false;
            return true;
        }

        // Reason when `edge` is an active works edge the scar must give way to now, else null.
        private string WorksConflict(Entity edge, ScarEntry sc)
        {
            if (!SiteRegistry.TryGetEdge(edge, out var rec) || !SiteRegistry.TryGetProject(rec.ProjectId, out var p)) return null;
            if (!WorksActive(m_Em, edge, p)) return null;
            if (p.Kind != WorksKind.Demolition) return "construction started";
            if (DemolitionCovers(edge, rec, p)) return "demolition hand-over (" + p.Phase + ")";
            if (sc.WaitSince == 0) sc.WaitSince = m_Now;
            return null;
        }

        // Demolition D0: the curing may go once nothing can show: the edge is (being) hidden, or the demolition's own Base
        // Course Cover has been on screen over the whole edge for >= 1 update. Without work-site textures: right away.
        private bool DemolitionCovers(Entity edge, EdgeRecord rec, ProjectRecord p)
        {
            if (p.Phase != WorksPhase.BreakUp) return true;   // D1 / D2: hidden (the Director hid it earlier in this frame)
            if (m_Em.HasComponent<Hidden>(edge)) return true;
            if (m_Em.HasComponent<RoadWorksRuntime>(edge) && m_Em.GetComponentData<RoadWorksRuntime>(edge).HideWanted) return true;
            if (p.Mode != VisualMode.FullDig) return true;
            if (!m_Em.HasComponent<RoadWorksSite>(edge) || m_Em.GetComponentData<RoadWorksSite>(edge).Mode != VisualMode.FullDig) return true;
            if (SurfaceState.Prefab(SurfaceLayer.BaseCourseCover, 100) == Entity.Null) return true;
            if (rec.Arc == null || !SurfaceState.Edges.TryGetValue(edge, out var es)) return false;
            float L = rec.Arc.Length;
            int bands = math.min(rec.Section.BandCount(SurfaceLayer.BaseCourseCover), EdgeSection.kMaxIntervals);
            // Every band's settled pieces together must cover the whole edge
            m_FullNeed.Clear();
            m_FullNeed.Add(new Span(kCoverTol, L - kCoverTol));
            for (int b = 0; b < bands; b++)
            {
                SettledRow(es, (int)SurfaceLayer.BaseCourseCover, b, ref m_CoverHave);
                if (m_CoverHave.IsEmpty || FirstGap(kCoverTol, L - kCoverTol, m_FullNeed, m_CoverHave, true, out _, out _)) return false;
            }
            return true;
        }

        private SpanSet m_FullNeed, m_CoverHave;

        private bool NodeHasActiveWorks(Entity node)
        {
            EcsUtil.ConnectedEdges(m_Em, node, m_Conn);
            for (int i = 0; i < m_Conn.Count; i++)
            {
                Entity e = m_Conn[i];
                if (SiteRegistry.TryGetEdge(e, out var rec) && SiteRegistry.TryGetProject(rec.ProjectId, out var p) && WorksActive(m_Em, e, p)) return true;
            }
            return false;
        }

        // The anchor edge was deleted. Edges created in this same update that lie on its curve are its split remnants
        // (Tools' CurveMatch rule): the curing stays on them (no pop). Without remnants the scar is removed.
        private void AddRemnants(ScarEntry sc, ScarAnchor lost)
        {
            if (m_CreatedEdges.IsEmptyIgnoreFilter) return;
            if (lost.Arc == null) lost.Arc = new EdgeArc(lost.Curve);
            if (lost.Arc.Length < 0.5f) return;
            var arr = m_CreatedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Entity e = arr[i];
                    if (!m_Em.HasComponent<Curve>(e) || IsAnchor(sc, e)) continue;
                    if (!CurveMatch.LiesOn(m_Em.GetComponentData<Curve>(e).m_Bezier, lost.Arc, kRemnantTol)) continue;
                    sc.Anchors.Add(MakeAnchor(e, false));
                    SurfaceState.ScarsRekeyed++;
                    RRWLog.Verbose("surfaces: curing/scar of lost road " + RRWLog.E(lost.Entity) + " kept on split remnant " + RRWLog.E(e));
                }
            }
            finally { arr.Dispose(); }
        }

        private static bool IsAnchor(ScarEntry sc, Entity e)
        {
            for (int k = 0; k < sc.Anchors.Count; k++) if (sc.Anchors[k].Entity == e) return true;
            return false;
        }

        // Registry edges (other than the anchors) whose footprint overlaps the scar polygon. Re-run when the registry
        // changes; the phase of each hit is evaluated every update (WorksConflict).
        private void ScanOverlaps(ScarEntry sc)
        {
            sc.ConflictRev = SiteRegistry.Revision;
            sc.Conflicts.Clear();
            if (sc.PolyXZ.Count < 3) return;
            foreach (var kv in SiteRegistry.Edges)
            {
                if (kv.Value.Arc == null || IsAnchor(sc, kv.Key)) continue;
                if (FootprintOverlaps(sc.PolyXZ, sc.Bounds, kv.Value)) sc.Conflicts.Add(kv.Key);
            }
        }

        // The works edge's footprint (centre and +-half width, every 2 m) lies inside the polygon over >= 4 m of its length.
        // The part inside its junction trims (+1 m) is skipped: a road attached to a curing road at a new T node only
        // touches the old verges with its first metre and is no conflict; a road built over / across a scar is.
        internal static bool FootprintOverlaps(List<float2> poly, float4 bounds, EdgeRecord rec)
        {
            var arc = rec.Arc;
            float hw = math.max(1f, rec.Section.HalfWidth);
            var c = arc.Curve;
            float2 lo = math.min(math.min(c.a.xz, c.b.xz), math.min(c.c.xz, c.d.xz)) - hw;
            float2 hi = math.max(math.max(c.a.xz, c.b.xz), math.max(c.c.xz, c.d.xz)) + hw;
            if (hi.x < bounds.x || lo.x > bounds.z || hi.y < bounds.y || lo.y > bounds.w) return false;
            float L = arc.Length;
            float s0 = math.min(math.max(kOverlapEndSkip, rec.Section.TrimStart + 1f), L * 0.5f);
            float s1 = math.max(L - math.max(kOverlapEndSkip, rec.Section.TrimEnd + 1f), L * 0.5f);
            int stations = 0;
            for (float s = s0; s <= s1 + 1e-3f; s += kOverlapStep)
            {
                float3 p = arc.Position(s);
                float3 d = arc.Direction(s);
                float2 right = math.normalizesafe(new float2(d.z, -d.x));
                for (int j = -1; j <= 1; j++)
                {
                    float2 q = p.xz + right * (j * 0.5f * hw);
                    if (q.x < bounds.x || q.x > bounds.z || q.y < bounds.y || q.y > bounds.w) continue;
                    if (!PointInPolygon(poly, q)) continue;
                    if (++stations >= kOverlapMinStations) return true;
                    break;   // one hit per station
                }
            }
            return false;
        }

        internal static bool PointInPolygon(List<float2> poly, float2 p)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float2 a = poly[i], b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        // Fade schedule in in-game hours (RRWConst). end = deletion time.
        //   construction verges (topsoil + subgrade): a100 -> a50 at 0.25 h -> a20 at 0.6 h -> deleted at 1.0 h,
        //     no hold (SurfacePalette.VergeStage);
        //   demolition topsoil scar: SurfacePalette.SoilStage over its 12 h life (hold, then linear).
        // Construction fresh asphalt never becomes a scar any more (no curing layer); ProcessScars removes any it meets.
        internal static float ScarEndHours(bool construction) => construction ? RRWConst.kVergeEndHours : RRWConst.kScarEndHours;

        internal static int DesiredStage(bool construction, SurfaceLayer layer, float hours)
        {
            int[] ladder = SurfacePalette.FadeLadder(layer);
            int desired = construction ? SurfacePalette.VergeStage(hours) : SurfacePalette.SoilStage(hours, RRWConst.kScarEndHours);
            return math.clamp(desired, 0, ladder.Length - 1);
        }

        private void ProcessScars()
        {
            var list = SurfaceState.Scars;
            if (list.Count == 0) { SurfaceState.DevScarAgeBonus = 0; return; }
            // simulation frames (no ageing while paused); the dev time-lapse scale ages scars with the works
            double add = RRWClock.SimDelta * (double)math.max(0f, RRWDebug.WorkTimeScale);
            if (SurfaceState.DevScarAgeBonus != 0) { add += SurfaceState.DevScarAgeBonus * RRWConst.kFramesPerHour; SurfaceState.DevScarAgeBonus = 0; }
            int steps = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var sc = list[i];
                try
                {
                    sc.AgeFrames += add;
                    float hours = (float)(sc.AgeFrames / RRWConst.kFramesPerHour);
                    var layer = sc.Area.Layer;
                    if (sc.Construction && !IsVergeLayer(layer))
                    {
                        // Defence: a construction keeps no road-surface layer after completion (no curing asphalt)
                        DeleteArea(sc.Area);
                        DeleteArea(sc.Next);
                        list.RemoveAt(i);
                        SurfaceState.StrayCuringRemoved++;
                        RRWLog.Once("surf-stray-curing", "surfaces: removed a construction " + layer + " scar (no curing layer after completion)");
                        continue;
                    }
                    float end = ScarEndHours(sc.Construction);
                    if (hours >= end)
                    {
                        DeleteArea(sc.Area);
                        DeleteArea(sc.Next);
                        list.RemoveAt(i);
                        if (sc.Construction)
                        {
                            SurfaceState.VergeScarsEnded++;
                            RRWLog.Verbose("surfaces: verge scar (" + layer + ", project #" + sc.Area.ProjectId + ") deleted after " + RRWLog.F(hours) + " in-game h");
                        }
                        continue;
                    }
                    if (sc.Next != null)
                    {
                        // spawn-first swap: the next variant has been on screen for an update -> drop the old one
                        if (sc.Next.Settled(m_Em, m_Now))
                        {
                            DeleteArea(sc.Area);
                            sc.Area = sc.Next;
                            sc.Next = null;
                            sc.Stage++;
                            SurfaceState.ScarSteps++;
                        }
                        else if (!sc.Next.Pending && !sc.Next.Live(m_Em)) sc.Next = null;   // lost: retry later
                        continue;
                    }
                    if (!sc.Area.Live(m_Em))
                    {
                        if (!sc.Area.Pending) list.RemoveAt(i);
                        continue;
                    }
                    int desired = DesiredStage(sc.Construction, layer, hours);
                    if (desired <= sc.Stage || steps >= kScarStepsPerUpdate) continue;
                    int pct = SurfacePalette.FadeLadder(layer)[desired];
                    Entity prefab = SurfaceState.Prefab(layer, pct);
                    if (prefab == Entity.Null) { sc.Stage = desired; continue; }
                    steps++;
                    if (SurfaceState.FadeBySwap)
                    {
                        // experimental path (rrw.surf.fade swap): swap the prefab in place (single frame if AreaBatchSystem re-batches cleanly)
                        m_Em.SetComponentData(sc.Area.Area, new PrefabRef(prefab));
                        if (!m_Em.HasComponent<Updated>(sc.Area.Area)) m_Em.AddComponent<Updated>(sc.Area.Area);
                        sc.Area.Prefab = prefab;
                        sc.Area.FadePct = pct;
                        sc.Stage = desired;
                        SurfaceState.ScarSteps++;
                        continue;
                    }
                    if (!CopyNodes(sc.Area.Area)) continue;
                    var n = new TrackedArea
                    {
                        Layer = layer, Band = sc.Area.Band, Prefab = prefab, FadePct = pct,
                        Site = Entity.Null, ProjectId = sc.Area.ProjectId, Group = DerivedGroup.Scar,
                        LS0 = sc.Area.LS0, LS1 = sc.Area.LS1,
                    };
                    if (SpawnArea(n, m_Poly))
                    {
                        sc.Next = n;
                        sc.Stage = desired - 1;   // becomes `desired` when the swap completes
                    }
                }
                catch (Exception e)
                {
                    RRWLog.ErrorOnce("surfaces scar", e);
                    list.RemoveAt(i);
                }
            }
        }

        // m_Poly = the live area's current polygon (positions as rendered).
        private bool CopyNodes(Entity area)
        {
            m_Poly.Clear();
            if (!m_Em.HasBuffer<AreaNode>(area)) return false;
            var buf = m_Em.GetBuffer<AreaNode>(area, true);
            for (int i = 0; i < buf.Length; i++) m_Poly.Add(buf[i].m_Position);
            return m_Poly.Count >= 3;
        }

        // ------------------------------------------------------------------ retiring areas

        private void ProcessRetiring()
        {
            var list = SurfaceState.Retiring;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var r = list[i];
                bool go;
                if (m_Now - r.Since >= kRetireTimeout) go = true;
                else if (r.Successor == null) go = m_Now - r.Since >= (uint)math.max(1, r.MinUpdates);
                else if (r.Successor.Settled(m_Em, m_Now)) go = true;
                else go = !r.Successor.Pending && !r.Successor.Live(m_Em);   // successor failed: do not keep a stale area forever
                if (!go) continue;
                SxPerf.Begin(SxT.AreaEcs);
                EcsUtil.MarkDeleted(m_Em, r.Area);
                SxPerf.End(SxT.AreaEcs);
                SxPerf.Count(SxC.Deletes);
                list.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ housekeeping

        private void Housekeeping()
        {
            if (m_Now - m_LastOrphanSweep >= kOrphanSweepInterval)
            {
                m_LastOrphanSweep = m_Now;
                SweepOrphans(false, null);
            }
            if (m_Now - m_LastProjSweep >= kOrphanSweepInterval)
            {
                m_LastProjSweep = m_Now;
                m_TmpEntities.Clear();
                var dead = s_DeadProjects;
                dead.Clear();
                foreach (var kv in SurfaceState.Projects)
                    if (!SiteRegistry.Projects.ContainsKey(kv.Key)) dead.Add(kv.Key);
                for (int i = 0; i < dead.Count; i++) SurfaceState.Projects.Remove(dead[i]);
            }
        }

        private static readonly List<uint> s_DeadProjects = new List<uint>(16);

        // Every area some Surfaces list still tracks (strips, replaced pieces, yellow lines, dev lines, caps, scars, retiring).
        internal static void CollectTracked(HashSet<Entity> refs)
        {
            refs.Clear();
            foreach (var es in SurfaceState.Edges.Values)
            {
                for (int l = 0; l < es.Areas.GetLength(0); l++)
                    for (int b = 0; b < es.Areas.GetLength(1); b++)
                        for (int k = 0; k < es.RowN[l, b]; k++)
                            if (es.Areas[l, b, k] != null && es.Areas[l, b, k].Area != Entity.Null) refs.Add(es.Areas[l, b, k].Area);
                for (int i = 0; i < es.Outgoing.Count; i++)
                    if (es.Outgoing[i].Area != Entity.Null) refs.Add(es.Outgoing[i].Area);
                for (int i = 0; i < es.TempRows.Count; i++)
                    foreach (var t in es.TempRows[i].Pieces) if (t.Area != Entity.Null) refs.Add(t.Area);
            }
            foreach (var dl in SurfaceState.DevLines)
                foreach (var t in dl.Pieces) if (t.Area != Entity.Null) refs.Add(t.Area);
            foreach (var c in SurfaceState.Caps.Values)
            {
                if (c.Cover != null && c.Cover.Area != Entity.Null) refs.Add(c.Cover.Area);
                if (c.Asphalt != null && c.Asphalt.Area != Entity.Null) refs.Add(c.Asphalt.Area);
            }
            foreach (var s in SurfaceState.Scars)
            {
                if (s.Area != null && s.Area.Area != Entity.Null) refs.Add(s.Area.Area);
                if (s.Next != null && s.Next.Area != Entity.Null) refs.Add(s.Next.Area);
            }
            foreach (var r in SurfaceState.Retiring) refs.Add(r.Area);
        }

        // Self-heal: delete live RRW areas that nothing tracks any more (an exception mid-update, a lost binding...).
        // Areas are never saved, so this only ever removes our own leaks. why: logged reason of a one-off sweep (null for the
        // periodic one).
        private void SweepOrphans(bool all, string why)
        {
            if (m_OurAreas.IsEmptyIgnoreFilter || SurfaceState.CloneEntities.Count == 0) return;
            var refs = m_Referenced;
            refs.Clear();
            if (!all) CollectTracked(refs);
            var arr = m_OurAreas.ToEntityArray(Allocator.Temp);
            SxPerf.Count(SxC.OrphanSweeps);
            SxPerf.Count(SxC.OrphanScanned, arr.Length);
            int n = 0;
            string first = null;
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var e = arr[i];
                    var prefab = m_Em.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!SurfaceState.IsOurPrefab(prefab)) continue;
                    if (refs.Contains(e) || m_Em.HasComponent<Created>(e)) continue;
                    if (first == null) first = DescribeArea(m_Em, e, prefab);
                    EcsUtil.MarkDeleted(m_Em, e);
                    n++;
                }
            }
            finally { arr.Dispose(); }
            if (n == 0 || all) return;
            SurfaceState.OrphansRemoved += n;
            SurfaceState.LastOrphan = "u" + m_Now + " " + n + "x, first " + first + (why != null ? " (" + why + ")" : "");
            RRWLog.Warn("surfaces: removed " + n + " untracked work-site texture area(s) (self-heal" + (why != null ? ", " + why : "") + "), first: " + first);
        }

        // "<clone> at (x, z), n nodes, project #p" for the logs and checks.
        internal static string DescribeArea(EntityManager em, Entity area, Entity prefab)
        {
            string at = "";
            int nodes = 0;
            if (em.HasBuffer<AreaNode>(area))
            {
                var buf = em.GetBuffer<AreaNode>(area, true);
                nodes = buf.Length;
                float2 c = float2.zero;
                for (int i = 0; i < buf.Length; i++) c += buf[i].m_Position.xz;
                if (buf.Length > 0) { c /= buf.Length; at = " at (" + RRWLog.F(c.x) + ", " + RRWLog.F(c.y) + ")"; }
            }
            uint pid = em.HasComponent<RRWDerived>(area) ? em.GetComponentData<RRWDerived>(area).m_ProjectId : 0u;
            return "\"" + SurfaceState.NameOf(prefab) + "\"" + at + ", " + nodes + " nodes" + (pid != 0 ? ", project #" + pid : "");
        }
    }
}
