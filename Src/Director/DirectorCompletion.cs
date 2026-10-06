using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Director
{
    // Completion and deletion.
    // Frame N (p reaches 1): runtimes carry Completing, the visual modules tear down (props in frame N, before any node
    // deletion). From frame N + 1: construction sites are removed once every edge reports RoadWorksGround.Settled (the
    // reveal hold finished, no clone referenced); demolitions delete the edge + orphan nodes once Ground applied
    // Morph(t = 0), so deleting the road is invisible.
    //
    // Found in the first in-game test (every demolition waited kCompletionTimeoutUpdates): the Director's only demolition
    // signal was RoadWorksGround.m_AppliedT <= 1e-3. Ground writes a terrain profile only when its QUANTISED values
    // (1/16 m) change, and mirrors m_AppliedT from the last WRITE. Near the end of D2 Morph(t) and Morph(0) quantise
    // to the same profile for t up to a few %, so the edge already carried the final Morph(0) composition while
    // m_AppliedT stayed at the last written t > 0 forever. Ground now adopts the target arguments when the applied
    // profile is the target (Ground's SyncAppliedArgs); the Director additionally accepts the SAME condition from its
    // side of the contract: kind Morph, no hold / restore, and the applied middle cap / floor equal to the planned final
    // profile PhasePlan.Terrain(site at p = 1) = Morph(0, RestoreY) (identical quantisation, so the written terrain is
    // the final one and deleting the road is invisible). The timeout stays as the last resort and now logs why.
    public partial class WorksDirectorSystem
    {
        private readonly List<Entity> m_DoneEdges = new List<Entity>(16);
        private readonly List<Entity> m_NodeOthers = new List<Entity>(8);

        public static int CompletionTimeouts;       // dev state line: completions that hit kCompletionTimeoutUpdates
        public static int DemolitionFinalMatches;   // demolition edges accepted by the final-profile match (AppliedT stale)

        // Nothing is removed while the release gate holds (ps.Hold, decided this frame in the
        // edge pass from PhasePlan.MachinesHoldRoad): the road (Closed) is handed back only after every machine has left the
        // carriageway. Removing the record is what opens the road in Traffic, so the gate must clear first.
        private void StepCompletion()
        {
            for (int i = 0; i < m_Projects.Count; i++)
            {
                var proj = m_Projects[i];
                if (!SiteRegistry.Projects.ContainsKey(proj.Id)) continue;
                var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
                if (ps.CallOff)
                {
                    if (ps.Hold) continue;
                    try { FinishCallOff(proj); }
                    catch (Exception e) { RRWLog.ErrorOnce("director call-off", e); }
                    continue;
                }
                if (proj.Phase != WorksPhase.Complete) continue;
                if (!ps.CompletingSeen || ps.CompletingSince == m_Now) continue;   // never in frame N
                if (ps.Hold) continue;                                             // machines still on / bound for the carriageway
                try { TryFinish(proj, ps); }
                catch (Exception e) { RRWLog.ErrorOnce("director completion", e); }
            }
        }

        // D0 call-off, once the gate cleared: the road is still there: remove the sites, wear back to 0, the closure opens
        // (Traffic sees the records go).
        private void FinishCallOff(ProjectRecord proj)
        {
            m_DoneEdges.Clear();
            m_DoneEdges.AddRange(proj.Edges);
            for (int i = 0; i < m_DoneEdges.Count; i++)
                if (SiteRegistry.TryGetEdge(m_DoneEdges[i], out var rec)) RemoveSiteKeepEdge(rec, true);
            RRWLog.Info("director: demolition project #" + proj.Id + " called off: " + m_DoneEdges.Count + " edge(s) reopen"
                        + (proj.ReleaseSince != 0 ? " (" + (m_Now - proj.ReleaseSince) + " updates after the call-off)" : ""));
            m_DoneEdges.Clear();
        }

        private void TryFinish(ProjectRecord proj, DirProjectState ps)
        {
            var em = EntityManager;
            // the Ground wait (and its timeout) counts from when the road could be handed back (gate cleared), not from frame N
            uint since = ps.OpenedAfterRelease && ps.OpenedAt > ps.CompletingSince ? ps.OpenedAt : ps.CompletingSince;
            uint waited = m_Now - since;
            bool ready = true;
            WorksKind kind = proj.Kind;
            Entity blocking = Entity.Null;
            m_FinishHow = FinishHow.None;
            for (int i = 0; i < proj.Edges.Count && ready; i++)
            {
                Entity e = proj.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec)) continue;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e)) continue;
                ready = EdgeReadyToFinish(e, rec, kind, waited);
                if (!ready) blocking = e;
            }
            if (!ready)
            {
                if (waited < DirConst.kCompletionTimeoutUpdates) return;
                if (!ps.CompletionTimeoutLogged)
                {
                    ps.CompletionTimeoutLogged = true;
                    CompletionTimeouts++;
                    RRWLog.Warn("director: project #" + proj.Id + " waited " + waited + " updates for Ground to settle: finishing anyway (Ground's orphan path restores the clip); "
                                + DescribeGroundWait(blocking, kind));
                }
            }
            m_DoneEdges.Clear();
            m_DoneEdges.AddRange(proj.Edges);
            if (kind == WorksKind.Construction)
            {
                try { CompletionTreeCheck(proj); }
                catch (Exception e) { RRWLog.ErrorOnce("director completion tree check", e); }
                // upgrade works leave the vanilla wear alone (only the new strip is new road)
                for (int i = 0; i < m_DoneEdges.Count; i++)
                    if (SiteRegistry.TryGetEdge(m_DoneEdges[i], out var rec)) RemoveSiteKeepEdge(rec, rec.Upgrade == null);
                RRWLog.Info("director: construction project #" + proj.Id + " finished: " + m_DoneEdges.Count + " edge(s) open (waited " + waited + " updates)");
            }
            else
            {
                m_TmpEntities.Clear();
                for (int i = 0; i < m_DoneEdges.Count; i++)
                {
                    Entity e = m_DoneEdges[i];
                    if (!SiteRegistry.TryGetEdge(e, out var rec)) continue;
                    var st = rec.Get<DirEdgeState>(ModuleSlot.Director);
                    if (st != null && st.IconPrefab != Entity.Null && em.Exists(e)) RemoveIcon(e, st.IconPrefab);
                    if (EcsUtil.Alive(em, e)) m_TmpEntities.Add(e);
                }
                DemolishEdges(m_TmpEntities);
                for (int i = 0; i < m_DoneEdges.Count; i++)
                    if (SiteRegistry.TryGetEdge(m_DoneEdges[i], out var rec)) RemoveRecord(rec);
                RRWLog.Info("director: demolition project #" + proj.Id + " finished: " + m_TmpEntities.Count + " edge(s) removed (waited " + waited + " updates, ground "
                            + (ready ? FinishHowText(m_FinishHow) : "timeout") + ")");
                m_TmpEntities.Clear();
            }
            m_DoneEdges.Clear();
        }

        private bool EdgeReadyToFinish(Entity e, EdgeRecord rec, WorksKind kind, uint waited)
        {
            var em = EntityManager;
            var g = em.HasComponent<RoadWorksGround>(e) ? em.GetComponentData<RoadWorksGround>(e) : RoadWorksGround.Initial;
            if (kind == WorksKind.Construction)
            {
                if (rec.HiddenApplied) return false;            // un-hidden this frame: the reveal hold has not even started
                if (g.Settled) return true;
                // upgrade works were never dug or hidden: nothing to wait for unless Ground holds something on the edge
                if (rec.Upgrade != null) return !g.Known;
                // Ground never wrote (module absent or faulted): nothing to wait for after a grace period
                return !g.Known && waited >= DirConst.kGroundUnknownGraceUpdates;
            }
            var rt = em.HasComponent<RoadWorksRuntime>(e) ? em.GetComponentData<RoadWorksRuntime>(e) : default;
            var site = em.HasComponent<RoadWorksSite>(e) ? em.GetComponentData<RoadWorksSite>(e) : default;
            bool dug = site.Mode == VisualMode.FullDig && rt.HideWanted;
            if (!dug)
            {
                // road visible (mode D / excavation off): the terrain under it is vanilla; if Ground still holds a
                // reveal, deleting the edge is fine as well (no road mesh, TerrainSystem drops the edge)
                Note(FinishHow.Visible);
                return true;
            }
            if (!g.Known)
            {
                if (waited < DirConst.kGroundUnknownGraceUpdates) return false;
                Note(FinishHow.Unknown);
                return true;
            }
            if (g.m_Kind == TerrainProfileKind.Morph)
            {
                if (g.m_AppliedT <= 1e-3f) { Note(FinishHow.MorphZero); return true; }
                if (AppliedIsFinalMorph(site, rt, g))
                {
                    Note(FinishHow.FinalMatch);
                    DemolitionFinalMatches++;
                    return true;
                }
                return false;
            }
            if (g.Settled) { Note(FinishHow.Settled); return true; }
            return false;
        }

        // Ground's applied edge-slot profile IS the planned final demolition profile (Morph(0, RestoreY) after the
        // natural brackets), even if m_AppliedT still holds the t of the last write (see the header). Floors are
        // quantised to 1/16 m by TerrainProfile on both sides, so the comparison is exact.
        private static bool AppliedIsFinalMorph(in RoadWorksSite site, in RoadWorksRuntime rt, in RoadWorksGround g)
        {
            if ((g.m_Flags & (GroundFlags.RevealHold | GroundFlags.Restoring)) != 0) return false;
            var input = PlanInput.From(site, rt);
            input.P = 1f;   // the Complete profile, whatever p rounding the runtime carries
            var fin = PhasePlan.Terrain(input);
            if (fin.Kind != TerrainProfileKind.Morph) return false;
            return g.m_FloorMinRel == fin.MiddleCap && g.m_FloorMaxRel == fin.MiddleFloor;
        }

        private enum FinishHow : byte { None, Visible, MorphZero, FinalMatch, Settled, Unknown }
        private FinishHow m_FinishHow;

        // Most notable reason over the project's edges (for the finish log line; later enum values win).
        private void Note(FinishHow h) { if (m_FinishHow == FinishHow.None || h > m_FinishHow) m_FinishHow = h; }

        private static string FinishHowText(FinishHow h)
        {
            switch (h)
            {
                case FinishHow.Visible: return "not dug";
                case FinishHow.MorphZero: return "morph t=0";
                case FinishHow.FinalMatch: return "final profile applied (t stale)";
                case FinishHow.Settled: return "settled";
                case FinishHow.Unknown: return "never reported";
                default: return "-";
            }
        }

        // One line about the edge the completion waited for (timeout diagnostics).
        private string DescribeGroundWait(Entity e, WorksKind kind)
        {
            var em = EntityManager;
            if (e == Entity.Null || !em.Exists(e)) return "no blocking edge";
            var g = em.HasComponent<RoadWorksGround>(e) ? em.GetComponentData<RoadWorksGround>(e) : RoadWorksGround.Initial;
            string s = "edge " + RRWLog.E(e) + " ground kind=" + g.m_Kind + " flags=" + g.m_Flags + " t=" + RRWLog.F(g.m_AppliedT)
                       + " floor=[" + RRWLog.F(g.m_FloorMinRel) + "," + RRWLog.F(g.m_FloorMaxRel) + "] settled=" + g.Settled;
            if (kind == WorksKind.Demolition && em.HasComponent<RoadWorksSite>(e) && em.HasComponent<RoadWorksRuntime>(e))
            {
                var input = PlanInput.From(em.GetComponentData<RoadWorksSite>(e), em.GetComponentData<RoadWorksRuntime>(e));
                input.P = 1f;
                var fin = PhasePlan.Terrain(input);
                s += " final=" + fin.Kind + "[" + RRWLog.F(fin.MiddleCap) + "," + RRWLog.F(fin.MiddleFloor) + "]";
            }
            else if (SiteRegistry.TryGetEdge(e, out var rec)) s += " hiddenApplied=" + rec.HiddenApplied;
            return s;
        }

        // Removes the works from an edge that stays (construction finished, demolition called off in D0, lost-site
        // fallback). Hidden only on the transition; wear back to 0; icon removed; record removed.
        internal void RemoveSiteKeepEdge(EdgeRecord rec, bool wearZero)
        {
            var em = EntityManager;
            Entity e = rec.Edge;
            if (em.Exists(e) && !em.HasComponent<Deleted>(e))
            {
                if (rec.HiddenApplied) EcsUtil.SetHidden(em, e, false);
                var st = rec.Get<DirEdgeState>(ModuleSlot.Director);
                if (st != null && st.IconPrefab != Entity.Null) { RemoveIcon(e, st.IconPrefab); st.IconPrefab = Entity.Null; }
                if (wearZero && RRWDebug.On(DebugLayers.Wear)) SetWear(e, 0f);
                if (em.HasComponent<RoadWorksSite>(e)) em.RemoveComponent<RoadWorksSite>(e);
                if (em.HasComponent<RoadWorksRuntime>(e)) em.RemoveComponent<RoadWorksRuntime>(e);
                if (em.HasComponent<RoadWorksGround>(e)) em.RemoveComponent<RoadWorksGround>(e);
            }
            rec.HiddenApplied = false;
            RemoveRecord(rec);
        }

        // RoadWorksUtils.Demolish of the previous mod version, for a batch: delete the edges, then delete nodes left
        // without any edge (and without Owner); nodes that keep other edges get Updated together with those edges (vanilla geometry).
        internal void DemolishEdges(List<Entity> edges)
        {
            var em = EntityManager;
            for (int i = 0; i < edges.Count; i++)
                if (EcsUtil.Alive(em, edges[i])) em.AddComponent<Deleted>(edges[i]);
            for (int i = 0; i < edges.Count; i++)
            {
                Entity e = edges[i];
                if (!em.Exists(e) || !em.HasComponent<Edge>(e)) continue;
                var ed = em.GetComponentData<Edge>(e);
                DetachNode(ed.m_Start);
                if (ed.m_End != ed.m_Start) DetachNode(ed.m_End);
            }
        }

        private void DetachNode(Entity node)
        {
            var em = EntityManager;
            if (!EcsUtil.Alive(em, node)) return;
            m_NodeOthers.Clear();
            if (em.HasBuffer<ConnectedEdge>(node))
            {
                var buf = em.GetBuffer<ConnectedEdge>(node, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    Entity c = buf[i].m_Edge;
                    if (c != Entity.Null && em.Exists(c) && !em.HasComponent<Deleted>(c)) m_NodeOthers.Add(c);
                }
            }
            if (m_NodeOthers.Count == 0 && !em.HasComponent<Owner>(node))
            {
                em.AddComponent<Deleted>(node);
                DirectorShared.HiddenNodes.Remove(node);
            }
            else
            {
                if (!em.HasComponent<Updated>(node)) em.AddComponent<Updated>(node);
                for (int i = 0; i < m_NodeOthers.Count; i++)
                    if (!em.HasComponent<Updated>(m_NodeOthers[i])) em.AddComponent<Updated>(m_NodeOthers[i]);
            }
            m_NodeOthers.Clear();
        }
    }
}
