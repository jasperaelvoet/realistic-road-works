using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Director
{
    // WorksRequests (UI, settings, Tools, dev) drained at Mod1. Every request is handled
    // in its own try/catch: one bad request never blocks the others. Works while the game is paused.
    public partial class WorksDirectorSystem
    {
        private readonly List<WorksRequest> m_Requests = new List<WorksRequest>(16);
        private readonly Dictionary<uint, List<Entity>> m_CancelEdgeGroups = new Dictionary<uint, List<Entity>>();
        private readonly Dictionary<int, List<Entity>> m_StartBatchesC = new Dictionary<int, List<Entity>>();
        private readonly Dictionary<int, List<Entity>> m_StartBatchesD = new Dictionary<int, List<Entity>>();
        private readonly List<SiteFactoryEdge> m_FactoryIn = new List<SiteFactoryEdge>(16);
        private readonly List<SiteFactoryResult> m_FactoryOut = new List<SiteFactoryResult>(16);
        private readonly List<Entity> m_ReqEdges = new List<Entity>(16);
        private readonly List<ProjectRecord> m_ReqProjects = new List<ProjectRecord>(16);

        private void StepRequests()
        {
            if (WorksRequests.Count == 0) return;
            m_Requests.Clear();
            WorksRequests.Drain(m_Requests);
            foreach (var l in m_CancelEdgeGroups.Values) l.Clear();
            foreach (var l in m_StartBatchesC.Values) l.Clear();
            foreach (var l in m_StartBatchesD.Values) l.Clear();

            for (int i = 0; i < m_Requests.Count; i++)
            {
                var r = m_Requests[i];
                try { Handle(r); }
                catch (Exception e) { RRWLog.ErrorOnce("director request " + r.Type, e); }
            }
            foreach (var kv in m_CancelEdgeGroups)
            {
                if (kv.Value.Count == 0) continue;
                try { CancelEdges(kv.Key, kv.Value); }
                catch (Exception e) { RRWLog.ErrorOnce("director request CancelEdge", e); }
            }
            foreach (var kv in m_StartBatchesC)
            {
                if (kv.Value.Count == 0) continue;
                try { StartWorks(kv.Value, WorksKind.Construction); }
                catch (Exception e) { RRWLog.ErrorOnce("director request StartConstruction", e); }
            }
            foreach (var kv in m_StartBatchesD)
            {
                if (kv.Value.Count == 0) continue;
                try { StartWorks(kv.Value, WorksKind.Demolition); }
                catch (Exception e) { RRWLog.ErrorOnce("director request StartDemolition", e); }
            }
            m_Requests.Clear();
        }

        private static void AddTo<TKey>(Dictionary<TKey, List<Entity>> d, TKey key, Entity e)
        {
            if (!d.TryGetValue(key, out var l)) { l = new List<Entity>(4); d.Add(key, l); }
            if (!l.Contains(e)) l.Add(e);
        }

        private void Handle(WorksRequest r)
        {
            switch (r.Type)
            {
                case WorksRequestType.FinishAll:
                    m_ReqProjects.Clear();
                    foreach (var p in SiteRegistry.Projects.Values) m_ReqProjects.Add(p);
                    for (int i = 0; i < m_ReqProjects.Count; i++)
                    {
                        var fp = m_ReqProjects[i].Get<DirProjectState>(ModuleSlot.Director);
                        if (fp != null && fp.CallOff) continue;   // a called-off demolition keeps its road
                        SetProgress(m_ReqProjects[i], 1f, "finish all");
                    }
                    RRWLog.Info("director: finish all works (" + m_ReqProjects.Count + " projects)");
                    return;
                case WorksRequestType.RebuildVisuals:
                    m_RebuildAllThisFrame = true;
                    RRWLog.Info("director: rebuild visuals requested (NeedsRebuild on every runtime this frame)");
                    return;
                case WorksRequestType.StartConstruction:
                    if (r.Edge != Entity.Null) AddTo(m_StartBatchesC, r.Batch, r.Edge);
                    return;
                case WorksRequestType.StartDemolition:
                    if (r.Edge != Entity.Null) AddTo(m_StartBatchesD, r.Batch, r.Edge);
                    return;
                case WorksRequestType.CancelEdge:
                    if (SiteRegistry.TryGetEdge(r.Edge, out var cr)) AddTo(m_CancelEdgeGroups, cr.ProjectId, r.Edge);
                    else RRWLog.Verbose("director: CancelEdge for a non-works edge " + RRWLog.E(r.Edge) + " ignored");
                    return;
                case WorksRequestType.CancelEdgeInstant:
                    CancelEdgeInstant(r.Edge);
                    return;
            }

            // Pause works was removed. Values 1 / 2 (the old pause and resume requests) are dropped.
            if ((byte)r.Type == 1 || (byte)r.Type == 2)
            {
                RRWLog.Once("director-pause-removed", "director: a pause/resume works request was dropped (the feature was removed)");
                return;
            }

            var proj = ResolveProject(r);
            if (proj == null)
            {
                RRWLog.Verbose("director: request " + r.Type + " without a works project (edge " + RRWLog.E(r.Edge) + ", id " + r.ProjectId + ")");
                return;
            }
            var rps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            if (rps.CallOff)
            {
                // the demolition was called off: its machines are leaving and the road is handed back; nothing else applies
                RRWLog.Info("director: request " + r.Type + " refused for project #" + proj.Id + ": the demolition was called off, the road reopens once the machines have left");
                return;
            }
            if (r.Type == kSetCrewsRequest) { SetCrews(proj, rps, r.Value); return; }
            switch (r.Type)
            {
                case WorksRequestType.Rush: Rush(proj); break;
                case WorksRequestType.Cancel: CancelProject(proj, false); break;
                case WorksRequestType.CancelInstant: CancelProject(proj, true); break;
                case WorksRequestType.Finish: SetProgress(proj, 1f, "finish"); break;
                case WorksRequestType.SetProgress: SetProgress(proj, math.saturate(r.Value), "set"); break;
                case WorksRequestType.JumpPhase:
                {
                    if (r.Phase == WorksPhase.Complete) { SetProgress(proj, 1f, "jump"); break; }
                    if (r.Phase == WorksPhase.None || PhasePlan.KindOf(r.Phase) != ProjectKind(proj))
                    {
                        RRWLog.Info("director: jump to " + r.Phase + " refused for " + ProjectKind(proj) + " project #" + proj.Id);
                        break;
                    }
                    SetProgress(proj, PhasePlan.ProgressAt(r.Phase, r.Value), "jump " + r.Phase);
                    break;
                }
            }
        }

        private ProjectRecord ResolveProject(WorksRequest r)
        {
            if (r.ProjectId != 0 && SiteRegistry.TryGetProject(r.ProjectId, out var p)) return p;
            if (r.Edge == Entity.Null) return null;
            if (SiteRegistry.TryGetEdge(r.Edge, out var rec) && SiteRegistry.TryGetProject(rec.ProjectId, out p)) return p;
            var em = EntityManager;
            if (em.Exists(r.Edge))
            {
                if (em.HasComponent<RRWDerived>(r.Edge) && SiteRegistry.TryGetProject(em.GetComponentData<RRWDerived>(r.Edge).m_ProjectId, out p)) return p;
                if (em.HasComponent<RRWMachine>(r.Edge) && SiteRegistry.TryGetProject(em.GetComponentData<RRWMachine>(r.Edge).m_ProjectId, out p)) return p;
                if (em.HasComponent<RoadWorksSite>(r.Edge) && SiteRegistry.TryGetProject(em.GetComponentData<RoadWorksSite>(r.Edge).m_ProjectId, out p)) return p;
            }
            return null;
        }

        // Kind straight from the saved sites (the ProjectRecord may not be filled yet this frame).
        private WorksKind ProjectKind(ProjectRecord proj)
        {
            var em = EntityManager;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (em.Exists(e) && em.HasComponent<RoadWorksSite>(e)) return em.GetComponentData<RoadWorksSite>(e).Kind;
            }
            return proj.Kind;
        }

        // Max progress over the project's saved sites (every edge shares it after harmonisation).
        private float ProjectProgress(ProjectRecord proj, out RoadWorksSite first, out int count)
        {
            var em = EntityManager;
            float p = 0f;
            first = default;
            count = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (count == 0) first = s;
                p = math.max(p, s.Progress);
                count++;
            }
            return p;
        }

        private void SetFlagAll(ProjectRecord proj, SiteFlags flag, bool on)
        {
            var em = EntityManager;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (s.Has(flag) == on) continue;
                s.Set(flag, on);
                em.SetComponentData(e, s);
            }
        }

        // Dev SetProgress / JumpPhase / Finish: WorkDone = round(p * Required) on all edges; model hard reset + ModelReset.
        private void SetProgress(ProjectRecord proj, float p, string why)
        {
            var em = EntityManager;
            p = math.saturate(p);
            float before = -1f;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (before < 0f) before = s.Progress;
                s.m_WorkDone = (uint)math.min((double)s.m_WorkRequired, math.round(p * (double)s.m_WorkRequired));
                em.SetComponentData(e, s);
            }
            var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            ps.ResetModel = true;
            ps.Carry = 0.0;
            ps.ProgressJumped = true;   // the stage switch is re-derived from the new p (like a load)
            RRWLog.Info("director: project #" + proj.Id + " " + why + " p " + RRWLog.F(math.max(0f, before)) + " -> " + RRWLog.F(p)
                        + " (" + PhasePlan.PhaseOf(ProjectKind(proj), p, out float _) + ")");
        }

        // ------------------------------------------------------------------ dev: SetCrews

        // WorksRequestType.SetCrews = 14 (Src/Dev rrw.crews <site> <n|auto>; a Core enum member). Value = n in 1..kMaxCrewsPerProject pins the crew count until the next phase change (re-latch in this
        // update's Accrue; capped by the kMinSectionLength rule so no section is shorter than 60 m); 0 = back to the automatic latch.
        internal const WorksRequestType kSetCrewsRequest = WorksRequestType.SetCrews;

        private void SetCrews(ProjectRecord proj, DirProjectState ps, float value)
        {
            int n = (int)math.round(value);
            if (n < 0 || n > RRWConst.kMaxCrewsPerProject)
            {
                RRWLog.Info("director: SetCrews " + RRWLog.F(value) + " refused for project #" + proj.Id + " (1.." + RRWConst.kMaxCrewsPerProject + " or 0 = auto)");
                return;
            }
            ps.CrewsOverride = n;
            RRWLog.Info("director: project #" + proj.Id + " dev SetCrews " + (n == 0 ? "auto" : n.ToString()) + " (now " + proj.Crews + " crews in " + proj.Phase
                        + "): re-latched this update" + (n > 0 ? ", until the next phase change" : ""));
        }

        // ------------------------------------------------------------------ rush

        private void Rush(ProjectRecord proj)
        {
            var em = EntityManager;
            var s = m_S;
            float p = ProjectProgress(proj, out var first, out int count);
            if (count == 0) return;
            if (first.Has(SiteFlags.Rushed)) { RRWLog.Info("director: project #" + proj.Id + " is already rushed"); return; }
            if (p >= 1f) return;
            long cost = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                cost += WorkTime.RushCost(site.m_PaidCost, math.abs(site.m_ChainU1 - site.m_ChainU0), p, s.RushCostPercent);
            }
            int c = (int)math.min(cost, int.MaxValue);
            if (!EcsUtil.TryPay(em, m_CitySystem.City, c))
            {
                RRWLog.Info("director: project #" + proj.Id + " rush refused: cannot afford " + c);
                return;
            }
            SetFlagAll(proj, SiteFlags.Rushed, true);
            RRWLog.Info("director: project #" + proj.Id + " rushed for " + c + " (24 h shift, x" + RRWLog.F(RRWConst.kRushRateMultiplier) + ")");
        }

        // ------------------------------------------------------------------ cancel

        // frontOverride: the cancel front in this project's chain coordinates when it was just split off a larger chain
        // (the main front of the ORIGINAL chain mapped into the split-off chain, so the decals continue from what was
        // really dug on these edges).
        private void CancelProject(ProjectRecord proj, bool instant, float frontOverride = float.NaN)
        {
            var em = EntityManager;
            var s = m_S;
            float p = ProjectProgress(proj, out var first, out int count);
            if (count == 0) return;
            var kind = first.Kind;

            if (kind == WorksKind.Demolition)
            {
                var dph = PhasePlan.PhaseOf(kind, p, out float _);
                if (dph != WorksPhase.BreakUp)
                {
                    RRWLog.Info("director: cancel refused for demolition project #" + proj.Id + " in " + dph + " (road bed already removed)");
                    return;
                }
                if (first.Has(SiteFlags.CancelledBuild))
                {
                    // the removal of a cancelled (and refunded) construction cannot be called off: that would hand out a
                    // finished road for the unrefunded share
                    RRWLog.Info("director: cancel refused for project #" + proj.Id + ": it removes a cancelled construction");
                    return;
                }
                // D0: the road is still there. No immediate removal (the breaker / trucks stand on the
                // lanes): the project releases the road (ReleaseSince, Releasing: no accrual, no spawns, Props tear down,
                // Machines leave); StepCompletion removes the sites (wear back to 0, the closure opens) once the gate clears.
                var dps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
                if (dps.CallOff) return;
                dps.CallOff = true;
                dps.Carry = 0.0;
                dps.GateActive = false;
                BeginRelease(proj, dps);
                SetFlagAll(proj, SiteFlags.CalledOff, true);   // saved: a save taken while the machines leave reopens on load
                RRWLog.Info("director: demolition project #" + proj.Id + " called off (" + proj.Edges.Count + " edges reopen "
                            + (proj.Mode == VisualMode.FullDig ? "once the machines have left)" : "now)"));
                return;
            }

            var phase = PhasePlan.PhaseOf(kind, p, out float f);
            if (phase == WorksPhase.Complete) { RRWLog.Info("director: cancel refused: project #" + proj.Id + " is complete"); return; }

            if (instant && !s.InstantCancelUnfinished)
            {
                RRWLog.Info("director: instant cancel is switched off in the settings: normal cancel of project #" + proj.Id);
                instant = false;
            }
            if (instant)
            {
                if (p < RRWConst.kInstantCancelProgress + DirConst.kCancelInstantSlack)
                {
                    m_ReqEdges.Clear();
                    m_ReqEdges.AddRange(proj.Edges);
                    InstantDelete(m_ReqEdges, p, "project #" + proj.Id);
                    return;
                }
                RRWLog.Info("director: instant cancel of project #" + proj.Id + " at p=" + RRWLog.F(p) + " is too late: normal cancel");
            }

            float U = 0f;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (em.Exists(e) && em.HasComponent<RoadWorksSite>(e)) U = math.max(U, em.GetComponentData<RoadWorksSite>(e).m_ChainLength);
            }
            // CancelFront is the ONE-crew front of the stage-aware view (its unit CancelFront / U is
            // shared by every section) and the crew layout is saved in SiteFlags bits 9-11; the cancel spans rebuild the per-section
            // fronts from both (FrontSet.Cancel). A chain split off a larger project (frontOverride) gets one section whose front
            // keeps the done length of its edges in the original layout (exact when they lay inside one original section).
            int crews;
            float F;
            if (float.IsNaN(frontOverride))
            {
                var cv = CancelView(proj, kind, phase, f, U, out crews);
                F = PhasePlan.MainFront(cv);
            }
            else
            {
                crews = 1;
                F = math.clamp(frontOverride, 0f, U);
            }
            bool hiddenPhase = phase <= WorksPhase.Foundation;
            uint demReq = 1;
            if (!hiddenPhase)
            {
                Entity e0 = Entity.Null;
                for (int i = 0; i < proj.Edges.Count && e0 == Entity.Null; i++) if (em.Exists(proj.Edges[i])) e0 = proj.Edges[i];
                var rc = e0 != Entity.Null ? EcsUtil.RoadClass(em, e0) : default;
                demReq = WorkTime.FramesFromHours(WorkTime.WorkHours(WorksKind.Demolition, U, rc, s));
            }

            long refund = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                refund += WorkTime.CancelRefund(site.m_PaidCost, p, false);
                site.m_CancelPhase = (byte)phase;
                site.m_CancelFront = F;
                site.Flags = PhasePlan.EncodeCancelCrews(site.Flags | SiteFlags.CancelledBuild, crews);
                site.Set(SiteFlags.LegacyPaused, false);
                site.Kind = WorksKind.Demolition;
                if (hiddenPhase)
                {
                    // C0..C2 (road hidden): straight to D2, restoring from the exact current terrain of this edge
                    RestoreFrom(e, site, phase, f, crews, F, out float t, out float y);
                    site.m_RestoreT = RoadWorksSite.EncodeT(t);
                    site.m_RestoreY16 = RoadWorksSite.EncodeY16(y);
                    site.m_WorkDone = (uint)math.min((double)site.m_WorkRequired, math.round(RRWConst.kD2 * (double)site.m_WorkRequired));
                }
                else
                {
                    // C3/C4 (road visible): a normal D0 break-up of the new road from the covers that existed at cancel
                    site.m_WorkRequired = demReq;
                    site.m_WorkDone = 0;
                    site.m_RestoreT = 255;
                    site.m_RestoreY16 = RoadWorksSite.EncodeY16(-RRWConst.kDemolitionDepth);
                }
                site.m_PaidCost = 0;   // refunded now: nothing left to refund later
                em.SetComponentData(e, site);
            }
            int credit = (int)math.min(refund, int.MaxValue);
            EcsUtil.Credit(em, m_CitySystem.City, credit);
            var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            ps.ResetModel = true;
            ps.Carry = 0.0;
            ps.GateInit = false;   // a D0 after a C3/C4 cancel mobilises like any demolition
            ps.ProgressJumped = true;   // a running C4 switch is dropped; D0 re-derives its own from p
            proj.Kind = WorksKind.Demolition;
            proj.Revision++;
            RRWLog.Info("director: construction project #" + proj.Id + " cancelled in " + phase + " at p=" + RRWLog.F(p) + " front=" + RRWLog.F(F)
                        + " crews=" + crews + (float.IsNaN(frontOverride) ? "" : " (split off: one section)")
                        + " refund " + credit + (hiddenPhase ? " -> restore (D2)" : " -> break-up (D0)"));
        }

        // Stage-aware view of a construction at (phase, f) for the cancel data, and the crew layout of that phase: the latched
        // ProjectRecord.Crews when the record is in that phase (it always is unless a dev SetProgress ran earlier in this batch),
        // else the count the latch would pick.
        private ProjectView CancelView(ProjectRecord proj, WorksKind kind, WorksPhase phase, float f, float U, out int crews)
        {
            var v = proj.View();
            v.Kind = kind;
            v.Phase = phase;
            v.F = f;
            v.U = U;
            if (v.Trim1 <= 0f || v.Trim1 > U) v.Trim1 = U;
            var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            crews = ps.CrewsLatched && proj.Phase == phase && proj.Kind == kind ? proj.Crews : CrewCountNow(proj, ps, v);
            crews = math.clamp(crews, 1, RRWConst.kMaxCrewsPerProject);
            v.Crews = crews;
            return v;
        }

        // RestoreT/Y of a cancelled hidden construction: Ground's applied profile, or (Ground has not written yet, e.g.
        // a cancel in the creation frame) the planned terrain profile of the phase.
        // `crews` / `cancelFront` = the cancel layout (CancelProject); the C1 fallback depth uses the edge's done fraction
        // over the crew sections (PhasePlan.EdgeDoneFraction), or the single split-off front.
        private void RestoreFrom(Entity e, in RoadWorksSite site, WorksPhase phase, float f, int crews, float cancelFront, out float t, out float y)
        {
            var em = EntityManager;
            var g = em.HasComponent<RoadWorksGround>(e) ? em.GetComponentData<RoadWorksGround>(e) : RoadWorksGround.Initial;
            if (g.Known && g.m_Kind != TerrainProfileKind.Vanilla)
            {
                t = math.saturate(g.m_AppliedT);
                y = float.IsNaN(g.m_AppliedY) ? 0f : g.m_AppliedY;
                return;
            }
            float dSub = RRWConst.kSubgradeDepthSmall;
            if (SiteRegistry.TryGetEdge(e, out var rec) && rec.SubgradeDepth > 0f) dSub = rec.SubgradeDepth;
            dSub = math.min(dSub, RRWConst.kMaxDepth);
            bool replaced = site.Has(SiteFlags.Replaced);
            switch (phase)
            {
                case WorksPhase.Survey:
                    t = replaced ? 1f : 0f; y = 0f; return;
                case WorksPhase.Excavation:
                {
                    float g01 = crews > 1
                        ? PhasePlan.EdgeDoneFraction(phase, f, site.m_ChainLength, crews, site.ChainLo, site.ChainHi)
                        : PhasePlan.LocalFraction(cancelFront, site.ChainLo, site.ChainHi);   // one crew: = MainFront
                    float ease = math.smoothstep(RRWConst.kC1DepthEase0, RRWConst.kC1DepthEase1, g01);
                    if (replaced) { t = 1f; y = math.lerp(0f, -dSub, ease); }
                    else { t = ease; y = -dSub; }
                    return;
                }
                default:
                    t = 1f; y = math.lerp(-dSub, RRWConst.kBaseTopY, PhasePlan.Sweep(f, 0f, 0.95f)); return;
            }
        }

        // Bulldozer on some edges of a construction project (p >= 5 %): split them off into their own project(s) via
        // SiteFactory (re-based chain coordinates, WorkDone/Required kept), then cancel those.
        private void CancelEdges(uint projectId, List<Entity> edges)
        {
            if (!SiteRegistry.TryGetProject(projectId, out var proj)) return;
            var em = EntityManager;
            if (ProjectKind(proj) != WorksKind.Construction) { RRWLog.Verbose("director: CancelEdge on demolition project #" + projectId + " ignored"); return; }
            int inProject = 0;
            for (int i = 0; i < edges.Count; i++) if (proj.Edges.Contains(edges[i])) inProject++;
            if (inProject == 0) return;
            if (inProject >= proj.Edges.Count)
            {
                CancelProject(proj, false);
                return;
            }
            // main front of the original chain at this moment
            float p = ProjectProgress(proj, out var _, out int _);
            var phase = PhasePlan.PhaseOf(WorksKind.Construction, p, out float f);
            float oldU = 0f;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (em.Exists(e) && em.HasComponent<RoadWorksSite>(e)) oldU = math.max(oldU, em.GetComponentData<RoadWorksSite>(e).m_ChainLength);
            }
            var ov = CancelView(proj, WorksKind.Construction, phase, f, oldU, out int oldCrews);
            float oldF = PhasePlan.MainFront(ov);
            var newIds = SplitOff(proj, edges, FrontSet.Of(oldF, oldCrews, oldU));
            for (int i = 0; i < newIds.Count; i++)
                if (SiteRegistry.TryGetProject(newIds[i], out var np))
                    CancelProject(np, false, m_SplitFront.TryGetValue(np.Id, out float cf) ? cf : float.NaN);
        }

        private readonly List<uint> m_SplitIds = new List<uint>(4);
        private readonly Dictionary<uint, float> m_SplitFront = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> m_SplitOldStart = new Dictionary<uint, float>();
        private readonly Dictionary<uint, bool> m_SplitFlip = new Dictionary<uint, bool>();

        // oldFronts = the fronts of the original chain (per crew section); fills m_SplitFront (new project id -> the
        // one-section front in its own chain: the done length of [old start, old start + U'] in the original layout, which is
        // the exact front when the split-off edges lay inside one original section, and oldF - oldStart for one crew).
        private List<uint> SplitOff(ProjectRecord proj, List<Entity> edges, FrontSet oldFronts)
        {
            var em = EntityManager;
            m_SplitIds.Clear();
            m_FactoryIn.Clear();
            m_FactoryOut.Clear();
            for (int i = 0; i < edges.Count; i++)
            {
                Entity e = edges[i];
                if (!proj.Edges.Contains(e) || !em.Exists(e) || !em.HasComponent<RoadWorksSite>(e) || !em.HasComponent<Edge>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                var ed = em.GetComponentData<Edge>(e);
                float len = em.HasComponent<Curve>(e) ? em.GetComponentData<Curve>(e).m_Length : math.abs(site.m_ChainU1 - site.m_ChainU0);
                m_FactoryIn.Add(new SiteFactoryEdge
                {
                    Edge = e, StartNode = ed.m_Start, EndNode = ed.m_End, Length = len,
                    DigEligible = site.Mode == VisualMode.FullDig, PaidCost = site.m_PaidCost, Dependants = site.Has(SiteFlags.Dependants),
                });
            }
            if (m_FactoryIn.Count == 0) return m_SplitIds;
            SiteFactory.CreateProjects(m_FactoryIn, WorksKind.Construction, EcsUtil.RoadClass(em, m_FactoryIn[0].Edge), m_S, SiteFlags.None, m_FactoryOut);
            // per new chain: run it in the same direction as the original chain (flip if SiteFactory started at the other
            // end), so new u = old u - (old u of its start) and the original front maps straight into it
            m_SplitFront.Clear();
            m_SplitOldStart.Clear();
            m_SplitFlip.Clear();
            for (int i = 0; i < m_FactoryOut.Count; i++)
            {
                var res = m_FactoryOut[i];
                var old = em.GetComponentData<RoadWorksSite>(res.Edge);
                uint id = res.Site.m_ProjectId;
                if (!m_SplitFlip.ContainsKey(id))
                    m_SplitFlip[id] = (old.m_ChainU1 >= old.m_ChainU0) != (res.Site.m_ChainU1 >= res.Site.m_ChainU0);
                m_SplitOldStart[id] = m_SplitOldStart.TryGetValue(id, out float os) ? math.min(os, old.ChainLo) : old.ChainLo;
            }
            for (int i = 0; i < m_FactoryOut.Count; i++)
            {
                var res = m_FactoryOut[i];
                var old = em.GetComponentData<RoadWorksSite>(res.Edge);
                var site = old;   // everything kept (p, mode, flags, money, natural samples, seed) except the chain
                uint id = res.Site.m_ProjectId;
                float U = res.Site.m_ChainLength;
                bool flip = m_SplitFlip[id];
                site.m_ProjectId = id;
                site.m_ChainU0 = flip ? U - res.Site.m_ChainU0 : res.Site.m_ChainU0;
                site.m_ChainU1 = flip ? U - res.Site.m_ChainU1 : res.Site.m_ChainU1;
                site.m_ChainLength = U;
                float os0 = m_SplitOldStart[id];
                m_SplitFront[id] = oldFronts.N <= 1 ? math.clamp(oldFronts.Single - os0, 0f, U)
                                                    : (U > 0f ? U * oldFronts.Covered(os0, os0 + U) : 0f);
                em.SetComponentData(res.Edge, site);
                if (SiteRegistry.TryGetEdge(res.Edge, out var rec)) DetachFromProject(rec);
                RegisterSite(res.Edge, false);
                if (!m_SplitIds.Contains(site.m_ProjectId)) m_SplitIds.Add(site.m_ProjectId);
            }
            proj.Revision++;
            RRWLog.Info("director: split " + m_FactoryOut.Count + " bulldozed edge(s) off project #" + proj.Id + " into " + m_SplitIds.Count + " project(s)");
            return m_SplitIds;
        }

        private void CancelEdgeInstant(Entity edge)
        {
            if (!SiteRegistry.TryGetEdge(edge, out var rec) || !SiteRegistry.TryGetProject(rec.ProjectId, out var proj)) return;
            var em = EntityManager;
            if (!em.Exists(edge) || !em.HasComponent<RoadWorksSite>(edge)) return;
            var site = em.GetComponentData<RoadWorksSite>(edge);
            if (site.Kind != WorksKind.Construction) return;
            float p = site.Progress;
            if (p >= RRWConst.kInstantCancelProgress + DirConst.kCancelInstantSlack || !m_S.InstantCancelUnfinished)
            {
                m_TmpEntities.Clear();
                m_TmpEntities.Add(edge);
                CancelEdges(proj.Id, new List<Entity>(m_TmpEntities));
                return;
            }
            m_ReqEdges.Clear();
            m_ReqEdges.Add(edge);
            InstantDelete(m_ReqEdges, p, "edge " + RRWLog.E(edge) + " of project #" + proj.Id);
        }

        // Instant cancel (p < 5 %): the Director deletes the edges itself and refunds exactly `paid` (one path,
        // one amount; vanilla's decaying Recent refund is never used for works roads). Terrain is Natural in C0: no pop.
        private void InstantDelete(List<Entity> edges, float p, string what)
        {
            var em = EntityManager;
            long refund = 0;
            m_TmpEntities.Clear();
            for (int i = 0; i < edges.Count; i++)
            {
                Entity e = edges[i];
                if (!EcsUtil.Alive(em, e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                if (site.Kind != WorksKind.Construction) continue;
                refund += WorkTime.CancelRefund(site.m_PaidCost, p, true);
                m_TmpEntities.Add(e);
            }
            if (m_TmpEntities.Count == 0) return;
            int credit = (int)math.min(refund, int.MaxValue);
            EcsUtil.Credit(em, m_CitySystem.City, credit);
            for (int i = 0; i < m_TmpEntities.Count; i++)
                if (SiteRegistry.TryGetEdge(m_TmpEntities[i], out var rec))
                {
                    var st = rec.Get<DirEdgeState>(ModuleSlot.Director);
                    if (st != null && st.IconPrefab != Entity.Null) RemoveIcon(rec.Edge, st.IconPrefab);
                }
            DemolishEdges(m_TmpEntities);
            for (int i = 0; i < m_TmpEntities.Count; i++)
                if (SiteRegistry.TryGetEdge(m_TmpEntities[i], out var rec)) RemoveRecord(rec);
            RRWLog.Info("director: instant cancel of " + what + ": " + m_TmpEntities.Count + " edge(s) removed, refund " + credit);
            m_TmpEntities.Clear();
        }

        // ------------------------------------------------------------------ dev: start works on existing roads

        private void StartWorks(List<Entity> edges, WorksKind kind)
        {
            var em = EntityManager;
            m_FactoryIn.Clear();
            m_FactoryOut.Clear();
            for (int i = 0; i < edges.Count; i++)
            {
                Entity e = edges[i];
                if (!EcsUtil.Alive(em, e) || em.HasComponent<Temp>(e) || em.HasComponent<RoadWorksSite>(e)) continue;
                if (!em.HasComponent<Edge>(e) || !em.HasComponent<Curve>(e) || !em.HasComponent<Road>(e) || em.HasComponent<Owner>(e)) continue;
                var ed = em.GetComponentData<Edge>(e);
                bool deps = em.HasBuffer<ConnectedBuilding>(e) && em.GetBuffer<ConnectedBuilding>(e, true).Length > 0;
                m_FactoryIn.Add(new SiteFactoryEdge
                {
                    Edge = e, StartNode = ed.m_Start, EndNode = ed.m_End, Length = em.GetComponentData<Curve>(e).m_Length,
                    DigEligible = EcsUtil.DigEligible(em, e, out string _), PaidCost = 0, Dependants = deps,
                });
            }
            if (m_FactoryIn.Count == 0) { RRWLog.Info("director: start " + kind + ": no eligible road in the batch"); return; }
            // An existing road becomes a construction from its old road bed (Replaced: starts at C1 on Bed(0), so
            // hiding the old road never pops the terrain to the natural landform).
            var flags = kind == WorksKind.Construction ? SiteFlags.Replaced : SiteFlags.None;
            SiteFactory.CreateProjects(m_FactoryIn, kind, EcsUtil.RoadClass(em, m_FactoryIn[0].Edge), m_S, flags, m_FactoryOut);
            for (int i = 0; i < m_FactoryOut.Count; i++)
            {
                var res = m_FactoryOut[i];
                if (em.HasComponent<RoadWorksSite>(res.Edge)) em.SetComponentData(res.Edge, res.Site);
                else em.AddComponentData(res.Edge, res.Site);
                RegisterSite(res.Edge, false);
            }
            RRWLog.Info("director: started " + kind + " on " + m_FactoryOut.Count + " existing edge(s)");
        }
    }
}
