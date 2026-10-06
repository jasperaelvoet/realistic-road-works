using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;

namespace RealisticRoadWorks.V3.Director
{
    // Upgrade works (mode H: a road replaced by another variant, only the changed strip is worked), Director part:
    //  * adoption: ProjectRecord.Upgrade (UpgradeRuntime) from the saved tails of the project's sites, rebuilt whenever a tail or
    //    the edge set changes; EdgeRecord.Upgrade per edge; corridor links; OpenLanes seeded with every group (the road was open)
    //    except the groups taken over closed from another running upgrade project (hand-over, below).
    //    Upgrade works are never hidden and have no pre-roll;
    //  * every update, before the stage switch: chain indices, sub-strips (from the live lane layout, read once per geometry
    //    revision), driveway keep-outs, HasCar, the traffic primitive of every window (a started window's saved primitive is a
    //    ceiling: it may only weaken), the stamp at a window start and the lane-drop latch (AllAtOnce);
    //  * after the staged zones: MachineSafe and the machine-safe lateral range of every band;
    //  * a project that mixes excavation works and upgrade sites completes its upgrade edges (the excavation goes on).
    // Everything fails closed: missing inputs keep the last primitive (Undecided when there is none: slow zone, no machine, strips
    // covered), an error drops every window to Dressing without any machine-safe zone.
    public partial class WorksDirectorSystem
    {
        private readonly RRWGuard m_GuardUpgrade = new RRWGuard("director upgrade works");
        private Action m_StepUpgrade;
        private bool m_UwRelink;                                            // re-link corridors after a registry rebuild (load)
        private readonly UpgradeLayoutRead m_UwRead = new UpgradeLayoutRead();
        private readonly List<EdgeRecord> m_UwRemove = new List<EdgeRecord>(4);
        private readonly List<Entity> m_UwMove = new List<Entity>(4);
        private readonly List<ProjectRecord> m_UwProjects = new List<ProjectRecord>(8);
        private readonly float[] m_UwLo = new float[RRWConst.kUwMaxChainBands];
        private readonly float[] m_UwHi = new float[RRWConst.kUwMaxChainBands];
        private readonly bool[] m_UwSeen = new bool[RRWConst.kUwMaxChainBands];

        // A saved mode H site: a HalfWidth construction with an upgrade tail.
        internal static bool IsUpgradeSite(in RoadWorksSite s) =>
            s.Mode == VisualMode.HalfWidth && s.IsUpgrade && s.Kind == WorksKind.Construction;

        // The project runs as upgrade works (runtime built, project mode HalfWidth).
        internal static bool IsUpgradeProject(ProjectRecord proj) =>
            proj != null && proj.Upgrade != null && proj.Mode == VisualMode.HalfWidth;

        // Phase of a project at progress p: upgrade works show the lead band's equivalent phase of the current window, every
        // other project the timeline phase.
        private static WorksPhase PhaseAt(ProjectRecord proj, WorksKind kind, float p, out float f)
        {
            if (kind == WorksKind.Construction && p < 1f && IsUpgradeProject(proj) && proj.Upgrade.LeadPhase(p, out var ph, out f)) return ph;
            return PhasePlan.PhaseOf(kind, p, out f);
        }

        private static uint UwMix(uint h, uint v) => (h ^ v) * 16777619u;

        // ------------------------------------------------------------------ adoption / rebuild (once per update, before the accrual)

        private void StepUpgrade()
        {
            bool removed = false;
            for (int i = 0; i < m_Projects.Count; i++)
            {
                var proj = m_Projects[i];
                if (!SiteRegistry.Projects.ContainsKey(proj.Id)) continue;
                try { removed |= SyncUpgrade(proj); }
                catch (Exception e)
                {
                    RRWLog.ErrorOnce("director upgrade sync", e);
                    UpgradeFailClosed(proj);
                }
            }
            if (m_UwRelink)
            {
                m_UwRelink = false;
                try { RelinkCorridors(); }
                catch (Exception e) { RRWLog.ErrorOnce("director upgrade corridors", e); }
            }
            if (removed) RefreshRecordList();
        }

        // Hash of the project's sites as the runtime sees them (edge order, tails, chain direction). anyH: an upgrade site;
        // anyA: an excavation (FullDig) site; anyOther: a site that is no construction (a demolition under the project's id).
        private uint UpgradeTailKey(ProjectRecord proj, out bool anyH, out bool anyA) => UpgradeTailKey(proj, out anyH, out anyA, out _);

        private uint UpgradeTailKey(ProjectRecord proj, out bool anyH, out bool anyA, out bool anyOther)
        {
            var em = EntityManager;
            anyH = anyA = anyOther = false;
            uint key = 2166136261u;
            int n = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                bool h = IsUpgradeSite(s);
                anyH |= h;
                anyA |= s.Mode == VisualMode.FullDig;
                anyOther |= s.Kind != WorksKind.Construction;
                key = UwMix(UwMix(UwMix(key, (uint)e.Index), s.TailHash()),
                            (h ? 1u : 0u) | (RoadZoneMath.ChainReversed(s.m_ChainU0, s.m_ChainU1) ? 2u : 0u) | ((uint)s.Kind << 2));
                n++;
            }
            key = UwMix(key, (uint)n);
            return key == 0u ? 1u : key;
        }

        // Builds, rebuilds or drops the project's runtime. True when records were removed (mixed project).
        private bool SyncUpgrade(ProjectRecord proj)
        {
            uint key = UpgradeTailKey(proj, out bool anyH, out bool anyA, out bool anyOther);
            var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            if (anyH && anyOther) return MoveOtherSitesOut(proj);
            if (anyH && anyA) return CompleteUpgradeEdges(proj, ps);
            if (!anyH)
            {
                if (proj.Upgrade != null) DropUpgrade(proj, ps, "no upgrade site left");
                return false;
            }
            if (proj.Upgrade != null && key == ps.UwTailKey) return false;
            BuildUpgrade(proj, ps, key);
            return false;
        }

