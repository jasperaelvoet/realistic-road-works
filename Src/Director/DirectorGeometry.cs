using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetSubLane = Game.Net.SubLane;

namespace RealisticRoadWorks.V3.Director
{
    // Edge geometry, geometry changes, trims and shared nodes.
    public partial class WorksDirectorSystem
    {
        // Arc, section, subgrade depth, hash, preview bounds. A NEW EdgeArc object on every change, so a module that
        // still holds the previous reference keeps a consistent old arc.
        private void RefreshGeometry(EdgeRecord rec, DirEdgeState st)
        {
            var em = EntityManager;
            Entity e = rec.Edge;
            var curve = em.GetComponentData<Curve>(e);
            rec.Arc = new EdgeArc(curve.m_Bezier);
            st.CurveLength = curve.m_Length > 0.01f ? curve.m_Length : rec.Arc.Length;
            st.SectionFromLanes = HasCarLanes(e);
            rec.Section = EcsUtil.MeasureSection(em, e, rec.Arc);
            st.LaneSig = EcsUtil.LaneSignature(em, e);
            rec.SubgradeDepth = PhasePlan.SubgradeDepth(rec.Section.CompositionWidth);
            rec.GeometryHash = GeometryHash(curve.m_Bezier, EcsUtil.CompositionWidth(em, e));
            st.Prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
            var b = curve.m_Bezier;
            float2 mn = math.min(math.min(b.a.xz, b.b.xz), math.min(b.c.xz, b.d.xz));
            float2 mx = math.max(math.max(b.a.xz, b.b.xz), math.max(b.c.xz, b.d.xz));
            st.BoundsXZ = new float4(mn, mx);
            st.CornerHash = CornerHash(e);
            st.EligKey = EligibilityKey(e);
        }

        // Quantised EdgeGeometry end corners (0.05 m). Changes when a node gains / loses an edge (trims), not on Ground's
        // composition repoint (a clone has the same width, so the geometry is identical).
        private uint CornerHash(Entity e)
        {
            var em = EntityManager;
            if (!em.HasComponent<EdgeGeometry>(e)) return 0u;
            var g = em.GetComponentData<EdgeGeometry>(e);
            float3 a = math.round(g.m_Start.m_Left.a * 20f), b = math.round(g.m_Start.m_Right.a * 20f);
            float3 c = math.round(g.m_End.m_Left.d * 20f), d = math.round(g.m_End.m_Right.d * 20f);
            return math.hash(new float4x3(new float4(a, 1f), new float4(b, 2f), new float4(c, 3f))) ^ math.hash(d);
        }

        // Everything EcsUtil.DigEligible looks at that an upgrade can change without changing the width: the prefab, the
        // Upgraded flags, the elevation sign and the composition flags (a Ground clone copies the source's
        // NetCompositionData, so the repoint to a clone does not change this key).
        private uint EligibilityKey(Entity e)
        {
            var em = EntityManager;
            uint k = 17u;
            if (em.HasComponent<PrefabRef>(e)) k = k * 31u + (uint)em.GetComponentData<PrefabRef>(e).m_Prefab.Index;
            if (em.HasComponent<Upgraded>(e))
            {
                var u = em.GetComponentData<Upgraded>(e).m_Flags;
                k = k * 31u + (uint)u.m_General; k = k * 31u + (uint)u.m_Left; k = k * 31u + (uint)u.m_Right;
            }
            if (em.HasComponent<Game.Net.Elevation>(e))
            {
                float2 el = em.GetComponentData<Game.Net.Elevation>(e).m_Elevation;
                k = k * 31u + (uint)(math.any(el > 0.5f) ? 1 : 0) + (uint)(math.any(el < -0.5f) ? 2 : 0);
            }
            if (em.HasComponent<Composition>(e))
            {
                Entity c = em.GetComponentData<Composition>(e).m_Edge;
                if (c != Entity.Null && em.Exists(c) && em.HasComponent<NetCompositionData>(c))
                {
                    var f = em.GetComponentData<NetCompositionData>(c).m_Flags;
                    k = k * 31u + (uint)f.m_General; k = k * 31u + (uint)f.m_Left; k = k * 31u + (uint)f.m_Right;
                }
            }
            return k;
        }

        private static uint GeometryHash(Bezier4x3 b, float width) =>
            math.hash(new float4x4(new float4(b.a, width), new float4(b.b, 1f), new float4(b.c, 2f), new float4(b.d, 3f)));

