using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Director
{
    // Tool-preview flicker fix (verified in game).
    // While a net tool hovers over / snaps to a hidden works road, GenerateEdgesSystem (Mod2) creates Temp copies of the
    // edge (split at the cursor) that are not hidden: they would render the full road mesh + street lights over the
    // trench. This system marks them hidden:
    //   - Temp edges whose original is a registry edge with Hidden applied;
    //   - Temp edges without an original whose curve lies on a hidden works edge of the same prefab that has a Temp copy
    //     this frame (split pieces; the copy is the split signature, so a player's own new road is never matched);
    //   - Temp nodes whose connected Temp edges were all hidden here, or (no connected temp edge) whose original is a
    //     node the Director hid. A temp node that also joins a visible new road stays visible (it is that road's end).
    // TempFlags.Delete is exempt: the bulldozer's red highlight stays visible.
    //
    // Fixes "connecting to a road under construction reports collisions": an earlier version put the Hidden
    // COMPONENT on the Temps at Mod4. For a Temp, the Hidden component means "replaced original": vanilla EdgeIterator
    // skips such edges at a Temp node, so NodeAlign (Mod2B), CompositionSelect (Mod3),
    // Net.GeometrySystem / LaneSystem (Mod4), SecondaryObjects (Mod4B) and ValidationHelpers built the junction WITHOUT
    // the works road from the 2nd tool update on (Temps are reused and kept the component) -> spurious OverlapExisting /
    // InvalidShape. Now: vanilla's own hide for a Temp, TempFlags.Hidden, set at Modification2B right before
    // NodeAlignSystem (after Net.ReferencesSystem filled ConnectedEdge, before SubObjectSystem), so vanilla propagates
    // it to sub-objects (Mod2B), lanes (Mod4) and secondary objects/lanes (Mod4B) and BatchInstanceSystem culls them
    // all; topology stays vanilla. PreviewHideCatchUpSystem (Mod5) flags children vanilla did not regenerate.
    // The Hidden COMPONENT is never put on a Temp any more (a leftover one is stripped).
    [RegisterSystem(SystemUpdatePhase.Modification2B, Before = typeof(NodeAlignSystem), Order = RRWOrder.PreviewHide)]
    public partial class PreviewHideSystem : GameSystemBase
    {
        private EntityQuery m_TempEdges;
        private EntityQuery m_TempNodes;
        private readonly RRWGuard m_Guard = new RRWGuard("director preview hide");
        private readonly HashSet<Entity> m_HiddenEdges = new HashSet<Entity>();
        private readonly HashSet<Entity> m_WorksWithTemp = new HashSet<Entity>();   // works edges with a Temp copy this frame
        private readonly List<EdgeRecord> m_Candidates = new List<EdgeRecord>(64);
        private readonly List<Entity> m_Tmp = new List<Entity>(32);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_TempEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_TempNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Net.Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            RRWLog.Info("director: preview hide at Modification2B before NodeAlignSystem (TempFlags.Hidden, order " + RRWOrder.PreviewHide + ")");
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Guard.Reset();
            m_HiddenEdges.Clear();
            m_WorksWithTemp.Clear();
        }

        protected override void OnUpdate()
        {
            DirectorShared.PreviewHiddenNow.Clear();
            if (m_Guard.Faulted) return;
            var gm = GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game) return;
            long t0 = RRWPerf.Start();
            try
            {
                if (!RRWDebug.On(DebugLayers.PreviewHide))
                {
                    // A/B aid (rrw.layer previewhide 0): hand every Temp we flagged back to vanilla
                    if (DirectorShared.PreviewHidden.Count > 0) ReleaseStale();
                    m_Guard.Ok();
                    return;
                }
                bool anyHidden = DirectorShared.HiddenEdgeCount > 0 || DirectorShared.HiddenNodes.Count > 0;
                if (!anyHidden && DirectorShared.PreviewHidden.Count == 0) { m_Guard.Ok(); return; }
                if (m_TempEdges.IsEmptyIgnoreFilter && m_TempNodes.IsEmptyIgnoreFilter && DirectorShared.PreviewHidden.Count == 0) { m_Guard.Ok(); return; }
                Process(anyHidden);
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
            finally { RRWPerf.Stop(PerfSlot.Director, t0); }
        }

        private void Process(bool anyHidden)
        {
            var em = EntityManager;
            var now = DirectorShared.PreviewHiddenNow;
            m_HiddenEdges.Clear();
            m_WorksWithTemp.Clear();
            m_Candidates.Clear();
            int edges = 0, nodes = 0;

            if (anyHidden && !m_TempEdges.IsEmptyIgnoreFilter)
            {
                var arr = m_TempEdges.ToEntityArray(Allocator.Temp);
                try
                {
                    // pass 1: which works edges have a Temp copy this frame (any flags: the split's Delete copy included)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var temp = em.GetComponentData<Temp>(arr[i]);
                        if (temp.m_Original != Entity.Null && SiteRegistry.Edges.ContainsKey(temp.m_Original)) m_WorksWithTemp.Add(temp.m_Original);
                    }
                    // The vanilla mid-edge split makes NO temp edge copy of the works edge W. It is driven
                    // by a Temp NODE (TempFlags.Replace) whose m_Original is W (GenerateNodesSystem,
                    // GenerateEdgesSystem SplitEdge); the two pieces carry only TempFlags.Essential and no
                    // original (CreateTempEdge). That node is the split signature.
                    if (!m_TempNodes.IsEmptyIgnoreFilter)
                    {
                        var nodesArr = m_TempNodes.ToEntityArray(Allocator.Temp);
                        try
                        {
                            for (int i = 0; i < nodesArr.Length; i++)
                            {
                                var nt = em.GetComponentData<Temp>(nodesArr[i]);
                                Entity o = nt.m_Original;
                                if (o != Entity.Null && SiteRegistry.Edges.ContainsKey(o) && em.HasComponent<Edge>(o)) m_WorksWithTemp.Add(o);
                            }
                        }
                        finally { nodesArr.Dispose(); }
                    }
                    // split-piece candidates: hidden works edges that are being modified by this preview
                    foreach (var w in m_WorksWithTemp)
                        if (SiteRegistry.TryGetEdge(w, out var rec) && rec.HiddenApplied && rec.Arc != null) m_Candidates.Add(rec);

                    // pass 2: hide
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Entity e = arr[i];
                        var temp = em.GetComponentData<Temp>(e);
                        if ((temp.m_Flags & TempFlags.Delete) != 0) continue;
                        bool hide;
                        if (temp.m_Original != Entity.Null)
                            hide = SiteRegistry.TryGetEdge(temp.m_Original, out var orec) && orec.HiddenApplied;
                        else
                            hide = m_Candidates.Count > 0 && LiesOnHiddenWorksEdge(e);
                        if (!hide) continue;
                        m_HiddenEdges.Add(e);
                        if (Hide(e, temp)) edges++;
                    }
                }
                finally { arr.Dispose(); }
            }

            if (!m_TempNodes.IsEmptyIgnoreFilter && (m_HiddenEdges.Count > 0 || DirectorShared.HiddenNodes.Count > 0))
            {
                var arr = m_TempNodes.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Entity n = arr[i];
                        var temp = em.GetComponentData<Temp>(n);
                        if ((temp.m_Flags & TempFlags.Delete) != 0) continue;
                        if (!NodeShouldHide(n, temp)) continue;
                        if (Hide(n, temp)) nodes++;
                    }
                }
                finally { arr.Dispose(); }
            }

            ReleaseStale();
            DirectorShared.PreviewEdgesHidden = m_HiddenEdges.Count;
            DirectorShared.PreviewNodesHidden = now.Count - m_HiddenEdges.Count;
            DirectorShared.PreviewHideTotal += edges + nodes;
            if (edges + nodes > 0 && RRWLog.VerboseEnabled)
                RRWLog.Verbose("director: preview hide (TempFlags.Hidden) +" + edges + " temp edges +" + nodes + " temp nodes, split candidates " + m_Candidates.Count);
        }

        // Sets TempFlags.Hidden (+ BatchesUpdated) if missing; strips a leftover Hidden COMPONENT (set by an older version: it
        // removes the works road from vanilla's junction topology). Returns true when it changed the entity.
        private bool Hide(Entity e, Temp temp)
        {
            var em = EntityManager;
            DirectorShared.PreviewHiddenNow.Add(e);
            DirectorShared.PreviewHidden.Add(e);
            bool changed = false;
            if ((temp.m_Flags & TempFlags.Hidden) == 0)
            {
                temp.m_Flags |= TempFlags.Hidden;
                em.SetComponentData(e, temp);
                changed = true;
            }
            if (em.HasComponent<Hidden>(e))
            {
                em.RemoveComponent<Hidden>(e);
                DirectorShared.PreviewComponentStripped++;
                changed = true;
            }
            if (changed && !em.HasComponent<BatchesUpdated>(e)) em.AddComponent<BatchesUpdated>(e);
            return changed;
        }

        private bool NodeShouldHide(Entity node, Temp temp)
        {
            var em = EntityManager;
            int connected = 0, hidden = 0;
            if (em.HasBuffer<ConnectedEdge>(node))
            {
                var buf = em.GetBuffer<ConnectedEdge>(node, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    Entity ce = buf[i].m_Edge;
                    if (ce == Entity.Null || !em.Exists(ce) || em.HasComponent<Deleted>(ce)) continue;
                    connected++;
                    if (m_HiddenEdges.Contains(ce)) hidden++;
                }
            }
            if (connected > 0) return hidden == connected;
            return temp.m_Original != Entity.Null && DirectorShared.HiddenNodes.Contains(temp.m_Original);
        }

        // Split pieces at the cursor: a temp without an original whose start, middle and end lie within 1 m of a hidden
        // works edge of the same prefab (CurveMatch.LiesOn), restricted to works edges that have a Temp copy (edge
        // original) or a Temp split node (node original = the edge) this frame.
        private bool LiesOnHiddenWorksEdge(Entity e)
        {
            var em = EntityManager;
            var bez = em.GetComponentData<Curve>(e).m_Bezier;
            Entity prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
            float tol = RRWConst.kSplitMatchTolerance;
            float2 a = bez.a.xz, d = bez.d.xz, m = MathUtils.Position(bez, 0.5f).xz;
            for (int i = 0; i < m_Candidates.Count; i++)
            {
                var rec = m_Candidates[i];
                var st = rec.Get<DirEdgeState>(ModuleSlot.Director);
                if (st != null)
                {
                    if (prefab != Entity.Null && st.Prefab != Entity.Null && st.Prefab != prefab) continue;
                    float4 b = st.BoundsXZ;
                    if (!Inside(a, b, tol) || !Inside(d, b, tol) || !Inside(m, b, tol)) continue;
                }
                if (CurveMatch.LiesOn(bez, rec.Arc, tol)) return true;
            }
            return false;
        }

        private static bool Inside(float2 p, float4 b, float tol) =>
            p.x >= b.x - tol && p.y >= b.y - tol && p.x <= b.z + tol && p.y <= b.w + tol;

        // Temps we flagged earlier that no longer match (the works road was revealed while the preview persisted, or the
        // PreviewHide layer was switched off). A Temp vanilla regenerated this frame (Updated) carries vanilla's fresh flags
        // and is left alone; only a Temp that kept OUR flag gets it cleared. Permanent ones are the Director's (adoption).
        private void ReleaseStale()
        {
            var set = DirectorShared.PreviewHidden;
            var now = DirectorShared.PreviewHiddenNow;
            if (set.Count == now.Count) return;
            var em = EntityManager;
            m_Tmp.Clear();
            foreach (var e in set) if (!now.Contains(e)) m_Tmp.Add(e);
            for (int i = 0; i < m_Tmp.Count; i++)
            {
                Entity e = m_Tmp[i];
                if (!em.Exists(e) || em.HasComponent<Deleted>(e)) { set.Remove(e); continue; }
                if (!em.HasComponent<Temp>(e)) continue;   // became permanent: the Director adopts or releases it at Mod1
                set.Remove(e);
                bool changed = false;
                if (em.HasComponent<Hidden>(e)) { em.RemoveComponent<Hidden>(e); changed = true; }
                if (!em.HasComponent<Updated>(e))
                {
                    var temp = em.GetComponentData<Temp>(e);
                    if ((temp.m_Flags & TempFlags.Hidden) != 0)
                    {
                        temp.m_Flags &= ~TempFlags.Hidden;
                        em.SetComponentData(e, temp);
                        changed = true;
                    }
                }
                if (changed)
                {
                    if (!em.HasComponent<BatchesUpdated>(e)) em.AddComponent<BatchesUpdated>(e);
                    DirectorShared.PreviewReleasedTemps++;
                }
            }
            m_Tmp.Clear();
        }
    }

    // Catch-up + probe, Modification5 (after LaneSystem at Mod4 and SecondaryObject/Lane systems at
    // Mod4B). Vanilla copies the owner's TempFlags.Hidden to sub-objects / sub-lanes it (re)generates; a child it did NOT
    // regenerate in a frame where we flagged its owner (works road hidden while the preview persisted) would still render:
    // flag it here, and hand such children back once their owner is no longer ours.
    // Probe: a works edge / node the Director hid that is NOT Hidden here while tool previews exist would be
    // validated as a real obstacle (OverlapExisting). Counted for rrw.check / rrw.director.preview; Warn once per entity.
    [RegisterSystem(SystemUpdatePhase.Modification5, Order = RRWOrder.PreviewHideCatchUp)]
    public partial class PreviewHideCatchUpSystem : GameSystemBase
    {
        private EntityQuery m_TempEdges;
        private readonly RRWGuard m_Guard = new RRWGuard("director preview hide catch-up");
        private HashSet<Entity> m_Children = new HashSet<Entity>();
        private HashSet<Entity> m_ChildrenPrev = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ProbeWarned = new HashSet<Entity>();
        private readonly List<Entity> m_Owners = new List<Entity>(32);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_TempEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Guard.Reset();
            m_Children.Clear();
            m_ChildrenPrev.Clear();
            m_ProbeWarned.Clear();
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            var gm = GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game) return;
            try
            {
                if (DirectorShared.PreviewHiddenNow.Count > 0 || m_Children.Count > 0) CatchUp();
                Probe();
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
        }

        private void CatchUp()
        {
            var em = EntityManager;
            var swap = m_ChildrenPrev; m_ChildrenPrev = m_Children; m_Children = swap;
            m_Children.Clear();
            int added = 0;
            m_Owners.Clear();
            foreach (var e in DirectorShared.PreviewHiddenNow) m_Owners.Add(e);
            for (int i = 0; i < m_Owners.Count; i++)
            {
                Entity e = m_Owners[i];
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || !em.HasComponent<Temp>(e)) continue;
                if ((em.GetComponentData<Temp>(e).m_Flags & TempFlags.Hidden) == 0) continue;
                added += FlagChildren(e, 0);
            }
            m_Owners.Clear();
            // children we flagged whose owner is no longer ours: hand back (unless vanilla regenerated them this frame)
            int released = 0;
            foreach (var c in m_ChildrenPrev)
            {
                if (m_Children.Contains(c)) continue;
                if (!em.Exists(c) || em.HasComponent<Deleted>(c) || !em.HasComponent<Temp>(c) || em.HasComponent<Updated>(c)) continue;
                var t = em.GetComponentData<Temp>(c);
                if ((t.m_Flags & TempFlags.Hidden) == 0) continue;
                t.m_Flags &= ~TempFlags.Hidden;
                em.SetComponentData(c, t);
                if (!em.HasComponent<BatchesUpdated>(c)) em.AddComponent<BatchesUpdated>(c);
                released++;
            }
            m_ChildrenPrev.Clear();
            DirectorShared.PreviewCatchUpTotal += added;
            if ((added > 0 || released > 0) && RRWLog.VerboseEnabled)
                RRWLog.Verbose("director: preview catch-up flagged " + added + " temp children, released " + released);
        }

        // Sub-lanes and sub-objects (two levels: street light -> its own sub-objects / lanes).
        private int FlagChildren(Entity owner, int depth)
        {
            var em = EntityManager;
            int n = 0;
            if (em.HasBuffer<Game.Net.SubLane>(owner))
            {
                var buf = em.GetBuffer<Game.Net.SubLane>(owner, true);
                for (int i = 0; i < buf.Length; i++) n += FlagOne(buf[i].m_SubLane);
            }
            if (em.HasBuffer<Game.Objects.SubObject>(owner))
            {
                m_Sub.Clear();
                var buf = em.GetBuffer<Game.Objects.SubObject>(owner, true);
                for (int i = 0; i < buf.Length; i++) m_Sub.Add(buf[i].m_SubObject);
                int count = m_Sub.Count;
                for (int i = 0; i < count; i++)
                {
                    Entity c = m_Sub[i];
                    n += FlagOne(c);
                    if (depth == 0 && em.Exists(c) && em.HasComponent<Temp>(c)) m_Sub2.Add(c);
                }
                if (depth == 0)
                {
                    for (int i = 0; i < m_Sub2.Count; i++) n += FlagChildren(m_Sub2[i], 1);
                    m_Sub2.Clear();
                }
            }
            return n;
        }

        private readonly List<Entity> m_Sub = new List<Entity>(16);
        private readonly List<Entity> m_Sub2 = new List<Entity>(16);

        private int FlagOne(Entity c)
        {
            var em = EntityManager;
            if (c == Entity.Null || !em.Exists(c) || em.HasComponent<Deleted>(c) || !em.HasComponent<Temp>(c)) return 0;
            var t = em.GetComponentData<Temp>(c);
            if ((t.m_Flags & TempFlags.Delete) != 0) return 0;
            m_Children.Add(c);
            if ((t.m_Flags & TempFlags.Hidden) != 0) return 0;
            t.m_Flags |= TempFlags.Hidden;
            em.SetComponentData(c, t);
            if (!em.HasComponent<BatchesUpdated>(c)) em.AddComponent<BatchesUpdated>(c);
            return 1;
        }

        private void Probe()
        {
            if (m_TempEdges.IsEmptyIgnoreFilter) { DirectorShared.ProbeUnhiddenWorks = 0; return; }
            var em = EntityManager;
            int bad = 0;
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                if (!rec.HiddenApplied) continue;
                Entity e = rec.Edge;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<Hidden>(e)) continue;
                bad++;
                Report(e, "edge");
            }
            foreach (var n in DirectorShared.HiddenNodes)
            {
                if (!em.Exists(n) || em.HasComponent<Deleted>(n) || em.HasComponent<Hidden>(n)) continue;
                bad++;
                Report(n, "node");
            }
            DirectorShared.ProbeUnhiddenWorks = bad;
            if (bad > 0)
            {
                DirectorShared.ProbeUnhiddenTotal += bad;
                DirectorShared.ProbeUnhiddenLastUpdate = RRWClock.UpdateIndex;
            }
        }

        private void Report(Entity e, string what)
        {
            if (m_ProbeWarned.Count > 64 || !m_ProbeWarned.Add(e)) return;
            RRWLog.Warn("director: probe (U1 rank 2): works " + what + " " + RRWLog.E(e) + " is not Hidden at Modification5 while tool previews exist "
                        + "(vanilla validation would treat it as an obstacle)");
        }
    }
}