        // Begin(first site) / AddEdge(every site) / Finish(), and EdgeRecord.Upgrade.Load for every upgrade edge. A rebuild of the
        // same schedule keeps the runtime decisions (UpgradeRuntime). The first build is the adoption.
        private void BuildUpgrade(ProjectRecord proj, DirProjectState ps, uint key)
        {
            var em = EntityManager;
            bool adopt = proj.Upgrade == null;
            var rt = proj.Upgrade ?? new UpgradeRuntime();
            bool begun = false;
            int edges = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e) || !SiteRegistry.TryGetEdge(e, out var rec)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (!begun) { rt.Begin(s); begun = true; }
                rt.AddEdge(s);
                edges++;
                if (IsUpgradeSite(s))
                {
                    if (rec.Upgrade == null) rec.Upgrade = new UpgradeEdgeState();
                    rec.Upgrade.Load(s);
                }
                else rec.Upgrade = null;
            }
            if (!begun) return;
            rt.Finish();
            proj.Upgrade = rt;
            proj.Mode = VisualMode.HalfWidth;
            ps.UwTailKey = key;
            // chain indices follow the new revision at once (readers never see a stale index after a stamp or a rebuild)
            for (int i = 0; i < proj.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(proj.Edges[i], out var r) && r.Upgrade != null) r.Upgrade.SyncChainIndex(rt);
            if (rt.Mismatch > 0)
                RRWLog.Warn("director: upgrade project #" + proj.Id + ": " + rt.Mismatch + " edge(s) disagree with the first edge's schedule or bands"
                            + " (the first edge's schedule is used): " + rt.Describe());
            if (!adopt)
            {
                DirectorShared.UpgradeRebuilds++;
                RRWLog.Verbose("director: upgrade project #" + proj.Id + " rebuilt from the saved tails (" + edges + " edges, revision " + rt.Revision + ")");
                return;
            }
            DirectorShared.UpgradesAdopted++;
            if (SiteRegistry.TakeCorridor(proj.Id, out var link))
            {
                rt.CorridorPrev = link.Prev;
                rt.CorridorNext = link.Next;
            }
            // The road was open: lane groups that no window closes never wait for a first machine report. Groups another running
            // upgrade project had closed on the edges taken over stay closed: they open through the staged opening or the release
            // once its machines and ours have left them.
            proj.OpenLanes = RoadZones.AllLanes & ~ps.UwCarry;
            ps.UwFresh = !ps.UwRebuilt;
            ps.UwStartDone = ps.UwSwapDone = int.MinValue;
            ps.UwSwitchWindow = -1;
            ps.UwSwitchIsSwap = false;
            RRWLog.Info("director: upgrade project #" + proj.Id + " adopted (" + edges + " edges, " + (ps.UwRebuilt ? "load" : "new")
                        + (ps.UwCarry != RoadZones.None ? ", " + ps.UwCarry + " kept closed from the previous project" : "") + "): " + rt.Describe());
        }

        private static void DropUpgrade(ProjectRecord proj, DirProjectState ps, string why)
        {
            proj.Upgrade = null;
            ps.UwTailKey = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) rec.Upgrade = null;
            RRWLog.Info("director: project #" + proj.Id + " is no longer upgrade works (" + why + ")");
        }

        // A project never mixes excavation works and upgrade works (Tools completes upgrade parts when it combines them). If it
        // happens anyway, the upgrade edges are completed now (the road already has its new type) and the excavation goes on.
        private bool CompleteUpgradeEdges(ProjectRecord proj, DirProjectState ps)
        {
            var em = EntityManager;
            m_UwRemove.Clear();
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e) || !SiteRegistry.TryGetEdge(e, out var rec)) continue;
                if (IsUpgradeSite(em.GetComponentData<RoadWorksSite>(e))) m_UwRemove.Add(rec);
            }
            DirectorShared.UpgradeMixes++;
            RRWLog.Warn("director: project #" + proj.Id + " mixes excavation works with " + m_UwRemove.Count + " upgrade edge(s): the upgrade edges complete now"
                        + " (the road already has its new type), the excavation works go on");
            for (int i = 0; i < m_UwRemove.Count; i++)
            {
                m_UwRemove[i].Upgrade = null;
                RemoveSiteKeepEdge(m_UwRemove[i], false);
            }
            proj.Upgrade = null;
            ps.UwTailKey = 0;
            bool any = m_UwRemove.Count > 0;
            m_UwRemove.Clear();
            return any;
        }

        // Sites of another kind under an upgrade project's id (a demolition the bulldozer wrote without a new project id): each
        // becomes a project of its own, so the upgrade project never turns into a demolition of all its edges.
        private bool MoveOtherSitesOut(ProjectRecord proj)
        {
            var em = EntityManager;
            m_UwMove.Clear();
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (em.Exists(e) && em.HasComponent<RoadWorksSite>(e) && em.GetComponentData<RoadWorksSite>(e).Kind != WorksKind.Construction) m_UwMove.Add(e);
            }
            for (int i = 0; i < m_UwMove.Count; i++) MoveToOwnProject(m_UwMove[i], "it is not part of the upgrade works");
            bool any = m_UwMove.Count > 0;
            m_UwMove.Clear();
            return any;
        }

        // Gives the edge's site a project of its own (new id, chain re-based to the edge) and re-registers it; the record leaves
        // its old project.
        internal void MoveToOwnProject(Entity e, string why)
        {
            var em = EntityManager;
            if (!EcsUtil.Alive(em, e) || !em.HasComponent<RoadWorksSite>(e)) return;
            var site = em.GetComponentData<RoadWorksSite>(e);
            uint oldId = site.m_ProjectId;
            float len = math.abs(site.m_ChainU1 - site.m_ChainU0);
            bool fwd = site.m_ChainU1 >= site.m_ChainU0;
            site.m_ProjectId = SiteRegistry.AllocateProjectId();
            site.m_ChainU0 = fwd ? 0f : len;
            site.m_ChainU1 = fwd ? len : 0f;
            site.m_ChainLength = len;
            em.SetComponentData(e, site);
            RegisterSite(e, false);
            RRWLog.Warn("director: edge " + RRWLog.E(e) + " (" + site.Kind + ") left project #" + oldId + " for its own project #" + site.m_ProjectId + ": " + why);
        }

        // Every window falls back to Dressing (slow zone, no machine in a lane, nothing machine-safe).
        private static void UpgradeFailClosed(ProjectRecord proj)
        {
            var rt = proj?.Upgrade;
            if (rt == null) return;
            for (int w = 0; w < RRWConst.kUwMaxWindows; w++)
            {
                rt.Primitive[w] = BandTraffic.Dressing;
                rt.Zones[w] = RoadZones.None;
            }
            rt.PreClosed = RoadZones.None;
            rt.MachineSafe = RoadZones.None;
            for (int i = 0; i < RRWConst.kUwMaxChainBands; i++) { rt.SafeLo[i] = 0f; rt.SafeHi[i] = 0f; }
        }

        // ------------------------------------------------------------------ corridor links

        // After a load the drag links are gone (runtime only): upgrade projects whose chain ends share a node are linked again, so
        // neighbouring projects never close opposite halves at the same time.
        private void RelinkCorridors()
        {
            m_UwProjects.Clear();
            foreach (var p in SiteRegistry.Projects.Values) if (IsUpgradeProject(p) && p.Edges.Count > 0) m_UwProjects.Add(p);
            int links = 0;
            for (int i = 0; i < m_UwProjects.Count; i++)
            {
                var a = m_UwProjects[i];
                if (!ChainEnds(a, out Entity aStart, out Entity aEnd)) continue;
                for (int j = 0; j < m_UwProjects.Count; j++)
                {
                    if (i == j) continue;
                    var b = m_UwProjects[j];
                    if (!ChainEnds(b, out Entity bStart, out Entity bEnd)) continue;
                    var rt = a.Upgrade;
                    if (aEnd != Entity.Null && (aEnd == bStart || aEnd == bEnd) && rt.CorridorNext == 0) { rt.CorridorNext = b.Id; links++; }
                    else if (aStart != Entity.Null && (aStart == bStart || aStart == bEnd) && rt.CorridorPrev == 0) { rt.CorridorPrev = b.Id; links++; }
                }
            }
            if (links > 0) RRWLog.Info("director: " + links + " corridor link(s) restored between upgrade projects that share a chain end");
            m_UwProjects.Clear();
        }

        // The node at chain u = 0 and at chain u = U (edges in chain order).
        private bool ChainEnds(ProjectRecord proj, out Entity start, out Entity end)
        {
            start = end = Entity.Null;
            var em = EntityManager;
            if (proj.Edges.Count == 0) return false;
            Entity fe = proj.Edges[0], le = proj.Edges[proj.Edges.Count - 1];
            if (!SiteRegistry.TryGetEdge(fe, out var first) || !SiteRegistry.TryGetEdge(le, out var last)) return false;
            if (!em.HasComponent<RoadWorksSite>(fe) || !em.HasComponent<RoadWorksSite>(le)) return false;
            var fs = em.GetComponentData<RoadWorksSite>(fe);
            var ls = em.GetComponentData<RoadWorksSite>(le);
            start = fs.m_ChainU1 >= fs.m_ChainU0 ? first.StartNode : first.EndNode;
            end = ls.m_ChainU1 >= ls.m_ChainU0 ? last.EndNode : last.StartNode;
            return true;
        }

        // A linked project of the same corridor has a one-way or carriageway closure now. Ties are broken so the decision is
        // stable: a committed (stamped) closure wins over an expected one, then the lower project id.
        private bool CorridorConflict(ProjectRecord proj, UpgradeRuntime rt, int window)
        {
            bool ours = rt.Saved[window] != BandTraffic.Undecided;
            return CorridorBlocks(proj, rt.CorridorPrev, ours) || CorridorBlocks(proj, rt.CorridorNext, ours);
        }

        private static bool CorridorBlocks(ProjectRecord proj, uint otherId, bool oursCommitted)
        {
            if (otherId == 0 || otherId == proj.Id || !SiteRegistry.TryGetProject(otherId, out var other) || !IsUpgradeProject(other)) return false;
            var ort = other.Upgrade;
            if (ort.Schedule.N == 0 || other.Phase == WorksPhase.Complete) return false;
            int lw = ort.LayoutWindowAt(other.Progress);
            var t = ort.Primitive[lw];
            if (t != BandTraffic.Half && t != BandTraffic.Carriageway) return false;
            bool theirs = ort.Saved[lw] != BandTraffic.Undecided;
            return theirs == oursCommitted ? other.Id < proj.Id : theirs;
        }

        // ------------------------------------------------------------------ every update (StageStepUpgrade, before the switch)

        // Sub-strips, keep-outs, HasCar, the car-half verdict per window, the primitives, the stamp at a window start and the
        // lane-drop latch. p0 = p before this update's accrual.
        private void UpgradeUpdate(ProjectRecord proj, DirProjectState ps, float p0)
        {
            var em = EntityManager;
            var rt = proj.Upgrade;
            bool lht = RRWCity.LeftHandTraffic;
            bool classified = true, carHalvesKnown = true, chainOk = true, buildings = false, cut = false;
            RoadZones houses = RoadZones.None;
            int nb = math.min(rt.Bands.Count, RRWConst.kUwMaxChainBands);
            for (int b = 0; b < nb; b++) rt.HasCar[b] = false;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec) || !em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                bool reversed = RoadZoneMath.ChainReversed(site.m_ChainU0, site.m_ChainU1);
                if (rec.Classified && rec.ClassifyReversed == reversed)
                {
                    buildings |= rec.BuildingCount > 0;
                    cut |= rec.CutEdge;
                    houses |= rec.BuildingZones & RoadZones.Sidewalks;
                }
                else classified = false;
                if (rec.CarHalvesRevision != rec.GeometryRevision || rec.CarHalvesReversed != reversed) carHalvesKnown = false;
                var eu = rec.Upgrade;
                if (eu == null) continue;
                var st = rec.GetOrCreate<DirEdgeState>(ModuleSlot.Director);
                eu.SyncChainIndex(rt);
                ReadUpgradeLayout(rec, st, eu);
                eu.RefreshSubStrips(rec.Section, rec.GeometryRevision, lht);
                RefreshKeepOuts(rec, st, eu, site);
                if (!eu.ChainIndexCurrent(rt)) { chainOk = false; continue; }
                st.UwChainIndexOk = m_Now;
                for (int j = 0; j < eu.BandCount; j++)
                {
                    int ci = eu.ChainIndex[j];
                    if (ci >= 0 && ci < nb && UpgradeLanes.HasCar(eu.SubStrips[j])) rt.HasCar[ci] = true;
                }
            }
            if (chainOk)
            {
                rt.MarkDerived();
                ps.UwDerivedOk = m_Now;
            }

            int N = rt.Schedule.N;
            if (N <= 0) return;
            UpgradePlan.At(rt.Schedule, p0, out int k0, out float _);
            int lw = rt.LayoutWindowAt(p0);
            var chainBlock = ps.ExitsKnown ? PhasePlan.ChainCarHalfBlock(proj.ExitAtStart, proj.ExitAtEnd, RRWGates.DeadEndRule) : StageBlockReason.None;
            bool ready = classified && carHalvesKnown && ps.ExitsKnown && rt.DerivedFresh;
            bool detourCurrent = rt.DetourUpdate != 0 && rt.DetourRevision == proj.Revision;

            // the half swap of the re-marking window: once it ran in this window it stays (no flip back on a gate or verdict change)
            int remarkW = -1;
            for (int b = 0; b < nb; b++) if (rt.Bands[b].Kind == BandKind.Remark) remarkW = rt.Bands[b].Window;
            bool swapLatched = remarkW >= 0 && ((proj.Switch != StageSwitch.None && ps.UwSwitchIsSwap && ps.UwSwitchWindow == remarkW) || ps.UwSwapDone == remarkW);
            bool swapAllowed = remarkW >= 0 && RRWGates.C4Swap && UpgradeHalfBlock(proj, RoadZones.Carriageway, true, chainBlock) == StageBlockReason.None;
            ps.UwSwapOn = swapLatched || (swapAllowed && !ps.SwitchSkipped);

            int stampW = -1;
            var stampPrim = BandTraffic.Undecided;
            bool dropsGone = false;
            string dropsWhy = null;
            var layoutBlock = StageBlockReason.None;
            for (int w = 0; w < N && w < RRWConst.kUwMaxWindows; w++)
            {
                var input = default(WindowPrimitiveInput);
                bool fresh = rt.FillWindowInput(w, ref input);
                bool started = k0 >= 0 && w <= math.min(k0, N - 1);
                var closed = rt.HalfClosesOf(w, input.RemarkWindow && ps.UwSwapOn);
                input.EveryDirectionKeepsLane = KeepsLaneEveryEdge(proj, w);
                input.Buildings = buildings;
                input.SideBuildings = (houses & BandSidewalks(rt, w)) != RoadZones.None;
                input.CarHalfBlock = closed == RoadZones.None ? StageBlockReason.None : UpgradeHalfBlock(proj, closed, input.RemarkWindow, chainBlock);
                input.DetourExists = rt.DetourFor(closed, proj.Revision);
                input.CorridorConflict = CorridorConflict(proj, rt, w);
                input.Policy = m_S.Policy;
                input.DropGate = RRWGates.UpgradeDrop;
                input.ClosedBGate = RRWGates.ClosedB;
                input.CutEdge = cut;
                input.Saved = started ? rt.Saved[w] : BandTraffic.Undecided;
                if (w == lw) layoutBlock = input.CarHalfBlock;

                bool ok = fresh && ready;
                // a started one-way window keeps its closure while Traffic re-checks the detour for a new project revision
                if (ok && started && rt.Primitive[w] == BandTraffic.Half && !detourCurrent)
                {
                    if (ps.UwDetourStaleSince == 0) ps.UwDetourStaleSince = math.max(1u, m_Now);
                    if (unchecked(m_Now - ps.UwDetourStaleSince) < (uint)DirConst.kUwDetourGraceUpdates) ok = false;
                }
                if (!ok)
                {
                    // nothing decided yet while Traffic's classification, the detour verdict or the band data are missing: the
                    // window stays Undecided (slow zone, no lane group closed, no machine; its strips are covered from the apply
                    // on, so a closure decided a few updates later finds them covered already)
                    if (rt.Primitive[w] == BandTraffic.Undecided)
                    {
                        rt.Zones[w] = RoadZones.None;
                        rt.WindowReason[w] = StageBlockReason.Waiting;
                    }
                    continue;
                }
                var prim = PhasePlan.WindowPrimitive(input, out var why);
                rt.Primitive[w] = prim;
                rt.WindowReason[w] = why;
                rt.Zones[w] = PhasePlan.WindowZones(prim, rt.Bands, w);
                if (started && w == lw && k0 < N && rt.Saved[w] == BandTraffic.Undecided) { stampW = w; stampPrim = prim; }
                if (rt.AllAtOnce && input.HasBuild && w >= lw && prim != BandTraffic.Drop && !dropsGone)
                {
                    dropsGone = true;
                    dropsWhy = "window " + (w + 1) + " runs as " + prim + (why != StageBlockReason.None ? " (" + why + ")" : "");
                }
            }
            if (detourCurrent) ps.UwDetourStaleSince = 0;
            if (rt.AllAtOnce && !RRWGates.UpgradeDrop) { dropsGone = true; dropsWhy = "lane drops switched off"; }

            // Groups of later windows closed already: an all-at-once project drops the lanes of every build window from the end of
            // the setup, and lane drops close single lanes (Traffic's blockers), never a lane group
            rt.PreClosed = RoadZones.None;

            proj.CarHalfAllowed = layoutBlock == StageBlockReason.None;
            proj.StageBlockReason = layoutBlock;
            if (rt.ReclassifyUpdate != 0)
            {
                RRWLog.Verbose("director: upgrade project #" + proj.Id + " primitives re-picked after a geometry change (" + (m_Now - rt.ReclassifyUpdate) + " updates ago)");
                rt.ReclassifyUpdate = 0;
            }

            if (dropsGone) ClearUpgradeAllAtOnce(proj, ps, dropsWhy);
            if (stampW >= 0) StampUpgradeWindow(proj, ps, stampW, stampPrim);
            LogUpgradeWindow(proj, ps, lw);
        }

        // Sidewalk groups of the bands of window w (chain sides).
        private static RoadZones BandSidewalks(UpgradeRuntime rt, int w)
        {
            var z = RoadZones.None;
            for (int i = 0; i < rt.Bands.Count; i++)
            {
                var b = rt.Bands[i];
                if (b.Window != w || b.Kind == BandKind.Remark || b.Kind == BandKind.Remove) continue;
                if (b.Side == BandSide.Left) z |= RoadZones.SidewalkLeft;
                else if (b.Side == BandSide.Right) z |= RoadZones.SidewalkRight;
                else z |= RoadZones.Sidewalks;
            }
            return z;
        }

        // On every upgrade edge each direction keeps a drive lane outside the build bands of window w (false without any edge).
        private static bool KeepsLaneEveryEdge(ProjectRecord proj, int w)
        {
            int n = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var rec) || rec.Upgrade == null) continue;
                if (!rec.Upgrade.KeepsLaneEachDirection(w)) return false;
                n++;
            }
            return n > 0;
        }

        // Why the car halves `closed` cannot close on this chain (None = they can): the chain dead-end rule, then per edge the lane
        // layout, tram tracks, a transport stop in a closed half, the dead-end rule and, for the re-marking crew, a closed half
        // narrower than kMinWorksHalfWidth.
        private StageBlockReason UpgradeHalfBlock(ProjectRecord proj, RoadZones closed, bool remark, StageBlockReason chainBlock)
        {
            if (chainBlock != StageBlockReason.None) return chainBlock;
            var em = EntityManager;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec) || !em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                bool reversed = RoadZoneMath.ChainReversed(site.m_ChainU0, site.m_ChainU1);
                var stops = (rec.StopZones & closed) != RoadZones.None ? RoadZones.Carriageway : RoadZones.None;
                var r = PhasePlan.CarHalfBlock(rec.Section, reversed, rec.CarHalves, rec.HasTrack, stops, rec.CutEdge, false, RRWGates.DeadEndRule);
                if (r == StageBlockReason.Narrow) r = RRWGates.DeadEndRule && rec.CutEdge ? StageBlockReason.DeadEnd : StageBlockReason.None;
                if (r == StageBlockReason.None && remark)
                {
                    if ((closed & RoadZones.LeftHalf) != 0 && RoadZoneMath.HalfWidthChain(rec.Section, reversed, RoadZones.LeftHalf) < RRWConst.kMinWorksHalfWidth) r = StageBlockReason.Narrow;
                    if ((closed & RoadZones.RightHalf) != 0 && RoadZoneMath.HalfWidthChain(rec.Section, reversed, RoadZones.RightHalf) < RRWConst.kMinWorksHalfWidth) r = StageBlockReason.Narrow;
                }
                if (r != StageBlockReason.None) return r;
            }
            return StageBlockReason.None;
        }

        // The primitive chosen at a window start is saved in that window's bands on every edge (the ceiling after a load); the
        // runtime is rebuilt at once (same schedule: the decisions are kept).
        private void StampUpgradeWindow(ProjectRecord proj, DirProjectState ps, int w, BandTraffic t)
        {
            var em = EntityManager;
            int n = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (!IsUpgradeSite(s) || !UpgradeRuntime.StampPrimitive(ref s, w, t)) continue;
                em.SetComponentData(e, s);
                n++;
            }
            DirectorShared.UpgradeStamps++;
            var rt = proj.Upgrade;
            RRWLog.Info("director: upgrade project #" + proj.Id + " window " + (w + 1) + "/" + rt.Schedule.N + " starts with " + t
                        + (rt.WindowReason[w] != StageBlockReason.None ? " (" + rt.WindowReason[w] + ")" : "") + ", saved on " + n + " edge(s)");
            if (n > 0)
            {
                BuildUpgrade(proj, ps, UpgradeTailKey(proj, out _, out _));
                RecutSubStrips(proj);
            }
        }

        // Lane drops are no longer available: AllAtOnce goes off for good (the later build bands wait as open road), also in
        // every edge's tail so it stays off after a load.
        private void ClearUpgradeAllAtOnce(ProjectRecord proj, DirProjectState ps, string why)
        {
            var em = EntityManager;
            var rt = proj.Upgrade;
            rt.ClearAllAtOnce();
            int n = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (!UpgradeRuntime.ClearAllAtOnce(ref s)) continue;
                em.SetComponentData(e, s);
                n++;
            }
            RRWLog.Info("director: upgrade project #" + proj.Id + ": lane drops are no longer available (" + why + "): the later build bands wait as open road ("
                        + n + " edge(s) updated)");
            if (n > 0)
            {
                BuildUpgrade(proj, ps, UpgradeTailKey(proj, out _, out _));
                RecutSubStrips(proj);
            }
        }

        // A rewritten tail moves every edge's TailRevision on: the sub-strips are cut again at once, so the readers later in this
        // update (props, machines, surfaces) never see them stale.
        private static void RecutSubStrips(ProjectRecord proj)
        {
            bool lht = RRWCity.LeftHandTraffic;
            for (int i = 0; i < proj.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(proj.Edges[i], out var rec) && rec.Upgrade != null)
                    rec.Upgrade.RefreshSubStrips(rec.Section, rec.GeometryRevision, lht);
        }

        private void LogUpgradeWindow(ProjectRecord proj, DirProjectState ps, int lw)
        {
            var rt = proj.Upgrade;
            var t = rt.Primitive[lw];
            var r = rt.WindowReason[lw];
            if (ps.UwLoggedWindow == lw && ps.UwLoggedPrim == t && ps.UwLoggedReason == r) return;
            ps.UwLoggedWindow = lw;
            ps.UwLoggedPrim = t;
            ps.UwLoggedReason = r;
            RRWLog.Info("director: upgrade project #" + proj.Id + " window " + (lw + 1) + "/" + rt.Schedule.N + " traffic " + t
                        + (r != StageBlockReason.None ? " (" + r + ")" : "") + (rt.Saved[lw] != BandTraffic.Undecided ? ", saved " + rt.Saved[lw] : ", expected")
                        + " zones " + RoadZoneMath.Describe(rt.Zones[lw]) + (rt.AllAtOnce ? ", all lanes at once" : ""));
        }

        // ------------------------------------------------------------------ per edge: lane layout, keep-outs

        // The live lane layout of the new road, read once per geometry revision (sub-strips then follow the real sidewalks, verges
        // and bike lanes; without it they come from the measured section).
        private void ReadUpgradeLayout(EdgeRecord rec, DirEdgeState st, UpgradeEdgeState eu)
        {
            if (st.UwLayoutTried == rec.GeometryRevision || rec.Arc == null) return;
            st.UwLayoutTried = rec.GeometryRevision;
            m_UwRead.Clear();
            bool ok = false;
            try { ok = UpgradeLayoutReader.Read(EntityManager, rec.Edge, rec.Arc, m_UwRead); }
            catch (Exception e) { RRWLog.ErrorOnce("director upgrade layout", e); }
            if (!ok)
            {
                RRWLog.Verbose("director: upgrade edge " + RRWLog.E(rec.Edge) + " lane layout unreadable (revision " + rec.GeometryRevision + "): sub-strips from the section");
                return;
            }
            if (eu.Layout == null) eu.Layout = new CompositionLayout();
            eu.Layout.CopyFrom(m_UwRead.L);
            eu.LayoutRevision = rec.GeometryRevision;
        }

        // Driveway keep-outs (CHAIN frame, +-kUwDrivewayKeepOut along the road) around every ConnectedBuilding access of the edge,
        // on the side the building stands. Recomputed when the geometry, the buildings or the chain coordinates change.
        private void RefreshKeepOuts(EdgeRecord rec, DirEdgeState st, UpgradeEdgeState eu, in RoadWorksSite site)
        {
            var em = EntityManager;
            Entity e = rec.Edge;
            var arc = rec.Arc;
            bool has = arc != null && em.HasBuffer<ConnectedBuilding>(e);
            int count = has ? em.GetBuffer<ConnectedBuilding>(e, true).Length : 0;
            uint key = UwMix(UwMix(UwMix((uint)rec.GeometryRevision, (uint)count), math.asuint(site.m_ChainU0)), math.asuint(site.m_ChainU1));
            if (key == 0u) key = 1u;
            if (key == st.UwKeepOutKey && eu.DrivewayRevision == rec.GeometryRevision) return;
            st.UwKeepOutKey = key;
            eu.DrivewayKeepOut.Clear();
            eu.DrivewayRevision = rec.GeometryRevision;
            if (count == 0) return;
            bool reversed = RoadZoneMath.ChainReversed(site.m_ChainU0, site.m_ChainU1);
            var buf = em.GetBuffer<ConnectedBuilding>(e, true);
            for (int k = 0; k < buf.Length; k++)
            {
                Entity b = buf[k].m_Building;
                if (!EcsUtil.Alive(em, b) || !em.HasComponent<Building>(b) || !em.HasComponent<ObjTransform>(b)) continue;
                var bd = em.GetComponentData<Building>(b);
                if (bd.m_RoadEdge != e) continue;
                float s = arc.SAt(bd.m_CurvePosition);
                float u = PhasePlan.ChainU(s, site.m_ChainU0, site.m_ChainU1, arc.Length);
                float3 bp = em.GetComponentData<ObjTransform>(b).m_Position;
                float lat = RoadZoneMath.ToChain(math.dot((bp - arc.Position(s)).xz, arc.Right(s).xz), reversed);
                eu.DrivewayKeepOut.Add(DrivewayWindow.Around(u, lat < 0f ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight));
            }
        }

        // ------------------------------------------------------------------ after the staged zones (ProjectGate)

        // MachineSafe = the drained groups of the stage the crews work in now (PhasePlan.UpgradeSafeZones: during the Vacate step
        // of a window start the previous window's groups, after the half swap of the re-marking window the other half) plus the
        // verge bits; per chain band the widest contiguous run of machine-safe sub-strips (CHAIN frame), intersected over the
        // edges that have one. An edge without a run (a short edge without lane drops, an intrusion, a strip that stays open) is
        // left out: machines check the run per edge and stay off it. A band without a run on any edge, or with a stale chain
        // index, gets no range (SafeHi <= SafeLo).
        private void UpgradeSafety(ProjectRecord proj)
        {
            var em = EntityManager;
            var rt = proj.Upgrade;
            var view = proj.View();
            var u = view.Upgrade;
            int lw = u.LayoutWindow;
            // a window whose primitive is not decided yet has no machine anywhere (verges included)
            bool undecided = PhasePlan.UpgradeUndecided(rt.Primitive[math.clamp(lw, 0, RRWConst.kUwMaxWindows - 1)])
                             || PhasePlan.UpgradeUndecided(u.AppliedTraffic);
            rt.MachineSafe = undecided ? RoadZones.None : PhasePlan.UpgradeSafeZones(view, proj.WorkZonesReady);
            u.MachineSafe = rt.MachineSafe;                 // this update's zones (the view still carries the last update's)
            int nb = math.min(rt.Bands.Count, RRWConst.kUwMaxChainBands);
            for (int b = 0; b < nb; b++) { m_UwLo[b] = float.MinValue; m_UwHi[b] = float.MaxValue; m_UwSeen[b] = false; }
            bool fresh = rt.DerivedFresh && !undecided;
            // dropped lanes count only in a window that runs (or, while machines vacate, ran) as lane drops
            bool dropOk = u.Valid && !u.InSetup && !u.InTeardown
                          && (rt.Primitive[lw] == BandTraffic.Drop || u.AppliedTraffic == BandTraffic.Drop);
            for (int i = 0; i < proj.Edges.Count && fresh; i++)
            {
                Entity e = proj.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec) || rec.Upgrade == null || !em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var eu = rec.Upgrade;
                if (!eu.ChainIndexCurrent(rt)) { fresh = false; break; }
                for (int j = 0; j < eu.BandCount; j++)
                {
                    int ci = eu.ChainIndex[j];
                    if (ci < 0 || ci >= nb) continue;
                    if (!SafeRun(rec, u, j, dropOk, out float lo, out float hi)) continue;
                    m_UwSeen[ci] = true;
                    m_UwLo[ci] = math.max(m_UwLo[ci], lo);
                    m_UwHi[ci] = math.min(m_UwHi[ci], hi);
                }
            }
            for (int b = 0; b < nb; b++)
            {
                bool any = fresh && m_UwSeen[b] && m_UwHi[b] > m_UwLo[b];
                rt.SafeLo[b] = any ? m_UwLo[b] : 0f;
                rt.SafeHi[b] = any ? m_UwHi[b] : 0f;
            }
        }

        // Widest run of machine-safe sub-strips of saved band `band` of one edge (EDGE-frame sub-strips, sorted), returned in the
        // CHAIN frame. Each sub-strip's state is the shared rule (UpgradeZones.SubStripStateOf: closed and drained group, a lane
        // Traffic dropped for this band, switched-off new parking). A dropped lane counts only when Traffic listed the dropped
        // lanes' own centres: a band edge can clip a lane that stays open, and its clipped piece must never pass as dropped.
        private static bool SafeRun(EdgeRecord rec, in UpgradeView u, int band, bool dropOk, out float lo, out float hi)
        {
            lo = hi = 0f;
            var eu = rec.Upgrade;
            var strips = eu.SubStrips[band];
            bool reversed = eu.ChainReversed;
            bool centres = eu.DropCentresWritten && eu.DropLaneCentres.Count > 0;
            float bestW = 0f, runLo = 0f, runHi = 0f;
            bool inRun = false;
            for (int k = 0; k < strips.Count; k++)
            {
                var s = strips[k];
                var state = UpgradeZones.SubStripStateOf(rec, u, s, band, false);
                if (!dropOk || !centres) state.DropReady = false;
                bool safe = PhasePlan.MachineSafe(state, false) && s.Hi > s.Lo;
                if (safe && inRun && s.Lo <= runHi + 0.05f) runHi = math.max(runHi, s.Hi);
                else if (safe) { runLo = s.Lo; runHi = s.Hi; inRun = true; }
                else inRun = false;
                if (inRun && runHi - runLo > bestW)
                {
                    bestW = runHi - runLo;
                    lo = RoadZoneMath.ToChain(reversed ? runHi : runLo, reversed);
                    hi = RoadZoneMath.ToChain(reversed ? runLo : runHi, reversed);
                }
            }
            return bestW > 0f;
        }

        // ------------------------------------------------------------------ hand-over between projects

        // Lane groups that are closed on a mode H edge right now, as the new project will see them (its CHAIN frame), when the edge
        // leaves a running upgrade project for another one (a re-upgrade of a running project, an undo, a split). Read from
        // Traffic's applied closure (lane groups present on the edge and not applied open). None for every other re-key: the
        // other modes keep their behaviour.
        private RoadZones UpgradeHandOverZones(EdgeRecord rec, in RoadWorksSite site)
        {
            var em = EntityManager;
            Entity e = rec.Edge;
            if (site.Kind != WorksKind.Construction || site.Mode != VisualMode.HalfWidth) return RoadZones.None;
            if (rec.ClosureApplied != ClosureLevel.Closed || !em.HasComponent<RoadWorksRuntime>(e)) return RoadZones.None;
            var rt = em.GetComponentData<RoadWorksRuntime>(e);
            if (rt.m_Kind != WorksKind.Construction || rt.m_Mode != VisualMode.HalfWidth) return RoadZones.None;
            var present = rec.ZonesPresent != RoadZones.None ? rec.ZonesPresent : RoadZones.AllLanes;
            var closed = RoadZones.AllLanes & present & ~rec.OpenLanesApplied;
            // the applied groups are in the chain frame the previous project ran the edge in
            if (RoadZoneMath.ChainReversed(rt.m_ChainU0, rt.m_ChainU1) != RoadZoneMath.ChainReversed(site.m_ChainU0, site.m_ChainU1))
                closed = MirrorLaneGroups(closed);
            return closed;
        }

        private void BeginUpgradeHandOver(ProjectRecord proj, DirProjectState ps, Entity edge, RoadZones carry, uint from)
        {
            ps.UwCarry |= carry & RoadZones.AllLanes;
            if (from != 0 && from != proj.Id && !ps.UwCarryFrom.Contains(from)) ps.UwCarryFrom.Add(from);
            ps.UwCarrySinceSim = math.max(1u, RRWClock.SimFrame);
            ps.UwCarrySinceUpdate = math.max(1u, m_Now);
            RRWLog.Info("director: edge " + RRWLog.E(edge) + " joined project #" + proj.Id + " from running upgrade project #" + from + " with " + carry
                        + " closed: they stay closed until that project's machines have left them");
        }

        // Keeps the carried groups closed (true) while a machine of the previous project may still be in them: a puppet of that
        // project between the ends of one of our edges, or, while that project still runs, its machine report (not newer than
        // the hand-over, or a machine in those groups on either side, as its chain may run the other way). Ends at the latest
        // kCompletionMachineWaitSimFrames after the hand-over.
        private bool UpgradeHandOverHold(ProjectRecord proj, DirProjectState ps)
        {
            if (ps.UwCarry == RoadZones.None) return false;
            uint age = unchecked(RRWClock.SimFrame - ps.UwCarrySinceSim);
            string busy = age >= (uint)RRWConst.kCompletionMachineWaitSimFrames ? null : HandOverBusy(proj, ps);
            if (busy != null) return true;
            if (age >= (uint)RRWConst.kCompletionMachineWaitSimFrames)
                RRWLog.Warn("director: project #" + proj.Id + ": machines of the previous project did not leave " + ps.UwCarry + " within "
                            + "kCompletionMachineWaitSimFrames (" + age + " sim frames): the groups are handed over anyway (safety cap)");
            else
                RRWLog.Info("director: project #" + proj.Id + ": " + ps.UwCarry + " taken over from the previous project are clear of its machines ("
                            + age + " sim frames)");
            ps.UwCarry = RoadZones.None;
            ps.UwCarryFrom.Clear();
            return false;
        }

        private string HandOverBusy(ProjectRecord proj, DirProjectState ps)
        {
            var carry = ps.UwCarry | MirrorLaneGroups(ps.UwCarry);
            for (int i = 0; i < ps.UwCarryFrom.Count; i++)
            {
                if (!SiteRegistry.TryGetProject(ps.UwCarryFrom[i], out var old) || old == proj) continue;
                var zones = old.MachineZones | MirrorLaneGroups(old.MachineZones);
                if (PhasePlan.MachinesBlockOpening(zones, carry, old.MachinesReportUpdate, ps.UwCarrySinceUpdate, m_Now))
                    return "report of project #" + old.Id;
            }
            if (ps.UwCarryFrom.Count == 0 || m_MachineQuery.IsEmptyIgnoreFilter) return null;
            var em = EntityManager;
            var arr = m_MachineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int k = 0; k < arr.Length; k++)
                {
                    var m = em.GetComponentData<RRWMachine>(arr[k]);
                    if (!ps.UwCarryFrom.Contains(m.m_ProjectId)) continue;
                    float2 pos = em.GetComponentData<ObjTransform>(arr[k]).m_Position.xz;
                    for (int i = 0; i < proj.Edges.Count; i++)
                    {
                        if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var rec) || rec.Arc == null) continue;
                        float d = MathUtils.Distance(rec.Arc.Curve.xz, pos, out float t);
                        float reach = (rec.Section.HalfWidth > 0f ? rec.Section.HalfWidth : RRWConst.kMinWorksHalfWidth) + DirConst.kUwHandOverReach;
                        if (t > 0f && t < 1f && d <= reach) return "puppet " + RRWLog.E(arr[k]) + " of project #" + m.m_ProjectId;
                    }
                }
            }
            finally { arr.Dispose(); }
            return null;
        }

        // Left and right lane groups swapped (a chain that runs the other way).
        private static RoadZones MirrorLaneGroups(RoadZones z)
        {
            var m = RoadZones.None;
            if ((z & RoadZones.LeftHalf) != 0) m |= RoadZones.RightHalf;
            if ((z & RoadZones.RightHalf) != 0) m |= RoadZones.LeftHalf;
            if ((z & RoadZones.SidewalkLeft) != 0) m |= RoadZones.SidewalkRight;
            if ((z & RoadZones.SidewalkRight) != 0) m |= RoadZones.SidewalkLeft;
            if ((z & RoadZones.ParkingLeft) != 0) m |= RoadZones.ParkingRight;
            if ((z & RoadZones.ParkingRight) != 0) m |= RoadZones.ParkingLeft;
            return m;
        }

        // ------------------------------------------------------------------ gameplay helpers

        // Primitive the closure follows: the one of the window whose lane groups are applied now (UpgradeView.AppliedTraffic:
        // the layout window's, or the previous window's while its machines vacate at a window start); Dressing while undecided.
        private static BandTraffic UpgradeClosurePrimitive(ProjectRecord proj)
        {
            if (!IsUpgradeProject(proj)) return BandTraffic.Dressing;
            var v = proj.View();
            if (!v.IsUpgrade) return BandTraffic.Dressing;
            var t = v.Upgrade.AppliedTraffic;
            return t == BandTraffic.Undecided ? BandTraffic.Dressing : t;
        }

        // Machines may come: a band of the layout window has a machine-safe range (never in teardown).
        private static bool UpgradeMachinesWanted(ProjectRecord proj)
        {
            if (!IsUpgradeProject(proj)) return false;
            var rt = proj.Upgrade;
            int N = rt.Schedule.N;
            if (N == 0) return false;
            UpgradePlan.At(rt.Schedule, proj.Progress, out int w, out float _);
            if (w >= N) return false;
            int lw = math.clamp(w, 0, N - 1);
            for (int b = 0; b < rt.Bands.Count && b < RRWConst.kUwMaxChainBands; b++)
                if (rt.Bands[b].Window == lw && rt.SafeHi[b] - rt.SafeLo[b] > DirConst.kUwMinSafeWidth) return true;
            return false;
        }

        // A cancel of a project with upgrade sites: every cancel path refuses it (an upgraded road is never deleted or reverted by
        // a cancel; bulldozing it ends the works). True when refused (logged).
        private bool UpgradeCancelRefused(ProjectRecord proj, string what)
        {
            if (proj == null) return false;
            bool h = IsUpgradeProject(proj) || proj.Mode == VisualMode.HalfWidth;
            var em = EntityManager;
            for (int i = 0; i < proj.Edges.Count && !h; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                h = s.Mode == VisualMode.HalfWidth || s.IsUpgrade;
            }
            if (!h) return false;
            DirectorShared.UpgradeCancelsRefused++;
            RRWLog.Info("director: " + what + " refused for upgrade project #" + proj.Id + ": upgrade works are never cancelled (bulldozing the road ends them)");
            return true;
        }
    }
}