        private bool HasCarLanes(Entity edge)
        {
            var em = EntityManager;
            if (!em.HasBuffer<NetSubLane>(edge)) return false;
            var buf = em.GetBuffer<NetSubLane>(edge, true);
            for (int i = 0; i < buf.Length; i++)
                if (em.HasComponent<NetCarLane>(buf[i].m_SubLane)) return true;
            return false;
        }

        // Each frame: edges carrying Updated (and once more the frame after), a small round robin, and new edges
        // whose lanes did not exist yet when the section was measured.
        private void CheckGeometry()
        {
            int n = m_Records.Count;
            if (n == 0) return;
            var em = EntityManager;
            int rrStart = m_GeomCursor % n;
            int rrCount = math.min(n, DirConst.kGeometryRoundRobin);
            m_GeomCursor = (rrStart + rrCount) % n;
            for (int i = 0; i < n; i++)
            {
                var rec = m_Records[i];
                Entity e = rec.Edge;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || !em.HasComponent<Curve>(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var st = rec.GetOrCreate<DirEdgeState>(ModuleSlot.Director);
                bool updated = em.HasComponent<Updated>(e);
                int rel = i - rrStart;
                if (rel < 0) rel += n;
                bool due = updated || st.RecheckGeometry || rel < rrCount;
                st.RecheckGeometry = updated;
                if (!st.SectionFromLanes && m_Now < st.SectionRetryUntil && HasCarLanes(e))
                {
                    // lanes of a new road appear a frame after the road: re-measure the carriage intervals once they exist
                    rec.Section = EcsUtil.MeasureSection(em, e, rec.Arc ?? new EdgeArc(em.GetComponentData<Curve>(e).m_Bezier));
                    st.LaneSig = EcsUtil.LaneSignature(em, e);
                    rec.SubgradeDepth = PhasePlan.SubgradeDepth(rec.Section.CompositionWidth);
                    st.SectionFromLanes = true;
                    rec.GeometryRevision++;
                    rec.GeometryChangedUpdate = m_Now;
                }
                if (!due) continue;
                if (em.HasComponent<Edge>(e))
                {
                    var ed = em.GetComponentData<Edge>(e);
                    if (ed.m_Start != rec.StartNode || ed.m_End != rec.EndNode)
                    {
                        rec.StartNode = ed.m_Start;
                        rec.EndNode = ed.m_End;
                        SiteRegistry.BumpRevision();
                    }
                }
                var curve = em.GetComponentData<Curve>(e);
                uint hash = GeometryHash(curve.m_Bezier, EcsUtil.CompositionWidth(em, e));
                if (hash == rec.GeometryHash)
                {
                    try { CheckEndsAndEligibility(rec, st); }
                    catch (global::System.Exception ex) { RRWLog.ErrorOnce("director trims/eligibility", ex); }
                    try { CheckLanes(rec, st); }
                    catch (global::System.Exception ex) { RRWLog.ErrorOnce("director lane re-measure", ex); }
                    continue;
                }
                try { OnGeometryChanged(rec, st, curve); }
                catch (global::System.Exception ex) { RRWLog.ErrorOnce("director geometry change", ex); rec.GeometryHash = hash; }
            }
        }

        // Same curve and width:
        //  - a road drawn into (or removed from) one of the edge's nodes moves the EdgeGeometry end corners: re-measure
        //    the section so EdgeRecord.Section.TrimStart/End (and the project trims) never go stale;
        //  - an upgrade to Lowered / elevated that keeps the width: re-run DigEligible and switch to mode D, otherwise
        //    the road would stay hidden over a held reveal floor.
        private void CheckEndsAndEligibility(EdgeRecord rec, DirEdgeState st)
        {
            var em = EntityManager;
            Entity e = rec.Edge;
            uint ch = CornerHash(e);
            if (ch != st.CornerHash)
            {
                st.CornerHash = ch;
                if (rec.Arc != null)
                {
                    rec.Section = EcsUtil.MeasureSection(em, e, rec.Arc);
                    st.LaneSig = EcsUtil.LaneSignature(em, e);
                    rec.SubgradeDepth = PhasePlan.SubgradeDepth(rec.Section.CompositionWidth);
                    st.SectionFromLanes = HasCarLanes(e);
                }
                rec.GeometryRevision++;
                rec.GeometryChangedUpdate = m_Now;
                if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj)) proj.Revision++;
                MarkUpgradeReclassify(rec);
                RRWLog.Verbose("director: edge " + RRWLog.E(e) + " end geometry changed (node edges): trims " + RRWLog.F(rec.Section.TrimStart) + "/" + RRWLog.F(rec.Section.TrimEnd));
            }
            uint ek = EligibilityKey(e);
            if (ek == st.EligKey) return;
            st.EligKey = ek;
            var site = em.GetComponentData<RoadWorksSite>(e);
            if (site.Mode == VisualMode.FullDig && !EcsUtil.DigEligible(em, e, out string why))
            {
                site.Mode = VisualMode.Minimal;
                em.SetComponentData(e, site);
                rec.GeometryRevision++;
                rec.GeometryChangedUpdate = m_Now;
                if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj)) proj.Revision++;
                RRWLog.Info("director: edge " + RRWLog.E(e) + " of project #" + rec.ProjectId + " is no longer eligible for excavation (" + why + "): mode D");
            }
        }

        // Same curve, width and end corners, but the lane set changed (lanes regenerated after the first measurement; the creation
        // frame of a new road can measure an incomplete or temporary lane set): re-measure the section. A changed section bumps
        // GeometryRevision (Traffic regroups, Props / Surfaces / Machines re-read the split); an unchanged one only stores the new
        // signature. A lane set without any car / track lane along the edge is not adopted (lanes mid-regeneration): the old section
        // stays and the next check retries.
        private void CheckLanes(EdgeRecord rec, DirEdgeState st)
        {
            if (rec.Arc == null) return;
            var em = EntityManager;
            Entity e = rec.Edge;
            uint sig = EcsUtil.LaneSignature(em, e);
            if (sig == st.LaneSig) return;
            var sec = EcsUtil.MeasureSection(em, e, rec.Arc);
            if (sec.Verdict == SplitVerdict.NoDriveLanes && rec.Section.LanesMeasured && rec.Section.Verdict != SplitVerdict.NoDriveLanes) return;
            st.LaneSig = sig;
            st.SectionFromLanes = HasCarLanes(e);
            if (SameSection(rec.Section, sec)) { rec.Section = sec; return; }
            var old = rec.Section;
            rec.Section = sec;
            rec.SubgradeDepth = PhasePlan.SubgradeDepth(sec.CompositionWidth);
            rec.GeometryRevision++;
            rec.GeometryChangedUpdate = m_Now;
            if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj)) proj.Revision++;
            MarkUpgradeReclassify(rec);
            RRWLog.Info("director: edge " + RRWLog.E(e) + " lanes changed: section re-measured " + old.Verdict + " split=" + RRWLog.F(old.DirSplit)
                        + " -> " + sec.Verdict + " split=" + RRWLog.F(sec.DirSplit) + " through=" + sec.ThroughF + ">" + sec.ThroughB + "<"
                        + " carriage=[" + RRWLog.F(sec.CarriageLo) + "," + RRWLog.F(sec.CarriageHi) + "]");
        }

        // Two measurements of the same edge agree (lane-derived fields within 1 cm, same verdict).
        private static bool SameSection(in EdgeSection a, in EdgeSection b)
        {
            const float tol = 0.01f;
            bool Eq(float x, float y) => float.IsNaN(x) ? float.IsNaN(y) : !float.IsNaN(y) && math.abs(x - y) <= tol;
            if (a.Verdict != b.Verdict || a.LanesMeasured != b.LanesMeasured || a.IntervalCount != b.IntervalCount) return false;
            if (!Eq(a.DirSplit, b.DirSplit) || !Eq(a.DriveLo, b.DriveLo) || !Eq(a.DriveHi, b.DriveHi)) return false;
            if (!Eq(a.CarriageLo, b.CarriageLo) || !Eq(a.CarriageHi, b.CarriageHi) || !Eq(a.TrimStart, b.TrimStart) || !Eq(a.TrimEnd, b.TrimEnd)) return false;
            for (int k = 0; k < 4; k++)
                if (!Eq(a.LaneLinesLeft[k], b.LaneLinesLeft[k]) || !Eq(a.LaneLinesRight[k], b.LaneLinesRight[k])) return false;
            for (int i = 0; i < a.IntervalCount; i++)
                if (!Eq(a.Interval(i).x, b.Interval(i).x) || !Eq(a.Interval(i).y, b.Interval(i).y)) return false;
            return true;
        }

        // The edge's curve or composition width changed (upgrade, node moved, edge extended by node reduction).
        private void OnGeometryChanged(EdgeRecord rec, DirEdgeState st, Curve curve)
        {
            var em = EntityManager;
            Entity e = rec.Edge;
            var site = em.GetComponentData<RoadWorksSite>(e);
            var oldArc = rec.Arc;
            bool coordsChanged = false;
            if (oldArc != null && oldArc.Length > 0.01f)
            {
                float oldLen = oldArc.Length;
                float sa = oldArc.ProjectExtended(curve.m_Bezier.a);
                float sd = oldArc.ProjectExtended(curve.m_Bezier.d);
                bool endsKept = math.abs(sa) <= 0.5f && math.abs(sd - oldLen) <= 0.5f;
                if (!endsKept)
                {
                    float U = site.m_ChainLength;
                    float ua = PhasePlan.ChainU(sa, site.m_ChainU0, site.m_ChainU1, oldLen);
                    float ud = PhasePlan.ChainU(sd, site.m_ChainU0, site.m_ChainU1, oldLen);
                    float lo = math.min(ua, ud), hi = math.max(ua, ud);
                    if (lo < -0.5f || hi > U + 0.5f)
                    {
                        // the edge was extended at a chain end: grow the project (shift every edge so u stays >= 0)
                        float shift = lo < 0f ? -lo : 0f;
                        float newU = math.max(U, hi) + shift;
                        GrowProject(rec.ProjectId, shift, newU);
                        site = em.GetComponentData<RoadWorksSite>(e);   // re-read after the project-wide write
                        ua += shift;
                        ud += shift;
                        RRWLog.Info("director: project #" + rec.ProjectId + " grew to " + RRWLog.F(newU) + " m (edge " + RRWLog.E(e) + " extended)");
                    }
                    else
                    {
                        ua = math.clamp(ua, 0f, U);
                        ud = math.clamp(ud, 0f, U);
                    }
                    site.m_ChainU0 = ua;
                    site.m_ChainU1 = ud;
                    coordsChanged = true;
                }
            }
            // eligibility (upgraded to elevated / tunnel): the saved mode becomes Minimal; Ground reveals through the hold
            if (site.Mode == VisualMode.FullDig && !EcsUtil.DigEligible(em, e, out string why))
            {
                site.Mode = VisualMode.Minimal;
                coordsChanged = true;
                RRWLog.Info("director: edge " + RRWLog.E(e) + " of project #" + rec.ProjectId + " is no longer eligible for excavation (" + why + "): mode D");
            }
            if (coordsChanged) em.SetComponentData(e, site);
            RefreshGeometry(rec, st);
            rec.GeometryRevision++;
            rec.GeometryChangedUpdate = m_Now;
            if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj))
            {
                proj.Revision++;
                proj.GetOrCreate<DirProjectState>(ModuleSlot.Director).SortDirty = true;
            }
            // upgrade works: the sub-strips, keep-outs and lane layout follow the new revision; the primitives are re-picked
            // (a started window only weakens: its saved primitive is the ceiling)
            MarkUpgradeReclassify(rec);
            RRWLog.Verbose("director: geometry changed edge " + RRWLog.E(e) + " u=[" + RRWLog.F(site.m_ChainU0) + "," + RRWLog.F(site.m_ChainU1) + "] len=" + RRWLog.F(st.CurveLength));
        }

        private void MarkUpgradeReclassify(EdgeRecord rec)
        {
            if (rec.Upgrade != null && SiteRegistry.TryGetProject(rec.ProjectId, out var proj) && proj.Upgrade != null)
                proj.Upgrade.ReclassifyUpdate = math.max(1u, m_Now);
        }

        private void GrowProject(uint projectId, float shift, float newU)
        {
            if (!SiteRegistry.TryGetProject(projectId, out var proj)) return;
            var em = EntityManager;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity pe = proj.Edges[i];
                if (!em.Exists(pe) || !em.HasComponent<RoadWorksSite>(pe)) continue;
                var s = em.GetComponentData<RoadWorksSite>(pe);
                float oldU = s.m_ChainLength;
                s.m_ChainU0 += shift;
                s.m_ChainU1 += shift;
                s.m_ChainLength = newU;
                if (s.Has(SiteFlags.CancelledBuild))
                {
                    // A multi-section cancel layout keeps its UNIT sweep (CancelFront / U, shared by every
                    // section of the new chain); a one-section layout keeps the plain shift (the dug prefix stays where it is)
                    if (PhasePlan.CancelCrewsOf(s.Flags) > 1 && oldU > 0f) s.m_CancelFront = math.clamp(s.m_CancelFront * newU / oldU, 0f, newU);
                    else s.m_CancelFront += shift;
                }
                em.SetComponentData(pe, s);
            }
            proj.ChainLength = newU;
            proj.Revision++;
        }

        // ------------------------------------------------------------------ trims and shared nodes

        private void StepTrims()
        {
            for (int i = 0; i < m_Projects.Count; i++)
            {
                var proj = m_Projects[i];
                var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
                int key = proj.Revision * 31 + SiteRegistry.Revision * 7 + GeometryKey(proj);
                if (key == ps.TrimKey && m_Now - ps.TrimCheckedUpdate < DirConst.kTrimRefreshUpdates) continue;
                ps.TrimKey = key;
                ps.TrimCheckedUpdate = m_Now;
                ComputeTrims(proj);
            }
        }

        private static int GeometryKey(ProjectRecord proj)
        {
            int k = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(proj.Edges[i], out var r)) k = k * 17 + r.GeometryRevision;
            return k;
        }

        private void ComputeTrims(ProjectRecord proj)
        {
            var em = EntityManager;
            float U = proj.ChainLength;
            proj.TrimU0 = 0f;
            proj.TrimU1 = U;
            proj.StartIsJunction = proj.EndIsJunction = false;
            proj.StartShared = proj.EndShared = false;
            proj.ExitAtStart = proj.ExitAtEnd = false;
            // The chain car-half verdict waits until the exits of this record were computed (both chain ends resolved)
            var dps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            dps.ExitsKnown = false;
            proj.ExitsKnown = false;
            if (proj.Edges.Count == 0) return;
            Entity firstE = proj.Edges[0], lastE = proj.Edges[proj.Edges.Count - 1];
            if (!SiteRegistry.TryGetEdge(firstE, out var first) || !SiteRegistry.TryGetEdge(lastE, out var last)) return;
            if (!em.HasComponent<RoadWorksSite>(firstE) || !em.HasComponent<RoadWorksSite>(lastE)) return;
            var fs = em.GetComponentData<RoadWorksSite>(firstE);
            var ls = em.GetComponentData<RoadWorksSite>(lastE);

            // chain start: the node at u = ChainLo of the first edge
            bool fFwd = fs.m_ChainU1 >= fs.m_ChainU0;
            Entity startNode = fFwd ? first.StartNode : first.EndNode;
            ClassifyEnd(startNode, proj.Id, out bool sJunction, out bool sShared);
            proj.ExitAtStart = IsExitNode(startNode, proj.Id);   // leavers only drive to a connected end
            proj.StartIsJunction = sJunction;
            proj.StartShared = sShared;
            if (sJunction)
            {
                float scale = Scale(fs, first);
                proj.TrimU0 = fFwd ? fs.m_ChainU0 + first.Section.TrimStart * scale : fs.m_ChainU1 + first.Section.TrimEnd * scale;
            }

            // chain end: the node at u = ChainHi of the last edge
            bool lFwd = ls.m_ChainU1 >= ls.m_ChainU0;
            Entity endNode = lFwd ? last.EndNode : last.StartNode;
            ClassifyEnd(endNode, proj.Id, out bool eJunction, out bool eShared);
            proj.ExitAtEnd = IsExitNode(endNode, proj.Id);
            proj.EndIsJunction = eJunction;
            proj.EndShared = eShared;
            if (eJunction)
            {
                float scale = Scale(ls, last);
                proj.TrimU1 = lFwd ? ls.m_ChainU1 - last.Section.TrimEnd * scale : ls.m_ChainU0 - last.Section.TrimStart * scale;
            }
            proj.TrimU0 = math.clamp(proj.TrimU0, 0f, U);
            proj.TrimU1 = math.clamp(proj.TrimU1, proj.TrimU0, U);
            dps.ExitsKnown = true;
            proj.ExitsKnown = true;   // published for Machines (isolated road check)
            LogExits(proj, dps);
        }

        private static float Scale(in RoadWorksSite s, EdgeRecord r)
        {
            float len = r.Arc != null ? r.Arc.Length : 0f;
            return len > 0.01f ? math.abs(s.m_ChainU1 - s.m_ChainU0) / len : 1f;
        }

        // junction = the node has a connected edge that is not a registry edge; shared = it has an edge of another project.
        private void ClassifyEnd(Entity node, uint projectId, out bool junction, out bool shared)
        {
            junction = shared = false;
            var em = EntityManager;
            if (!EcsUtil.Alive(em, node) || !em.HasBuffer<ConnectedEdge>(node)) return;
            var buf = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < buf.Length; i++)
            {
                Entity ce = buf[i].m_Edge;
                if (ce == Entity.Null || !em.Exists(ce) || em.HasComponent<Deleted>(ce)) continue;
                if (!SiteRegistry.TryGetEdge(ce, out var r)) junction = true;
                else if (r.ProjectId != projectId) shared = true;
            }
        }
    }
}
